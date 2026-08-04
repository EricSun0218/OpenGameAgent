using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GameAgent.Workflow;

internal enum WorkflowRunLogOperation
{
    Create = 1,
    Commit = 2,
    LeaseAcquire = 3,
    LeaseRenew = 4,
    LeaseRelease = 5,
    Cancel = 6
}

internal sealed class WorkflowRunLogScan
{
    public WorkflowRunLogScan(
        bool initialized,
        WorkflowRunSnapshot? snapshot,
        long committedLength,
        long frameSequence,
        int frameCount,
        bool hasTornTail)
    {
        Initialized = initialized;
        Snapshot = snapshot;
        CommittedLength = committedLength;
        FrameSequence = frameSequence;
        FrameCount = frameCount;
        HasTornTail = hasTornTail;
    }

    public bool Initialized { get; }

    public WorkflowRunSnapshot? Snapshot { get; }

    public long CommittedLength { get; }

    public long FrameSequence { get; }

    public int FrameCount { get; }

    public bool HasTornTail { get; }
}

internal static class WorkflowRunLog
{
    public const int HeaderBytes = 48;
    public const int FrameOverheadBytes = 112;

    private const int FileVersion = 1;
    private const int FrameVersion = 1;
    private const int FramePrefixBytes = 60;
    private const int FrameFooterBytes = 52;

    private static readonly byte[] FileMagic =
        Encoding.ASCII.GetBytes("GAWFRUN1");
    private static readonly byte[] FrameMagic =
        Encoding.ASCII.GetBytes("GAWFFRM1");
    private static readonly byte[] CommitMagic =
        Encoding.ASCII.GetBytes("GAWFCMT1");

    public static WorkflowRunLogScan Scan(
        FileStream stream,
        FileWorkflowRunStoreOptions options)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (stream.Length > options.MaxFileBytesPerRun)
        {
            throw new WorkflowFileStoreCapacityException(
                "A workflow run file exceeds its configured byte limit.");
        }

        if (stream.Length == 0)
        {
            return new WorkflowRunLogScan(
                initialized: false,
                snapshot: null,
                committedLength: 0,
                frameSequence: 0,
                frameCount: 0,
                hasTornTail: false);
        }

        if (stream.Length < HeaderBytes)
        {
            return new WorkflowRunLogScan(
                initialized: false,
                snapshot: null,
                committedLength: 0,
                frameSequence: 0,
                frameCount: 0,
                hasTornTail: true);
        }

        stream.Position = 0;
        var header = new byte[HeaderBytes];
        ReadExactly(stream, header, 0, header.Length);
        ValidateHeader(header);

