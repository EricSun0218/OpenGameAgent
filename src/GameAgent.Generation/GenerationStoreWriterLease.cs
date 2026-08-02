using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GameAgent.Generation;

internal sealed class GenerationStoreWriterLease : IDisposable
{
    private static readonly ConcurrentDictionary<string, object> OwnedPaths =
        new(PathComparer());

    private readonly string _path;
    private readonly object _ownershipToken;
    private FileStream? _stream;

    private GenerationStoreWriterLease(
        string path,
        object ownershipToken,
        FileStream stream)
    {
        _path = path;
        _ownershipToken = ownershipToken;
        _stream = stream;
    }

    public static GenerationStoreWriterLease Acquire(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var ownershipToken = new object();
        if (!OwnedPaths.TryAdd(fullPath, ownershipToken))
        {
            throw new IOException(
                $"Generation store '{fullPath}' already has an active writer.");
        }

        FileStream? stream = null;
        var lockHeld = false;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
            stream.Lock(0, 1);
            lockHeld = true;
            return new GenerationStoreWriterLease(
                fullPath,
                ownershipToken,
                stream);
        }
        catch
        {
            if (lockHeld)
            {
                BestEffortUnlock(stream);
            }

            BestEffortDispose(stream);
            RemoveOwnership(fullPath, ownershipToken);
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

        try
        {
            BestEffortUnlock(stream);
            stream.Dispose();
        }
        finally
        {
            RemoveOwnership(_path, _ownershipToken);
        }
    }

    private static void BestEffortUnlock(FileStream? stream)
    {
        try
        {
            stream?.Unlock(0, 1);
        }
        catch (IOException)
        {
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
        }
    }

    private static void RemoveOwnership(string path, object ownershipToken)
    {
        if (OwnedPaths.TryGetValue(path, out var current)
            && ReferenceEquals(current, ownershipToken))
        {
            _ = OwnedPaths.TryRemove(path, out _);
        }
    }

    private static StringComparer PathComparer() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
