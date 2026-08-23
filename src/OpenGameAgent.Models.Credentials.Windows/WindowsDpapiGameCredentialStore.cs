using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Models;

namespace OpenGameAgent.Models.Credentials.Windows;

public sealed class WindowsDpapiGameCredentialStoreOptions
{
    public WindowsDpapiGameCredentialStoreOptions(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A credential-store directory is required.", nameof(directory));
        }

        Directory = directory;
    }

    public string Directory { get; }

    public int Capacity { get; set; } = 128;

    public int MaximumCredentialBytes { get; set; } = 8 * 1024 * 1024;

    public int MaximumStoreBytes { get; set; } = 32 * 1024 * 1024;

    public int LockTimeoutMilliseconds { get; set; } = 10_000;
}

public sealed class WindowsDpapiGameCredentialStore : IGameCredentialStore
{
    public const string DefaultFileName = "credentials.v1.json";

    private const string LockFileName = "credentials.lock";
    private const string TemporaryFileName = "credentials.v1.json.tmp";
    private const int StoreVersion = 1;
    private const int EntryVersion = 1;
    private const int PayloadVersion = 1;
    private const int EntropyBytes = 32;
    private const int MaximumSupportedCapacity = 10_000;
    private const int MaximumSupportedCredentialBytes = 16 * 1024 * 1024;
    private const int MaximumSupportedStoreBytes = 256 * 1024 * 1024;
    private const int ProtectedDataOverheadAllowance = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directory;
    private readonly string _dataPath;
    private readonly string _lockPath;
    private readonly string _temporaryPath;
    private readonly int _capacity;
    private readonly int _maximumCredentialBytes;
    private readonly int _maximumStoreBytes;
    private readonly int _lockTimeoutMilliseconds;
    private readonly SemaphoreSlim _instanceGate = new(1, 1);

    public WindowsDpapiGameCredentialStore(WindowsDpapiGameCredentialStoreOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        EnsureSupported();
        ValidateOptions(options);
        _directory = Path.GetFullPath(options.Directory);
        _dataPath = Path.Combine(_directory, DefaultFileName);
        _lockPath = Path.Combine(_directory, LockFileName);
        _temporaryPath = Path.Combine(_directory, TemporaryFileName);
        _capacity = options.Capacity;
        _maximumCredentialBytes = options.MaximumCredentialBytes;
        _maximumStoreBytes = options.MaximumStoreBytes;
        _lockTimeoutMilliseconds = options.LockTimeoutMilliseconds;
        EnsureStoragePathsAreSafe(createDirectory: true);
    }

    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public async ValueTask<GameCredential?> GetAsync(
        GameCredentialKey key,
        CancellationToken cancellationToken)
    {
        EnsureValidKey(key, nameof(key));
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return Find(document.Records, key)?.Credential;
    }

