using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

internal sealed class FileStore
{
    private readonly string _directory;
    private readonly long _maximumFileBytes;
    private readonly SemaphoreSlim[] _gates;

    public FileStore(string directory, long maximumFileBytes, int concurrencyStripes)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A persistence directory is required.", nameof(directory));
        }

        if (maximumFileBytes < 1 || maximumFileBytes > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        if (concurrencyStripes < 1 || concurrencyStripes > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrencyStripes));
        }

        _directory = Path.GetFullPath(directory);
        _maximumFileBytes = maximumFileBytes;
        _gates = new SemaphoreSlim[concurrencyStripes];
        for (var index = 0; index < _gates.Length; index++)
        {
            _gates[index] = new SemaphoreSlim(1, 1);
        }

        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public string PathFor(string identity, string suffix)
    {
        using var algorithm = SHA256.Create();
        var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(identity));
        var name = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        return Path.Combine(_directory, name + suffix);
    }

    public SemaphoreSlim GateFor(string identity)
    {
        var hash = StringComparer.Ordinal.GetHashCode(identity) & int.MaxValue;
        return _gates[hash % _gates.Length];
    }

    public async ValueTask<IDisposable> AcquireProcessLeaseAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        var path = PathFor(identity, ".lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void EnsurePathFor(string path, string identity, string suffix, string documentKind)
    {
        if (!string.Equals(Path.GetFullPath(path), PathFor(identity, suffix), StringComparison.Ordinal))
        {
            throw new PersistenceException($"The {documentKind} identity does not match its storage path.");
        }
    }

    public async ValueTask<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > _maximumFileBytes)
            {
                throw new PersistenceException("A persistence file exceeds the configured size limit.");
            }

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureUnambiguous(document.RootElement);
            return JsonSerializer.Deserialize<T>(document.RootElement.GetRawText())
                ?? throw new PersistenceException("A persistence file is empty.");
        }
        catch (JsonException exception)
        {
            throw new PersistenceException("A persistence file contains invalid JSON.", exception);
        }
    }

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new PersistenceException("A persistence file contains duplicate JSON property names.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    public async ValueTask WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (stream.Length > _maximumFileBytes)
                {
                    throw new PersistenceException("The persistence document exceeds the configured size limit.");
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static T DecodeDocument<T>(string documentKind, Func<T> decode)
    {
        try
        {
            return decode();
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or FormatException
                                          or OverflowException)
        {
            throw new PersistenceException($"The {documentKind} contains invalid data.", exception);
        }
    }
}

public sealed class PersistenceException : Exception
{
    public PersistenceException(string message)
        : base(message)
    {
    }

    public PersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
