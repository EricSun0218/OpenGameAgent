using System.Collections.ObjectModel;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.Bedrock;
using OpenGameAgent.Providers.Google;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Models.BuiltIn;

public static class BuiltInGameModelApis
{
    public const string AnthropicMessages = "anthropic-messages";
    public const string AzureOpenAiResponses = "azure-openai-responses";
    public const string BedrockConverseStream = "bedrock-converse-stream";
    public const string GoogleGenerativeAi = "google-generative-ai";
    public const string GoogleVertex = "google-vertex";
    public const string MistralConversations = "mistral-conversations";
    public const string OpenAiCodexResponses = "openai-codex-responses";
    public const string OpenAiCompletions = "openai-completions";
    public const string OpenAiResponses = "openai-responses";
}

public static class BuiltInGameModelConfigurationKeys
{
    public const string EnvironmentVariablesMetadata = "environmentVariables";
    public const string AuthenticationHeader = "auth.header";
    public const string AuthenticationScheme = "auth.scheme";
    public const string AuthenticationStyle = "auth.style";
    public const string AzureApiVersion = "azure.api-version";
    public const string AzureDeploymentName = "azure.deployment-name";
    public const string AzureResourceName = "azure.resource-name";
    public const string GoogleProject = "google.project";
    public const string GoogleLocation = "google.location";
    public const string OpenAiCodexAccountId = "openai-codex.account-id";
    public const string OpenAiCodexEnvironmentVariable = "openai-codex.environment-variable";
    public const string AwsRegion = "aws.region";
    public const string AwsProfile = "aws.profile";
    public const string AwsSkipAuthentication = "aws.skip-authentication";
    public const string AwsSecretAccessKeyMetadata = "aws.secret-access-key";
    public const string AwsSessionTokenMetadata = "aws.session-token";
}

public sealed class GameModelProviderTransportConfiguration
{
    public Uri? BaseUrl { get; set; }

    public BedrockConverseTransport? BedrockTransport { get; set; }

    public IDictionary<string, string?> Headers { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public IDictionary<string, string> Options { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameModelTransportConfigurationContext
{
    internal GameModelTransportConfigurationContext(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ModelRequest request)
    {
        Provider = provider;
        Model = model;
        Request = request;
    }

    public GameProviderDescriptor Provider { get; }

    public GameModelDescriptor Model { get; }

    public ModelRequest Request { get; }
}

public delegate ValueTask<GameModelProviderTransportConfiguration?> GameModelTransportConfigurationResolver(
    GameModelTransportConfigurationContext context,
    CancellationToken cancellationToken);

public delegate IGameProviderAuthentication BuiltInGameProviderAuthenticationFactory(
    GameProviderDescriptor provider,
    IReadOnlyList<GameModelDescriptor> models);

public sealed class BuiltInGameModelRuntimeOptions
{
    public BuiltInGameModelRuntimeOptions(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public HttpClient HttpClient { get; }

    public bool AllowInsecureHttp { get; set; }

    public GameModelDirectorySnapshot Directory { get; set; } = GameModelDirectory.LoadBundled();

    public GameModelCatalog Catalog { get; set; } = new();

    public bool ReplaceExistingProviders { get; set; }

    public IDictionary<string, IGameProviderAuthentication> Authentications { get; } =
        new Dictionary<string, IGameProviderAuthentication>(StringComparer.Ordinal);

    public BuiltInGameProviderAuthenticationFactory? CreateAuthentication { get; set; }

    /// <summary>
    /// Trusted host configuration. Base URLs and outbound headers can redirect requests or change their authority.
    /// </summary>
    public IDictionary<string, GameModelProviderTransportConfiguration> ProviderConfigurations { get; } =
        new Dictionary<string, GameModelProviderTransportConfiguration>(StringComparer.Ordinal);

    /// <summary>
    /// Resolves trusted per-request transport configuration. The callback may redirect requests or change headers.
    /// </summary>
    public GameModelTransportConfigurationResolver? ResolveConfigurationAsync { get; set; }

    public Func<string, string?> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

    public GoogleCredentialProvider? VertexApplicationDefaultCredential { get; set; } =
        GoogleVertexCredentials.ApplicationDefault();

    /// <summary>
    /// Observes bounded, allowlisted response metadata. Request headers and credentials are never included.
    /// </summary>
    public ProviderResponseObserver? ResponseObserver { get; set; }

    public int ResponseObserverTimeoutMilliseconds { get; set; } =
        ProviderResponseObserverRunner.DefaultTimeoutMilliseconds;
}

internal sealed class ResolvedGameModelTransportConfiguration
{
    private ResolvedGameModelTransportConfiguration(
        Uri? baseUrl,
        BedrockConverseTransport? bedrockTransport,
        IReadOnlyDictionary<string, string?> headers,
        IReadOnlyDictionary<string, string> options)
    {
        BaseUrl = baseUrl;
        BedrockTransport = bedrockTransport;
        Headers = headers;
        Options = options;
    }

    public Uri? BaseUrl { get; }

    public BedrockConverseTransport? BedrockTransport { get; }

    public IReadOnlyDictionary<string, string?> Headers { get; }

    public IReadOnlyDictionary<string, string> Options { get; }

    public static ResolvedGameModelTransportConfiguration Empty { get; } = new(
        null,
        null,
        new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)),
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

    public static ResolvedGameModelTransportConfiguration Snapshot(GameModelProviderTransportConfiguration? value)
    {
        if (value is null)
        {
            return Empty;
        }

        return new ResolvedGameModelTransportConfiguration(
            value.BaseUrl,
            value.BedrockTransport,
            CopyHeaders(value.Headers),
            CopyOptions(value.Options));
    }

    public ResolvedGameModelTransportConfiguration Overlay(ResolvedGameModelTransportConfiguration value)
    {
        var headers = new Dictionary<string, string?>(Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in value.Headers)
        {
            headers[pair.Key] = pair.Value;
        }

        var options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in value.Options)
        {
            options[pair.Key] = pair.Value;
        }

        return new ResolvedGameModelTransportConfiguration(
            value.BaseUrl ?? BaseUrl,
            value.BedrockTransport ?? BedrockTransport,
            new ReadOnlyDictionary<string, string?>(headers),
            new ReadOnlyDictionary<string, string>(options));
    }

    private static IReadOnlyDictionary<string, string?> CopyHeaders(IDictionary<string, string?> source)
    {
        if (source is null)
        {
            throw new ArgumentException("A model transport configuration dictionary cannot be null.");
        }

        return new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(source, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string> CopyOptions(IDictionary<string, string> source)
    {
        if (source is null)
        {
            throw new ArgumentException("A model transport configuration dictionary cannot be null.");
        }

        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase));
    }
}
