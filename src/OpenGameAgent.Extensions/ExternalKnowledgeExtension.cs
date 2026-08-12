using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public sealed class GameExternalKnowledgeRequest
{
    public GameExternalKnowledgeRequest(GameInput input, string queryJson, int limit)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        QueryJson = RequireJson(queryJson);
        if (limit < 1 || limit > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        Limit = limit;
    }

    public GameInput Input { get; }

    public string QueryJson { get; }

    public int Limit { get; }

    private static string RequireJson(string value)
    {
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
        return value;
    }
}

public sealed class GameExternalKnowledgeItem
{
    public GameExternalKnowledgeItem(
        string id,
        string title,
        string payloadJson,
        string? summary = null,
        string? uri = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Id = Require(id, nameof(id), 512);
        Title = Require(title, nameof(title), 4_096);
        if (payloadJson is null || payloadJson.Length > 10_000_000)
        {
            throw new ArgumentException("A knowledge payload cannot exceed 10000000 characters.", nameof(payloadJson));
        }

        using (var document = JsonDocument.Parse(payloadJson, new JsonDocumentOptions { MaxDepth = 128 }))
        {
            PayloadJson = payloadJson;
        }

        if (summary?.Length > 65_536)
        {
            throw new ArgumentException("A knowledge summary cannot exceed 65536 characters.", nameof(summary));
        }

        Summary = summary;
        if (uri is not null && (!System.Uri.TryCreate(uri, UriKind.Absolute, out _) || uri.Length > 16_384))
        {
            throw new ArgumentException("A knowledge URI must be an absolute bounded URI.", nameof(uri));
        }

        Uri = uri;
        var copied = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (copied.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Knowledge metadata keys and values must be non-null.", nameof(metadata));
        }

        if (copied.Count > 128
            || copied.Any(pair => pair.Key.Length > 256 || pair.Value.Length > 16_384))
        {
            throw new ArgumentException("Knowledge metadata exceeds its configured field limits.", nameof(metadata));
        }

        Metadata = new ReadOnlyDictionary<string, string>(copied);
    }

    public string Id { get; }

    public string Title { get; }

    public string PayloadJson { get; }

    public string? Summary { get; }

    public string? Uri { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    private static string Require(string value, string name, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? throw new ArgumentException($"A value with at most {maximum} characters is required.", name)
            : value;
}

public interface IGameExternalKnowledgeSource
{
    string Id { get; }

    ValueTask<IReadOnlyList<GameExternalKnowledgeItem>> QueryAsync(
        GameExternalKnowledgeRequest request,
        CancellationToken cancellationToken);
}

public sealed class ExternalKnowledgeExtension : IGameAgentExtension
{
    private readonly IReadOnlyDictionary<string, IGameExternalKnowledgeSource> _sources;
    private readonly int _maximumInlineResultCharacters;
    private readonly int _maximumResultCharacters;
    private readonly IGameAgentArtifactStore? _artifactStore;
    private readonly string _schema;

    public ExternalKnowledgeExtension(
        IReadOnlyList<IGameExternalKnowledgeSource> sources,
        int maximumInlineResultCharacters = 262_144,
        IGameAgentArtifactStore? artifactStore = null,
        int maximumResultCharacters = 10_000_000)
    {
        var copied = (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray();
        if (copied.Length == 0 || copied.Any(source => source is null || string.IsNullOrWhiteSpace(source.Id)))
        {
            throw new ArgumentException("At least one source with a valid ID is required.", nameof(sources));
        }

        var duplicate = copied.GroupBy(source => source.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Knowledge source '{duplicate.Key}' is duplicated.", nameof(sources));
        }

        if (maximumInlineResultCharacters < 1_024 || maximumInlineResultCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInlineResultCharacters));
        }

        if (maximumResultCharacters < maximumInlineResultCharacters || maximumResultCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResultCharacters));
        }