        var offset = (long)HeaderBytes;
        var sequence = 0L;
        var frameCount = 0;
        WorkflowRunSnapshot? snapshot = null;
        while (offset < stream.Length)
        {
            var remaining = stream.Length - offset;
            if (remaining < FramePrefixBytes)
            {
                return new WorkflowRunLogScan(
                    initialized: true,
                    snapshot,
                    offset,
                    sequence,
                    frameCount,
                    hasTornTail: true);
            }

            stream.Position = offset;
            var prefix = new byte[FramePrefixBytes];
            ReadExactly(stream, prefix, 0, prefix.Length);
            if (!BytesEqual(prefix, 0, FrameMagic))
            {
                throw Corrupt(
                    "A committed workflow frame has an invalid prefix.");
            }

            var expectedPrefixChecksum = prefix
                .AsSpan(28, 32)
                .ToArray();
            var actualPrefixChecksum = ComputeSha256(
                prefix.AsSpan(0, 28));
            if (!FixedTimeEquals(
                    expectedPrefixChecksum,
                    actualPrefixChecksum))
            {
                throw Corrupt(
                    "A committed workflow frame prefix checksum does not match.");
            }

            var frameVersion = BinaryPrimitives.ReadInt32LittleEndian(
                prefix.AsSpan(8, 4));
            if (frameVersion != FrameVersion)
            {
                throw new WorkflowFileStoreCorruptionException(
                    WorkflowFileStoreReasonCodes.UnsupportedVersion,
                    "The workflow frame version is unsupported.");
            }

            var nextSequence = BinaryPrimitives.ReadInt64LittleEndian(
                prefix.AsSpan(12, 8));
            if (nextSequence != sequence + 1)
            {
                throw Corrupt(
                    "Workflow frame sequence numbers are not contiguous.");
            }

            var operationRaw = BinaryPrimitives.ReadInt32LittleEndian(
                prefix.AsSpan(20, 4));
            if (operationRaw is < (int)WorkflowRunLogOperation.Create
                or > (int)WorkflowRunLogOperation.Cancel)
            {
                throw new WorkflowFileStoreCorruptionException(
                    WorkflowFileStoreReasonCodes.UnsupportedVersion,
                    "The workflow frame operation is unsupported.");
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
                prefix.AsSpan(24, 4));
            if (payloadLength < 1
                || payloadLength > options.MaxSnapshotBytes
                || (long)payloadLength + FrameOverheadBytes
                > options.MaxFrameBytes)
            {
                throw Corrupt(
                    "A committed workflow frame has an invalid length.");
            }

            var frameLength =
                (long)payloadLength + FrameOverheadBytes;
            if (remaining < frameLength)
            {
                return new WorkflowRunLogScan(
                    initialized: true,
                    snapshot,
                    offset,
                    sequence,
                    frameCount,
                    hasTornTail: true);
            }

            var payload = new byte[payloadLength];
            ReadExactly(stream, payload, 0, payload.Length);
            var footer = new byte[FrameFooterBytes];
            ReadExactly(stream, footer, 0, footer.Length);
            var expectedPayloadChecksum = footer
                .AsSpan(20, 32)
                .ToArray();
            if (!BytesEqual(footer, 0, CommitMagic)
                || BinaryPrimitives.ReadInt64LittleEndian(
                    footer.AsSpan(8, 8)) != nextSequence
                || BinaryPrimitives.ReadInt32LittleEndian(
                    footer.AsSpan(16, 4)) != payloadLength)
            {
                throw Corrupt(
                    "A committed workflow frame has an invalid commit marker.");
            }

            var actualChecksum = ComputeSha256(payload);
            if (!FixedTimeEquals(
                    expectedPayloadChecksum,
                    actualChecksum))
            {
                throw Corrupt(
                    "A committed workflow frame checksum does not match.");
            }

            var decoded = WorkflowRunSnapshotCodec.Decode(
                payload,
                options.MaxStageInstancesPerRun);
            var operation = (WorkflowRunLogOperation)operationRaw;
            ValidateTransition(snapshot, decoded, operation);
            snapshot = decoded;
            sequence = nextSequence;
            frameCount++;
            if (frameCount > options.MaxOperationsPerRun)
            {
                throw new WorkflowFileStoreCapacityException(
                    "A workflow run exceeds its operation limit.");
            }

            offset = checked(offset + frameLength);
        }

