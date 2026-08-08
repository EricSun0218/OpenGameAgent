using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Remote;

internal static class ProxyWire
{
    public const int Version = 1;
    public const string SetupFrame = "s";
    public const string EventFrame = "e";
    public const string TerminalFrame = "z";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string SerializeRequest(ModelRequest request) =>
        JsonSerializer.Serialize(
            new WireRequestEnvelope { Version = Version, Request = WireModelRequest.From(request) },
            JsonOptions);

    public static ModelRequest ParseRequest(string json, int maximumDepth)
    {
        var envelope = Parse<WireRequestEnvelope>(json, maximumDepth, "proxy request");
        if (envelope.Version != Version || envelope.Request is null)
        {
            throw new InvalidDataException("The remote provider request uses an unsupported protocol version.");
        }

        return Convert(() => envelope.Request.ToModelRequest(), "The remote provider request is invalid.");
    }

    public static string SerializeFrame(WireFrame frame) => JsonSerializer.Serialize(frame, JsonOptions);

    public static WireFrame ParseFrame(string json, int maximumDepth) =>
        Parse<WireFrame>(json, maximumDepth, "proxy stream frame");

    public static WireResponse Response(ModelResponse response) => WireResponse.From(response);

    public static WireContent Content(AgentContent content) => WireContent.From(content);

    public static ModelResponse ToResponse(WireResponse response) =>
        Convert(response.ToModelResponse, "The remote provider response is invalid.");

    public static AgentContent ToContent(WireContent content) =>
        Convert(content.ToAgentContent, "The remote provider content is invalid.");

    public static bool ContentEquals(AgentContent left, AgentContent right) =>
        string.Equals(
            JsonSerializer.Serialize(WireContent.From(left), JsonOptions),
            JsonSerializer.Serialize(WireContent.From(right), JsonOptions),
            StringComparison.Ordinal);

    public static bool ContentSequenceEquals(
        IReadOnlyList<AgentContent> left,
        IReadOnlyList<AgentContent> right) =>
        left.Count == right.Count && left.Zip(right, ContentEquals).All(value => value);

    public static bool ResponseEquals(ModelResponse left, ModelResponse right) =>
        string.Equals(
            JsonSerializer.Serialize(WireResponse.From(left), JsonOptions),
            JsonSerializer.Serialize(WireResponse.From(right), JsonOptions),
            StringComparison.Ordinal);

    private static T Parse<T>(string json, int maximumDepth, string description)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maximumDepth });
            EnsureUnambiguous(document.RootElement);
            return JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), JsonOptions)
                   ?? throw new InvalidDataException("The " + description + " is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The " + description + " is not valid JSON.", exception);
        }
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
                    throw new InvalidDataException("Remote provider JSON cannot contain duplicate properties.");
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

    private static T Convert<T>(Func<T> convert, string message)
    {
        try
        {
            return convert();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException(message + " " + exception.Message, exception);
        }
    }
}

internal sealed class WireRequestEnvelope
{
    [JsonPropertyName("v")]
    public int Version { get; set; }

    [JsonPropertyName("r")]
    public WireModelRequest? Request { get; set; }
}

internal sealed class WireFrame
{
    [JsonPropertyName("t")]
    public string? Type { get; set; }

    [JsonPropertyName("v")]
    public int? Version { get; set; }

    [JsonPropertyName("k")]
    public int? Kind { get; set; }

    [JsonPropertyName("i")]
    public int? ContentIndex { get; set; }

    [JsonPropertyName("d")]
    public string? Delta { get; set; }

