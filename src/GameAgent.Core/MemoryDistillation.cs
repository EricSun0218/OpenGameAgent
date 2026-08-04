using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Core;

public static class DistilledMemoryStates
{
    public const string Active = "active";
    public const string Contradicted = "contradicted";
    public const string Polluted = "polluted";
    public const string Retired = "retired";

    internal static bool IsKnown(string value) =>
        value == Active || value == Contradicted || value == Polluted || value == Retired;
}

public sealed class MemoryEvidenceCitation
{
    public string MemoryId { get; set; } = string.Empty;

    public string ContentDigest { get; set; } = string.Empty;

    public string? SourceEventId { get; set; }
}

public sealed class DistilledMemoryRecord
{
    public string DistillationId { get; set; } = string.Empty;

    public string MemoryId { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public JsonElement Content { get; set; }

    public IReadOnlyList<MemoryEvidenceCitation> Citations { get; set; } =
        Array.Empty<MemoryEvidenceCitation>();

    public int Salience { get; set; }

    public int Confidence { get; set; }

    public string State { get; set; } = DistilledMemoryStates.Active;

    public GameTimePoint CreatedAt { get; set; } = null!;

    public GameTimePoint? LastUsedAt { get; set; }

    public GameTimePoint? RetainUntil { get; set; }

    public long UsageCount { get; set; }

    public long Revision { get; set; }
}

public sealed class MemoryDistillationRequest
{
    public string DistillationId { get; set; } = string.Empty;

    public string TargetMemoryId { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public IReadOnlyList<MemoryRecord> Sources { get; set; } = Array.Empty<MemoryRecord>();

    public GameTimePoint GameTime { get; set; } = null!;

    public JsonElement Instructions { get; set; }
}

public interface IMemoryDistiller
{
    string DistillerId { get; }

    ValueTask<DistilledMemoryRecord> DistillAsync(
        MemoryDistillationRequest request,
        CancellationToken cancellationToken);
}

public interface IMemoryDistillationStore
{
    ValueTask<DistilledMemoryRecord?> TryGetAsync(
        string distillationId,
        CancellationToken cancellationToken);

    ValueTask PutAsync(
        DistilledMemoryRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DistilledMemoryRecord>> ListAsync(
        string? scope,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DistilledMemoryRecord>> ListDueAsync(
        string? scope,
        GameTimePoint now,
        int maximumCount,
        CancellationToken cancellationToken);
}

public sealed class MemoryDistillationException : Exception
{
    public MemoryDistillationException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Coordinates bounded, cited, derived memory. Distilled records are hints for
/// recall only: they do not prove a world fact, authorize a tool, or settle a
/// game-side effect. Retention is evaluated exclusively in host-provided game
/// time, so a paused world does not age while wall-clock time passes.
/// </summary>
public sealed class MemoryDistillationCoordinator
{
    private const int MaxCommitRetries = 32;

    private readonly IMemoryDistillationStore _store;
    private readonly IMemoryDistiller? _distiller;
    private readonly JsonValueLimits _limits;

    public MemoryDistillationCoordinator(
        IMemoryDistillationStore store,
        IMemoryDistiller? distiller = null,
        int maxContentUtf8Bytes = 131_072)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _distiller = distiller;
        if (maxContentUtf8Bytes is < 1_024 or > CanonicalJsonDigest.MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContentUtf8Bytes));
        }

        _limits = new JsonValueLimits(
            maxContentUtf8Bytes,
            maxDepth: 64,
            maxNodes: 65_536,
            maxStringUtf8Bytes: maxContentUtf8Bytes,
            maxContainerItems: 32_768);
    }

    public async ValueTask<DistilledMemoryRecord> DistillAsync(
        MemoryDistillationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_distiller is null)
        {
            throw new InvalidOperationException("No memory distiller is configured.");
        }