        return new WorkflowRunLogScan(
            initialized: true,
            snapshot,
            offset,
            sequence,
            frameCount,
            hasTornTail: false);
    }

    public static long GetFrameLength(int payloadLength)
    {
        return checked((long)payloadLength + FrameOverheadBytes);
    }

    public static void Append(
        FileStream stream,
        WorkflowRunLogScan scan,
        WorkflowRunLogOperation operation,
        WorkflowRunSnapshot snapshot,
        byte[] payload,
        FileWorkflowRunStoreOptions options)
    {
        ValidateTransition(scan.Snapshot, snapshot, operation);
        if (scan.FrameCount >= options.MaxOperationsPerRun)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow run operation limit is exhausted.");
        }

        if (payload.Length > options.MaxSnapshotBytes
            || GetFrameLength(payload.Length) > options.MaxFrameBytes)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow snapshot exceeds its frame limit.");
        }

        if (!scan.Initialized)
        {
            stream.SetLength(0);
            stream.Position = 0;
            WriteHeader(stream);
        }
        else
        {
            stream.SetLength(scan.CommittedLength);
            stream.Position = scan.CommittedLength;
        }

        var nextSequence = checked(scan.FrameSequence + 1);
        options.FaultInjector?.OnFaultPoint(
            WorkflowFileStoreFaultPoint.BeforeFrameWrite,
            snapshot.RunId,
            nextSequence);

        var payloadChecksum = ComputeSha256(payload);
        var prefix = new byte[FramePrefixBytes];
        FrameMagic.CopyTo(prefix, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix.AsSpan(8, 4),
            FrameVersion);
        BinaryPrimitives.WriteInt64LittleEndian(
            prefix.AsSpan(12, 8),
            nextSequence);
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix.AsSpan(20, 4),
            (int)operation);
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix.AsSpan(24, 4),
            payload.Length);
        var prefixChecksum = ComputeSha256(prefix.AsSpan(0, 28));
        prefixChecksum.CopyTo(prefix, 28);
        stream.Write(prefix, 0, prefix.Length);
        options.FaultInjector?.OnFaultPoint(
            WorkflowFileStoreFaultPoint.AfterFramePrefixWrite,
            snapshot.RunId,
            nextSequence);

        stream.Write(payload, 0, payload.Length);
        options.FaultInjector?.OnFaultPoint(
            WorkflowFileStoreFaultPoint.AfterFramePayloadWrite,
            snapshot.RunId,
            nextSequence);
        options.FaultInjector?.OnFaultPoint(
            WorkflowFileStoreFaultPoint.BeforePayloadFlush,
            snapshot.RunId,
            nextSequence);
        stream.Flush(flushToDisk: true);
        options.FaultInjector?.OnFaultPoint(
            WorkflowFileStoreFaultPoint.AfterPayloadFlushBeforeCommitMarker,
            snapshot.RunId,
            nextSequence);

        var footer = new byte[FrameFooterBytes];
        CommitMagic.CopyTo(footer, 0);
        BinaryPrimitives.WriteInt64LittleEndian(
            footer.AsSpan(8, 8),
            nextSequence);
        BinaryPrimitives.WriteInt32LittleEndian(
            footer.AsSpan(16, 4),
            payload.Length);
        payloadChecksum.CopyTo(footer, 20);
        stream.Write(footer, 0, footer.Length);
        options.FaultInjector?.OnFaultPoint(
            WorkflowFileStoreFaultPoint
                .AfterCommitMarkerWriteBeforeFlush,
            snapshot.RunId,
            nextSequence);
        stream.Flush(flushToDisk: true);
        options.FaultInjector?.OnFaultPoint(
            WorkflowFileStoreFaultPoint
                .AfterCommitFlushBeforeAcknowledge,
            snapshot.RunId,
            nextSequence);
    }

    private static void WriteHeader(FileStream stream)
    {
        var header = new byte[HeaderBytes];
        FileMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(8, 4),
            FileVersion);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(12, 4),
            0);
        var checksum = ComputeSha256(header.AsSpan(0, 16));
        checksum.CopyTo(header, 16);
        stream.Write(header, 0, header.Length);
    }

    private static void ValidateHeader(byte[] header)
    {
        if (!BytesEqual(header, 0, FileMagic))
        {
            throw new WorkflowFileStoreCorruptionException(
                WorkflowFileStoreReasonCodes.CorruptHeader,
                "The workflow run file header is corrupt.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(8, 4));
        if (version != FileVersion)
        {
            throw new WorkflowFileStoreCorruptionException(
                WorkflowFileStoreReasonCodes.UnsupportedVersion,
                "The workflow run file version is unsupported.");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(12, 4)) != 0)
        {
            throw new WorkflowFileStoreCorruptionException(
                WorkflowFileStoreReasonCodes.UnsupportedVersion,
                "The workflow run file header flags are unsupported.");
        }

        var expected = ComputeSha256(header.AsSpan(0, 16));
        if (!FixedTimeEquals(expected, header.AsSpan(16, 32)))
        {
            throw new WorkflowFileStoreCorruptionException(
                WorkflowFileStoreReasonCodes.CorruptHeader,
                "The workflow run file header checksum does not match.");
        }
    }

    private static void ValidateTransition(
        WorkflowRunSnapshot? previous,
        WorkflowRunSnapshot next,
        WorkflowRunLogOperation operation)
    {
        if (previous is null)
        {
            if (operation != WorkflowRunLogOperation.Create
                || next.Revision != 0
                || next.FencingEpoch != 0
                || next.Lease is not null)
            {
                throw Corrupt(
                    "The first workflow frame is not a valid create operation.");
            }

            return;
        }

        if (!HasSameIdentity(previous, next))
        {
            throw Corrupt(
                "A workflow frame changes immutable run identity.");
        }

        switch (operation)
        {
            case WorkflowRunLogOperation.Commit:
                if (next.Revision != previous.Revision + 1
                    || next.FencingEpoch != previous.FencingEpoch
                    || !LeaseEquals(previous.Lease, next.Lease))
                {
                    throw Corrupt(
                        "A workflow commit frame violates revision or lease invariants.");
                }

                break;
            case WorkflowRunLogOperation.Cancel:
                if (next.Revision != previous.Revision + 1
                    || !next.CancellationRequested
                    || next.FencingEpoch != previous.FencingEpoch
                    || !LeaseEquals(previous.Lease, next.Lease))
                {
                    throw Corrupt(
                        "A workflow cancellation frame violates its invariants.");
                }

                break;
            case WorkflowRunLogOperation.LeaseAcquire:
                if (next.Revision != previous.Revision
                    || next.FencingEpoch != previous.FencingEpoch + 1
                    || next.Lease is null
                    || next.Lease.FencingEpoch != next.FencingEpoch)
                {
                    throw Corrupt(
                        "A workflow lease-acquire frame violates fencing invariants.");
                }

                break;
            case WorkflowRunLogOperation.LeaseRenew:
                if (next.Revision != previous.Revision
                    || previous.Lease is null
                    || next.Lease is null
                    || next.FencingEpoch != previous.FencingEpoch
                    || !string.Equals(
                        previous.Lease.OwnerId,
                        next.Lease.OwnerId,
                        StringComparison.Ordinal)
                    || previous.Lease.FencingEpoch
                    != next.Lease.FencingEpoch)
                {
                    throw Corrupt(
                        "A workflow lease-renew frame violates its invariants.");
                }

                break;
            case WorkflowRunLogOperation.LeaseRelease:
                if (next.Revision != previous.Revision
                    || previous.Lease is null
                    || next.Lease is not null
                    || next.FencingEpoch != previous.FencingEpoch)
                {
                    throw Corrupt(
                        "A workflow lease-release frame violates its invariants.");
                }

                break;
            default:
                throw new WorkflowFileStoreCorruptionException(
                    WorkflowFileStoreReasonCodes.UnsupportedVersion,
                    "A workflow frame operation is unsupported.");
        }
    }

    private static bool HasSameIdentity(
        WorkflowRunSnapshot left,
        WorkflowRunSnapshot right)
    {
        return string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
               && string.Equals(
                   left.WorkflowId,
                   right.WorkflowId,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.WorkflowVersion,
                   right.WorkflowVersion,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.DefinitionDigest,
                   right.DefinitionDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.InputDigest,
                   right.InputDigest,
                   StringComparison.Ordinal);
    }

    private static bool LeaseEquals(
        WorkflowLeaseSnapshot? left,
        WorkflowLeaseSnapshot? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
                   left.OwnerId,
                   right.OwnerId,
                   StringComparison.Ordinal)
               && left.FencingEpoch == right.FencingEpoch
               && left.ExpiresAt == right.ExpiresAt;
    }

    private static byte[] ComputeSha256(ReadOnlySpan<byte> bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes.ToArray());
    }

    private static byte[] ComputeSha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
    }

    private static bool FixedTimeEquals(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }

    private static bool BytesEqual(
        byte[] source,
        int offset,
        byte[] expected)
    {
        return source.AsSpan(offset, expected.Length)
            .SequenceEqual(expected);
    }

    private static void ReadExactly(
        Stream stream,
        byte[] buffer,
        int offset,
        int count)
    {
        var read = 0;
        while (read < count)
        {
            var next = stream.Read(
                buffer,
                offset + read,
                count - read);
            if (next == 0)
            {
                throw Corrupt(
                    "A committed workflow frame ended unexpectedly.");
            }

            read += next;
        }
    }

    private static WorkflowFileStoreCorruptionException Corrupt(
        string message)
    {
        return new WorkflowFileStoreCorruptionException(
            WorkflowFileStoreReasonCodes.CorruptCommittedFrame,
            message);
    }
}
