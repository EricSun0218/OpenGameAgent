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

public delegate ValueTask<GameMemoryQuery?> GameMemoryRecallQueryFactory(
    GameAgentExtensionRunContext context,
    CancellationToken cancellationToken);

public sealed class GameMemoryExtension : IGameAgentExtension
{
    private const string RememberSchema = """
        {"type":"object","required":["scope","kind","payload"],"properties":{"memoryId":{"type":"string","minLength":1,"maxLength":512},"scope":{"type":"string","minLength":1,"maxLength":512},"kind":{"type":"string","enum":["event","fact","relationship","goal","reflection","procedure"]},"payload":{},"importance":{"type":"number","minimum":0,"maximum":1},"searchableText":{"type":"string","maxLength":65536},"tags":{"type":"array","maxItems":64,"items":{"type":"string","minLength":1,"maxLength":512},"uniqueItems":true},"expiresAtTick":{"type":"integer"}},"additionalProperties":false}
        """;
    private const string SearchSchema = """
        {"type":"object","properties":{"ownerId":{"type":"string","minLength":1,"maxLength":512},"scopes":{"type":"array","maxItems":64,"items":{"type":"string","minLength":1,"maxLength":512},"uniqueItems":true},"kinds":{"type":"array","maxItems":6,"items":{"type":"string","enum":["event","fact","relationship","goal","reflection","procedure"]},"uniqueItems":true},"tags":{"type":"array","maxItems":64,"items":{"type":"string","minLength":1,"maxLength":512},"uniqueItems":true},"text":{"type":"string","maxLength":65536},"limit":{"type":"integer","minimum":1,"maximum":64},"atOrBeforeTick":{"type":"integer"},"minimumImportance":{"type":"number","minimum":0,"maximum":1}},"additionalProperties":false}
        """;

    private readonly IGameMemoryStore _store;
    private readonly GameMemoryRecallQueryFactory? _recall;
    private readonly bool _allowCrossActorSearch;
    private readonly int _maximumResultCharacters;

