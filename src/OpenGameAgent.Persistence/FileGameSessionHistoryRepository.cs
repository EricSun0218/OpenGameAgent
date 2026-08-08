using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class FileGameHistoryOptions
{
    public FileGameHistoryOptions(string rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("A history root directory is required.", nameof(rootDirectory))
            : rootDirectory;
    }

    public string RootDirectory { get; }
    public GameHistoryLimits? Limits { get; set; }
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(20);
}

public sealed class FileGameSessionHistoryRepository : IGameSessionHistoryRepository
{
    private const int FormatVersion = 1;
    private const string Extension = ".ogahistory.jsonl";
    private readonly string _root;
    private readonly GameHistoryLimits _limits;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _lockRetryDelay;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = false };

    public FileGameSessionHistoryRepository(FileGameHistoryOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _root = Path.GetFullPath(options.RootDirectory);
        _limits = (options.Limits ?? new GameHistoryLimits()).CopyAndValidate();
        _lockTimeout = RequireDuration(options.LockTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(10), nameof(options.LockTimeout));
        _lockRetryDelay = RequireDuration(options.LockRetryDelay, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1), nameof(options.LockRetryDelay));
        Directory.CreateDirectory(_root);
    }

    public async Task<GameSessionHistory> CreateAsync(
        GameHistoryCreateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GameHistoryCreateOptions();
        var id = options.Id ?? Guid.NewGuid().ToString("N");
        GameHistoryValidation.SessionId(id, nameof(options.Id), _limits);
        GameHistoryValidation.OptionalIdentifier(options.ParentSessionId, nameof(options.ParentSessionId), _limits);
        if (options.MetadataJson is not null)
        {
            GameHistoryValidation.JsonObject(options.MetadataJson, nameof(options.MetadataJson), _limits.MaxPayloadCharacters);
        }

        var path = SessionPath(id);
        await WithLockPathAsync(
            Path.Combine(_root, ".repository.lck"),
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    throw new GameHistoryException(GameHistoryErrorCode.AlreadyExists, $"History session already exists: {id}.");
                }

                if (CountSessionFiles() >= _limits.MaxSessions)
                {
                    throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history repository is full.");
                }

                var now = DateTimeOffset.UtcNow;
                var metadata = new GameHistoryMetadata(id, now, options.ParentSessionId, options.MetadataJson, now);
                try
                {
                    PublishNewSnapshot(path, metadata, new GameHistoryState(_limits));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new GameHistoryException(GameHistoryErrorCode.Storage, $"Failed to create history session {id}.", exception);
                }
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return new GameSessionHistory(new FileGameSessionHistoryStorage(this, id), _limits);
    }

    public async Task<GameSessionHistory> OpenAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.SessionId(sessionId, nameof(sessionId), _limits);
        await ReadAsync(sessionId, loaded => loaded.Metadata, cancellationToken).ConfigureAwait(false);
        return new GameSessionHistory(new FileGameSessionHistoryStorage(this, sessionId), _limits);
    }

    public async Task<GameHistoryListPage> ListAsync(
        GameHistoryListQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new GameHistoryListQuery();
        var limit = GameHistoryValidation.Limit(query.Limit, _limits);
        if (query.AfterSessionId is not null)
        {
            GameHistoryValidation.SessionId(query.AfterSessionId, nameof(query.AfterSessionId), _limits);
        }

        var ordered = await ReadAllMetadataAsync(cancellationToken).ConfigureAwait(false);
        var start = CursorStart(ordered, query.AfterSessionId);
        var items = ordered.Skip(start).Take(limit).ToArray();
        var next = start + items.Length < ordered.Length ? items.LastOrDefault()?.Id : null;
        return new GameHistoryListPage(items, next);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.SessionId(sessionId, nameof(sessionId), _limits);
        await WithSessionLockAsync(
            sessionId,
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SessionPath(sessionId);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new GameHistoryException(GameHistoryErrorCode.Storage, $"Failed to delete history session {sessionId}.", exception);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameSessionHistory> ForkAsync(
        string sourceSessionId,
        GameHistoryForkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.SessionId(sourceSessionId, nameof(sourceSessionId), _limits);
        options ??= new GameHistoryForkOptions();
        GameHistoryValidation.Fork(options, _limits);
        var targetId = options.Id ?? Guid.NewGuid().ToString("N");
        GameHistoryValidation.SessionId(targetId, nameof(options.Id), _limits);
        if (string.Equals(sourceSessionId, targetId, StringComparison.Ordinal))
        {
            throw new GameHistoryException(GameHistoryErrorCode.AlreadyExists, "A fork requires a distinct session ID.");
        }

        var forkedState = await ReadAsync(
            sourceSessionId,
            loaded =>
            {
                if (options.ExpectedSourceSequence is { } expected && expected != loaded.State.Sequence)
                {
                    throw new GameHistoryConcurrencyException(expected, loaded.State.Sequence);
                }

                return loaded.State.CopyForFork(options);
            },
            cancellationToken).ConfigureAwait(false);

        var targetPath = SessionPath(targetId);
        await WithLockPathAsync(
            Path.Combine(_root, ".repository.lck"),
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(targetPath))
                {
                    throw new GameHistoryException(GameHistoryErrorCode.AlreadyExists, $"History session already exists: {targetId}.");
                }

                if (CountSessionFiles() >= _limits.MaxSessions)
                {
                    throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history repository is full.");
                }

                var now = DateTimeOffset.UtcNow;
                var metadata = new GameHistoryMetadata(
                    targetId,
                    now,
                    options.ParentSessionId ?? sourceSessionId,
                    options.MetadataJson,
                    now);
                try
                {
                    PublishNewSnapshot(targetPath, metadata, forkedState);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new GameHistoryException(GameHistoryErrorCode.Storage, $"Failed to create history fork {targetId}.", exception);
                }
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return new GameSessionHistory(new FileGameSessionHistoryStorage(this, targetId), _limits);
    }

    public async Task<GameHistorySearchPage> SearchAsync(
        GameHistorySearchQuery query,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Search(query, _limits);
        var limit = query.Limit ?? Math.Min(_limits.DefaultQueryResults, _limits.MaxSearchResults);
        var sessions = (await ReadAllMetadataAsync(cancellationToken).ConfigureAwait(false))
            .Where(metadata => query.SessionId is null || string.Equals(metadata.Id, query.SessionId, StringComparison.Ordinal))
            .OrderBy(metadata => metadata.Id, StringComparer.Ordinal)
            .ToArray();
        var hits = new List<GameHistorySearchHit>();
        var scannedEntries = 0;
        var cursorPassed = query.Cursor is null;
        foreach (var metadata in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completedCursor = await ReadAsync(
                metadata.Id,
                loaded =>
                {
                    long? entryCursor = null;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var page = loaded.State.FindEntries(new GameHistoryEntryQuery
                        {
                            Type = query.EntryType,
                            Order = GameHistoryOrder.OldestFirst,
                            Limit = _limits.MaxQueryResults,
                            CursorSequence = entryCursor,
                        });
                        foreach (var entry in page.Items)
                        {
                            if (++scannedEntries > _limits.MaxSearchScannedEntries)
                            {
                                throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The search scan limit was exceeded.");
                            }

                            if (!cursorPassed)
                            {
                                cursorPassed = string.CompareOrdinal(metadata.Id, query.Cursor!.SessionId) > 0
                                    || (string.Equals(metadata.Id, query.Cursor.SessionId, StringComparison.Ordinal)
                                        && entry.Sequence > query.Cursor.EntrySequence);
                                if (!cursorPassed)
                                {
                                    continue;
                                }
                            }

                            if (!entry.Type.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                                && !entry.PayloadJson.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var snippet = entry.PayloadJson.Length <= 512 ? entry.PayloadJson : entry.PayloadJson.Substring(0, 512);
                            hits.Add(new GameHistorySearchHit(metadata, entry, snippet));
                            if (hits.Count == limit)
                            {
                                return new GameHistorySearchCursor(metadata.Id, entry.Sequence);
                            }
                        }

                        if (page.NextSequence is null)
                        {
                            return null;
                        }

                        entryCursor = page.NextSequence;
                    }
                },
                cancellationToken).ConfigureAwait(false);
            if (completedCursor is not null)
            {
                return new GameHistorySearchPage(hits, completedCursor);
            }
        }

        return new GameHistorySearchPage(hits, null);
    }

    private async Task<GameHistoryMetadata[]> ReadAllMetadataAsync(CancellationToken cancellationToken)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(_root, $"*{Extension}", SearchOption.TopDirectoryOnly)
                .Take(_limits.MaxSessions + 1)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GameHistoryException(GameHistoryErrorCode.Storage, "Failed to list history sessions.", exception);
        }

        if (files.Length > _limits.MaxSessions)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history repository contains too many sessions.");
        }

        var metadata = new List<GameHistoryMetadata>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var id = fileName.Substring(0, fileName.Length - Extension.Length);
            try
            {
                var item = await WithSessionLockAsync(
                    id,
                    () => ReadHeaderSafely(file),
                    cancellationToken).ConfigureAwait(false);
                metadata.Add(item);
            }
            catch (GameHistoryException exception) when (exception.Code is GameHistoryErrorCode.CorruptStorage or GameHistoryErrorCode.NotFound)
            {
                // Discovery remains usable when one session is corrupt or concurrently deleted; direct open stays strict.
            }
        }

        return metadata
            .OrderByDescending(item => item.ModifiedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal Task<T> ReadAsync<T>(string sessionId, Func<LoadedSession, T> read, CancellationToken cancellationToken) =>
        WithSessionLockAsync(
            sessionId,
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return read(LoadSafely(SessionPath(sessionId), cancellationToken));
            },
            cancellationToken);

    internal Task<T> MutateAsync<T>(
        string sessionId,
        string mutationId,
        Func<GameHistoryState, T> mutation,
        CancellationToken cancellationToken)
    {
        return WithSessionLockAsync(
            sessionId,
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SessionPath(sessionId);
                var loaded = LoadSafely(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var previousSequence = loaded.State.Sequence;
                var result = mutation(loaded.State);
                if (loaded.State.Sequence == previousSequence)
                {
                    return result;
                }

                var item = loaded.State.ExportLog().Last();
                try
                {
                    AppendMutation(path, item);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new GameHistoryCommitException(
                        mutationId,
                        outcomeUnknown: true,
                        $"The durable outcome of history mutation {mutationId} is unknown.",
                        exception);
                }

                return result;
            },
            cancellationToken);
    }

    private LoadedSession Load(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new GameHistoryException(GameHistoryErrorCode.NotFound, $"History session not found: {Path.GetFileNameWithoutExtension(path)}.");
        }

        var endsWithNewline = EndsWithNewline(path);
        HeaderLine? header = null;
        var state = new GameHistoryState(_limits);
        var lineNumber = 0;
        var repairedTornTail = false;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: false))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = ReadBoundedLine(reader, EncodedLineLimit());
                if (line is null)
                {
                    break;
                }

                lineNumber++;
                if (lineNumber == 1)
                {
                    header = DecodeHeader(line, path, lineNumber);
                    continue;
                }

                try
                {
                    state.Replay(DecodeMutation(line, path, lineNumber));
                }
                catch (JsonException) when (!endsWithNewline && reader.Peek() < 0)
                {
                    repairedTornTail = true;
                    break;
                }
                catch (JsonException exception)
                {
                    throw Corrupt(path, lineNumber, "The history mutation is invalid JSON.", exception);
                }
                catch (GameHistoryException exception) when (exception.Code is GameHistoryErrorCode.InvalidInput
                    or GameHistoryErrorCode.AlreadyExists
                    or GameHistoryErrorCode.NotFound
                    or GameHistoryErrorCode.InvalidLane
                    or GameHistoryErrorCode.Conflict)
                {
                    throw Corrupt(path, lineNumber, exception.Message, exception);
                }
            }
        }

        if (header is null)
        {
            throw Corrupt(path, 1, "The history header is missing.");
        }

        var metadata = ValidateHeader(header, path);
        if (repairedTornTail)
        {
            ReplaceSnapshot(path, metadata, state);
        }
        else if (!endsWithNewline)
        {
            try
            {
                using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                append.WriteByte((byte)'\n');
                append.Flush(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new GameHistoryException(GameHistoryErrorCode.Storage, "Failed to repair the history terminator.", exception);
            }
        }

        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        metadata = new GameHistoryMetadata(metadata.Id, metadata.CreatedAt, metadata.ParentSessionId, metadata.MetadataJson, modified);
        return new LoadedSession(metadata, state);
    }

    private LoadedSession LoadSafely(string path, CancellationToken cancellationToken)
    {
        try
        {
            return Load(path, cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GameHistoryException(GameHistoryErrorCode.CorruptStorage, $"History file {path} is not valid UTF-8.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GameHistoryException(GameHistoryErrorCode.Storage, $"Failed to read history file {path}.", exception);
        }
    }

    private GameHistoryMetadata ReadHeader(string path)
    {
        if (!File.Exists(path))
        {
            throw new GameHistoryException(GameHistoryErrorCode.NotFound, "The history session was deleted while listing.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, false);
        var line = ReadBoundedLine(reader, EncodedLineLimit())
            ?? throw Corrupt(path, 1, "The history header is missing.");
        return ValidateHeader(DecodeHeader(line, path, 1), path);
    }

    private GameHistoryMetadata ReadHeaderSafely(string path)
    {
        try
        {
            return ReadHeader(path);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GameHistoryException(GameHistoryErrorCode.CorruptStorage, $"History header {path} is not valid UTF-8.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GameHistoryException(GameHistoryErrorCode.Storage, $"Failed to read history header {path}.", exception);
        }
    }

    private GameHistoryMetadata ValidateHeader(HeaderLine header, string path)
    {
        if (!string.Equals(header.Kind, "header", StringComparison.Ordinal) || header.Version != FormatVersion)
        {
            throw Corrupt(path, 1, "The history header version is unsupported.");
        }

        try
        {
            GameHistoryValidation.SessionId(header.Id ?? string.Empty, nameof(header.Id), _limits);
            GameHistoryValidation.OptionalIdentifier(header.ParentSessionId, nameof(header.ParentSessionId), _limits);
            if (header.MetadataJson is not null)
            {
                GameHistoryValidation.JsonObject(header.MetadataJson, nameof(header.MetadataJson), _limits.MaxPayloadCharacters);
            }
        }
        catch (GameHistoryException exception)
        {
            throw Corrupt(path, 1, exception.Message, exception);
        }

        var expectedId = Path.GetFileName(path).Substring(0, Path.GetFileName(path).Length - Extension.Length);
        if (!string.Equals(header.Id, expectedId, StringComparison.Ordinal))
        {
            throw Corrupt(path, 1, "The history header ID does not match its file name.");
        }

        DateTimeOffset created;
        try
        {
            if (header.CreatedUnixMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(header.CreatedUnixMilliseconds));
            }

            created = DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw Corrupt(path, 1, "The history creation timestamp is invalid.", exception);
        }

        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return new GameHistoryMetadata(header.Id!, created, header.ParentSessionId, header.MetadataJson, modified);
    }

    private HeaderLine DecodeHeader(string line, string path, int lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize<HeaderLine>(line, _jsonOptions)
                ?? throw Corrupt(path, lineNumber, "The history header is null.");
        }
        catch (JsonException exception)
        {
            throw Corrupt(path, lineNumber, "The history header is invalid JSON.", exception);
        }
    }

    private GameHistoryLogItem DecodeMutation(string line, string path, int lineNumber)
    {
        MutationLine value;
        value = JsonSerializer.Deserialize<MutationLine>(line, _jsonOptions)
            ?? throw Corrupt(path, lineNumber, "The history mutation is null.");

        if (!string.Equals(value.Kind, "mutation", StringComparison.Ordinal)
            || !Enum.TryParse<GameHistoryMutationKind>(value.MutationKind, ignoreCase: false, out var kind)
            || value.Sequence < 1)
        {
            throw Corrupt(path, lineNumber, "The history mutation envelope is invalid.");
        }

        GameHistoryValidation.Identifier(value.MutationId ?? string.Empty, nameof(value.MutationId), _limits);
        GameHistoryEntry? entry = null;
        if (value.Entry is not null)
        {
            GameHistoryValidation.Identifier(value.Entry.Id ?? string.Empty, nameof(value.Entry.Id), _limits);
            GameHistoryValidation.OptionalIdentifier(value.Entry.ParentId, nameof(value.Entry.ParentId), _limits);
            GameHistoryValidation.Type(value.Entry.Type ?? string.Empty, nameof(value.Entry.Type), _limits);
            GameHistoryValidation.Json(value.Entry.PayloadJson ?? string.Empty, nameof(value.Entry.PayloadJson), _limits.MaxPayloadCharacters);
            entry = new GameHistoryEntry(
                value.Entry.Id!,
                value.Sequence,
                value.Entry.ParentId,
                Timestamp(value.Entry.TimestampUnixMilliseconds, path, lineNumber),
                value.Entry.Type!,
                value.Entry.PayloadJson!);
        }

        GameHistoryRecord? record = null;
        if (value.Record is not null)
        {
            GameHistoryValidation.Identifier(value.Record.Id ?? string.Empty, nameof(value.Record.Id), _limits);
            GameHistoryValidation.Identifier(value.Record.Lane ?? string.Empty, nameof(value.Record.Lane), _limits);
            GameHistoryValidation.Type(value.Record.Type ?? string.Empty, nameof(value.Record.Type), _limits);
            GameHistoryValidation.Json(value.Record.PayloadJson ?? string.Empty, nameof(value.Record.PayloadJson), _limits.MaxPayloadCharacters);
            record = new GameHistoryRecord(
                value.Record.Id!,
                value.Sequence,
                Timestamp(value.Record.TimestampUnixMilliseconds, path, lineNumber),
                value.Record.Lane!,
                value.Record.Type!,
                value.Record.PayloadJson!);
        }

        GameHistoryValidation.OptionalIdentifier(value.Lane, nameof(value.Lane), _limits);
        GameHistoryValidation.OptionalIdentifier(value.LeafEntryId, nameof(value.LeafEntryId), _limits);
        GameHistoryValidation.OptionalIdentifier(value.TargetEntryId, nameof(value.TargetEntryId), _limits);
        if (value.Name is not null) GameHistoryValidation.Fact(value.Name, nameof(value.Name), _limits);
        if (value.Label is not null) GameHistoryValidation.Fact(value.Label, nameof(value.Label), _limits);
        ValidateMutationShape(value, kind, entry, record, path, lineNumber);
        return new GameHistoryLogItem(
            value.MutationId!,
            value.Sequence,
            kind,
            entry,
            record,
            value.Lane,
            value.LeafEntryId,
            value.CreatesLane,
            value.Name,
            value.TargetEntryId,
            value.Label);
    }

    private static void ValidateMutationShape(
        MutationLine value,
        GameHistoryMutationKind kind,
        GameHistoryEntry? entry,
        GameHistoryRecord? record,
        string path,
        int line)
    {
        var valid = kind switch
        {
            GameHistoryMutationKind.Entry => entry is not null
                && record is null
                && value.CreatesLane is null
                && value.Name is null
                && value.TargetEntryId is null
                && value.Label is null,
            GameHistoryMutationKind.Record => entry is null
                && record is not null
                && string.Equals(value.Lane, record.Lane, StringComparison.Ordinal)
                && value.LeafEntryId is null
                && value.CreatesLane is null
                && value.Name is null
                && value.TargetEntryId is null
                && value.Label is null,
            GameHistoryMutationKind.Lane => entry is null
                && record is null
                && value.Lane is not null
                && value.CreatesLane is not null
                && value.Name is null
                && value.TargetEntryId is null
                && value.Label is null,
            GameHistoryMutationKind.Name => entry is null
                && record is null
                && value.Lane is null
                && value.LeafEntryId is null
                && value.CreatesLane is null
                && value.Name is not null
                && value.TargetEntryId is null
                && value.Label is null,
            GameHistoryMutationKind.Label => entry is null
                && record is null
                && value.Lane is null
                && value.LeafEntryId is null
                && value.CreatesLane is null
                && value.Name is null
                && value.TargetEntryId is not null,
            _ => false,
        };
        if (!valid)
        {
            throw Corrupt(path, line, "The history mutation fields do not match its kind.");
        }
    }

    private void PublishNewSnapshot(string destination, GameHistoryMetadata metadata, GameHistoryState state)
    {
        var temp = destination + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteSnapshot(temp, metadata, state);
            File.Move(temp, destination);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private void ReplaceSnapshot(string destination, GameHistoryMetadata metadata, GameHistoryState state)
    {
        var temp = destination + ".repair-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteSnapshot(temp, metadata, state);
            File.Replace(temp, destination, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            TryDelete(temp);
            throw new GameHistoryException(GameHistoryErrorCode.Storage, "Failed to publish the torn-tail repair.", exception);
        }
    }

    private void WriteSnapshot(string path, GameHistoryMetadata metadata, GameHistoryState state)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        WriteLine(stream, EncodeHeader(metadata));
        foreach (var item in state.ExportLog())
        {
            WriteLine(stream, EncodeMutation(item));
        }

        stream.Flush(true);
    }

    private void AppendMutation(string path, GameHistoryLogItem item)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        WriteLine(stream, EncodeMutation(item));
        stream.Flush(true);
    }

    private string EncodeHeader(GameHistoryMetadata metadata) => JsonSerializer.Serialize(
        new HeaderLine
        {
            Kind = "header",
            Version = FormatVersion,
            Id = metadata.Id,
            CreatedUnixMilliseconds = metadata.CreatedAt.ToUnixTimeMilliseconds(),
            ParentSessionId = metadata.ParentSessionId,
            MetadataJson = metadata.MetadataJson,
        },
        _jsonOptions);

    private string EncodeMutation(GameHistoryLogItem item) => JsonSerializer.Serialize(
        new MutationLine
        {
            Kind = "mutation",
            MutationId = item.MutationId,
            Sequence = item.Sequence,
            MutationKind = item.Kind.ToString(),
            Lane = item.Lane,
            LeafEntryId = item.LeafEntryId,
            CreatesLane = item.CreatesLane,
            Name = item.Name,
            TargetEntryId = item.TargetEntryId,
            Label = item.Label,
            Entry = item.Entry is null
                ? null
                : new EntryLine
                {
                    Id = item.Entry.Id,
                    ParentId = item.Entry.ParentId,
                    TimestampUnixMilliseconds = item.Entry.Timestamp.ToUnixTimeMilliseconds(),
                    Type = item.Entry.Type,
                    PayloadJson = item.Entry.PayloadJson,
                },
            Record = item.Record is null
                ? null
                : new RecordLine
                {
                    Id = item.Record.Id,
                    TimestampUnixMilliseconds = item.Record.Timestamp.ToUnixTimeMilliseconds(),
                    Lane = item.Record.Lane,
                    Type = item.Record.Type,
                    PayloadJson = item.Record.PayloadJson,
                },
        },
        _jsonOptions);

    private static void WriteLine(Stream stream, string line)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private async Task<T> WithSessionLockAsync<T>(string sessionId, Func<T> operation, CancellationToken cancellationToken)
    {
        return await WithLockPathAsync(SessionPath(sessionId) + ".lck", operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithLockPathAsync<T>(string lockPath, Func<T> operation, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? handle = null;
            try
            {
                handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow - started < _lockTimeout)
            {
                await Task.Delay(_lockRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (IOException exception)
            {
                throw new GameHistoryException(GameHistoryErrorCode.Storage, $"Timed out acquiring the history lock {lockPath}.", exception);
            }

            using (handle)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
        }
    }

    private string SessionPath(string id)
    {
        var path = Path.GetFullPath(Path.Combine(_root, id + Extension));
        if (!string.Equals(Path.GetDirectoryName(path), _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, "The history session path escapes its repository.");
        }

        return path;
    }

    private int CountSessionFiles()
    {
        int count;
        try
        {
            count = Directory.EnumerateFiles(_root, $"*{Extension}", SearchOption.TopDirectoryOnly)
                .Take(_limits.MaxSessions + 1)
                .Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GameHistoryException(GameHistoryErrorCode.Storage, "Failed to count history sessions.", exception);
        }
        if (count > _limits.MaxSessions)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history repository contains too many sessions.");
        }

        return count;
    }

    private long EncodedLineLimit() => Math.Min(int.MaxValue, _limits.MaxPayloadCharacters * 6L + 65_536L);

    private static string? ReadBoundedLine(StreamReader reader, long maxCharacters)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var value = reader.Read();
            if (value < 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (value == '\n')
            {
                if (builder.Length > 0 && builder[builder.Length - 1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            if (builder.Length >= maxCharacters)
            {
                throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "A history line exceeds the configured payload bound.");
            }

            builder.Append((char)value);
        }
    }

    private static bool EndsWithNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
        {
            return false;
        }

        stream.Position = stream.Length - 1;
        return stream.ReadByte() == '\n';
    }

    private static DateTimeOffset Timestamp(long value, string path, int line)
    {
        try
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw Corrupt(path, line, "The history mutation timestamp is invalid.", exception);
        }
    }

    private static int CursorStart(IReadOnlyList<GameHistoryMetadata> values, string? afterId)
    {
        if (afterId is null)
        {
            return 0;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index].Id, afterId, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The list cursor does not identify a visible session.");
    }

    private static TimeSpan RequireDuration(TimeSpan value, TimeSpan min, TimeSpan max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static GameHistoryException Corrupt(string path, int line, string message, Exception? inner = null) =>
        new(GameHistoryErrorCode.CorruptStorage, $"Invalid history file {path} at line {line}: {message}", inner);

    internal sealed class LoadedSession
    {
        internal LoadedSession(GameHistoryMetadata metadata, GameHistoryState state)
        {
            Metadata = metadata;
            State = state;
        }

        internal GameHistoryMetadata Metadata { get; }
        internal GameHistoryState State { get; }
    }

    private sealed class HeaderLine
    {
        public string? Kind { get; set; }
        public int Version { get; set; }
        public string? Id { get; set; }
        public long CreatedUnixMilliseconds { get; set; }
        public string? ParentSessionId { get; set; }
        public string? MetadataJson { get; set; }
    }

    private sealed class MutationLine
    {
        public string? Kind { get; set; }
        public string? MutationId { get; set; }
        public long Sequence { get; set; }
        public string? MutationKind { get; set; }
        public EntryLine? Entry { get; set; }
        public RecordLine? Record { get; set; }
        public string? Lane { get; set; }
        public string? LeafEntryId { get; set; }
        public bool? CreatesLane { get; set; }
        public string? Name { get; set; }
        public string? TargetEntryId { get; set; }
        public string? Label { get; set; }
    }

    private sealed class EntryLine
    {
        public string? Id { get; set; }
        public string? ParentId { get; set; }
        public long TimestampUnixMilliseconds { get; set; }
        public string? Type { get; set; }
        public string? PayloadJson { get; set; }
    }

    private sealed class RecordLine
    {
        public string? Id { get; set; }
        public long TimestampUnixMilliseconds { get; set; }
        public string? Lane { get; set; }
        public string? Type { get; set; }
        public string? PayloadJson { get; set; }
    }
}

internal sealed class FileGameSessionHistoryStorage : IGameSessionHistoryStorage
{
    private readonly FileGameSessionHistoryRepository _repository;
    private readonly string _sessionId;

    internal FileGameSessionHistoryStorage(FileGameSessionHistoryRepository repository, string sessionId)
    {
        _repository = repository;
        _sessionId = sessionId;
    }

    public Task<GameHistoryMetadata> GetMetadataAsync(CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.Metadata, cancellationToken);

    public Task<IReadOnlyList<GameHistoryLane>> GetLanesAsync(CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.GetLanes(), cancellationToken);

    public Task<GameHistoryEntry?> GetEntryAsync(string id, CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.GetEntry(id), cancellationToken);

    public Task<GameHistoryPage<GameHistoryEntry>> FindEntriesAsync(GameHistoryEntryQuery query, CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.FindEntries(query), cancellationToken);

    public Task<GameHistoryPage<GameHistoryEntry>> FindBranchAsync(string lane, GameHistoryBranchQuery query, CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.FindBranch(lane, query), cancellationToken);

    public Task<GameHistoryPage<GameHistoryRecord>> FindRecordsAsync(GameHistoryRecordQuery query, CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.FindRecords(query), cancellationToken);

    public Task<GameHistoryPage<GameHistoryLogItem>> GetLogAsync(GameHistoryLogQuery query, CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.GetLog(query), cancellationToken);

    public Task<string?> GetNameAsync(CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.Name, cancellationToken);

    public Task<string?> GetLabelAsync(string entryId, CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.GetLabel(entryId), cancellationToken);

    public Task<GameHistoryStats> GetStatsAsync(CancellationToken cancellationToken) =>
        _repository.ReadAsync(_sessionId, loaded => loaded.State.GetStats(), cancellationToken);

    public Task<GameHistoryEntryCommit> AppendEntryAsync(string lane, string id, string type, string payloadJson, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        _repository.MutateAsync(_sessionId, mutationId, state => state.AppendEntry(lane, id, type, payloadJson, mutationId, expectedSequence, DateTimeOffset.UtcNow), cancellationToken);

    public Task<GameHistoryRecordCommit> AppendRecordAsync(string lane, string id, string type, string payloadJson, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        _repository.MutateAsync(_sessionId, mutationId, state => state.AppendRecord(lane, id, type, payloadJson, mutationId, expectedSequence, DateTimeOffset.UtcNow), cancellationToken);

    public Task<GameHistoryCommit> CreateLaneAsync(string lane, string? atEntryId, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        _repository.MutateAsync(_sessionId, mutationId, state => state.CreateLane(lane, atEntryId, mutationId, expectedSequence), cancellationToken);

    public Task<GameHistoryCommit> MoveLaneAsync(string lane, string? toEntryId, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        _repository.MutateAsync(_sessionId, mutationId, state => state.MoveLane(lane, toEntryId, mutationId, expectedSequence), cancellationToken);

    public Task<GameHistoryCommit> SetNameAsync(string name, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        _repository.MutateAsync(_sessionId, mutationId, state => state.SetName(name, mutationId, expectedSequence), cancellationToken);

    public Task<GameHistoryCommit> SetLabelAsync(string entryId, string? label, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        _repository.MutateAsync(_sessionId, mutationId, state => state.SetLabel(entryId, label, mutationId, expectedSequence), cancellationToken);
}
