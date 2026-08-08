namespace OpenGameAgent.Models.Auth.BuiltIn;

internal sealed class ResolutionOverlayAuthentication : IGameProviderAuthentication
{
    private readonly IGameProviderAuthentication _inner;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public ResolutionOverlayAuthentication(
        IGameProviderAuthentication inner,
        IReadOnlyDictionary<string, string> headers)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _headers = new Dictionary<string, string>(
            headers ?? throw new ArgumentNullException(nameof(headers)),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Schemes => _inner.Schemes;

    public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken) =>
        _inner.CheckAsync(cancellationToken);

    public async ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
    {
        var resolution = await _inner.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (resolution is null)
        {
            return null;
        }

        var headers = new Dictionary<string, string?>(resolution.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _headers)
        {
            headers[pair.Key] = pair.Value;
        }

        return new GameProviderAuthResolution(
            resolution.Credential,
            resolution.Source,
            resolution.BaseUrl,
            headers,
            resolution.Configuration);
    }

    public ValueTask<GameCredential> LoginAsync(
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken) =>
        _inner.LoginAsync(scheme, interaction, cancellationToken);

    public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
        _inner.LogoutAsync(cancellationToken);
}
