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
    ReadAttachment = 9,
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

/// <summary>
/// A bounded opaque credential presented in a JSON request body. This is intended for local
/// engine clients that cannot set HTTP headers. The credential is authentication input only and
/// must never be copied into a game input, transcript, session snapshot, or response.
/// </summary>
public sealed class GameAgentPresentedCredentialContext
{
    public GameAgentPresentedCredentialContext(
        string credential,
        GameSessionKey key,
        GameAgentServerOperation operation)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 4_096)
        {
            throw new ArgumentException("A presented credential must contain between 1 and 4096 characters.", nameof(credential));
        }

        if (credential.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("A presented credential cannot contain control characters.", nameof(credential));
        }

        Credential = credential;
        Key = new GameSessionKey(key.SessionId, key.ActorId);
        Operation = operation;
    }

    public string Credential { get; }

    public GameSessionKey Key { get; }

    public GameAgentServerOperation Operation { get; }
}

/// <summary>
/// Maps a host-issued body credential to an authenticated principal. Ownership is still decided
/// independently by <see cref="IGameAgentOwnerAuthorizer"/> using the returned principal.
/// </summary>
public interface IGameAgentPresentedCredentialAuthenticator
{
    ValueTask<ClaimsPrincipal?> AuthenticateAsync(
        GameAgentPresentedCredentialContext context,
        CancellationToken cancellationToken);
}
