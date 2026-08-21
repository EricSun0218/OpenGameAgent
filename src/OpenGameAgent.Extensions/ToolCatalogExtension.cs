using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public delegate ValueTask<AgentTool> GameCatalogToolFactory(
    GameAgentExtensionRunContext context,
    CancellationToken cancellationToken);

public sealed class GameToolCatalogEntry
{
    public GameToolCatalogEntry(
        string name,
        string description,
        GameCatalogToolFactory createTool,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? inputTypes = null,
        int priority = 0)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException("A catalog tool name with at most 128 characters is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description) || description.Length > 8_192)
        {
            throw new ArgumentException("A catalog tool description with at most 8192 characters is required.", nameof(description));
        }

        Name = name;
        Description = description;
        CreateTool = createTool ?? throw new ArgumentNullException(nameof(createTool));
        Tags = CopyIds(tags);
        InputTypes = CopyIds(inputTypes);
        Priority = priority;
    }

    public string Name { get; }

    public string Description { get; }

    public GameCatalogToolFactory CreateTool { get; }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<string> InputTypes { get; }

    public int Priority { get; }

    private static IReadOnlyList<string> CopyIds(IEnumerable<string>? values)
    {
        var copied = (values ?? Array.Empty<string>())
            .Select(value => string.IsNullOrWhiteSpace(value) || value.Length > 128
                ? throw new ArgumentException("Catalog metadata values must contain 1 to 128 characters.", nameof(values))
                : value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (copied.Length > 64)
        {
            throw new ArgumentException("Catalog metadata can contain at most 64 values.", nameof(values));
        }

        return Array.AsReadOnly(copied);
    }
}

public sealed class GameToolCatalogQuery
{
    public GameToolCatalogQuery(
        string query,
        string inputType,
        IEnumerable<string>? tags = null,
        int maximumResults = 10)
    {
        if (query is null || query.Length > 4_096)
        {
            throw new ArgumentException("A catalog query cannot exceed 4096 characters.", nameof(query));
        }

        if (string.IsNullOrWhiteSpace(inputType) || inputType.Length > 256)
        {
            throw new ArgumentException("An input type with at most 256 characters is required.", nameof(inputType));
        }

        if (maximumResults < 1 || maximumResults > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        Query = query;
        InputType = inputType;
        var copiedTags = (tags ?? Array.Empty<string>())
            .Select(value => string.IsNullOrWhiteSpace(value) || value.Length > 128
                ? throw new ArgumentException("Catalog query tags must contain 1 to 128 characters.", nameof(tags))
                : value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (copiedTags.Length > 16)
        {
            throw new ArgumentException("A catalog query can contain at most 16 tags.", nameof(tags));
        }

        Tags = Array.AsReadOnly(copiedTags);
        MaximumResults = maximumResults;
    }

    public string Query { get; }

    public string InputType { get; }

    public IReadOnlyList<string> Tags { get; }

    public int MaximumResults { get; }
}

public interface IGameToolCatalog
{
    ValueTask<IReadOnlyList<GameToolCatalogEntry>> SearchAsync(
        GameToolCatalogQuery query,
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken);

    ValueTask<GameToolCatalogEntry?> FindAsync(
        string name,
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameToolCatalog : IGameToolCatalog
{
    private readonly IReadOnlyDictionary<string, GameToolCatalogEntry> _entries;

    public InMemoryGameToolCatalog(IEnumerable<GameToolCatalogEntry> entries)
    {
        var copied = (entries ?? throw new ArgumentNullException(nameof(entries))).ToArray();
        if (copied.Any(value => value is null))
        {
            throw new ArgumentException("Catalog entries cannot contain null values.", nameof(entries));
        }

        var duplicate = copied.GroupBy(value => value.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate catalog tool '{duplicate.Key}'.", nameof(entries));
        }

        _entries = new ReadOnlyDictionary<string, GameToolCatalogEntry>(
            copied.ToDictionary(value => value.Name, StringComparer.Ordinal));
    }

    public ValueTask<IReadOnlyList<GameToolCatalogEntry>> SearchAsync(
        GameToolCatalogQuery query,
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var terms = query.Query.Split(new[] { ' ', '\t', '\r', '\n', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var requiredTags = new HashSet<string>(query.Tags, StringComparer.OrdinalIgnoreCase);
        var results = _entries.Values
            .Where(entry => entry.InputTypes.Count == 0 || entry.InputTypes.Contains(query.InputType, StringComparer.Ordinal))
            .Where(entry => requiredTags.Count == 0 || requiredTags.IsSubsetOf(entry.Tags))
            .Select(entry => new
            {
                Entry = entry,
                Score = Score(entry, terms),
            })
            .Where(value => terms.Length == 0 || value.Score > 0)
            .OrderByDescending(value => value.Score)
            .ThenByDescending(value => value.Entry.Priority)
            .ThenBy(value => value.Entry.Name, StringComparer.Ordinal)
            .Take(query.MaximumResults)
            .Select(value => value.Entry)
            .ToArray();
        return new ValueTask<IReadOnlyList<GameToolCatalogEntry>>(Array.AsReadOnly(results));
    }

    public ValueTask<GameToolCatalogEntry?> FindAsync(
        string name,
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<GameToolCatalogEntry?>(_entries.TryGetValue(name, out var entry) ? entry : null);
    }

    private static int Score(GameToolCatalogEntry entry, IReadOnlyList<string> terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
            }

            if (entry.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (entry.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                score += 5;
            }
        }

        return score;
    }
}

public sealed class ToolCatalogExtension : IGameAgentExtension
{
    private const string StateKey = "active-tools";
    private const string SearchSchema = """
        {"type":"object","properties":{"query":{"type":"string","maxLength":4096},"tags":{"type":"array","maxItems":16,"items":{"type":"string","maxLength":128}},"limit":{"type":"integer","minimum":1,"maximum":20}},"additionalProperties":false}
        """;
    private const string ActivateSchema = """
        {"type":"object","required":["names"],"properties":{"names":{"type":"array","maxItems":64,"items":{"type":"string","minLength":1,"maxLength":128},"uniqueItems":true},"replace":{"type":"boolean"}},"additionalProperties":false}
        """;

    private readonly IGameToolCatalog _catalog;
    private readonly int _maximumActiveTools;

    public ToolCatalogExtension(IGameToolCatalog catalog, int maximumActiveTools = 32)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (maximumActiveTools < 1 || maximumActiveTools > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActiveTools));
        }

        _maximumActiveTools = maximumActiveTools;
    }

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.tool-catalog",
        "1.0.0",
        "Search and activate large game tool catalogs without placing every schema in every request.",
        new[] { "tool-discovery", "dynamic-tools", "context-control" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.RegisterPromptFragment(
            "tool-catalog-guidance",
            "When a needed game capability is not currently available, search the tool catalog and activate only the smallest relevant set. Activated tools appear on the next turn.");
        api.RegisterToolProvider("catalog-tools", CreateToolsAsync, priority: 500);
    }

    private async ValueTask<IReadOnlyList<AgentTool>> CreateToolsAsync(
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken)
    {
        var tools = new List<AgentTool>
        {
            CreateSearchTool(context),
            CreateActivationTool(context),
        };
        foreach (var name in ReadActiveNames(context.State))
        {
            var entry = await _catalog.FindAsync(name, context, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            var tool = await entry.CreateTool(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Catalog tool factory '{name}' returned null.");
            if (!string.Equals(tool.Definition.Name, entry.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Catalog tool factory '{name}' returned tool '{tool.Definition.Name}'.");
            }

            tools.Add(tool);
        }

        return Array.AsReadOnly(tools.ToArray());
    }

    private AgentTool CreateSearchTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "search_game_tools",
                "Search the available game capability catalog by name, description, input type, and tags.",
                SearchSchema),
            async (arguments, _, cancellationToken) =>
            {
                var query = arguments.TryGetProperty("query", out var queryElement)
                    ? queryElement.GetString() ?? string.Empty
                    : string.Empty;
                var tags = arguments.TryGetProperty("tags", out var tagsElement)
                    ? tagsElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
                    : Array.Empty<string>();
                var limit = arguments.TryGetProperty("limit", out var limitElement) ? limitElement.GetInt32() : 10;
                var results = await _catalog.SearchAsync(
                    new GameToolCatalogQuery(query, context.Input.Type, tags, limit),
                    context,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The game tool catalog returned null.");
                if (results.Count > limit)
                {
                    throw new InvalidOperationException("The game tool catalog exceeded the requested result limit.");
                }

                if (results.Any(value => value is null)
                    || results.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != results.Count)
                {
                    throw new InvalidOperationException("The game tool catalog returned null or duplicate entries.");
                }

                return JsonResult(new
                {
                    tools = results.Select(value => new
                    {
                        value.Name,
                        value.Description,
                        value.Tags,
                        value.Priority,
                    }),
                    active = ReadActiveNames(context.State),
                });
            },
            ToolRisk.ReadOnly);

    private AgentTool CreateActivationTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "set_active_game_tools",
                "Activate or deactivate catalog tools. The selected tool schemas become available on the next turn.",
                ActivateSchema),
            async (arguments, _, cancellationToken) =>
            {
                var requested = arguments.GetProperty("names").EnumerateArray()
                    .Select(value => value.GetString() ?? string.Empty)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var replace = !arguments.TryGetProperty("replace", out var replaceElement) || replaceElement.GetBoolean();
                var names = replace
                    ? requested.ToList()
                    : ReadActiveNames(context.State).Concat(requested).Distinct(StringComparer.Ordinal).ToList();
                if (names.Count > _maximumActiveTools)
                {
                    return ToolResult.Error(
                        $"At most {_maximumActiveTools} catalog tools may be active.",
                        ToolFailureCategory.RuleRejected);
                }

                foreach (var name in names)
                {
                    if (await _catalog.FindAsync(name, context, cancellationToken).ConfigureAwait(false) is null)
                    {
                        return ToolResult.Error(
                            $"Catalog tool '{name}' does not exist.",
                            ToolFailureCategory.InvalidArguments);
                    }
                }

                context.State.Set(StateKey, JsonSerializer.Serialize(names));
                return JsonResult(new { active = names, availableOnNextTurn = true });
            },
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Sequential);

    private IReadOnlyList<string> ReadActiveNames(GameAgentExtensionState state)
    {
        var json = state.Get(StateKey);
        if (json is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var names = JsonSerializer.Deserialize<string[]>(json)
                ?? throw new InvalidOperationException("The active tool catalog state is null.");
            if (names.Length > _maximumActiveTools
                || names.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128)
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            {
                throw new InvalidOperationException("The active tool catalog state exceeds its configured limits.");
            }

            return Array.AsReadOnly(names);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The active tool catalog state is invalid.", exception);
        }
    }

    private static ToolResult JsonResult(object value) =>
        new(new AgentContent[] { new JsonContent(JsonSerializer.Serialize(value)) });
}
