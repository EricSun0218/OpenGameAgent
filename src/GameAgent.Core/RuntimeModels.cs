using System.Globalization;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class ModelMessage
{
    public string Role { get; set; } = string.Empty;

    public string? ToolCallId { get; set; }

    public JsonElement Content { get; set; }
}

public sealed class ModelToolCall
{
    public string ToolCallId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public JsonElement Arguments { get; set; }
}

public sealed class ModelRequest
{
    public string RunId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string AttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public IReadOnlyList<ModelMessage> Messages { get; set; } = Array.Empty<ModelMessage>();

    public IReadOnlyList<ToolDescriptor> Tools { get; set; } = Array.Empty<ToolDescriptor>();
}

public sealed class ModelResponse
{
    public bool IsFinal { get; private set; }

    public JsonElement FinalOutput { get; private set; }

    public IReadOnlyList<ModelToolCall> ToolCalls { get; private set; } =
        Array.Empty<ModelToolCall>();

    public ProviderUsage Usage { get; private set; } = new();

    public static ModelResponse Final(
        JsonElement output,
        ProviderUsage? usage = null)
    {
        return new ModelResponse
        {
            IsFinal = true,
            FinalOutput = output.Clone(),
            Usage = ValidateAndCloneUsage(usage ?? new ProviderUsage())
        };
    }

    public static ModelResponse CallTools(params ModelToolCall[] toolCalls)
    {
        return CallTools(new ProviderUsage(), toolCalls);
    }

    public static ModelResponse CallTools(
        ProviderUsage usage,
        params ModelToolCall[] toolCalls)
    {
        return new ModelResponse
        {
            ToolCalls = toolCalls,
            Usage = ValidateAndCloneUsage(usage)
        };
    }

    internal ProviderUsage GetValidatedUsage()
    {
        return ValidateAndCloneUsage(Usage);
    }

    private static ProviderUsage ValidateAndCloneUsage(
        ProviderUsage? usage)
    {
        if (usage is null)
        {
            throw new ArgumentNullException(nameof(usage));
        }

        var protocolUsage = new AgentUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CostUsd = usage.CostUsd
        };
        if (ProtocolValidator.Validate(protocolUsage).Count != 0
            || !decimal.TryParse(
                usage.CostUsd,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var cost)
            || cost < 0)
        {
            throw new ArgumentException(
                "Provider usage must contain non-negative token counts "
                + "and a representable canonical cost.",
                nameof(usage));
        }

        return new ProviderUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CostUsd = usage.CostUsd
        };
    }
}

public sealed class HeadlessRunRequest
{
    public AgentRun Run { get; set; } = new();

    public IReadOnlyList<ObservationEnvelope> Observations { get; set; } =
        Array.Empty<ObservationEnvelope>();

    public IReadOnlyList<ToolDescriptor> Tools { get; set; } =
        Array.Empty<ToolDescriptor>();
}

public sealed class HeadlessRunOutcome
{
    public AgentRun Run { get; set; } = new();

    public JsonElement? FinalOutput { get; set; }

    public bool IsTerminal =>
        Run.State == RunStates.Completed
        || Run.State == RunStates.BudgetExhausted
        || Run.State == RunStates.Interrupted
        || Run.State == RunStates.Cancelled
        || Run.State == RunStates.Failed;
}
