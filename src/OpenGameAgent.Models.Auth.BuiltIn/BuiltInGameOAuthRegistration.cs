using OpenGameAgent.Models.BuiltIn;

namespace OpenGameAgent.Models.Auth.BuiltIn;

public static class BuiltInGameOAuthRegistration
{
    private static readonly IReadOnlyCollection<string> ProviderIds = Array.AsReadOnly(new[]
    {
        BuiltInGameProviderAuthentications.AnthropicProviderId,
        BuiltInGameProviderAuthentications.OpenRouterProviderId,
        BuiltInGameProviderAuthentications.XaiProviderId,
        BuiltInGameProviderAuthentications.KimiForCodingProviderId,
        BuiltInGameProviderAuthentications.OpenAICodexProviderId,
    });

    public static IReadOnlyCollection<string> SupportedProviderIds => ProviderIds;

    public static IGameProviderAuthentication Create(
        string providerId,
        BuiltInGameOAuthOptions options)
    {
        var id = RequireProviderId(providerId, nameof(providerId));
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return id switch
        {
            BuiltInGameProviderAuthentications.AnthropicProviderId =>
                BuiltInGameProviderAuthentications.CreateAnthropic(options),
            BuiltInGameProviderAuthentications.OpenRouterProviderId =>
                BuiltInGameProviderAuthentications.CreateOpenRouter(options),
            BuiltInGameProviderAuthentications.XaiProviderId =>
                BuiltInGameProviderAuthentications.CreateXai(options),
            BuiltInGameProviderAuthentications.KimiForCodingProviderId =>
                BuiltInGameProviderAuthentications.CreateKimiForCoding(options),
            BuiltInGameProviderAuthentications.OpenAICodexProviderId =>
                BuiltInGameProviderAuthentications.CreateOpenAICodex(options),
            _ => throw new KeyNotFoundException(
                $"Provider '{id}' does not have a built-in OAuth registration."),
        };
    }

    public static bool TryCreate(
        string providerId,
        BuiltInGameOAuthOptions options,
        out IGameProviderAuthentication? authentication)
    {
        var id = RequireProviderId(providerId, nameof(providerId));
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!ProviderIds.Contains(id, StringComparer.Ordinal))
        {
            authentication = null;
            return false;
        }

        authentication = Create(id, options);
        return true;
    }

    public static int RegisterBuiltInOAuth(
        this BuiltInGameModelRuntimeOptions runtimeOptions,
        BuiltInGameOAuthOptions authenticationOptions,
        bool replaceExisting = false)
    {
        if (runtimeOptions is null)
        {
            throw new ArgumentNullException(nameof(runtimeOptions));
        }

        if (authenticationOptions is null)
        {
            throw new ArgumentNullException(nameof(authenticationOptions));
        }

        var directory = runtimeOptions.Directory
                        ?? throw new ArgumentException(
                            "A model directory is required before OAuth registration.",
                            nameof(runtimeOptions));
        var registered = 0;
        foreach (var providerId in ProviderIds)
        {
            if (directory.GetProvider(providerId) is null)
            {
                continue;
            }

            if (!replaceExisting && runtimeOptions.Authentications.ContainsKey(providerId))
            {
                continue;
            }

            runtimeOptions.Authentications[providerId] = Create(providerId, authenticationOptions);
            registered++;
        }

        return registered;
    }

    private static string RequireProviderId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A bounded provider identifier is required.", parameterName);
        }

        return value;
    }
}