    public async ValueTask SetAsync(
        GameCredentialKey key,
        GameCredential credential,
        CancellationToken cancellationToken)
    {
        EnsureValidKey(key, nameof(key));
        if (credential is null)
        {
            throw new ArgumentNullException(nameof(credential));
        }

        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var existing = Find(document.Records, key);
        if (existing is null && document.Records.Count >= _capacity)
        {
            throw new InvalidOperationException("The credential store reached its capacity.");
        }

        var replacement = Protect(key, credential);
        if (existing is null)
        {
            document.Records.Add(replacement);
        }
        else
        {
            document.Records[document.Records.IndexOf(existing)] = replacement;
        }

        await WriteAsync(document.NextRevision(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveAsync(
        GameCredentialKey key,
        CancellationToken cancellationToken)
    {
        EnsureValidKey(key, nameof(key));
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var existing = Find(document.Records, key);
        if (existing is null)
        {
            return false;
        }

        document.Records.Remove(existing);
        await WriteAsync(document.NextRevision(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<GameCredential?> ModifyAsync(
        GameCredentialKey key,
        Func<GameCredential?, CancellationToken, ValueTask<GameCredential?>> mutation,
        CancellationToken cancellationToken)
    {
        EnsureValidKey(key, nameof(key));
        if (mutation is null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }

        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var existing = Find(document.Records, key);
        var next = await mutation(existing?.Credential, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (next is null)
        {
            if (existing is null)
            {
                return null;
            }

            document.Records.Remove(existing);
        }
        else if (existing is null)
        {
            if (document.Records.Count >= _capacity)
            {
                throw new InvalidOperationException("The credential store reached its capacity.");
            }

            document.Records.Add(Protect(key, next));
        }
        else
        {
            document.Records[document.Records.IndexOf(existing)] = Protect(key, next);
        }

        await WriteAsync(document.NextRevision(), cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Windows CurrentUser DPAPI credential persistence is available only on Windows.");
        }
    }

    private static void EnsureValidKey(GameCredentialKey key, string parameterName)
    {
        try
        {
            _ = new GameCredentialKey(key.ProviderId ?? string.Empty, key.Profile ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("A valid credential key is required.", parameterName, exception);
        }
    }

    private static void ValidateOptions(WindowsDpapiGameCredentialStoreOptions options)
    {
        if (options.Capacity <= 0 || options.Capacity > MaximumSupportedCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Capacity));
        }

        if (options.MaximumCredentialBytes < 4 * 1024
            || options.MaximumCredentialBytes > MaximumSupportedCredentialBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumCredentialBytes));
        }

        if (options.MaximumStoreBytes < 64 * 1024
            || options.MaximumStoreBytes > MaximumSupportedStoreBytes
            || options.MaximumStoreBytes < options.MaximumCredentialBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumStoreBytes));
        }

        if (options.LockTimeoutMilliseconds < 100 || options.LockTimeoutMilliseconds > 120_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.LockTimeoutMilliseconds));
        }
    }

    private async ValueTask<StoreLease> AcquireAsync(CancellationToken cancellationToken)
    {
        EnsureSupported();
        var timer = Stopwatch.StartNew();
        if (!await _instanceGate.WaitAsync(
                _lockTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("The credential store could not acquire its write lease in time.");
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureStoragePathsAreSafe(createDirectory: true);
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        _lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.WriteThrough);
                }
                catch (IOException) when (timer.ElapsedMilliseconds < _lockTimeoutMilliseconds)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (IOException)
                {
                    throw new TimeoutException("The credential store could not acquire its write lease in time.");
                }

                try
                {
                    EnsureRegularFile(_lockPath);
                    DeleteInterruptedWrite();
                    return new StoreLease(this, stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            _instanceGate.Release();
            throw;
        }
    }

    private async ValueTask<DecryptedDocument> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStoragePathsAreSafe(createDirectory: false);
        if (!File.Exists(_dataPath))
        {
            return new DecryptedDocument(0, new List<CredentialRecord>());
        }

        EnsureRegularFile(_dataPath);
        var info = new FileInfo(_dataPath);
        if (info.Length <= 0 || info.Length > _maximumStoreBytes)
        {
            throw CorruptStore();
        }

        byte[] bytes;
        using (var stream = new FileStream(
                   _dataPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.SequentialScan))
        {
            if (stream.Length <= 0 || stream.Length > _maximumStoreBytes)
            {
                throw CorruptStore();
            }

            bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(
                    bytes,
                    offset,
                    bytes.Length - offset,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw CorruptStore();
                }

                offset += read;
            }
        }

