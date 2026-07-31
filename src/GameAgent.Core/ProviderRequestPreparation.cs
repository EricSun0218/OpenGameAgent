using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class ProviderRequestPreparationContext
{
    private readonly ProviderRequestPreparationReport? _baseline;

    internal ProviderRequestPreparationContext(
        string providerId,
        ProviderRouteIdentity routeIdentity,
        ProviderCapabilities capabilities,
        StreamingModelRequest request,
        ProviderRequestPreparationReport? baseline = null)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        RouteIdentity =
            routeIdentity ?? throw new ArgumentNullException(nameof(routeIdentity));
        Capabilities =
            capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        _baseline = baseline;
    }

    public string ProviderId { get; }

    public ProviderRouteIdentity RouteIdentity { get; }

    public ProviderCapabilities Capabilities { get; }

    public StreamingModelRequest Request { get; }

    public ProviderPreparedRequest CreatePreparedRequest(
        StreamingModelRequest output,
        ProviderRequestPreparationChanges? changes = null,
        CancellationToken cancellationToken = default)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        ProviderRequestContentGuard.EnsureInputWithinLimits(
            output.Messages,
            output.Tools,
            cancellationToken);
        var baseline = _baseline
                       ?? ProviderRequestSanitizer.Unchanged(
                           Request,
                           cancellationToken).Report;
        var applied = changes ?? ProviderRequestPreparationChanges.None;
        return new ProviderPreparedRequest(
            output,
            new ProviderRequestPreparationReport(
                baseline.InputMessageCount,
                output.Messages.Count,
                applied.RemovedReasoningParts,
                applied.RemovedOrphanToolResults,
                applied.RemovedDuplicateToolCalls,
                applied.SynthesizedToolResults,
                baseline.InputDigest,
                ProviderRequestSanitizer.DigestMessages(
                    output.Messages,
                    cancellationToken)));
    }
}

public sealed class ProviderRequestPreparationChanges
{
    public static ProviderRequestPreparationChanges None { get; } = new();

    public ProviderRequestPreparationChanges(
        int removedReasoningParts = 0,
        int removedOrphanToolResults = 0,
        int removedDuplicateToolCalls = 0,
        int synthesizedToolResults = 0)
    {
        if (removedReasoningParts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(removedReasoningParts));
        }

        if (removedOrphanToolResults < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(removedOrphanToolResults));
        }

        if (removedDuplicateToolCalls < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(removedDuplicateToolCalls));
        }

        if (synthesizedToolResults < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(synthesizedToolResults));
        }

        RemovedReasoningParts = removedReasoningParts;
        RemovedOrphanToolResults = removedOrphanToolResults;
        RemovedDuplicateToolCalls = removedDuplicateToolCalls;
        SynthesizedToolResults = synthesizedToolResults;
    }

    public int RemovedReasoningParts { get; }

    public int RemovedOrphanToolResults { get; }

    public int RemovedDuplicateToolCalls { get; }

    public int SynthesizedToolResults { get; }
}

public sealed class ProviderRequestPreparationReport
{
    internal ProviderRequestPreparationReport(
        int inputMessageCount,
        int outputMessageCount,
        int removedReasoningParts,
        int removedOrphanToolResults,
        int removedDuplicateToolCalls,
        int synthesizedToolResults,
        string inputDigest,
        string outputDigest)
    {
        InputMessageCount = inputMessageCount;
        OutputMessageCount = outputMessageCount;
        RemovedReasoningParts = removedReasoningParts;
        RemovedOrphanToolResults = removedOrphanToolResults;
        RemovedDuplicateToolCalls = removedDuplicateToolCalls;
        SynthesizedToolResults = synthesizedToolResults;
        InputDigest = inputDigest;
        OutputDigest = outputDigest;
    }

    public int InputMessageCount { get; }

    public int OutputMessageCount { get; }

    public int RemovedReasoningParts { get; }

    public int RemovedOrphanToolResults { get; }

    public int RemovedDuplicateToolCalls { get; }

    public int SynthesizedToolResults { get; }

    public string InputDigest { get; }

    public string OutputDigest { get; }

    public bool Changed =>
        !string.Equals(InputDigest, OutputDigest, StringComparison.Ordinal);