    [JsonPropertyName("id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("n")]
    public string? ToolName { get; set; }

    [JsonPropertyName("x")]
    public WireContent? Content { get; set; }

    [JsonPropertyName("r")]
    public WireResponse? Response { get; set; }
}

internal sealed class WireModelRequest
{
    [JsonPropertyName("m")]
    public string? Model { get; set; }

    [JsonPropertyName("s")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("g")]
    public List<WireMessage>? Messages { get; set; }

    [JsonPropertyName("o")]
    public List<WireTool>? Tools { get; set; }

    [JsonPropertyName("p")]
    public WireParameters? Parameters { get; set; }

    [JsonPropertyName("q")]
    public string? SessionId { get; set; }

    [JsonPropertyName("r")]
    public string? RunId { get; set; }

    [JsonPropertyName("n")]
    public int Turn { get; set; }

    public static WireModelRequest From(ModelRequest request) => new()
    {
        Model = request.Model,
        SystemPrompt = request.SystemPrompt,
        Messages = request.Messages.Select(WireMessage.From).ToList(),
        Tools = request.Tools.Select(WireTool.From).ToList(),
        Parameters = WireParameters.From(request.Parameters),
        SessionId = request.SessionId,
        RunId = request.RunId,
        Turn = request.Turn,
    };

    public ModelRequest ToModelRequest() => new(
        Model!,
        SystemPrompt ?? string.Empty,
        (Messages ?? new List<WireMessage>()).Select(value => value.ToAgentMessage()).ToArray(),
        (Tools ?? new List<WireTool>()).Select(value => value.ToToolDefinition()).ToArray(),
        (Parameters ?? new WireParameters()).ToModelParameters(),
        SessionId,
        RunId!,
        Turn);
}

internal sealed class WireMessage
{
    [JsonPropertyName("r")]
    public int Role { get; set; }

    [JsonPropertyName("c")]
    public List<WireContent>? Content { get; set; }

    [JsonPropertyName("t")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("z")]
    public string? CustomRole { get; set; }

    [JsonPropertyName("i")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("n")]
    public string? ToolName { get; set; }

    [JsonPropertyName("e")]
    public bool IsError { get; set; }

    [JsonPropertyName("d")]
    public string? DetailsJson { get; set; }

    [JsonPropertyName("x")]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("m")]
    public string? Model { get; set; }

    [JsonPropertyName("s")]
    public int? StopReason { get; set; }

    [JsonPropertyName("u")]
    public WireUsage? Usage { get; set; }

    [JsonPropertyName("f")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("p")]
    public string? Provider { get; set; }

    [JsonPropertyName("a")]
    public string? Api { get; set; }

    [JsonPropertyName("rm")]
    public string? ResponseModel { get; set; }

    [JsonPropertyName("ri")]
    public string? ResponseId { get; set; }

    [JsonPropertyName("rs")]
    public string? RawStopReason { get; set; }

    [JsonPropertyName("y")]
    public bool? EndTurn { get; set; }

    [JsonPropertyName("l")]
    public List<WireDiagnostic>? Diagnostics { get; set; }

    [JsonPropertyName("h")]
    public WireDeferred? Deferred { get; set; }

    [JsonPropertyName("at")]
    public List<string>? AddedToolNames { get; set; }

    public static WireMessage From(AgentMessage message) => new()
    {
        Role = (int)message.Role,
        Content = message.Content.Select(WireContent.From).ToList(),
        Timestamp = message.Timestamp,
        CustomRole = message.CustomRole,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        IsError = message.IsError,
        DetailsJson = message.DetailsJson,
        Metadata = new Dictionary<string, string>(message.Metadata, StringComparer.Ordinal),
        Model = message.Model,
        StopReason = message.StopReason is null ? null : (int)message.StopReason.Value,
        Usage = message.Usage is null ? null : WireUsage.From(message.Usage),
        ErrorMessage = message.ErrorMessage,
        Provider = message.Provider,
        Api = message.Api,
        ResponseModel = message.ResponseModel,
        ResponseId = message.ResponseId,
        RawStopReason = message.RawStopReason,
        EndTurn = message.EndTurn,
        Diagnostics = message.Role == AgentRole.Assistant
            ? message.Diagnostics.Select(WireDiagnostic.From).ToList()
            : null,
        Deferred = message.Deferred is null ? null : WireDeferred.From(message.Deferred),
        AddedToolNames = message.Role == AgentRole.Tool ? message.AddedToolNames.ToList() : null,
    };

    public AgentMessage ToAgentMessage() => new(
        RequireEnum<AgentRole>(Role, nameof(Role)),
        (Content ?? new List<WireContent>()).Select(value => value.ToAgentContent()),
        Timestamp,
        CustomRole,
        ToolCallId,
        ToolName,
        IsError,
        DetailsJson,
        Metadata,
        Model,
        StopReason is null ? null : RequireEnum<ModelStopReason>(StopReason.Value, nameof(StopReason)),
        Usage?.ToModelUsage(),
        ErrorMessage,
        Provider,
        Api,
        ResponseModel,
        ResponseId,
        RawStopReason,
        EndTurn,
        Diagnostics?.Select(value => value.ToModelDiagnostic()),
        Deferred?.ToDeferredModelHandle(),
        AddedToolNames);

    internal static T RequireEnum<T>(int value, string name) where T : struct
    {
        if (!Enum.IsDefined(typeof(T), value))
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return (T)Enum.ToObject(typeof(T), value);
    }
}

internal sealed class WireContent
{
    [JsonPropertyName("k")]
    public int Kind { get; set; }

