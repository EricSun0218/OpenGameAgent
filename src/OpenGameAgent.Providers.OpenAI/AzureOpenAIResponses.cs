namespace OpenGameAgent.Providers.OpenAI;

public static class AzureOpenAIResponses
{
    public static OpenAIResponsesProviderOptions CreateOptions(
        HttpClient httpClient,
        string baseUrl,
        string? apiKey = null,
        string apiVersion = "v1")
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            throw new ArgumentException("An API version is required.", nameof(apiVersion));
        }

        return new OpenAIResponsesProviderOptions(httpClient, BuildResponsesEndpoint(baseUrl, apiVersion))
        {
            ApiKey = apiKey,
            AuthenticationStyle = OpenAIAuthenticationStyle.ApiKeyHeader,
            ProviderId = "azure-openai-responses",
            ApiId = "azure-openai-responses",
            SupportsDeveloperRole = true,
            SupportsStrictTools = true,
            SupportsLongCacheRetention = false,
        };
    }

    public static OpenAIResponsesProviderOptions CreateOptionsForResource(
        HttpClient httpClient,
        string resourceName,
        string? apiKey = null,
        string apiVersion = "v1")
    {
        if (string.IsNullOrWhiteSpace(resourceName)
            || resourceName.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("A resource name may contain only letters, digits, and hyphens.", nameof(resourceName));
        }

        return CreateOptions(
            httpClient,
            "https://" + resourceName + ".openai.azure.com/openai/v1",
            apiKey,
            apiVersion);
    }

    public static Uri BuildResponsesEndpoint(string baseUrl, string apiVersion = "v1")
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.UserInfo.Length > 0)
        {
            throw new ArgumentException("The base URL is invalid.", nameof(baseUrl));
        }

        var builder = new UriBuilder(parsed);
        var path = builder.Path.TrimEnd('/');
        var hosted = builder.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                     || builder.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)
                     || builder.Host.EndsWith(".ai.azure.com", StringComparison.OrdinalIgnoreCase);
        if (hosted && (path.Length == 0
                       || path == "/openai"
                       || path == "/openai/v1"
                       || path == "/openai/v1/responses"))
        {
            path = "/openai/v1";
            builder.Query = string.Empty;
        }

        if (!path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            path += "/responses";
        }

        builder.Path = path;
        var query = ParseQuery(builder.Query);
        if (hosted)
        {
            query["api-version"] = apiVersion;
        }

        builder.Query = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return builder.Uri;
    }

    private static IDictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(new[] { '=' }, 2);
            result[Uri.UnescapeDataString(pieces[0])] = pieces.Length == 2
                ? Uri.UnescapeDataString(pieces[1])
                : string.Empty;
        }

        return result;
    }
}
