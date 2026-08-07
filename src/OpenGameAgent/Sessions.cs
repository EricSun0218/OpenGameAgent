using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    public static bool operator ==(GameSessionKey left, GameSessionKey right) => left.Equals(right);

    public static bool operator !=(GameSessionKey left, GameSessionKey right) => !left.Equals(right);

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
        GameMoment? lastMoment = null,
        IReadOnlyDictionary<string, string>? extensionState = null,
        string? pendingInputId = null)
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
        PendingInputId = pendingInputId is null
            ? null
            : GameJson.RequireId(pendingInputId, nameof(pendingInputId));
        if (PendingInputId is not null && ProcessedInputIds.Contains(PendingInputId, StringComparer.Ordinal))
        {
            throw new ArgumentException("A pending input cannot already be marked as processed.", nameof(pendingInputId));
        }

        LastMoment = lastMoment?.EnsureValid(nameof(lastMoment));
        var copiedExtensionState = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in extensionState ?? new Dictionary<string, string>())
        {
            var stateKey = GameJson.RequireId(pair.Key, nameof(extensionState));
            var value = GameJson.RequireValid(pair.Value, nameof(extensionState));
            if (!copiedExtensionState.TryAdd(stateKey, value))
            {
                throw new ArgumentException($"Duplicate extension state key '{stateKey}'.", nameof(extensionState));
            }
        }

        ExtensionState = new ReadOnlyDictionary<string, string>(copiedExtensionState);
    }

    public GameSessionKey Key { get; }

    public long Revision { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public IReadOnlyCollection<string> ProcessedInputIds { get; }

    /// <summary>
    /// Input whose completed tool turns were durably checkpointed but whose agent run has not reached
    /// a terminal commit. Resubmitting the same input resumes after the checkpoint; a different input
    /// is rejected until this one is settled or explicitly repaired by the host.
    /// </summary>
    public string? PendingInputId { get; }

    public GameMoment? LastMoment { get; }

    /// <summary>
    /// Namespaced extension-owned JSON state. It is persisted but never added to model context automatically.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtensionState { get; }
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
        new(
            snapshot.Key,
            snapshot.Revision,
            snapshot.Messages,
            snapshot.ProcessedInputIds,
            snapshot.LastMoment,
            snapshot.ExtensionState,
            snapshot.PendingInputId);
}
