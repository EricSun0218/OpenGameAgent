namespace OpenGameAgent.Models.Auth.BuiltIn;

internal sealed class OAuthClientRegistrationRequiredAuthentication : IGameProviderAuthentication
{
    private readonly IGameProviderAuthentication _fallback;
    private readonly string _providerId;

    public OAuthClientRegistrationRequiredAuthentication(
        string providerId,
        IGameProviderAuthentication fallback)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > 512
            || providerId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A bounded provider identifier is required.", nameof(providerId));
        }

        _providerId = providerId;
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

    public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken) =>
        _fallback.CheckAsync(cancellationToken);

    public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken) =>
        _fallback.ResolveAsync(cancellationToken);

    public ValueTask<GameCredential> LoginAsync(
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            $"OAuth login for provider '{_providerId}' requires an explicitly configured OAuth client ID.");
    }

    public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
        _fallback.LogoutAsync(cancellationToken);
}
