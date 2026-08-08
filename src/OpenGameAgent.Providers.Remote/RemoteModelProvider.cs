using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Remote;

public sealed class RemoteModelProvider : IModelProvider
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly RemoteModelProviderSettings _settings;

    public RemoteModelProvider(RemoteModelProviderOptions options)
    {
        _settings = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestJson = ProxyWire.SerializeRequest(request);
        if (StrictUtf8.GetByteCount(requestJson) > _settings.MaximumRequestBytes)
        {
            throw new InvalidDataException("The remote provider request exceeded the configured size limit.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint)
        {
            Content = new StringContent(requestJson, StrictUtf8, "application/json"),
        };
        httpRequest.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        foreach (var pair in _settings.Headers)
        {
            if (!httpRequest.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("A configured remote provider header could not be applied.");
            }
        }

        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            var credential = string.IsNullOrEmpty(_settings.ApiKeyScheme)
                ? _settings.ApiKey
                : _settings.ApiKeyScheme + " " + _settings.ApiKey;
            if (!httpRequest.Headers.TryAddWithoutValidation(_settings.ApiKeyHeader, credential))
            {
                throw new InvalidOperationException("The configured remote provider API key header could not be applied.");
            }
        }

        using var response = await _settings.HttpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedBodyAsync(
                response.Content,
                _settings.MaximumEventBytes,
                cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                "Remote provider HTTP error "
                + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ": "
                + errorBody);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The remote provider response must use text/event-stream.");
        }

        var responseStreamTask = response.Content.ReadAsStreamAsync();
        using var pendingContentRegistration = cancellationToken.Register(response.Content.Dispose);
        using var responseStream = await AwaitWithCancellation(responseStreamTask, cancellationToken).ConfigureAwait(false);
        using var registration = cancellationToken.Register(responseStream.Dispose);
        using var boundedStream = new BoundedReadStream(responseStream, _settings.MaximumResponseBytes);
        using var reader = new StreamReader(boundedStream, StrictUtf8, false, 4096, leaveOpen: false);
        var decoder = new RemoteStreamDecoder();
        var data = new StringBuilder();
        var dataBytes = 0;
        var hasDataLine = false;
        var frameCount = 0;
        ModelStreamEvent? terminal = null;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (!hasDataLine)
                {
                    continue;
                }

                var decoded = DecodeFrame(data.ToString(), decoder, ref frameCount);
                data.Clear();
                dataBytes = 0;
                hasDataLine = false;
                if (decoded is null)
                {
                    continue;
                }

                if (decoded.IsTerminal)
                {
                    if (terminal is not null)
                    {
                        throw new InvalidDataException("The remote provider stream emitted more than one terminal event.");
                    }

                    terminal = decoded;
                }
                else
                {
                    if (terminal is not null)
                    {
                        throw new InvalidDataException("The remote provider stream emitted an event after its terminal event.");
                    }

                    yield return decoded;
                }

                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The remote provider stream contains an unsupported SSE field.");
            }

            var value = line.Substring(5);
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value.Substring(1);
            }

            var valueBytes = StrictUtf8.GetByteCount(value);
            var separatorBytes = hasDataLine ? 1 : 0;
            if ((long)dataBytes + separatorBytes + valueBytes > _settings.MaximumEventBytes)
            {
                throw new InvalidDataException("A remote provider stream event exceeded the configured size limit.");
            }

            if (hasDataLine)
            {
                data.Append('\n');
            }

            data.Append(value);
            dataBytes += separatorBytes + valueBytes;
            hasDataLine = true;
        }

        if (hasDataLine)
        {
            var decoded = DecodeFrame(data.ToString(), decoder, ref frameCount);
            if (decoded is { IsTerminal: true })
            {
                if (terminal is not null)
                {
                    throw new InvalidDataException("The remote provider stream emitted more than one terminal event.");
                }

                terminal = decoded;
            }
            else if (decoded is not null)
            {
                if (terminal is not null)
                {
                    throw new InvalidDataException("The remote provider stream emitted an event after its terminal event.");
                }

                yield return decoded;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        decoder.EnsureComplete();
        yield return terminal
                     ?? throw new InvalidDataException("The remote provider stream ended without a terminal event.");
    }

    private ModelStreamEvent? DecodeFrame(string json, RemoteStreamDecoder decoder, ref int frameCount)
    {
        if (StrictUtf8.GetByteCount(json) > _settings.MaximumEventBytes)
        {
            throw new InvalidDataException("A remote provider stream event exceeded the configured size limit.");
        }

        frameCount++;
        if (frameCount > _settings.MaximumEvents)
        {
            throw new InvalidDataException("The remote provider stream exceeded the configured event limit.");
        }

        return decoder.Decode(ProxyWire.ParseFrame(json, _settings.MaximumJsonDepth));
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(source.Dispose);
        using var bounded = new BoundedReadStream(source, maximumBytes);
        using var reader = new StreamReader(bounded, StrictUtf8, false, 4096, leaveOpen: false);
        try
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => canceled.TrySetResult(true));
        if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
        {
            ObserveLateFault(task);
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}

internal sealed class RemoteStreamDecoder
{
    private readonly List<AgentContent> _content = new();
    private readonly Dictionary<int, ContentFamily> _active = new();
    private ModelResponse? _setup;
    private bool _started;
    private bool _terminal;
    private bool _sawContentEvents;

    public ModelStreamEvent? Decode(WireFrame frame)
    {
        if (frame is null || string.IsNullOrWhiteSpace(frame.Type))
        {
            throw new InvalidDataException("A remote provider stream frame requires a type.");
        }

        if (_terminal)
        {
            throw new InvalidDataException("The remote provider stream emitted data after its terminal event.");
        }

        return frame.Type switch
        {
            ProxyWire.SetupFrame => Setup(frame),
            ProxyWire.EventFrame => Update(frame),
            ProxyWire.TerminalFrame => Terminal(frame),
            _ => throw new InvalidDataException("The remote provider stream contains an unknown frame type."),
        };
    }

    public void EnsureComplete()
    {
        if (_setup is null)
        {
            throw new InvalidDataException("The remote provider stream did not contain setup metadata.");
        }

        if (!_terminal)
        {
            throw new InvalidDataException("The remote provider stream ended without a terminal event.");
        }
    }

    private ModelStreamEvent? Setup(WireFrame frame)
    {
        if (_setup is not null
            || frame.Version != ProxyWire.Version
            || frame.Response is null
            || frame.Kind is not null
            || frame.ContentIndex is not null
            || frame.Delta is not null
            || frame.ToolCallId is not null
            || frame.ToolName is not null
            || frame.Content is not null)
        {
            throw new InvalidDataException("The remote provider setup frame is invalid or duplicated.");
        }

        _setup = ProxyWire.ToResponse(frame.Response);
        if (_setup.StopReason != ModelStopReason.Pending || _setup.Deferred is not null)
        {
            throw new InvalidDataException("The remote provider setup response must be pending.");
        }

        _content.AddRange(_setup.Content);
        return null;
    }

    private ModelStreamEvent Update(WireFrame frame)
    {
        if (_setup is null || frame.Version is not null || frame.Response is not null || frame.Kind is null)
        {
            throw new InvalidDataException("A remote provider update frame appeared before setup or has invalid fields.");
        }

        var kind = WireMessage.RequireEnum<ModelStreamEventKind>(frame.Kind.Value, nameof(frame.Kind));
        if (kind is ModelStreamEventKind.Completed or ModelStreamEventKind.Failed)
        {
            throw new InvalidDataException("Terminal model events require a terminal frame.");
        }

        if (kind == ModelStreamEventKind.Started)
        {
            if (_started
                || frame.ContentIndex is not null
                || frame.Delta is not null
                || frame.ToolCallId is not null
                || frame.ToolName is not null
                || frame.Content is not null)
            {
                throw new InvalidDataException("The remote provider start event is invalid or duplicated.");
            }

            _started = true;
            return ModelStreamEvent.Update(ModelStreamEventKind.Started, Snapshot());
        }

        if (!_started)
        {
            throw new InvalidDataException("A remote provider content event appeared before the start event.");
        }

        var index = frame.ContentIndex
                    ?? throw new InvalidDataException("A remote provider content event requires an index.");
        if (index < 0)
        {
            throw new InvalidDataException("A remote provider content index cannot be negative.");
        }

        var family = Family(kind);
        if (IsStart(kind))
        {
            if (index != _content.Count || _active.ContainsKey(index) || frame.Content is null || frame.Delta is not null)
            {
                throw new InvalidDataException("A remote provider content block started out of order.");
            }

            var content = ProxyWire.ToContent(frame.Content);
            RequireFamily(content, family);
            _content.Add(content);
            _active.Add(index, family);
            _sawContentEvents = true;
            return CreateUpdate(kind, index, null, content, frame.ToolCallId, frame.ToolName);
        }

        if (!_active.TryGetValue(index, out var activeFamily) || activeFamily != family || index >= _content.Count)
        {
            throw new InvalidDataException("A remote provider content update referenced a missing or ended block.");
        }

        if (IsDelta(kind))
        {
            if (frame.Delta is null)
            {
                throw new InvalidDataException("A remote provider delta event requires delta content.");
            }

            if (family == ContentFamily.Tool)
            {
                var replacement = frame.Content is null
                    ? throw new InvalidDataException("A remote tool delta requires a normalized partial tool call.")
                    : ProxyWire.ToContent(frame.Content);
                RequireFamily(replacement, family);
                var previous = (ToolCallContent)_content[index];
                var current = (ToolCallContent)replacement;
                if (!string.Equals(previous.Id, current.Id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A remote tool call changed identity while streaming.");
                }

                _content[index] = replacement;
            }
            else
            {
                if (frame.Content is not null)
                {
                    throw new InvalidDataException("Text and reasoning deltas cannot carry replacement content.");
                }

                _content[index] = Append(_content[index], frame.Delta);
            }

            return CreateUpdate(
                kind,
                index,
                frame.Delta,
                _content[index],
                frame.ToolCallId,
                frame.ToolName);
        }

        if (!IsEnd(kind) || frame.Content is null || frame.Delta is not null)
        {
            throw new InvalidDataException("The remote provider content event kind is invalid.");
        }

        var finalContent = ProxyWire.ToContent(frame.Content);
        RequireFamily(finalContent, family);
        if (family == ContentFamily.Tool
            && !string.Equals(((ToolCallContent)_content[index]).Id, ((ToolCallContent)finalContent).Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A remote tool call changed identity before completion.");
        }

        _content[index] = finalContent;
        _active.Remove(index);
        return CreateUpdate(kind, index, null, finalContent, frame.ToolCallId, frame.ToolName);
    }

    private ModelStreamEvent Terminal(WireFrame frame)
    {
        if (_setup is null
            || frame.Version is not null
            || frame.Kind is not null
            || frame.ContentIndex is not null
            || frame.Delta is not null
            || frame.ToolCallId is not null
            || frame.ToolName is not null
            || frame.Content is not null
            || frame.Response is null)
        {
            throw new InvalidDataException("The remote provider terminal frame is invalid.");
        }

        var response = ProxyWire.ToResponse(frame.Response);
        if (response.StopReason == ModelStopReason.Pending)
        {
            throw new InvalidDataException("The remote provider terminal response cannot be pending.");
        }

        if (_active.Count > 0 && response.StopReason is not ModelStopReason.Error and not ModelStopReason.Aborted)
        {
            throw new InvalidDataException("A successful remote provider terminal response arrived with open content blocks.");
        }

        if (!_started && response.StopReason is not ModelStopReason.Error and not ModelStopReason.Aborted)
        {
            throw new InvalidDataException("A successful remote provider terminal response requires a start event.");
        }

        if (_sawContentEvents && !ProxyWire.ContentSequenceEquals(_content, response.Content))
        {
            throw new InvalidDataException("The remote provider terminal response disagrees with the streamed content.");
        }

        _terminal = true;
        return ModelStreamEvent.Terminal(response);
    }

    private ModelResponse Snapshot() => new(
        _content,
        ModelStopReason.Pending,
        _setup!.Usage,
        provider: _setup.Provider,
        api: _setup.Api,
        responseModel: _setup.ResponseModel,
        responseId: _setup.ResponseId,
        rawStopReason: _setup.RawStopReason,
        endTurn: _setup.EndTurn,
        diagnostics: _setup.Diagnostics);

    private ModelStreamEvent CreateUpdate(
        ModelStreamEventKind kind,
        int index,
        string? delta,
        AgentContent content,
        string? toolCallId,
        string? toolName)
    {
        var tool = content as ToolCallContent;
        return ModelStreamEvent.Update(
            kind,
            Snapshot(),
            delta,
            index,
            toolCallId,
            toolName,
            kind == ModelStreamEventKind.ToolCallEnded ? tool : null,
            kind is ModelStreamEventKind.TextEnded or ModelStreamEventKind.ReasoningEnded
                ? ContentText(content)
                : null);
    }

    private static AgentContent Append(AgentContent content, string delta) => content switch
    {
        TextContent text => new TextContent(text.Text + delta, text.Signature, text.Phase),
        ReasoningContent reasoning => new ReasoningContent(
            reasoning.Text + delta,
            reasoning.Signature,
            reasoning.Redacted),
        _ => throw new InvalidDataException("A remote text delta targeted non-text content."),
    };

    private static string ContentText(AgentContent content) => content switch
    {
        TextContent text => text.Text,
        ReasoningContent reasoning => reasoning.Text,
        _ => throw new InvalidDataException("A remote content end event targeted non-text content."),
    };

    private static void RequireFamily(AgentContent content, ContentFamily family)
    {
        if ((family == ContentFamily.Text && content is TextContent)
            || (family == ContentFamily.Reasoning && content is ReasoningContent)
            || (family == ContentFamily.Tool && content is ToolCallContent))
        {
            return;
        }

        throw new InvalidDataException("The remote provider content type does not match its event kind.");
    }

    private static ContentFamily Family(ModelStreamEventKind kind) => kind switch
    {
        ModelStreamEventKind.TextStarted or ModelStreamEventKind.TextDelta or ModelStreamEventKind.TextEnded =>
            ContentFamily.Text,
        ModelStreamEventKind.ReasoningStarted or ModelStreamEventKind.ReasoningDelta or ModelStreamEventKind.ReasoningEnded =>
            ContentFamily.Reasoning,
        ModelStreamEventKind.ToolCallStarted or ModelStreamEventKind.ToolCallDelta or ModelStreamEventKind.ToolCallEnded =>
            ContentFamily.Tool,
        _ => throw new InvalidDataException("The remote provider event is not a content event."),
    };

    private static bool IsStart(ModelStreamEventKind kind) => kind is
        ModelStreamEventKind.TextStarted or
        ModelStreamEventKind.ReasoningStarted or
        ModelStreamEventKind.ToolCallStarted;

    private static bool IsDelta(ModelStreamEventKind kind) => kind is
        ModelStreamEventKind.TextDelta or
        ModelStreamEventKind.ReasoningDelta or
        ModelStreamEventKind.ToolCallDelta;

    private static bool IsEnd(ModelStreamEventKind kind) => kind is
        ModelStreamEventKind.TextEnded or
        ModelStreamEventKind.ReasoningEnded or
        ModelStreamEventKind.ToolCallEnded;

    private enum ContentFamily
    {
        Text,
        Reasoning,
        Tool,
    }
}

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private long _read;

    public BoundedReadStream(Stream inner, long maximumBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var value = _inner.Read(buffer, offset, count);
        Count(value);
        return value;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var value = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Count(value);
        return value;
    }

    private void Count(int value)
    {
        _read = checked(_read + value);
        if (_read > _maximumBytes)
        {
            throw new InvalidDataException("The remote provider response exceeded the configured size limit.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
