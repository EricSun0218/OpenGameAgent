using GameAgent.Protocol;

namespace GameAgent.Storage.Relational;

internal static class RelationalJournalSemantics
{
    public static bool EventsEquivalent(RuntimeEvent candidate, RuntimeEvent existing)
    {
        var canonical = Clone(candidate);
        canonical.Sequence = existing.Sequence;
        canonical.Timestamp = existing.Timestamp;
        if (AllowsAttemptRebinding(candidate) && AllowsAttemptRebinding(existing))
        {
            canonical.AttemptId = existing.AttemptId;
            canonical.StreamAttemptId = existing.StreamAttemptId;
        }
        if (IsReceipt(candidate) && candidate.Kind == existing.Kind)
        {
            try
            {
                if (!ReceiptsEquivalent(Receipt(candidate), Receipt(existing))) return false;
                canonical.Payload = existing.Payload.Clone();
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                return false;
            }
        }
        return ProtocolJson.Serialize(canonical) == ProtocolJson.Serialize(existing);
    }

    public static ActionRequest Request(RuntimeEvent runtimeEvent)
    {
        var value = ProtocolJson.DeserializeActionRequest(runtimeEvent.Payload.GetRawText());
        ProtocolValidator.EnsureValid(value);
        return value;
    }

    public static ActionReceipt Receipt(RuntimeEvent runtimeEvent)
    {
        var value = ProtocolJson.DeserializeActionReceipt(runtimeEvent.Payload.GetRawText());
        ProtocolValidator.EnsureValid(value);
        return value;
    }

    public static bool RequestsEquivalent(ActionRequest left, ActionRequest right) =>
        ProtocolJson.Serialize(left) == ProtocolJson.Serialize(right);

    public static bool ReceiptsEquivalent(ActionReceipt left, ActionReceipt right)
    {
        var canonical = ProtocolJson.DeserializeActionReceipt(ProtocolJson.Serialize(left));
        canonical.ReceivedAt = right.ReceivedAt;
        return ProtocolJson.Serialize(canonical) == ProtocolJson.Serialize(right);
    }

    private static RuntimeEvent Clone(RuntimeEvent value) =>
        ProtocolJson.DeserializeRuntimeEvent(ProtocolJson.Serialize(value));

    private static bool IsReceipt(RuntimeEvent value) => value.Kind is
        RuntimeEventKinds.ActionReceived or RuntimeEventKinds.ActionOutcomeUncertain
        or RuntimeEventKinds.ToolCompleted or RuntimeEventKinds.ToolFailed;

    private static bool AllowsAttemptRebinding(RuntimeEvent value) => value.Kind is
        RuntimeEventKinds.TranscriptMessage or RuntimeEventKinds.ActionRequested
        or RuntimeEventKinds.ActionReceived or RuntimeEventKinds.ActionOutcomeUncertain
        or RuntimeEventKinds.ToolCompleted or RuntimeEventKinds.ToolFailed;
}
