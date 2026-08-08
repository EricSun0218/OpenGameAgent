namespace OpenGameAgent.Models.Auth.BuiltIn;

public sealed class BuiltInGameOAuthOptions
{
    public BuiltInGameOAuthOptions(HttpClient httpClient, IGameCredentialStore credentialStore)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        CredentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public HttpClient HttpClient { get; }

    public IGameCredentialStore CredentialStore { get; }

    public string Profile { get; set; } = "default";

    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromMinutes(5);

    public string? AnthropicClientId { get; set; }

    public string? XaiClientId { get; set; }

    public string? KimiForCodingClientId { get; set; }

    public string? OpenAICodexClientId { get; set; }

    public string OpenAICodexOriginator { get; set; } = "opengameagent";

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    internal OAuthRuntimeSettings Snapshot()
    {
        var profile = RequireId(Profile, nameof(Profile));
        if (LoginTimeout < TimeSpan.FromSeconds(10) || LoginTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(LoginTimeout));
        }

        if (RequestTimeout < TimeSpan.FromMilliseconds(100) || RequestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (RefreshSkew < TimeSpan.Zero || RefreshSkew > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(RefreshSkew));
        }

        return new OAuthRuntimeSettings(
            HttpClient,
            CredentialStore,
            profile,
            LoginTimeout,
            RequestTimeout,
            RefreshSkew,
            OptionalClientId(AnthropicClientId, nameof(AnthropicClientId)),
            OptionalClientId(XaiClientId, nameof(XaiClientId)),
            OptionalClientId(KimiForCodingClientId, nameof(KimiForCodingClientId)),
            OptionalClientId(OpenAICodexClientId, nameof(OpenAICodexClientId)),
            RequireValue(OpenAICodexOriginator, 256, nameof(OpenAICodexOriginator)),
            Clock ?? throw new ArgumentException("An OAuth clock is required.", nameof(Clock)),
            DelayAsync ?? throw new ArgumentException("An OAuth delay strategy is required.", nameof(DelayAsync)));
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A bounded non-empty profile identifier is required.", parameterName);
        }

        return value;
    }

    private static string? OptionalClientId(string? value, string parameterName) =>
        value is null ? null : RequireValue(value, 4096, parameterName);

    private static string RequireValue(string value, int maximum, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximum
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A bounded non-empty OAuth identifier is required.", parameterName);
        }

        return value;
    }
}

internal sealed class OAuthRuntimeSettings
{
    public OAuthRuntimeSettings(
        HttpClient httpClient,
        IGameCredentialStore credentialStore,
        string profile,
        TimeSpan loginTimeout,
        TimeSpan requestTimeout,
        TimeSpan refreshSkew,
        string? anthropicClientId,
        string? xaiClientId,
        string? kimiForCodingClientId,
        string? openAICodexClientId,
        string openAICodexOriginator,
        Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        HttpClient = httpClient;
        CredentialStore = credentialStore;
        Profile = profile;
        LoginTimeout = loginTimeout;
        RequestTimeout = requestTimeout;
        RefreshSkew = refreshSkew;
        AnthropicClientId = anthropicClientId;
        XaiClientId = xaiClientId;
        KimiForCodingClientId = kimiForCodingClientId;
        OpenAICodexClientId = openAICodexClientId;
        OpenAICodexOriginator = openAICodexOriginator;
        Clock = clock;
        DelayAsync = delayAsync;
    }

    public HttpClient HttpClient { get; }

    public IGameCredentialStore CredentialStore { get; }

    public string Profile { get; }

    public TimeSpan LoginTimeout { get; }

    public TimeSpan RequestTimeout { get; }

    public TimeSpan RefreshSkew { get; }

    public string? AnthropicClientId { get; }

    public string? XaiClientId { get; }

    public string? KimiForCodingClientId { get; }

    public string? OpenAICodexClientId { get; }

    public string OpenAICodexOriginator { get; }

    public Func<DateTimeOffset> Clock { get; }

    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; }
}
