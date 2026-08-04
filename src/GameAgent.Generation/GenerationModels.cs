using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Generation;

public static class GenerationModalities
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Speech = "speech";
    public const string StructuredContent = "structured_content";

    internal static bool IsKnown(string value) =>
        value == Image
        || value == Video
        || value == Speech
        || value == StructuredContent;
}

public static class GenerationJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string CancelRequested = "cancel_requested";
    public const string Materializing = "materializing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Unknown = "unknown";

    public static bool IsTerminal(string value) =>
        value == Succeeded || value == Failed || value == Cancelled;

    internal static bool IsKnown(string value) =>
        value == Queued
        || value == Running
        || value == CancelRequested
        || value == Materializing
        || value == Succeeded
        || value == Failed
        || value == Cancelled
        || value == Unknown;
}

public static class GenerationAcceptance
{
    public const string Accepted = "accepted";
    public const string NotAccepted = "not_accepted";
    public const string Unknown = "unknown";
}

public sealed class GenerationRequest
{
    public string OperationId { get; set; } = string.Empty;

    public string Modality { get; set; } = string.Empty;

    public string? Model { get; set; }

    public JsonElement Input { get; set; }

    public Dictionary<string, JsonElement> Options { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, string> Metadata { get; set; } =
        new(StringComparer.Ordinal);

    public string? AuthorityId { get; set; }

    public string? IdempotencyKey { get; set; }
}

public sealed class GenerationArtifactSource
{
    public Uri? RemoteUri { get; set; }

    public ReadOnlyMemory<byte> InlineData { get; set; }

    public string MediaType { get; set; } = "application/octet-stream";

    public string? FileName { get; set; }

    public string? Sha256 { get; set; }

    public long? SizeBytes { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? AuthorizationReference { get; set; }
}

public sealed class GenerationArtifact
{
    public string ArtifactId { get; set; } = string.Empty;

    public string Uri { get; set; } = string.Empty;

    public string MediaType { get; set; } = "application/octet-stream";

    public string Sha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string? FileName { get; set; }

    public DateTimeOffset? SourceExpiresAt { get; set; }
}

public sealed class GenerationProviderResult
{
    public string Status { get; set; } = GenerationJobStatuses.Unknown;

    public string? ProviderJobId { get; set; }

    public double? Progress { get; set; }

    public JsonElement? Output { get; set; }

    public IReadOnlyList<GenerationArtifactSource> Artifacts { get; set; } =
        Array.Empty<GenerationArtifactSource>();

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public bool Retryable { get; set; }

    public string? CostUsd { get; set; }
}

public sealed class GenerationSubmission
{
    public string Acceptance { get; set; } = GenerationAcceptance.Unknown;

    public GenerationProviderResult Result { get; set; } = new();
}

public sealed class GenerationCancelResult
{
    public bool Accepted { get; set; }

    public string Status { get; set; } = GenerationJobStatuses.Unknown;
}

public sealed class GenerationJob
{
    public string OperationId { get; set; } = string.Empty;

    public string RequestDigest { get; set; } = string.Empty;