    [JsonPropertyName("v")]
    public string? Value { get; set; }

    [JsonPropertyName("m")]
    public string? MediaType { get; set; }

    [JsonPropertyName("n")]
    public string? Name { get; set; }

    [JsonPropertyName("g")]
    public string? Signature { get; set; }

    [JsonPropertyName("p")]
    public int? Phase { get; set; }

    [JsonPropertyName("r")]
    public bool Redacted { get; set; }

    [JsonPropertyName("q")]
    public int? MediaKind { get; set; }

    [JsonPropertyName("i")]
    public string? Id { get; set; }

    [JsonPropertyName("a")]
    public string? ArgumentsJson { get; set; }

    [JsonPropertyName("x")]
    public string? Namespace { get; set; }

    public static WireContent From(AgentContent content) => content switch
    {
        TextContent text => new WireContent
        {
            Kind = (int)AgentContentKind.Text,
            Value = text.Text,
            Signature = text.Signature,
            Phase = text.Phase is null ? null : (int)text.Phase.Value,
        },
        JsonContent json => new WireContent { Kind = (int)AgentContentKind.Json, Value = json.Json },
        ResourceContent resource => new WireContent
        {
            Kind = (int)AgentContentKind.Resource,
            Value = resource.Uri,
            MediaType = resource.MediaType,
            Name = resource.Name,
        },
        BinaryContent binary => new WireContent
        {
            Kind = (int)AgentContentKind.Binary,
            Value = binary.Data,
            MediaType = binary.MediaType,
            Name = binary.Name,
            MediaKind = (int)binary.MediaKind,
        },
        ReasoningContent reasoning => new WireContent
        {
            Kind = (int)AgentContentKind.Reasoning,
            Value = reasoning.Text,
            Signature = reasoning.Signature,
            Redacted = reasoning.Redacted,
        },
        ToolCallContent call => new WireContent
        {
            Kind = (int)AgentContentKind.ToolCall,
            Id = call.Id,
            Name = call.Name,
            ArgumentsJson = call.ArgumentsJson,
            Signature = call.ThoughtSignature,
            Namespace = call.Namespace,
        },
        _ => throw new ArgumentException("Unsupported remote provider content type.", nameof(content)),
    };

