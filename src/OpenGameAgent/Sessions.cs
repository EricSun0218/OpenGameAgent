using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public readonly struct GameSessionKey : IEquatable<GameSessionKey>
{
    public GameSessionKey(string sessionId, string actorId)
    {
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        ActorId = GameJson.RequireId(actorId, nameof(actorId));
    }

    public string SessionId { get; }

    public string ActorId { get; }

    public bool Equals(GameSessionKey other) =>
        string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
        && string.Equals(ActorId, other.ActorId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GameSessionKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((SessionId is null ? 0 : StringComparer.Ordinal.GetHashCode(SessionId)) * 397)
                ^ (ActorId is null ? 0 : StringComparer.Ordinal.GetHashCode(ActorId));
        }
    }

    public override string ToString() => (SessionId ?? string.Empty) + ":" + (ActorId ?? string.Empty);

    internal GameSessionKey EnsureValid(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(SessionId) || string.IsNullOrWhiteSpace(ActorId))
        {
            throw new ArgumentException("A valid game session key is required.", parameterName);
        }

        return this;
    }
}

public sealed class GameSessionSnapshot
{
    public GameSessionSnapshot(
        GameSessionKey key,
        long revision,
        IReadOnlyList<AgentMessage>? messages = null,
        IReadOnlyCollection<string>? processedInputIds = null,
        GameMoment? lastMoment = null)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Key = key.EnsureValid(nameof(key));
        Revision = revision;
        var copiedMessages = (messages ?? Array.Empty<AgentMessage>()).ToArray();
        if (copiedMessages.Any(message => message is null))
        {
            throw new ArgumentException("A session transcript cannot contain null messages.", nameof(messages));
        }

        var copiedInputIds = (processedInputIds ?? Array.Empty<string>())
            .Select(value => GameJson.RequireId(value, nameof(processedInputIds)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Messages = Array.AsReadOnly(copiedMessages);
        ProcessedInputIds = Array.AsReadOnly(copiedInputIds);
        LastMoment = lastMoment?.EnsureValid(nameof(lastMoment));
    }

    public GameSessionKey Key { get; }

    public long Revision { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public IReadOnlyCollection<string> ProcessedInputIds { get; }

    public GameMoment? LastMoment { get; }
}

public sealed class GameSessionSaveResult
{
    public GameSessionSaveResult(bool saved, GameSessionSnapshot current)
    {
        Saved = saved;
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public bool Saved { get; }

    public GameSessionSnapshot Current { get; }
}

public interface IGameSessionStore
{
    ValueTask<GameSessionSnapshot?> LoadAsync(GameSessionKey key, CancellationToken cancellationToken);

    ValueTask<GameSessionSaveResult> SaveAsync(
        GameSessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameSessionStore : IGameSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<GameSessionKey, GameSessionSnapshot> _sessions = new();
    private readonly int _capacity;

    public InMemoryGameSessionStore(int capacity = 10_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask<GameSessionSnapshot?> LoadAsync(
        GameSessionKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        key.EnsureValid(nameof(key));
        lock (_gate)
        {
            return new ValueTask<GameSessionSnapshot?>(_sessions.TryGetValue(key, out var session) ? Copy(session) : null);
        }
    }

    public ValueTask<GameSessionSaveResult> SaveAsync(
        GameSessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (_gate)
        {
            if (_sessions.TryGetValue(snapshot.Key, out var current))
            {
                if (current.Revision != expectedRevision)
                {
                    return new ValueTask<GameSessionSaveResult>(
                        new GameSessionSaveResult(saved: false, Copy(current)));
                }
            }
            else
            {
                if (expectedRevision != 0)
                {
                    return new ValueTask<GameSessionSaveResult>(
                        new GameSessionSaveResult(
                            saved: false,
                            new GameSessionSnapshot(snapshot.Key, 0)));
                }

                if (_sessions.Count >= _capacity)
                {
                    throw new GameRuntimeLimitException(nameof(_capacity), "The session store reached its capacity.");
                }
            }

            if (snapshot.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A saved snapshot revision must advance by exactly one.", nameof(snapshot));
            }

            var saved = Copy(snapshot);
            _sessions[snapshot.Key] = saved;
            return new ValueTask<GameSessionSaveResult>(new GameSessionSaveResult(saved: true, Copy(saved)));
        }
    }

    private static GameSessionSnapshot Copy(GameSessionSnapshot snapshot) =>
        new(snapshot.Key, snapshot.Revision, snapshot.Messages, snapshot.ProcessedInputIds, snapshot.LastMoment);
}
