using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Persistence;

/// <summary>
/// Single-writer, crash-tolerant group-interaction store. Each successful
/// transition appends a checksummed immutable session snapshot. Recovery
/// revalidates every snapshot through <see cref="GroupInteractionStateMachine"/>.
/// </summary>
public sealed class FileGroupInteractionStore :
    IGroupInteractionStore,
    IDisposable,
    IAsyncDisposable
{
    internal const int FrameHeaderSize = 12;
    internal const int FrameFooterSize = 4;

    private const uint FrameMagic = 0x31475247;
    private const uint CommitMagic = 0x54494D43;
    private const int FormatVersion = 1;
    private const string GenesisFrameDigest =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly string _path;
    private readonly ExclusiveFileWriterLease _writerLease;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly GroupInteractionStateMachine _stateMachine;
    private readonly Dictionary<string, GroupInteractionSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly int _maxFramePayloadBytes;
    private readonly long _maxLogBytes;
    private readonly long _maxMutationFrames;
    private readonly int _maxSessions;
    private readonly bool _flushToDiskOnMutation;
    private readonly IJournalFaultInjector? _faultInjector;

    private long _storeRevision;
    private string _lastFrameDigest = GenesisFrameDigest;
    private bool _faulted;
    private bool _disposed;

    public FileGroupInteractionStore(
        string path,
        FileGroupInteractionStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A group-interaction store path is required.",
                nameof(path));
        }

        options ??= new FileGroupInteractionStoreOptions();
        if (options.Limits is null)
        {
            throw new ArgumentNullException(
                nameof(options),
                "Group-interaction limits are required.");
        }

        if (options.MaxFramePayloadBytes is < 1_024
            or > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFramePayloadBytes must be between 1 KiB and 512 MiB.");
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

        if (options.MaxMutationFrames is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxMutationFrames must be between 1 and 10,000,000.");
        }

        if (options.MaxSessions is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxSessions must be between 1 and 1,000,000.");
        }

        _path = System.IO.Path.GetFullPath(path);
        _stateMachine = new GroupInteractionStateMachine(options.Limits);
        _maxFramePayloadBytes = options.MaxFramePayloadBytes;
        _maxLogBytes = options.MaxLogBytes;
        _maxMutationFrames = options.MaxMutationFrames;
        _maxSessions = options.MaxSessions;
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
            EnsureExistingLogCapacity();
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

    public int SessionCount
    {
        get
        {
            _gate.Wait();
            try
            {
                ThrowIfUnavailable();
                return _sessions.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask<GroupInteractionSession?> ReadAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var admittedSessionId = RequiredSessionId(
            sessionId,
            nameof(sessionId));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            return _sessions.TryGetValue(
                admittedSessionId,
                out var session)
                ? session
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GroupInteractionWriteResult> CreateAsync(
        GroupInteractionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessions.TryGetValue(request.SessionId, out var current))
            {
                return DuplicateCreate(current, request);
            }

            if (_sessions.Count >= _maxSessions)
            {
                throw Capacity(
                    nameof(FileGroupInteractionStoreOptions.MaxSessions),
                    _maxSessions,
                    SaturatingAdd(_sessions.Count, 1));
            }

            var result = _stateMachine.Create(request);
            await PersistAppliedAsync(
                    result.Session!,
                    isNewSession: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<GroupInteractionWriteResult> ReplaceMembersAsync(
        GroupInteractionMembershipRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MutateAsync(
            request.SessionId,
            session => _stateMachine.ReplaceMembers(session, request),
            cancellationToken);
    }

    public ValueTask<GroupInteractionWriteResult> AppendAsync(
        GroupInteractionAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MutateAsync(
            request.SessionId,
            session => _stateMachine.Append(session, request),
            cancellationToken);
    }

    public ValueTask<GroupInteractionWriteResult> CloseAsync(
        GroupInteractionCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MutateAsync(
            request.SessionId,
            session => _stateMachine.Close(session, request),
            cancellationToken);
    }

    public async ValueTask<GroupInteractionProjection?> ProjectAsync(
        string sessionId,
        GameEntityIdentity viewer,
        CancellationToken cancellationToken = default)
    {
        var admittedSessionId = RequiredSessionId(
            sessionId,
            nameof(sessionId));

        if (viewer is null)
        {
            throw new ArgumentNullException(nameof(viewer));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            return _sessions.TryGetValue(
                admittedSessionId,
                out var session)
                ? _stateMachine.Project(session, viewer)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<IReadOnlyList<GroupInteractionSession>>
        CaptureInteractiveWorldBundleAsync(
            int maximumSessions,
            CancellationToken cancellationToken)
    {
        await using var lease =
            await AcquireInteractiveWorldBundleCaptureAsync(
                    maximumSessions,
                    cancellationToken)
                .ConfigureAwait(false);
        return lease.Items;
    }

    internal async ValueTask<
            InteractiveWorldSidecarCaptureLease<GroupInteractionSession>>
        AcquireInteractiveWorldBundleCaptureAsync(
            int maximumSessions,
            CancellationToken cancellationToken)
    {
        if (maximumSessions < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSessions));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var release = true;
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessions.Count > maximumSessions)
            {
                throw new InteractiveWorldBundleException(
                    InteractiveWorldBundleReasonCodes.CapacityExceeded,
                    "The group-interaction sidecar exceeds the bundle "
                    + "session limit.");
            }

            var items =
                new ReadOnlyCollection<GroupInteractionSession>(
                _sessions.Values
                    .OrderBy(
                        static item => item.SessionId,
                        StringComparer.Ordinal)
                    .ToArray());
            release = false;
            return new InteractiveWorldSidecarCaptureLease<
                GroupInteractionSession>(
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

    internal async ValueTask RestoreInteractiveWorldBundleAsync(
        IReadOnlyList<GroupInteractionSession> sessions,
        CancellationToken cancellationToken)
    {
        if (sessions is null)
        {
            throw new ArgumentNullException(nameof(sessions));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessions.Count != 0 || _storeRevision != 0)
            {
                throw new InvalidOperationException(
                    "A group-interaction bundle can restore only into an "
                    + "empty store.");
            }

            if (sessions.Count > _maxSessions)
            {
                throw Capacity(
                    nameof(FileGroupInteractionStoreOptions.MaxSessions),
                    _maxSessions,
                    sessions.Count);
            }

            string? previousSessionId = null;
            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session is null
                    || previousSessionId is not null
                    && string.CompareOrdinal(
                        previousSessionId,
                        session.SessionId) >= 0)
                {
                    throw new InvalidOperationException(
                        "Bundle group sessions must be non-null, unique, "
                        + "and ordinally ordered.");
                }

                previousSessionId = session.SessionId;
                for (long revision = 0;
                     revision <= session.Revision;
                     revision++)
                {
                    var operations = session.Operations
                        .Where(item => item.AppliedRevision <= revision)
                        .ToArray();
                    var messages = session.Messages
                        .Where(item => item.AppliedRevision <= revision)
                        .ToArray();
                    var membershipHistory = session.MembershipHistory
                        .Where(item => item.AppliedRevision <= revision)
                        .ToArray();
                    if (operations.Length == 0
                        || membershipHistory.Length == 0)
                    {
                        throw new InvalidOperationException(
                            "A bundle group session has incomplete revision "
                            + "history.");
                    }

                    var membership = membershipHistory[^1];
                    var status = revision == session.Revision
                        ? session.Status
                        : GroupInteractionStatuses.Open;
                    var restored = _stateMachine.Restore(
                        session.SessionId,
                        session.GroupId,
                        session.SharedScope,
                        session.SharedScopeDigest,
                        status,
                        revision,
                        membership.MembershipRevision,
                        membership.Members,
                        membershipHistory,
                        messages,
                        operations,
                        session.WorldBinding);
                    await PersistAppliedAsync(
                            restored,
                            isNewSession: revision == 0,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
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

    private GroupInteractionWriteResult DuplicateCreate(
        GroupInteractionSession current,
        GroupInteractionCreateRequest request)
    {
        var candidate = _stateMachine.Create(request);
        var replay = current.Operations.FirstOrDefault(
            item => string.Equals(
                item.OperationId,
                request.OperationId,
                StringComparison.Ordinal));
        var status = replay is null
            ? GroupInteractionWriteStatuses.SessionAlreadyExists
            : string.Equals(
                replay.RequestDigest,
                candidate.Session!.Operations[0].RequestDigest,
                StringComparison.Ordinal)
                ? GroupInteractionWriteStatuses.Idempotent
                : GroupInteractionWriteStatuses.OperationConflict;
        return new GroupInteractionWriteResult(
            status,
            current,
            string.Equals(
                status,
                GroupInteractionWriteStatuses.Idempotent,
                StringComparison.Ordinal)
                ? replay?.AppliedRevision
                : null);
    }

    private async ValueTask<GroupInteractionWriteResult> MutateAsync(
        string sessionId,
        Func<GroupInteractionSession, GroupInteractionWriteResult> transition,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessions.TryGetValue(sessionId, out var current))
            {
                return new GroupInteractionWriteResult(
                    GroupInteractionWriteStatuses.NotFound,
                    session: null);
            }

            var result = transition(current);
            if (string.Equals(
                    result.Status,
                    GroupInteractionWriteStatuses.Applied,
                    StringComparison.Ordinal))
            {
                await PersistAppliedAsync(
                        result.Session!,
                        isNewSession: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask PersistAppliedAsync(
        GroupInteractionSession session,
        bool isNewSession,
        CancellationToken cancellationToken)
    {
        var nextStoreRevision = checked(_storeRevision + 1);
        var record = new GroupInteractionFrameRecord
        {
            FormatVersion = FormatVersion,
            StoreRevision = nextStoreRevision,
            PreviousFrameDigest = _lastFrameDigest,
            Session =
                PersistedGroupInteractionSession.FromSession(session)
        };
        using var payload = BoundedJsonPayload.Serialize(
            record,
            PersistenceJsonContext.Default.GroupInteractionFrameRecord,
            _maxFramePayloadBytes,
            attempted => Capacity(
                nameof(
                    FileGroupInteractionStoreOptions
                        .MaxFramePayloadBytes),
                _maxFramePayloadBytes,
                attempted));
        var frame = BuildFrame(payload.WrittenSpan);
        EnsureMutationCapacity(nextStoreRevision, frame.Length);
        cancellationToken.ThrowIfCancellationRequested();

        var frameDigest = ComputeSha256(payload.WrittenSpan);
        await WriteFrameAsync(frame).ConfigureAwait(false);

        if (isNewSession)
        {
            _sessions.Add(session.SessionId, session);
        }
        else
        {
            _sessions[session.SessionId] = session;
        }

        _lastFrameDigest = frameDigest;
        Interlocked.Exchange(ref _storeRevision, nextStoreRevision);
    }

    private async ValueTask WriteFrameAsync(byte[] frame)
    {
        // Cancellation is checked immediately before this method. Once bytes
        // may be visible, finish the bounded commit without cancellation so a
        // caller never receives a cancelled result for a committed mutation.
        var bytesToWrite = _faultInjector?.GetWriteLength(frame.Length)
            ?? frame.Length;
        if (bytesToWrite < 0 || bytesToWrite > frame.Length)
        {
            throw new InvalidOperationException(
                "A group-store fault injector returned an invalid write "
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
                    $"Only {bytesToWrite} of {frame.Length} group frame "
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
                        FileGroupInteractionStoreOptions
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
                                     .GroupInteractionFrameRecord)
                             ?? throw new JsonException(
                                 "Frame payload is null.");
                ApplyRecoveredRecord(record, payload);
            }
            catch (Exception exception) when (
                exception is (
                    JsonException
                    or ArgumentException
                    or InvalidOperationException
                    or OverflowException)
                && exception is not
                    FileGroupInteractionStoreCapacityException)
            {
                throw Corrupt(frameOffset, exception.Message, exception);
            }

            lastCommittedOffset = checked(
                frameOffset + totalFrameLength);
        }

        _stream.Position = _stream.Length;
    }

    private void ApplyRecoveredRecord(
        GroupInteractionFrameRecord record,
        byte[] payload)
    {
        if (record.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported group-store format version "
                + $"'{record.FormatVersion}'.");
        }

        var expectedStoreRevision = checked(_storeRevision + 1);
        if (record.StoreRevision != expectedStoreRevision)
        {
            throw new InvalidOperationException(
                $"Expected store revision {expectedStoreRevision}, "
                + $"but found {record.StoreRevision}.");
        }

        if (!IsSha256(record.PreviousFrameDigest)
            || !string.Equals(
                record.PreviousFrameDigest,
                _lastFrameDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The frame history digest chain is invalid.");
        }

        var session = (record.Session
                       ?? throw new JsonException(
                           "A group frame requires a session."))
            .Restore(_stateMachine);
        if (_sessions.TryGetValue(session.SessionId, out var current))
        {
            ValidateSuccessor(current, session);
            _sessions[session.SessionId] = session;
        }
        else
        {
            if (session.Revision != 0)
            {
                throw new InvalidOperationException(
                    "A recovered session must begin at revision zero.");
            }

            if (_sessions.Count >= _maxSessions)
            {
                throw Capacity(
                    nameof(FileGroupInteractionStoreOptions.MaxSessions),
                    _maxSessions,
                    SaturatingAdd(_sessions.Count, 1));
            }

            _sessions.Add(session.SessionId, session);
        }

        _lastFrameDigest = ComputeSha256(payload);
        Interlocked.Exchange(
            ref _storeRevision,
            record.StoreRevision);
    }

    private static void ValidateSuccessor(
        GroupInteractionSession current,
        GroupInteractionSession next)
    {
        if (next.Revision != checked(current.Revision + 1)
            || !string.Equals(
                next.GroupId,
                current.GroupId,
                StringComparison.Ordinal)
            || !string.Equals(
                next.SharedScopeDigest,
                current.SharedScopeDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                current.Status,
                GroupInteractionStatuses.Open,
                StringComparison.Ordinal)
            || next.MembershipRevision < current.MembershipRevision
            || next.MembershipRevision > current.MembershipRevision + 1
            || next.Operations.Count != current.Operations.Count + 1
            || next.MembershipHistory.Count
            < current.MembershipHistory.Count
            || next.MembershipHistory.Count
            > current.MembershipHistory.Count + 1
            || next.Messages.Count < current.Messages.Count
            || !OperationPrefixMatches(current, next)
            || !MembershipPrefixMatches(current, next)
            || !MessagePrefixMatches(current, next))
        {
            throw new InvalidOperationException(
                "A recovered group snapshot does not extend the prior "
                + "committed snapshot.");
        }
    }

    private static bool OperationPrefixMatches(
        GroupInteractionSession current,
        GroupInteractionSession next)
    {
        for (var index = 0; index < current.Operations.Count; index++)
        {
            var left = current.Operations[index];
            var right = next.Operations[index];
            if (!string.Equals(
                    left.OperationId,
                    right.OperationId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    left.Kind,
                    right.Kind,
                    StringComparison.Ordinal)
                || !string.Equals(
                    left.RequestDigest,
                    right.RequestDigest,
                    StringComparison.Ordinal)
                || left.AppliedRevision != right.AppliedRevision)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MembershipPrefixMatches(
        GroupInteractionSession current,
        GroupInteractionSession next)
    {
        for (var index = 0;
             index < current.MembershipHistory.Count;
             index++)
        {
            var left = current.MembershipHistory[index];
            var right = next.MembershipHistory[index];
            if (left.MembershipRevision != right.MembershipRevision
                || left.AppliedRevision != right.AppliedRevision
                || !MembersEqual(left.Members, right.Members))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MessagePrefixMatches(
        GroupInteractionSession current,
        GroupInteractionSession next)
    {
        for (var index = 0; index < current.Messages.Count; index++)
        {
            var left = current.Messages[index];
            var right = next.Messages[index];
            if (left.Sequence != right.Sequence
                || left.AppliedRevision != right.AppliedRevision
                || left.MembershipRevision != right.MembershipRevision
                || left.PayloadUtf8Bytes != right.PayloadUtf8Bytes
                || !string.Equals(
                    left.MessageId,
                    right.MessageId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    left.Kind,
                    right.Kind,
                    StringComparison.Ordinal)
                || !string.Equals(
                    left.PayloadDigest,
                    right.PayloadDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    left.AudienceMode,
                    right.AudienceMode,
                    StringComparison.Ordinal)
                || !string.Equals(
                    left.CausationId,
                    right.CausationId,
                    StringComparison.Ordinal)
                || !IdentitiesEqual(left.Author, right.Author)
                || !IdentityListsEqual(left.Audience, right.Audience))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MembersEqual(
        IReadOnlyList<GroupInteractionMember> left,
        IReadOnlyList<GroupInteractionMember> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!IdentitiesEqual(left[index].Actor, right[index].Actor)
                || !left[index].Roles.SequenceEqual(
                    right[index].Roles,
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IdentityListsEqual(
        IReadOnlyList<GameEntityIdentity> left,
        IReadOnlyList<GameEntityIdentity> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!IdentitiesEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IdentitiesEqual(
        GameEntityIdentity? left,
        GameEntityIdentity? right)
    {
        return left is null
            ? right is null
            : right is not null
              && left.Incarnation == right.Incarnation
              && string.Equals(
                  left.EntityId,
                  right.EntityId,
                  StringComparison.Ordinal);
    }

    private void EnsureExistingLogCapacity()
    {
        if (_stream.Length > _maxLogBytes)
        {
            throw Capacity(
                nameof(FileGroupInteractionStoreOptions.MaxLogBytes),
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
            throw Capacity(
                nameof(
                    FileGroupInteractionStoreOptions.MaxMutationFrames),
                _maxMutationFrames,
                attemptedRevision);
        }

        var attemptedLength = SaturatingAdd(_stream.Length, frameLength);
        if (attemptedLength > _maxLogBytes)
        {
            throw Capacity(
                nameof(FileGroupInteractionStoreOptions.MaxLogBytes),
                _maxLogBytes,
                attemptedLength);
        }
    }

    private void EnsureRecoveredMutationCapacity()
    {
        var attemptedRevision = SaturatingAdd(_storeRevision, 1);
        if (attemptedRevision > _maxMutationFrames)
        {
            throw Capacity(
                nameof(
                    FileGroupInteractionStoreOptions.MaxMutationFrames),
                _maxMutationFrames,
                attemptedRevision);
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
                nameof(FileGroupInteractionStore));
        }

        if (_faulted)
        {
            throw new FileGroupInteractionStoreFaultedException(_path);
        }
    }

    private FileGroupInteractionStoreCapacityException Capacity(
        string limitName,
        long limit,
        long attempted)
    {
        return new FileGroupInteractionStoreCapacityException(
            limitName,
            limit,
            attempted);
    }

    private FileGroupInteractionStoreCorruptionException Corrupt(
        long offset,
        string message,
        Exception? innerException = null)
    {
        return new FileGroupInteractionStoreCorruptionException(
            _path,
            offset,
            message,
            innerException);
    }

    private static long SaturatingAdd(long value, long additional)
    {
        return value > long.MaxValue - additional
            ? long.MaxValue
            : value + additional;
    }

    private static string RequiredSessionId(
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException(
                "A bounded session ID is required.",
                parameterName);
        }

        foreach (var character in value)
        {
            var allowed = character is >= 'A' and <= 'Z'
                          || character is >= 'a' and <= 'z'
                          || character is >= '0' and <= '9'
                          || character is '.' or '_' or ':' or '-';
            if (!allowed)
            {
                throw new ArgumentException(
                    "A session ID contains an unsupported character.",
                    parameterName);
            }
        }

        return value;
    }

    private static byte[] BuildFrame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[
            checked(FrameHeaderSize + payload.Length + FrameFooterSize)];
        WriteUInt32(frame, 0, FrameMagic);
        WriteInt32(frame, 4, payload.Length);
        WriteUInt32(frame, 8, Crc32.Compute(payload));
        payload.CopyTo(
            frame.AsSpan(FrameHeaderSize, payload.Length));
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

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var item in value)
        {
            if (item is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
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
