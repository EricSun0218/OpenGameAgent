using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Persistence;

/// <summary>
/// Single-writer, append-only store for verified world presentations.
/// Checksummed frames and a digest chain make torn tails recoverable and
/// committed-prefix corruption fail closed.
/// </summary>
public sealed class FileWorldPresentationStore :
    IWorldPresentationStore,
    IDisposable,
    IAsyncDisposable
{
    internal const int FrameHeaderSize = 12;
    internal const int FrameFooterSize = 4;

    private const uint FrameMagic = 0x31525057;
    private const uint CommitMagic = 0x54494D43;
    private const int FormatVersion = 1;
    private const string GenesisFrameDigest =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly string _path;
    private readonly ExclusiveFileWriterLease _writerLease;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<VerifiedWorldPresentation>>
        _historiesByScopedId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VerifiedWorldPresentation>>
        _recordsByAudiencePosting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CursorPosition> _cursorPositions =
        new(StringComparer.Ordinal);
    private readonly WorldPresentationLimits _limits;
    private readonly int _maxFramePayloadBytes;
    private readonly long _maxLogBytes;
    private readonly long _maxRecords;
    private readonly int _maxFrameJsonTokens;
    private readonly long _maxResidentBytes;
    private readonly bool _flushToDiskOnMutation;
    private readonly IJournalFaultInjector? _faultInjector;

    private long _storeRevision;
    private long _residentBytes;
    private string _lastFrameDigest = GenesisFrameDigest;
    private bool _faulted;
    private bool _disposed;

    public FileWorldPresentationStore(
        string path,
        FileWorldPresentationStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A world-presentation store path is required.",
                nameof(path));
        }

        options ??= new FileWorldPresentationStoreOptions();
        _limits = options.Limits
                  ?? throw new ArgumentNullException(
                      nameof(options),
                      "World-presentation limits are required.");
        if (options.MaxFramePayloadBytes is < 1_024
            or > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFramePayloadBytes must be between 1 KiB and 64 MiB.");
        }

        if (options.MaxLogBytes
            < options.MaxFramePayloadBytes
            + (long)FrameHeaderSize
            + FrameFooterSize
            || options.MaxLogBytes > 4L * 1_099_511_627_776)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxLogBytes must fit at least one maximum-size frame and "
                + "cannot exceed 4 TiB.");
        }

        if (options.MaxRecords is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxRecords must be between 1 and 10,000,000.");
        }

        if (options.MaxFrameJsonTokens is < 1_024 or > 4_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFrameJsonTokens must be between 1,024 and "
                + "4,000,000.");
        }

        var minimumResidentBytes = checked(
            options.MaxFramePayloadBytes * 4L
            + 4_096
            + _limits.MaxAudienceMembers * 2_048L);
        if (options.MaxResidentBytes < minimumResidentBytes
            || options.MaxResidentBytes > 16L * 1_073_741_824)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxResidentBytes must fit one conservatively estimated "
                + "maximum frame and cannot exceed 16 GiB.");
        }

        _path = System.IO.Path.GetFullPath(path);
        _maxFramePayloadBytes = options.MaxFramePayloadBytes;
        _maxLogBytes = options.MaxLogBytes;
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
                        FileWorldPresentationStoreOptions.MaxLogBytes),
                    _maxLogBytes,
                    _stream.Length);
            }

            Recover();
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
                return checked((int)_storeRevision);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public long EstimatedResidentBytes =>
        Interlocked.Read(ref _residentBytes);

    public async ValueTask<WorldPresentationPublishResult>
        PublishVerifiedAsync(
            VerifiedWorldPresentation presentation,
            long expectedPreviousContentRevision,
            CancellationToken cancellationToken = default)
    {
        if (presentation is null)
        {
            throw new ArgumentNullException(nameof(presentation));
        }

        if (expectedPreviousContentRevision < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedPreviousContentRevision));
        }

        ValidateAgainstStoreLimits(presentation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var historyKey = ScopedPresentationKey(
                presentation.Binding,
                presentation.PresentationId);
            if (_historiesByScopedId.TryGetValue(
                    historyKey,
                    out var history))
            {
                var sameRevision =
                    presentation.ContentRevision < history.Count
                        ? history[checked(
                            (int)presentation.ContentRevision)]
                        : null;
                if (sameRevision is not null)
                {
                    return new WorldPresentationPublishResult(
                        string.Equals(
                            sameRevision.SemanticDigest,
                            presentation.SemanticDigest,
                            StringComparison.Ordinal)
                            ? WorldPresentationWriteStatuses.Idempotent
                            : WorldPresentationWriteStatuses
                                .PresentationConflict,
                        history[^1].ContentRevision,
                        string.Equals(
                            sameRevision.SemanticDigest,
                            presentation.SemanticDigest,
                            StringComparison.Ordinal)
                            ? sameRevision
                            : null);
                }

                var current = history[^1];
                if (current.ContentRevision
                    != expectedPreviousContentRevision
                    || presentation.ContentRevision
                    != checked(current.ContentRevision + 1))
                {
                    return new WorldPresentationPublishResult(
                        WorldPresentationWriteStatuses.RevisionConflict,
                        current.ContentRevision);
                }

                if (!IsSamePresentationStream(current, presentation))
                {
                    return new WorldPresentationPublishResult(
                        WorldPresentationWriteStatuses
                            .PresentationConflict,
                        current.ContentRevision);
                }
            }
            else if (expectedPreviousContentRevision != -1
                     || presentation.ContentRevision != 0)
            {
                return new WorldPresentationPublishResult(
                    WorldPresentationWriteStatuses.RevisionConflict,
                    currentContentRevision: -1);
            }

            var nextSequence = checked(_storeRevision + 1);
            if (nextSequence > _maxRecords)
            {
                throw Capacity(
                    nameof(FileWorldPresentationStoreOptions.MaxRecords),
                    _maxRecords,
                    nextSequence);
            }

            var committed = presentation.WithSequence(nextSequence);
            var residentBytes = await PersistAsync(
                    committed,
                    cancellationToken)
                .ConfigureAwait(false);
            AddCommitted(committed, residentBytes);
            return new WorldPresentationPublishResult(
                WorldPresentationWriteStatuses.Applied,
                committed.ContentRevision,
                committed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<WorldPresentationProjection?> ReadLatestAsync(
        string presentationId,
        WorldPresentationQuery query,
        CancellationToken cancellationToken = default)
    {
        var admittedId = RequiredPresentationId(
            presentationId,
            nameof(presentationId));
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var admittedBinding = WorldPresentationValidation.CloneBinding(
            query.Binding);
        var admittedGrant = WorldPresentationValidation.CloneGrant(
            query.Grant);
        VerifiedWorldPresentation? latest = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var historyKey = ScopedPresentationKey(
                admittedBinding,
                admittedId);
            if (_historiesByScopedId.TryGetValue(
                    historyKey,
                    out var history))
            {
                var candidate = history[^1];
                if (candidate.Binding.IsSameAs(admittedBinding)
                    && admittedGrant.Allows(candidate.Audience))
                {
                    latest = candidate;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return latest is null
            ? null
            : new WorldPresentationProjection(latest, admittedGrant);
    }

    public async ValueTask<WorldPresentationPage> QueryAsync(
        WorldPresentationQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        return await QueryCoreAsync(query, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorldPresentationExport> ExportAsync(
        WorldPresentationQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var page = await QueryCoreAsync(query, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new WorldPresentationExport(
            query,
            page.Items,
            page.ContinuationCursor,
            page.HasMore);
    }

    internal async ValueTask<IReadOnlyList<VerifiedWorldPresentation>>
        CaptureInteractiveWorldBundleAsync(
            int maximumRecords,
            CancellationToken cancellationToken)
    {
        await using var lease =
            await AcquireInteractiveWorldBundleCaptureAsync(
                    maximumRecords,
                    cancellationToken)
                .ConfigureAwait(false);
        return lease.Items;
    }

    internal async ValueTask<
            InteractiveWorldSidecarCaptureLease<
                VerifiedWorldPresentation>>
        AcquireInteractiveWorldBundleCaptureAsync(
            int maximumRecords,
            CancellationToken cancellationToken)
    {
        if (maximumRecords < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var release = true;
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_storeRevision > maximumRecords)
            {
                throw new InteractiveWorldBundleException(
                    InteractiveWorldBundleReasonCodes.CapacityExceeded,
                    "The durable-presentation sidecar exceeds the bundle "
                    + "record limit.");
            }

            var items =
                new ReadOnlyCollection<VerifiedWorldPresentation>(
                _historiesByScopedId.Values
                    .SelectMany(static history => history)
                    .OrderBy(static item => item.Sequence)
                    .ToArray());
            release = false;
            return new InteractiveWorldSidecarCaptureLease<
                VerifiedWorldPresentation>(
                _gate,
                items);
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

    private async ValueTask<WorldPresentationPage> QueryCoreAsync(
        WorldPresentationQuery query,
        CancellationToken cancellationToken)
    {
        QuerySelection selection;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            selection = SelectUnderGate(query, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        var projections = new List<WorldPresentationProjection>(
            selection.Records.Count);
        foreach (var record in selection.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projections.Add(new WorldPresentationProjection(
                record,
                query.Grant));
        }

        return new WorldPresentationPage(
            projections,
            selection.ContinuationCursor,
            selection.HasMore);
    }

    private QuerySelection SelectUnderGate(
        WorldPresentationQuery query,
        CancellationToken cancellationToken)
    {
        var result = new List<VerifiedWorldPresentation>(
            Math.Min(query.MaxItems, 256));
        var continuation = query.AfterCursor;
        var hasMore = false;
        long projectedBytes = 0;
        var afterSequence = ResolveAfterSequence(query);
        var cursors = BuildPostingCursors(query, afterSequence);
        var examined = 0;
        while (cursors.Count > 0)
        {
            if ((examined++ & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (result.Count >= query.MaxItems)
            {
                hasMore = true;
                break;
            }

            var posting = cursors.Min
                          ?? throw new InvalidOperationException(
                              "A presentation posting heap is inconsistent.");
            var record = posting.Current;

            var attemptedBytes = SaturatingAdd(
                projectedBytes,
                record.ProjectionUtf8Bytes);
            if (attemptedBytes > query.MaxProjectedUtf8Bytes)
            {
                if (result.Count == 0)
                {
                    throw new RuntimeContentLimitException(
                        nameof(query),
                        "world_presentation_projection_bytes_exceeded",
                        $"A presentation requires "
                        + $"{record.ProjectionUtf8Bytes} "
                        + "projected UTF-8 bytes, which exceeds the "
                        + $"{query.MaxProjectedUtf8Bytes}-byte page "
                        + "limit.");
                }

                hasMore = true;
                break;
            }

            result.Add(record);
            continuation =
                WorldPresentationValidation.ComputeProjectionCursor(
                    record,
                    query.Grant);
            projectedBytes = attemptedBytes;
            _ = cursors.Remove(posting);
            if (posting.TryMoveNext())
            {
                _ = cursors.Add(posting);
            }
        }

        return new QuerySelection(
            result,
            continuation,
            hasMore);
    }

    private long ResolveAfterSequence(WorldPresentationQuery query)
    {
        if (query.AfterCursor is null)
        {
            return 0;
        }

        var baseCursor = query.AfterCursor[..64];
        if (!_cursorPositions.TryGetValue(
                baseCursor,
                out var position)
            || !position.Presentation.Binding.IsSameAs(query.Binding)
            || !position.Viewer.IsSameIncarnation(query.Grant.Viewer)
            || !query.Grant.Allows(position.Presentation.Audience)
            || !string.Equals(
                query.AfterCursor,
                WorldPresentationValidation.ComputeProjectionCursor(
                    position.Presentation,
                    query.Grant),
                StringComparison.Ordinal))
        {
            throw new WorldPresentationCursorException();
        }

        return position.Presentation.Sequence;
    }

    private SortedSet<PostingCursor> BuildPostingCursors(
        WorldPresentationQuery query,
        long afterSequence)
    {
        var cursors = new SortedSet<PostingCursor>(
            PostingCursorComparer.Instance);
        var ordinal = 0;
        foreach (var privacyClass in query.Grant.PrivacyClasses)
        {
            foreach (var redactionClass in query.Grant.RedactionClasses)
            {
                var key = AudiencePostingKey(
                    query.Binding,
                    query.Grant.MembershipScopeId,
                    query.Grant.MembershipRevision,
                    query.Grant.Viewer,
                    privacyClass,
                    redactionClass);
                if (!_recordsByAudiencePosting.TryGetValue(
                        key,
                        out var records))
                {
                    continue;
                }

                var start = FirstAfter(records, afterSequence);
                if (start < records.Count)
                {
                    _ = cursors.Add(new PostingCursor(
                        records,
                        start,
                        ordinal++));
                }
            }
        }

        return cursors;
    }

    private async ValueTask<long> PersistAsync(
        VerifiedWorldPresentation presentation,
        CancellationToken cancellationToken)
    {
        var frameRecord = new WorldPresentationFrameRecord
        {
            FormatVersion = FormatVersion,
            StoreRevision = presentation.Sequence,
            PreviousFrameDigest = _lastFrameDigest,
            Presentation =
                PersistedWorldPresentation.FromPresentation(presentation)
        };
        using var payload = BoundedJsonPayload.Serialize(
            frameRecord,
            PersistenceJsonContext.Default.WorldPresentationFrameRecord,
            _maxFramePayloadBytes,
            attempted => Capacity(
                nameof(
                    FileWorldPresentationStoreOptions
                        .MaxFramePayloadBytes),
                _maxFramePayloadBytes,
                attempted));
        WorldPresentationFrameJsonGuard.Validate(
            payload.WrittenSpan,
            _limits,
            _maxFrameJsonTokens);
        var residentBytes = EstimateResidentBytes(
            payload.WrittenCount,
            presentation.Audience.Members.Count);
        EnsureResidentCapacity(residentBytes);
        var frame = BuildFrame(payload.WrittenSpan);
        var attemptedLength = SaturatingAdd(_stream.Length, frame.Length);
        if (attemptedLength > _maxLogBytes)
        {
            throw Capacity(
                nameof(FileWorldPresentationStoreOptions.MaxLogBytes),
                _maxLogBytes,
                attemptedLength);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var frameDigest = ComputeSha256(payload.WrittenSpan);
        await WriteFrameAsync(frame).ConfigureAwait(false);
        _lastFrameDigest = frameDigest;
        Interlocked.Exchange(
            ref _storeRevision,
            presentation.Sequence);
        return residentBytes;
    }

    private async ValueTask WriteFrameAsync(byte[] frame)
    {
        var bytesToWrite = _faultInjector?.GetWriteLength(frame.Length)
            ?? frame.Length;
        if (bytesToWrite < 0 || bytesToWrite > frame.Length)
        {
            throw new InvalidOperationException(
                "A world-presentation fault injector returned an invalid "
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
                    $"Only {bytesToWrite} of {frame.Length} "
                    + "world-presentation frame bytes were written.");
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
                        FileWorldPresentationStoreOptions
                            .MaxFramePayloadBytes),
                    _maxFramePayloadBytes,
                    payloadLength);
            }

            var totalLength = checked(
                (long)FrameHeaderSize
                + payloadLength
                + FrameFooterSize);
            if (remaining < totalLength)
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

            EnsureResidentCapacity(
                EstimateResidentBytes(payloadLength));
            if (_storeRevision >= _maxRecords)
            {
                throw Capacity(
                    nameof(FileWorldPresentationStoreOptions.MaxRecords),
                    _maxRecords,
                    SaturatingAdd(_storeRevision, 1));
            }

            try
            {
                WorldPresentationFrameJsonGuard.Validate(
                    payload,
                    _limits,
                    _maxFrameJsonTokens);
                var record = JsonSerializer.Deserialize(
                                 payload,
                                 PersistenceJsonContext.Default
                                     .WorldPresentationFrameRecord)
                             ?? throw new JsonException(
                                 "Frame payload is null.");
                ApplyRecovered(record, payload);
            }
            catch (Exception exception) when (
                exception is (
                    JsonException
                    or ArgumentException
                    or InvalidOperationException
                    or OverflowException)
                && exception is not
                    FileWorldPresentationStoreCapacityException)
            {
                throw Corrupt(frameOffset, exception.Message, exception);
            }

            lastCommittedOffset = checked(frameOffset + totalLength);
        }

        _stream.Position = _stream.Length;
    }

    private void ApplyRecovered(
        WorldPresentationFrameRecord record,
        byte[] payload)
    {
        if (record.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported world-presentation format version "
                + $"'{record.FormatVersion}'.");
        }

        var expectedRevision = checked(_storeRevision + 1);
        if (record.StoreRevision != expectedRevision)
        {
            throw new InvalidOperationException(
                $"Expected store revision {expectedRevision}, but found "
                + $"{record.StoreRevision}.");
        }

        if (!CanonicalJsonDigest.IsSha256(record.PreviousFrameDigest)
            || !string.Equals(
                record.PreviousFrameDigest,
                _lastFrameDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The world-presentation frame digest chain is invalid.");
        }

        var presentation = (record.Presentation
                            ?? throw new JsonException(
                                "A world-presentation frame requires a "
                                + "presentation."))
            .Restore();
        if (presentation.Sequence != expectedRevision)
        {
            throw new InvalidOperationException(
                "The presentation sequence does not match its frame.");
        }

        ValidateAgainstStoreLimits(presentation);
        ValidateRecoveredSuccessor(presentation);
        AddCommitted(
            presentation,
            EstimateResidentBytes(
                payload.Length,
                presentation.Audience.Members.Count));
        _lastFrameDigest = ComputeSha256(payload);
        Interlocked.Exchange(ref _storeRevision, record.StoreRevision);
    }

    private void ValidateRecoveredSuccessor(
        VerifiedWorldPresentation presentation)
    {
        var historyKey = ScopedPresentationKey(
            presentation.Binding,
            presentation.PresentationId);
        if (!_historiesByScopedId.TryGetValue(
                historyKey,
                out var history))
        {
            if (presentation.ContentRevision != 0)
            {
                throw new InvalidOperationException(
                    "A presentation history must begin at content "
                    + "revision zero.");
            }

            return;
        }

        var current = history[^1];
        if (presentation.ContentRevision
            != checked(current.ContentRevision + 1)
            || !IsSamePresentationStream(current, presentation))
        {
            throw new InvalidOperationException(
                "A recovered presentation does not extend its committed "
                + "history.");
        }
    }

    private void AddCommitted(
        VerifiedWorldPresentation presentation,
        long residentBytes)
    {
        EnsureResidentCapacity(residentBytes);
        var historyKey = ScopedPresentationKey(
            presentation.Binding,
            presentation.PresentationId);
        if (!_historiesByScopedId.TryGetValue(
                historyKey,
                out var history))
        {
            history = new List<VerifiedWorldPresentation>();
            _historiesByScopedId.Add(historyKey, history);
        }

        history.Add(presentation);
        foreach (var member in presentation.Audience.Members)
        {
            var postingKey = AudiencePostingKey(
                presentation.Binding,
                presentation.Audience.MembershipScopeId,
                presentation.Audience.MembershipRevision,
                member,
                presentation.Audience.PrivacyClass,
                presentation.Audience.RedactionClass);
            if (!_recordsByAudiencePosting.TryGetValue(
                    postingKey,
                    out var records))
            {
                records = new List<VerifiedWorldPresentation>();
                _recordsByAudiencePosting.Add(postingKey, records);
            }

            records.Add(presentation);
            var cursor =
                WorldPresentationValidation.ComputeProjectionCursorBase(
                    presentation,
                    member);
            if (!_cursorPositions.TryAdd(
                    cursor,
                    new CursorPosition(presentation, member)))
            {
                throw new InvalidOperationException(
                    "A duplicate presentation continuation cursor was "
                    + "generated.");
            }
        }

        Interlocked.Exchange(
            ref _residentBytes,
            checked(_residentBytes + residentBytes));
    }

    private void ValidateAgainstStoreLimits(
        VerifiedWorldPresentation presentation)
    {
        _ = new WorldPresentationDraft(
            presentation.PresentationId,
            presentation.ContentRevision,
            presentation.Source,
            presentation.Binding,
            presentation.Audience,
            presentation.Content,
            presentation.Provenance,
            _limits);
    }

    private static bool IsSamePresentationStream(
        VerifiedWorldPresentation current,
        VerifiedWorldPresentation candidate)
    {
        return current.Source.IsSameAs(candidate.Source)
               && current.Binding.IsSameAs(candidate.Binding)
               && string.Equals(
                   current.Audience.SemanticDigest,
                   candidate.Audience.SemanticDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   current.EvidenceDigest,
                   candidate.EvidenceDigest,
                   StringComparison.Ordinal);
    }

    private void EnsureResidentCapacity(long additionalBytes)
    {
        var attempted = SaturatingAdd(_residentBytes, additionalBytes);
        if (attempted > _maxResidentBytes)
        {
            throw Capacity(
                nameof(
                    FileWorldPresentationStoreOptions.MaxResidentBytes),
                _maxResidentBytes,
                attempted);
        }
    }

    private static long EstimateResidentBytes(
        int payloadBytes,
        int audienceMemberCount = 0)
    {
        return SaturatingAdd(
            SaturatingAdd(
                checked(payloadBytes * 4L),
                4_096),
            checked(audienceMemberCount * 2_048L));
    }

    private static string AudiencePostingKey(
        WorldPresentationBinding binding,
        string membershipScopeId,
        long membershipRevision,
        GameEntityIdentity viewer,
        string privacyClass,
        string redactionClass)
    {
        return string.Concat(
            binding.SemanticDigest,
            "|",
            membershipScopeId.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ":",
            membershipScopeId,
            "|",
            membershipRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "|",
            viewer.EntityId.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ":",
            viewer.EntityId,
            "|",
            viewer.Incarnation.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "|",
            privacyClass.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ":",
            privacyClass,
            "|",
            redactionClass.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ":",
            redactionClass);
    }

    private static string ScopedPresentationKey(
        WorldPresentationBinding binding,
        string presentationId)
    {
        return string.Concat(
            binding.SemanticDigest,
            "|",
            presentationId.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ":",
            presentationId);
    }

    private static int FirstAfter(
        IReadOnlyList<VerifiedWorldPresentation> records,
        long sequence)
    {
        var low = 0;
        var high = records.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (records[middle].Sequence <= sequence)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
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
                nameof(FileWorldPresentationStore));
        }

        if (_faulted)
        {
            throw new FileWorldPresentationStoreFaultedException(_path);
        }
    }

    private FileWorldPresentationStoreCapacityException Capacity(
        string limitName,
        long limit,
        long attempted)
    {
        return new FileWorldPresentationStoreCapacityException(
            limitName,
            limit,
            attempted);
    }

    private FileWorldPresentationStoreCorruptionException Corrupt(
        long offset,
        string message,
        Exception? innerException = null)
    {
        return new FileWorldPresentationStoreCorruptionException(
            _path,
            offset,
            message,
            innerException);
    }

    private static string RequiredPresentationId(
        string? value,
        string parameterName)
    {
        return RuntimeGuard.RequiredUtf8(value, 128, parameterName);
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
            _ = result.Append(item.ToString("x2"));
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

    private static long SaturatingAdd(long value, long additional)
    {
        return value > long.MaxValue - additional
            ? long.MaxValue
            : value + additional;
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
                    value = (value & 1) != 0
                        ? value >> 1 ^ Polynomial
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }

    private sealed class QuerySelection
    {
        public QuerySelection(
            IReadOnlyList<VerifiedWorldPresentation> records,
            string? continuationCursor,
            bool hasMore)
        {
            Records = records;
            ContinuationCursor = continuationCursor;
            HasMore = hasMore;
        }

        public IReadOnlyList<VerifiedWorldPresentation> Records { get; }

        public string? ContinuationCursor { get; }

        public bool HasMore { get; }
    }

    private sealed class CursorPosition
    {
        public CursorPosition(
            VerifiedWorldPresentation presentation,
            GameEntityIdentity viewer)
        {
            Presentation = presentation;
            Viewer = viewer;
        }

        public VerifiedWorldPresentation Presentation { get; }

        public GameEntityIdentity Viewer { get; }
    }

    private sealed class PostingCursor
    {
        private readonly IReadOnlyList<VerifiedWorldPresentation> _records;
        private int _index;

        public PostingCursor(
            IReadOnlyList<VerifiedWorldPresentation> records,
            int index,
            int ordinal)
        {
            _records = records;
            _index = index;
            Ordinal = ordinal;
        }

        public VerifiedWorldPresentation Current => _records[_index];

        public int Ordinal { get; }

        public bool TryMoveNext()
        {
            _index = checked(_index + 1);
            return _index < _records.Count;
        }
    }

    private sealed class PostingCursorComparer : IComparer<PostingCursor>
    {
        public static PostingCursorComparer Instance { get; } = new();

        public int Compare(PostingCursor? left, PostingCursor? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var sequence = left.Current.Sequence.CompareTo(
                right.Current.Sequence);
            return sequence != 0
                ? sequence
                : left.Ordinal.CompareTo(right.Ordinal);
        }
    }
}
