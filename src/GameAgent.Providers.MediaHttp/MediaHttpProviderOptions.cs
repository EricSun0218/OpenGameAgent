namespace GameAgent.Providers.MediaHttp;

public interface IGenerationCredentialSource
{
    ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken);
}

public sealed class MediaHttpProviderOptions
{
    public string Name { get; set; } = "media_http";

    public Uri BaseUri { get; set; } = new("https://localhost/");

    public string? ImagePath { get; set; } = "/v1/images/generations";

    public string? VideoPath { get; set; } = "/v1/videos";

    public string? SpeechPath { get; set; } = "/v1/audio/speech";

    public string? StructuredContentPath { get; set; }

    public string VideoStatusPathTemplate { get; set; } = "/v1/videos/{id}";

    public string VideoContentPathTemplate { get; set; } =
        "/v1/videos/{id}/content";

    public string VideoCancelPathTemplate { get; set; } = "/v1/videos/{id}";

    public string? StructuredContentStatusPathTemplate { get; set; }

    public string? StructuredContentContentPathTemplate { get; set; }

    public string? StructuredContentCancelPathTemplate { get; set; }

    public string AuthorizationScheme { get; set; } = "Bearer";

    public string AuthorizationHeader { get; set; } = "Authorization";

    public string? ArtifactAuthorizationReference { get; set; }

    public bool AllowInsecureLoopback { get; set; }

    public int MaxMetadataResponseBytes { get; set; } = 4 * 1024 * 1024;

    public int MaxInlineArtifactBytes { get; set; } = 32 * 1024 * 1024;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)
            || Name.Length > 128
            || Name.Any(character =>
                !(char.IsLetterOrDigit(character)
                  || character is '_' or '-' or '.' or ':' or '/')))
        {
            throw new ArgumentException(
                "Provider name must be a bounded portable identifier.",
                nameof(Name));
        }
        if (!BaseUri.IsAbsoluteUri
            || !string.IsNullOrEmpty(BaseUri.UserInfo)
            || !string.IsNullOrEmpty(BaseUri.Query)
            || !string.IsNullOrEmpty(BaseUri.Fragment))
        {
            throw new ArgumentException(
                "The media API base URI must be an absolute origin without credentials, query, or fragment.",
                nameof(BaseUri));
        }

        var loopback = BaseUri.IsLoopback
                       || string.Equals(
                           BaseUri.Host,
                           "localhost",
                           StringComparison.OrdinalIgnoreCase);
        if (BaseUri.Scheme != Uri.UriSchemeHttps
            && !(AllowInsecureLoopback
                 && loopback
                 && BaseUri.Scheme == Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "Media APIs require HTTPS. Plain HTTP is allowed only for explicitly enabled loopback endpoints.",
                nameof(BaseUri));
        }

        ValidatePath(ImagePath, nameof(ImagePath));
        ValidatePath(VideoPath, nameof(VideoPath));
        ValidatePath(SpeechPath, nameof(SpeechPath));
        ValidatePath(StructuredContentPath, nameof(StructuredContentPath));
        ValidateTemplate(VideoStatusPathTemplate, nameof(VideoStatusPathTemplate));
        ValidateTemplate(VideoContentPathTemplate, nameof(VideoContentPathTemplate));
        ValidateTemplate(VideoCancelPathTemplate, nameof(VideoCancelPathTemplate));
        ValidateTemplate(
            StructuredContentStatusPathTemplate,
            nameof(StructuredContentStatusPathTemplate));
        ValidateTemplate(
            StructuredContentContentPathTemplate,
            nameof(StructuredContentContentPathTemplate));
        ValidateTemplate(
            StructuredContentCancelPathTemplate,
            nameof(StructuredContentCancelPathTemplate));
        if (ImagePath is null
            && VideoPath is null
            && SpeechPath is null
            && StructuredContentPath is null)
        {
            throw new ArgumentException(
                "Configure at least one media endpoint path.");
        }

        if (MaxMetadataResponseBytes is < 1_024 or > 64 * 1024 * 1024
            || MaxInlineArtifactBytes is < 1_024 or > 1024 * 1024 * 1024
            || RequestTimeout < TimeSpan.FromSeconds(1)
            || RequestTimeout > TimeSpan.FromHours(1)
            || AuthorizationScheme.Length is < 1 or > 64
            || AuthorizationScheme.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_')
            || AuthorizationHeader is not "Authorization" and not "x-api-key")
        {
            throw new ArgumentOutOfRangeException(
                nameof(MediaHttpProviderOptions),
                "Media HTTP limits or authorization settings are invalid.");
        }
    }

    internal Uri Resolve(string path) => new(BaseUri, path);

    internal string PathForModality(string modality) => modality switch
    {
        GameAgent.Generation.GenerationModalities.Image => ImagePath!,
        GameAgent.Generation.GenerationModalities.Video => VideoPath!,
        GameAgent.Generation.GenerationModalities.Speech => SpeechPath!,
        GameAgent.Generation.GenerationModalities.StructuredContent =>
            StructuredContentPath!,
        _ => throw new ArgumentOutOfRangeException(nameof(modality))
    };

    internal string? StatusPathTemplateForModality(string modality) => modality switch
    {
        GameAgent.Generation.GenerationModalities.Video => VideoStatusPathTemplate,
        GameAgent.Generation.GenerationModalities.StructuredContent =>
            StructuredContentStatusPathTemplate,
        _ => null
    };

    internal string? ContentPathTemplateForModality(string modality) => modality switch
    {
        GameAgent.Generation.GenerationModalities.Video => VideoContentPathTemplate,
        GameAgent.Generation.GenerationModalities.StructuredContent =>
            StructuredContentContentPathTemplate,
        _ => null
    };

    internal string? CancelPathTemplateForModality(string modality) => modality switch
    {
        GameAgent.Generation.GenerationModalities.Video => VideoCancelPathTemplate,
        GameAgent.Generation.GenerationModalities.StructuredContent =>
            StructuredContentCancelPathTemplate,
        _ => null
    };

    private static void ValidatePath(string? path, string name)
    {
        if (path is null)
        {
            return;
        }

        if (path.Length is < 1 or > 1_024
            || !path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains('\\')
            || path.Contains('?')
            || path.Contains('#'))
        {
            throw new ArgumentException(
                "Media endpoint paths must be bounded rooted paths without traversal, query, or fragment.",
                name);
        }
    }

    private static void ValidateTemplate(string? path, string name)
    {
        if (path is null)
        {
            return;
        }

        ValidatePath(path, name);
        if (path.Count(character => character == '{') != 1
            || path.Count(character => character == '}') != 1
            || !path.Contains("{id}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A media job path template must contain exactly one '{id}' placeholder.",
                name);
        }
    }
}
