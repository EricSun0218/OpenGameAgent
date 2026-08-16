using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Models;

public enum GameCredentialKind
{
    ApiKey,
    BearerToken,
    OAuth,
    DeveloperHostedToken,
    Ambient,
}

public sealed class GameCredential
{
    public GameCredential(
        GameCredentialKind kind,
        string secret,
        DateTimeOffset? expiresAt = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!Enum.IsDefined(typeof(GameCredentialKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(secret)
            || secret.Length > 65_536
            || secret.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("A credential secret is required and cannot contain line breaks or null characters.", nameof(secret));
        }

        Kind = kind;
        Secret = secret;
        ExpiresAt = expiresAt;
        if (metadata is { Count: > 256 })
        {
            throw new ArgumentException("Credential metadata cannot contain more than 256 entries.", nameof(metadata));
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata ?? new Dictionary<string, string>())
        {
            var key = GameModelDescriptor.RequireId(pair.Key, nameof(metadata));
            if (pair.Value is null || pair.Value.Length > 16_384 || !copy.TryAdd(key, pair.Value))
            {
                throw new ArgumentException("Credential metadata is invalid or contains duplicate keys.", nameof(metadata));
            }
        }

        Metadata = new ReadOnlyDictionary<string, string>(copy);
    }

    public GameCredentialKind Kind { get; }

    public string Secret { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public bool IsExpired(DateTimeOffset now, TimeSpan? refreshSkew = null)
    {
        var skew = refreshSkew ?? TimeSpan.Zero;
        if (skew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshSkew));
        }

        return ExpiresAt is { } expiration
            && (expiration <= now || expiration - now <= skew);
    }

    public override string ToString() => $"{Kind} credential (redacted)";
}

public readonly struct GameCredentialKey : IEquatable<GameCredentialKey>
{
    public GameCredentialKey(string providerId, string profile = "default")
    {
        ProviderId = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        Profile = GameModelDescriptor.RequireId(profile, nameof(profile));
    }

    public string ProviderId { get; }

    public string Profile { get; }

    public bool Equals(GameCredentialKey other) =>
        string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal)
        && string.Equals(Profile, other.Profile, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GameCredentialKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((ProviderId is null ? 0 : StringComparer.Ordinal.GetHashCode(ProviderId)) * 397)
                ^ (Profile is null ? 0 : StringComparer.Ordinal.GetHashCode(Profile));
        }
    }

    public static bool operator ==(GameCredentialKey left, GameCredentialKey right) => left.Equals(right);

    public static bool operator !=(GameCredentialKey left, GameCredentialKey right) => !left.Equals(right);

    internal void EnsureValid(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(Profile))
        {
            throw new ArgumentException("A valid credential key is required.", parameterName);
        }
    }
}

public interface IGameCredentialStore
{
    ValueTask<GameCredential?> GetAsync(GameCredentialKey key, CancellationToken cancellationToken);

    ValueTask SetAsync(GameCredentialKey key, GameCredential credential, CancellationToken cancellationToken);

    ValueTask<bool> RemoveAsync(GameCredentialKey key, CancellationToken cancellationToken);

    ValueTask<GameCredential?> ModifyAsync(
        GameCredentialKey key,
        Func<GameCredential?, CancellationToken, ValueTask<GameCredential?>> mutation,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameCredentialStore : IGameCredentialStore
{
    private readonly Dictionary<GameCredentialKey, GameCredential> _credentials = new();
    private readonly Dictionary<GameCredentialKey, CredentialGate> _keyGates = new();
    private readonly object _stateGate = new();
    private readonly int _capacity;

    public InMemoryGameCredentialStore(int capacity = 128)
    {
        if (capacity <= 0 || capacity > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public async ValueTask<GameCredential?> GetAsync(GameCredentialKey key, CancellationToken cancellationToken)
    {
        key.EnsureValid(nameof(key));
        using var lease = await AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            return _credentials.TryGetValue(key, out var value) ? value : null;
        }
    }

    public async ValueTask SetAsync(
        GameCredentialKey key,
        GameCredential credential,
        CancellationToken cancellationToken)
    {
        key.EnsureValid(nameof(key));
        if (credential is null)
        {
            throw new ArgumentNullException(nameof(credential));
        }

        using var lease = await AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_credentials.ContainsKey(key) && _credentials.Count >= _capacity)
            {
                throw new InvalidOperationException("The credential store reached its capacity.");
            }

            _credentials[key] = credential;
        }
    }

    public async ValueTask<bool> RemoveAsync(GameCredentialKey key, CancellationToken cancellationToken)
    {
        key.EnsureValid(nameof(key));
        using var lease = await AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _credentials.Remove(key);
        }
    }

