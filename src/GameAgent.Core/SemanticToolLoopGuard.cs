using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Configures the bounded semantic no-progress guard for tool loops.
/// Repetition counts exclude the first observed outcome.
/// </summary>
public sealed class SemanticToolLoopGuardOptions
{
    public bool Enabled { get; set; } = true;

    public int WarningRepetitions { get; set; } = 2;

    public int HardStopRepetitions { get; set; } = 4;

    public bool DetectArgumentChurn { get; set; } = true;

    public int ArgumentChurnWarningRepetitions { get; set; } = 4;

    public int ArgumentChurnHardStopRepetitions { get; set; } = 8;

    public int MaxTrackedSignatures { get; set; } = 128;

    public int MaxRebuildMessages { get; set; } = 2_048;

    public int MaxPendingToolCalls { get; set; } = 256;

    public int MaxDigestJsonUtf8Bytes { get; set; } = 262_144;

    internal void Validate()
    {
        if (WarningRepetitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(WarningRepetitions));
        }

        if (HardStopRepetitions <= WarningRepetitions)
        {
            throw new ArgumentOutOfRangeException(nameof(HardStopRepetitions));
        }

        if (ArgumentChurnWarningRepetitions < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArgumentChurnWarningRepetitions));
        }

        if (ArgumentChurnHardStopRepetitions
            <= ArgumentChurnWarningRepetitions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArgumentChurnHardStopRepetitions));
        }

        if (MaxTrackedSignatures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTrackedSignatures));
        }

        if (MaxRebuildMessages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRebuildMessages));
        }

        if (MaxPendingToolCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPendingToolCalls));
        }

        if (MaxDigestJsonUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDigestJsonUtf8Bytes));
        }
    }

    internal SemanticToolLoopGuardOptions Snapshot()
    {
        Validate();
        return new SemanticToolLoopGuardOptions
        {
            Enabled = Enabled,
            WarningRepetitions = WarningRepetitions,
            HardStopRepetitions = HardStopRepetitions,
            DetectArgumentChurn = DetectArgumentChurn,
            ArgumentChurnWarningRepetitions =
                ArgumentChurnWarningRepetitions,
            ArgumentChurnHardStopRepetitions =
                ArgumentChurnHardStopRepetitions,
            MaxTrackedSignatures = MaxTrackedSignatures,
            MaxRebuildMessages = MaxRebuildMessages,
            MaxPendingToolCalls = MaxPendingToolCalls,
            MaxDigestJsonUtf8Bytes = MaxDigestJsonUtf8Bytes
        };
    }
}

internal sealed class SemanticToolLoopGuard
{
    internal const string WarningContentType =
        "application/vnd.game-agent.tool-loop-warning+json";
    internal const string WarningReasonCode = "tool_no_progress_warning";
    internal const string ArgumentChurnWarningReasonCode =
        "tool_argument_churn_warning";
    internal const string HardStopReasonCode = "tool_no_progress";
    internal const string ExactCallPatternKind = "exact_call";
    internal const string ArgumentChurnPatternKind = "argument_churn";

    private readonly SemanticToolLoopGuardOptions _options;
    private readonly JsonValueLimits _digestLimits;
    private readonly Dictionary<string, PendingCall> _pending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StablePattern> _patterns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArgumentChurnPattern> _churnPatterns =
        new(StringComparer.Ordinal);
    private long _sequence;

    private SemanticToolLoopGuard(SemanticToolLoopGuardOptions options)
    {
        _options = options;
        _digestLimits = new JsonValueLimits(
            maxUtf8Bytes: options.MaxDigestJsonUtf8Bytes);
    }

