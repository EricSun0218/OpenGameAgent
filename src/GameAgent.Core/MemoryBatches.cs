using System.Collections.ObjectModel;

namespace GameAgent.Core;

public static class MemoryBatchLimits
{
    public const int MaxMutations = 1_024;
    public const int MaxAggregateContentUtf8Bytes = 8 * 1024 * 1024;
    public const int MaxInMemoryIdempotencyKeys = 100_000;
}

public static class MemoryBatchReasonCodes
{
    public const string Empty = "memory_batch_empty";
    public const string TooManyMutations =
        "memory_batch_mutation_count_exceeded";
    public const string NullMutation = "memory_batch_null_mutation";
    public const string DuplicateMemoryId = "memory_batch_duplicate_id";
    public const string AggregateContentBytesExceeded =
        "memory_batch_content_bytes_exceeded";
    public const string NotSupported = "memory_batch_not_supported";
    public const string IdempotencyConflict =
        "memory_batch_idempotency_conflict";
    public const string IdempotencyNotSupported =
        "memory_batch_idempotency_not_supported";
    public const string IdempotencyCapacityExceeded =
        "memory_batch_idempotency_capacity_exceeded";
}

public enum MemoryMutationKind
{
    Upsert = 0,
    Delete = 1
}

public sealed class MemoryMutation
{
    private MemoryMutation(
        MemoryMutationKind kind,
        string memoryId,
        MemoryRecord? record)
    {
        Kind = kind;
        MemoryId = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
        Record = record;
    }

    public MemoryMutationKind Kind { get; }

    public string MemoryId { get; }

    public MemoryRecord? Record { get; }

    public static MemoryMutation Upsert(MemoryRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new MemoryMutation(
            MemoryMutationKind.Upsert,
            record.MemoryId,
            record);
    }

    public static MemoryMutation Delete(string memoryId)
    {
        return new MemoryMutation(
            MemoryMutationKind.Delete,
            memoryId,
            record: null);
    }
}

public sealed class MemoryMutationResult
{
    public MemoryMutationResult(
        MemoryMutationKind kind,
        string memoryId,
        bool changed)
    {
        if (kind is not MemoryMutationKind.Upsert
            and not MemoryMutationKind.Delete)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        MemoryId = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
        Changed = changed;
    }

    public MemoryMutationKind Kind { get; }

    public string MemoryId { get; }

    public bool Changed { get; }
}

public sealed class MemoryBatchValidationException : ArgumentException
{
    internal MemoryBatchValidationException(
        string reasonCode,
        string message,
        int? mutationIndex = null,
        string? memoryId = null)
        : base($"{reasonCode}: {message}", "mutations")
    {
        ReasonCode = reasonCode;
        MutationIndex = mutationIndex;
        MemoryId = memoryId;
    }

    public string ReasonCode { get; }

    public int? MutationIndex { get; }

    public string? MemoryId { get; }
}

public sealed class MemoryBatchNotSupportedException : NotSupportedException
{
    public MemoryBatchNotSupportedException()
        : base(
            "The configured memory write store does not support atomic "
            + "batches.")
    {
    }

    public string ReasonCode => MemoryBatchReasonCodes.NotSupported;
}

public interface IAtomicMemoryBatchStore : IMemoryStore
{
    ValueTask<IReadOnlyList<MemoryMutationResult>> ApplyAtomicBatchAsync(
        IReadOnlyList<MemoryMutation> mutations,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Atomic memory store that durably deduplicates batches by commit identity.
/// Reusing an identity with the same payload digest is a no-op; reusing it
/// with a different digest must fail.
/// </summary>
public interface IIdempotentAtomicMemoryBatchStore : IAtomicMemoryBatchStore
{
    ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default);
}

public sealed class MemoryIdempotentBatchNotSupportedException
    : NotSupportedException
{
    public MemoryIdempotentBatchNotSupportedException()
        : base(
            "The configured memory write store does not support durable "
            + "idempotent atomic batches.")
    {
    }

    public string ReasonCode => MemoryBatchReasonCodes.IdempotencyNotSupported;
}

public sealed class MemoryBatchIdempotencyConflictException
    : InvalidOperationException
{
    public MemoryBatchIdempotencyConflictException(string commitId)
        : base(
            "A memory batch commit identity was reused with a different "
            + "payload digest.")
    {
        CommitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
    }

    public string CommitId { get; }

    public string ReasonCode => MemoryBatchReasonCodes.IdempotencyConflict;
}

internal static class MemoryBatchValidator
{
    public static MemoryMutation[] Snapshot(
        IReadOnlyList<MemoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations is null)
        {
            throw new ArgumentNullException(nameof(mutations));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // IReadOnlyList<T>.Count is supplied by the caller and is not a
        // trustworthy resource bound. Enumerate once into an owned snapshot,
        // stopping immediately at the first item beyond the hard cap.
        var snapshot = new List<MemoryMutation>();
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Count >= MemoryBatchLimits.MaxMutations)
            {
                throw new MemoryBatchValidationException(
                    MemoryBatchReasonCodes.TooManyMutations,
                    $"A memory batch exceeds "
                    + $"{MemoryBatchLimits.MaxMutations} mutations.");
            }