        _sources = new ReadOnlyDictionary<string, IGameExternalKnowledgeSource>(
            copied.ToDictionary(source => source.Id, StringComparer.Ordinal));
        _maximumInlineResultCharacters = maximumInlineResultCharacters;
        _maximumResultCharacters = maximumResultCharacters;
        _artifactStore = artifactStore;
        _schema = JsonSerializer.Serialize(new
        {
            type = "object",
            required = new[] { "source", "query" },
            properties = new
            {
                source = new { type = "string", @enum = copied.Select(source => source.Id).OrderBy(value => value, StringComparer.Ordinal) },
                query = new { },
                limit = new { type = "integer", minimum = 1, maximum = 64 },
            },
            additionalProperties = false,
        });
    }

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.external-knowledge",
        "1.0.0",
        "Bounded queries to developer-configured local or remote knowledge sources.",
        new[] { "knowledge", "local-data", "remote-data", "large-results" });

    public void Configure(GameAgentExtensionApi api) =>
        api.RegisterToolProvider(
            "external-knowledge-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { CreateTool(context) }));

    private AgentTool CreateTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "query_external_knowledge",
                "Query one developer-configured local or remote knowledge source. URLs are never chosen by the model.",
                _schema),
            async (arguments, execution, cancellationToken) =>
            {
                var sourceId = arguments.GetProperty("source").GetString() ?? string.Empty;
                if (!_sources.TryGetValue(sourceId, out var source))
                {
                    return ToolResult.Error($"Knowledge source '{sourceId}' is not configured.");
                }

                var request = new GameExternalKnowledgeRequest(
                    context.Input,
                    arguments.GetProperty("query").GetRawText(),
                    arguments.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 8);
                var items = await source.QueryAsync(request, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Knowledge source '{sourceId}' returned null.");
                if (items.Count > request.Limit || items.Any(item => item is null))
                {
                    throw new InvalidOperationException($"Knowledge source '{sourceId}' returned an invalid result set.");
                }

                var duplicate = items.GroupBy(item => item.Id, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate is not null)
                {
                    throw new InvalidOperationException(
                        $"Knowledge source '{sourceId}' returned duplicate item ID '{duplicate.Key}'.");
                }

                var rawCharacters = items.Sum(EstimateCharacters);
                if (rawCharacters > _maximumResultCharacters)
                {
                    throw new InvalidOperationException(
                        $"Knowledge source '{sourceId}' exceeded the configured result limit.");
                }

                var json = Serialize(sourceId, items);
                if (json.Length > _maximumResultCharacters)
                {
                    throw new InvalidOperationException(
                        $"Knowledge source '{sourceId}' exceeded the configured serialized result limit.");
                }
                if (json.Length <= _maximumInlineResultCharacters)
                {
                    return new ToolResult(new AgentContent[] { new JsonContent(json) });
                }

                if (_artifactStore is null)
                {
                    return ToolResult.Error(
                        "The knowledge result exceeded the inline limit and no artifact store is configured.");
                }

                var artifactId = GameExtensionOperationIds.Create(
                    "oga-knowledge-v1:",
                    "query_external_knowledge:" + sourceId,
                    context.Input,
                    execution);
                await _artifactStore.PutAsync(
                    new GameAgentArtifact(
                        artifactId,
                        context.Input.SessionId,
                        context.Input.ActorId,
                        "application/json",
                        json,
                        context.Input.Moment),
                    cancellationToken).ConfigureAwait(false);
                return new ToolResult(new AgentContent[]
                {
                    new JsonContent(JsonSerializer.Serialize(new
                    {
                        artifactId,
                        mediaType = "application/json",
                        totalCharacters = json.Length,
                        readTool = "read_agent_artifact",
                    })),
                });
            },
            ToolRisk.ReadOnly);

    private static string Serialize(string source, IReadOnlyList<GameExternalKnowledgeItem> items) =>
        JsonSerializer.Serialize(new
        {
            source,
            items = items.Select(item => new
            {
                item.Id,
                item.Title,
                item.Summary,
                item.Uri,
                payload = Parse(item.PayloadJson),
                item.Metadata,
            }),
        });

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static long EstimateCharacters(GameExternalKnowledgeItem item) =>
        (long)item.Id.Length
        + item.Title.Length
        + item.PayloadJson.Length
        + (item.Summary?.Length ?? 0)
        + (item.Uri?.Length ?? 0)
        + item.Metadata.Sum(pair => (long)pair.Key.Length + pair.Value.Length);
}

