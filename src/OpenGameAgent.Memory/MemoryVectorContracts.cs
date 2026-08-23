using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Memory;

public sealed class MemoryEmbeddingIdentity : IEquatable<MemoryEmbeddingIdentity>
{
    public MemoryEmbeddingIdentity(
        string providerId,
        string modelId,
        string version,
        int dimensions)
    {
        ProviderId = MemoryVectorGuard.Id(providerId, nameof(providerId), 256);
        ModelId = MemoryVectorGuard.Id(modelId, nameof(modelId), 512);
        Version = MemoryVectorGuard.Id(version, nameof(version), 256);
        if (dimensions < 1 || dimensions > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }

        Dimensions = dimensions;
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string Version { get; }

    public int Dimensions { get; }

    public bool Equals(MemoryEmbeddingIdentity? other) =>
        other is not null
        && string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal)
        && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal)
        && string.Equals(Version, other.Version, StringComparison.Ordinal)
        && Dimensions == other.Dimensions;

    public override bool Equals(object? obj) => Equals(obj as MemoryEmbeddingIdentity);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(ProviderId);
            hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ModelId);
            hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Version);
            return (hash * 397) ^ Dimensions;
        }
    }

    public override string ToString() => $"{ProviderId}/{ModelId}@{Version}:{Dimensions}";
}

/// <summary>
/// Supplies vectors without imposing a model runtime. A game may implement
/// this interface with an in-process model, a local sidecar, or a remote API.
/// Query and document methods are separate so asymmetric embedding models can
/// select the correct task or input type.
/// </summary>
public interface IMemoryEmbeddingProvider : IAsyncDisposable
{
    MemoryEmbeddingIdentity Identity { get; }

    ValueTask<ReadOnlyMemory<float>> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}

public interface IMemoryEmbeddingTextProjector
{
    string ProjectDocument(GameMemory memory);

    string ProjectQuery(GameMemoryQuery query);
}

public sealed class DefaultMemoryEmbeddingTextProjector : IMemoryEmbeddingTextProjector
{
    private readonly int _maximumCharacters;

    public DefaultMemoryEmbeddingTextProjector(int maximumCharacters = 100_000)
    {
        if (maximumCharacters < 1 || maximumCharacters > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        _maximumCharacters = maximumCharacters;
    }

    public string ProjectDocument(GameMemory memory)
    {
        if (memory is null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(memory.SearchableText))
        {
            builder.Append(memory.SearchableText);
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(memory.PayloadJson);
        if (memory.Tags.Count > 0)
        {
            builder.Append('\n').Append(string.Join(" ", memory.Tags));
        }

        return Bound(builder.ToString());
    }

    public string ProjectQuery(GameMemoryQuery query)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        return Bound(query.Text ?? string.Empty);
    }

    private string Bound(string value) => value.Length <= _maximumCharacters
        ? value
        : value.Substring(0, _maximumCharacters);
}

public sealed class VectorMemoryIndexEntry
{
    public VectorMemoryIndexEntry(
        GameMemory memory,
        MemoryEmbeddingIdentity? identity,
        IReadOnlyList<float>? vector,
        string? diagnosticCode = null)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Identity = identity;
        if (vector is not null)
        {
            if (identity is null || vector.Count != identity.Dimensions)
            {
                throw new ArgumentException("A vector must match its embedding identity.", nameof(vector));
            }

            var copied = vector.ToArray();
            MemoryVectorGuard.ValidateVector(copied, identity.Dimensions, nameof(vector));
            Vector = Array.AsReadOnly(copied);
        }

        DiagnosticCode = diagnosticCode is null
            ? null
            : MemoryVectorGuard.Id(diagnosticCode, nameof(diagnosticCode), 256);
        if (Vector is null && DiagnosticCode is null)
        {
            DiagnosticCode = "embedding_pending";
        }
    }

    public GameMemory Memory { get; }

    public MemoryEmbeddingIdentity? Identity { get; }

    public IReadOnlyList<float>? Vector { get; }

    public string? DiagnosticCode { get; }
}

