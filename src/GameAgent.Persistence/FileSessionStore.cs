using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence;

public sealed class FileSessionStore :
    IDurableSessionStore,
    IOperationLedger,
    IAtomicJournalBatchStore
{
    private const uint FrameMagic = 0x314A4147;
    private const uint CommitMagic = 0x54494D43;
    private const int HeaderSize = 12;
    private const int FooterSize = 4;
    private const int JournalFormatVersion = 3;
    private const int MinimumJournalFormatVersion = 1;
    private const int LegacyCheckpointLifecycleFormatVersion = 2;
    private const string GlobalStreamId = "$global";

    private readonly string _path;
    private readonly ExclusiveFileWriterLease _writerLease;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _flushToDiskOnAppend;
    private readonly int _maxFramePayloadBytes;
    private readonly long _maxJournalBytes;
    private readonly long _maxTotalCommittedEvents;
    private readonly int _maxEventsPerRun;
    private readonly IJournalFaultInjector? _faultInjector;
    private readonly Dictionary<string, RunStreamState> _runStreams =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredJournalEntry> _eventsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredOperation> _operations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentRun> _runCheckpoints =
        new(StringComparer.Ordinal);
    private long _totalCommittedEvents;
    private bool _faulted;
    private bool _disposed;

    public FileSessionStore(
        string path,
        FileJournalOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A journal path is required.",
                nameof(path));
        }

        options ??= new FileJournalOptions();
        if (options.MaxFramePayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFramePayloadBytes must be positive.");
        }

        if (options.MaxJournalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxJournalBytes must be positive.");
        }

        if (options.MaxTotalCommittedEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxTotalCommittedEvents must be positive.");
        }

        if (options.MaxEventsPerRun <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxEventsPerRun must be positive.");
        }

        _path = System.IO.Path.GetFullPath(path);
        _flushToDiskOnAppend = options.FlushToDiskOnAppend;
        _maxFramePayloadBytes = options.MaxFramePayloadBytes;
        _maxJournalBytes = options.MaxJournalBytes;
        _maxTotalCommittedEvents =
            options.MaxTotalCommittedEvents;
        _maxEventsPerRun = options.MaxEventsPerRun;
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
            EnsureExistingJournalCapacity();
            Recover();
        }
        catch
        {
            _writerLease.Dispose();
            throw;
        }
    }

    public string Path => _path;

    public async ValueTask AppendAsync(
        RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        _ = await AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<JournalAppendResult> AppendAtomicAsync(
        RuntimeEvent runtimeEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (runtimeEvent is null)
        {
            throw new ArgumentNullException(nameof(runtimeEvent));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var eventSnapshot = SnapshotEventWithinFrameCapacity(
            runtimeEvent,
            serializedBytesBeforeEvent: 0,
            out _);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            return await AppendUnderLockAsync(
                    eventSnapshot,
                    expectedRunRevision,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<JournalAppendResult>>
        AppendAtomicBatchAsync(
            IReadOnlyList<RuntimeEvent> runtimeEvents,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
    {
        if (runtimeEvents is null)
        {
            throw new ArgumentNullException(nameof(runtimeEvents));
        }

        int eventCount;
        try
        {
            eventCount = runtimeEvents.Count;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new ArgumentException(
                "The atomic journal batch did not expose a stable count.",
                nameof(runtimeEvents),
                exception);
        }

        if (eventCount <= 0)
        {
            throw new ArgumentException(
                "An atomic journal batch cannot be empty.",
                nameof(runtimeEvents));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (eventCount > _maxEventsPerRun)
        {
            throw new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxEventsPerRun),
                _maxEventsPerRun,
                eventCount);
        }

        if (eventCount > _maxTotalCommittedEvents)
        {
            throw new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxTotalCommittedEvents),
                _maxTotalCommittedEvents,
                eventCount);
        }

        var eventSnapshots = new RuntimeEvent[eventCount];
        var aggregateEventBytes = 0L;
        for (var index = 0; index < eventCount; index++)
        {
            RuntimeEvent? runtimeEvent;
            try
            {
                runtimeEvent = runtimeEvents[index];
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                throw new ArgumentException(
                    "The atomic journal batch did not match its declared "
                    + "count.",
                    nameof(runtimeEvents),
                    exception);
            }

            if (runtimeEvent is null)
            {
                throw new ArgumentException(
                    "A journal batch cannot contain null events.",
                    nameof(runtimeEvents));
            }
            eventSnapshots[index] = SnapshotEventWithinFrameCapacity(
                runtimeEvent,
                aggregateEventBytes,
                out var serializedEventBytes);
            aggregateEventBytes = SaturatingAdd(
                aggregateEventBytes,
                serializedEventBytes);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            return await AppendBatchUnderLockAsync(
                    eventSnapshots,
                    expectedRunRevision,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ValidateRequiredId(runId, nameof(runId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (!_runStreams.TryGetValue(runId, out var stream))
            {
                return Array.Empty<RuntimeEvent>();
            }

            var events = new RuntimeEvent[stream.Entries.Count];
            for (var index = 0; index < events.Length; index++)
            {
                events[index] = CloneEvent(
                    stream.Entries[index].Record.RuntimeEvent!);
            }

            return events;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RunJournalCursor> GetRunCursorAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(runId, nameof(runId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            return _runStreams.TryGetValue(runId, out var stream)
                ? new RunJournalCursor(
                    runId,
                    stream.NextSequence,
                    stream.Revision)
                : new RunJournalCursor(runId, 0, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<OperationLedgerEntry?> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(operationId, nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            return _operations.TryGetValue(operationId, out var operation)
                ? ToPublicOperation(operation)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<OperationLedgerEntry>>
        ReadPendingOperationsAsync(
            string? runId = null,
            CancellationToken cancellationToken = default)
    {
        if (runId is not null)
        {
            ValidateRequiredId(runId, nameof(runId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            return _operations.Values
                .Where(item =>
                    item.IsPending
                    && (runId is null
                        || string.Equals(
                            item.Request.RunId,
                            runId,
                            StringComparison.Ordinal)))
                .OrderBy(item => item.RequestEntry.Record.RunRevision)
                .ThenBy(
                    item => item.Request.OperationId,
                    StringComparer.Ordinal)
                .Select(ToPublicOperation)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
        RuntimeEvent receiptEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (receiptEvent is null)
        {
            throw new ArgumentNullException(nameof(receiptEvent));
        }

        if (!string.Equals(
                receiptEvent.Kind,
                RuntimeEventKinds.ActionReceived,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Receipt reconciliation requires an "
                + $"'{RuntimeEventKinds.ActionReceived}' event.",
                nameof(receiptEvent));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var receiptSnapshot = SnapshotEventWithinFrameCapacity(
            receiptEvent,
            serializedBytesBeforeEvent: 0,
            out _);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var stream = GetRunStreamOrEmpty(
                GetStreamId(receiptSnapshot.RunId));
            var projection = PrepareProjection(
                receiptSnapshot,
                stream.NextSequence,
                checked(stream.Revision + 1));
            if (projection.Receipt is null)
            {
                throw new ArgumentException(
                    "The event payload is not an action receipt.",
                    nameof(receiptEvent));
            }

            var append = await AppendUnderLockAsync(
                    receiptSnapshot,
                    expectedRunRevision,
                    CancellationToken.None,
                    projection)
                .ConfigureAwait(false);
            var operation = _operations[projection.Receipt.OperationId];
            return new ReceiptReconcileResult(
                append,
                ToPublicOperation(operation));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask FlushAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _stream.FlushAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (_flushToDiskOnAppend)
                {
                    _stream.Flush(flushToDisk: true);
                }
            }
            catch
            {
                _faulted = true;
                throw;
            }
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
            _writerLease.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<JournalAppendResult> AppendUnderLockAsync(
        RuntimeEvent runtimeEvent,
        long? expectedRunRevision,
        CancellationToken cancellationToken,
        EventProjection? preparedProjection = null)
    {
        ValidateRuntimeEvent(runtimeEvent);

        if (_eventsById.TryGetValue(
                runtimeEvent.EventId,
                out var existingEvent))
        {
            if (!EventsAreEquivalent(
                    runtimeEvent,
                    existingEvent.Record.RuntimeEvent!))
            {
                throw new JournalEntryConflictException(
                    $"Event id '{runtimeEvent.EventId}' already refers "
                    + "to different content.");
            }

            return DuplicateResult(existingEvent);
        }

        var streamId = GetStreamId(runtimeEvent.RunId);
        var stream = GetRunStreamOrEmpty(streamId);
        var projection = preparedProjection
                         ?? PrepareProjection(
                             runtimeEvent,
                             stream.NextSequence,
                             checked(stream.Revision + 1));
        if (projection.DuplicateEntry is not null)
        {
            return DuplicateResult(projection.DuplicateEntry);
        }

        if (expectedRunRevision.HasValue
            && expectedRunRevision.Value != stream.Revision)
        {
            throw new RunRevisionConflictException(
                runtimeEvent.RunId ?? GlobalStreamId,
                expectedRunRevision.Value,
                stream.Revision);
        }

        EnsureEventCapacity(stream, additionalEvents: 1);

        var canonicalEvent = runtimeEvent;
        canonicalEvent.Sequence = stream.NextSequence;
        var record = new JournalFrameRecord
        {
            FormatVersion = JournalFormatVersion,
            StreamId = streamId,
            RunSequence = stream.NextSequence,
            RunRevision = checked(stream.Revision + 1),
            RuntimeEvent = canonicalEvent
        };
        await WriteRecordAsync(record, cancellationToken).ConfigureAwait(false);

        var stored = ApplyCommittedRecord(record);
        return new JournalAppendResult(
            stored.Record.RunSequence,
            stored.Record.RunRevision,
            wasDuplicate: false);
    }

    private async ValueTask<IReadOnlyList<JournalAppendResult>>
        AppendBatchUnderLockAsync(
            IReadOnlyList<RuntimeEvent> runtimeEvents,
            long? expectedRunRevision,
            CancellationToken cancellationToken)
    {
        var first = runtimeEvents[0]
                    ?? throw new ArgumentException(
                        "A journal batch cannot contain null events.",
                        nameof(runtimeEvents));
        var streamId = GetStreamId(first.RunId);
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var receiptOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var stagedReceipts = new Dictionary<string, ActionReceipt>(
            StringComparer.Ordinal);
        var stagedCheckpoints = new Dictionary<string, AgentRun>(
            StringComparer.Ordinal);
        var results = new JournalAppendResult?[runtimeEvents.Count];
        var newEvents = new List<(int Index, RuntimeEvent Event)>(
            runtimeEvents.Count);
        var stream = GetRunStreamOrEmpty(streamId);

        for (var index = 0; index < runtimeEvents.Count; index++)
        {
            var runtimeEvent = runtimeEvents[index]
                               ?? throw new ArgumentException(
                                   "A journal batch cannot contain null events.",
                                   nameof(runtimeEvents));
            ValidateRuntimeEvent(runtimeEvent);
            if (!string.Equals(
                    GetStreamId(runtimeEvent.RunId),
                    streamId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Every event in an atomic batch must belong to one run.",
                    nameof(runtimeEvents));
            }

            if (!eventIds.Add(runtimeEvent.EventId))
            {
                throw new JournalEntryConflictException(
                    $"Event id '{runtimeEvent.EventId}' occurs more than once "
                    + "in the same atomic batch.");
            }

            if (_eventsById.TryGetValue(
                    runtimeEvent.EventId,
                    out var existingEvent))
            {
                if (!EventsAreEquivalent(
                        runtimeEvent,
                        existingEvent.Record.RuntimeEvent!))
                {
                    throw new JournalEntryConflictException(
                        $"Event id '{runtimeEvent.EventId}' already refers "
                        + "to different content.");
                }

                results[index] = DuplicateResult(existingEvent);
                continue;
            }

            var projection = PrepareProjection(
                runtimeEvent,
                checked(stream.NextSequence + newEvents.Count),
                checked(stream.Revision + newEvents.Count + 1),
                stagedReceipts,
                stagedCheckpoints);
            if (projection.DuplicateEntry is not null)
            {
                results[index] = DuplicateResult(projection.DuplicateEntry);
                continue;
            }

            if (projection.Request is not null
                && !operationIds.Add(projection.Request.OperationId))
            {
                throw new OperationLedgerConflictException(
                    projection.Request.OperationId,
                    "an atomic batch contains the operation more than once.");
            }

            if (projection.Receipt is not null
                && !receiptOperationIds.Add(projection.Receipt.OperationId))
            {
                throw new OperationLedgerConflictException(
                    projection.Receipt.OperationId,
                    "an atomic batch contains more than one new receipt for "
                    + "the operation.");
            }

            if (projection.Receipt is not null)
            {
                stagedReceipts.Add(
                    projection.Receipt.OperationId,
                    projection.Receipt);
            }

            if (projection.Checkpoint is not null)
            {
                stagedCheckpoints[projection.Checkpoint.RunId] =
                    projection.Checkpoint;
            }

            newEvents.Add((index, runtimeEvent));
        }

        if (newEvents.Count == 0)
        {
            return results
                .Select(item => item!)
                .ToArray();
        }

        if (newEvents.Count != runtimeEvents.Count)
        {
            throw new JournalEntryConflictException(
                "An atomic journal batch cannot mix duplicate and "
                + "new events.");
        }

        if (expectedRunRevision.HasValue
            && expectedRunRevision.Value != stream.Revision)
        {
            throw new RunRevisionConflictException(
                first.RunId ?? GlobalStreamId,
                expectedRunRevision.Value,
                stream.Revision);
        }

        EnsureEventCapacity(stream, newEvents.Count);

        var startSequence = stream.NextSequence;
        var startRevision = checked(stream.Revision + 1);
        var canonicalEvents = new List<RuntimeEvent>(newEvents.Count);
        for (var offset = 0; offset < newEvents.Count; offset++)
        {
            var canonical = newEvents[offset].Event;
            canonical.Sequence = checked(startSequence + offset);
            canonicalEvents.Add(canonical);
        }

        var frameRecord = new JournalFrameRecord
        {
            FormatVersion = JournalFormatVersion,
            StreamId = streamId,
            RunSequence = startSequence,
            RunRevision = startRevision,
            RuntimeEvents = canonicalEvents
        };
        await WriteRecordAsync(frameRecord, cancellationToken)
            .ConfigureAwait(false);

        for (var offset = 0; offset < canonicalEvents.Count; offset++)
        {
            var record = new JournalFrameRecord
            {
                FormatVersion = JournalFormatVersion,
                StreamId = streamId,
                RunSequence = checked(startSequence + offset),
                RunRevision = checked(startRevision + offset),
                RuntimeEvent = canonicalEvents[offset]
            };
            var stored = ApplyCommittedRecord(record);
            results[newEvents[offset].Index] = new JournalAppendResult(
                stored.Record.RunSequence,
                stored.Record.RunRevision,
                wasDuplicate: false);
        }

        return results
            .Select(item => item!)
            .ToArray();
    }

    private async ValueTask WriteRecordAsync(
        JournalFrameRecord record,
        CancellationToken cancellationToken)
    {
        using var payload = BoundedJsonPayload.Serialize(
            record,
            PersistenceJsonContext.Default.JournalFrameRecord,
            _maxFramePayloadBytes,
            attempted => new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxFramePayloadBytes),
                _maxFramePayloadBytes,
                attempted));
        var frame = BuildFrame(payload.WrittenSpan);
        EnsureJournalByteCapacity(frame.Length);
        await WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteFrameAsync(
        byte[] frame,
        CancellationToken cancellationToken)
    {
        var bytesToWrite = _faultInjector?.GetWriteLength(frame.Length)
            ?? frame.Length;
        if (bytesToWrite < 0 || bytesToWrite > frame.Length)
        {
            throw new InvalidOperationException(
                "A journal fault injector returned an invalid write length.");
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
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _faultInjector?.OnWriteStage(
                JournalWriteStage.AfterWrite,
                bytesToWrite,
                frame.Length);

            if (bytesToWrite != frame.Length)
            {
                throw new IOException(
                    $"Only {bytesToWrite} of {frame.Length} journal frame "
                    + "bytes were written.");
            }

            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_flushToDiskOnAppend)
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
                throw new JournalCapacityExceededException(
                    nameof(FileJournalOptions.MaxFramePayloadBytes),
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

            JournalFrameRecord record;
            try
            {
                record = JsonSerializer.Deserialize(
                        payload,
                        PersistenceJsonContext.Default.JournalFrameRecord)
                    ?? throw new JsonException("Frame payload is null.");
                ApplyRecoveredFrame(record);
            }
            catch (Exception exception) when (
                exception is not JournalCapacityExceededException
                && (exception is JsonException
                    or ArgumentException
                    or InvalidOperationException
                    or OverflowException))
            {
                throw Corrupt(frameOffset, exception.Message);
            }

            lastCommittedOffset = checked(frameOffset + totalFrameLength);
        }

        _stream.Position = _stream.Length;
    }

    private void ApplyRecoveredFrame(JournalFrameRecord frame)
    {
        if (frame.FormatVersion < MinimumJournalFormatVersion
            || frame.FormatVersion > JournalFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported journal format version "
                + $"'{frame.FormatVersion}'.");
        }

        var runtimeEvents = frame.RuntimeEvents;
        if (frame.RuntimeEvent is not null)
        {
            if (runtimeEvents is not null)
            {
                throw new InvalidOperationException(
                    "A journal frame cannot contain both a single event "
                    + "and an event batch.");
            }

            runtimeEvents = new List<RuntimeEvent> { frame.RuntimeEvent };
        }

        if (runtimeEvents is null || runtimeEvents.Count == 0)
        {
            throw new InvalidOperationException(
                "A journal frame does not contain any runtime events.");
        }

        for (var index = 0; index < runtimeEvents.Count; index++)
        {
            var record = new JournalFrameRecord
            {
                FormatVersion = frame.FormatVersion,
                StreamId = frame.StreamId,
                RunSequence = checked(frame.RunSequence + index),
                RunRevision = checked(frame.RunRevision + index),
                RuntimeEvent = runtimeEvents[index]
            };
            ValidateRecoveredRecord(record);
            _ = ApplyCommittedRecord(record);
        }
    }

    private void ValidateRecoveredRecord(JournalFrameRecord record)
    {
        var runtimeEvent = record.RuntimeEvent
                           ?? throw new InvalidOperationException(
                               "A recovered journal record is missing its "
                               + "runtime event.");
        ValidateRuntimeEvent(runtimeEvent);
        var expectedStreamId = GetStreamId(runtimeEvent.RunId);
        if (!string.Equals(
                record.StreamId,
                expectedStreamId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Journal stream id does not match the runtime event.");
        }

        var stream = GetRunStreamOrEmpty(record.StreamId);
        if (record.RunSequence != stream.NextSequence
            || runtimeEvent.Sequence != record.RunSequence)
        {
            throw new InvalidOperationException(
                $"Run sequence is not contiguous. Expected "
                + $"{stream.NextSequence}, found {record.RunSequence}.");
        }

        if (record.RunRevision != stream.Revision + 1)
        {
            throw new InvalidOperationException(
                $"Run revision is not contiguous. Expected "
                + $"{stream.Revision + 1}, found {record.RunRevision}.");
        }

        if (_eventsById.ContainsKey(runtimeEvent.EventId))
        {
            throw new InvalidOperationException(
                $"Duplicate committed event id "
                + $"'{runtimeEvent.EventId}'.");
        }

        EnsureEventCapacity(stream, additionalEvents: 1);

        var projection = PrepareProjection(
            runtimeEvent,
            record.RunSequence,
            record.RunRevision,
            allowLegacyReconcilingDurationCheckpoint:
                record.FormatVersion
                <= LegacyCheckpointLifecycleFormatVersion);
        if (projection.DuplicateEntry is not null)
        {
            throw new InvalidOperationException(
                "The journal contains a duplicate committed operation entry.");
        }
    }

    private StoredJournalEntry ApplyCommittedRecord(JournalFrameRecord record)
    {
        var runtimeEvent = record.RuntimeEvent
                           ?? throw new InvalidOperationException(
                               "A committed journal record is missing its "
                               + "runtime event.");
        var stream = GetOrCreateRunStream(record.StreamId);
        var stored = new StoredJournalEntry(record);
        stream.Entries.Add(stored);
        stream.NextSequence = checked(record.RunSequence + 1);
        stream.Revision = record.RunRevision;
        _eventsById.Add(runtimeEvent.EventId, stored);
        _totalCommittedEvents = checked(_totalCommittedEvents + 1);

        if (RunCheckpointLifecycleValidator.IsCheckpointKind(
                runtimeEvent.Kind))
        {
            var checkpoint = ValidateRunCheckpoint(
                runtimeEvent,
                record.RunSequence,
                record.RunRevision,
                allowLegacyReconcilingDurationCheckpoint:
                    record.FormatVersion
                    <= LegacyCheckpointLifecycleFormatVersion);
            _runCheckpoints[checkpoint.RunId] = checkpoint;
        }
        else if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.ActionRequested,
                StringComparison.Ordinal))
        {
            var request = DeserializeActionRequest(runtimeEvent);
            _operations.Add(
                request.OperationId,
                new StoredOperation(request, stored));
        }
        else if (string.Equals(
                     runtimeEvent.Kind,
                     RuntimeEventKinds.ActionReceived,
                     StringComparison.Ordinal))
        {
            var receipt = DeserializeActionReceipt(runtimeEvent);
            var operation = _operations[receipt.OperationId];
            operation.Receipts.Add(
                receipt.Revision,
                new StoredReceipt(receipt, stored));
            if (operation.LatestReceipt is null
                || receipt.Revision
                > operation.LatestReceipt.Receipt.Revision)
            {
                operation.LatestReceipt =
                    operation.Receipts[receipt.Revision];
            }
        }

        return stored;
    }

    private EventProjection PrepareProjection(
        RuntimeEvent runtimeEvent,
        long projectedSequence,
        long projectedRevision,
        IReadOnlyDictionary<string, ActionReceipt>? stagedReceipts = null,
        IReadOnlyDictionary<string, AgentRun>? stagedCheckpoints = null,
        bool allowLegacyReconcilingDurationCheckpoint = false)
    {
        if (RunCheckpointLifecycleValidator.IsCheckpointKind(
                runtimeEvent.Kind))
        {
            AgentRun? previous = null;
            if (stagedCheckpoints?.TryGetValue(
                    runtimeEvent.RunId!,
                    out var staged) == true)
            {
                previous = staged;
            }
            else
            {
                _runCheckpoints.TryGetValue(
                    runtimeEvent.RunId!,
                    out previous);
            }

            return EventProjection.ForCheckpoint(
                ValidateRunCheckpoint(
                    runtimeEvent,
                    projectedSequence,
                    projectedRevision,
                    previous,
                    allowLegacyReconcilingDurationCheckpoint));
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.ActionRequested,
                StringComparison.Ordinal))
        {
            var request = DeserializeActionRequest(runtimeEvent);
            if (!string.Equals(
                    request.RunId,
                    runtimeEvent.RunId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.TurnId,
                    runtimeEvent.TurnId,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    request.OperationId,
                    "request identity does not match the journal event.");
            }

            var checkpoint = RequireRunCheckpoint(
                request.RunId,
                request.OperationId);
            if (!string.Equals(
                    request.AgentId,
                    checkpoint.AgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.WorldId,
                    checkpoint.WorldId,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    request.OperationId,
                    "request agent or world does not match the durable run.");
            }

            if (_operations.TryGetValue(
                    request.OperationId,
                    out var existing))
            {
                if (RequestsAreEquivalent(
                        request,
                        existing.Request))
                {
                    return EventProjection.DuplicateRequest(
                        request,
                        existing.RequestEntry);
                }

                throw new OperationLedgerConflictException(
                    request.OperationId,
                    "a different request is already durable.");
            }

            return EventProjection.ForRequest(request);
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.ActionOutcomeUncertain,
                StringComparison.Ordinal))
        {
            var uncertainty = DeserializeActionReceipt(runtimeEvent);
            if (!string.Equals(
                    uncertainty.Status,
                    ReceiptStatuses.Unknown,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    uncertainty.OperationId,
                    "an action uncertainty must have unknown status.");
            }

            if (!_operations.TryGetValue(
                    uncertainty.OperationId,
                    out var uncertainOperation))
            {
                throw new OperationLedgerConflictException(
                    uncertainty.OperationId,
                    "no durable action request exists.");
            }

            if (!string.Equals(
                    uncertainOperation.Request.RunId,
                    runtimeEvent.RunId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    uncertainOperation.Request.TurnId,
                    runtimeEvent.TurnId,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    uncertainty.OperationId,
                    "action uncertainty identity does not match its request.");
            }

            if (!uncertainOperation.IsPending
                || uncertainOperation.LatestReceipt is not null)
            {
                throw new OperationLedgerConflictException(
                    uncertainty.OperationId,
                    "action uncertainty cannot follow an authoritative receipt.");
            }

            _ = ActionReceiptIngressValidator.ValidateAndClone(
                uncertainOperation.Request,
                uncertainty,
                RequireRunCheckpoint(
                    uncertainOperation.Request.RunId,
                    uncertainty.OperationId));
            return EventProjection.None;
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.ActionReceived,
                StringComparison.Ordinal))
        {
            var receipt = DeserializeActionReceipt(runtimeEvent);

            if (!_operations.TryGetValue(
                    receipt.OperationId,
                    out var operation))
            {
                throw new OperationLedgerConflictException(
                    receipt.OperationId,
                    "no durable action request exists.");
            }

            if (!string.Equals(
                    operation.Request.RunId,
                    runtimeEvent.RunId,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    receipt.OperationId,
                    "receipt event runId does not match its request.");
            }

            if (!string.Equals(
                    operation.Request.TurnId,
                    runtimeEvent.TurnId,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    receipt.OperationId,
                    "receipt event turnId does not match its request.");
            }

            receipt = ActionReceiptIngressValidator.ValidateAndClone(
                operation.Request,
                receipt,
                RequireRunCheckpoint(
                    operation.Request.RunId,
                    receipt.OperationId));

            if (operation.Receipts.TryGetValue(
                    receipt.Revision,
                    out var sameRevision))
            {
                if (ReceiptsAreEquivalent(
                        receipt,
                        sameRevision.Receipt))
                {
                    return EventProjection.DuplicateReceipt(
                        receipt,
                        sameRevision.Entry);
                }

                throw new OperationLedgerConflictException(
                    receipt.OperationId,
                    $"revision {receipt.Revision} already has different "
                    + "receipt content.");
            }

            if (operation.LatestReceipt is not null
                && receipt.Revision
                < operation.LatestReceipt.Receipt.Revision)
            {
                throw new OperationLedgerConflictException(
                    receipt.OperationId,
                    $"receipt revision {receipt.Revision} is older than "
                    + $"{operation.LatestReceipt.Receipt.Revision}.");
            }

            if (operation.LatestReceipt is not null
                && !operation.IsPending
                && string.Equals(
                    receipt.Status,
                    ReceiptStatuses.Unknown,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    receipt.OperationId,
                    "a terminal operation cannot regress to unknown.");
            }

            return EventProjection.ForReceipt(receipt);
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.ToolCompleted,
                StringComparison.Ordinal)
            || string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.ToolFailed,
                StringComparison.Ordinal))
        {
            ValidateTerminalReceiptEvent(runtimeEvent, stagedReceipts);
        }

        return EventProjection.None;
    }

    private void ValidateTerminalReceiptEvent(
        RuntimeEvent runtimeEvent,
        IReadOnlyDictionary<string, ActionReceipt>? stagedReceipts)
    {
        var receipt = DeserializeActionReceipt(runtimeEvent);
        if (!_operations.TryGetValue(
                receipt.OperationId,
                out var operation))
        {
            throw new OperationLedgerConflictException(
                receipt.OperationId,
                "no durable action request exists.");
        }

        if (!string.Equals(
                operation.Request.RunId,
                runtimeEvent.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.Request.TurnId,
                runtimeEvent.TurnId,
                StringComparison.Ordinal))
        {
            throw new OperationLedgerConflictException(
                receipt.OperationId,
                "terminal receipt identity does not match its request.");
        }

        receipt = ActionReceiptIngressValidator.ValidateAndClone(
            operation.Request,
            receipt,
            RequireRunCheckpoint(
                operation.Request.RunId,
                receipt.OperationId));
        ActionReceipt? receivedReceipt = null;
        if (operation.Receipts.TryGetValue(
                receipt.Revision,
                out var received))
        {
            receivedReceipt = received.Receipt;
        }
        else if (stagedReceipts is not null
                 && stagedReceipts.TryGetValue(
                     receipt.OperationId,
                     out var staged)
                 && staged.Revision == receipt.Revision)
        {
            receivedReceipt = staged;
        }

        if (receivedReceipt is null
            || !ReceiptsAreEquivalent(receipt, receivedReceipt))
        {
            throw new OperationLedgerConflictException(
                receipt.OperationId,
                "terminal receipt does not match a durable received receipt.");
        }

        var statusMatchesKind =
            string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.ToolFailed,
                StringComparison.Ordinal)
                ? string.Equals(
                    receipt.Status,
                    ReceiptStatuses.Failed,
                    StringComparison.Ordinal)
                : string.Equals(
                      receipt.Status,
                      ReceiptStatuses.Succeeded,
                      StringComparison.Ordinal)
                  || string.Equals(
                      receipt.Status,
                      ReceiptStatuses.Rejected,
                      StringComparison.Ordinal);
        if (!statusMatchesKind)
        {
            throw new OperationLedgerConflictException(
                receipt.OperationId,
                "terminal receipt status does not match the event kind.");
        }
    }

    private AgentRun RequireRunCheckpoint(
        string runId,
        string operationId)
    {
        if (_runCheckpoints.TryGetValue(runId, out var checkpoint))
        {
            return checkpoint;
        }

        throw new OperationLedgerConflictException(
            operationId,
            "no durable run checkpoint exists.");
    }

    private AgentRun ValidateRunCheckpoint(
        RuntimeEvent runtimeEvent,
        long projectedSequence,
        long projectedRevision,
        AgentRun? previous = null,
        bool allowLegacyReconcilingDurationCheckpoint = false)
    {
        try
        {
            if (previous is null)
            {
                _runCheckpoints.TryGetValue(
                    runtimeEvent.RunId!,
                    out previous);
            }

            return RunCheckpointLifecycleValidator.ValidateAndClone(
                runtimeEvent,
                previous,
                projectedSequence,
                projectedRevision,
                allowLegacyReconcilingDurationCheckpoint);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
            or InvalidOperationException)
        {
            throw new ArgumentException(
                $"Event '{runtimeEvent.EventId}' has an invalid "
                + $"{nameof(AgentRun)} checkpoint payload.",
                nameof(runtimeEvent),
                exception);
        }
    }

    private static bool IsKnownReceiptStatus(string status)
    {
        return string.Equals(
                   status,
                   ReceiptStatuses.Succeeded,
                   StringComparison.Ordinal)
               || string.Equals(
                   status,
                   ReceiptStatuses.Rejected,
                   StringComparison.Ordinal)
               || string.Equals(
                   status,
                   ReceiptStatuses.Failed,
                   StringComparison.Ordinal)
               || string.Equals(
                   status,
                   ReceiptStatuses.Unknown,
                   StringComparison.Ordinal);
    }

    private static ActionRequest DeserializeActionRequest(
        RuntimeEvent runtimeEvent)
    {
        try
        {
            var request = ProtocolJson.DeserializeActionRequest(
                runtimeEvent.Payload.GetRawText());
            ProtocolValidator.EnsureValid(request);
            return request;
        }
        catch (Exception exception) when (
            exception is JsonException
            or InvalidOperationException)
        {
            throw new ArgumentException(
                $"Event '{runtimeEvent.EventId}' has an invalid "
                + $"{nameof(ActionRequest)} payload.",
                nameof(runtimeEvent),
                exception);
        }
    }

    private static ActionReceipt DeserializeActionReceipt(
        RuntimeEvent runtimeEvent)
    {
        try
        {
            var receipt = ProtocolJson.DeserializeActionReceipt(
                runtimeEvent.Payload.GetRawText());
            ProtocolValidator.EnsureValid(receipt);
            return receipt;
        }
        catch (Exception exception) when (
            exception is JsonException
            or InvalidOperationException)
        {
            throw new ArgumentException(
                $"Event '{runtimeEvent.EventId}' has an invalid "
                + $"{nameof(ActionReceipt)} payload.",
                nameof(runtimeEvent),
                exception);
        }
    }

    private static OperationLedgerEntry ToPublicOperation(
        StoredOperation operation)
    {
        return new OperationLedgerEntry(
            CloneActionRequest(operation.Request),
            operation.LatestReceipt is null
                ? null
                : CloneActionReceipt(operation.LatestReceipt.Receipt),
            operation.RequestEntry.Record.RunSequence,
            operation.RequestEntry.Record.RunRevision,
            operation.LatestReceipt?.Entry.Record.RunSequence,
            operation.LatestReceipt?.Entry.Record.RunRevision);
    }

    private static JournalAppendResult DuplicateResult(
        StoredJournalEntry entry)
    {
        return new JournalAppendResult(
            entry.Record.RunSequence,
            entry.Record.RunRevision,
            wasDuplicate: true);
    }

    private static bool EventsAreEquivalent(
        RuntimeEvent candidate,
        RuntimeEvent existing)
    {
        var canonicalCandidate = CloneEvent(candidate);
        canonicalCandidate.Sequence = existing.Sequence;
        canonicalCandidate.Timestamp = existing.Timestamp;
        if (AllowsAttemptIdentityRebinding(candidate)
            && AllowsAttemptIdentityRebinding(existing))
        {
            canonicalCandidate.AttemptId = existing.AttemptId;
            canonicalCandidate.StreamAttemptId = existing.StreamAttemptId;
        }

        if (IsReceiptEvent(candidate)
            && string.Equals(
                candidate.Kind,
                existing.Kind,
                StringComparison.Ordinal))
        {
            try
            {
                if (!ReceiptsAreEquivalent(
                        DeserializeActionReceipt(candidate),
                        DeserializeActionReceipt(existing)))
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            canonicalCandidate.Payload = existing.Payload.Clone();
        }

        return string.Equals(
            ProtocolJson.Serialize(canonicalCandidate),
            ProtocolJson.Serialize(existing),
            StringComparison.Ordinal);
    }

    private static bool IsReceiptEvent(RuntimeEvent runtimeEvent)
    {
        return string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ActionReceived,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ActionOutcomeUncertain,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ToolCompleted,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ToolFailed,
                   StringComparison.Ordinal);
    }

    private static bool AllowsAttemptIdentityRebinding(
        RuntimeEvent runtimeEvent)
    {
        return string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.TranscriptMessage,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ActionRequested,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ActionReceived,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ActionOutcomeUncertain,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ToolCompleted,
                   StringComparison.Ordinal)
               || string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.ToolFailed,
                   StringComparison.Ordinal);
    }

    private static bool RequestsAreEquivalent(
        ActionRequest left,
        ActionRequest right)
    {
        return string.Equals(
            ProtocolJson.Serialize(left),
            ProtocolJson.Serialize(right),
            StringComparison.Ordinal);
    }

    private static RuntimeEvent CloneEvent(RuntimeEvent runtimeEvent)
    {
        return ProtocolJson.DeserializeRuntimeEvent(
            ProtocolJson.Serialize(runtimeEvent));
    }

    private RuntimeEvent SnapshotEventWithinFrameCapacity(
        RuntimeEvent runtimeEvent,
        long serializedBytesBeforeEvent,
        out int serializedEventBytes)
    {
        var remainingCapacity = serializedBytesBeforeEvent
                                >= _maxFramePayloadBytes
            ? 0
            : _maxFramePayloadBytes
              - checked((int)serializedBytesBeforeEvent);
        using var payload = BoundedJsonPayload.Serialize(
            runtimeEvent,
            ProtocolJsonContext.Default.RuntimeEvent,
            remainingCapacity,
            attempted => new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxFramePayloadBytes),
                _maxFramePayloadBytes,
                SaturatingAdd(
                    serializedBytesBeforeEvent,
                    attempted)));
        serializedEventBytes = payload.WrittenCount;
        var snapshot = JsonSerializer.Deserialize(
                           payload.WrittenSpan,
                           ProtocolJsonContext.Default.RuntimeEvent)
                       ?? throw new JsonException(
                           "The snapshotted runtime event is null.");
        ValidateRuntimeEvent(snapshot);
        return snapshot;
    }

    private static bool ReceiptsAreEquivalent(
        ActionReceipt left,
        ActionReceipt right)
    {
        var canonicalLeft = CloneActionReceipt(left);
        canonicalLeft.ReceivedAt = right.ReceivedAt;
        return string.Equals(
            ProtocolJson.Serialize(canonicalLeft),
            ProtocolJson.Serialize(right),
            StringComparison.Ordinal);
    }

    private static ActionRequest CloneActionRequest(ActionRequest request)
    {
        return ProtocolJson.DeserializeActionRequest(
            ProtocolJson.Serialize(request));
    }

    private static ActionReceipt CloneActionReceipt(ActionReceipt receipt)
    {
        return ProtocolJson.DeserializeActionReceipt(
            ProtocolJson.Serialize(receipt));
    }

    private RunStreamState GetOrCreateRunStream(string streamId)
    {
        if (!_runStreams.TryGetValue(streamId, out var stream))
        {
            stream = new RunStreamState();
            _runStreams.Add(streamId, stream);
        }

        return stream;
    }

    private RunStreamState GetRunStreamOrEmpty(string streamId)
    {
        return _runStreams.TryGetValue(streamId, out var stream)
            ? stream
            : new RunStreamState();
    }

    private static string GetStreamId(string? runId)
    {
        return string.IsNullOrEmpty(runId) ? GlobalStreamId : runId;
    }

    private static void ValidateRuntimeEvent(RuntimeEvent runtimeEvent)
    {
        try
        {
            ProtocolValidator.EnsureValid(runtimeEvent);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Runtime event does not satisfy the public wire contract.",
                nameof(runtimeEvent),
                exception);
        }
    }

    private static void ValidateRequiredId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{parameterName} is required.",
                parameterName);
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileSessionStore));
        }

        if (_faulted)
        {
            throw new JournalFaultedException(_path);
        }
    }

    private void EnsureExistingJournalCapacity()
    {
        if (_stream.Length > _maxJournalBytes)
        {
            throw new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxJournalBytes),
                _maxJournalBytes,
                _stream.Length);
        }
    }

    private void EnsureJournalByteCapacity(int frameLength)
    {
        var attemptedLength = SaturatingAdd(_stream.Length, frameLength);
        if (attemptedLength > _maxJournalBytes)
        {
            throw new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxJournalBytes),
                _maxJournalBytes,
                attemptedLength);
        }
    }

    private void EnsureEventCapacity(
        RunStreamState stream,
        int additionalEvents)
    {
        var attemptedTotal = SaturatingAdd(
            _totalCommittedEvents,
            additionalEvents);
        if (attemptedTotal > _maxTotalCommittedEvents)
        {
            throw new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxTotalCommittedEvents),
                _maxTotalCommittedEvents,
                attemptedTotal);
        }

        var attemptedRunEvents = SaturatingAdd(
            stream.Entries.Count,
            additionalEvents);
        if (attemptedRunEvents > _maxEventsPerRun)
        {
            throw new JournalCapacityExceededException(
                nameof(FileJournalOptions.MaxEventsPerRun),
                _maxEventsPerRun,
                attemptedRunEvents);
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

    private JournalCorruptionException Corrupt(long offset, string message)
    {
        return new JournalCorruptionException(_path, offset, message);
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

    private sealed class StoredJournalEntry
    {
        public StoredJournalEntry(JournalFrameRecord record)
        {
            Record = record;
        }

        public JournalFrameRecord Record { get; }
    }

    private sealed class RunStreamState
    {
        public long NextSequence { get; set; }

        public long Revision { get; set; }

        public List<StoredJournalEntry> Entries { get; } = new();
    }

    private sealed class StoredOperation
    {
        public StoredOperation(
            ActionRequest request,
            StoredJournalEntry requestEntry)
        {
            Request = request;
            RequestEntry = requestEntry;
        }

        public ActionRequest Request { get; }

        public StoredJournalEntry RequestEntry { get; }

        public Dictionary<long, StoredReceipt> Receipts { get; } = new();

        public StoredReceipt? LatestReceipt { get; set; }

        public bool IsPending =>
            LatestReceipt is null
            || string.Equals(
                LatestReceipt.Receipt.Status,
                ReceiptStatuses.Unknown,
                StringComparison.Ordinal);
    }

    private sealed class StoredReceipt
    {
        public StoredReceipt(
            ActionReceipt receipt,
            StoredJournalEntry entry)
        {
            Receipt = receipt;
            Entry = entry;
        }

        public ActionReceipt Receipt { get; }

        public StoredJournalEntry Entry { get; }
    }

    private sealed class EventProjection
    {
        private EventProjection()
        {
        }

        public static EventProjection None { get; } = new();

        public ActionRequest? Request { get; private set; }

        public ActionReceipt? Receipt { get; private set; }

        public AgentRun? Checkpoint { get; private set; }

        public StoredJournalEntry? DuplicateEntry { get; private set; }

        public static EventProjection ForCheckpoint(AgentRun checkpoint)
        {
            return new EventProjection { Checkpoint = checkpoint };
        }

        public static EventProjection ForRequest(ActionRequest request)
        {
            return new EventProjection { Request = request };
        }

        public static EventProjection DuplicateRequest(
            ActionRequest request,
            StoredJournalEntry duplicateEntry)
        {
            return new EventProjection
            {
                Request = request,
                DuplicateEntry = duplicateEntry
            };
        }

        public static EventProjection ForReceipt(ActionReceipt receipt)
        {
            return new EventProjection { Receipt = receipt };
        }

        public static EventProjection DuplicateReceipt(
            ActionReceipt receipt,
            StoredJournalEntry duplicateEntry)
        {
            return new EventProjection
            {
                Receipt = receipt,
                DuplicateEntry = duplicateEntry
            };
        }
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
