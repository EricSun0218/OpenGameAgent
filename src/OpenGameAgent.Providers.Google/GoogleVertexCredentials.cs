using Google.Apis.Auth.OAuth2;

namespace OpenGameAgent.Providers.Google;

public static class GoogleVertexCredentials
{
    public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    public static GoogleCredentialProvider ApplicationDefault(params string[] scopes)
    {
        var selectedScopes = scopes is { Length: > 0 }
            ? scopes.ToArray()
            : new[] { CloudPlatformScope };
        if (selectedScopes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Google OAuth scopes must be non-empty.", nameof(scopes));
        }

        var gate = new SemaphoreSlim(1, 1);
        GoogleCredential? credential = null;
        return async cancellationToken =>
        {
            if (credential is null)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (credential is null)
                    {
                        var discovered = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken)
                            .ConfigureAwait(false);
                        credential = discovered.IsCreateScopedRequired
                            ? discovered.CreateScoped(selectedScopes)
                            : discovered;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            return await credential.UnderlyingCredential
                .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        };
    }

    public static Uri Endpoint(string project, string location)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new ArgumentException("A Google Cloud project is required.", nameof(project));
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("A Google Cloud location is required.", nameof(location));
        }

        var host = Uri.EscapeDataString(location) + "-aiplatform.googleapis.com";
        var path = "/v1/projects/" + Uri.EscapeDataString(project)
                   + "/locations/" + Uri.EscapeDataString(location)
                   + "/publishers/google/models/{model}:streamGenerateContent";
        return new Uri("https://" + host + path, UriKind.Absolute);
    }
}