    internal JsonElement ToSnapshotExtension()
    {
        return JsonArrayBuilder.Object(
            ("inputMessageCount",
                JsonArrayBuilder.Number(InputMessageCount)),
            ("outputMessageCount",
                JsonArrayBuilder.Number(OutputMessageCount)),
            ("removedReasoningParts",
                JsonArrayBuilder.Number(RemovedReasoningParts)),
            ("removedOrphanToolResults",
                JsonArrayBuilder.Number(RemovedOrphanToolResults)),
            ("removedDuplicateToolCalls",
                JsonArrayBuilder.Number(RemovedDuplicateToolCalls)),
            ("synthesizedToolResults",
                JsonArrayBuilder.Number(SynthesizedToolResults)),
            ("inputDigest", JsonArrayBuilder.String(InputDigest)),
            ("outputDigest", JsonArrayBuilder.String(OutputDigest)));
    }
}

public sealed class ProviderPreparedRequest
{
    public ProviderPreparedRequest(
        StreamingModelRequest request,
        ProviderRequestPreparationReport report)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public StreamingModelRequest Request { get; }

    public ProviderRequestPreparationReport Report { get; }
}

public interface IProviderRequestAdapter
{
    ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
        ProviderRequestPreparationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds a provider-only request view. Repairs never change the runtime's
/// authoritative transcript, operation ledger, or host receipts.
/// </summary>
public sealed class ProviderRequestSanitizer : IProviderRequestAdapter
{
    public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
        ProviderRequestPreparationContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProviderRequestContentGuard.EnsureInputWithinLimits(
            context.Request.Messages,
            context.Request.Tools,
            cancellationToken);
        var source = SnapshotMessages(
            context.Request.Messages,
            cancellationToken);
        var inputDigest = DigestMessages(source, cancellationToken);
        var calls = new Dictionary<string, ToolCallLocation>(
            StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<NormalizedMessage>(source.Count);
        var removedReasoning = 0;
        var removedOrphans = 0;
        var removedDuplicates = 0;

        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceMessage = source[sourceIndex];
            var parts = new List<NormalizedContentPart>();
            foreach (var part in sourceMessage.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(
                        part.Type,
                        NormalizedPartTypes.Reasoning,
                        StringComparison.Ordinal)
                    && !context.Capabilities.ReasoningInput)
                {
                    removedReasoning++;
                    continue;
                }

                if (string.Equals(
                        part.Type,
                        NormalizedPartTypes.ToolCall,
                        StringComparison.Ordinal))
                {
                    var callId = part.ToolCallId!;
                    if (calls.ContainsKey(callId))
                    {
                        removedDuplicates++;
                        continue;
                    }

                    calls.Add(
                        callId,
                        new ToolCallLocation(
                            messages.Count,
                            part.ToolName!,
                            sourceMessage.CreatedAt));
                }
                else if (string.Equals(
                             part.Type,
                             NormalizedPartTypes.ToolResult,
                             StringComparison.Ordinal))
                {
                    var callId = part.ToolCallId!;
                    if (!calls.ContainsKey(callId) || !completed.Add(callId))
                    {
                        removedOrphans++;
                        continue;
                    }
                }

                parts.Add(ClonePart(part));
            }

            if (parts.Count == 0)
            {
                continue;
            }

            messages.Add(
                new NormalizedMessage
                {
                    MessageId = sourceMessage.MessageId,
                    Role = sourceMessage.Role,
                    CreatedAt = sourceMessage.CreatedAt,
                    Parts = parts
                });
        }

        var synthesized = 0;
        if (context.Capabilities.RequiresCompleteToolPairs)
        {
            var missingCallCount = calls.Count - completed.Count;
            if (missingCallCount
                > ProviderRequestContentGuard.MaxMessages - messages.Count)
            {
                throw ProviderRequestContentGuard.PreparedLimitExceeded(
                    context.ProviderId);
            }

            foreach (var pair in calls
                         .Where(pair => !completed.Contains(pair.Key))
                         .OrderByDescending(pair => pair.Value.MessageIndex)
                         .ThenByDescending(
                             pair => pair.Key,
                             StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = ProtocolJson.ParseElement(
                    """{"error":{"code":"provider_request_tool_result_missing","message":"The prior tool call did not produce a durable result."},"ok":false}""");
                messages.Insert(
                    Math.Min(pair.Value.MessageIndex + 1, messages.Count),
                    new NormalizedMessage
                    {
                        MessageId = "provider-repair:" + pair.Key,
                        Role = NormalizedRoles.Tool,
                        CreatedAt = pair.Value.CreatedAt,
                        Parts = new List<NormalizedContentPart>
                        {
                            NormalizedContentPart.FromToolResult(
                                pair.Key,
                                pair.Value.ToolName,
                                payload)
                        }
                    });
                synthesized++;
            }
        }

        ValidateProviderLimits(
            context.Capabilities,
            context.Request.Tools,
            cancellationToken);
        ProviderRequestContentGuard.EnsurePreparedWithinLimits(
            messages,
            context.Request.Tools,
            context.ProviderId,
            cancellationToken);
        var output = new ReadOnlyCollection<NormalizedMessage>(
            SnapshotMessages(messages, cancellationToken).ToArray());
        var prepared = CloneRequest(
            context.Request,
            output,
            cancellationToken);
        return new ValueTask<ProviderPreparedRequest>(
            new ProviderPreparedRequest(
                prepared,
                new ProviderRequestPreparationReport(
                    source.Count,
                    output.Count,
                    removedReasoning,
                    removedOrphans,
                    removedDuplicates,
                    synthesized,
                    inputDigest,
                    DigestMessages(output, cancellationToken))));
    }

