using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.Persistence;

/// <summary>
/// Private append-only model-context provenance. Corrupt or identity-mismatched data fails closed;
/// credentials and hidden reasoning are never added by the official provenance extension.
/// </summary>
public sealed class FileGameModelContextProvenanceStore : IGameModelContextProvenanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly int _maximumEntriesPerActor;
    private readonly long _maximumFileBytes;

    public FileGameModelContextProvenanceStore(
        string rootDirectory,
        int maximumEntriesPerActor = 100_000,
        long maximumFileBytes = 1024L * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A provenance storage directory is required.", nameof(rootDirectory));
        }

        if (maximumEntriesPerActor < 1 || maximumEntriesPerActor > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntriesPerActor));
        }

        if (maximumFileBytes < 1_024 || maximumFileBytes > 100L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _root = Path.GetFullPath(rootDirectory);
        _maximumEntriesPerActor = maximumEntriesPerActor;
        _maximumFileBytes = maximumFileBytes;
    }

    public async ValueTask AppendAsync(
        GameModelContextProvenanceEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(entry.Session);
        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadAllAsync(entry.Session, path, cancellationToken).ConfigureAwait(false);
            var duplicate = existing.FirstOrDefault(value => string.Equals(value.EntryId, entry.EntryId, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                if (!Equivalent(duplicate, entry))
                {
                    throw new PersistenceException("A provenance entry ID was reused for different content.");
                }

                return;
            }

            if (existing.Count >= _maximumEntriesPerActor)
            {
                throw new PersistenceException("The provenance store reached its configured actor capacity.");
            }

            var line = JsonSerializer.Serialize(Encode(entry), JsonOptions) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            var currentLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (currentLength > _maximumFileBytes - bytes.Length)
            {
                throw new PersistenceException("The provenance store reached its configured file-size limit.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PersistenceException("Unable to append model-context provenance.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GameModelContextProvenanceEntry>> ListAsync(
        GameSessionKey session,
        string? inputId,
        int maximum,
        CancellationToken cancellationToken)
    {
        ValidateSession(session);
        if (inputId is { Length: > 1_024 } || inputId?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The input ID exceeds its contract bound.", nameof(inputId));
        }

        if (maximum < 1 || maximum > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        var path = PathFor(session);
        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await ReadAllAsync(session, path, cancellationToken).ConfigureAwait(false);
            return Array.AsReadOnly(values
                .Where(value => inputId is null || string.Equals(value.InputId, inputId, StringComparison.Ordinal))
                .OrderByDescending(value => value.OperationalTimestamp)
                .ThenByDescending(value => value.EntryId, StringComparer.Ordinal)
                .Take(maximum)
                .OrderBy(value => value.OperationalTimestamp)
                .ThenBy(value => value.EntryId, StringComparer.Ordinal)
                .ToArray());
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<GameModelContextProvenanceEntry>> ReadAllAsync(
        GameSessionKey session,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<GameModelContextProvenanceEntry>();
        }

        var info = new FileInfo(path);
        if (info.Length < 1 || info.Length > _maximumFileBytes)
        {
            throw new PersistenceException("The provenance file has an invalid size.");
        }

        var result = new List<GameModelContextProvenanceEntry>();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 64 * 1024, leaveOpen: false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || line.Length > 10_000_000)
            {
                throw new PersistenceException("The provenance file contains an invalid record boundary.");
            }

            ProvenanceDocument document;
            try
            {
                document = JsonSerializer.Deserialize<ProvenanceDocument>(line, JsonOptions)
                    ?? throw new PersistenceException("The provenance file contains a null record.");
            }
            catch (JsonException exception)
            {
                throw new PersistenceException("The provenance file contains invalid JSON.", exception);
            }

            var value = Decode(document);
            if (!value.Session.Equals(session))
            {
                throw new PersistenceException("The provenance file identity does not match its storage path.");
            }

            if (result.Any(existing => string.Equals(existing.EntryId, value.EntryId, StringComparison.Ordinal)))
            {
                throw new PersistenceException("The provenance file contains duplicate entry IDs.");
            }

            result.Add(value);
            if (result.Count > _maximumEntriesPerActor)
            {
                throw new PersistenceException("The provenance file exceeds its configured actor capacity.");
            }
        }

        return result;
    }

    private string PathFor(GameSessionKey session)
    {
        ValidateSession(session);
        var hash = Hash(session.SessionId + "\n" + session.ActorId);
        return Path.Combine(_root, hash.Substring(0, 2), hash + ".jsonl");
    }

    private static ProvenanceDocument Encode(GameModelContextProvenanceEntry entry) => new()
    {
        Format = 1,
        EntryId = entry.EntryId,
        SessionId = entry.Session.SessionId,
        ActorId = entry.Session.ActorId,
        InputId = entry.InputId,
        RunId = entry.RunId,
        Turn = entry.Turn,
        Kind = entry.Kind,
        DetailsJson = entry.DetailsJson,
        OperationalTimestamp = entry.OperationalTimestamp,
    };

    private static GameModelContextProvenanceEntry Decode(ProvenanceDocument document)
    {
        if (document.Format != 1)
        {
            throw new PersistenceException("The provenance record format is unsupported.");
        }

        try
        {
            return new GameModelContextProvenanceEntry(
                document.EntryId ?? throw new PersistenceException("The provenance entry ID is missing."),
                new GameSessionKey(
                    document.SessionId ?? throw new PersistenceException("The provenance session ID is missing."),
                    document.ActorId ?? throw new PersistenceException("The provenance actor ID is missing.")),
                document.InputId ?? throw new PersistenceException("The provenance input ID is missing."),
                document.RunId ?? throw new PersistenceException("The provenance run ID is missing."),
                document.Turn,
                document.Kind ?? throw new PersistenceException("The provenance kind is missing."),
                document.DetailsJson ?? throw new PersistenceException("The provenance details are missing."),
                document.OperationalTimestamp);
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PersistenceException("The provenance record is invalid.", exception);
        }
    }

    private static bool Equivalent(GameModelContextProvenanceEntry left, GameModelContextProvenanceEntry right) =>
        left.Session.Equals(right.Session)
        && string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
        && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && left.Turn == right.Turn
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal);

    private static void ValidateSession(GameSessionKey session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId)
            || string.IsNullOrWhiteSpace(session.ActorId)
            || session.SessionId.Length > 1_024
            || session.ActorId.Length > 1_024
            || session.SessionId.Any(char.IsControl)
            || session.ActorId.Any(char.IsControl))
        {
            throw new ArgumentException("A valid bounded session key is required.", nameof(session));
        }
    }

    private static string Hash(string value)
    {
        using var algorithm = SHA256.Create();
        var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var item in bytes)
        {
            builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private sealed class ProvenanceDocument
    {
        public int Format { get; set; }

        public string? EntryId { get; set; }

        public string? SessionId { get; set; }

        public string? ActorId { get; set; }

        public string? InputId { get; set; }

        public string? RunId { get; set; }

        public int Turn { get; set; }

        public string? Kind { get; set; }

        public string? DetailsJson { get; set; }

        public DateTimeOffset OperationalTimestamp { get; set; }
    }
}
