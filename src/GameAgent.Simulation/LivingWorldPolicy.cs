using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Simulation;

public sealed class LivingWorldPolicy
{
    private readonly LivingWorldPolicyOptions _options;

    public LivingWorldPolicy(LivingWorldPolicyOptions? options = null)
    {
        _options = (options ?? new LivingWorldPolicyOptions()).Snapshot();
    }

    public LivingWorldPlan Plan(
        LivingWorldCycle cycle,
        IReadOnlyList<LivingWorldActorSignal> actors)
    {
        if (cycle is null) throw new ArgumentNullException(nameof(cycle));
        if (actors is null) throw new ArgumentNullException(nameof(actors));
        ValidateId(cycle.WorldId, nameof(cycle.WorldId));
        if (cycle.GameTick < 0) throw new ArgumentOutOfRangeException(nameof(cycle.GameTick));
        var maxRuns = BoundOverride(cycle.MaxRuns, _options.MaxActorsPerCycle, nameof(cycle.MaxRuns));
        var maxTokens = BoundOverride(cycle.MaxTokens, _options.MaxEstimatedTokensPerCycle, nameof(cycle.MaxTokens));
        var maxSteps = BoundOverride(cycle.MaxSteps, _options.MaxEstimatedStepsPerCycle, nameof(cycle.MaxSteps));
        if (actors.Count > 1_000_000) throw new ArgumentException("The actor signal batch exceeds its limit.", nameof(actors));

        var classified = new List<Candidate>(actors.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in actors)
        {
            Validate(actor, cycle.GameTick, ids);
            classified.Add(Classify(actor, cycle.GameTick));
        }

        var selected = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedTokens = 0;
        var usedSteps = 0;
        Select(LivingWorldActivationTiers.Foreground, _options.MaxForegroundActors);
        Select(LivingWorldActivationTiers.Nearby, _options.MaxNearbyActors);
        Select(LivingWorldActivationTiers.Background, _options.MaxBackgroundActors);

        var decisions = classified
            .OrderBy(static item => item.Signal.ActorId, StringComparer.Ordinal)
            .Select(item => CreateDecision(
                item,
                selected.TryGetValue(item.Signal.ActorId, out var admissionOrder) ? admissionOrder : null))
            .ToArray();
        return new LivingWorldPlan(cycle.WorldId, cycle.GameTick, decisions);

        void Select(string tier, int tierLimit)
        {
            var admitted = 0;
            foreach (var item in classified
                         .Where(value => value.EffectiveTier == tier && value.HasWork)
                         .OrderByDescending(static value => value.Priority)
                         .ThenBy(static value => value.Signal.ActorId, StringComparer.Ordinal))
            {
                if (admitted >= tierLimit || selected.Count >= maxRuns) break;
                if (item.Signal.EstimatedTokens > maxTokens - usedTokens
                    || item.Signal.EstimatedSteps > maxSteps - usedSteps)
                {
                    continue;
                }
                selected.Add(item.Signal.ActorId, selected.Count);
                admitted++;
                usedTokens += item.Signal.EstimatedTokens;
                usedSteps += item.Signal.EstimatedSteps;
            }
        }
    }

    private Candidate Classify(LivingWorldActorSignal signal, long gameTick)
    {
        var overdue = checked(gameTick - signal.LastEvaluatedGameTick);
        var hasWork = signal.HasDirectPlayerInput || signal.IsVisible || signal.IsInCombat
                      || signal.PendingMessages > 0 || signal.PendingTriggers > 0;
        string tier;
        if (signal.HasDirectPlayerInput || signal.IsVisible || signal.IsInCombat
            || signal.PendingMessages > 0
            || signal.DistanceToNearestPlayer <= _options.ForegroundDistance)
        {
            tier = LivingWorldActivationTiers.Foreground;
        }
        else if (signal.DistanceToNearestPlayer <= _options.NearbyDistance
                 || signal.Salience >= _options.NearbySalience)
        {
            tier = LivingWorldActivationTiers.Nearby;
        }
        else if (overdue < _options.DormantAfterGameTicks
                 || signal.Salience >= _options.BackgroundSalience)
        {
            tier = LivingWorldActivationTiers.Background;
        }
        else
        {
            tier = LivingWorldActivationTiers.Dormant;
        }

        var effectiveTier = tier;
        if (tier == LivingWorldActivationTiers.Background
            && overdue >= _options.StarvationAfterGameTicks)
        {
            effectiveTier = LivingWorldActivationTiers.Nearby;
        }
        var priority = signal.Salience * 1_000_000d
                       + Math.Min(overdue, 1_000_000)
                       + signal.PendingMessages * 10_000d
                       + signal.PendingTriggers * 100d;
        return new Candidate(signal, tier, effectiveTier, overdue, hasWork, priority);
    }

