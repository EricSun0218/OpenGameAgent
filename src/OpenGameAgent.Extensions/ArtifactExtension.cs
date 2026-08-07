using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public sealed class GameAgentArtifact
{
    public GameAgentArtifact(
        string artifactId,
        string sessionId,
        string actorId,
        string mediaType,
        string content,
        GameMoment createdAt)
    {
        ArtifactId = Require(artifactId, 512, nameof(artifactId));
        SessionId = Require(sessionId, 1_024, nameof(sessionId));
        ActorId = Require(actorId, 1_024, nameof(actorId));
        MediaType = Require(mediaType, 512, nameof(mediaType));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (string.IsNullOrWhiteSpace(createdAt.TimelineId))
        {
            throw new ArgumentException("A valid creation moment is required.", nameof(createdAt));
        }

        CreatedAt = createdAt;
    }

    public string ArtifactId { get; }

    public string SessionId { get; }

    public string ActorId { get; }

    public string MediaType { get; }

    public string Content { get; }

    public GameMoment CreatedAt { get; }

    private static string Require(string value, int maximumCharacters, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters
            ? throw new ArgumentException($"A value containing at most {maximumCharacters} characters is required.", name)
            : value;
}

public interface IGameAgentArtifactStore
{
    ValueTask PutAsync(GameAgentArtifact artifact, CancellationToken cancellationToken);

    ValueTask<GameAgentArtifact?> GetAsync(
        string sessionId,
        string actorId,
        string artifactId,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameAgentArtifactStore : IGameAgentArtifactStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string SessionId, string ActorId, string ArtifactId), GameAgentArtifact> _artifacts = new();
    private readonly int _maximumArtifacts;
    private readonly int _maximumArtifactCharacters;
    private readonly long _maximumTotalCharacters;
    private long _totalCharacters;

    public InMemoryGameAgentArtifactStore(
        int maximumArtifacts = 10_000,
        int maximumArtifactCharacters = 10_000_000,
        long maximumTotalCharacters = 100_000_000)
    {
        if (maximumArtifacts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifacts));
        }

        if (maximumArtifactCharacters < 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactCharacters));
        }

        if (maximumTotalCharacters < maximumArtifactCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTotalCharacters));
        }

        _maximumArtifacts = maximumArtifacts;
        _maximumArtifactCharacters = maximumArtifactCharacters;
        _maximumTotalCharacters = maximumTotalCharacters;
    }

    public ValueTask PutAsync(GameAgentArtifact artifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (artifact is null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }

        if (artifact.Content.Length > _maximumArtifactCharacters)
        {
            throw new InvalidOperationException("The artifact exceeds the configured size limit.");
        }

        var key = (artifact.SessionId, artifact.ActorId, artifact.ArtifactId);
        lock (_gate)
        {
            if (_artifacts.TryGetValue(key, out var existing))
            {
                if (!Equivalent(existing, artifact))
                {
                    throw new InvalidOperationException("An artifact ID cannot be reused for different content.");
                }

                return default;
            }

            if (_artifacts.Count >= _maximumArtifacts
                || artifact.Content.Length > _maximumTotalCharacters - _totalCharacters)
            {
                throw new InvalidOperationException("The artifact store reached its configured capacity.");
            }

            _artifacts.Add(key, artifact);
            _totalCharacters += artifact.Content.Length;
        }

        return default;
    }

    public ValueTask<GameAgentArtifact?> GetAsync(
        string sessionId,
        string actorId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(actorId)
            || string.IsNullOrWhiteSpace(artifactId))
        {
            throw new ArgumentException("Artifact IDs and owners are required.");
        }

        lock (_gate)
        {
            return new ValueTask<GameAgentArtifact?>(
                _artifacts.TryGetValue((sessionId, actorId, artifactId), out var artifact) ? artifact : null);
        }
    }

    private static bool Equivalent(GameAgentArtifact left, GameAgentArtifact right) =>
        string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal)
        && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
        && string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal)
        && string.Equals(left.MediaType, right.MediaType, StringComparison.Ordinal)
        && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
        && left.CreatedAt == right.CreatedAt;
}

public sealed class GameAgentArtifactExtension : IGameAgentExtension
{
    private const string ReadSchema = """
        {"type":"object","required":["artifactId"],"properties":{"artifactId":{"type":"string","minLength":1,"maxLength":512},"offset":{"type":"integer","minimum":0},"maximumCharacters":{"type":"integer","minimum":256,"maximum":65536}},"additionalProperties":false}
        """;

    private readonly IGameAgentArtifactStore _store;
    private readonly int _spillToolResultsAboveCharacters;
    private readonly int _maximumInlinePreviewCharacters;

