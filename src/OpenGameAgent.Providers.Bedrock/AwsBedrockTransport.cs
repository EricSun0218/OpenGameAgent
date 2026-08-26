using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Providers.Bedrock;

internal static class AwsBedrockTransport
{
    public static async IAsyncEnumerable<BedrockProtocolEvent> StreamAsync(
        IAmazonBedrockRuntime client,
        ConverseStreamRequest request,
        IReadOnlyDictionary<string, string> headers,
        string providerId,
        string apiId,
        string model,
        ProviderResponseObserver? responseObserver,
        int responseObserverTimeoutMilliseconds,
        int maximumResponseCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RequestEventHandler? requestHandler = null;
        var serviceClient = client as AmazonServiceClient;
        IDisposable? clientRequestLease = null;
        if (serviceClient is not null)
        {
            clientRequestLease = await BedrockClientRequestGate.EnterAsync(serviceClient, cancellationToken)
                .ConfigureAwait(false);
        }

        if (serviceClient is not null && headers.Count > 0)
        {
            requestHandler = (_, args) =>
            {
                if (args is HeadersRequestEventArgs headerArgs)
                {
                    ApplyHeaders(headerArgs.Headers, headers);
                }
            };
            serviceClient.BeforeRequestEvent += requestHandler;
        }

        ConverseStreamResponse response;
        try
        {
            response = await client.ConverseStreamAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ObserveFailureAsync(
                    exception,
                    providerId,
                    apiId,
                    model,
                    responseObserver,
                    responseObserverTimeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
            throw CreateProviderFailure(exception, null, null);
        }
        finally
        {
            if (requestHandler is not null)
            {
                serviceClient!.BeforeRequestEvent -= requestHandler;
            }

            clientRequestLease?.Dispose();
        }

        using (response)
        {
            var responseRequestId = response.ResponseMetadata?.RequestId;
            var responseStatus = (int)response.HttpStatusCode;
            await ObserveResponseAsync(
                    providerId,
                    apiId,
                    model,
                    responseStatus,
                    responseRequestId,
                    responseObserver,
                    responseObserverTimeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);

            var stream = response.Stream ?? throw new InvalidDataException("Bedrock returned no response stream.");
            var queue = new Queue<BedrockProtocolEvent>();
            var signal = new SemaphoreSlim(0);
            Exception? streamException = null;

            void Enqueue(BedrockProtocolEvent value)
            {
                lock (queue)
                {
                    queue.Enqueue(value);
                }

                signal.Release();
            }

            stream.MessageStartReceived += (_, args) =>
                Enqueue(BedrockProtocolEvent.MessageStart(args.EventStreamEvent.Role?.Value ?? string.Empty));
            stream.ContentBlockStartReceived += (_, args) =>
            {
                var item = args.EventStreamEvent;
                Enqueue(BedrockProtocolEvent.ContentStart(
                    item.ContentBlockIndex ?? 0,
                    item.Start?.ToolUse?.ToolUseId,
                    item.Start?.ToolUse?.Name));
            };
            stream.ContentBlockDeltaReceived += (_, args) =>
            {
                var item = args.EventStreamEvent;
                var index = item.ContentBlockIndex ?? 0;
                if (item.Delta?.Text is { } text)
                {
                    Enqueue(BedrockProtocolEvent.TextDelta(index, text));
                }
                else if (item.Delta?.ToolUse?.Input is { } input)
                {
                    Enqueue(BedrockProtocolEvent.ToolDelta(index, input));
                }
                else if (item.Delta?.ReasoningContent is { } reasoning)
                {
                    if (reasoning.RedactedContent is { Length: > 0 } redacted)
                    {
                        if (redacted.Length > maximumResponseCharacters)
                        {
                            streamException = new InvalidDataException(
                                "Bedrock redacted reasoning exceeded the configured response limit.");
                            signal.Release();
                            return;
                        }

                        Enqueue(BedrockProtocolEvent.RedactedReasoningDelta(index, redacted.ToArray()));
                    }
                    else
                    {
                        Enqueue(BedrockProtocolEvent.ReasoningDelta(index, reasoning.Text, reasoning.Signature));
                    }
                }
            };
            stream.ContentBlockStopReceived += (_, args) =>
                Enqueue(BedrockProtocolEvent.ContentStop(args.EventStreamEvent.ContentBlockIndex ?? 0));
            stream.MessageStopReceived += (_, args) =>
                Enqueue(BedrockProtocolEvent.MessageStop(args.EventStreamEvent.StopReason?.Value ?? string.Empty));
            stream.MetadataReceived += (_, args) =>
            {
                var usage = args.EventStreamEvent.Usage;
                if (usage is not null)
                {
                    Enqueue(BedrockProtocolEvent.Usage(
                        usage.InputTokens ?? 0,
                        usage.OutputTokens ?? 0,
                        usage.CacheReadInputTokens ?? 0,
                        usage.CacheWriteInputTokens ?? 0));
                }
            };
            stream.ExceptionReceived += (_, args) =>
            {
                streamException = args.EventStreamException;
                signal.Release();
            };

            Task processing;
            try
            {
                processing = stream.StartProcessingAsync();
            }
            catch (Exception exception)
            {
                throw CreateProviderFailure(exception, responseRequestId, responseStatus);
            }

            while (true)
            {
                while (TryDequeue(queue, out var item))
                {
                    yield return item!;
                }

                if (processing.IsCompleted)
                {
                    break;
                }

                await Task.WhenAny(signal.WaitAsync(cancellationToken), processing).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (streamException is not null)
                {
                    throw CreateProviderFailure(streamException, responseRequestId, responseStatus);
                }
            }

            try
            {
                await processing.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateProviderFailure(exception, responseRequestId, responseStatus);
            }

            if (streamException is not null)
            {
                throw CreateProviderFailure(streamException, responseRequestId, responseStatus);
            }

            while (TryDequeue(queue, out var finalItem))
            {
                yield return finalItem!;
            }
        }
    }

    internal static ModelProviderException CreateProviderFailure(
        Exception exception,
        string? responseRequestId,
        int? responseStatus)
    {
        if (exception is ModelProviderException providerException)
        {
            return providerException;
        }

        var service = exception as AmazonServiceException;
        var status = service is not null && (int)service.StatusCode > 0
            ? (int)service.StatusCode
            : responseStatus is > 0 and not 200 ? responseStatus : null;
        var requestId = Bound(service?.RequestId ?? responseRequestId, 1024);
        var errorCode = Bound(service?.ErrorCode, 256);
        if (string.Equals(errorCode, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = null;
        }

        if (service is null
            && errorCode is null
            && exception.GetType() != typeof(Exception)
            && exception.GetType().Name.EndsWith("Exception", StringComparison.Ordinal)
            && exception is not IOException)
        {
            errorCode = Bound(exception.GetType().Name, 256);
        }

        var data = new Dictionary<string, object?>();
        if (status is not null)
        {
            data["status"] = status;
        }

        if (errorCode is not null)
        {
            data["errorCode"] = errorCode;
        }

        if (requestId is not null)
        {
            data["requestId"] = requestId;
        }

        var diagnostics = data.Count == 0
            ? Array.Empty<ModelDiagnostic>()
            : new[]
            {
                new ModelDiagnostic(
                    "bedrock_response_failure",
                    "The provider returned structured failure metadata.",
                    ModelDiagnosticSeverity.Error,
                    System.Text.Json.JsonSerializer.Serialize(data)),
            };
        var retryableByProvider = service is null
            ? IsKnownTransientLocalFailure(exception)
            : service.Retryable is not null || IsRetryableErrorCode(errorCode)
                ? true
                : (bool?)null;
        var retry = ProviderHttpRetryMetadata.FromStatus(status, retryableByProvider);
        return new ModelProviderException(
            exception.Message,
            diagnostics,
            retry.IsTransient,
            retry.RetryAfter,
            status,
            exception);
    }

    private static async ValueTask ObserveFailureAsync(
        Exception exception,
        string providerId,
        string apiId,
        string model,
        ProviderResponseObserver? responseObserver,
        int responseObserverTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var service = exception as AmazonServiceException;
        var status = service is null ? 0 : (int)service.StatusCode;
        if (status is < 100 or > 599)
        {
            return;
        }

        await ObserveResponseAsync(
                providerId,
                apiId,
                model,
                status,
                service!.RequestId,
                responseObserver,
                responseObserverTimeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static ValueTask<ProviderResponseObserverOutcome> ObserveResponseAsync(
        string providerId,
        string apiId,
        string model,
        int statusCode,
        string? requestId,
        ProviderResponseObserver? responseObserver,
        int responseObserverTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (statusCode is < 100 or > 599)
        {
            return new ValueTask<ProviderResponseObserverOutcome>(ProviderResponseObserverOutcome.NotConfigured);
        }

        return ProviderResponseObserverRunner.NotifyAsync(
            responseObserver,
            ProviderResponseObservation.FromProviderResponse(
                providerId,
                apiId,
                model,
                statusCode,
                requestId),
            responseObserverTimeoutMilliseconds,
            cancellationToken);
    }

    private static bool IsRetryableErrorCode(string? errorCode) =>
        errorCode is not null
        && (errorCode.Contains("throttl", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("ModelNotReadyException", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("ModelTimeoutException", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("ServiceUnavailableException", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("InternalServerException", StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownTransientLocalFailure(Exception exception) =>
        exception is IOException or HttpRequestException or TimeoutException or TaskCanceledException;

    private static string? Bound(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ? null : value;

    internal static void ApplyHeaders(
        IDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, string> customHeaders)
    {
        foreach (var pair in customHeaders)
        {
            requestHeaders[pair.Key] = pair.Value;
        }
    }

    private static bool TryDequeue(Queue<BedrockProtocolEvent> queue, out BedrockProtocolEvent? value)
    {
        lock (queue)
        {
            if (queue.Count > 0)
            {
                value = queue.Dequeue();
                return true;
            }
        }

        value = null;
        return false;
    }
}

internal static class BedrockClientRequestGate
{
    private static readonly ConditionalWeakTable<object, SemaphoreSlim> Gates = new();

    public static async ValueTask<IDisposable> EnterAsync(
        object client,
        CancellationToken cancellationToken)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        var gate = Gates.GetValue(client, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? gate;

        public Lease(SemaphoreSlim gate)
        {
            this.gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref gate, null)?.Release();
        }
    }
}