    public async ValueTask<GameCredential?> ModifyAsync(
        GameCredentialKey key,
        Func<GameCredential?, CancellationToken, ValueTask<GameCredential?>> mutation,
        CancellationToken cancellationToken)
    {
        key.EnsureValid(nameof(key));
        if (mutation is null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }

        return await CancellableOperation.WaitAsync(
            ModifyCoreAsync(key, mutation, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GameCredential?> ModifyCoreAsync(
        GameCredentialKey key,
        Func<GameCredential?, CancellationToken, ValueTask<GameCredential?>> mutation,
        CancellationToken cancellationToken)
    {
        using var lease = await AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        GameCredential? current;
        lock (_stateGate)
        {
            _credentials.TryGetValue(key, out current);
        }

        var next = await mutation(current, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            if (next is null)
            {
                _credentials.Remove(key);
            }
            else
            {
                if (current is null && _credentials.Count >= _capacity)
                {
                    throw new InvalidOperationException("The credential store reached its capacity.");
                }

                _credentials[key] = next;
            }

            return next;
        }
    }

    private async ValueTask<CredentialLease> AcquireAsync(
        GameCredentialKey key,
        CancellationToken cancellationToken)
    {
        CredentialGate gate;
        lock (_stateGate)
        {
            if (!_keyGates.TryGetValue(key, out gate!))
            {
                gate = new CredentialGate();
                _keyGates.Add(key, gate);
            }

            gate.References++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CredentialLease(this, key, gate);
        }
        catch
        {
            ReleaseReference(key, gate, releaseSemaphore: false);
            throw;
        }
    }

    private void ReleaseReference(GameCredentialKey key, CredentialGate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        lock (_stateGate)
        {
            gate.References--;
            if (gate.References == 0)
            {
                _keyGates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class CredentialGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class CredentialLease : IDisposable
    {
        private InMemoryGameCredentialStore? _owner;
        private readonly GameCredentialKey _key;
        private readonly CredentialGate _gate;

        public CredentialLease(
            InMemoryGameCredentialStore owner,
            GameCredentialKey key,
            CredentialGate gate)
        {
            _owner = owner;
            _key = key;
            _gate = gate;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseReference(_key, _gate, releaseSemaphore: true);
        }
    }
}

public sealed class GameProviderAuthStatus
{
    public GameProviderAuthStatus(
        bool configured,
        string source,
        GameCredentialKind? kind = null,
        DateTimeOffset? expiresAt = null,
        string? error = null)
    {
        if (configured && error is not null)
        {
            throw new ArgumentException("Configured authentication cannot carry an error.", nameof(error));
        }

        if (error is not null && (string.IsNullOrWhiteSpace(error) || error.Length > 65_536))
        {
            throw new ArgumentException("An authentication error must contain at most 65,536 characters.", nameof(error));
        }

        if (kind is { } credentialKind && !Enum.IsDefined(typeof(GameCredentialKind), credentialKind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Configured = configured;
        Source = GameModelDescriptor.RequireId(source, nameof(source));
        Kind = kind;
        ExpiresAt = expiresAt;
        Error = error;
    }

    public bool Configured { get; }

    public string Source { get; }

    public GameCredentialKind? Kind { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public string? Error { get; }
}

public sealed class GameProviderAuthResolution
{
    public GameProviderAuthResolution(
        GameCredential? credential,
        string source,
        Uri? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        IReadOnlyDictionary<string, string>? configuration = null)
    {
        if (baseUrl is not null
            && (!baseUrl.IsAbsoluteUri
                || baseUrl.UserInfo.Length > 0
                || baseUrl.Fragment.Length > 0
                || baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "An authentication base URL must be an absolute HTTP or HTTPS URI without embedded credentials or a fragment.",
                nameof(baseUrl));
        }

        Source = GameModelDescriptor.RequireId(source, nameof(source));
        Credential = credential;
        BaseUrl = baseUrl;
        Headers = CopyHeaders(headers);
        Configuration = CopyConfiguration(configuration);
    }

    public GameCredential? Credential { get; }

    public string Source { get; }

    public Uri? BaseUrl { get; }

    public IReadOnlyDictionary<string, string?> Headers { get; }

    public IReadOnlyDictionary<string, string> Configuration { get; }

    private static IReadOnlyDictionary<string, string?> CopyHeaders(
        IReadOnlyDictionary<string, string?>? source)
    {
        if (source is { Count: > 64 })
        {
            throw new ArgumentException("Authentication headers cannot contain more than 64 entries.", nameof(source));
        }

        var copy = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source ?? new Dictionary<string, string?>())
        {
            if (!IsHeaderName(pair.Key)
                || pair.Value is { Length: > 16_384 }
                || pair.Value?.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                || !copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    "Authentication headers contain an invalid or case-insensitively duplicate entry.",
                    nameof(source));
            }
        }

        return new ReadOnlyDictionary<string, string?>(copy);
    }

    private static IReadOnlyDictionary<string, string> CopyConfiguration(
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is { Count: > 256 })
        {
            throw new ArgumentException(
                "Authentication configuration cannot contain more than 256 entries.",
                nameof(source));
        }

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source ?? new Dictionary<string, string>())
        {
            var key = GameModelDescriptor.RequireId(pair.Key, nameof(source));
            if (pair.Value is null
                || pair.Value.Length > 16_384
                || pair.Value.IndexOf('\0') >= 0
                || !copy.TryAdd(key, pair.Value))
            {
                throw new ArgumentException(
                    "Authentication configuration contains an invalid or case-insensitively duplicate entry.",
                    nameof(source));
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static bool IsHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
        {
            return false;
        }

        try
        {
            using var request = new System.Net.Http.HttpRequestMessage();
            return request.Headers.TryAddWithoutValidation(name, "value");
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class GameAuthInteraction
{
    public Func<Uri, CancellationToken, ValueTask>? OpenBrowserAsync { get; set; }

    public Func<string, bool, CancellationToken, ValueTask<string>>? PromptAsync { get; set; }

    public Func<string, CancellationToken, ValueTask>? NotifyAsync { get; set; }
}

public interface IGameProviderAuthentication
{
    IReadOnlyCollection<string> Schemes { get; }

    ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken);

    ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken);

    ValueTask<GameCredential> LoginAsync(
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken);

    ValueTask LogoutAsync(CancellationToken cancellationToken);
}

public sealed class StaticGameProviderAuthentication : IGameProviderAuthentication
{
    private readonly GameProviderAuthStatus _status;
    private readonly GameProviderAuthResolution? _resolution;

    public StaticGameProviderAuthentication(
        bool configured = true,
        string source = "ambient",
        GameCredential? credential = null)
    {
        if (!configured && credential is not null)
        {
            throw new ArgumentException("Unconfigured static authentication cannot expose a credential.", nameof(credential));
        }

        _status = new GameProviderAuthStatus(
            configured,
            source,
            credential?.Kind,
            credential?.ExpiresAt,
            configured ? null : "The provider is not configured.");
        _resolution = configured && credential is not null
            ? new GameProviderAuthResolution(credential, source)
            : null;
    }

    public IReadOnlyCollection<string> Schemes { get; } = Array.AsReadOnly(new[] { "ambient" });

    public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<GameProviderAuthStatus>(_status);
    }

    public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<GameProviderAuthResolution?>(_resolution);
    }

    public ValueTask<GameCredential> LoginAsync(
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Static authentication does not expose a login flow.");

    public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Static authentication cannot be logged out.");
}

public sealed class EnvironmentGameProviderAuthentication : IGameProviderAuthentication
{
    private readonly string _variableName;
    private readonly GameCredentialKind _kind;
    private readonly string _source;
    private readonly Func<string, string?> _read;

    public EnvironmentGameProviderAuthentication(
        string variableName,
        GameCredentialKind kind = GameCredentialKind.ApiKey,
        string source = "environment",
        Func<string, string?>? read = null)
    {
        if (string.IsNullOrWhiteSpace(variableName)
            || variableName.Length > 512
            || variableName.Contains('=')
            || variableName.Contains('\0'))
        {
            throw new ArgumentException("A valid environment variable name is required.", nameof(variableName));
        }

        if (!Enum.IsDefined(typeof(GameCredentialKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _variableName = variableName;
        _kind = kind;
        _source = GameModelDescriptor.RequireId(source, nameof(source));
        _read = read ?? Environment.GetEnvironmentVariable;
    }

    public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

    public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameCredential? credential;
        try
        {
            credential = ReadCredential();
        }
        catch (ArgumentException)
        {
            return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(
                false,
                _source,
                error: $"Environment variable '{_variableName}' contains an invalid credential."));
        }

        var configured = credential is not null;
        return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(
            configured,
            _source,
            credential?.Kind,
            credential?.ExpiresAt,
            error: configured ? null : $"Environment variable '{_variableName}' is not configured."));
    }

    public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credential = ReadCredential();
        return new ValueTask<GameProviderAuthResolution?>(credential is null
            ? null
            : new GameProviderAuthResolution(credential, _source));
    }

    public ValueTask<GameCredential> LoginAsync(
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Environment authentication does not expose a login flow.");

    public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Environment authentication cannot modify the process environment.");

    private GameCredential? ReadCredential()
    {
        var secret = _read(_variableName);
        return string.IsNullOrWhiteSpace(secret) ? null : new GameCredential(_kind, secret);
    }
}

public sealed class StoredGameProviderAuthentication : IGameProviderAuthentication
{
    private readonly GameCredentialKey _key;
    private readonly IGameCredentialStore _store;
    private readonly IReadOnlyCollection<string> _schemes;
    private readonly Func<string, GameAuthInteraction, CancellationToken, ValueTask<GameCredential>>? _login;
    private readonly Func<GameCredential, CancellationToken, ValueTask<GameCredential>>? _refresh;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _refreshSkew;
    private readonly int _refreshTimeoutMilliseconds;
    private readonly int _credentialCommitTimeoutMilliseconds;

    public StoredGameProviderAuthentication(
        string providerId,
        IGameCredentialStore store,
        IReadOnlyCollection<string>? schemes = null,
        Func<string, GameAuthInteraction, CancellationToken, ValueTask<GameCredential>>? login = null,
        Func<GameCredential, CancellationToken, ValueTask<GameCredential>>? refresh = null,
        string profile = "default",
        Func<DateTimeOffset>? clock = null,
        TimeSpan? refreshSkew = null,
        int refreshTimeoutMilliseconds = 15_000,
        int credentialCommitTimeoutMilliseconds = 10_000)
    {
        _key = new GameCredentialKey(providerId, profile);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var copiedSchemes = (schemes ?? new[] { "api-key" })
            .Select(scheme => GameModelDescriptor.RequireId(scheme, nameof(schemes)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (copiedSchemes.Length == 0)
        {
            throw new ArgumentException("At least one authentication scheme is required.", nameof(schemes));
        }

        if (copiedSchemes.Length > 64)
        {
            throw new ArgumentException("At most 64 authentication schemes can be registered.", nameof(schemes));
        }

        _schemes = Array.AsReadOnly(copiedSchemes);
        _login = login;
        _refresh = refresh;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _refreshSkew = refreshSkew ?? TimeSpan.FromMinutes(5);
        if (_refreshSkew < TimeSpan.Zero || _refreshSkew > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(refreshSkew));
        }

        if (refreshTimeoutMilliseconds < 100 || refreshTimeoutMilliseconds > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshTimeoutMilliseconds));
        }

        if (credentialCommitTimeoutMilliseconds < 100 || credentialCommitTimeoutMilliseconds > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(credentialCommitTimeoutMilliseconds));
        }

        _refreshTimeoutMilliseconds = refreshTimeoutMilliseconds;
        _credentialCommitTimeoutMilliseconds = credentialCommitTimeoutMilliseconds;
    }

    public IReadOnlyCollection<string> Schemes => _schemes;

    public async ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var credential = await _store.GetAsync(_key, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return new GameProviderAuthStatus(false, "credential-store");
        }

        if (credential.IsExpired(_clock(), _refreshSkew))
        {
            if (_refresh is not null)
            {
                return new GameProviderAuthStatus(
                    true,
                    "credential-store",
                    credential.Kind,
                    credential.ExpiresAt);
            }

            return new GameProviderAuthStatus(
                false,
                "credential-store",
                credential.Kind,
                credential.ExpiresAt,
                "The stored credential is expired.");
        }

        return new GameProviderAuthStatus(
            true,
            "credential-store",
            credential.Kind,
            credential.ExpiresAt);
    }

    public async ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
    {
        var credential = await _store.ModifyAsync(
            _key,
            async (current, token) =>
            {
                if (current is null || !current.IsExpired(_clock(), _refreshSkew) || _refresh is null)
                {
                    return current;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_refreshTimeoutMilliseconds);
                GameCredential refreshed;
                try
                {
                    refreshed = await CancellableOperation.WaitAsync(
                            _refresh(current, timeout.Token),
                            timeout.Token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The credential refresh returned no credential.");
                }
                catch (OperationCanceledException exception)
                    when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The credential refresh exceeded {_refreshTimeoutMilliseconds} ms.",
                        exception);
                }

                if (refreshed.IsExpired(_clock(), _refreshSkew))
                {
                    throw new InvalidOperationException("The credential refresh returned an expired credential.");
                }

                return refreshed;
            },
            cancellationToken).ConfigureAwait(false);
        return credential is null || credential.IsExpired(_clock(), _refreshSkew)
            ? null
            : new GameProviderAuthResolution(credential, "credential-store");
    }

    public async ValueTask<GameCredential> LoginAsync(
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var validScheme = GameModelDescriptor.RequireId(scheme, nameof(scheme));
        if (!_schemes.Contains(validScheme, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Authentication scheme '{validScheme}' is not supported.");
        }

        if (_login is null)
        {
            throw new InvalidOperationException("This authentication provider does not expose an interactive login flow.");
        }

        var credential = await _login(
            validScheme,
            interaction ?? throw new ArgumentNullException(nameof(interaction)),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The login flow returned no credential.");
        if (credential.IsExpired(_clock(), _refreshSkew))
        {
            throw new InvalidOperationException("The login flow returned an expired credential.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var settlement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        settlement.CancelAfter(_credentialCommitTimeoutMilliseconds);
        try
        {
            await _store.SetAsync(_key, credential, settlement.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && settlement.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The credential commit exceeded {_credentialCommitTimeoutMilliseconds} ms.",
                exception);
        }

        return credential;
    }

    public async ValueTask LogoutAsync(CancellationToken cancellationToken)
    {
        _ = await _store.RemoveAsync(_key, cancellationToken).ConfigureAwait(false);
    }
}
