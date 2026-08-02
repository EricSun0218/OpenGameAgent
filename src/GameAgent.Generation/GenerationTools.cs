using System.Buffers;
using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Generation;

public static class GenerationToolNames
{
    public const string GenerateImage = "generate_image";
    public const string GenerateVideo = "generate_video";
    public const string GenerateSpeech = "generate_speech";
    public const string GenerateStructuredContent = "generate_structured_content";
    public const string GetGeneration = "get_generation";
    public const string CancelGeneration = "cancel_generation";

    internal static string? ModalityFor(string actionName) => actionName switch
    {
        GenerateImage => GenerationModalities.Image,
        GenerateVideo => GenerationModalities.Video,
        GenerateSpeech => GenerationModalities.Speech,
        GenerateStructuredContent => GenerationModalities.StructuredContent,
        _ => null
    };
}

public sealed class GenerationToolBridge
{
    private static readonly IReadOnlyList<ToolDescriptor> DescriptorCatalog =
        CreateDescriptors();
    private readonly GenerationRuntime _runtime;

    public GenerationToolBridge(GenerationRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IReadOnlyList<ToolDescriptor> Tools =>
        new ReadOnlyCollection<ToolDescriptor>(
            DescriptorCatalog.Select(CloneDescriptor).ToArray());

    public bool Handles(string actionName) =>
        GenerationToolNames.ModalityFor(actionName) is not null
        || actionName is GenerationToolNames.GetGeneration
            or GenerationToolNames.CancelGeneration;

    public async ValueTask<ActionReceipt?> TryHandleAsync(
        ActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!Handles(request.ActionName))
        {
            return null;
        }

        return await HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ActionReceipt> HandleAsync(
        ActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var modality = GenerationToolNames.ModalityFor(request.ActionName);
        try
        {
            GenerationJob job;
            if (modality is not null)
            {
                job = await _runtime.SubmitAsync(
                        ParseGenerationRequest(request, modality),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var targetOperationId = ReadTargetOperationId(request.Arguments);
                job = request.ActionName == GenerationToolNames.GetGeneration
                    ? await RequireAsync(targetOperationId, cancellationToken)
                        .ConfigureAwait(false)
                    : await _runtime.RequestCancellationAsync(
                            targetOperationId,
                            cancellationToken)
                        .ConfigureAwait(false);
            }

            return ReceiptForJob(request, job);
        }
        catch (GenerationOperationException exception)
        {
            return ErrorReceipt(
                request,
                exception.OutcomeUncertain
                    ? ReceiptStatuses.Unknown
                    : ReceiptStatuses.Failed,
                exception.ReasonCode,
                exception.Message,
                retryable: false);
        }
        catch (KeyNotFoundException exception)
        {
            return ErrorReceipt(
                request,
                ReceiptStatuses.Failed,
                "generation_not_found",
                exception.Message,
                retryable: false);
        }
        catch (ArgumentException exception)
        {
            return ErrorReceipt(
                request,
                ReceiptStatuses.Rejected,
                "generation_arguments_invalid",
                exception.Message,
                retryable: false);
        }
    }

    private async ValueTask<GenerationJob> RequireAsync(
        string operationId,
        CancellationToken cancellationToken) =>
        await _runtime.TryGetAsync(operationId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new KeyNotFoundException(
            $"Generation operation '{operationId}' was not found.");

    private static GenerationRequest ParseGenerationRequest(
        ActionRequest action,
        string modality)
    {
        if (action.Arguments.ValueKind != JsonValueKind.Object
            || !action.Arguments.TryGetProperty("input", out var input))
        {
            throw new ArgumentException(
                "Generation tool arguments require an 'input' JSON value.",
                nameof(action));
        }

        return new GenerationRequest
        {
            OperationId = action.OperationId,
            Modality = modality,
            Model = ReadOptionalString(action.Arguments, "model", 256),
            Input = input.Clone(),
            Options = ReadJsonMap(action.Arguments, "options"),
            Metadata = ReadStringMap(action.Arguments, "metadata"),
            AuthorityId = action.AgentId,
            IdempotencyKey = ReadOptionalString(
                action.Arguments,
                "idempotencyKey",
                256)
                ?? action.OperationId
        };
    }

    private static string ReadTargetOperationId(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("operationId", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                "Generation status tools require an 'operationId' string.",
                nameof(arguments));
        }

        return GenerationValidation.Identifier(
            value.GetString()!,
            nameof(arguments),
            128);
    }

    private static Dictionary<string, JsonElement> ReadJsonMap(
        JsonElement arguments,
        string name)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"'{name}' must be a JSON object.", nameof(arguments));
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new ArgumentException(
                    $"'{name}' contains duplicate properties.",
                    nameof(arguments));
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadStringMap(
        JsonElement arguments,
        string name)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"'{name}' must be a JSON object.", nameof(arguments));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String
                || !result.TryAdd(property.Name, property.Value.GetString()!))
            {
                throw new ArgumentException(
                    $"'{name}' must contain unique string values.",
                    nameof(arguments));
            }
        }

        return result;
    }

    private static string? ReadOptionalString(
        JsonElement parent,
        string name,
        int maximumLength)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text
            || text.Length > maximumLength)
        {
            throw new ArgumentException(
                $"'{name}' must be a bounded non-empty string.",
                nameof(parent));
        }

        return text;
    }

    private static ActionReceipt ReceiptForJob(
        ActionRequest request,
        GenerationJob job)
    {
        var status = job.Status == GenerationJobStatuses.Failed
            ? ReceiptStatuses.Failed
            : job.Status == GenerationJobStatuses.Unknown
                ? ReceiptStatuses.Unknown
                : ReceiptStatuses.Succeeded;
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = Math.Max(1, job.Revision),
            Status = status,
            Result = WriteJob(job),
            ErrorCode = status == ReceiptStatuses.Succeeded
                ? null
                : job.ErrorCode ?? "generation_incomplete",
            Retryable = false,
            CommittedAt = status == ReceiptStatuses.Succeeded
                ? DateTimeOffset.UtcNow
                : null,
            ReceivedAt = DateTimeOffset.UtcNow
        };
    }

    private static ActionReceipt ErrorReceipt(
        ActionRequest request,
        string status,
        string reasonCode,
        string message,
        bool retryable) =>
        new()
        {
            OperationId = request.OperationId,
            Revision = 1,
            Status = status,
            Result = WriteError(reasonCode, message),
            ErrorCode = reasonCode,
            Retryable = retryable,
            ReceivedAt = DateTimeOffset.UtcNow
        };

    private static JsonElement WriteJob(GenerationJob job)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operationId", job.OperationId);
            writer.WriteString("modality", job.Modality);
            writer.WriteString("provider", job.Provider);
            writer.WriteString("status", job.Status);
            if (job.Progress.HasValue)
            {
                writer.WriteNumber("progress", job.Progress.Value);
            }

            if (job.Output.HasValue)
            {
                writer.WritePropertyName("output");
                job.Output.Value.WriteTo(writer);
            }

            writer.WriteStartArray("artifacts");
            foreach (var artifact in job.Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("artifactId", artifact.ArtifactId);
                writer.WriteString("uri", artifact.Uri);
                writer.WriteString("mediaType", artifact.MediaType);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteNumber("sizeBytes", artifact.SizeBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (job.ErrorCode is not null)
            {
                writer.WriteString("errorCode", job.ErrorCode);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement WriteError(string reasonCode, string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("errorCode", reasonCode);
            writer.WriteString("message", message.Length <= 2_048
                ? message
                : message[..2_048]);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<ToolDescriptor> CreateDescriptors()
    {
        var generationSchema = ProtocolJson.ParseElement(
            "{\"type\":\"object\",\"properties\":{\"input\":{},\"model\":{\"type\":\"string\"},\"options\":{\"type\":\"object\"},\"metadata\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}},\"idempotencyKey\":{\"type\":\"string\"}},\"required\":[\"input\"],\"additionalProperties\":false}");
        var lookupSchema = ProtocolJson.ParseElement(
            "{\"type\":\"object\",\"properties\":{\"operationId\":{\"type\":\"string\"}},\"required\":[\"operationId\"],\"additionalProperties\":false}");
        var resultSchema = ProtocolJson.ParseElement(
            "{\"type\":\"object\",\"required\":[\"operationId\",\"status\"],\"properties\":{\"operationId\":{\"type\":\"string\"},\"modality\":{\"type\":\"string\"},\"provider\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"},\"progress\":{\"type\":\"number\"},\"output\":{},\"artifacts\":{\"type\":\"array\"},\"errorCode\":{\"type\":\"string\"}},\"additionalProperties\":false}");
        return new ReadOnlyCollection<ToolDescriptor>(new[]
        {
            CreateGenerationDescriptor(GenerationToolNames.GenerateImage, "Generate an image artifact.", generationSchema, resultSchema),
            CreateGenerationDescriptor(GenerationToolNames.GenerateVideo, "Generate a video artifact asynchronously.", generationSchema, resultSchema),
            CreateGenerationDescriptor(GenerationToolNames.GenerateSpeech, "Generate a speech audio artifact.", generationSchema, resultSchema),
            CreateGenerationDescriptor(GenerationToolNames.GenerateStructuredContent, "Generate bounded structured game content.", generationSchema, resultSchema),
            CreateLookupDescriptor(GenerationToolNames.GetGeneration, "Read a generation job without changing it.", ToolEffects.PureRead, lookupSchema, resultSchema),
            CreateLookupDescriptor(GenerationToolNames.CancelGeneration, "Request cancellation of a generation job.", ToolEffects.ExternalWrite, lookupSchema, resultSchema)
        });
    }

    private static ToolDescriptor CreateGenerationDescriptor(
        string name,
        string description,
        JsonElement parameters,
        JsonElement result) =>
        CreateLookupDescriptor(
            name,
            description,
            ToolEffects.ExternalWrite,
            parameters,
            result);

    private static ToolDescriptor CreateLookupDescriptor(
        string name,
        string description,
        string effect,
        JsonElement parameters,
        JsonElement result) =>
        new()
        {
            Name = name,
            Version = "1.0.0",
            Description = description,
            ParametersSchema = parameters.Clone(),
            ResultSchema = result.Clone(),
            Effect = effect,
            ConflictScopes = new List<string>(),
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 600_000,
            RetryPolicy = effect == ToolEffects.PureRead
                ? ToolRetryPolicies.SafeRead
                : ToolRetryPolicies.Idempotent,
            IdempotencyPolicy = effect == ToolEffects.PureRead
                ? ToolIdempotencyPolicies.None
                : ToolIdempotencyPolicies.Required,
            Toolset = "generation",
            Visibility = ToolVisibilities.Direct
        };

    private static ToolDescriptor CloneDescriptor(ToolDescriptor descriptor) =>
        ProtocolJson.DeserializeToolDescriptor(ProtocolJson.Serialize(descriptor));
}
