using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Providers.OpenAI;

public static class OpenAICodexResponses
{
    public static readonly Uri DefaultEndpoint = new("https://chatgpt.com/backend-api/codex/responses");

    public static OpenAIResponsesProviderOptions CreateOptions(
        HttpClient httpClient,
        string accessToken,
        Uri? endpoint = null,
        bool supportsAdditionalTools = true,
        bool supportsToolSearch = true)
    {
        var credential = PrepareCredential(new OpenAIRequestCredential(accessToken));
        var options = CreateBaseOptions(
            httpClient,
            endpoint,
            supportsAdditionalTools,
            supportsToolSearch);
        options.ApiKey = credential.ApiKey;
        foreach (var header in credential.Headers)
        {
            options.Headers[header.Key] = header.Value;
        }

        return options;
    }

    public static OpenAIResponsesProviderOptions CreateOptions(
        HttpClient httpClient,
        OpenAIRequestCredentialProvider getCredentialAsync,
        Uri? endpoint = null,
        bool supportsAdditionalTools = true,
        bool supportsToolSearch = true)
    {
        if (getCredentialAsync is null)
        {
            throw new ArgumentNullException(nameof(getCredentialAsync));
        }

        var options = CreateBaseOptions(
            httpClient,
            endpoint,
            supportsAdditionalTools,
            supportsToolSearch);
        options.GetCredentialAsync = async cancellationToken =>
        {
            var credential = await getCredentialAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The credential provider returned null.");
            return PrepareCredential(credential);
        };
        return options;
    }

    public static string ExtractAccountId(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 65_536)
        {
            throw new ArgumentException("A bounded access token is required.", nameof(accessToken));
        }

        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length != 3)
            {
                throw new FormatException();
            }

            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > 1_000_000)
            {
                throw new FormatException();
            }

            using var document = JsonDocument.Parse(bytes);
            var accountId = document.RootElement
                .GetProperty("https://api.openai.com/auth")
                .GetProperty("chatgpt_account_id")
                .GetString();
            if (string.IsNullOrWhiteSpace(accountId)
                || accountId.Length > 512
                || accountId.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new FormatException();
            }

            return accountId;
        }
        catch (Exception exception) when (exception is FormatException
                                          or JsonException
                                          or KeyNotFoundException
                                          or InvalidOperationException)
        {
            throw new ArgumentException("The access token does not contain a valid account identifier.", nameof(accessToken), exception);
        }
    }

    private static OpenAIResponsesProviderOptions CreateBaseOptions(
        HttpClient httpClient,
        Uri? endpoint,
        bool supportsAdditionalTools,
        bool supportsToolSearch)
    {
        var options = new OpenAIResponsesProviderOptions(httpClient, endpoint ?? DefaultEndpoint)
        {
            ProviderId = "openai-codex",
            ApiId = "openai-codex-responses",
            AuthenticationStyle = OpenAIAuthenticationStyle.Bearer,
            SystemPromptMode = OpenAISystemPromptMode.Instructions,
            DefaultInstructions = "You are a helpful assistant.",
            ReasoningSummary = "auto",
            TextVerbosity = OpenAITextVerbosity.Low,
            ToolChoice = OpenAIToolChoice.Auto,
            ParallelToolCalls = true,
            AlwaysIncludeEncryptedReasoning = true,
            SupportsDeveloperRole = false,
            SupportsStrictTools = true,
            SupportsGrammarTools = true,
            SupportsAdditionalTools = supportsAdditionalTools,
            SupportsToolSearch = supportsToolSearch,
            SupportsLongCacheRetention = false,
            SessionAffinityFormat = OpenAISessionAffinityFormat.Codex,
        };
        options.Headers["OpenAI-Beta"] = "responses=experimental";
        options.Headers["originator"] = "opengameagent";
        options.Headers["Accept"] = "text/event-stream";
        return options;
    }

    private static OpenAIRequestCredential PrepareCredential(OpenAIRequestCredential credential)
    {
        if (credential is null)
        {
            throw new ArgumentNullException(nameof(credential));
        }

        var token = credential.ApiKey;
        var accountId = ExtractAccountId(token ?? string.Empty);
        var headers = new Dictionary<string, string>(credential.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["chatgpt-account-id"] = accountId,
        };
        return new OpenAIRequestCredential(token, headers);
    }
}
