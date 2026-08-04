using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GameAgent.Persistence;

/// <summary>
/// Owns the only writable handle for one normalized persistence path.
/// File sharing alone does not reject a second writer on every supported
/// operating system, so the lease combines a process-local path registry with
/// an operating-system byte-range lock on a persistent sidecar held for the
/// handle lifetime. The data file remains available to shared readers.
/// </summary>
internal sealed class ExclusiveFileWriterLease : IDisposable
{
    private const string LockFileSuffix = ".writer.lock";

    private static readonly ConcurrentDictionary<string, object> OwnedPaths =
        new(PathComparer());

    private readonly string _pathKey;
    private readonly object _ownershipToken;
    private FileStream? _lockStream;
    private FileStream? _stream;

    private ExclusiveFileWriterLease(
        string pathKey,
        object ownershipToken,
        FileStream stream,
        FileStream lockStream)
    {
        _pathKey = pathKey;
        _ownershipToken = ownershipToken;
        _stream = stream;
        _lockStream = lockStream;
    }

    public FileStream Stream =>
        Volatile.Read(ref _stream)
        ?? throw new ObjectDisposedException(
            nameof(ExclusiveFileWriterLease));

    public static ExclusiveFileWriterLease Acquire(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var ownershipToken = new object();
        if (!OwnedPaths.TryAdd(fullPath, ownershipToken))
        {
            throw AlreadyOwned(fullPath);
        }

        FileStream? stream = null;
        FileStream? lockStream = null;
        var lockHeld = false;
        try
        {
            lockStream = new FileStream(
                fullPath + LockFileSuffix,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.None);
            lockStream.Lock(0, 1);
            lockHeld = true;
            stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new ExclusiveFileWriterLease(
                fullPath,
                ownershipToken,
                stream,
                lockStream);
        }
        catch
        {
            BestEffortDispose(stream);
            if (lockHeld)
            {
                BestEffortUnlock(lockStream);
            }

            BestEffortDispose(lockStream);
            RemoveProcessOwnership(fullPath, ownershipToken);
            throw;
        }
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        var lockStream = Interlocked.Exchange(ref _lockStream, null);
        System.Diagnostics.Debug.Assert(lockStream is not null);
        Exception? failure = null;
        try
        {
            stream?.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            lockStream?.Unlock(0, 1);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            lockStream?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            RemoveProcessOwnership(_pathKey, _ownershipToken);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static void BestEffortUnlock(FileStream? stream)
    {
        try
        {
            stream?.Unlock(0, 1);
        }
        catch
        {
            // Preserve the acquisition failure.
        }
    }

    private static void BestEffortDispose(FileStream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch
        {
            // Preserve the acquisition failure.
        }
    }

    private static void RemoveProcessOwnership(
        string path,
        object ownershipToken)
    {
        if (OwnedPaths.TryGetValue(path, out var current)
            && ReferenceEquals(current, ownershipToken))
        {
            _ = OwnedPaths.TryRemove(path, out _);
        }
    }

    private static IOException AlreadyOwned(string path)
    {
        return new IOException(
            $"Persistence path '{path}' already has an active writer.");
    }

    private static StringComparer PathComparer()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}