        try
        {
            StoreDocument? stored;
            try
            {
                stored = JsonSerializer.Deserialize<StoreDocument>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                throw CorruptStore();
            }

            if (stored is null
                || stored.Version != StoreVersion
                || stored.Revision < 0
                || stored.Entries is null
                || stored.Entries.Count > _capacity)
            {
                throw CorruptStore();
            }

            var records = new List<CredentialRecord>(stored.Entries.Count);
            var keys = new HashSet<GameCredentialKey>();
            foreach (var entry in stored.Entries)
            {
                var record = Unprotect(entry);
                if (!keys.Add(record.Key))
                {
                    throw CorruptStore();
                }

                records.Add(record);
            }

            return new DecryptedDocument(stored.Revision, records);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask WriteAsync(
        DecryptedDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStoragePathsAreSafe(createDirectory: false);
        var stored = new StoreDocument
        {
            Version = StoreVersion,
            Revision = document.Revision,
            Entries = document.Records.Select(record => record.Envelope).ToList(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions);
        if (bytes.Length > _maximumStoreBytes)
        {
            throw new InvalidOperationException("The encrypted credential store reached its size limit.");
        }

        try
        {
            if (File.Exists(_temporaryPath))
            {
                EnsureRegularFile(_temporaryPath);
                File.Delete(_temporaryPath);
            }

            using (var stream = new FileStream(
                       _temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularFile(_temporaryPath);
            if (File.Exists(_dataPath))
            {
                EnsureRegularFile(_dataPath);
                File.Replace(_temporaryPath, _dataPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(_temporaryPath, _dataPath);
            }

            EnsureRegularFile(_dataPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            TryDeleteTemporaryFile();
        }
    }

    private CredentialRecord Protect(GameCredentialKey key, GameCredential credential)
    {
        var payload = new CredentialPayload
        {
            Version = PayloadVersion,
            ProviderId = key.ProviderId,
            Profile = key.Profile,
            Kind = (int)credential.Kind,
            Secret = credential.Secret,
            ExpiresAt = credential.ExpiresAt,
            Metadata = credential.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (plaintext.Length > _maximumCredentialBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidOperationException("The credential reached its encrypted storage size limit.");
        }

        var entropy = new byte[EntropyBytes];
        RandomNumberGenerator.Fill(entropy);
        byte[]? protectedData = null;
        try
        {
            protectedData = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
            if (protectedData.Length > _maximumCredentialBytes + ProtectedDataOverheadAllowance)
            {
                throw new InvalidOperationException("The credential reached its encrypted storage size limit.");
            }

            var envelope = new EntryDocument
            {
                Version = EntryVersion,
                Entropy = Convert.ToBase64String(entropy),
                ProtectedData = Convert.ToBase64String(protectedData),
            };
            return new CredentialRecord(key, credential, envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            if (protectedData is not null)
            {
                CryptographicOperations.ZeroMemory(protectedData);
            }
        }
    }

    private CredentialRecord Unprotect(EntryDocument? entry)
    {
        if (entry is null
            || entry.Version != EntryVersion
            || string.IsNullOrWhiteSpace(entry.Entropy)
            || string.IsNullOrWhiteSpace(entry.ProtectedData)
            || entry.Entropy.Length > 256
            || entry.ProtectedData.Length > ((_maximumCredentialBytes + ProtectedDataOverheadAllowance) * 2))
        {
            throw CorruptStore();
        }

        byte[] entropy;
        byte[] protectedData;
        try
        {
            entropy = Convert.FromBase64String(entry.Entropy);
            protectedData = Convert.FromBase64String(entry.ProtectedData);
        }
        catch (FormatException)
        {
            throw CorruptStore();
        }

        byte[]? plaintext = null;
        try
        {
            if (entropy.Length != EntropyBytes
                || protectedData.Length == 0
                || protectedData.Length > _maximumCredentialBytes + ProtectedDataOverheadAllowance)
            {
                throw CorruptStore();
            }

            try
            {
                plaintext = ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                throw CorruptStore();
            }

            if (plaintext.Length == 0 || plaintext.Length > _maximumCredentialBytes)
            {
                throw CorruptStore();
            }

            CredentialPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<CredentialPayload>(plaintext, JsonOptions);
            }
            catch (JsonException)
            {
                throw CorruptStore();
            }

            if (payload is null
                || payload.Version != PayloadVersion
                || !Enum.IsDefined(typeof(GameCredentialKind), payload.Kind)
                || payload.Secret is null)
            {
                throw CorruptStore();
            }

            try
            {
                var key = new GameCredentialKey(payload.ProviderId ?? string.Empty, payload.Profile ?? string.Empty);
                var credential = new GameCredential(
                    (GameCredentialKind)payload.Kind,
                    payload.Secret,
                    payload.ExpiresAt,
                    payload.Metadata);
                return new CredentialRecord(key, credential, entry);
            }
            catch (ArgumentException)
            {
                throw CorruptStore();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(protectedData);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private void EnsureStoragePathsAreSafe(bool createDirectory)
    {
        EnsureDirectoryChainIsSafe(_directory);
        if (createDirectory)
        {
            Directory.CreateDirectory(_directory);
            EnsureDirectoryChainIsSafe(_directory);
        }
        else if (!System.IO.Directory.Exists(_directory))
        {
            throw new IOException("The credential-store directory is unavailable.");
        }

        EnsureRegularFileOrMissing(_dataPath);
        EnsureRegularFileOrMissing(_lockPath);
        EnsureRegularFileOrMissing(_temporaryPath);
    }

    private static void EnsureDirectoryChainIsSafe(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Credential storage cannot use symbolic links or reparse points.");
            }

            current = current.Parent;
        }
    }

    private static void EnsureRegularFileOrMissing(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("Credential storage cannot use symbolic links, reparse points, or directories as files.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsureRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("Credential storage expected a regular file.");
            }
        }
        catch (FileNotFoundException)
        {
            throw new IOException("A credential-store file disappeared during an operation.");
        }
    }

    private void DeleteInterruptedWrite()
    {
        if (!File.Exists(_temporaryPath))
        {
            return;
        }

        EnsureRegularFile(_temporaryPath);
        File.Delete(_temporaryPath);
    }

    private void TryDeleteTemporaryFile()
    {
        try
        {
            if (File.Exists(_temporaryPath))
            {
                EnsureRegularFile(_temporaryPath);
                File.Delete(_temporaryPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static CredentialRecord? Find(
        IEnumerable<CredentialRecord> records,
        GameCredentialKey key) => records.FirstOrDefault(record => record.Key == key);

    private static InvalidDataException CorruptStore() =>
        new("The encrypted credential store is corrupt or cannot be decrypted for the current Windows user.");

    private void Release(StoreLease lease)
    {
        lease.Stream.Dispose();
        _instanceGate.Release();
    }

    private sealed class StoreLease : IDisposable
    {
        private WindowsDpapiGameCredentialStore? _owner;

        public StoreLease(WindowsDpapiGameCredentialStore owner, FileStream stream)
        {
            _owner = owner;
            Stream = stream;
        }

        public FileStream Stream { get; }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(this);
        }
    }

    private sealed class DecryptedDocument
    {
        public DecryptedDocument(long revision, List<CredentialRecord> records)
        {
            Revision = revision;
            Records = records;
        }

        public long Revision { get; }

        public List<CredentialRecord> Records { get; }

        public DecryptedDocument NextRevision()
        {
            if (Revision == long.MaxValue)
            {
                throw new InvalidOperationException("The credential-store revision is exhausted.");
            }

            return new DecryptedDocument(Revision + 1, Records);
        }
    }

    private sealed class CredentialRecord
    {
        public CredentialRecord(GameCredentialKey key, GameCredential credential, EntryDocument envelope)
        {
            Key = key;
            Credential = credential;
            Envelope = envelope;
        }

        public GameCredentialKey Key { get; }

        public GameCredential Credential { get; }

        public EntryDocument Envelope { get; }
    }

    private sealed class StoreDocument
    {
        public int Version { get; set; }

        public long Revision { get; set; }

        public List<EntryDocument>? Entries { get; set; }
    }

    private sealed class EntryDocument
    {
        public int Version { get; set; }

        public string? Entropy { get; set; }

        public string? ProtectedData { get; set; }
    }

    private sealed class CredentialPayload
    {
        public int Version { get; set; }

        public string? ProviderId { get; set; }

        public string? Profile { get; set; }

        public int Kind { get; set; }

        public string? Secret { get; set; }

        public DateTimeOffset? ExpiresAt { get; set; }

        public Dictionary<string, string>? Metadata { get; set; }
    }
}
