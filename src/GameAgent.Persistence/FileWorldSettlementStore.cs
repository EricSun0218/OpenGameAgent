using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Persistence;

/// <summary>
/// Single-writer, append-only settlement outbox. Committed frames use a
/// checksum, commit marker, monotonic revision, and digest chain. Torn tails
/// are truncated; corruption inside the committed prefix fails closed.
/// </summary>
public sealed class FileWorldSettlementStore :
    IWorldSettlementStore,
    IWorldSettlementQuiescenceSource,
    IDisposable,
    IAsyncDisposable
{
    internal const int FrameHeaderSize = 12;
    internal const int FrameFooterSize = 4;

    private const uint FrameMagic = 0x31534F57;
    private const uint CommitMagic = 0x54494D43;
    private const int FormatVersion = 1;
    private const string GenesisFrameDigest =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly string _path;
    private readonly ExclusiveFileWriterLease _writerLease;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, WorldSettlementRecord> _records =
        new(StringComparer.Ordinal);
    private readonly SortedSet<string> _unsettledIds =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _residentBytesByRecord =
        new(StringComparer.Ordinal);
    private readonly int _maxFramePayloadBytes;
    private readonly long _maxLogBytes;
    private readonly long _maxMutationFrames;
    private readonly int _maxRecords;
    private readonly int _maxFrameJsonTokens;
    private readonly long _maxResidentBytes;
    private readonly bool _flushToDiskOnMutation;
    private readonly IJournalFaultInjector? _faultInjector;

    private long _storeRevision;
    private long _residentBytes;
    private long _reservedTerminalFrames;
    private string _lastFrameDigest = GenesisFrameDigest;
    private bool _faulted;
    private bool _disposed;

    public FileWorldSettlementStore(
        string path,
        FileWorldSettlementStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A world settlement store path is required.",
                nameof(path));
        }

        options ??= new FileWorldSettlementStoreOptions();
        ValidateOptions(options);
        _path = System.IO.Path.GetFullPath(path);
        _maxFramePayloadBytes = options.MaxFramePayloadBytes;
        _maxLogBytes = options.MaxLogBytes;
        _maxMutationFrames = options.MaxMutationFrames;
        _maxRecords = options.MaxRecords;
        _maxFrameJsonTokens = options.MaxFrameJsonTokens;
        _maxResidentBytes = options.MaxResidentBytes;
        _flushToDiskOnMutation = options.FlushToDiskOnMutation;
        _faultInjector = options.FaultInjector;

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writerLease = ExclusiveFileWriterLease.Acquire(_path);
        _stream = _writerLease.Stream;
        try
        {
            if (_stream.Length > _maxLogBytes)
            {
                throw Capacity(
                    nameof(
                        FileWorldSettlementStoreOptions.MaxLogBytes),
                    _maxLogBytes,
                    _stream.Length);
            }

            Recover();
            EnsureRecoveredReserveCapacity();
        }
        catch
        {
            _writerLease.Dispose();
            throw;
        }
    }

    public string Path => _path;

    public long StoreRevision => Interlocked.Read(ref _storeRevision);

    public int RecordCount
    {
        get
        {
            _gate.Wait();
            try
            {
                ThrowIfUnavailable();
                return _records.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public long EstimatedResidentBytes =>
        Interlocked.Read(ref _residentBytes);

    public async ValueTask<WorldSettlementRecord?> ReadAsync(
        string settlementId,
        CancellationToken cancellationToken = default)
    {
        var id = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            return _records.TryGetValue(id, out var record)
                ? WorldSettlementValidation.CloneRecord(record)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<WorldSettlementBeginResult> BeginAsync(
        WorldSettlementPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var admitted = WorldSettlementValidation.ClonePlan(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_records.TryGetValue(
                    admitted.SettlementId,
                    out var existing))
            {
                return new WorldSettlementBeginResult(
                    string.Equals(
                        existing.Plan.SemanticDigest,
                        admitted.SemanticDigest,
                        StringComparison.Ordinal)
                        ? WorldSettlementBeginStatus.Existing
                        : WorldSettlementBeginStatus.Conflict,
                    WorldSettlementValidation.CloneRecord(existing));
            }

            if (_records.Count >= _maxRecords)
            {
                return new WorldSettlementBeginResult(
                    WorldSettlementBeginStatus.CapacityExceeded,
                    record: null);
            }

            var record = WorldSettlementValidation.NewRecord(admitted);
            await PersistAsync(
                    record,
                    isNewRecord: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return new WorldSettlementBeginResult(
                WorldSettlementBeginStatus.Created,
                WorldSettlementValidation.CloneRecord(record));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<WorldSettlementTransitionResult>
        TryTransitionAsync(
            WorldSettlementTransition transition,
            CancellationToken cancellationToken = default)
    {
        if (transition is null)
        {
            throw new ArgumentNullException(nameof(transition));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_records.TryGetValue(
                    transition.SettlementId,
                    out var current))
            {
                return new WorldSettlementTransitionResult(
                    WorldSettlementTransitionStatus.NotFound,
                    record: null);
            }

            var index = WorldSettlementValidation.FindDeliveryIndex(
                current,
                transition.OperationId);
            if (current.Revision != transition.ExpectedRecordRevision
                || !string.Equals(
                    current.Plan.SemanticDigest,
                    transition.PlanDigest,
                    StringComparison.Ordinal)
                || index < 0
                || current.DeliveryStates[index].Stage
                != transition.ExpectedStage)
            {
                return new WorldSettlementTransitionResult(
                    WorldSettlementTransitionStatus.Conflict,
                    WorldSettlementValidation.CloneRecord(current));
            }

            var updated = current.Transition(
                index,
                transition.ExpectedStage,
                transition.NextStage,
                transition.ReasonCode);
            await PersistAsync(
                    updated,
                    isNewRecord: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return new WorldSettlementTransitionResult(
                WorldSettlementTransitionStatus.Applied,
                WorldSettlementValidation.CloneRecord(updated));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<WorldSettlementPage>
        ListUnsettledAsync(
            WorldSettlementListRequest request,
            CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var afterId = request.ContinuationCursor is null
            ? null
            : WorldSettlementValidation.DecodeCursor(
                request.ContinuationCursor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var items = new List<WorldSettlementSummary>(
                request.MaxResults);
            var hasMore = false;
            if (_unsettledIds.Count > 0
                && (afterId is null
                    || StringComparer.Ordinal.Compare(
                        afterId,
                        _unsettledIds.Max!)
                    < 0))
            {
                var candidates = afterId is null
                    ? _unsettledIds
                    : _unsettledIds.GetViewBetween(
                        afterId,
                        _unsettledIds.Max!);
                foreach (var settlementId in candidates)
                {
                    if (afterId is not null
                        && StringComparer.Ordinal.Compare(
                            settlementId,
                            afterId)
                        <= 0)
                    {
                        continue;
                    }

                    if (items.Count == request.MaxResults)
                    {
                        hasMore = true;
                        break;
                    }

                    items.Add(WorldSettlementValidation.Summarize(
                        _records[settlementId]));
                }
            }

            return new WorldSettlementPage(
                items,
                items.Count == 0
                    ? null
                    : WorldSettlementValidation.EncodeCursor(
                        items[^1].SettlementId),
                hasMore);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IWorldSettlementQuiescenceLease?>
        TryAcquireSettledQuiescenceAsync(
            CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var release = true;
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_unsettledIds.Count != 0)
            {
                return null;
            }

            release = false;
            return new FileQuiescenceLease(
                _gate,
                _storeRevision);
        }
        finally
        {
            if (release)
            {
                _gate.Release();
            }
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
            _writerLease.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private sealed class FileQuiescenceLease
        : IWorldSettlementQuiescenceLease
    {
        private SemaphoreSlim? _gate;

        public FileQuiescenceLease(
            SemaphoreSlim gate,
            long storeRevision)
        {
            _gate = gate;
            StoreRevision = storeRevision;
        }

        public long StoreRevision { get; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return default;
        }
    }

    private async ValueTask PersistAsync(
        WorldSettlementRecord record,
        bool isNewRecord,
        CancellationToken cancellationToken)
    {
        var nextStoreRevision = checked(_storeRevision + 1);
        var frameRecord = new WorldSettlementFrameRecord
        {
            FormatVersion = FormatVersion,
            StoreRevision = nextStoreRevision,
            PreviousFrameDigest = _lastFrameDigest,
            Record = PersistedWorldSettlementRecord.FromRecord(record)
        };
        using var payload = BoundedJsonPayload.Serialize(
            frameRecord,
            PersistenceJsonContext.Default.WorldSettlementFrameRecord,
            _maxFramePayloadBytes,
            attempted => Capacity(
                nameof(
                    FileWorldSettlementStoreOptions
                        .MaxFramePayloadBytes),
                _maxFramePayloadBytes,
                attempted));
        ValidateJson(payload.WrittenSpan);
        var frame = BuildFrame(payload.WrittenSpan);
        var estimatedResidentBytes =
            EstimateResidentBytes(payload.WrittenCount);
        EnsureMutationCapacity(
            nextStoreRevision,
            frame.Length,
            record,
            estimatedResidentBytes,
            isNewRecord);
        cancellationToken.ThrowIfCancellationRequested();

        var frameDigest = ComputeSha256(payload.WrittenSpan);
        await WriteFrameAsync(frame).ConfigureAwait(false);
        ApplyCommitted(
            record,
            isNewRecord,
            estimatedResidentBytes,
            nextStoreRevision,
            frameDigest);
    }

    private void ApplyCommitted(
        WorldSettlementRecord record,
        bool isNewRecord,
        long estimatedResidentBytes,
        long storeRevision,
        string frameDigest)
    {
        var previousReservations = isNewRecord
            ? 0
            : CountReconciliation(
                _records[record.Plan.SettlementId]);
        if (isNewRecord)
        {
            _records.Add(record.Plan.SettlementId, record);
            _residentBytesByRecord.Add(
                record.Plan.SettlementId,
                estimatedResidentBytes);
            _residentBytes = checked(
                _residentBytes + estimatedResidentBytes);
        }
        else
        {
            var previousBytes =
                _residentBytesByRecord[record.Plan.SettlementId];
            _records[record.Plan.SettlementId] = record;
            _residentBytesByRecord[record.Plan.SettlementId] =
                estimatedResidentBytes;
            _residentBytes = checked(
                _residentBytes - previousBytes
                + estimatedResidentBytes);
        }

        var nextReservations = CountReconciliation(record);
        _reservedTerminalFrames = checked(
            _reservedTerminalFrames
            - previousReservations
            + nextReservations);

        if (record.Stage is WorldSettlementStage.Applied
            or WorldSettlementStage.Rejected)
        {
            _unsettledIds.Remove(record.Plan.SettlementId);
        }
        else
        {
            _unsettledIds.Add(record.Plan.SettlementId);
        }

        _lastFrameDigest = frameDigest;
        Interlocked.Exchange(ref _storeRevision, storeRevision);
    }

    private async ValueTask WriteFrameAsync(byte[] frame)
    {
        // Cancellation is checked before bytes can become visible. Once the
        // write starts, finish or fault the handle without reporting a
        // cancelled mutation that may already be durable.
        var bytesToWrite = _faultInjector?.GetWriteLength(frame.Length)
            ?? frame.Length;
        if (bytesToWrite < 0 || bytesToWrite > frame.Length)
        {
            throw new InvalidOperationException(
                "A settlement-store fault injector returned an invalid "
                + "write length.");
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
                    $"Only {bytesToWrite} of {frame.Length} settlement "
                    + "frame bytes were written.");
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
            }

            throw;
        }
    }

    private void Recover()
    {
        _stream.Position = 0;
        var header = new byte[FrameHeaderSize];
        var footer = new byte[FrameFooterSize];
        var lastCommittedOffset = 0L;
        while (_stream.Position < _stream.Length)
        {
            var frameOffset = _stream.Position;
            var remaining = _stream.Length - frameOffset;
            if (remaining < FrameHeaderSize)
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
                throw Capacity(
                    nameof(
                        FileWorldSettlementStoreOptions
                            .MaxFramePayloadBytes),
                    _maxFramePayloadBytes,
                    payloadLength);
            }

            var totalFrameLength = checked(
                (long)FrameHeaderSize
                + payloadLength
                + FrameFooterSize);
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

            if (ReadUInt32(header, 8) != Crc32.Compute(payload))
            {
                throw Corrupt(frameOffset, "frame checksum mismatch.");
            }

            EnsureRecoveredMutationCapacity();
            try
            {
                ValidateJson(payload);
                var frame = JsonSerializer.Deserialize(
                                payload,
                                PersistenceJsonContext.Default
                                    .WorldSettlementFrameRecord)
                            ?? throw new JsonException(
                                "Settlement frame payload is null.");
                ApplyRecovered(frame, payload);
            }
            catch (Exception exception) when (
                exception is (
                    JsonException
                    or ArgumentException
                    or InvalidOperationException
                    or OverflowException)
                && exception is not
                    FileWorldSettlementStoreCapacityException)
            {
                throw Corrupt(frameOffset, exception.Message, exception);
            }

            lastCommittedOffset = checked(
                frameOffset + totalFrameLength);
        }

        _stream.Position = _stream.Length;
    }

    private void ApplyRecovered(
        WorldSettlementFrameRecord frame,
        byte[] payload)
    {
        if (frame.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported settlement-store format version "
                + $"'{frame.FormatVersion}'.");
        }

        var expectedStoreRevision = checked(_storeRevision + 1);
        if (frame.StoreRevision != expectedStoreRevision)
        {
            throw new InvalidOperationException(
                $"Expected store revision {expectedStoreRevision}, but "
                + $"found {frame.StoreRevision}.");
        }

        if (!CanonicalJsonDigest.IsSha256(frame.PreviousFrameDigest)
            || !string.Equals(
                frame.PreviousFrameDigest,
                _lastFrameDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The settlement frame digest chain is invalid.");
        }

        var record = (frame.Record
                      ?? throw new JsonException(
                          "A settlement frame requires a record."))
            .Restore();
        var isNewRecord = !_records.TryGetValue(
            record.Plan.SettlementId,
            out var current);
        if (isNewRecord)
        {
            if (record.Revision != 0
                || record.DeliveryStates.Any(
                    item => item.Stage
                            != WorldSettlementStage.Pending))
            {
                throw new InvalidOperationException(
                    "A recovered settlement must begin at revision zero "
                    + "with pending deliveries.");
            }

            if (_records.Count >= _maxRecords)
            {
                throw Capacity(
                    nameof(
                        FileWorldSettlementStoreOptions.MaxRecords),
                    _maxRecords,
                    SaturatingAdd(_records.Count, 1));
            }
        }
        else
        {
            ValidateSuccessor(current!, record);
        }

        var estimatedResidentBytes = EstimateResidentBytes(payload.Length);
        EnsureResidentCapacity(
            record.Plan.SettlementId,
            estimatedResidentBytes,
            isNewRecord);
        ApplyCommitted(
            record,
            isNewRecord,
            estimatedResidentBytes,
            frame.StoreRevision,
            ComputeSha256(payload));
    }

    private static void ValidateSuccessor(
        WorldSettlementRecord current,
        WorldSettlementRecord next)
    {
        if (next.Revision != checked(current.Revision + 1)
            || !string.Equals(
                next.Plan.SemanticDigest,
                current.Plan.SemanticDigest,
                StringComparison.Ordinal)
            || next.DeliveryStates.Count != current.DeliveryStates.Count)
        {
            throw new InvalidOperationException(
                "The recovered settlement successor is invalid.");
        }

        var changed = 0;
        for (var index = 0;
             index < current.DeliveryStates.Count;
             index++)
        {
            var before = current.DeliveryStates[index];
            var after = next.DeliveryStates[index];
            if (!string.Equals(
                    before.OperationId,
                    after.OperationId,
                    StringComparison.Ordinal)
                || before.Kind != after.Kind)
            {
                throw new InvalidOperationException(
                    "A settlement successor changed delivery identity.");
            }

            if (before.Stage == after.Stage)
            {
                if (!string.Equals(
                        before.ReasonCode,
                        after.ReasonCode,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "An unchanged settlement stage changed its reason.");
                }

                continue;
            }

            changed++;
            if (!WorldSettlementValidation.IsAllowedTransition(
                    before.Stage,
                    after.Stage))
            {
                throw new InvalidOperationException(
                    "A settlement successor contains an invalid "
                    + "transition.");
            }
        }

        if (changed != 1)
        {
            throw new InvalidOperationException(
                "A settlement successor must change exactly one "
                + "delivery.");
        }
    }

    private void ValidateJson(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(
            payload,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 72
            });
        var tokens = 0;
        var containers = new List<JsonContainer>(capacity: 72);
        while (reader.Read())
        {
            tokens = checked(tokens + 1);
            if (tokens > _maxFrameJsonTokens)
            {
                throw Capacity(
                    nameof(
                        FileWorldSettlementStoreOptions
                            .MaxFrameJsonTokens),
                    _maxFrameJsonTokens,
                    tokens);
            }

            if (reader.TokenType is JsonTokenType.String
                    or JsonTokenType.PropertyName
                && reader.ValueSpan.Length > _maxFramePayloadBytes)
            {
                throw Capacity(
                    "raw JSON string bytes",
                    _maxFramePayloadBytes,
                    reader.ValueSpan.Length);
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Add(JsonContainer.Object());
                    break;
                case JsonTokenType.StartArray:
                    containers.Add(JsonContainer.Array());
                    break;
                case JsonTokenType.PropertyName:
                    if (containers.Count == 0
                        || containers[^1].IsArray)
                    {
                        throw new JsonException(
                            "A JSON property must belong to an object.");
                    }

                    var name = reader.GetString()
                               ?? throw new JsonException(
                                   "A JSON property name cannot be null.");
                    if (!containers[^1].PropertyNames!.Add(name))
                    {
                        throw new JsonException(
                            $"Duplicate JSON property '{name}' is not "
                            + "allowed in a settlement frame.");
                    }

                    break;
                case JsonTokenType.EndObject:
                    if (containers.Count == 0
                        || containers[^1].IsArray)
                    {
                        throw new JsonException(
                            "The settlement JSON object stack is invalid.");
                    }

                    containers.RemoveAt(containers.Count - 1);
                    break;
                case JsonTokenType.EndArray:
                    if (containers.Count == 0
                        || !containers[^1].IsArray)
                    {
                        throw new JsonException(
                            "The settlement JSON array stack is invalid.");
                    }

                    containers.RemoveAt(containers.Count - 1);
                    break;
            }
        }

        if (containers.Count != 0)
        {
            throw new JsonException(
                "The settlement JSON container stack is incomplete.");
        }
    }

    private void EnsureMutationCapacity(
        long attemptedRevision,
        int frameLength,
        WorldSettlementRecord record,
        long estimatedResidentBytes,
        bool isNewRecord)
    {
        var currentReservations = isNewRecord
            ? 0
            : CountReconciliation(
                _records[record.Plan.SettlementId]);
        var nextReservations = checked(
            _reservedTerminalFrames
            - currentReservations
            + CountReconciliation(record));
        var attemptedFrames = SaturatingAdd(
            attemptedRevision,
            nextReservations);
        if (attemptedFrames > _maxMutationFrames)
        {
            throw Capacity(
                nameof(
                    FileWorldSettlementStoreOptions.MaxMutationFrames),
                _maxMutationFrames,
                attemptedFrames);
        }

        var attemptedLength = SaturatingAdd(_stream.Length, frameLength);
        var maximumFrameLength = checked(
            _maxFramePayloadBytes
            + (long)FrameHeaderSize
            + FrameFooterSize);
        var reservedBytes = nextReservations > 0
            && maximumFrameLength
            > long.MaxValue / nextReservations
                ? long.MaxValue
                : maximumFrameLength * nextReservations;
        var attemptedReservedLength = SaturatingAdd(
            attemptedLength,
            reservedBytes);
        if (attemptedReservedLength > _maxLogBytes)
        {
            throw Capacity(
                nameof(
                    FileWorldSettlementStoreOptions.MaxLogBytes),
                _maxLogBytes,
                attemptedReservedLength);
        }

        EnsureResidentCapacity(
            record.Plan.SettlementId,
            estimatedResidentBytes,
            isNewRecord);
    }

    private static int CountReconciliation(
        WorldSettlementRecord record)
    {
        return record.DeliveryStates.Count(
            item => item.Stage == WorldSettlementStage.Reconciliation);
    }

    private void EnsureRecoveredMutationCapacity()
    {
        var attempted = SaturatingAdd(_storeRevision, 1);
        if (attempted > _maxMutationFrames)
        {
            throw Capacity(
                nameof(
                    FileWorldSettlementStoreOptions.MaxMutationFrames),
                _maxMutationFrames,
                attempted);
        }
    }

    private void EnsureRecoveredReserveCapacity()
    {
        var attemptedFrames = SaturatingAdd(
            _storeRevision,
            _reservedTerminalFrames);
        if (attemptedFrames > _maxMutationFrames)
        {
            throw Capacity(
                nameof(
                    FileWorldSettlementStoreOptions.MaxMutationFrames),
                _maxMutationFrames,
                attemptedFrames);
        }

        var maximumFrameLength = checked(
            _maxFramePayloadBytes
            + (long)FrameHeaderSize
            + FrameFooterSize);
        var reservedBytes = _reservedTerminalFrames > 0
            && maximumFrameLength
            > long.MaxValue / _reservedTerminalFrames
                ? long.MaxValue
                : maximumFrameLength * _reservedTerminalFrames;
        var attemptedLength = SaturatingAdd(
            _stream.Length,
            reservedBytes);
        if (attemptedLength > _maxLogBytes)
        {
            throw Capacity(
                nameof(FileWorldSettlementStoreOptions.MaxLogBytes),
                _maxLogBytes,
                attemptedLength);
        }
    }

    private void EnsureResidentCapacity(
        string settlementId,
        long estimatedResidentBytes,
        bool isNewRecord)
    {
        var previous = isNewRecord
            ? 0
            : _residentBytesByRecord[settlementId];
        var attempted = SaturatingAdd(
            Math.Max(0, _residentBytes - previous),
            estimatedResidentBytes);
        if (attempted > _maxResidentBytes)
        {
            throw Capacity(
                nameof(
                    FileWorldSettlementStoreOptions.MaxResidentBytes),
                _maxResidentBytes,
                attempted);
        }
    }

    private void TruncateTornTail(long length)
    {
        _stream.SetLength(length);
        _stream.Position = length;
        _stream.Flush(flushToDisk: true);
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(FileWorldSettlementStore));
        }

        if (_faulted)
        {
            throw new FileWorldSettlementStoreFaultedException(_path);
        }
    }

    private FileWorldSettlementStoreCapacityException Capacity(
        string limitName,
        long limit,
        long attempted)
    {
        return new FileWorldSettlementStoreCapacityException(
            limitName,
            limit,
            attempted);
    }

    private FileWorldSettlementStoreCorruptionException Corrupt(
        long offset,
        string message,
        Exception? innerException = null)
    {
        return new FileWorldSettlementStoreCorruptionException(
            _path,
            offset,
            message,
            innerException);
    }

    private static void ValidateOptions(
        FileWorldSettlementStoreOptions options)
    {
        if (options.MaxFramePayloadBytes is < 1_024
            or > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFramePayloadBytes must be between 1 KiB and 64 MiB.");
        }

        var minimumLogBytes = checked(
            options.MaxFramePayloadBytes
            + (long)FrameHeaderSize
            + FrameFooterSize);
        if (options.MaxLogBytes < minimumLogBytes
            || options.MaxLogBytes > 4L * 1_099_511_627_776)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxLogBytes must fit one maximum frame and cannot exceed "
                + "4 TiB.");
        }

        if (options.MaxMutationFrames is < 1 or > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxMutationFrames must be between 1 and 100,000,000.");
        }

        if (options.MaxRecords is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxRecords must be between 1 and 10,000,000.");
        }

        if (options.MaxFrameJsonTokens is < 1_024 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFrameJsonTokens must be between 1,024 and "
                + "10,000,000.");
        }

        var minimumResident = checked(
            options.MaxFramePayloadBytes * 4L + 4_096);
        if (options.MaxResidentBytes < minimumResident
            || options.MaxResidentBytes > 16L * 1_073_741_824)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxResidentBytes must fit one conservatively estimated "
                + "maximum record and cannot exceed 16 GiB.");
        }
    }

    private static long EstimateResidentBytes(int payloadBytes)
    {
        return checked(payloadBytes * 4L + 4_096);
    }

    private static long SaturatingAdd(long value, long additional)
    {
        return value > long.MaxValue - additional
            ? long.MaxValue
            : value + additional;
    }

    private static byte[] BuildFrame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[
            checked(FrameHeaderSize + payload.Length + FrameFooterSize)];
        WriteUInt32(frame, 0, FrameMagic);
        WriteInt32(frame, 4, payload.Length);
        WriteUInt32(frame, 8, Crc32.Compute(payload));
        payload.CopyTo(frame.AsSpan(FrameHeaderSize, payload.Length));
        WriteUInt32(
            frame,
            FrameHeaderSize + payload.Length,
            CommitMagic);
        return frame;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> value)
    {
        using var algorithm = SHA256.Create();
        var digest = algorithm.ComputeHash(value.ToArray());
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            _ = result.Append(item.ToString(
                "x2",
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
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

    private static void WriteInt32(
        byte[] buffer,
        int offset,
        int value)
    {
        WriteUInt32(buffer, offset, unchecked((uint)value));
    }

    private static void WriteUInt32(
        byte[] buffer,
        int offset,
        uint value)
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

    private sealed class JsonContainer
    {
        private JsonContainer(
            bool isArray,
            HashSet<string>? propertyNames)
        {
            IsArray = isArray;
            PropertyNames = propertyNames;
        }

        public bool IsArray { get; }

        public HashSet<string>? PropertyNames { get; }

        public static JsonContainer Object()
        {
            return new JsonContainer(
                isArray: false,
                new HashSet<string>(StringComparer.Ordinal));
        }

        public static JsonContainer Array()
        {
            return new JsonContainer(isArray: true, propertyNames: null);
        }
    }
}
