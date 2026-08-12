using System.Security.Claims;

namespace OpenGameAgent.Server;

/// <summary>
/// Identifies a server operation that is scoped to a game session and actor.
/// </summary>
public enum GameAgentServerOperation
{
    Run = 0,
    Stream = 1,
    Steer = 2,
    Abort = 3,
    ReadUsage = 4,
    ClaimActions = 5,
    StreamActions = 6,
    SubmitActionReceipt = 7,
    ReconcileAction = 8,
}

/// <summary>
/// Carries the authenticated request principal and the server-owned resource being accessed.
/// Resource ownership must be derived from <see cref="Principal"/>, never from an owner identifier in a request body.
/// </summary>
public sealed class GameAgentAuthorizationContext
{
    public GameAgentAuthorizationContext(
        ClaimsPrincipal principal,
        GameSessionKey key,
        GameAgentServerOperation operation)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        Key = new GameSessionKey(key.SessionId, key.ActorId);
        Operation = operation;
    }

    public ClaimsPrincipal Principal { get; }

    public GameSessionKey Key { get; }

    public GameAgentServerOperation Operation { get; }
}

/// <summary>
/// Authorizes an authenticated principal for a session/actor resource.
/// Hosts can resolve ownership from claims, a database, a lease service, or another authoritative source.
/// </summary>
public interface IGameAgentOwnerAuthorizer
{
    ValueTask<bool> AuthorizeAsync(
        GameAgentAuthorizationContext context,
        CancellationToken cancellationToken);
}