public delegate ValueTask<IReadOnlyDictionary<string, string>> GameKnowledgeHeaderProvider(
    GameExternalKnowledgeRequest request,
    CancellationToken cancellationToken);

public sealed class JsonHttpGameKnowledgeSource : IGameExternalKnowledgeSource
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly GameKnowledgeHeaderProvider? _headers;
    private readonly int _maximumResponseBytes;
    private readonly bool _includeInputPayload;

    public JsonHttpGameKnowledgeSource(
        string id,
        HttpClient client,
        Uri endpoint,
        GameKnowledgeHeaderProvider? headers = null,
        int maximumResponseBytes = 4_000_000,
        bool includeInputPayload = false,
        bool allowInsecureHttp = false)
    {
        Id = string.IsNullOrWhiteSpace(id) || id.Length > 512
            ? throw new ArgumentException("A source ID of at most 512 characters is required.", nameof(id))
            : id;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (!_endpoint.IsAbsoluteUri
            || _endpoint.UserInfo.Length > 0
            || (_endpoint.Scheme != Uri.UriSchemeHttps
                && !(allowInsecureHttp && _endpoint.Scheme == Uri.UriSchemeHttp)))
        {
            throw new ArgumentException(
                "An absolute HTTPS endpoint is required unless insecure HTTP is explicitly enabled.",
                nameof(endpoint));
        }

        if (maximumResponseBytes < 1_024 || maximumResponseBytes > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        _headers = headers;
        _maximumResponseBytes = maximumResponseBytes;
        _includeInputPayload = includeInputPayload;
    }

    public string Id { get; }

    public async ValueTask<IReadOnlyList<GameExternalKnowledgeItem>> QueryAsync(
        GameExternalKnowledgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = JsonSerializer.Serialize(new
        {
            query = Parse(request.QueryJson),
            request.Limit,
            game = new
            {
                request.Input.Type,
                payload = _includeInputPayload ? Parse(request.Input.PayloadJson) : (JsonElement?)null,
                request.Input.Moment.TimelineId,
                request.Input.Moment.Tick,
            },
        });
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (_headers is not null)
        {
            var headers = await _headers(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The knowledge header provider returned null.");
            if (headers.Count > 64)
            {
                throw new InvalidOperationException("The knowledge header provider returned too many headers.");
            }

            foreach (var header in new List<KeyValuePair<string, string>>(headers))
            {
                if (string.IsNullOrWhiteSpace(header.Key)
                    || header.Key.Length > 256
                    || header.Key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                    || header.Value is null
                    || header.Value.Length > 65_536
                    || header.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                {
                    throw new InvalidOperationException("The knowledge header provider returned an invalid header.");
                }

                if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    throw new InvalidOperationException($"Knowledge header '{header.Key}' is invalid.");
                }
            }
        }

        using var response = await _client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length > _maximumResponseBytes)
        {
            throw new InvalidOperationException("The knowledge response exceeded the configured size limit.");
        }

        var bytes = await ReadBoundedAsync(response.Content, _maximumResponseBytes, cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Knowledge source '{Id}' returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        EnsureUnambiguous(document.RootElement);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The knowledge response must contain an items array.");
        }

        var result = new List<GameExternalKnowledgeItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (result.Count >= request.Limit)
            {
                throw new InvalidOperationException("The knowledge response exceeded the requested item limit.");
            }

            result.Add(new GameExternalKnowledgeItem(
                item.GetProperty("id").GetString() ?? string.Empty,
                item.GetProperty("title").GetString() ?? string.Empty,
                item.GetProperty("payload").GetRawText(),
                item.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
                item.TryGetProperty("uri", out var uri) ? uri.GetString() : null,
                ReadMetadata(item)));
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(JsonElement item)
    {
        if (!item.TryGetProperty("metadata", out var metadata))
        {
            return new Dictionary<string, string>();
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Knowledge metadata must be a string object.");
        }

        return metadata.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString()
                        ?? throw new InvalidOperationException("Knowledge metadata values must be strings."),
            StringComparer.Ordinal);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidOperationException("The knowledge response exceeded the configured size limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException("The knowledge response contains duplicate JSON properties.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }
}
