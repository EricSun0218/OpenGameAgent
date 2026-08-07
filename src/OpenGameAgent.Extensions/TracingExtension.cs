using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public sealed class GameAgentTraceEntry
{
    public GameAgentTraceEntry(
        long sequence,
        string kind,
        string sessionId,
        string actorId,
        string inputId,
        GameMoment moment,
        DateTimeOffset operationalTimestamp,
        string detailsJson)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        Sequence = sequence;
        Kind = Require(kind, nameof(kind));
        SessionId = Require(sessionId, nameof(sessionId));
        ActorId = Require(actorId, nameof(actorId));
        InputId = Require(inputId, nameof(inputId));
        if (string.IsNullOrWhiteSpace(moment.TimelineId) || moment.TimelineId.Length > 1_024)
        {
            throw new ArgumentException("A valid game moment is required.", nameof(moment));
        }

        Moment = moment;
        if (operationalTimestamp == default)
        {
            throw new ArgumentException("An operational timestamp is required.", nameof(operationalTimestamp));
        }

        OperationalTimestamp = operationalTimestamp;
        DetailsJson = RequireJson(detailsJson);
    }

    public long Sequence { get; }

    public string Kind { get; }

    public string SessionId { get; }

    public string ActorId { get; }

    public string InputId { get; }

    public GameMoment Moment { get; }

    public DateTimeOffset OperationalTimestamp { get; }

    public string DetailsJson { get; }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 1_024
            ? throw new ArgumentException("A value of at most 1,024 characters is required.", name)
            : value;

    private static string RequireJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 10_000_000)
        {
            throw new ArgumentException("Trace details must contain at most 10,000,000 characters.", nameof(value));
        }

        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
        return value;
    }
}

public interface IGameAgentTraceSink
{
    ValueTask WriteAsync(GameAgentTraceEntry entry, CancellationToken cancellationToken);
}

public sealed class InMemoryGameAgentTraceSink : IGameAgentTraceSink
{
    private readonly object _gate = new();
    private readonly Queue<GameAgentTraceEntry> _entries;
    private readonly int _capacity;

    public InMemoryGameAgentTraceSink(int capacity = 10_000)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _entries = new Queue<GameAgentTraceEntry>(Math.Min(capacity, 1024));
    }

    public ValueTask WriteAsync(GameAgentTraceEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        lock (_gate)
        {
            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        return default;
    }

    public IReadOnlyList<GameAgentTraceEntry> Snapshot()
    {
        lock (_gate)
        {
            return new ReadOnlyCollection<GameAgentTraceEntry>(_entries.ToArray());
        }
    }
}

public sealed class GameAgentTracingOptions
{
    public bool IncludeInputPayload { get; set; }

    public bool IncludeToolArguments { get; set; }

    public int MaximumDetailsCharacters { get; set; } = 65_536;

    public Func<DateTimeOffset> OperationalClock { get; set; } = () => DateTimeOffset.UtcNow;

    internal GameAgentTracingOptions CopyAndValidate()
    {
        var copy = (GameAgentTracingOptions)MemberwiseClone();
        if (copy.MaximumDetailsCharacters < 256 || copy.MaximumDetailsCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDetailsCharacters));
        }

        if (copy.OperationalClock is null)
        {
            throw new ArgumentNullException(nameof(OperationalClock));
        }

        return copy;
    }
}

public sealed class GameAgentTracingExtension : IGameAgentExtension
{
    private readonly IGameAgentTraceSink _sink;
    private readonly GameAgentTracingOptions _options;
    private long _sequence;

    public GameAgentTracingExtension(IGameAgentTraceSink sink, GameAgentTracingOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = (options ?? new GameAgentTracingOptions()).CopyAndValidate();
    }

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.tracing",
        "1.0.0",
        "Bounded structured traces that keep game time separate from operational time.",
        new[] { "tracing", "observability", "diagnostics" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.On(GameAgentExtensionEvents.InputReceived, (value, context, token) =>
            WriteAsync(
                "input.received",
                context,
                _options.IncludeInputPayload
                    ? (object)new { type = value.Input.Type, payload = Parse(value.Input.PayloadJson), metadataCount = value.Input.Metadata.Count }
                    : new { type = value.Input.Type, payloadOmitted = true, metadataCount = value.Input.Metadata.Count },
                token));
        api.On(GameAgentExtensionEvents.SessionLoaded, (value, context, token) =>
            WriteAsync(
                "session.loaded",
                context,
                new { revision = value.Session.Revision, messages = value.Session.Messages.Count },
                token));
        api.On(GameAgentExtensionEvents.ContextCollected, (value, context, token) =>
            WriteAsync(
                "context.collected",
                context,
                new { count = value.Context.Count, sources = value.Context.Select(slice => slice.Source).ToArray() },
                token));
        api.On(GameAgentExtensionEvents.ToolsCollected, (value, context, token) =>
            WriteAsync(
                "tools.collected",
                context,
                new { count = value.Tools.Count, names = value.Tools.Select(tool => tool.Definition.Name).ToArray() },
                token));
        api.On(GameAgentExtensionEvents.RouteSelected, (value, context, token) =>
            WriteAsync(
                "route.selected",
                context,
                new { route = value.Decision.Route.ToString(), value.Decision.Reason, value.Decision.Workflow },
                token));
        api.On(GameAgentExtensionEvents.SkillsSelected, (value, context, token) =>
            WriteAsync(
                "skills.selected",
                context,
                new { count = value.Skills.Count, ids = value.Skills.Select(skill => skill.SkillId).ToArray() },
                token));
        api.On(GameAgentExtensionEvents.KernelEvent, (value, context, token) =>
            WriteAsync("kernel." + value.Value.Kind.ToString().ToLowerInvariant(), context, KernelDetails(value.Value), token));
        api.On(GameAgentExtensionEvents.RunCompleted, (value, context, token) =>
            WriteAsync(
                "run.completed",
                context,
                new
                {
                    status = value.Result.Status.ToString(),
                    route = value.Result.Route.Route.ToString(),
                    revision = value.Result.SessionRevision,
                    succeeded = value.Result.Succeeded,
                    turns = value.Result.AgentResult?.Turns,
                    toolCalls = value.Result.AgentResult?.ToolCalls,
                },
                token));
        api.On(GameAgentExtensionEvents.RunFailed, (value, context, token) =>
            WriteAsync(
                "run.failed",
                context,
                new { exception = value.Exception.GetType().FullName, value.Exception.Message },
                token));
    }

    private ValueTask WriteAsync(
        string kind,
        GameAgentExtensionRunContext context,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details);
        if (json.Length > _options.MaximumDetailsCharacters)
        {
            json = JsonSerializer.Serialize(new { truncated = true, originalCharacters = json.Length });
        }

        return _sink.WriteAsync(
            new GameAgentTraceEntry(
                Interlocked.Increment(ref _sequence),
                kind,
                context.Input.SessionId,
                context.Input.ActorId,
                context.Input.InputId,
                context.Input.Moment,
                _options.OperationalClock(),
                json),
            cancellationToken);
    }

    private object KernelDetails(AgentEvent value) => new
    {
        value.RunId,
        value.Turn,
        tool = value.ToolCall?.Name,
        toolCallId = value.ToolCall?.Id,
        arguments = _options.IncludeToolArguments && value.ToolCall is not null
            ? Parse(value.ToolCall.ArgumentsJson)
            : (JsonElement?)null,
        status = value.Status?.ToString(),
        value.Error,
        contentParts = value.Message?.Content.Count,
        progressMessage = value.Progress?.Message,
        toolError = value.ToolResult?.IsError,
    };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }
}