    public string Modality { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string? ProviderJobId { get; set; }

    public string Acceptance { get; set; } = GenerationAcceptance.Unknown;

    public string Status { get; set; } = GenerationJobStatuses.Unknown;

    public double? Progress { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public JsonElement? Output { get; set; }

    public IReadOnlyList<GenerationArtifact> Artifacts { get; set; } =
        Array.Empty<GenerationArtifact>();

    /// <summary>
    /// Provider artifact sources durably checkpointed while local materialization
    /// is incomplete. Consumers should treat remote URIs as sensitive and
    /// short-lived provider data.
    /// </summary>
    public IReadOnlyList<GenerationArtifactSource> PendingArtifacts { get; set; } =
        Array.Empty<GenerationArtifactSource>();

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public bool Retryable { get; set; }

    public string? CostUsd { get; set; }

    public string? AuthorityId { get; set; }

    public long Revision { get; set; }
}

public sealed class GenerationEvent
{
    public string OperationId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public double? Progress { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? ReasonCode { get; set; }
}

public sealed class GenerationRuntimeOptions
{
    public int MaxConcurrentSubmissions { get; set; } = 4;

    public int MaxTrackedJobs { get; set; } = 4_096;

    public int MaxInputUtf8Bytes { get; set; } = 256 * 1024;

    public int MaxOutputUtf8Bytes { get; set; } = 4 * 1024 * 1024;

    public int MaxOptions { get; set; } = 64;

    public int MaxMetadataEntries { get; set; } = 64;

    public int MaxArtifactCount { get; set; } = 64;

    public TimeSpan DefaultPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MinimumPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaximumWait { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan EventPublishTimeout { get; set; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        if (MaxConcurrentSubmissions is < 1 or > 256
            || MaxTrackedJobs is < 1 or > 1_000_000
            || MaxInputUtf8Bytes is < 1 or > 16 * 1024 * 1024
            || MaxOutputUtf8Bytes is < 1_024 or > 64 * 1024 * 1024
            || MaxOptions is < 0 or > 1_024
            || MaxMetadataEntries is < 0 or > 1_024
            || MaxArtifactCount is < 0 or > 1_024
            || DefaultPollInterval < MinimumPollInterval
            || MinimumPollInterval < TimeSpan.FromMilliseconds(10)
            || MaximumWait <= TimeSpan.Zero
            || MaximumWait > TimeSpan.FromDays(1)
            || EventPublishTimeout < TimeSpan.FromMilliseconds(10)
            || EventPublishTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(GenerationRuntimeOptions),
                "Generation runtime limits are outside supported bounds.");
        }
    }
}

public static class GenerationRequestSnapshotter
{
    public static GenerationRequest Snapshot(
        GenerationRequest request,
        GenerationRuntimeOptions? limits = null)
    {
        var effectiveLimits = limits ?? new GenerationRuntimeOptions();
        effectiveLimits.Validate();
        return GenerationValidation.SnapshotRequest(request, effectiveLimits);
    }

    public static string ComputeDigest(
        GenerationRequest request,
        GenerationRuntimeOptions? limits = null)
    {
        var snapshot = Snapshot(request, limits);
        return GenerationValidation.ComputeRequestDigest(snapshot);
    }
}

public sealed class GenerationProviderCapabilities
{
    public IReadOnlyList<string> Modalities { get; set; } =
        Array.Empty<string>();

    public bool SupportsPolling { get; set; }

    public bool SupportsCancellation { get; set; }

    public bool SupportsStreamingSpeech { get; set; }
}

public interface IGenerationProvider
{
    string Name { get; }

    GenerationProviderCapabilities Capabilities { get; }

    ValueTask<GenerationSubmission> SubmitAsync(
        GenerationRequest request,
        CancellationToken cancellationToken);

    ValueTask<GenerationProviderResult> GetAsync(
        string providerJobId,
        string modality,
        CancellationToken cancellationToken);

    ValueTask<GenerationCancelResult> CancelAsync(
        string providerJobId,
        string modality,
        CancellationToken cancellationToken);
}

public static class SpeechStreamEventKinds
{
    public const string Started = "started";
    public const string Audio = "audio";
    public const string Completed = "completed";
}

public sealed class SpeechStreamEvent
{
    public string Kind { get; set; } = string.Empty;

    public string MediaType { get; set; } = "application/octet-stream";

    public ReadOnlyMemory<byte> Audio { get; set; }

    public long Sequence { get; set; }

    public TimeSpan Elapsed { get; set; }
}

public interface IStreamingSpeechProvider
{
    string Name { get; }

    IAsyncEnumerable<SpeechStreamEvent> StreamSpeechAsync(
        GenerationRequest request,
        CancellationToken cancellationToken);
}

public interface IGenerationRoutePolicy
{
    IGenerationProvider Select(
        GenerationRequest request,
        IReadOnlyList<IGenerationProvider> providers);
}

public interface IGenerationJobStore
{
    ValueTask<GenerationJob?> TryGetAsync(
        string operationId,
        CancellationToken cancellationToken);

    ValueTask PutAsync(
        GenerationJob job,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GenerationJob>> ListUnfinishedAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}

public interface IGenerationArtifactStore
{
    ValueTask<GenerationArtifact> ImportAsync(
        string operationId,
        int ordinal,
        GenerationArtifactSource source,
        CancellationToken cancellationToken);
}

public interface IGenerationEventSink
{
    ValueTask PublishAsync(
        GenerationEvent generationEvent,
        CancellationToken cancellationToken);
}

public sealed class GenerationOperationException : Exception
{
    public GenerationOperationException(
        string reasonCode,
        string message,
        bool outcomeUncertain = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
        OutcomeUncertain = outcomeUncertain;
    }

    public string ReasonCode { get; }

    public bool OutcomeUncertain { get; }
}

public sealed class GenerationProviderException : Exception
{
    public GenerationProviderException(
        string reasonCode,
        string message,
        string acceptance = GenerationAcceptance.Unknown,
        bool retryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (acceptance != GenerationAcceptance.Accepted
            && acceptance != GenerationAcceptance.NotAccepted
            && acceptance != GenerationAcceptance.Unknown)
        {
            throw new ArgumentException(
                "The provider acceptance value is invalid.",
                nameof(acceptance));
        }

        ReasonCode = reasonCode;
        Acceptance = acceptance;
        Retryable = retryable;
    }

    public string ReasonCode { get; }

    public string Acceptance { get; }

    public bool Retryable { get; }
}

internal static class GenerationValidation
{
    public static GenerationRequest SnapshotRequest(
        GenerationRequest request,
        GenerationRuntimeOptions limits)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var operationId = Identifier(
            request.OperationId,
            nameof(request.OperationId),
            128);
        if (!GenerationModalities.IsKnown(request.Modality))
        {
            throw new ArgumentException(
                "The generation modality is not supported.",
                nameof(request));
        }

        if (request.Options is null || request.Metadata is null)
        {
            throw new ArgumentException(
                "Generation options and metadata collections cannot be null.",
                nameof(request));
        }

        if (request.Input.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "Generation input must contain defined JSON.",
                nameof(request));
        }

        var inputBytes = Encoding.UTF8.GetByteCount(request.Input.GetRawText());
        if (inputBytes > limits.MaxInputUtf8Bytes)
        {
            throw new GenerationOperationException(
                "generation_input_too_large",
                $"Generation input exceeds {limits.MaxInputUtf8Bytes} UTF-8 bytes.");
        }

        if (request.Options.Count > limits.MaxOptions
            || request.Metadata.Count > limits.MaxMetadataEntries)
        {
            throw new GenerationOperationException(
                "generation_metadata_limit_exceeded",
                "Generation options or metadata exceed configured limits.");
        }

        return new GenerationRequest
        {
            OperationId = operationId,
            Modality = request.Modality,
            Model = OptionalText(request.Model, nameof(request.Model), 256),
            Input = request.Input.Clone(),
            Options = SnapshotJsonMap(request.Options, 128, 64 * 1024),
            Metadata = SnapshotStringMap(request.Metadata, 128, 2_048),
            AuthorityId = OptionalText(
                request.AuthorityId,
                nameof(request.AuthorityId),
                128),
            IdempotencyKey = OptionalText(
                request.IdempotencyKey,
                nameof(request.IdempotencyKey),
                256)
        };
    }

    public static GenerationJob SnapshotJob(GenerationJob job)
    {
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        ValidateJob(job);

        return new GenerationJob
        {
            OperationId = job.OperationId,
            RequestDigest = job.RequestDigest,
            Modality = job.Modality,
            Provider = job.Provider,
            ProviderJobId = job.ProviderJobId,
            Acceptance = job.Acceptance,
            Status = job.Status,
            Progress = job.Progress,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            Output = job.Output?.Clone(),
            Artifacts = new ReadOnlyCollection<GenerationArtifact>(
                job.Artifacts.Select(SnapshotArtifact).ToArray()),
            PendingArtifacts = new ReadOnlyCollection<GenerationArtifactSource>(
                job.PendingArtifacts.Select(SnapshotArtifactSource).ToArray()),
            ErrorCode = job.ErrorCode,
            ErrorMessage = job.ErrorMessage,
            Retryable = job.Retryable,
            CostUsd = job.CostUsd,
            AuthorityId = job.AuthorityId,
            Revision = job.Revision
        };
    }

    public static void ValidateProviderResult(
        GenerationProviderResult result,
        GenerationRuntimeOptions limits)
    {
        if (result is null || !GenerationJobStatuses.IsKnown(result.Status))
        {
            throw new GenerationOperationException(
                "generation_provider_contract_invalid",
                "The provider returned an invalid generation status.");
        }

        if (result.Artifacts is null
            || result.Progress.HasValue
            && (double.IsNaN(result.Progress.Value)
                || double.IsInfinity(result.Progress.Value)
                || result.Progress.Value is < 0 or > 1)
            || result.Artifacts?.Count > limits.MaxArtifactCount
            || result.Output is { ValueKind: JsonValueKind.Undefined })
        {
            throw new GenerationOperationException(
                "generation_provider_contract_invalid",
                "The provider returned invalid progress, output, or artifact data.");
        }


        if (result.Output.HasValue
            && Encoding.UTF8.GetByteCount(result.Output.Value.GetRawText())
            > limits.MaxOutputUtf8Bytes)
        {
            throw new GenerationOperationException(
                "generation_provider_output_too_large",
                $"The provider output exceeds {limits.MaxOutputUtf8Bytes} UTF-8 bytes.");
        }

        if (result.ProviderJobId is { } providerJobId
            && (providerJobId.Length is < 1 or > 256
                || providerJobId.Any(char.IsControl))
            || result.ErrorCode is { Length: > 256 }
            || result.ErrorMessage is { Length: > 8_192 }
            || result.CostUsd is { Length: > 128 }
            || result.Artifacts!.Any(source => source is null))
        {
            throw new GenerationOperationException(
                "generation_provider_contract_invalid",
                "The provider returned invalid bounded generation metadata.");
        }

        if (result.CostUsd is not null
            && (!decimal.TryParse(
                    result.CostUsd,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var cost)
                || cost < 0))
        {
            throw new GenerationOperationException(
                "generation_provider_contract_invalid",
                "The provider returned an invalid cost.");
        }
    }

    public static string ComputeRequestDigest(GenerationRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteString("modality", request.Modality);
            WriteOptionalString(writer, "model", request.Model);
            writer.WritePropertyName("input");
            WriteCanonicalJson(writer, request.Input);
            writer.WritePropertyName("options");
            writer.WriteStartObject();
            foreach (var pair in request.Options.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WriteCanonicalJson(writer, pair.Value);
            }

            writer.WriteEndObject();
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            foreach (var pair in request.Metadata.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            WriteOptionalString(writer, "authorityId", request.AuthorityId);
            WriteOptionalString(writer, "idempotencyKey", request.IdempotencyKey);
            writer.WriteEndObject();
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(buffer.WrittenSpan.ToArray());
        var result = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    public static void ValidateJob(GenerationJob job)
    {
        Identifier(job.OperationId, nameof(job.OperationId), 128);
        Identifier(job.Provider, nameof(job.Provider), 128);
        if (!GenerationModalities.IsKnown(job.Modality)
            || job.RequestDigest is null
            || job.RequestDigest.Length != 64
            || job.RequestDigest.Any(character => !Uri.IsHexDigit(character))
            || !GenerationJobStatuses.IsKnown(job.Status)
            || job.Acceptance != GenerationAcceptance.Accepted
               && job.Acceptance != GenerationAcceptance.NotAccepted
               && job.Acceptance != GenerationAcceptance.Unknown
            || job.Progress.HasValue
            && (double.IsNaN(job.Progress.Value)
                || double.IsInfinity(job.Progress.Value)
                || job.Progress.Value is < 0 or > 1)
            || job.CreatedAt == default
            || job.UpdatedAt < job.CreatedAt
            || job.Revision < 1
            || job.ProviderJobId is { } providerJobId
            && (providerJobId.Length is < 1 or > 256
                || providerJobId.Any(char.IsControl))
            || job.ErrorCode is { Length: > 256 }
            || job.ErrorMessage is { Length: > 8_192 }
            || job.CostUsd is { Length: > 128 }
            || job.AuthorityId is { Length: > 128 }
            || job.Artifacts is null
            || job.Artifacts.Count > 1_024
            || job.Artifacts.Any(artifact => !IsValidArtifact(artifact))
            || job.PendingArtifacts is null
            || job.PendingArtifacts.Count > 1_024
            || job.PendingArtifacts.Any(source => !IsValidArtifactSource(source))
            || job.Status != GenerationJobStatuses.Materializing
            && job.PendingArtifacts.Count != 0
            || job.CostUsd is not null
            && (!decimal.TryParse(
                    job.CostUsd,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var cost)
                || cost < 0))
        {
            throw new GenerationOperationException(
                "generation_job_invalid",
                "The generation job contains invalid durable state.");
        }

        if (job.AuthorityId is not null)
        {
            Identifier(job.AuthorityId, nameof(job.AuthorityId), 128);
        }
    }

    public static string Identifier(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(character =>
                !(char.IsLetterOrDigit(character)
                  || character is '_' or '-' or '.' or ':' or '/')))
        {
            throw new ArgumentException(
                "The value must be a bounded portable identifier.",
                parameterName);
        }

        return value;
    }

    private static string? OptionalText(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0 || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value must contain 1 to {maximumLength} characters.",
                parameterName);
        }

        return value;
    }

    private static Dictionary<string, JsonElement> SnapshotJsonMap(
        IReadOnlyDictionary<string, JsonElement> source,
        int maximumKeyLength,
        int maximumValueBytes)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var key = Identifier(pair.Key, nameof(source), maximumKeyLength);
            if (pair.Value.ValueKind == JsonValueKind.Undefined
                || Encoding.UTF8.GetByteCount(pair.Value.GetRawText()) > maximumValueBytes)
            {
                throw new GenerationOperationException(
                    "generation_option_invalid",
                    $"Generation option '{key}' is undefined or too large.");
            }

            result.Add(key, pair.Value.Clone());
        }

