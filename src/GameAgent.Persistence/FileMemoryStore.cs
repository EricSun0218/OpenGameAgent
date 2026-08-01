using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Persistence;

/// <summary>
/// A crash-tolerant, deterministic local memory store backed by an append-only
/// file. Mutations are committed as checksummed frames and recovered on the
/// next process start.
/// </summary>
public sealed class FileMemoryStore :
    IMemoryStore,
    IRuntimeAuthoritativeMemoryBatchStore,
    ILegacyRuntimeMemoryBatchReplayStore,
    IMemoryIndexDiagnosticsProvider,
    IDisposable,
    IAsyncDisposable
{
    private const uint FrameMagic = 0x314D4147;
    private const uint CommitMagic = 0x54494D43;
    private const int HeaderSize = 12;
    private const int FooterSize = 4;
    private const int LegacyFormatVersion = 1;
    private const int FormatVersion = 2;
    private const int IndexRebuildBatchSize = 32;
    private const string UpsertOperation = "upsert";
    private const string DeleteOperation = "delete";
    private const string BatchOperation = "batch";

    private readonly string _path;
    private readonly ExclusiveFileWriterLease _writerLease;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAtomicMemoryBatchStore _index;
    private Dictionary<string, MemoryRecord> _records =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _committedBatchDigests =
        new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly int _maxFramePayloadBytes;
    private readonly long _maxLogBytes;
    private readonly long _maxMutationFrames;
    private readonly bool _flushToDiskOnMutation;
    private readonly IJournalFaultInjector? _faultInjector;
    private readonly FileMemorySearchMode _searchMode;
    private readonly string _indexIdentity;
    private readonly string _indexVersion;
    private readonly string _tokenizerIdentity;
    private readonly string _tokenizerVersion;
    private long _revision;
    private int _indexStatus = (int)MemoryIndexStatus.Rebuilding;
    private bool _faulted;
    private bool _disposed;

    public int RuntimeMutationContractVersion =>
        RuntimeMemoryMutationContract.CurrentVersion;

    public FileMemoryStore(
        string path,
        FileMemoryStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A memory-store path is required.",
                nameof(path));
        }

        options ??= new FileMemoryStoreOptions();
        if (options.Capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Capacity must be positive.");
        }

        if (options.MaxFramePayloadBytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFramePayloadBytes must be positive.");
        }

        if (options.MaxLogBytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxLogBytes must be positive.");
        }

        if (options.MaxMutationFrames < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxMutationFrames must be positive.");
        }

        if (options.SearchMode is not FileMemorySearchMode.DeterministicLexical
            and not FileMemorySearchMode.Bm25)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SearchMode is not supported.");
        }

        _path = System.IO.Path.GetFullPath(path);
        _capacity = options.Capacity;
        _maxFramePayloadBytes = options.MaxFramePayloadBytes;
        _maxLogBytes = options.MaxLogBytes;
        _maxMutationFrames = options.MaxMutationFrames;
        _flushToDiskOnMutation = options.FlushToDiskOnMutation;
        _faultInjector = options.FaultInjector;
        _searchMode = options.SearchMode;
        if (_searchMode == FileMemorySearchMode.Bm25)
        {
            var index = new Bm25MemoryStore(
                options.ProviderId,
                options.Capacity,
                options.Bm25Options);
            _index = index;
            var diagnostics = index.IndexDiagnostics;
            _indexIdentity = diagnostics.Identity;
            _indexVersion = diagnostics.Version;
            _tokenizerIdentity = diagnostics.TokenizerIdentity;
            _tokenizerVersion = diagnostics.TokenizerVersion;
        }
        else
        {
            _index = new DeterministicMemoryStore(
                options.ProviderId,
                options.Capacity);
            _indexIdentity = "deterministic-lexical-memory";
            _indexVersion = "1";
            _tokenizerIdentity = "invariant-json-lexical";
            _tokenizerVersion = "1";
        }

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writerLease = ExclusiveFileWriterLease.Acquire(_path);
        _stream = _writerLease.Stream;

        try
        {
            EnsureExistingLogCapacity();
            Recover();
            if (_records.Count > _capacity)
            {
                throw new MemoryStoreCapacityExceededException(
                    nameof(FileMemoryStoreOptions.Capacity),
                    _capacity,
                    _records.Count);
            }

            RebuildIndex();

            Volatile.Write(
                ref _indexStatus,
                (int)MemoryIndexStatus.Ready);
        }
        catch
        {
            Volatile.Write(
                ref _indexStatus,
                (int)MemoryIndexStatus.Faulted);
            _writerLease.Dispose();
            throw;
        }
    }

    public string ProviderId => _index.ProviderId;

    public string Path => _path;

    public long Revision => Interlocked.Read(ref _revision);

    public FileMemorySearchMode SearchMode => _searchMode;

    public MemoryIndexDiagnostics IndexDiagnostics =>
        new(
            _indexIdentity,
            _indexVersion,
            _tokenizerIdentity,
            _tokenizerVersion,
            Revision,
            (MemoryIndexStatus)Volatile.Read(ref _indexStatus));

    public async ValueTask UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken)
    {
        _ = await UpsertAtomicAsync(
                record,
                expectedRevision: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<MemoryStoreMutationResult> UpsertAtomicAsync(
        MemoryRecord record,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        ValidateExpectedRevision(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            EnsureExpectedRevision(expectedRevision);
            _records.TryGetValue(record.MemoryId, out var existing);
            MemoryMutationAdmission.EnsureCanApplyUnconditionalUpsert(
                MemoryMutation.Upsert(record),
                existing);
            if (!_records.ContainsKey(record.MemoryId)
                && _records.Count >= _capacity)
            {
                throw new RuntimeContentLimitException(
                    nameof(record),
                    "memory_capacity_exceeded",
                    $"Memory capacity exceeds {_capacity} records.");
            }

            if (_index is IPreflightMemoryIndex preflight)
            {
                preflight.ValidateUpsert(record, cancellationToken);
            }

            var nextRevision = checked(_revision + 1);
            var frame = new MemoryFrameRecord
            {
                FormatVersion = FormatVersion,
                Revision = nextRevision,
                Operation = UpsertOperation,
                Record = PersistedMemoryRecord.FromMemoryRecord(record)
            };
            await WriteRecordAsync(frame, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                _records[record.MemoryId] = record;
                await _index.UpsertAsync(record, CancellationToken.None)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _revision, nextRevision);
            }
            catch
            {
                _faulted = true;
                Volatile.Write(
                    ref _indexStatus,
                    (int)MemoryIndexStatus.Faulted);
                throw;
            }

            return new MemoryStoreMutationResult(nextRevision, changed: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        string memoryId,
        CancellationToken cancellationToken)
    {
        var result = await DeleteAtomicAsync(
                memoryId,
                expectedRevision: null,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Changed;
    }

    public async ValueTask<MemoryStoreMutationResult> DeleteAtomicAsync(
        string memoryId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMemoryId(memoryId);
        ValidateExpectedRevision(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            EnsureExpectedRevision(expectedRevision);
            if (!_records.ContainsKey(memoryId))
            {
                return new MemoryStoreMutationResult(
                    _revision,
                    changed: false);
            }

            var nextRevision = checked(_revision + 1);
            var frame = new MemoryFrameRecord
            {
                FormatVersion = FormatVersion,
                Revision = nextRevision,
                Operation = DeleteOperation,
                MemoryId = memoryId
            };
            await WriteRecordAsync(frame, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (!_records.Remove(memoryId)
                    || !await _index.DeleteAsync(
                            memoryId,
                            CancellationToken.None)
                        .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The committed memory deletion could not be applied "
                        + "to the in-process index.");
                }

                Interlocked.Exchange(ref _revision, nextRevision);
            }
            catch
            {
                _faulted = true;
                Volatile.Write(
                    ref _indexStatus,
                    (int)MemoryIndexStatus.Faulted);
                throw;
            }

            return new MemoryStoreMutationResult(nextRevision, changed: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyAtomicBatchAsync(
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        var result = await ApplyAtomicBatchWithRevisionAsync(
                mutations,
                expectedRevision: null,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Mutations;
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return await ApplyIdempotentAtomicBatchCoreAsync(
                commitId,
                mutations,
                allowLegacyReplay: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyLegacyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return await ApplyIdempotentAtomicBatchCoreAsync(
                commitId,
                mutations,
                allowLegacyReplay: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchCoreAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            bool allowLegacyReplay,
            CancellationToken cancellationToken)
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_committedBatchDigests.TryGetValue(
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

                return new ReadOnlyCollection<MemoryMutationResult>(
                    snapshot
                        .Select(
                            item => new MemoryMutationResult(
                                item.Kind,
                                item.MemoryId,
                                changed: false))
                        .ToArray());
            }

            var staged = new Dictionary<string, MemoryRecord>(
                _records,
                StringComparer.Ordinal);
            var results = new MemoryMutationResult[snapshot.Length];
            var changed = false;
            for (var index = 0; index < snapshot.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutation = snapshot[index];
                staged.TryGetValue(mutation.MemoryId, out var existing);
                if (allowLegacyReplay)
                {
                    MemoryMutationAdmission.EnsureCanReplayLegacy(mutation);
                }
                else
                {
                    MemoryMutationAdmission.EnsureCanApply(
                        mutation,
                        existing);
                }
                switch (mutation.Kind)
                {
                    case MemoryMutationKind.Upsert:
                        staged[mutation.MemoryId] = mutation.Record
                            ?? throw new InvalidOperationException(
                                "An upsert mutation requires a record.");
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            changed: true);
                        changed = true;
                        break;
                    case MemoryMutationKind.Delete:
                        var deleted = staged.Remove(mutation.MemoryId);
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            deleted);
                        changed |= deleted;
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

            if (allowLegacyReplay
                && _index is not ILegacyRuntimeMemoryBatchReplayStore)
            {
                throw new MemoryLegacyReplayNotSupportedException();
            }

            if (changed
                && !allowLegacyReplay
                && _index is IPreflightMemoryIndex preflight)
            {
                preflight.ValidateAtomicBatch(
                    snapshot,
                    cancellationToken);
            }

            var nextRevision = checked(_revision + 1);
            await WriteBatchRecordAsync(
                    snapshot,
                    nextRevision,
                    cancellationToken,
                    commitId,
                    payloadDigest,
                    allowLegacyReplay)
                .ConfigureAwait(false);

            try
            {
                if (changed)
                {
                    if (allowLegacyReplay)
                    {
                        _ = await ((ILegacyRuntimeMemoryBatchReplayStore)
                                _index)
                            .ApplyLegacyIdempotentAtomicBatchAsync(
                                commitId,
                                snapshot,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _ = await _index.ApplyAtomicBatchAsync(
                                snapshot,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    _records = staged;
                }

                _committedBatchDigests.Add(commitId, payloadDigest);
                Interlocked.Exchange(ref _revision, nextRevision);
            }
            catch
            {
                _faulted = true;
                Volatile.Write(
                    ref _indexStatus,
                    (int)MemoryIndexStatus.Faulted);
                throw;
            }

            return new ReadOnlyCollection<MemoryMutationResult>(results);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MemoryStoreBatchMutationResult>
        ApplyAtomicBatchWithRevisionAsync(
            IReadOnlyList<MemoryMutation> mutations,
            long? expectedRevision = null,
            CancellationToken cancellationToken = default)
    {
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        ValidateExpectedRevision(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            EnsureExpectedRevision(expectedRevision);
            cancellationToken.ThrowIfCancellationRequested();

            var staged = new Dictionary<string, MemoryRecord>(
                _records,
                StringComparer.Ordinal);
            var results = new MemoryMutationResult[snapshot.Length];
            var changed = false;
            for (var index = 0; index < snapshot.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutation = snapshot[index];
                staged.TryGetValue(mutation.MemoryId, out var existing);
                MemoryMutationAdmission.EnsureCanApply(mutation, existing);
                switch (mutation.Kind)
                {
                    case MemoryMutationKind.Upsert:
                        staged[mutation.MemoryId] = mutation.Record
                            ?? throw new InvalidOperationException(
                                "An upsert mutation requires a record.");
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            changed: true);
                        changed = true;
                        break;
                    case MemoryMutationKind.Delete:
                        var deleted = staged.Remove(mutation.MemoryId);
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            deleted);
                        changed |= deleted;
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
            if (!changed)
            {
                return new MemoryStoreBatchMutationResult(
                    _revision,
                    results);
            }

            if (_index is IPreflightMemoryIndex preflight)
            {
                preflight.ValidateAtomicBatch(
                    snapshot,
                    cancellationToken);
            }

            var nextRevision = checked(_revision + 1);
            await WriteBatchRecordAsync(
                    snapshot,
                    nextRevision,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                _ = await _index.ApplyAtomicBatchAsync(
                        snapshot,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                _records = staged;
                Interlocked.Exchange(ref _revision, nextRevision);
            }
            catch
            {
                _faulted = true;
                Volatile.Write(
                    ref _indexStatus,
                    (int)MemoryIndexStatus.Faulted);
                throw;
            }

            return new MemoryStoreBatchMutationResult(
                nextRevision,
                results);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var results = await _index.SearchAsync(
                    query,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ReadOnlyCollection<MemorySearchResult>(
                results.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<long> GetRevisionAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            return _revision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Volatile.Write(
                ref _indexStatus,
                (int)MemoryIndexStatus.Disposed);
            _writerLease.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Volatile.Write(
                ref _indexStatus,
                (int)MemoryIndexStatus.Disposed);
            _writerLease.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask WriteRecordAsync(
        MemoryFrameRecord record,
        CancellationToken cancellationToken)
    {
        using var payload = BoundedJsonPayload.Serialize(
            record,
            PersistenceJsonContext.Default.MemoryFrameRecord,
            _maxFramePayloadBytes,
            attempted => new MemoryStoreCapacityExceededException(
                nameof(FileMemoryStoreOptions.MaxFramePayloadBytes),
                _maxFramePayloadBytes,
                attempted));
        var frame = BuildFrame(payload.WrittenSpan);
        EnsureMutationCapacity(record.Revision, frame.Length);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteFrameAsync(frame).ConfigureAwait(false);
    }

    private void RebuildIndex()
    {
        var ordered = _records.Values.OrderBy(
            value => value.MemoryId,
            StringComparer.Ordinal);
        if (_searchMode != FileMemorySearchMode.Bm25)
        {
            foreach (var record in ordered)
            {
                _index.UpsertAsync(record, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            return;
        }

        var batch = new List<MemoryMutation>(IndexRebuildBatchSize);
        foreach (var record in ordered)
        {
            batch.Add(MemoryMutation.Upsert(record));
            if (batch.Count < IndexRebuildBatchSize)
            {
                continue;
            }

            _index.ApplyAtomicBatchAsync(
                    batch,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            _index.ApplyAtomicBatchAsync(
                    batch,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }

    private async ValueTask WriteBatchRecordAsync(
        IReadOnlyList<MemoryMutation> mutations,
        long revision,
        CancellationToken cancellationToken,
        string? commitId = null,
        string? payloadDigest = null,
        bool allowLegacyReplay = false)
    {
        using var payload = BoundedJsonPayload.Write(
            _maxFramePayloadBytes,
            attempted => new MemoryStoreCapacityExceededException(
                nameof(FileMemoryStoreOptions.MaxFramePayloadBytes),
                _maxFramePayloadBytes,
                attempted),
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", FormatVersion);
                writer.WriteNumber("revision", revision);
                writer.WriteString("operation", BatchOperation);
                if (commitId is not null)
                {
                    writer.WriteString("commitId", commitId);
                    writer.WriteString("payloadDigest", payloadDigest);
                }

                if (allowLegacyReplay)
                {
                    writer.WriteNumber("mutationContractVersion", 0);
                }

                writer.WritePropertyName("mutations");
                writer.WriteStartArray();
                foreach (var mutation in mutations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    switch (mutation.Kind)
                    {
                        case MemoryMutationKind.Upsert:
                            writer.WriteString(
                                "operation",
                                UpsertOperation);
                            writer.WritePropertyName("record");
                            JsonSerializer.Serialize(
                                writer,
                                PersistedMemoryRecord.FromMemoryRecord(
                                    mutation.Record
                                    ?? throw new InvalidOperationException(
                                        "An upsert mutation requires a "
                                        + "record.")),
                                PersistenceJsonContext.Default
                                    .PersistedMemoryRecord);
                            break;
                        case MemoryMutationKind.Delete:
                            writer.WriteString(
                                "operation",
                                DeleteOperation);
                            writer.WriteString(
                                "memoryId",
                                mutation.MemoryId);
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unknown memory mutation kind "
                                + $"'{mutation.Kind}'.");
                    }

                    if (mutation.ExpectedRecord is not null)
                    {
                        writer.WritePropertyName("expectedRecord");
                        JsonSerializer.Serialize(
                            writer,
                            PersistedMemoryExpectation.FromExpectation(
                                mutation.ExpectedRecord),
                            PersistenceJsonContext.Default
                                .PersistedMemoryExpectation);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        cancellationToken.ThrowIfCancellationRequested();
        var frame = BuildFrame(payload.WrittenSpan);
        EnsureMutationCapacity(revision, frame.Length);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteFrameAsync(frame).ConfigureAwait(false);
    }

    private async ValueTask WriteFrameAsync(byte[] frame)
    {
        // Callers make their final cancellation check before entering this
        // method. Past that commit boundary, finish the bounded write so
        // cancellation cannot make the result ambiguous.
        var bytesToWrite = _faultInjector?.GetWriteLength(frame.Length)
            ?? frame.Length;
        if (bytesToWrite < 0 || bytesToWrite > frame.Length)
        {
            throw new InvalidOperationException(
                "A memory-store fault injector returned an invalid write "
                + "length.");
        }

        _faultInjector?.OnWriteStage(
            JournalWriteStage.BeforeWrite,
            bytesWritten: 0,
            frame.Length);

        var writeStarted = false;
        try
        {
            _stream.Position = _stream.Length;
            if (bytesToWrite > 0)
            {
                writeStarted = true;
                await _stream.WriteAsync(
                        frame,
                        0,
                        bytesToWrite,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _faultInjector?.OnWriteStage(
                JournalWriteStage.AfterWrite,
                bytesToWrite,
                frame.Length);

            if (bytesToWrite != frame.Length)
            {
                throw new IOException(
                    $"Only {bytesToWrite} of {frame.Length} memory frame "
                    + "bytes were written.");
            }

            await _stream.FlushAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (_flushToDiskOnMutation)
            {
                _stream.Flush(flushToDisk: true);
            }

            _faultInjector?.OnWriteStage(
                JournalWriteStage.AfterFlush,
                bytesToWrite,
                frame.Length);
        }
        catch
        {
            if (writeStarted)
            {
                _faulted = true;
                Volatile.Write(
                    ref _indexStatus,
                    (int)MemoryIndexStatus.Faulted);
            }

            throw;
        }
    }

    private void Recover()
    {
        _stream.Position = 0;
        var header = new byte[HeaderSize];
        var footer = new byte[FooterSize];
        var lastCommittedOffset = 0L;

        while (_stream.Position < _stream.Length)
        {
            var frameOffset = _stream.Position;
            var remaining = _stream.Length - frameOffset;
            if (remaining < HeaderSize)
            {
                TruncateTornTail(lastCommittedOffset);
                return;
            }

            ReadExactly(_stream, header, 0, header.Length);
            if (ReadUInt32(header, 0) != FrameMagic)
            {
                throw Corrupt(frameOffset, "invalid frame magic.");
            }

            var payloadLength = ReadInt32(header, 4);
            if (payloadLength < 0)
            {
                throw Corrupt(
                    frameOffset,
                    $"invalid payload length {payloadLength}.");
            }

            if (payloadLength > _maxFramePayloadBytes)
            {
                throw new MemoryStoreCapacityExceededException(
                    nameof(FileMemoryStoreOptions.MaxFramePayloadBytes),
                    _maxFramePayloadBytes,
                    payloadLength);
            }

            var totalFrameLength = checked(
                (long)HeaderSize + payloadLength + FooterSize);
            if (remaining < totalFrameLength)
            {
                TruncateTornTail(lastCommittedOffset);
                return;
            }

            var payload = new byte[payloadLength];
            ReadExactly(_stream, payload, 0, payload.Length);
            ReadExactly(_stream, footer, 0, footer.Length);
            if (ReadUInt32(footer, 0) != CommitMagic)
            {
                throw Corrupt(frameOffset, "missing commit marker.");
            }

            var expectedChecksum = ReadUInt32(header, 8);
            var actualChecksum = Crc32.Compute(payload);
            if (expectedChecksum != actualChecksum)
            {
                throw Corrupt(frameOffset, "frame checksum mismatch.");
            }

            EnsureRecoveredMutationCapacity();

            try
            {
                var record = JsonSerializer.Deserialize(
                                 payload,
                                 PersistenceJsonContext.Default
                                     .MemoryFrameRecord)
                             ?? throw new JsonException(
                                 "Frame payload is null.");
                ApplyRecoveredRecord(record);
            }
            catch (Exception exception) when (
                exception is (
                    JsonException
                    or ArgumentException
                    or InvalidOperationException
                    or OverflowException)
                && exception is not MemoryStoreCapacityExceededException)
            {
                throw Corrupt(frameOffset, exception.Message);
            }

            lastCommittedOffset = checked(frameOffset + totalFrameLength);
        }

        _stream.Position = _stream.Length;
    }

    private void ApplyRecoveredRecord(MemoryFrameRecord frame)
    {
        if (frame.FormatVersion != LegacyFormatVersion
            && frame.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported memory format version "
                + $"'{frame.FormatVersion}'.");
        }

        var expectedRevision = checked(_revision + 1);
        if (frame.Revision != expectedRevision)
        {
            throw new InvalidOperationException(
                $"Expected memory revision {expectedRevision}, "
                + $"but found {frame.Revision}.");
        }

        if (string.Equals(
                frame.Operation,
                UpsertOperation,
                StringComparison.Ordinal))
        {
            if (frame.MemoryId is not null
                || frame.Record is null
                || frame.Mutations is not null
                || frame.CommitId is not null
                || frame.PayloadDigest is not null
                || frame.MutationContractVersion.HasValue)
            {
                throw new InvalidOperationException(
                    "An upsert frame must contain exactly one memory record.");
            }

            var record = frame.Record.ToMemoryRecord();
            if (frame.FormatVersion != LegacyFormatVersion)
            {
                _records.TryGetValue(record.MemoryId, out var existing);
                MemoryMutationAdmission.EnsureCanApplyUnconditionalUpsert(
                    MemoryMutation.Upsert(record),
                    existing);
            }
            if (!_records.ContainsKey(record.MemoryId)
                && _records.Count >= _capacity)
            {
                throw new MemoryStoreCapacityExceededException(
                    nameof(FileMemoryStoreOptions.Capacity),
                    _capacity,
                    checked((long)_records.Count + 1));
            }

            _records[record.MemoryId] = record;
        }
        else if (string.Equals(
                     frame.Operation,
                     DeleteOperation,
                     StringComparison.Ordinal))
        {
            if (frame.Record is not null
                || frame.MemoryId is null
                || frame.Mutations is not null
                || frame.CommitId is not null
                || frame.PayloadDigest is not null
                || frame.MutationContractVersion.HasValue)
            {
                throw new InvalidOperationException(
                    "A delete frame must contain exactly one memory id.");
            }

            ValidateMemoryId(frame.MemoryId);
            if (!_records.Remove(frame.MemoryId))
            {
                throw new InvalidOperationException(
                    $"Delete frame references unknown memory "
                    + $"'{frame.MemoryId}'.");
            }
        }
        else if (string.Equals(
                     frame.Operation,
                     BatchOperation,
                     StringComparison.Ordinal))
        {
            if (frame.Record is not null
                || frame.MemoryId is not null
                || frame.Mutations is null
                || (frame.CommitId is null)
                != (frame.PayloadDigest is null)
                || frame.MutationContractVersion is not null and not 0)
            {
                throw new InvalidOperationException(
                    "A batch frame must contain exactly one mutation array.");
            }

            var idempotent = frame.CommitId is not null;
            if (frame.FormatVersion == LegacyFormatVersion
                && frame.MutationContractVersion.HasValue)
            {
                throw new InvalidOperationException(
                    "A version 1 batch cannot declare a mutation contract version.");
            }
            if (frame.MutationContractVersion == 0 && !idempotent)
            {
                throw new InvalidOperationException(
                    "A legacy replay marker requires an idempotent commit identity.");
            }

            var allowLegacyReplay =
                frame.FormatVersion == LegacyFormatVersion
                || frame.MutationContractVersion == 0;
            var snapshot = ApplyRecoveredBatch(
                frame.Mutations,
                allowNoChange: idempotent,
                allowLegacyReplay);
            if (idempotent)
            {
                var commitId = RuntimeGuard.RequiredUtf8(
                    frame.CommitId!,
                    256,
                    nameof(frame.CommitId));
                var digest =
                    RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(
                        snapshot);
                if (!CanonicalJsonDigest.IsSha256(frame.PayloadDigest)
                    || !string.Equals(
                        digest,
                        frame.PayloadDigest,
                        StringComparison.Ordinal)
                    || !_committedBatchDigests.TryAdd(
                        commitId,
                        frame.PayloadDigest!))
                {
                    throw new InvalidOperationException(
                        "An idempotent memory batch frame is invalid.");
                }
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown memory operation '{frame.Operation}'.");
        }

        Interlocked.Exchange(ref _revision, frame.Revision);
    }

    private MemoryMutation[] ApplyRecoveredBatch(
        IReadOnlyList<MemoryFrameMutation> mutations,
        bool allowNoChange,
        bool allowLegacyReplay)
    {
        var mutationCount = mutations.Count;
        if (mutationCount is < 1 or > MemoryBatchLimits.MaxMutations)
        {
            throw new InvalidOperationException(
                $"A memory batch must contain between 1 and "
                + $"{MemoryBatchLimits.MaxMutations} mutations.");
        }

        var parsed = new MemoryMutation[mutationCount];
        for (var index = 0; index < mutationCount; index++)
        {
            var mutation = mutations[index]
                               ?? throw new InvalidOperationException(
                                   $"Memory mutation {index} is null.");
            var expected = mutation.ExpectedRecord?.ToExpectation();
            if (string.Equals(
                    mutation.Operation,
                    UpsertOperation,
                    StringComparison.Ordinal))
            {
                if (mutation.MemoryId is not null
                    || mutation.Record is null)
                {
                    throw new InvalidOperationException(
                        $"Batch upsert {index} must contain exactly one "
                        + "memory record.");
                }

                var record = mutation.Record.ToMemoryRecord();
                parsed[index] = MemoryMutation.Restore(
                    MemoryMutationKind.Upsert,
                    record.MemoryId,
                    record,
                    expected);
            }
            else if (string.Equals(
                         mutation.Operation,
                         DeleteOperation,
                         StringComparison.Ordinal))
            {
                if (mutation.Record is not null
                    || mutation.MemoryId is null)
                {
                    throw new InvalidOperationException(
                        $"Batch delete {index} must contain exactly one "
                        + "memory id.");
                }

                ValidateMemoryId(mutation.MemoryId);
                parsed[index] = MemoryMutation.Restore(
                    MemoryMutationKind.Delete,
                    mutation.MemoryId,
                    record: null,
                    expectedRecord: expected);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unknown memory batch operation "
                    + $"'{mutation.Operation}'.");
            }
        }

        var snapshot = MemoryBatchValidator.Snapshot(
            parsed,
            CancellationToken.None);
        var staged = new Dictionary<string, MemoryRecord>(
            _records,
            StringComparer.Ordinal);
        var changed = false;
        foreach (var mutation in snapshot)
        {
            staged.TryGetValue(mutation.MemoryId, out var existing);
            if (allowLegacyReplay)
            {
                MemoryMutationAdmission.EnsureCanReplayLegacy(mutation);
            }
            else
            {
                MemoryMutationAdmission.EnsureCanApply(mutation, existing);
            }
            switch (mutation.Kind)
            {
                case MemoryMutationKind.Upsert:
                    staged[mutation.MemoryId] = mutation.Record
                        ?? throw new InvalidOperationException(
                            "An upsert mutation requires a record.");
                    changed = true;
                    break;
                case MemoryMutationKind.Delete:
                    changed |= staged.Remove(mutation.MemoryId);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown memory mutation kind "
                        + $"'{mutation.Kind}'.");
            }
        }

        if (!changed && !allowNoChange)
        {
            throw new InvalidOperationException(
                "A committed memory batch must change at least one record.");
        }

        if (staged.Count > _capacity)
        {
            throw new MemoryStoreCapacityExceededException(
                nameof(FileMemoryStoreOptions.Capacity),
                _capacity,
                staged.Count);
        }

        _records = staged;
        return snapshot;
    }

    private void EnsureExpectedRevision(long? expectedRevision)
    {
        if (expectedRevision.HasValue
            && expectedRevision.Value != _revision)
        {
            throw new MemoryStoreRevisionConflictException(
                expectedRevision.Value,
                _revision);
        }
    }

    private static void ValidateExpectedRevision(long? expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision));
        }
    }

    private static void ValidateMemoryId(string memoryId)
    {
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            throw new ArgumentException(
                "A memory id is required.",
                nameof(memoryId));
        }

        if (Encoding.UTF8.GetByteCount(memoryId) > 128)
        {
            throw new RuntimeContentLimitException(
                nameof(memoryId),
                "string_bytes_exceeded",
                "The memory id exceeds 128 UTF-8 bytes.");
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileMemoryStore));
        }

        if (_faulted)
        {
            throw new MemoryStoreFaultedException(_path);
        }
    }

    private void EnsureExistingLogCapacity()
    {
        if (_stream.Length > _maxLogBytes)
        {
            throw new MemoryStoreCapacityExceededException(
                nameof(FileMemoryStoreOptions.MaxLogBytes),
                _maxLogBytes,
                _stream.Length);
        }
    }

    private void EnsureMutationCapacity(
        long attemptedRevision,
        int frameLength)
    {
        if (attemptedRevision > _maxMutationFrames)
        {
            throw new MemoryStoreCapacityExceededException(
                nameof(FileMemoryStoreOptions.MaxMutationFrames),
                _maxMutationFrames,
                attemptedRevision);
        }

        var attemptedLength = SaturatingAdd(_stream.Length, frameLength);
        if (attemptedLength > _maxLogBytes)
        {
            throw new MemoryStoreCapacityExceededException(
                nameof(FileMemoryStoreOptions.MaxLogBytes),
                _maxLogBytes,
                attemptedLength);
        }
    }

    private void EnsureRecoveredMutationCapacity()
    {
        var attemptedRevision = SaturatingAdd(_revision, 1);
        if (attemptedRevision > _maxMutationFrames)
        {
            throw new MemoryStoreCapacityExceededException(
                nameof(FileMemoryStoreOptions.MaxMutationFrames),
                _maxMutationFrames,
                attemptedRevision);
        }
    }

    private static long SaturatingAdd(long value, long additional)
    {
        return value > long.MaxValue - additional
            ? long.MaxValue
            : value + additional;
    }

    private void TruncateTornTail(long length)
    {
        _stream.SetLength(length);
        _stream.Position = length;
        _stream.Flush(flushToDisk: true);
    }

    private MemoryStoreCorruptionException Corrupt(
        long offset,
        string message)
    {
        return new MemoryStoreCorruptionException(_path, offset, message);
    }

    private static byte[] BuildFrame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[
            checked(HeaderSize + payload.Length + FooterSize)];
        WriteUInt32(frame, 0, FrameMagic);
        WriteInt32(frame, 4, payload.Length);
        WriteUInt32(frame, 8, Crc32.Compute(payload));
        payload.CopyTo(frame.AsSpan(HeaderSize, payload.Length));
        WriteUInt32(
            frame,
            HeaderSize + payload.Length,
            CommitMagic);
        return frame;
    }

    private static void ReadExactly(
        Stream stream,
        byte[] buffer,
        int offset,
        int count)
    {
        while (count > 0)
        {
            var read = stream.Read(buffer, offset, count);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
            count -= read;
        }
    }

    private static int ReadInt32(byte[] buffer, int offset)
    {
        return unchecked((int)ReadUInt32(buffer, offset));
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        return (uint)(
            buffer[offset]
            | buffer[offset + 1] << 8
            | buffer[offset + 2] << 16
            | buffer[offset + 3] << 24);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        WriteUInt32(buffer, offset, unchecked((uint)value));
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static class Crc32
    {
        private const uint Polynomial = 0xEDB88320;
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> value)
        {
            var checksum = uint.MaxValue;
            foreach (var item in value)
            {
                checksum = Table[(checksum ^ item) & 0xFF]
                           ^ checksum >> 8;
            }

            return ~checksum;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) == 0
                        ? value >> 1
                        : value >> 1 ^ Polynomial;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