    public GameMemoryExtension(
        IGameMemoryStore store,
        GameMemoryRecallQueryFactory? recall = null,
        bool allowCrossActorSearch = false,
        int maximumResultCharacters = 262_144)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recall = recall;
        _allowCrossActorSearch = allowCrossActorSearch;
        if (maximumResultCharacters < 1_024 || maximumResultCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResultCharacters));
        }

        _maximumResultCharacters = maximumResultCharacters;
    }

    public static GameAgentExtensionChannel<GameMemory> MemoryAppended { get; } = new("memory.appended");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.memory",
        "1.0.0",
        "Game-time memory append, search, and optional bounded recall.",
        new[] { "memory", "game-time", "search", "context-recall" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.RegisterToolProvider(
            "memory-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                CreateRememberTool(api, context),
                CreateSearchTool(context),
            }));
        if (_recall is not null)
        {
            api.RegisterContextProvider("memory-recall", RecallAsync, priority: 20);
        }
    }

    private AgentTool CreateRememberTool(GameAgentExtensionApi api, GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "remember_game_memory",
                "Persist an actor memory on the current game timeline. Game state must be stored by game tools, not in memory.",
                RememberSchema),
            async (arguments, execution, cancellationToken) =>
            {
                var id = arguments.TryGetProperty("memoryId", out var configuredId)
                    ? configuredId.GetString() ?? string.Empty
                    : GameExtensionOperationIds.Create(
                        "oga-memory-v1:",
                        "remember_game_memory",
                        context.Input,
                        execution);
                var expiresAt = arguments.TryGetProperty("expiresAtTick", out var expiry)
                    ? new GameMoment(context.Input.Moment.TimelineId, expiry.GetInt64())
                    : (GameMoment?)null;
                var memory = new GameMemory(
                    id,
                    context.Input.SessionId,
                    context.Input.ActorId,
                    arguments.GetProperty("scope").GetString() ?? string.Empty,
                    ParseKind(arguments.GetProperty("kind").GetString()),
                    arguments.GetProperty("payload").GetRawText(),
                    context.Input.Moment,
                    arguments.TryGetProperty("importance", out var importance) ? importance.GetDouble() : 0.5,
                    arguments.TryGetProperty("searchableText", out var searchableText) ? searchableText.GetString() : null,
                    ReadStrings(arguments, "tags"),
                    context.Input.InputId,
                    expiresAt);
                await _store.AppendAsync(memory, cancellationToken).ConfigureAwait(false);
                await api.PublishAsync(MemoryAppended, memory, cancellationToken).ConfigureAwait(false);
                return JsonResult(new
                {
                    memoryId = memory.MemoryId,
                    ownerId = memory.OwnerId,
                    timelineId = memory.Moment.TimelineId,
                    tick = memory.Moment.Tick,
                });
            },
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Sequential);

    private AgentTool CreateSearchTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "search_game_memory",
                "Search visible memories at a point on the game timeline.",
                SearchSchema),
            async (arguments, _, cancellationToken) =>
            {
                var requestedOwner = arguments.TryGetProperty("ownerId", out var owner)
                    ? owner.GetString()
                    : null;
                if (!_allowCrossActorSearch
                    && requestedOwner is not null
                    && !string.Equals(requestedOwner, context.Input.ActorId, StringComparison.Ordinal))
                {
                    return ToolResult.Error("Cross-actor memory search is disabled.");
                }

                var moment = new GameMoment(
                    context.Input.Moment.TimelineId,
                    arguments.TryGetProperty("atOrBeforeTick", out var tick)
                        ? tick.GetInt64()
                        : context.Input.Moment.Tick);
                if (moment.Tick > context.Input.Moment.Tick)
                {
                    return ToolResult.Error("Memory search cannot read from the future of the current game timeline.");
                }

                var query = new GameMemoryQuery(
                    context.Input.SessionId,
                    arguments.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 8,
                    requestedOwner ?? context.Input.ActorId,
                    ReadStrings(arguments, "scopes"),
                    ReadKinds(arguments, "kinds"),
                    ReadStrings(arguments, "tags"),
                    arguments.TryGetProperty("text", out var text) ? text.GetString() : null,
                    moment,
                    arguments.TryGetProperty("minimumImportance", out var minimum) ? minimum.GetDouble() : 0);
                var memories = await _store.SearchAsync(query, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The memory store returned null.");
                ValidateResults(query, memories);
                return SerializeMemories(memories, query.Limit);
            },
            ToolRisk.ReadOnly);

    private async ValueTask<IReadOnlyList<GameContextSlice>> RecallAsync(
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken)
    {
        var query = await _recall!(context, cancellationToken).ConfigureAwait(false);
        if (query is null || query.Limit == 0)
        {
            return Array.Empty<GameContextSlice>();
        }

        if (!string.Equals(query.SessionId, context.Input.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A memory recall query cannot target another game session.");
        }

        if (!_allowCrossActorSearch
            && query.OwnerId is not null
            && !string.Equals(query.OwnerId, context.Input.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A memory recall query cannot target another actor.");
        }

        if (query.AtOrBefore is { } moment
            && (!string.Equals(moment.TimelineId, context.Input.Moment.TimelineId, StringComparison.Ordinal)
                || moment.Tick > context.Input.Moment.Tick))
        {
            throw new InvalidOperationException("A memory recall query cannot read from another timeline or the future.");
        }

        var effectiveQuery = new GameMemoryQuery(
            query.SessionId,
            query.Limit,
            query.OwnerId ?? (_allowCrossActorSearch ? null : context.Input.ActorId),
            query.Scopes,
            query.Kinds,
            query.Tags,
            query.Text,
            query.AtOrBefore ?? context.Input.Moment,
            query.MinimumImportance);
        var memories = await _store.SearchAsync(effectiveQuery, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The memory store returned null.");
        ValidateResults(effectiveQuery, memories);
        var json = SerializeMemoryJson(memories, effectiveQuery.Limit);
        return new[] { new GameContextSlice("memory", json, priority: 20, version: context.Input.Moment.Tick.ToString()) };
    }

    private ToolResult SerializeMemories(IReadOnlyList<GameMemory> memories, int requestedLimit) =>
        new(new AgentContent[] { new JsonContent(SerializeMemoryJson(memories, requestedLimit)) });

    private string SerializeMemoryJson(IReadOnlyList<GameMemory> memories, int requestedLimit)
    {
        var accepted = new List<GameMemory>();
        foreach (var memory in memories.Take(requestedLimit))
        {
            accepted.Add(memory);
            var candidate = Serialize(accepted, truncated: accepted.Count < memories.Count);
            if (candidate.Length <= _maximumResultCharacters)
            {
                continue;
            }

            accepted.RemoveAt(accepted.Count - 1);
            break;
        }

        var json = Serialize(accepted, truncated: accepted.Count < Math.Min(memories.Count, requestedLimit));
        if (json.Length > _maximumResultCharacters)
        {
            return "{\"memories\":[],\"truncated\":true}";
        }

        return json;
    }

    private static void ValidateResults(GameMemoryQuery query, IReadOnlyList<GameMemory> memories)
    {
        if (memories.Count > query.Limit)
        {
            throw new InvalidOperationException("The memory store exceeded the requested result limit.");
        }

        var ids = new HashSet<(string OwnerId, string MemoryId)>();
        foreach (var memory in memories)
        {
            if (memory is null || !ids.Add((memory.OwnerId, memory.MemoryId)))
            {
                throw new InvalidOperationException("The memory store returned a null or duplicate memory.");
            }

            if (!string.Equals(memory.SessionId, query.SessionId, StringComparison.Ordinal)
                || (query.OwnerId is not null
                    && !string.Equals(memory.OwnerId, query.OwnerId, StringComparison.Ordinal))
                || (query.Scopes.Count > 0 && !query.Scopes.Contains(memory.Scope, StringComparer.Ordinal))
                || (query.Kinds.Count > 0 && !query.Kinds.Contains(memory.Kind))
                || query.Tags.Any(tag => !memory.Tags.Contains(tag, StringComparer.Ordinal))
                || memory.Importance < query.MinimumImportance)
            {
                throw new InvalidOperationException("The memory store returned a memory outside the requested visibility filters.");
            }

            if (query.AtOrBefore is { } moment
                && (!string.Equals(memory.Moment.TimelineId, moment.TimelineId, StringComparison.Ordinal)
                    || memory.Moment.Tick > moment.Tick
                    || (memory.ExpiresAt is { } expiry && moment.Tick >= expiry.Tick)))
            {
                throw new InvalidOperationException("The memory store returned a memory outside the requested game-time boundary.");
            }
        }
    }

    private static string Serialize(IReadOnlyList<GameMemory> memories, bool truncated) =>
        JsonSerializer.Serialize(new
        {
            memories = memories.Select(memory => new
            {
                memoryId = memory.MemoryId,
                ownerId = memory.OwnerId,
                scope = memory.Scope,
                kind = memory.Kind.ToString(),
                payload = ParseElement(memory.PayloadJson),
                timelineId = memory.Moment.TimelineId,
                tick = memory.Moment.Tick,
                importance = memory.Importance,
                tags = memory.Tags,
                expiresAtTick = memory.ExpiresAt?.Tick,
            }),
            truncated,
        });

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static IReadOnlyCollection<string> ReadStrings(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var values))
        {
            return Array.Empty<string>();
        }

        return values.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
    }

    private static IReadOnlyCollection<GameMemoryKind> ReadKinds(JsonElement arguments, string property) =>
        !arguments.TryGetProperty(property, out var values)
            ? Array.Empty<GameMemoryKind>()
            : values.EnumerateArray().Select(value => ParseKind(value.GetString())).ToArray();

    private static GameMemoryKind ParseKind(string? value) => value switch
    {
        "event" => GameMemoryKind.Event,
        "fact" => GameMemoryKind.Fact,
        "relationship" => GameMemoryKind.Relationship,
        "goal" => GameMemoryKind.Goal,
        "reflection" => GameMemoryKind.Reflection,
        "procedure" => GameMemoryKind.Procedure,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown memory kind."),
    };

    private static ToolResult JsonResult(object value) =>
        new(new AgentContent[] { new JsonContent(JsonSerializer.Serialize(value)) });
}

internal static class GameExtensionOperationIds
{
    public static string Create(
        string prefix,
        string operation,
        GameInput input,
        ToolExecutionContext execution)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 128)
        {
            throw new ArgumentException("An extension operation ID prefix is required.", nameof(prefix));
        }

        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 1_024)
        {
            throw new ArgumentException("An extension operation name is required.", nameof(operation));
        }

        _ = input ?? throw new ArgumentNullException(nameof(input));
        _ = execution ?? throw new ArgumentNullException(nameof(execution));
        using var hash = SHA256.Create();
        using var stream = new MemoryStream();
        Write("OpenGameAgent.ExtensionOperationId.v1");
        Write(input.SessionId);
        Write(input.ActorId);
        Write(input.InputId);
        Write(input.Moment.TimelineId);
        Write(input.Moment.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Write(execution.Turn.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Write(execution.ToolCallIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Write(operation);
        stream.Position = 0;
        return prefix + string.Concat(
            hash.ComputeHash(stream).Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));

        void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            length[0] = (byte)(bytes.Length >> 24);
            length[1] = (byte)(bytes.Length >> 16);
            length[2] = (byte)(bytes.Length >> 8);
            length[3] = (byte)bytes.Length;
            stream.Write(length);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