        return result;
    }

    private static Dictionary<string, string> SnapshotStringMap(
        IReadOnlyDictionary<string, string> source,
        int maximumKeyLength,
        int maximumValueLength)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var key = Identifier(pair.Key, nameof(source), maximumKeyLength);
            if (pair.Value is null || pair.Value.Length > maximumValueLength)
            {
                throw new GenerationOperationException(
                    "generation_metadata_invalid",
                    $"Generation metadata '{key}' is too large.");
            }

            result.Add(key, pair.Value);
        }

        return result;
    }

    private static GenerationArtifact SnapshotArtifact(GenerationArtifact artifact) =>
        new()
        {
            ArtifactId = artifact.ArtifactId,
            Uri = artifact.Uri,
            MediaType = artifact.MediaType,
            Sha256 = artifact.Sha256,
            SizeBytes = artifact.SizeBytes,
            FileName = artifact.FileName,
            SourceExpiresAt = artifact.SourceExpiresAt
        };

    internal static GenerationArtifactSource SnapshotArtifactSource(
        GenerationArtifactSource source) =>
        new()
        {
            RemoteUri = source.RemoteUri is null
                ? null
                : new Uri(source.RemoteUri.OriginalString, UriKind.Absolute),
            InlineData = source.InlineData.IsEmpty
                ? ReadOnlyMemory<byte>.Empty
                : source.InlineData.ToArray(),
            MediaType = source.MediaType,
            FileName = source.FileName,
            Sha256 = source.Sha256,
            SizeBytes = source.SizeBytes,
            ExpiresAt = source.ExpiresAt,
            AuthorizationReference = source.AuthorizationReference
        };

    private static bool IsValidArtifact(GenerationArtifact? artifact) =>
        artifact is not null
        && artifact.ArtifactId is { Length: > 0 and <= 256 }
        && artifact.Uri is { Length: > 0 and <= 4_096 }
        && Uri.TryCreate(artifact.Uri, UriKind.Absolute, out _)
        && artifact.MediaType is { Length: > 0 and <= 255 }
        && !artifact.MediaType.Any(char.IsControl)
        && artifact.Sha256 is { Length: 64 }
        && artifact.Sha256.All(Uri.IsHexDigit)
        && artifact.SizeBytes > 0
        && artifact.FileName is not { Length: > 255 };

    private static bool IsValidArtifactSource(GenerationArtifactSource? source)
    {
        if (source is null
            || source.MediaType is not { Length: > 0 and <= 255 }
            || source.MediaType.Any(char.IsControl)
            || source.FileName is { Length: > 255 }
            || source.AuthorizationReference is { Length: > 256 }
            || source.AuthorizationReference is not null
            && source.AuthorizationReference.Any(char.IsControl)
            || source.SizeBytes is < 1
            || source.Sha256 is not null
            && (source.Sha256.Length != 64 || source.Sha256.Any(character => !Uri.IsHexDigit(character))))
        {
            return false;
        }

        var hasInline = !source.InlineData.IsEmpty;
        if (hasInline == (source.RemoteUri is not null))
        {
            return false;
        }

        return source.RemoteUri is null
               || source.RemoteUri.IsAbsoluteUri
               && source.RemoteUri.OriginalString.Length <= 4_096
               && string.IsNullOrEmpty(source.RemoteUri.UserInfo)
               && string.IsNullOrEmpty(source.RemoteUri.Fragment);
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