    public AgentContent ToAgentContent()
    {
        var kind = WireMessage.RequireEnum<AgentContentKind>(Kind, nameof(Kind));
        return kind switch
        {
            AgentContentKind.Text => new TextContent(
                Value ?? string.Empty,
                Signature,
                Phase is null ? null : WireMessage.RequireEnum<AgentTextPhase>(Phase.Value, nameof(Phase))),
            AgentContentKind.Json => new JsonContent(Value!),
            AgentContentKind.Resource => new ResourceContent(Value!, MediaType!, Name),
            AgentContentKind.Binary => new BinaryContent(
                WireMessage.RequireEnum<AgentMediaKind>(MediaKind ?? -1, nameof(MediaKind)),
                Value!,
                MediaType!,
                Name),
            AgentContentKind.Reasoning => new ReasoningContent(Value ?? string.Empty, Signature, Redacted),
            AgentContentKind.ToolCall => new ToolCallContent(Id!, Name!, ArgumentsJson!, Signature, Namespace),
            _ => throw new InvalidOperationException("Unsupported remote provider content kind."),
        };
    }
}

internal sealed class WireTool
{
    [JsonPropertyName("n")]
    public string? Name { get; set; }

    [JsonPropertyName("d")]
    public string? Description { get; set; }

    [JsonPropertyName("s")]
    public string? InputSchemaJson { get; set; }

    [JsonPropertyName("c")]
    public WireConstrainedSampling? ConstrainedSampling { get; set; }

    public static WireTool From(ToolDefinition tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchemaJson = tool.InputSchemaJson,
        ConstrainedSampling = tool.ConstrainedSampling is null
            ? null
            : WireConstrainedSampling.From(tool.ConstrainedSampling),
    };

    public ToolDefinition ToToolDefinition() =>
        new(Name!, Description!, InputSchemaJson!, ConstrainedSampling?.ToToolConstrainedSampling());
}

internal sealed class WireConstrainedSampling
{
    [JsonPropertyName("k")]
    public int Kind { get; set; }

    [JsonPropertyName("s")]
    public int? Strictness { get; set; }

    [JsonPropertyName("l")]
    public string? OpenAiLark { get; set; }

    [JsonPropertyName("r")]
    public string? OpenAiRegex { get; set; }

    public static WireConstrainedSampling From(ToolConstrainedSampling value) => new()
    {
        Kind = (int)value.Kind,
        Strictness = value.Strictness is null ? null : (int)value.Strictness.Value,
        OpenAiLark = value.OpenAiLark,
        OpenAiRegex = value.OpenAiRegex,
    };

    public ToolConstrainedSampling ToToolConstrainedSampling() =>
        WireMessage.RequireEnum<ToolConstrainedSamplingKind>(Kind, nameof(Kind)) switch
        {
            ToolConstrainedSamplingKind.JsonSchema => ToolConstrainedSampling.JsonSchema(
                WireMessage.RequireEnum<ToolSchemaStrictness>(Strictness ?? -1, nameof(Strictness))),
            ToolConstrainedSamplingKind.Grammar => ToolConstrainedSampling.Grammar(OpenAiLark, OpenAiRegex),
            _ => throw new InvalidOperationException("Unsupported constrained-sampling kind."),
        };
}

internal sealed class WireParameters
{
    [JsonPropertyName("t")]
    public double? Temperature { get; set; }

    [JsonPropertyName("m")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("r")]
    public string? ReasoningLevel { get; set; }

    [JsonPropertyName("b")]
    public Dictionary<string, int>? ReasoningBudgets { get; set; }

    [JsonPropertyName("s")]
    public string? SamplingParametersJson { get; set; }

    [JsonPropertyName("x")]
    public int Transport { get; set; }

    [JsonPropertyName("c")]
    public int CacheRetention { get; set; }

    [JsonPropertyName("w")]
    public int? WebSocketConnectTimeoutMilliseconds { get; set; }

    [JsonPropertyName("d")]
    public bool Deferred { get; set; }

    [JsonPropertyName("f")]
    public int? DeferredWindow { get; set; }

    [JsonPropertyName("j")]
    public string? MetadataJson { get; set; }

    [JsonPropertyName("e")]
    public Dictionary<string, string>? Extensions { get; set; }

