namespace OpenGameAgent.Models.Auth.BuiltIn;

internal sealed class FallbackGameProviderAuthentication : IGameProviderAuthentication
{
    private readonly IGameProviderAuthentication _interactive;
    private readonly IGameProviderAuthentication _fallback;
    private readonly IReadOnlyCollection<string> _schemes;

    public FallbackGameProviderAuthentication(
        IGameProviderAuthentication interactive,
        IGameProviderAuthentication fallback)
    {
        _interactive = interactive ?? throw new ArgumentNullException(nameof(interactive));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _schemes = Array.AsReadOnly(
            interactive.Schemes.Concat(fallback.Schemes).Distinct(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyCollection<string> Schemes => _schemes;

    public async ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var interactive = await _interactive.CheckAsync(cancellationToken).ConfigureAwait(false);
        return interactive.Configured
            ? interactive
            : await _fallback.CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
    {
        var interactive = await _interactive.ResolveAsync(cancellationToken).ConfigureAwait(false);
        return interactive ?? await _fallback.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<GameCredential> LoginAsync(
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        if (_interactive.Schemes.Contains(scheme, StringComparer.Ordinal))
        {
            return _interactive.LoginAsync(scheme, interaction, cancellationToken);
        }

        if (_fallback.Schemes.Contains(scheme, StringComparer.Ordinal))
        {
            return _fallback.LoginAsync(scheme, interaction, cancellationToken);
        }

        throw new InvalidOperationException($"Authentication scheme '{scheme}' is not supported.");
    }

    public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
        _interactive.LogoutAsync(cancellationToken);
}