    private static LivingWorldDecision CreateDecision(Candidate candidate, int? admissionOrder)
    {
        if (admissionOrder.HasValue)
        {
            return new LivingWorldDecision(
                candidate.Signal.ActorId,
                candidate.Tier,
                LivingWorldDecisionKinds.RunNow,
                candidate.EffectiveTier == candidate.Tier ? "tier_admitted" : "starvation_promoted",
                candidate.Tier == LivingWorldActivationTiers.Foreground
                    ? ProviderWorkloadClasses.Interactive
                    : ProviderWorkloadClasses.Background,
                candidate.Overdue,
                checked(candidate.Signal.PendingMessages + candidate.Signal.PendingTriggers),
                admissionOrder);
        }
        var signalCount = checked(candidate.Signal.PendingMessages + candidate.Signal.PendingTriggers);
        if (candidate.Tier == LivingWorldActivationTiers.Dormant && signalCount > 0)
        {
            return new LivingWorldDecision(candidate.Signal.ActorId, candidate.Tier,
                LivingWorldDecisionKinds.Aggregate, "dormant_signals_aggregated",
                ProviderWorkloadClasses.Background, candidate.Overdue, signalCount, null);
        }
        if (candidate.HasWork)
        {
            return new LivingWorldDecision(candidate.Signal.ActorId, candidate.Tier,
                LivingWorldDecisionKinds.Coalesce, "cycle_budget_deferred",
                candidate.Tier == LivingWorldActivationTiers.Foreground
                    ? ProviderWorkloadClasses.Interactive
                    : ProviderWorkloadClasses.Background,
                candidate.Overdue, signalCount, null);
        }
        return new LivingWorldDecision(candidate.Signal.ActorId, candidate.Tier,
            LivingWorldDecisionKinds.Skip, "no_due_work",
            ProviderWorkloadClasses.Background, candidate.Overdue, 0, null);
    }

    private static void Validate(LivingWorldActorSignal actor, long gameTick, ISet<string> ids)
    {
        if (actor is null) throw new ArgumentNullException(nameof(actor));
        ValidateId(actor.ActorId, nameof(actor.ActorId));
        if (!ids.Add(actor.ActorId)) throw new ArgumentException("Actor IDs must be unique.", nameof(actor));
        if (actor.PendingMessages is < 0 or > 1_000_000
            || actor.PendingTriggers is < 0 or > 1_000_000
            || actor.EstimatedTokens < 1 || actor.EstimatedSteps < 1
            || actor.LastEvaluatedGameTick < 0 || actor.LastEvaluatedGameTick > gameTick)
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }
        if (double.IsNaN(actor.Salience) || double.IsInfinity(actor.Salience)
            || actor.Salience is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(actor.Salience));
        }
        if (actor.DistanceToNearestPlayer is { } distance
            && (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(actor.DistanceToNearestPlayer));
        }
    }

    private static int BoundOverride(int? requested, int configured, string name)
    {
        if (!requested.HasValue) return configured;
        if (requested.Value < 0 || requested.Value > configured) throw new ArgumentOutOfRangeException(name);
        return requested.Value;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new ArgumentException("A bounded ID is required.", name);
    }

    private sealed class Candidate
    {
        public Candidate(
            LivingWorldActorSignal signal,
            string tier,
            string effectiveTier,
            long overdue,
            bool hasWork,
            double priority)
        {
            Signal = signal;
            Tier = tier;
            EffectiveTier = effectiveTier;
            Overdue = overdue;
            HasWork = hasWork;
            Priority = priority;
        }

        public LivingWorldActorSignal Signal { get; }
        public string Tier { get; }
        public string EffectiveTier { get; }
        public long Overdue { get; }
        public bool HasWork { get; }
        public double Priority { get; }
    }
}
