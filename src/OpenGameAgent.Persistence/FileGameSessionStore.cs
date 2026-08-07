using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class FileGameSessionStore : IGameSessionStore
{
    private const string Suffix = ".session.json";
    private readonly FileStore _files;

    public FileGameSessionStore(
        string directory,
        long maximumFileBytes = 64_000_000,
        int concurrencyStripes = 64)
    {
        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
    }

    public async ValueTask<GameSessionSnapshot?> LoadAsync(
        GameSessionKey key,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var identity = IdentityFor(key);
        var gate = _files.GateFor(identity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Decode(await _files.ReadAsync<SessionDocument>(_files.PathFor(identity, Suffix), cancellationToken).ConfigureAwait(false));
            if (session is not null && !session.Key.Equals(key))
            {
                throw new PersistenceException("The session document identity does not match its storage key.");
            }

            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameSessionSaveResult> SaveAsync(
        GameSessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var identity = IdentityFor(snapshot.Key);
        var path = _files.PathFor(identity, Suffix);
        var gate = _files.GateFor(identity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Decode(await _files.ReadAsync<SessionDocument>(path, cancellationToken).ConfigureAwait(false));
            if (current is not null && !current.Key.Equals(snapshot.Key))
            {
                throw new PersistenceException("The session document identity does not match its storage key.");
            }

            var currentRevision = current?.Revision ?? 0;
            if (currentRevision != expectedRevision)
            {
                return new GameSessionSaveResult(
                    saved: false,
                    current ?? new GameSessionSnapshot(snapshot.Key, 0));
            }

            if (snapshot.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A saved snapshot revision must advance by exactly one.", nameof(snapshot));
            }

            await _files.WriteAtomicAsync(path, Encode(snapshot), cancellationToken).ConfigureAwait(false);
            return new GameSessionSaveResult(saved: true, snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SessionDocument Encode(GameSessionSnapshot snapshot) => new()
    {
        FormatVersion = 1,
        SessionId = snapshot.Key.SessionId,
        ActorId = snapshot.Key.ActorId,
        Revision = snapshot.Revision,
        Messages = snapshot.Messages.Select(AgentMessageCodec.Encode).ToList(),
        ProcessedInputIds = snapshot.ProcessedInputIds.ToList(),
        LastMoment = snapshot.LastMoment is null ? null : MomentDocument.Encode(snapshot.LastMoment.Value),
    };

    private static void ValidateKey(GameSessionKey key)
    {
        if (string.IsNullOrWhiteSpace(key.SessionId) || string.IsNullOrWhiteSpace(key.ActorId))
        {
            throw new ArgumentException("A valid game session key is required.", nameof(key));
        }
    }

    private static string IdentityFor(GameSessionKey key) => string.Concat(
        key.SessionId.Length.ToString(CultureInfo.InvariantCulture),
        ":",
        key.SessionId,
        key.ActorId.Length.ToString(CultureInfo.InvariantCulture),
        ":",
        key.ActorId);

    private static GameSessionSnapshot? Decode(SessionDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        if (document.FormatVersion != 1)
        {
            throw new PersistenceException($"Unsupported session format version '{document.FormatVersion}'.");
        }

        return FileStore.DecodeDocument(
            "session document",
            () => new GameSessionSnapshot(
                new GameSessionKey(document.SessionId, document.ActorId),
                document.Revision,
                (document.Messages ?? new List<MessageDocument>()).Select(AgentMessageCodec.Decode).ToArray(),
                document.ProcessedInputIds ?? new List<string>(),
                document.LastMoment?.Decode()));
    }

    private sealed class SessionDocument
    {
        public int FormatVersion { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public long Revision { get; set; }

        public List<MessageDocument>? Messages { get; set; }

        public List<string>? ProcessedInputIds { get; set; }

        public MomentDocument? LastMoment { get; set; }
    }
}

internal sealed class MomentDocument
{
    public string TimelineId { get; set; } = string.Empty;

    public long Tick { get; set; }

    public string? CalendarJson { get; set; }

    public static MomentDocument Encode(GameMoment moment) => new()
    {
        TimelineId = moment.TimelineId,
        Tick = moment.Tick,
        CalendarJson = moment.CalendarJson,
    };

    public GameMoment Decode() => new(TimelineId, Tick, CalendarJson);
}
