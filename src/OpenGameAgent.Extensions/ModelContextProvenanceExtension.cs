using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public sealed class GameModelContextProvenanceEntry
{
    public GameModelContextProvenanceEntry(
        string entryId,
        GameSessionKey session,
        string inputId,
        string runId,
        int turn,
        string kind,
        string detailsJson,
        DateTimeOffset operationalTimestamp)
    {
        EntryId = RequireId(entryId, nameof(entryId));
        if (string.IsNullOrWhiteSpace(session.SessionId) || string.IsNullOrWhiteSpace(session.ActorId))
        {
            throw new ArgumentException("A valid session key is required.", nameof(session));
        }

        Session = session;
        InputId = RequireId(inputId, nameof(inputId));
        RunId = RequireId(runId, nameof(runId));
        Turn = turn > 0 ? turn : throw new ArgumentOutOfRangeException(nameof(turn));
        Kind = RequireId(kind, nameof(kind));
        if (string.IsNullOrWhiteSpace(detailsJson) || detailsJson.Length > 10_000_000)
        {
            throw new ArgumentException("Provenance details exceed the contract bound.", nameof(detailsJson));
        }

        using (JsonDocument.Parse(detailsJson, new JsonDocumentOptions { MaxDepth = 128 }))
        {
        }

        DetailsJson = detailsJson;

        OperationalTimestamp = operationalTimestamp != default
            ? operationalTimestamp
            : throw new ArgumentException("An operational timestamp is required.", nameof(operationalTimestamp));
    }

    public string SchemaVersion => "1";

    public string EntryId { get; }

    public GameSessionKey Session { get; }

    public string InputId { get; }

    public string RunId { get; }

    public int Turn { get; }

    public string Kind { get; }

    public string DetailsJson { get; }

    public DateTimeOffset OperationalTimestamp { get; }

    private static string RequireId(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 1_024 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded identifier is required.", name)
            : value;
}

public interface IGameModelContextProvenanceStore
{
    ValueTask AppendAsync(GameModelContextProvenanceEntry entry, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameModelContextProvenanceEntry>> ListAsync(
        GameSessionKey session,
        string? inputId,
        int maximum,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameModelContextProvenanceStore : IGameModelContextProvenanceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameModelContextProvenanceEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public InMemoryGameModelContextProvenanceStore(int capacity = 100_000)
    {
        _capacity = capacity is >= 1 and <= 10_000_000
            ? capacity
            : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public ValueTask AppendAsync(GameModelContextProvenanceEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(entry.EntryId, out var existing))
            {
                if (!Equivalent(existing, entry))
                {
                    throw new InvalidOperationException("A provenance entry ID cannot be reused for different content.");
                }

                return default;
            }

            if (_entries.Count >= _capacity)
            {
                throw new InvalidOperationException("The provenance store reached its configured capacity.");
            }

            _entries.Add(entry.EntryId, entry);
        }

        return default;
    }

    public ValueTask<IReadOnlyList<GameModelContextProvenanceEntry>> ListAsync(
        GameSessionKey session,
        string? inputId,
        int maximum,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(session.SessionId) || string.IsNullOrWhiteSpace(session.ActorId))
        {
            throw new ArgumentException("A valid session key is required.", nameof(session));
        }

        if (maximum < 1 || maximum > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        lock (_gate)
        {
            var values = _entries.Values
                .Where(value => value.Session.Equals(session)
                    && (inputId is null || string.Equals(inputId, value.InputId, StringComparison.Ordinal)))
                .OrderByDescending(value => value.OperationalTimestamp)
                .ThenByDescending(value => value.EntryId, StringComparer.Ordinal)
                .Take(maximum)
                .OrderBy(value => value.OperationalTimestamp)
                .ThenBy(value => value.EntryId, StringComparer.Ordinal)
                .ToArray();
            return new ValueTask<IReadOnlyList<GameModelContextProvenanceEntry>>(Array.AsReadOnly(values));
        }
    }

    private static bool Equivalent(GameModelContextProvenanceEntry left, GameModelContextProvenanceEntry right) =>
        left.Session.Equals(right.Session)
        && string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
        && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && left.Turn == right.Turn
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal);
}

public sealed class GameModelContextProvenanceOptions
{
    /// <summary>
    /// Stores visible prompt text, JSON, and tool arguments in the private provenance store. The
    /// default records bounded hashes, identities, sizes, and image references instead.
    /// Hidden reasoning and signatures are never copied.
    /// </summary>
    public bool IncludeModelVisibleContent { get; set; }

    public int MaximumDetailsCharacters { get; set; } = 4_000_000;

    public Func<DateTimeOffset> OperationalClock { get; set; } = () => DateTimeOffset.UtcNow;

    internal GameModelContextProvenanceOptions CopyAndValidate()
    {
        var copy = (GameModelContextProvenanceOptions)MemberwiseClone();
        if (copy.MaximumDetailsCharacters < 1_024 || copy.MaximumDetailsCharacters > 10_000_000)
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

/// <summary>
/// Records the exact identities and hashes that formed each model-visible request. It is a private
/// replay/evaluation surface, not a public audience projection.
/// </summary>
public sealed class GameModelContextProvenanceExtension : IGameAgentExtension
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly ConcurrentDictionary<string, RunState> _states = new(StringComparer.Ordinal);
    private readonly IGameModelContextProvenanceStore _store;
    private readonly GameModelContextProvenanceOptions _options;

    public GameModelContextProvenanceExtension(
        IGameModelContextProvenanceStore store,
        GameModelContextProvenanceOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = (options ?? new GameModelContextProvenanceOptions()).CopyAndValidate();
    }

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.model-context-provenance",
        "1.0.0",
        "Private, reconstructable provenance for model-visible requests and resolved provider responses.",
        new[] { "provenance", "replay", "evaluation", "model-context" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.On(GameAgentExtensionEvents.InputReceived, (value, context, _) =>
        {
            _states[Key(context)] = new RunState(value.Input);
            return default;
        });
        api.On(GameAgentExtensionEvents.ContextCollected, (value, context, _) =>
        {
            State(context).Context = value.Context.ToArray();
            return default;
        });
        api.On(GameAgentExtensionEvents.ToolsCollected, (value, context, _) =>
        {
            State(context).Tools = value.Tools.Select(tool => tool.Definition).ToArray();
            return default;
        });
        api.On(GameAgentExtensionEvents.RouteSelected, (value, context, _) =>
        {
            State(context).Route = value.Decision;
            return default;
        });
        api.On(GameAgentExtensionEvents.SkillsSelected, (value, context, _) =>
        {
            State(context).Skills = value.Skills.ToArray();
            return default;
        });
        api.On(GameAgentExtensionEvents.ImagesProjected, async (value, context, token) =>
        {
            var state = State(context);
            state.ImageProjections[value.Turn] = value.Images.ToArray();
            if (state.PendingRequests.TryRemove(value.Turn, out var pending))
            {
                await WriteRequestAsync(context, state, pending, value.Images, token).ConfigureAwait(false);
            }
        });
        api.On(GameAgentExtensionEvents.KernelEvent, async (value, context, token) =>
        {
            var state = State(context);
            var kernel = value.Value;
            if (kernel.Kind == AgentEventKind.ModelRequestStarted && kernel.ModelRequest is not null)
            {
                var containsImages = kernel.ModelRequest.Messages
                    .Any(message => message.Content.Any(content => content is ImageAttachmentContent));
                IReadOnlyList<GameAgentImageProjectionRecord> images = Array.Empty<GameAgentImageProjectionRecord>();
                if (containsImages && !state.ImageProjections.TryGetValue(kernel.ModelRequest.Turn, out images))
                {
                    state.PendingRequests[kernel.ModelRequest.Turn] = kernel.ModelRequest;
                }
                else
                {
                    await WriteRequestAsync(
                        context,
                        state,
                        kernel.ModelRequest,
                        images,
                        token).ConfigureAwait(false);
                }
            }
            else if (kernel.Kind == AgentEventKind.MessageEnded
                && kernel.Message is { Role: AgentRole.Assistant } message)
            {
                await WriteResponseAsync(context, kernel.RunId, kernel.Turn, message, token).ConfigureAwait(false);
            }
        });
        api.On(GameAgentExtensionEvents.RunCompleted, (_, context, _) =>
        {
            _states.TryRemove(Key(context), out _);
            return default;
        });
        api.On(GameAgentExtensionEvents.RunFailed, (_, context, _) =>
        {
            _states.TryRemove(Key(context), out _);
            return default;
        });
    }

    private RunState State(GameAgentExtensionRunContext context) =>
        _states.GetOrAdd(Key(context), _ => new RunState(context.Input));

    private async ValueTask WriteRequestAsync(
        GameAgentExtensionRunContext context,
        RunState state,
        ModelRequest request,
        IReadOnlyList<GameAgentImageProjectionRecord> images,
        CancellationToken cancellationToken)
    {
        var details = new
        {
            schemaVersion = 1,
            route = state.Route is null ? null : new
            {
                kind = state.Route.Route.ToString(),
                state.Route.Reason,
                state.Route.Workflow,
            },
            request = new
            {
                request.Model,
                systemPrompt = ProjectText(request.SystemPrompt),
                messages = request.Messages.Select(ProjectMessage).ToArray(),
                tools = request.Tools.Select(ProjectTool).ToArray(),
                parameters = new
                {
                    request.Parameters.Temperature,
                    request.Parameters.MaxOutputTokens,
                    request.Parameters.ReasoningLevel,
                    request.Parameters.Transport,
                    request.Parameters.CacheRetention,
                    request.Parameters.Deferred,
                },
            },
            context = state.Context.Select(slice => new
            {
                slice.Source,
                slice.Version,
                slice.Priority,
                payload = ProjectText(slice.PayloadJson),
            }).ToArray(),
            skills = state.Skills.Select(skill => new
            {
                skill.SkillId,
                source = skill.SourceInfo?.Source,
                scope = skill.SourceInfo?.Scope,
                instructions = ProjectText(skill.Instructions),
            }).ToArray(),
            collectedToolNames = state.Tools.Select(tool => tool.Name).ToArray(),
            images = images.Select(image => new
            {
                image.Ordinal,
                image.SourceAttachmentId,
                image.RequestAttachmentId,
                disposition = image.Disposition.ToString(),
                image.TransformId,
                image.Width,
                image.Height,
                image.Bytes,
            }).ToArray(),
        };
        await AppendAsync(
            context,
            request.RunId,
            request.Turn,
            "model-request",
            request.RunId + ":" + request.Turn + ":request",
            details,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteResponseAsync(
        GameAgentExtensionRunContext context,
        string runId,
        int turn,
        AgentMessage message,
        CancellationToken cancellationToken) => AppendAsync(
            context,
            runId,
            turn,
            "provider-response",
            runId + ":" + turn + ":response",
            new
            {
                message.Provider,
                message.Api,
                requestedModel = message.Model,
                message.ResponseModel,
                message.ResponseId,
                stopReason = message.StopReason?.ToString(),
                message.RawStopReason,
            },
            cancellationToken);

    private async ValueTask AppendAsync(
        GameAgentExtensionRunContext context,
        string runId,
        int turn,
        string kind,
        string suffix,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, JsonOptions);
        if (json.Length > _options.MaximumDetailsCharacters)
        {
            throw new InvalidOperationException("The model-context provenance entry exceeded its configured bound.");
        }

        await _store.AppendAsync(
            new GameModelContextProvenanceEntry(
                CreateEntryId(context, suffix),
                new GameSessionKey(context.Input.SessionId, context.Input.ActorId),
                context.Input.InputId,
                runId,
                turn,
                kind,
                json,
                _options.OperationalClock()),
            cancellationToken).ConfigureAwait(false);
    }

    private object ProjectMessage(AgentMessage message) => new
    {
        role = message.Role.ToString(),
        message.CustomRole,
        content = message.Content.Select(ProjectContent).ToArray(),
        metadata = message.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        message.ToolCallId,
        message.ToolName,
        message.IsError,
    };

    private object ProjectTool(ToolDefinition tool) => new
    {
        tool.Name,
        description = ProjectText(tool.Description),
        schema = ProjectText(tool.InputSchemaJson),
        constrainedSampling = tool.ConstrainedSampling?.Kind.ToString(),
    };

    private object ProjectContent(AgentContent content) => content switch
    {
        TextContent text => new { kind = "text", value = ProjectText(text.Text), phase = text.Phase?.ToString() },
        JsonContent json => new { kind = "json", value = ProjectText(json.Json) },
        ResourceContent resource => new { kind = "resource", resource.Uri, resource.MediaType, resource.Name },
        ImageAttachmentContent image => new
        {
            kind = "image",
            image.Attachment.AttachmentId,
            image.Attachment.MediaType,
            image.Attachment.Bytes,
            image.Attachment.Width,
            image.Attachment.Height,
        },
        BinaryContent binary => new
        {
            kind = "binary",
            mediaKind = binary.MediaKind.ToString(),
            binary.MediaType,
            bytes = binary.Data.Length,
            digest = Hash(binary.Data),
        },
        ToolCallContent call => new
        {
            kind = "tool-call",
            call.Id,
            call.Name,
            arguments = ProjectText(call.ArgumentsJson),
            signaturePresent = call.ThoughtSignature is not null,
        },
        ReasoningContent reasoning => new
        {
            kind = "reasoning",
            reasoning.Redacted,
            characters = reasoning.Text.Length,
            digest = Hash(reasoning.Text),
            signaturePresent = reasoning.Signature is not null,
        },
        _ => new { kind = content.Kind.ToString() },
    };

    private object ProjectText(string value) => new TextProjection(
        _options.IncludeModelVisibleContent,
        value.Length,
        Hash(value),
        _options.IncludeModelVisibleContent ? value : null);

    private static string CreateEntryId(GameAgentExtensionRunContext context, string suffix) =>
        "oga-provenance-v1:" + Hash(
            context.Input.SessionId + "\n" + context.Input.ActorId + "\n" + context.Input.InputId + "\n" + suffix);

    private static string Key(GameAgentExtensionRunContext context) =>
        context.Input.SessionId + "\u001f" + context.Input.ActorId + "\u001f" + context.Input.InputId;

    private static string Hash(string value)
    {
        using var algorithm = SHA256.Create();
        return Convert.ToBase64String(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private sealed class RunState
    {
        public RunState(GameInput input)
        {
            Input = input;
        }

        public GameInput Input { get; }

        public IReadOnlyList<GameContextSlice> Context { get; set; } = Array.Empty<GameContextSlice>();

        public IReadOnlyList<ToolDefinition> Tools { get; set; } = Array.Empty<ToolDefinition>();

        public IReadOnlyList<GameSkill> Skills { get; set; } = Array.Empty<GameSkill>();

        public GameRouteDecision? Route { get; set; }

        public ConcurrentDictionary<int, ModelRequest> PendingRequests { get; } = new();

        public ConcurrentDictionary<int, IReadOnlyList<GameAgentImageProjectionRecord>> ImageProjections { get; } = new();
    }

    private sealed class TextProjection
    {
        public TextProjection(bool included, int characters, string digest, string? value)
        {
            Included = included;
            Characters = characters;
            Digest = digest;
            Value = value;
        }

        public bool Included { get; }

        public int Characters { get; }

        public string Digest { get; }

        public string? Value { get; }
    }
}
