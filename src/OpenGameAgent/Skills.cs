using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent;

public sealed class GameSkill
{
    public GameSkill(
        string skillId,
        string name,
        string description,
        string instructions,
        IReadOnlyCollection<string>? inputTypes,
        IReadOnlyCollection<string>? toolNames,
        int priority,
        IReadOnlyDictionary<string, string>? metadata)
        : this(
            skillId,
            name,
            description,
            instructions,
            inputTypes,
            toolNames,
            priority,
            metadata,
            disableModelInvocation: false,
            sourceInfo: null)
    {
    }

    public GameSkill(
        string skillId,
        string name,
        string description,
        string instructions,
        IReadOnlyCollection<string>? inputTypes = null,
        IReadOnlyCollection<string>? toolNames = null,
        int priority = 0,
        IReadOnlyDictionary<string, string>? metadata = null,
        bool disableModelInvocation = false,
        GameResourceSourceInfo? sourceInfo = null)
    {
        SkillId = GameJson.RequireId(skillId, nameof(skillId));
        Name = GameJson.RequireId(name, nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        InputTypes = Array.AsReadOnly(
            (inputTypes ?? Array.Empty<string>())
                .Select(value => GameJson.RequireId(value, nameof(inputTypes)))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        ToolNames = Array.AsReadOnly(
            (toolNames ?? Array.Empty<string>())
                .Select(value => GameJson.RequireId(value, nameof(toolNames)))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        Priority = priority;
        var copiedMetadata = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (copiedMetadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Skill metadata requires non-empty keys and non-null values.", nameof(metadata));
        }

        Metadata = new ReadOnlyDictionary<string, string>(copiedMetadata);
        DisableModelInvocation = disableModelInvocation;
        SourceInfo = sourceInfo;
    }

    public string SkillId { get; }

    public string Name { get; }

    public string Description { get; }

    public string Instructions { get; }

    public IReadOnlyCollection<string> InputTypes { get; }

    public IReadOnlyCollection<string> ToolNames { get; }

    public int Priority { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public bool DisableModelInvocation { get; }

    public GameResourceSourceInfo? SourceInfo { get; }
}

public sealed class GameSkillQuery
{
    public GameSkillQuery(GameInput input, IReadOnlyCollection<string> availableTools, int limit)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        AvailableTools = Array.AsReadOnly(
            (availableTools ?? throw new ArgumentNullException(nameof(availableTools)))
                .Select(value => GameJson.RequireId(value, nameof(availableTools)))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        Limit = limit >= 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
    }

    public GameInput Input { get; }

    public IReadOnlyCollection<string> AvailableTools { get; }

    public int Limit { get; }
}

public interface IGameSkillSource
{
    ValueTask<IReadOnlyList<GameSkill>> SelectAsync(GameSkillQuery query, CancellationToken cancellationToken);
}

public sealed class InMemoryGameSkillSource : IGameSkillSource
{
    private readonly IReadOnlyList<GameSkill> _skills;

    public InMemoryGameSkillSource(IEnumerable<GameSkill> skills, int capacity = 10_000)
    {
        if (skills is null)
        {
            throw new ArgumentNullException(nameof(skills));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var copied = skills.ToArray();
        if (copied.Length > capacity)
        {
            throw new GameRuntimeLimitException(nameof(capacity), "Too many skills were registered.");
        }

        if (copied.Any(skill => skill is null))
        {
            throw new ArgumentException("The skill list cannot contain null values.", nameof(skills));
        }

        var duplicate = copied.GroupBy(skill => skill.SkillId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate skill ID '{duplicate.Key}'.", nameof(skills));
        }

        _skills = Array.AsReadOnly(copied);
    }

    public ValueTask<IReadOnlyList<GameSkill>> SelectAsync(
        GameSkillQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var tools = new HashSet<string>(query.AvailableTools, StringComparer.Ordinal);
        var selected = _skills
            .Where(skill => !skill.DisableModelInvocation)
            .Where(skill => skill.InputTypes.Count == 0 || skill.InputTypes.Contains(query.Input.Type, StringComparer.Ordinal))
            .Where(skill => skill.ToolNames.All(tools.Contains))
            .OrderByDescending(skill => skill.Priority)
            .ThenBy(skill => skill.SkillId, StringComparer.Ordinal)
            .Take(query.Limit)
            .ToArray();
        return new ValueTask<IReadOnlyList<GameSkill>>(selected);
    }
}