            snapshot.Add(mutation);
        }

        if (snapshot.Count == 0)
        {
            throw new MemoryBatchValidationException(
                MemoryBatchReasonCodes.Empty,
                "A memory batch requires at least one mutation.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var aggregateContentBytes = 0L;
        for (var index = 0; index < snapshot.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = snapshot[index];
            if (mutation is null)
            {
                throw new MemoryBatchValidationException(
                    MemoryBatchReasonCodes.NullMutation,
                    $"Memory mutation {index} is null.",
                    index);
            }

            if (!ids.Add(mutation.MemoryId))
            {
                throw new MemoryBatchValidationException(
                    MemoryBatchReasonCodes.DuplicateMemoryId,
                    $"Memory id '{mutation.MemoryId}' appears more than once "
                    + "in the same atomic batch.",
                    index,
                    mutation.MemoryId);
            }

            if (mutation.Kind == MemoryMutationKind.Upsert)
            {
                var record = mutation.Record
                             ?? throw new InvalidOperationException(
                                 "An upsert mutation requires a record.");
                var contentBytes = JsonValueInspector.ValidateAndMeasure(
                    record.Content,
                    new JsonValueLimits(maxUtf8Bytes: 131_072),
                    nameof(mutations));
                aggregateContentBytes += contentBytes;
                if (aggregateContentBytes
                    > MemoryBatchLimits.MaxAggregateContentUtf8Bytes)
                {
                    throw new MemoryBatchValidationException(
                        MemoryBatchReasonCodes.AggregateContentBytesExceeded,
                        $"Memory batch content exceeds "
                        + $"{MemoryBatchLimits.MaxAggregateContentUtf8Bytes} "
                        + "UTF-8 bytes.",
                        index,
                        mutation.MemoryId);
                }
            }
        }

        return snapshot.ToArray();
    }
}

public sealed partial class DeterministicMemoryStore
{
    private readonly Dictionary<string, string> _idempotentBatchDigests =
        new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        commitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        var payloadDigest =
            RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(snapshot);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_idempotentBatchDigests.TryGetValue(
                    commitId,
                    out var existingDigest))
            {
                if (!string.Equals(
                        existingDigest,
                        payloadDigest,
                        StringComparison.Ordinal))
                {
                    throw new MemoryBatchIdempotencyConflictException(commitId);
                }

                return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
                    new ReadOnlyCollection<MemoryMutationResult>(
                        snapshot
                            .Select(
                                item => new MemoryMutationResult(
                                    item.Kind,
                                    item.MemoryId,
                                    changed: false))
                            .ToArray()));
            }

            if (_idempotentBatchDigests.Count
                >= MemoryBatchLimits.MaxInMemoryIdempotencyKeys)
            {
                throw new RuntimeContentLimitException(
                    nameof(commitId),
                    MemoryBatchReasonCodes.IdempotencyCapacityExceeded,
                    "Memory batch idempotency capacity is exhausted.");
            }

            var result = ApplyAtomicBatchAsync(snapshot, cancellationToken)
                .GetAwaiter()
                .GetResult();
            _idempotentBatchDigests.Add(commitId, payloadDigest);
            return new ValueTask<IReadOnlyList<MemoryMutationResult>>(result);
        }
    }

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyAtomicBatchAsync(
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        var prepared = new IndexedRecord?[snapshot.Length];
        for (var index = 0; index < snapshot.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = snapshot[index];
            if (mutation.Kind == MemoryMutationKind.Upsert)
            {
                var record = mutation.Record
                             ?? throw new InvalidOperationException(
                                 "An upsert mutation requires a record.");
                prepared[index] = new IndexedRecord(
                    record,
                    Tokenize(record.Content));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staged = new Dictionary<string, IndexedRecord>(
                _records,
                StringComparer.Ordinal);
            var results = new MemoryMutationResult[snapshot.Length];
            for (var index = 0; index < snapshot.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutation = snapshot[index];
                switch (mutation.Kind)
                {
                    case MemoryMutationKind.Upsert:
                        staged[mutation.MemoryId] = prepared[index]
                            ?? throw new InvalidOperationException(
                                "An upsert mutation was not prepared.");
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            changed: true);
                        break;
                    case MemoryMutationKind.Delete:
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            staged.Remove(mutation.MemoryId));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown memory mutation kind "
                            + $"'{mutation.Kind}'.");
                }
            }

            if (staged.Count > _capacity)
            {
                throw new RuntimeContentLimitException(
                    nameof(mutations),
                    "memory_capacity_exceeded",
                    $"Memory capacity exceeds {_capacity} records.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _records = staged;
            return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
                new ReadOnlyCollection<MemoryMutationResult>(results));
        }
    }
}