        var admitted = SnapshotRequest(request);
        var candidate = await _distiller.DistillAsync(admitted, cancellationToken)
            .ConfigureAwait(false);
        var sourceDigests = admitted.Sources.ToDictionary(
            source => source.MemoryId,
            source => CanonicalJsonDigest.ComputeSha256(source.Content),
            StringComparer.Ordinal);
        var snapshot = Validate(candidate);
        if (snapshot.DistillationId != admitted.DistillationId
            || snapshot.MemoryId != admitted.TargetMemoryId
            || snapshot.Scope != admitted.Scope
            || !snapshot.CreatedAt.IsComparableTo(admitted.GameTime)
            || snapshot.CreatedAt.CompareTo(admitted.GameTime) != 0
            || snapshot.State != DistilledMemoryStates.Active
            || snapshot.LastUsedAt is not null
            || snapshot.UsageCount != 0
            || snapshot.Revision != 0
            || snapshot.Citations.Count == 0
            || snapshot.Citations.Any(citation =>
                !sourceDigests.TryGetValue(citation.MemoryId, out var digest)
                || digest != citation.ContentDigest))
        {
            throw new MemoryDistillationException(
                "memory_distillation_evidence_invalid",
                "The distiller result is not bound to the admitted source memories.");
        }

        var candidateDigest = Digest(snapshot);
        for (var attempt = 0; attempt < MaxCommitRetries; attempt++)
        {
            var existing = await _store.TryGetAsync(
                    snapshot.DistillationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && Digest(existing) == candidateDigest)
            {
                return Snapshot(existing);
            }

            if (existing is not null)
            {
                throw new MemoryDistillationException(
                    "memory_distillation_identity_conflict",
                    "The distillation ID is bound to different derived content.");
            }

            snapshot.Revision = 1;
            try
            {
                await _store.PutAsync(snapshot, null, cancellationToken).ConfigureAwait(false);
                return Snapshot(snapshot);
            }
            catch (MemoryDistillationException exception)
                when (exception.ReasonCode == "memory_distillation_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw Contention();
    }

    public async ValueTask<DistilledMemoryRecord> RecordRecallAsync(
        string distillationId,
        GameTimePoint usedAt,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxCommitRetries; attempt++)
        {
            var current = await RequireAsync(distillationId, cancellationToken)
                .ConfigureAwait(false);
            if (!current.CreatedAt.IsComparableTo(usedAt)
                || current.LastUsedAt is not null
                   && (!current.LastUsedAt.IsComparableTo(usedAt)
                       || current.LastUsedAt.CompareTo(usedAt) > 0))
            {
                throw new MemoryDistillationException(
                    "memory_distillation_time_invalid",
                    "Memory usage must advance on the same game-time coordinate.");
            }

            current.LastUsedAt = Clone(usedAt);
            current.UsageCount = checked(current.UsageCount + 1);
            var expected = current.Revision;
            current.Revision++;
            try
            {
                await _store.PutAsync(current, expected, cancellationToken)
                    .ConfigureAwait(false);
                return Snapshot(current);
            }
            catch (MemoryDistillationException exception)
                when (exception.ReasonCode == "memory_distillation_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw Contention();
    }

    public async ValueTask<DistilledMemoryRecord> SetStateAsync(
        string distillationId,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (!DistilledMemoryStates.IsKnown(state))
        {
            throw new ArgumentException("The distilled memory state is invalid.", nameof(state));
        }

        for (var attempt = 0; attempt < MaxCommitRetries; attempt++)
        {
            var current = await RequireAsync(distillationId, cancellationToken)
                .ConfigureAwait(false);
            if (current.State == state)
            {
                return current;
            }

            var expected = current.Revision;
            current.State = state;
            current.Revision++;
            try
            {
                await _store.PutAsync(current, expected, cancellationToken)
                    .ConfigureAwait(false);
                return Snapshot(current);
            }
            catch (MemoryDistillationException exception)
                when (exception.ReasonCode == "memory_distillation_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw Contention();
    }

    public async ValueTask<IReadOnlyList<DistilledMemoryRecord>> RetireDueAsync(
        string? scope,
        GameTimePoint now,
        int maximumCount = 256,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var records = await _store.ListDueAsync(
                scope,
                now ?? throw new ArgumentNullException(nameof(now)),
                maximumCount,
                cancellationToken)
            .ConfigureAwait(false);
        var retired = new List<DistilledMemoryRecord>();
        foreach (var record in records)
        {
            for (var attempt = 0; attempt < MaxCommitRetries; attempt++)
            {
                var current = attempt == 0
                    ? record
                    : await RequireAsync(record.DistillationId, cancellationToken)
                        .ConfigureAwait(false);
                if (current.State != DistilledMemoryStates.Active
                    || current.RetainUntil is null
                    || !current.RetainUntil.IsComparableTo(now)
                    || current.RetainUntil.CompareTo(now) > 0)
                {
                    break;
                }

                var expected = current.Revision;
                current.State = DistilledMemoryStates.Retired;
                current.Revision++;
                try
                {
                    await _store.PutAsync(current, expected, cancellationToken)
                        .ConfigureAwait(false);
                    retired.Add(Snapshot(current));
                    break;
                }
                catch (MemoryDistillationException exception)
                    when (exception.ReasonCode == "memory_distillation_revision_conflict")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attempt == MaxCommitRetries - 1)
                    {
                        throw Contention();
                    }
                }
            }
        }

        return new ReadOnlyCollection<DistilledMemoryRecord>(retired);
    }

    private async ValueTask<DistilledMemoryRecord> RequireAsync(
        string id,
        CancellationToken cancellationToken) =>
        await _store.TryGetAsync(
                RuntimeGuard.RequiredUtf8(id, 128, nameof(id)),
                cancellationToken)
            .ConfigureAwait(false)
        ?? throw new MemoryDistillationException(
            "memory_distillation_missing",
            "The distilled memory record does not exist.");

    private MemoryDistillationRequest SnapshotRequest(MemoryDistillationRequest request)
    {
        if (request is null || request.GameTime is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var sources = request.Sources?.Take(257).ToArray()
            ?? throw new ArgumentNullException(nameof(request.Sources));
        if (sources.Length is < 1 or > 256
            || sources.Any(source => source is null)
            || sources.Select(source => source.MemoryId).Distinct(StringComparer.Ordinal).Count()
               != sources.Length)
        {
            throw new MemoryDistillationException(
                "memory_distillation_sources_invalid",
                "Distillation requires one to 256 unique source memories.");
        }

        JsonValueInspector.ValidateAndMeasure(request.Instructions, _limits, "instructions");
        return new MemoryDistillationRequest
        {
            DistillationId = RuntimeGuard.RequiredUtf8(
                request.DistillationId, 128, nameof(request.DistillationId)),
            TargetMemoryId = RuntimeGuard.RequiredUtf8(
                request.TargetMemoryId, 128, nameof(request.TargetMemoryId)),
            Scope = RuntimeGuard.RequiredUtf8(request.Scope, 256, nameof(request.Scope)),
            Sources = new ReadOnlyCollection<MemoryRecord>(sources),
            GameTime = Clone(request.GameTime),
            Instructions = request.Instructions.Clone()
        };
    }

    internal DistilledMemoryRecord Validate(DistilledMemoryRecord record)
    {
        if (record is null || record.CreatedAt is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        JsonValueInspector.ValidateAndMeasure(record.Content, _limits, "content");
        if (record.Salience is < 0 or > 100
            || record.Confidence is < 0 or > 100
            || record.UsageCount < 0
            || record.Revision < 0
            || !DistilledMemoryStates.IsKnown(record.State))
        {
            throw new MemoryDistillationException(
                "memory_distillation_record_invalid",
                "The distilled memory metadata is invalid.");
        }

        var citations = (record.Citations ?? Array.Empty<MemoryEvidenceCitation>())
            .Take(257)
            .Select(Snapshot)
            .OrderBy(item => item.MemoryId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceEventId, StringComparer.Ordinal)
            .ToArray();
        if (citations.Length > 256
            || citations.Select(item => item.MemoryId)
                .Distinct(StringComparer.Ordinal).Count() != citations.Length)
        {
            throw new MemoryDistillationException(
                "memory_distillation_citations_invalid",
                "Memory citations are duplicated or exceed their limit.");
        }

        if (record.LastUsedAt is not null
            && (!record.CreatedAt.IsComparableTo(record.LastUsedAt)
                || record.CreatedAt.CompareTo(record.LastUsedAt) > 0)
            || record.RetainUntil is not null
            && (!record.CreatedAt.IsComparableTo(record.RetainUntil)
                || record.CreatedAt.CompareTo(record.RetainUntil) > 0))
        {
            throw new MemoryDistillationException(
                "memory_distillation_time_invalid",
                "Distilled memory time coordinates are incompatible or reversed.");
        }

        return new DistilledMemoryRecord
        {
            DistillationId = RuntimeGuard.RequiredUtf8(
                record.DistillationId, 128, nameof(record.DistillationId)),
            MemoryId = RuntimeGuard.RequiredUtf8(record.MemoryId, 128, nameof(record.MemoryId)),
            Scope = RuntimeGuard.RequiredUtf8(record.Scope, 256, nameof(record.Scope)),
            Content = record.Content.Clone(),
            Citations = new ReadOnlyCollection<MemoryEvidenceCitation>(citations),
            Salience = record.Salience,
            Confidence = record.Confidence,
            State = record.State,
            CreatedAt = Clone(record.CreatedAt),
            LastUsedAt = record.LastUsedAt is null ? null : Clone(record.LastUsedAt),
            RetainUntil = record.RetainUntil is null ? null : Clone(record.RetainUntil),
            UsageCount = record.UsageCount,
            Revision = record.Revision
        };
    }

    internal static DistilledMemoryRecord Snapshot(DistilledMemoryRecord record) =>
        new()
        {
            DistillationId = record.DistillationId,
            MemoryId = record.MemoryId,
            Scope = record.Scope,
            Content = record.Content.Clone(),
            Citations = new ReadOnlyCollection<MemoryEvidenceCitation>(
                record.Citations.Select(Snapshot).ToArray()),
            Salience = record.Salience,
            Confidence = record.Confidence,
            State = record.State,
            CreatedAt = Clone(record.CreatedAt),
            LastUsedAt = record.LastUsedAt is null ? null : Clone(record.LastUsedAt),
            RetainUntil = record.RetainUntil is null ? null : Clone(record.RetainUntil),
            UsageCount = record.UsageCount,
            Revision = record.Revision
        };

    private static MemoryEvidenceCitation Snapshot(MemoryEvidenceCitation citation)
    {
        if (citation is null)
        {
            throw new MemoryDistillationException(
                "memory_distillation_citation_invalid",
                "A memory citation cannot be null.");
        }

        if (!CanonicalJsonDigest.IsSha256(citation.ContentDigest))
        {
            throw new MemoryDistillationException(
                "memory_distillation_citation_digest_invalid",
                "A memory citation requires a lowercase SHA-256 digest.");
        }

        return new MemoryEvidenceCitation
        {
            MemoryId = RuntimeGuard.RequiredUtf8(
                citation.MemoryId, 128, nameof(citation.MemoryId)),
            ContentDigest = citation.ContentDigest,
            SourceEventId = citation.SourceEventId is null
                ? null
                : RuntimeGuard.RequiredUtf8(
                    citation.SourceEventId, 128, nameof(citation.SourceEventId))
        };
    }

    private static GameTimePoint Clone(GameTimePoint point) =>
        new(point.ClockId, point.TimelineId, point.Epoch, point.Tick);

    private static string Digest(DistilledMemoryRecord record) =>
        CanonicalJsonDigest.ComputeSha256(JsonArrayBuilder.Object(
            ("id", JsonArrayBuilder.String(record.DistillationId)),
            ("memoryId", JsonArrayBuilder.String(record.MemoryId)),
            ("scope", JsonArrayBuilder.String(record.Scope)),
            ("content", record.Content.Clone()),
            ("citations", JsonArrayBuilder.Array(record.Citations.Select(citation =>
                JsonArrayBuilder.Object(
                    ("memoryId", JsonArrayBuilder.String(citation.MemoryId)),
                    ("contentDigest", JsonArrayBuilder.String(citation.ContentDigest)),
                    ("sourceEventId", citation.SourceEventId is null
                        ? JsonArrayBuilder.Null()
                        : JsonArrayBuilder.String(citation.SourceEventId)))))),
            ("salience", JsonArrayBuilder.Number(record.Salience)),
            ("confidence", JsonArrayBuilder.Number(record.Confidence)),
            ("createdAt", TimeJson(record.CreatedAt)),
            ("retainUntil", record.RetainUntil is null
                ? JsonArrayBuilder.Null()
                : TimeJson(record.RetainUntil))));

    private static JsonElement TimeJson(GameTimePoint point) =>
        JsonArrayBuilder.Object(
            ("clockId", JsonArrayBuilder.String(point.ClockId)),
            ("timelineId", JsonArrayBuilder.String(point.TimelineId)),
            ("epoch", JsonArrayBuilder.Number(point.Epoch)),
            ("tick", JsonArrayBuilder.Number(point.Tick)));

    private static MemoryDistillationException Contention() =>
        new(
            "memory_distillation_contention",
            "The distilled memory record changed too frequently to commit safely.");
}

public sealed class InMemoryMemoryDistillationStore : IMemoryDistillationStore
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, DistilledMemoryRecord> _records =
        new(StringComparer.Ordinal);

    public InMemoryMemoryDistillationStore(int capacity = 65_536)
    {
        if (capacity is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask<DistilledMemoryRecord?> TryGetAsync(
        string distillationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.TryGetValue(distillationId, out var record);
        return new ValueTask<DistilledMemoryRecord?>(
            record is null ? null : MemoryDistillationCoordinator.Snapshot(record));
    }

    public ValueTask PutAsync(
        DistilledMemoryRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = MemoryDistillationCoordinator.Snapshot(record);
        while (true)
        {
            if (_records.TryGetValue(snapshot.DistillationId, out var current))
            {
                if (current.Revision != expectedRevision)
                {
                    throw Conflict();
                }

                if (_records.TryUpdate(snapshot.DistillationId, snapshot, current))
                {
                    return default;
                }

                continue;
            }

            if (expectedRevision is not null)
            {
                throw Conflict();
            }

            if (_records.Count >= _capacity)
            {
                throw new MemoryDistillationException(
                    "memory_distillation_capacity",
                    "The distilled memory store is full.");
            }

            if (_records.TryAdd(snapshot.DistillationId, snapshot))
            {
                return default;
            }
        }
    }

    public ValueTask<IReadOnlyList<DistilledMemoryRecord>> ListAsync(
        string? scope,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        IReadOnlyList<DistilledMemoryRecord> records =
            new ReadOnlyCollection<DistilledMemoryRecord>(_records.Values
                .Where(item => scope is null || item.Scope == scope)
                .OrderByDescending(item => item.Salience)
                .ThenByDescending(item => item.UsageCount)
                .ThenBy(item => item.DistillationId, StringComparer.Ordinal)
                .Take(maximumCount)
                .Select(MemoryDistillationCoordinator.Snapshot)
                .ToArray());
        return new ValueTask<IReadOnlyList<DistilledMemoryRecord>>(records);
    }

    public ValueTask<IReadOnlyList<DistilledMemoryRecord>> ListDueAsync(
        string? scope,
        GameTimePoint now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (now is null)
        {
            throw new ArgumentNullException(nameof(now));
        }

        if (maximumCount is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        IReadOnlyList<DistilledMemoryRecord> records =
            new ReadOnlyCollection<DistilledMemoryRecord>(_records.Values
                .Where(item =>
                    (scope is null || item.Scope == scope)
                    && item.State == DistilledMemoryStates.Active
                    && item.RetainUntil is not null
                    && item.RetainUntil.IsComparableTo(now)
                    && item.RetainUntil.CompareTo(now) <= 0)
                .OrderBy(item => item.RetainUntil!.Tick)
                .ThenBy(item => item.DistillationId, StringComparer.Ordinal)
                .Take(maximumCount)
                .Select(MemoryDistillationCoordinator.Snapshot)
                .ToArray());
        return new ValueTask<IReadOnlyList<DistilledMemoryRecord>>(records);
    }

    private static MemoryDistillationException Conflict() =>
        new(
            "memory_distillation_revision_conflict",
            "The distilled memory record revision changed.");
}
