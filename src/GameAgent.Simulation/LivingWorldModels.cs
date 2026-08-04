using GameAgent.Protocol;

namespace GameAgent.Simulation;

public static class LivingWorldActivationTiers
{
    public const string Foreground = "foreground";
    public const string Nearby = "nearby";
    public const string Background = "background";
    public const string Dormant = "dormant";
}

public static class LivingWorldDecisionKinds
{
    public const string RunNow = "run_now";
    public const string Coalesce = "coalesce";
    public const string Aggregate = "aggregate";
    public const string Skip = "skip";
}

public sealed class LivingWorldActorSignal
{
    public string ActorId { get; set; } = string.Empty;
    public bool HasDirectPlayerInput { get; set; }
    public bool IsVisible { get; set; }
    public bool IsInCombat { get; set; }
    public int PendingMessages { get; set; }
    public int PendingTriggers { get; set; }
    public double? DistanceToNearestPlayer { get; set; }
    public double Salience { get; set; }
    public long LastEvaluatedGameTick { get; set; }
    public int EstimatedTokens { get; set; } = 1;
    public int EstimatedSteps { get; set; } = 1;
}

public sealed class LivingWorldCycle
{
    public string WorldId { get; set; } = string.Empty;
    public long GameTick { get; set; }
    public int? MaxRuns { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxSteps { get; set; }
}

public sealed class LivingWorldPolicyOptions
{
    public int MaxActorsPerCycle { get; set; } = 64;
    public int MaxForegroundActors { get; set; } = 32;
    public int MaxNearbyActors { get; set; } = 24;
    public int MaxBackgroundActors { get; set; } = 8;
    public int MaxEstimatedTokensPerCycle { get; set; } = 32_768;
    public int MaxEstimatedStepsPerCycle { get; set; } = 512;
    public double ForegroundDistance { get; set; } = 12;
    public double NearbyDistance { get; set; } = 64;
    public double NearbySalience { get; set; } = 0.7;
    public double BackgroundSalience { get; set; } = 0.2;
    public long DormantAfterGameTicks { get; set; } = 10_000;
    public long StarvationAfterGameTicks { get; set; } = 2_000;

    internal LivingWorldPolicyOptions Snapshot()
    {
        ValidatePositive(MaxActorsPerCycle, nameof(MaxActorsPerCycle), 1_000_000);
        ValidateRange(MaxForegroundActors, nameof(MaxForegroundActors), 0, MaxActorsPerCycle);
        ValidateRange(MaxNearbyActors, nameof(MaxNearbyActors), 0, MaxActorsPerCycle);
        ValidateRange(MaxBackgroundActors, nameof(MaxBackgroundActors), 0, MaxActorsPerCycle);
        ValidatePositive(MaxEstimatedTokensPerCycle, nameof(MaxEstimatedTokensPerCycle), int.MaxValue);
        ValidatePositive(MaxEstimatedStepsPerCycle, nameof(MaxEstimatedStepsPerCycle), int.MaxValue);
        ValidateFiniteNonNegative(ForegroundDistance, nameof(ForegroundDistance));
        ValidateFiniteNonNegative(NearbyDistance, nameof(NearbyDistance));
        if (NearbyDistance < ForegroundDistance) throw new ArgumentOutOfRangeException(nameof(NearbyDistance));
        ValidateUnit(NearbySalience, nameof(NearbySalience));
        ValidateUnit(BackgroundSalience, nameof(BackgroundSalience));
        if (NearbySalience < BackgroundSalience) throw new ArgumentOutOfRangeException(nameof(NearbySalience));
        if (DormantAfterGameTicks < 0) throw new ArgumentOutOfRangeException(nameof(DormantAfterGameTicks));
        if (StarvationAfterGameTicks < 1) throw new ArgumentOutOfRangeException(nameof(StarvationAfterGameTicks));
        return (LivingWorldPolicyOptions)MemberwiseClone();
    }

    private static void ValidatePositive(int value, string name, int maximum)
    {
        if (value < 1 || value > maximum) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateRange(int value, string name, int minimum, int maximum)
    {
        if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateFiniteNonNegative(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateUnit(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(name);
    }
}

public sealed class LivingWorldDecision
{
    internal LivingWorldDecision(
        string actorId,
        string activationTier,
        string decision,
        string reasonCode,
        string workloadClass,
        long overdueGameTicks,
        int coalescedSignalCount,
        int? admissionOrder)
    {
        ActorId = actorId;
        ActivationTier = activationTier;
        Decision = decision;
        ReasonCode = reasonCode;
        WorkloadClass = workloadClass;
        OverdueGameTicks = overdueGameTicks;
        CoalescedSignalCount = coalescedSignalCount;
        AdmissionOrder = admissionOrder;
    }

    public string ActorId { get; }
    public string ActivationTier { get; }
    public string Decision { get; }
    public string ReasonCode { get; }
    public string WorkloadClass { get; }
    public long OverdueGameTicks { get; }
    public int CoalescedSignalCount { get; }
    public int? AdmissionOrder { get; }
}

public sealed class LivingWorldPlan
{
    internal LivingWorldPlan(string worldId, long gameTick, IReadOnlyList<LivingWorldDecision> decisions)
    {
        WorldId = worldId;
        GameTick = gameTick;
        Decisions = decisions;
    }

    public string WorldId { get; }
    public long GameTick { get; }
    public IReadOnlyList<LivingWorldDecision> Decisions { get; }
    public IReadOnlyList<LivingWorldDecision> Runnable =>
        Decisions
            .Where(static item => item.Decision == LivingWorldDecisionKinds.RunNow)
            .OrderBy(static item => item.AdmissionOrder)
            .ThenBy(static item => item.ActorId, StringComparer.Ordinal)
            .ToArray();
}