    public GameAgentArtifactExtension(
        IGameAgentArtifactStore store,
        int spillToolResultsAboveCharacters = 65_536,
        int maximumInlinePreviewCharacters = 4_096)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (spillToolResultsAboveCharacters < 1_024 || spillToolResultsAboveCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(spillToolResultsAboveCharacters));
        }

        if (maximumInlinePreviewCharacters < 0
            || maximumInlinePreviewCharacters > spillToolResultsAboveCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInlinePreviewCharacters));
        }

        _spillToolResultsAboveCharacters = spillToolResultsAboveCharacters;
        _maximumInlinePreviewCharacters = maximumInlinePreviewCharacters;
    }

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.artifacts",
        "1.0.0",
        "Out-of-context storage and bounded reads for large tool or knowledge results.",
        new[] { "artifacts", "large-results", "context-control" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.RegisterService("artifact-store", _store);
        api.RegisterToolProvider(
            "artifact-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { CreateReadTool(context) }));
        api.RegisterAgentHooks(
            "large-tool-result-spill",
            context => new AgentHooks
            {
                AfterToolCallAsync = (call, result, _, cancellationToken) =>
                    SpillToolResultAsync(context, call, result, cancellationToken),
            });
    }

    private async ValueTask<ToolResult?> SpillToolResultAsync(
        GameAgentExtensionRunContext context,
        ToolCallContent call,
        ToolResult result,
        CancellationToken cancellationToken)
    {
        if (string.Equals(call.Name, "read_agent_artifact", StringComparison.Ordinal))
        {
            return result;
        }

        var spillCharacters = result.Content.Sum(content => content switch
        {
            TextContent text => (long)text.Text.Length,
            JsonContent json => json.Json.Length,
            _ => 0,
        });
        if (spillCharacters <= _spillToolResultsAboveCharacters)
        {
            return result;
        }

        var payload = JsonSerializer.Serialize(new
        {
            toolName = call.Name,
            toolCallId = call.Id,
            result.IsError,
            result.OutcomeUncertain,
            result.DetailsJson,
            content = result.Content.Select(SerializeContent),
        });
        var artifactId = CreateArtifactId(context, call, payload);
        try
        {
            await _store.PutAsync(
                new GameAgentArtifact(
                    artifactId,
                    context.Input.SessionId,
                    context.Input.ActorId,
                    "application/vnd.opengameagent.tool-result+json",
                    payload,
                    context.Input.Moment),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Artifact storage is a context optimization. Preserve the authoritative
            // tool result if the optional store is unavailable.
            return result;
        }

        var preview = CreatePreview(result.Content, _maximumInlinePreviewCharacters);
        var replacement = new List<AgentContent>
        {
            new JsonContent(JsonSerializer.Serialize(new
            {
                artifactId,
                toolName = call.Name,
                totalCharacters = payload.Length,
                preview,
                truncated = true,
                readTool = "read_agent_artifact",
            })),
        };
        replacement.AddRange(result.Content.OfType<ResourceContent>());
        return new ToolResult(
            replacement,
            result.IsError,
            result.DetailsJson,
            result.Terminate,
            result.Usage,
            result.OutcomeUncertain);
    }

    private static object SerializeContent(AgentContent content) => content switch
    {
        TextContent text => new { type = "text", text = (object)text.Text },
        JsonContent json => new { type = "json", text = (object)ParseElement(json.Json) },
        ResourceContent resource => new
        {
            type = "resource",
            text = (object)new { resource.Uri, resource.MediaType, resource.Name },
        },
        _ => throw new InvalidOperationException($"Unsupported tool-result content '{content.GetType().FullName}'."),
    };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static string CreatePreview(IReadOnlyList<AgentContent> content, int maximumCharacters)
    {
        if (maximumCharacters == 0)
        {
            return string.Empty;
        }

        var preview = new StringBuilder(Math.Min(maximumCharacters, 4_096));
        foreach (var item in content)
        {
            var value = item switch
            {
                TextContent text => text.Text,
                JsonContent json => json.Json,
                _ => null,
            };
            if (value is null || preview.Length >= maximumCharacters)
            {
                continue;
            }

            var count = Math.Min(value.Length, maximumCharacters - preview.Length);
            count = AvoidSplitSurrogate(value, 0, count);
            preview.Append(value, 0, count);
        }

        return preview.ToString();
    }

    private static string CreateArtifactId(
        GameAgentExtensionRunContext context,
        ToolCallContent call,
        string payload)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            context.Input.SessionId,
            context.Input.ActorId,
            context.Input.InputId,
            call.Id,
            payload)));
        var encoded = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            encoded.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return "tool-result-" + encoded;
    }

    private AgentTool CreateReadTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "read_agent_artifact",
                "Read one bounded text chunk from a large result artifact.",
                ReadSchema),
            async (arguments, _, cancellationToken) =>
            {
                var id = arguments.GetProperty("artifactId").GetString() ?? string.Empty;
                var artifact = await _store.GetAsync(
                    context.Input.SessionId,
                    context.Input.ActorId,
                    id,
                    cancellationToken).ConfigureAwait(false);
                if (artifact is null)
                {
                    return ToolResult.Error($"Artifact '{id}' does not exist.");
                }

                var offset = arguments.TryGetProperty("offset", out var offsetElement) ? offsetElement.GetInt32() : 0;
                if (offset > artifact.Content.Length)
                {
                    return ToolResult.Error("The artifact offset is beyond the end of the content.");
                }

                var maximum = arguments.TryGetProperty("maximumCharacters", out var maximumElement)
                    ? maximumElement.GetInt32()
                    : 16_384;
                var count = Math.Min(maximum, artifact.Content.Length - offset);
                count = AvoidSplitSurrogate(artifact.Content, offset, count);
                var nextOffset = offset + count;
                return new ToolResult(new AgentContent[]
                {
                    new JsonContent(JsonSerializer.Serialize(new
                    {
                        artifactId = artifact.ArtifactId,
                        artifact.MediaType,
                        offset,
                        content = artifact.Content.Substring(offset, count),
                        nextOffset = nextOffset < artifact.Content.Length ? nextOffset : (int?)null,
                        complete = nextOffset >= artifact.Content.Length,
                        totalCharacters = artifact.Content.Length,
                    })),
                });
            },
            ToolRisk.ReadOnly);

    private static int AvoidSplitSurrogate(string content, int offset, int count)
    {
        if (count > 0
            && offset + count < content.Length
            && char.IsHighSurrogate(content[offset + count - 1])
            && char.IsLowSurrogate(content[offset + count]))
        {
            return count - 1;
        }

        return count;
    }
}