    public static WireParameters From(ModelParameters parameters) => new()
    {
        Temperature = parameters.Temperature,
        MaxOutputTokens = parameters.MaxOutputTokens,
        ReasoningLevel = parameters.ReasoningLevel,
        ReasoningBudgets = new Dictionary<string, int>(parameters.ReasoningBudgets, StringComparer.Ordinal),
        SamplingParametersJson = parameters.SamplingParametersJson,
        Transport = (int)parameters.Transport,
        CacheRetention = (int)parameters.CacheRetention,
        WebSocketConnectTimeoutMilliseconds = parameters.WebSocketConnectTimeoutMilliseconds,
        Deferred = parameters.Deferred,
        DeferredWindow = parameters.DeferredWindow is null ? null : (int)parameters.DeferredWindow.Value,
        MetadataJson = parameters.MetadataJson,
        Extensions = new Dictionary<string, string>(parameters.Extensions, StringComparer.Ordinal),
    };

    public ModelParameters ToModelParameters() => new()
    {
        Temperature = Temperature,
        MaxOutputTokens = MaxOutputTokens,
        ReasoningLevel = ReasoningLevel,
        ReasoningBudgets = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(ReasoningBudgets ?? new Dictionary<string, int>(), StringComparer.Ordinal)),
        SamplingParametersJson = SamplingParametersJson,
        Transport = WireMessage.RequireEnum<ModelTransport>(Transport, nameof(Transport)),
        CacheRetention = WireMessage.RequireEnum<ModelCacheRetention>(CacheRetention, nameof(CacheRetention)),
        WebSocketConnectTimeoutMilliseconds = WebSocketConnectTimeoutMilliseconds,
        Deferred = Deferred,
        DeferredWindow = DeferredWindow is null
            ? null
            : WireMessage.RequireEnum<ModelDeferredWindow>(DeferredWindow.Value, nameof(DeferredWindow)),
        MetadataJson = MetadataJson,
        Extensions = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(Extensions ?? new Dictionary<string, string>(), StringComparer.Ordinal)),
    };
}

internal sealed class WireCost
{
    [JsonPropertyName("i")]
    public double Input { get; set; }

    [JsonPropertyName("o")]
    public double Output { get; set; }

    [JsonPropertyName("r")]
    public double CacheRead { get; set; }

    [JsonPropertyName("w")]
    public double CacheWrite { get; set; }

    public static WireCost From(ModelCost cost) => new()
    {
        Input = cost.Input,
        Output = cost.Output,
        CacheRead = cost.CacheRead,
        CacheWrite = cost.CacheWrite,
    };

    public ModelCost ToModelCost() => new(Input, Output, CacheRead, CacheWrite);
}

internal sealed class WireUsage
{
    [JsonPropertyName("i")]
    public long InputTokens { get; set; }

    [JsonPropertyName("o")]
    public long OutputTokens { get; set; }

    [JsonPropertyName("r")]
    public long CacheReadTokens { get; set; }

    [JsonPropertyName("w")]
    public long CacheWriteTokens { get; set; }

    [JsonPropertyName("g")]
    public long? ReasoningTokens { get; set; }

    [JsonPropertyName("h")]
    public long? CacheWriteOneHourTokens { get; set; }

    [JsonPropertyName("c")]
    public WireCost? Cost { get; set; }

    public static WireUsage From(ModelUsage usage) => new()
    {
        InputTokens = usage.InputTokens,
        OutputTokens = usage.OutputTokens,
        CacheReadTokens = usage.CacheReadTokens,
        CacheWriteTokens = usage.CacheWriteTokens,
        ReasoningTokens = usage.ReasoningTokens,
        CacheWriteOneHourTokens = usage.CacheWriteOneHourTokens,
        Cost = WireCost.From(usage.Cost),
    };

    public ModelUsage ToModelUsage() => new(
        InputTokens,
        OutputTokens,
        CacheReadTokens,
        CacheWriteTokens,
        ReasoningTokens,
        CacheWriteOneHourTokens,
        Cost?.ToModelCost());
}

internal sealed class WireDiagnostic
{
    [JsonPropertyName("c")]
    public string? Code { get; set; }

    [JsonPropertyName("m")]
    public string? Message { get; set; }

