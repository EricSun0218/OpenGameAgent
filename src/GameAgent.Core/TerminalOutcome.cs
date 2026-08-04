using System.Text.Json;

namespace GameAgent.Core;

internal sealed class DurableTerminalOutcome
{
    public DurableTerminalOutcome(
        string code,
        string category,
        string safeMessage)
    {
        Code = RuntimeGuard.RequiredReasonCode(code, nameof(code));
        Category = RuntimeGuard.RequiredUtf8(
            category,
            96,
            nameof(category));
        SafeMessage = RuntimeGuard.RequiredUtf8(
            safeMessage,
            2_048,
            nameof(safeMessage));
    }

    public string Code { get; }

    public string Category { get; }

    public string SafeMessage { get; }
}

internal static class TerminalOutcomeJournalCodec
{
    public const string ExtensionName = "runtimeTerminalOutcome";

    public static IReadOnlyDictionary<string, JsonElement> Extensions(
        DurableTerminalOutcome outcome)
    {
        if (outcome is null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [ExtensionName] = JsonArrayBuilder.Object(
                ("code", JsonArrayBuilder.String(outcome.Code)),
                ("category", JsonArrayBuilder.String(outcome.Category)),
                ("safeMessage", JsonArrayBuilder.String(
                    outcome.SafeMessage)))
        };
    }

    public static DurableTerminalOutcome Read(JsonElement value)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            new JsonValueLimits(
                maxUtf8Bytes: 4_096,
                maxDepth: 3,
                maxNodes: 8,
                maxStringUtf8Bytes: 2_048,
                maxContainerItems: 4),
            ExtensionName);
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 3
            || !value.TryGetProperty("code", out var codeValue)
            || codeValue.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("category", out var categoryValue)
            || categoryValue.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("safeMessage", out var messageValue)
            || messageValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "The durable terminal outcome is malformed.");
        }

        try
        {
            return new DurableTerminalOutcome(
                codeValue.GetString()!,
                categoryValue.GetString()!,
                messageValue.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The durable terminal outcome is malformed.",
                exception);
        }
    }
}