    internal SemanticToolLoopGuardDecision? Decision
    {
        get
        {
            if (!_options.Enabled)
            {
                return null;
            }

            var pattern = _patterns.Values
                .Where(item =>
                    item.RepetitionCount >= _options.WarningRepetitions)
                .OrderByDescending(item => item.RepetitionCount)
                .ThenByDescending(item => item.LastSequence)
                .ThenBy(item => item.SignatureDigest, StringComparer.Ordinal)
                .FirstOrDefault();
            var candidates = new List<SemanticToolLoopGuardDecision>(2);
            if (pattern is not null)
            {
                candidates.Add(new SemanticToolLoopGuardDecision(
                    pattern.ToolName,
                    ExactCallPatternKind,
                    pattern.SignatureDigest,
                    pattern.OutcomeDigest,
                    pattern.RepetitionCount,
                    pattern.LastObservedAt,
                    pattern.LastSequence,
                    pattern.RepetitionCount >= _options.HardStopRepetitions,
                    WarningReasonCode,
                    _options.HardStopRepetitions));
            }

            if (_options.DetectArgumentChurn)
            {
                var churn = _churnPatterns.Values
                    .Where(item => item.RepetitionCount
                        >= _options.ArgumentChurnWarningRepetitions)
                    .OrderByDescending(item => item.RepetitionCount)
                    .ThenByDescending(item => item.LastSequence)
                    .ThenBy(item => item.PatternDigest, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (churn is not null)
                {
                    candidates.Add(new SemanticToolLoopGuardDecision(
                        churn.ToolName,
                        ArgumentChurnPatternKind,
                        churn.PatternDigest,
                        churn.OutcomeDigest,
                        churn.RepetitionCount,
                        churn.LastObservedAt,
                        churn.LastSequence,
                        churn.RepetitionCount
                            >= _options.ArgumentChurnHardStopRepetitions,
                        ArgumentChurnWarningReasonCode,
                        _options.ArgumentChurnHardStopRepetitions));
                }
            }

            return candidates
                .OrderByDescending(item => item.HardStop)
                .ThenByDescending(item => item.RepetitionCount)
                .ThenByDescending(item => item.LastSequence)
                .ThenBy(item => item.PatternDigest, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    internal static SemanticToolLoopGuard Rebuild(
        SemanticToolLoopGuardOptions options,
        IReadOnlyList<NormalizedMessage> transcript)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (transcript is null)
        {
            throw new ArgumentNullException(nameof(transcript));
        }

        var snapshot = options.Snapshot();
        var guard = new SemanticToolLoopGuard(snapshot);
        if (!snapshot.Enabled || transcript.Count == 0)
        {
            return guard;
        }

        var first = Math.Max(0, transcript.Count - snapshot.MaxRebuildMessages);
        guard.ObserveMessages(transcript.Skip(first));
        return guard;
    }

    internal void ObserveMessages(IEnumerable<NormalizedMessage> messages)
    {
        if (!_options.Enabled)
        {
            return;
        }

        foreach (var message in messages)
        {
            if (message is null || IsWarningMessage(message))
            {
                continue;
            }

            if (string.Equals(
                    message.Role,
                    NormalizedRoles.Assistant,
                    StringComparison.Ordinal))
            {
                if (!ObserveAssistant(message))
                {
                    ResetForIndeterminateOutcome();
                    return;
                }
            }
            else if (string.Equals(
                         message.Role,
                         NormalizedRoles.Tool,
                         StringComparison.Ordinal))
            {
                ObserveToolResults(message);
            }
        }

        if (_pending.Count > 0)
        {
            ResetForIndeterminateOutcome();
        }
    }

    internal void ResetForIndeterminateOutcome()
    {
        _pending.Clear();
        ResetPatterns();
    }

    internal NormalizedMessage? CreateWarningMessage()
    {
        var decision = Decision;
        if (decision is null || decision.HardStop)
        {
            return null;
        }

        var payload = JsonArrayBuilder.Object(
            ("contentType", JsonArrayBuilder.String(WarningContentType)),
            ("reasonCode", JsonArrayBuilder.String(
                decision.WarningReasonCode)),
            ("toolName", JsonArrayBuilder.String(decision.ToolName)),
            ("patternKind", JsonArrayBuilder.String(decision.PatternKind)),
            ("patternDigest",
                JsonArrayBuilder.String(decision.PatternDigest)),
            ("callSignatureDigest",
                JsonArrayBuilder.String(decision.CallSignatureDigest)),
            ("outcomeDigest", JsonArrayBuilder.String(decision.OutcomeDigest)),
            ("repetitionCount",
                JsonArrayBuilder.Number(decision.RepetitionCount)),
            ("hardStopRepetitions",
                JsonArrayBuilder.Number(decision.HardStopRepetitions)));
        var idDigest = new CanonicalDigestBuilder();
        idDigest.Add("kind", decision.WarningReasonCode);
        idDigest.Add("pattern", decision.PatternDigest);
        idDigest.Add("outcome", decision.OutcomeDigest);
        idDigest.Add("repetitions", decision.RepetitionCount);
        return new NormalizedMessage
        {
            MessageId = "tool-loop-warning-" + idDigest.Finish(),
            Role = NormalizedRoles.User,
            CreatedAt = decision.LastObservedAt,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromJson(payload)
            }
        };
    }

    internal JsonElement SafeDiagnostic()
    {
        var decision = Decision
            ?? throw new InvalidOperationException(
                "The tool-loop guard has no active decision.");
        return JsonArrayBuilder.Object(
            ("reasonCode", JsonArrayBuilder.String(
                decision.HardStop
                    ? HardStopReasonCode
                    : decision.WarningReasonCode)),
            ("toolName", JsonArrayBuilder.String(decision.ToolName)),
            ("patternKind", JsonArrayBuilder.String(decision.PatternKind)),
            ("patternDigest",
                JsonArrayBuilder.String(decision.PatternDigest)),
            ("callSignatureDigest",
                JsonArrayBuilder.String(decision.CallSignatureDigest)),
            ("outcomeDigest", JsonArrayBuilder.String(decision.OutcomeDigest)),
            ("repetitionCount",
                JsonArrayBuilder.Number(decision.RepetitionCount)),
            ("hardStopRepetitions",
                JsonArrayBuilder.Number(decision.HardStopRepetitions)));
    }

    internal static bool IsWarningMessage(NormalizedMessage message)
    {
        if (!string.Equals(
                message.Role,
                NormalizedRoles.User,
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var part in message.Parts)
        {
            if (!string.Equals(
                    part.Type,
                    NormalizedPartTypes.Json,
                    StringComparison.Ordinal)
                || !part.Json.HasValue
                || part.Json.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (part.Json.Value.TryGetProperty(
                    "contentType",
                    out var contentType)
                && contentType.ValueKind == JsonValueKind.String
                && string.Equals(
                    contentType.GetString(),
                    WarningContentType,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool ObserveAssistant(NormalizedMessage message)
    {
        foreach (var part in message.Parts)
        {
            if (!string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolCall,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.ToolCallId)
                || string.IsNullOrWhiteSpace(part.ToolName)
                || !part.Json.HasValue
                || _pending.ContainsKey(part.ToolCallId))
            {
                return false;
            }

            if (_pending.Count >= _options.MaxPendingToolCalls)
            {
                return false;
            }

            var digests = TryComputeCallDigests(part);
            _pending.Add(
                part.ToolCallId,
                new PendingCall(
                    part.ToolName,
                    KnownEffect(part.ToolEffect),
                    digests?.SignatureDigest,
                    digests?.ToolIdentityDigest));
        }

        return true;
    }

    private void ObserveToolResults(NormalizedMessage message)
    {
        foreach (var part in message.Parts)
        {
            if (!string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolResult,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.ToolCallId)
                || !_pending.Remove(part.ToolCallId, out var call)
                || call.SignatureDigest is null
                || call.ToolIdentityDigest is null
                || !part.Json.HasValue)
            {
                ResetPatterns();
                continue;
            }

            var outcome = Classify(call, part.Json.Value);
            switch (outcome.Kind)
            {
                case SemanticOutcomeKind.Progress:
                case SemanticOutcomeKind.Indeterminate:
                    ResetPatterns();
                    break;
                case SemanticOutcomeKind.Comparable:
                    ObserveComparable(
                        call,
                        outcome.Digest!,
                        message.CreatedAt);
                    break;
            }
        }
    }

    private CallDigests? TryComputeCallDigests(NormalizedContentPart part)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(part.ToolName!) > 96
                || (part.ToolVersion is not null
                    && Encoding.UTF8.GetByteCount(part.ToolVersion) > 32)
                || (part.ToolDescriptorDigest is not null
                    && Encoding.UTF8.GetByteCount(
                        part.ToolDescriptorDigest) > 256))
            {
                return null;
            }

            JsonValueInspector.ValidateAndMeasure(
                part.Json!.Value,
                _digestLimits,
                "toolArguments");
            var toolIdentity = new CanonicalDigestBuilder();
            toolIdentity.Add("kind", "tool_identity");
            toolIdentity.Add("toolName", part.ToolName);
            toolIdentity.Add("toolVersion", part.ToolVersion);
            toolIdentity.Add("toolEffect", KnownEffect(part.ToolEffect));
            toolIdentity.Add(
                "toolDescriptorDigest",
                part.ToolDescriptorDigest);
            var identityDigest = toolIdentity.Finish();

            var signature = new CanonicalDigestBuilder();
            signature.Add("kind", "tool_call");
            signature.Add("toolName", part.ToolName);
            signature.Add("toolVersion", part.ToolVersion);
            signature.Add("toolEffect", KnownEffect(part.ToolEffect));
            signature.Add(
                "toolDescriptorDigest",
                part.ToolDescriptorDigest);
            signature.Add("arguments", part.Json.Value);
            return new CallDigests(
                signature.Finish(),
                identityDigest);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private SemanticOutcome Classify(PendingCall call, JsonElement value)
    {
        try
        {
            JsonValueInspector.ValidateAndMeasure(
                value,
                _digestLimits,
                "toolResult");
        }
        catch (ArgumentException)
        {
            return SemanticOutcome.Progress;
        }
        catch (OverflowException)
        {
            return SemanticOutcome.Progress;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return string.Equals(
                call.Effect,
                ToolEffects.PureRead,
                StringComparison.Ordinal)
                ? SemanticOutcome.Comparable(
                    ComputeValueOutcomeDigest(
                        "pure_read_result",
                        value))
                : SemanticOutcome.Progress;
        }

        if (value.TryGetProperty("status", out var statusValue))
        {
            if (statusValue.ValueKind != JsonValueKind.String)
            {
                return SemanticOutcome.Indeterminate;
            }

            var status = statusValue.GetString();
            if (string.Equals(
                    status,
                    ReceiptStatuses.Unknown,
                    StringComparison.Ordinal))
            {
                return SemanticOutcome.Indeterminate;
            }

            if (HasProgressEvidence(value))
            {
                return SemanticOutcome.Progress;
            }

            if (string.Equals(
                    status,
                    ReceiptStatuses.Failed,
                    StringComparison.Ordinal)
                || string.Equals(
                    status,
                    ReceiptStatuses.Rejected,
                    StringComparison.Ordinal))
            {
                return SemanticOutcome.Comparable(
                    ComputeOutcomeDigest("terminal_receipt", value, true));
            }

            if (!string.Equals(
                    status,
                    ReceiptStatuses.Succeeded,
                    StringComparison.Ordinal))
            {
                return SemanticOutcome.Indeterminate;
            }

            if (!string.Equals(
                    call.Effect,
                    ToolEffects.PureRead,
                    StringComparison.Ordinal))
            {
                return SemanticOutcome.Progress;
            }

            return SemanticOutcome.Comparable(
                ComputeOutcomeDigest("pure_read_receipt", value, true));
        }

        if (HasProgressEvidence(value))
        {
            return SemanticOutcome.Progress;
        }

        if (IsImmediateTerminalError(value))
        {
            return SemanticOutcome.Comparable(
                ComputeOutcomeDigest("terminal_error", value, false));
        }

        if (TryReadActivationResult(value, out var activated))
        {
            return activated
                ? SemanticOutcome.Progress
                : SemanticOutcome.Comparable(
                    ComputeOutcomeDigest(
                        "terminal_activation_error",
                        value,
                        false));
        }

        if (!string.Equals(
                call.Effect,
                ToolEffects.PureRead,
                StringComparison.Ordinal))
        {
            return SemanticOutcome.Progress;
        }

        return SemanticOutcome.Comparable(
            ComputeOutcomeDigest("pure_read_result", value, false));
    }

    private static bool HasProgressEvidence(JsonElement value)
    {
        if (value.TryGetProperty("stateDiff", out var stateDiff)
            && stateDiff.ValueKind is not JsonValueKind.Null
                and not JsonValueKind.Undefined)
        {
            return true;
        }

        if (!value.TryGetProperty(
                "authoritativeObservations",
                out var observations))
        {
            return false;
        }

        if (observations.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return observations.GetArrayLength() > 0;
    }

    private static bool IsImmediateTerminalError(JsonElement value)
    {
        return TryReadString(value, "code", out _)
               && TryReadString(value, "category", out _)
               && TryReadString(value, "message", out _);
    }

    private static bool TryReadActivationResult(
        JsonElement value,
        out bool activated)
    {
        activated = false;
        if (!TryReadString(value, "contentType", out var contentType)
            || !string.Equals(
                contentType,
                "application/vnd.game-agent.tool-activation-result+json",
                StringComparison.Ordinal)
            || !value.TryGetProperty("activated", out var property)
            || property.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False)
        {
            return false;
        }

        activated = property.GetBoolean();
        return true;
    }

    private static bool TryReadString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return result is not null;
    }

    private static string ComputeOutcomeDigest(
        string kind,
        JsonElement value,
        bool excludeReceiptVolatility)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("kind", kind);
        foreach (var property in value
                     .EnumerateObject()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (excludeReceiptVolatility
                && (string.Equals(
                        property.Name,
                        "operationId",
                        StringComparison.Ordinal)
                    || string.Equals(
                        property.Name,
                        "receivedAt",
                        StringComparison.Ordinal)
                    || string.Equals(
                        property.Name,
                        "committedAt",
                        StringComparison.Ordinal)))
            {
                continue;
            }

            digest.Add(property.Name, property.Value);
        }

        return digest.Finish();
    }

    private static string ComputeValueOutcomeDigest(
        string kind,
        JsonElement value)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("kind", kind);
        digest.Add("value", value);
        return digest.Finish();
    }

    private void ObserveComparable(
        PendingCall call,
        string outcomeDigest,
        DateTimeOffset observedAt)
    {
        _sequence++;
        ObserveArgumentChurn(call, outcomeDigest, observedAt);
        if (_patterns.TryGetValue(call.SignatureDigest!, out var existing))
        {
            if (string.Equals(
                    existing.OutcomeDigest,
                    outcomeDigest,
                    StringComparison.Ordinal))
            {
                existing.RepetitionCount++;
                existing.LastSequence = _sequence;
                existing.LastObservedAt = observedAt;
                return;
            }

            ResetPatterns();
        }

        if (_patterns.Count >= _options.MaxTrackedSignatures)
        {
            var evicted = _patterns.Values
                .OrderBy(item => item.LastSequence)
                .ThenBy(item => item.SignatureDigest, StringComparer.Ordinal)
                .First();
            _patterns.Remove(evicted.SignatureDigest);
        }

        _patterns[call.SignatureDigest!] = new StablePattern(
            call.ToolName,
            call.SignatureDigest!,
            outcomeDigest,
            _sequence,
            observedAt);
    }

    private void ObserveArgumentChurn(
        PendingCall call,
        string outcomeDigest,
        DateTimeOffset observedAt)
    {
        if (!_options.DetectArgumentChurn)
        {
            return;
        }

        foreach (var stale in _churnPatterns.Values
                     .Where(item => string.Equals(
                         item.ToolIdentityDigest,
                         call.ToolIdentityDigest,
                         StringComparison.Ordinal)
                         && !string.Equals(
                             item.OutcomeDigest,
                             outcomeDigest,
                             StringComparison.Ordinal))
                     .Select(item => item.PatternDigest)
                     .ToArray())
        {
            _churnPatterns.Remove(stale);
        }

        var digest = new CanonicalDigestBuilder();
        digest.Add("kind", ArgumentChurnPatternKind);
        digest.Add("toolIdentity", call.ToolIdentityDigest);
        digest.Add("outcome", outcomeDigest);
        var patternDigest = digest.Finish();
        if (_churnPatterns.TryGetValue(patternDigest, out var existing))
        {
            if (!string.Equals(
                    existing.LastSignatureDigest,
                    call.SignatureDigest,
                    StringComparison.Ordinal))
            {
                existing.RepetitionCount++;
                existing.LastSignatureDigest = call.SignatureDigest!;
                existing.LastSequence = _sequence;
                existing.LastObservedAt = observedAt;
            }

            return;
        }

        if (_churnPatterns.Count >= _options.MaxTrackedSignatures)
        {
            var evicted = _churnPatterns.Values
                .OrderBy(item => item.LastSequence)
                .ThenBy(item => item.PatternDigest, StringComparer.Ordinal)
                .First();
            _churnPatterns.Remove(evicted.PatternDigest);
        }

        _churnPatterns[patternDigest] = new ArgumentChurnPattern(
            call.ToolName,
            call.ToolIdentityDigest!,
            patternDigest,
            outcomeDigest,
            call.SignatureDigest!,
            _sequence,
            observedAt);
    }

    private void ResetPatterns()
    {
        _patterns.Clear();
        _churnPatterns.Clear();
    }

    private static string? KnownEffect(string? effect)
    {
        if (string.Equals(effect, ToolEffects.PureRead, StringComparison.Ordinal)
            || string.Equals(
                effect,
                ToolEffects.AgentLocalWrite,
                StringComparison.Ordinal)
            || string.Equals(
                effect,
                ToolEffects.WorldCommand,
                StringComparison.Ordinal)
            || string.Equals(
                effect,
                ToolEffects.ExternalWrite,
                StringComparison.Ordinal))
        {
            return effect;
        }

        return null;
    }

    private sealed class PendingCall
    {
        public PendingCall(
            string toolName,
            string? effect,
            string? signatureDigest,
            string? toolIdentityDigest)
        {
            ToolName = toolName;
            Effect = effect;
            SignatureDigest = signatureDigest;
            ToolIdentityDigest = toolIdentityDigest;
        }

        public string ToolName { get; }

        public string? Effect { get; }

        public string? SignatureDigest { get; }

        public string? ToolIdentityDigest { get; }
    }

    private sealed class CallDigests
    {
        public CallDigests(
            string signatureDigest,
            string toolIdentityDigest)
        {
            SignatureDigest = signatureDigest;
            ToolIdentityDigest = toolIdentityDigest;
        }

        public string SignatureDigest { get; }

        public string ToolIdentityDigest { get; }
    }

    private sealed class StablePattern
    {
        public StablePattern(
            string toolName,
            string signatureDigest,
            string outcomeDigest,
            long lastSequence,
            DateTimeOffset lastObservedAt)
        {
            ToolName = toolName;
            SignatureDigest = signatureDigest;
            OutcomeDigest = outcomeDigest;
            LastSequence = lastSequence;
            LastObservedAt = lastObservedAt;
        }

        public string ToolName { get; }

        public string SignatureDigest { get; }

        public string OutcomeDigest { get; }

        public int RepetitionCount { get; set; }

        public long LastSequence { get; set; }

        public DateTimeOffset LastObservedAt { get; set; }
    }

    private sealed class ArgumentChurnPattern
    {
        public ArgumentChurnPattern(
            string toolName,
            string toolIdentityDigest,
            string patternDigest,
            string outcomeDigest,
            string lastSignatureDigest,
            long lastSequence,
            DateTimeOffset lastObservedAt)
        {
            ToolName = toolName;
            ToolIdentityDigest = toolIdentityDigest;
            PatternDigest = patternDigest;
            OutcomeDigest = outcomeDigest;
            LastSignatureDigest = lastSignatureDigest;
            LastSequence = lastSequence;
            LastObservedAt = lastObservedAt;
        }

        public string ToolName { get; }

        public string ToolIdentityDigest { get; }

        public string PatternDigest { get; }

        public string OutcomeDigest { get; }

        public string LastSignatureDigest { get; set; }

        public int RepetitionCount { get; set; }

        public long LastSequence { get; set; }

        public DateTimeOffset LastObservedAt { get; set; }
    }

    private readonly struct SemanticOutcome
    {
        private SemanticOutcome(SemanticOutcomeKind kind, string? digest)
        {
            Kind = kind;
            Digest = digest;
        }

        public SemanticOutcomeKind Kind { get; }

        public string? Digest { get; }

        public static SemanticOutcome Progress =>
            new(SemanticOutcomeKind.Progress, null);

        public static SemanticOutcome Indeterminate =>
            new(SemanticOutcomeKind.Indeterminate, null);

        public static SemanticOutcome Comparable(string digest) =>
            new(SemanticOutcomeKind.Comparable, digest);
    }

    private enum SemanticOutcomeKind
    {
        Progress,
        Indeterminate,
        Comparable
    }
}

internal sealed class SemanticToolLoopGuardDecision
{
    public SemanticToolLoopGuardDecision(
        string toolName,
        string patternKind,
        string patternDigest,
        string outcomeDigest,
        int repetitionCount,
        DateTimeOffset lastObservedAt,
        long lastSequence,
        bool hardStop,
        string warningReasonCode,
        int hardStopRepetitions)
    {
        ToolName = toolName;
        PatternKind = patternKind;
        PatternDigest = patternDigest;
        OutcomeDigest = outcomeDigest;
        RepetitionCount = repetitionCount;
        LastObservedAt = lastObservedAt;
        LastSequence = lastSequence;
        HardStop = hardStop;
        WarningReasonCode = warningReasonCode;
        HardStopRepetitions = hardStopRepetitions;
    }

    public string ToolName { get; }

    public string PatternKind { get; }

    public string PatternDigest { get; }

    public string CallSignatureDigest => PatternDigest;

    public string OutcomeDigest { get; }

    public int RepetitionCount { get; }

    public DateTimeOffset LastObservedAt { get; }

    internal long LastSequence { get; }

    public bool HardStop { get; }

    public string WarningReasonCode { get; }

    public int HardStopRepetitions { get; }
}