    [JsonPropertyName("s")]
    public int Severity { get; set; }

    [JsonPropertyName("d")]
    public string? DataJson { get; set; }

    public static WireDiagnostic From(ModelDiagnostic diagnostic) => new()
    {
        Code = diagnostic.Code,
        Message = diagnostic.Message,
        Severity = (int)diagnostic.Severity,
        DataJson = diagnostic.DataJson,
    };

    public ModelDiagnostic ToModelDiagnostic() => new(
        Code!,
        Message!,
        WireMessage.RequireEnum<ModelDiagnosticSeverity>(Severity, nameof(Severity)),
        DataJson);
}

internal sealed class WireDeferred
{
    [JsonPropertyName("p")]
    public string? Provider { get; set; }

    [JsonPropertyName("m")]
    public string? Model { get; set; }

    [JsonPropertyName("a")]
    public string? Api { get; set; }

    [JsonPropertyName("i")]
    public string? Id { get; set; }

    [JsonPropertyName("e")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("w")]
    public int? PollAfterMilliseconds { get; set; }

    [JsonPropertyName("d")]
    public string? DataJson { get; set; }

    public static WireDeferred From(DeferredModelHandle deferred) => new()
    {
        Provider = deferred.Provider,
        Model = deferred.Model,
        Api = deferred.Api,
        Id = deferred.Id,
        ExpiresAt = deferred.ExpiresAt,
        PollAfterMilliseconds = deferred.PollAfterMilliseconds,
        DataJson = deferred.DataJson,
    };

    public DeferredModelHandle ToDeferredModelHandle() =>
        new(Provider!, Model!, Api!, Id!, ExpiresAt, PollAfterMilliseconds, DataJson);
}

internal sealed class WireResponse
{
    [JsonPropertyName("c")]
    public List<WireContent>? Content { get; set; }

    [JsonPropertyName("s")]
    public int StopReason { get; set; }

    [JsonPropertyName("u")]
    public WireUsage? Usage { get; set; }

    [JsonPropertyName("e")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("p")]
    public string? Provider { get; set; }

    [JsonPropertyName("a")]
    public string? Api { get; set; }

    [JsonPropertyName("m")]
    public string? ResponseModel { get; set; }

    [JsonPropertyName("i")]
    public string? ResponseId { get; set; }

    [JsonPropertyName("r")]
    public string? RawStopReason { get; set; }

    [JsonPropertyName("y")]
    public bool? EndTurn { get; set; }

    [JsonPropertyName("g")]
    public List<WireDiagnostic>? Diagnostics { get; set; }

    [JsonPropertyName("d")]
    public WireDeferred? Deferred { get; set; }

    public static WireResponse From(ModelResponse response) => new()
    {
        Content = response.Content.Select(WireContent.From).ToList(),
        StopReason = (int)response.StopReason,
        Usage = WireUsage.From(response.Usage),
        ErrorMessage = response.ErrorMessage,
        Provider = response.Provider,
        Api = response.Api,
        ResponseModel = response.ResponseModel,
        ResponseId = response.ResponseId,
        RawStopReason = response.RawStopReason,
        EndTurn = response.EndTurn,
        Diagnostics = response.Diagnostics.Select(WireDiagnostic.From).ToList(),
        Deferred = response.Deferred is null ? null : WireDeferred.From(response.Deferred),
    };

    public ModelResponse ToModelResponse() => new(
        (Content ?? new List<WireContent>()).Select(value => value.ToAgentContent()),
        WireMessage.RequireEnum<ModelStopReason>(StopReason, nameof(StopReason)),
        Usage?.ToModelUsage() ?? throw new InvalidDataException("A remote provider response requires usage."),
        ErrorMessage,
        Provider,
        Api,
        ResponseModel,
        ResponseId,
        RawStopReason,
        EndTurn,
        Diagnostics?.Select(value => value.ToModelDiagnostic()),
        Deferred?.ToDeferredModelHandle());
}