    internal static ProviderPreparedRequest Unchanged(
        StreamingModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var messages = SnapshotMessages(
            request.Messages,
            cancellationToken);
        var digest = DigestMessages(messages, cancellationToken);
        return new ProviderPreparedRequest(
            CloneRequest(request, messages, cancellationToken),
            new ProviderRequestPreparationReport(
                messages.Count,
                messages.Count,
                0,
                0,
                0,
                0,
                digest,
                digest));
    }

    internal static void ValidateProviderLimits(
        ProviderCapabilities capabilities,
        IReadOnlyList<ToolDescriptor> tools,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (capabilities.MaxTools > 0 && tools.Count > capabilities.MaxTools)
        {
            throw new ProviderException(
                "provider_tool_count_exceeded",
                "capability",
                "The disclosed tool set exceeds this provider's limit.",
                false,
                usageKnownToBeZero: true);
        }

        if (capabilities.MaxToolSchemaUtf8Bytes <= 0)
        {
            return;
        }

        var bytes = 0;
        foreach (var tool in tools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes = checked(
                bytes
                + Encoding.UTF8.GetByteCount(ProtocolJson.Serialize(tool)));
            if (bytes > capabilities.MaxToolSchemaUtf8Bytes)
            {
                throw new ProviderException(
                    "provider_tool_schema_bytes_exceeded",
                    "capability",
                    "The disclosed tool schemas exceed this provider's limit.",
                    false,
                    usageKnownToBeZero: true);
            }
        }
    }

    private static StreamingModelRequest CloneRequest(
        StreamingModelRequest source,
        IReadOnlyList<NormalizedMessage> messages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tools = new ToolDescriptor[source.Tools.Count];
        for (var index = 0; index < source.Tools.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tools[index] = ProtocolJson.DeserializeToolDescriptor(
                ProtocolJson.Serialize(source.Tools[index]));
        }

        return new StreamingModelRequest
        {
            RunId = source.RunId,
            RunAttemptId = source.RunAttemptId,
            TurnId = source.TurnId,
            ProviderAttemptId = source.ProviderAttemptId,
            StreamAttemptId = source.StreamAttemptId,
            Messages = messages,
            Tools = tools,
            MaxOutputTokens = source.MaxOutputTokens,
            OpaqueContinuationState =
                source.OpaqueContinuationState?.Snapshot()
        };
    }

    private static List<NormalizedMessage> SnapshotMessages(
        IReadOnlyList<NormalizedMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var snapshot = new List<NormalizedMessage>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.Add(
                NormalizedMessageJournalCodec.CloneValidated(
                    messages[index],
                    cancellationToken));
        }

        return snapshot;
    }

    private static NormalizedContentPart ClonePart(
        NormalizedContentPart part)
    {
        return new NormalizedContentPart
        {
            Type = part.Type,
            Text = part.Text,
            Json = part.Json?.Clone(),
            ToolCallId = part.ToolCallId,
            ToolName = part.ToolName,
            ToolVersion = part.ToolVersion,
            ToolEffect = part.ToolEffect,
            ToolDescriptorDigest = part.ToolDescriptorDigest
        };
    }

    internal static string DigestMessages(
        IReadOnlyList<NormalizedMessage> messages,
        CancellationToken cancellationToken = default)
    {
        return ProviderRequestMessageDigest.Compute(
            messages,
            cancellationToken);
    }

    private sealed class ToolCallLocation
    {
        public ToolCallLocation(
            int messageIndex,
            string toolName,
            DateTimeOffset createdAt)
        {
            MessageIndex = messageIndex;
            ToolName = toolName;
            CreatedAt = createdAt;
        }

        public int MessageIndex { get; }

        public string ToolName { get; }

        public DateTimeOffset CreatedAt { get; }
    }
}