public interface IVectorMemoryIndex
{
    ValueTask UpsertAsync(VectorMemoryIndexEntry entry, CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        string sessionId,
        string ownerId,
        string memoryId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<VectorMemoryIndexEntry>> ListAsync(
        string sessionId,
        int maximumEntries,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional owner-partition read capability. Implementations must return only
/// entries matching both identities and preserve the same bounds as ListAsync.
/// </summary>
public interface IVectorMemoryPartitionIndex
{
    ValueTask<IReadOnlyList<VectorMemoryIndexEntry>> ListAsync(
        string sessionId,
        string ownerId,
        int maximumEntries,
        CancellationToken cancellationToken);
}

public enum MemoryVectorDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed class MemoryVectorDiagnostic
{
    public MemoryVectorDiagnostic(
        string code,
        MemoryVectorDiagnosticSeverity severity,
        string message,
        string? sessionId = null,
        string? ownerId = null,
        string? memoryId = null,
        string? detailsJson = null)
    {
        Code = MemoryVectorGuard.Id(code, nameof(code), 256);
        if (!Enum.IsDefined(typeof(MemoryVectorDiagnosticSeverity), severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
        Message = MemoryVectorGuard.Text(message, nameof(message), 4_096);
        SessionId = MemoryVectorGuard.OptionalId(sessionId, nameof(sessionId), 1_024);
        OwnerId = MemoryVectorGuard.OptionalId(ownerId, nameof(ownerId), 1_024);
        MemoryId = MemoryVectorGuard.OptionalId(memoryId, nameof(memoryId), 1_024);
        DetailsJson = detailsJson is null
            ? null
            : MemoryVectorGuard.Json(detailsJson, nameof(detailsJson), 65_536);
    }

    public string Code { get; }

    public MemoryVectorDiagnosticSeverity Severity { get; }

    public string Message { get; }

    public string? SessionId { get; }

    public string? OwnerId { get; }

    public string? MemoryId { get; }

    public string? DetailsJson { get; }
}

public interface IMemoryVectorDiagnosticSink
{
    ValueTask ReportAsync(MemoryVectorDiagnostic diagnostic, CancellationToken cancellationToken);
}

public sealed class NullMemoryVectorDiagnosticSink : IMemoryVectorDiagnosticSink
{
    public static NullMemoryVectorDiagnosticSink Instance { get; } = new();

    private NullMemoryVectorDiagnosticSink()
    {
    }

    public ValueTask ReportAsync(MemoryVectorDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        _ = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }
}

public enum VectorMemoryState
{
    Empty,
    Ready,
    Degraded,
    RebuildRequired,
}

public sealed class VectorMemoryStatus
{
    public VectorMemoryStatus(
        VectorMemoryState state,
        MemoryEmbeddingIdentity activeIdentity,
        int totalEntries,
        int readyEntries,
        int pendingEntries,
        int staleEntries,
        int orphanEntries)
    {
        if (!Enum.IsDefined(typeof(VectorMemoryState), state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (totalEntries < 0 || readyEntries < 0 || pendingEntries < 0 || staleEntries < 0 || orphanEntries < 0
            || readyEntries + pendingEntries + staleEntries != totalEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(totalEntries));
        }

        State = state;
        ActiveIdentity = activeIdentity ?? throw new ArgumentNullException(nameof(activeIdentity));
        TotalEntries = totalEntries;
        ReadyEntries = readyEntries;
        PendingEntries = pendingEntries;
        StaleEntries = staleEntries;
        OrphanEntries = orphanEntries;
    }

    public VectorMemoryState State { get; }

    public MemoryEmbeddingIdentity ActiveIdentity { get; }

    public int TotalEntries { get; }

    public int ReadyEntries { get; }

    public int PendingEntries { get; }

    public int StaleEntries { get; }

    public int OrphanEntries { get; }

    public bool RequiresRebuild => PendingEntries > 0 || StaleEntries > 0 || OrphanEntries > 0;
}

internal static class MemoryVectorGuard
{
    public static string Id(string value, string parameterName, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters || HasControl(value))
        {
            throw new ArgumentException("A bounded non-control identifier is required.", parameterName);
        }

        return value;
    }

    public static string? OptionalId(string? value, string parameterName, int maximumCharacters) =>
        value is null ? null : Id(value, parameterName, maximumCharacters);

    public static string Text(string value, string parameterName, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A bounded text value is required.", parameterName);
        }

        return value;
    }

    public static string Json(string value, string parameterName, int maximumCharacters)
    {
        value = Text(value, parameterName, maximumCharacters);
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
            return document.RootElement.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("A valid bounded JSON value is required.", parameterName, exception);
        }
    }

    public static void ValidateVector(IReadOnlyList<float> vector, int dimensions, string parameterName)
    {
        if (vector.Count != dimensions)
        {
            throw new ArgumentException("Embedding dimensions do not match the provider identity.", parameterName);
        }

        var norm = 0d;
        foreach (var value in vector)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException("Embedding vectors must contain finite values.", parameterName);
            }

            norm += value * value;
        }

        if (norm <= 0 || double.IsInfinity(norm) || double.IsNaN(norm))
        {
            throw new ArgumentException("Embedding vectors must have a finite non-zero norm.", parameterName);
        }
    }

    public static float[] Normalize(ReadOnlyMemory<float> source, MemoryEmbeddingIdentity identity, string parameterName)
    {
        var values = source.ToArray();
        ValidateVector(values, identity.Dimensions, parameterName);
        var norm = Math.Sqrt(values.Sum(value => (double)value * value));
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)(values[index] / norm);
        }

        return values;
    }

    private static bool HasControl(string value) => value.Any(character => char.IsControl(character));
}
