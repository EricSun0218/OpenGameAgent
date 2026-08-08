using System.Collections.ObjectModel;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Providers.MessageGateway;

public delegate ValueTask<string?> MessageGatewayAccessTokenProvider(CancellationToken cancellationToken);

public enum MessageGatewayToolChoiceMode
{
    Auto,
    None,
    Required,
    Function,
}

public static class MessageGatewayParameterKeys
{
    public const string Debug = "message-gateway.debug";
    public const string ToolChoice = "message-gateway.tool-choice";
}

public sealed class MessageGatewayProviderOptions
{
    public MessageGatewayProviderOptions(HttpClient httpClient, Uri baseUrl)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
    }

    public HttpClient HttpClient { get; }

    public Uri BaseUrl { get; }

    public string? AccessToken { get; set; }

    public MessageGatewayAccessTokenProvider? GetAccessTokenAsync { get; set; }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ProviderResponseObserver? ResponseObserver { get; set; }

    public int ResponseObserverTimeoutMilliseconds { get; set; } =
        ProviderResponseObserverRunner.DefaultTimeoutMilliseconds;

    public string ProviderId { get; set; } = "message-gateway";

    public string ApiId { get; set; } = "message-gateway";

    public bool Debug { get; set; }

    public MessageGatewayToolChoiceMode? ToolChoice { get; set; }

    public string? ToolName { get; set; }

    public bool AllowInsecureHttp { get; set; }

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseBytes { get; set; } = 32_000_000;

    public int MaxEventBytes { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 64_000;

    public int MaxEvents { get; set; } = 100_000;

    public int MaxJsonDepth { get; set; } = 128;

    public int MaxContentBlocks { get; set; } = 10_000;

    public int MaxContentCharacters { get; set; } = 16_000_000;

    public int MaxToolCalls { get; set; } = 1_000;

    public int MaxPartialSnapshotWork { get; set; } = 64_000_000;
}

internal sealed class MessageGatewaySettings
{
    public MessageGatewaySettings(MessageGatewayProviderOptions options)
    {
        if (options.BaseUrl is null
            || !options.BaseUrl.IsAbsoluteUri
            || options.BaseUrl.UserInfo.Length > 0
            || options.BaseUrl.Fragment.Length > 0
            || options.BaseUrl.Scheme != Uri.UriSchemeHttp && options.BaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The message gateway base URL must be an absolute HTTP or HTTPS URL without embedded credentials or a fragment.",
                nameof(options));
        }

        if (options.BaseUrl.Scheme == Uri.UriSchemeHttp
            && !options.BaseUrl.IsLoopback
            && !options.AllowInsecureHttp)
        {
            throw new ArgumentException(
                "Remote message gateway endpoints must use HTTPS unless insecure HTTP is explicitly enabled.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ProviderId)
            || options.ProviderId.Length > 256
            || options.ProviderId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(options.ApiId)
            || options.ApiId.Length > 256
            || options.ApiId.Any(char.IsControl)
            || options.ResponseObserverTimeoutMilliseconds is < 1 or > 30_000
            || options.MaxRequestBytes is < 2 or > 100_000_000
            || options.MaxResponseBytes is < 2 or > 100_000_000
            || options.MaxEventBytes is < 2 or > 100_000_000
            || options.MaxErrorCharacters is < 1 or > 10_000_000
            || options.MaxEvents is < 1 or > 1_000_000
            || options.MaxJsonDepth is < 1 or > 1_024
            || options.MaxContentBlocks is < 1 or > 100_000
            || options.MaxContentCharacters is < 1 or > 100_000_000
            || options.MaxToolCalls is < 1 or > 100_000
            || options.MaxPartialSnapshotWork is < 1 or > 1_000_000_000)
        {
            throw new ArgumentException("One or more message gateway identifiers or bounds are invalid.", nameof(options));
        }

        ValidateCredential(options.AccessToken, nameof(options));
        ProviderHeaderGuard.Validate(options.Headers, nameof(options));
        if (options.Headers.Keys.Any(name =>
                string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Accept and Content-Type are controlled by the message gateway transport.", nameof(options));
        }

        if (options.Headers.TryGetValue("Authorization", out var authorization)
            && (string.IsNullOrWhiteSpace(authorization) || authorization.Any(char.IsControl)))
        {
            throw new ArgumentException("A configured message gateway authorization header is invalid.", nameof(options));
        }

        if (options.ToolChoice is { } choice && !Enum.IsDefined(typeof(MessageGatewayToolChoiceMode), choice))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ToolChoice == MessageGatewayToolChoiceMode.Function)
        {
            RequireToolName(options.ToolName, nameof(options));
        }
        else if (options.ToolName is not null)
        {
            throw new ArgumentException("A tool name is valid only for function tool choice.", nameof(options));
        }

        HttpClient = options.HttpClient;
        Endpoint = BuildEndpoint(options.BaseUrl);
        AccessToken = options.AccessToken;
        GetAccessTokenAsync = options.GetAccessTokenAsync;
        Headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase));
        ResponseObserver = options.ResponseObserver;
        ResponseObserverTimeoutMilliseconds = options.ResponseObserverTimeoutMilliseconds;
        ProviderId = options.ProviderId;
        ApiId = options.ApiId;
        Debug = options.Debug;
        ToolChoice = options.ToolChoice;
        ToolName = options.ToolName;
        MaxRequestBytes = options.MaxRequestBytes;
        MaxResponseBytes = options.MaxResponseBytes;
        MaxEventBytes = Math.Min(options.MaxEventBytes, options.MaxResponseBytes);
        MaxErrorCharacters = options.MaxErrorCharacters;
        MaxEvents = options.MaxEvents;
        MaxJsonDepth = options.MaxJsonDepth;
        MaxContentBlocks = options.MaxContentBlocks;
        MaxContentCharacters = options.MaxContentCharacters;
        MaxToolCalls = options.MaxToolCalls;
        MaxPartialSnapshotWork = options.MaxPartialSnapshotWork;
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; }

    public string? AccessToken { get; }

    public MessageGatewayAccessTokenProvider? GetAccessTokenAsync { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public ProviderResponseObserver? ResponseObserver { get; }

    public int ResponseObserverTimeoutMilliseconds { get; }

    public string ProviderId { get; }

    public string ApiId { get; }

    public bool Debug { get; }

    public MessageGatewayToolChoiceMode? ToolChoice { get; }

    public string? ToolName { get; }

    public int MaxRequestBytes { get; }

    public int MaxResponseBytes { get; }

    public int MaxEventBytes { get; }

    public int MaxErrorCharacters { get; }

    public int MaxEvents { get; }

    public int MaxJsonDepth { get; }

    public int MaxContentBlocks { get; }

    public int MaxContentCharacters { get; }

    public int MaxToolCalls { get; }

    public int MaxPartialSnapshotWork { get; }

    public static void ValidateCredential(string? value, string parameterName)
    {
        if ((value?.Length ?? 0) > 65_536
            || value is { Length: > 0 } && string.IsNullOrWhiteSpace(value)
            || value?.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) == true)
        {
            throw new ArgumentException(
                "A message gateway credential is empty, too large, or contains invalid control characters.",
                parameterName);
        }
    }

    public static string RequireToolName(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-empty tool name is required.", parameterName);
        }

        return value;
    }

    private static Uri BuildEndpoint(Uri baseUrl)
    {
        var builder = new UriBuilder(baseUrl);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            path += "/messages";
        }

        builder.Path = path;
        return builder.Uri;
    }
}
