using System.Diagnostics;
using GameAgent.Core;

namespace GameAgent.Observability.OpenTelemetry;

public static class GameAgentTelemetry
{
    public const string ActivitySourceName = "GameAgent.Runtime";
    private static readonly ActivitySource Source = new(ActivitySourceName);

    public static Activity? StartActivity(
        string operation,
        string? workloadClass = null,
        string? engine = null,
        ActivityContext parent = default)
    {
        if (operation is not ("run" or "resume" or "recover" or "action" or "memory" or "generation"))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
        var activity = parent == default
            ? Source.StartActivity("game_agent." + operation, ActivityKind.Internal)
            : Source.StartActivity("game_agent." + operation, ActivityKind.Internal, parent);
        if (activity is null) return null;
        if (workloadClass is not null)
        {
            activity.SetTag("workload.class",
                workloadClass == ProviderWorkloadClasses.Background
                    ? ProviderWorkloadClasses.Background
                    : ProviderWorkloadClasses.Interactive);
        }
        if (engine is not null)
        {
            activity.SetTag("engine", engine is "godot" or "unity" ? engine : "core");
        }
        return activity;
    }
}
