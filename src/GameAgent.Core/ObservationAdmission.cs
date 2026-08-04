using System.Collections.ObjectModel;
using GameAgent.Protocol;

namespace GameAgent.Core;

public static class ObservationAdmissionReasonCodes
{
    public const string AudienceIncarnationMissing =
        "observation_audience_incarnation_missing";

    public const string AudienceIncarnationMismatch =
        "observation_audience_incarnation_mismatch";

    public const string AudienceIncarnationInvalid =
        "observation_audience_incarnation_invalid";
}

public sealed class ObservationAdmissionException : ArgumentException
{
    internal ObservationAdmissionException(
        string observationId,
        string reasonCode,
        string message)
        : base(message, "observation")
    {
        ObservationId = observationId;
        ReasonCode = reasonCode;
    }

    public string ObservationId { get; }

    public string ReasonCode { get; }
}

/// <summary>
/// Applies world, session, and audience boundaries before an observation is
/// flattened into provider-facing context.
/// </summary>
public static class ObservationAdmission
{
    public static void EnsureVisibleToRun(
        ObservationEnvelope observation,
        AgentRun run)
    {
        EnsureVisibleToRun(
            observation,
            run,
            requireAudienceIncarnation: false);
    }

    public static void EnsureVisibleToRun(
        ObservationEnvelope observation,
        AgentRun run,
        bool requireAudienceIncarnation)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        var coordinate = requireAudienceIncarnation
            ? GameContextEnvelope.ValidateForRun(run, nameof(run))
            : null;
        EnsureVisibleToRun(
            observation,
            RuntimeGuard.RequiredId(run.RunId, nameof(run)),
            RuntimeGuard.RequiredId(run.AgentId, nameof(run)),
            RuntimeGuard.RequiredId(run.WorldId, nameof(run)),
            run.SessionId,
            coordinate?.Observer,
            requireAudienceIncarnation);
    }

    internal static void EnsureVisibleToRun(
        ObservationEnvelope observation,
        string runId,
        string agentId,
        string worldId,
        string? sessionId)
    {
        EnsureVisibleToRun(
            observation,
            runId,
            agentId,
            worldId,
            sessionId,
            observer: null,
            requireAudienceIncarnation: false);
    }

    internal static void EnsureVisibleToRun(
        ObservationEnvelope observation,
        string runId,
        string agentId,
        string worldId,
        string? sessionId,
        GameEntityIdentity? observer,
        bool requireAudienceIncarnation)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        _ = RuntimeGuard.RequiredId(runId, nameof(runId));
        agentId = RuntimeGuard.RequiredId(agentId, nameof(agentId));
        worldId = RuntimeGuard.RequiredId(worldId, nameof(worldId));
        ProtocolValidator.EnsureValid(observation);
        var binding = requireAudienceIncarnation
            ? ObservationAudienceIncarnations.ReadForAdmission(observation)
            : AudienceIncarnationReadResult.Missing;
        EnsureVisibleToRun(
            observation.ObservationId,
            observation.WorldId,
            observation.SessionId,
            observation.Visibility.Scope,
            observation.Visibility.AudienceIds,
            binding.State,
            binding.Bindings,
            agentId,
            worldId,
            sessionId,
            observer,
            requireAudienceIncarnation);
    }

    internal static ObservationAdmissionSnapshot Snapshot(
        ObservationEnvelope observation,
        string admittedAgentId)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        ProtocolValidator.EnsureValid(observation);
        admittedAgentId = RuntimeGuard.RequiredId(
            admittedAgentId,
            nameof(admittedAgentId));
        return ObservationAdmissionSnapshot.Capture(
            observation,
            admittedAgentId);
    }

    internal static void EnsureVisibleToRun(
        ObservationAdmissionSnapshot observation,
        AgentRun run,
        bool requireAudienceIncarnation)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        var coordinate = requireAudienceIncarnation
            ? GameContextEnvelope.ValidateForRun(run, nameof(run))
            : null;
        EnsureVisibleToRun(
            observation.ObservationId,
            observation.WorldId,
            observation.SessionId,
            observation.Scope,
            observation.AudienceIds,
            observation.BindingState,
            observation.Bindings,
            RuntimeGuard.RequiredId(run.AgentId, nameof(run)),
            RuntimeGuard.RequiredId(run.WorldId, nameof(run)),
            run.SessionId,
            coordinate?.Observer,
            requireAudienceIncarnation);
    }

    private static void EnsureVisibleToRun(
        string observationId,
        string observationWorldId,
        string? observationSessionId,
        string visibilityScope,
        IReadOnlyList<string> audienceIds,
        AudienceIncarnationBindingState bindingState,
        IReadOnlyList<ObservationAudienceIncarnationBinding> bindings,
        string agentId,
        string worldId,
        string? sessionId,
        GameEntityIdentity? observer,
        bool requireAudienceIncarnation)
    {
        if (!string.Equals(
                observationWorldId,
                worldId,
                StringComparison.Ordinal))
        {
            Deny(
                observationId,
                "observation_world_mismatch",
                "The observation belongs to a different world.");
        }

        if (observationSessionId is not null
            && !string.Equals(
                observationSessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            Deny(
                observationId,
                "observation_session_mismatch",
                "The observation belongs to a different session.");
        }

        var publicWorld = IsPublicWorld(visibilityScope, audienceIds);
        if (publicWorld)
        {
            return;
        }

        if (audienceIds.Count == 0
            || !audienceIds.Contains(agentId, StringComparer.Ordinal))
        {
            Deny(
                observationId,
                "observation_audience_mismatch",
                "The run agent is not in the observation audience.");
        }

        if (string.Equals(
                visibilityScope,
                ObservationVisibilityScopes.Private,
                StringComparison.Ordinal)
            && audienceIds.Count != 1)
        {
            Deny(
                observationId,
                "observation_private_audience_invalid",
                "A private observation must identify exactly one audience.");
        }

        if (!requireAudienceIncarnation)
        {
            return;
        }

        if (observer is null
            || bindingState == AudienceIncarnationBindingState.Missing)
        {
            Deny(
                observationId,
                ObservationAdmissionReasonCodes
                    .AudienceIncarnationMissing,
                "A restricted observation requires an audience incarnation binding and a run observer.");
        }

        if (bindingState != AudienceIncarnationBindingState.Valid)
        {
            Deny(
                observationId,
                ObservationAdmissionReasonCodes
                    .AudienceIncarnationInvalid,
                "The observation audience incarnation binding is malformed or incomplete.");
        }

        var binding = bindings.FirstOrDefault(
            item => string.Equals(
                item.AudienceId,
                agentId,
                StringComparison.Ordinal));
        if (binding is null)
        {
            Deny(
                observationId,
                ObservationAdmissionReasonCodes
                    .AudienceIncarnationMissing,
                "The run audience has no entity-incarnation binding.");
        }

        if (!observer!.IsSameIncarnation(binding!.Entity))
        {
            Deny(
                observationId,
                ObservationAdmissionReasonCodes
                    .AudienceIncarnationMismatch,
                "The observation audience belongs to a different entity incarnation.");
        }
    }

    private static bool IsPublicWorld(
        string scope,
        IReadOnlyCollection<string> audienceIds) =>
        string.Equals(
            scope,
            ObservationVisibilityScopes.World,
            StringComparison.Ordinal)
        && audienceIds.Count == 0;

    private static void Deny(
        string observationId,
        string reasonCode,
        string message)
    {
        throw new ObservationAdmissionException(
            observationId,
            reasonCode,
            message);
    }
}

internal sealed class ObservationAdmissionSnapshot
{
    public ObservationAdmissionSnapshot(
        string observationId,
        string worldId,
        string? sessionId,
        string scope,
        IEnumerable<string> audienceIds,
        AudienceIncarnationBindingState bindingState,
        IEnumerable<ObservationAudienceIncarnationBinding> bindings)
    {
        ObservationId = RuntimeGuard.RequiredId(
            observationId,
            nameof(observationId));
        WorldId = RuntimeGuard.RequiredId(worldId, nameof(worldId));
        SessionId = sessionId is null
            ? null
            : RuntimeGuard.RequiredId(sessionId, nameof(sessionId));
        Scope = RuntimeGuard.RequiredUtf8(scope, 64, nameof(scope));
        if (!string.Equals(
                Scope,
                ObservationVisibilityScopes.World,
                StringComparison.Ordinal)
            && !string.Equals(
                Scope,
                ObservationVisibilityScopes.Group,
                StringComparison.Ordinal)
            && !string.Equals(
                Scope,
                ObservationVisibilityScopes.Agent,
                StringComparison.Ordinal)
            && !string.Equals(
                Scope,
                ObservationVisibilityScopes.Private,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Observation visibility scope is unsupported.",
                nameof(scope));
        }

        AudienceIds = new ReadOnlyCollection<string>(
            RuntimeInputGuard.CopyBounded(
                    audienceIds,
                    ObservationAudienceIncarnations.MaxBindings,
                    id => RuntimeGuard.RequiredId(id, nameof(audienceIds)),
                    nameof(audienceIds),
                    "observation_audience_count_exceeded")
                .ToArray());
        if (AudienceIds.Distinct(StringComparer.Ordinal).Count()
            != AudienceIds.Count)
        {
            throw new ArgumentException(
                "Observation audience IDs must be unique.",
                nameof(audienceIds));
        }

        BindingState = bindingState;
        Bindings = new ReadOnlyCollection<
            ObservationAudienceIncarnationBinding>(
                RuntimeInputGuard.CopyBounded(
                        bindings,
                        ObservationAudienceIncarnations.MaxBindings,
                        binding => binding
                                   ?? throw new ArgumentException(
                                       "Audience incarnation bindings cannot contain null entries.",
                                       nameof(bindings)),
                        nameof(bindings),
                        "observation_audience_incarnation_count_exceeded")
                    .ToArray());
        if (BindingState != AudienceIncarnationBindingState.Valid
            && Bindings.Count != 0)
        {
            throw new ArgumentException(
                "Only valid audience incarnation metadata may contain bindings.",
                nameof(bindings));
        }

        if (BindingState == AudienceIncarnationBindingState.Valid)
        {
            var bindingIds = Bindings
                .Select(binding => binding.AudienceId)
                .ToHashSet(StringComparer.Ordinal);
            if (bindingIds.Count != Bindings.Count
                || bindingIds.Count != AudienceIds.Count
                || AudienceIds.Any(id => !bindingIds.Contains(id)))
            {
                throw new ArgumentException(
                    "Valid audience incarnation bindings must exactly cover the observation audience.",
                    nameof(bindings));
            }
        }
    }

    public string ObservationId { get; }

    public string WorldId { get; }

    public string? SessionId { get; }

    public string Scope { get; }

    public IReadOnlyList<string> AudienceIds { get; }

    public AudienceIncarnationBindingState BindingState { get; }

    public IReadOnlyList<ObservationAudienceIncarnationBinding> Bindings
    {
        get;
    }

    public static ObservationAdmissionSnapshot Capture(
        ObservationEnvelope observation,
        string admittedAgentId)
    {
        var binding =
            ObservationAudienceIncarnations.ReadForAdmission(observation);
        var publicWorld = string.Equals(
                              observation.Visibility.Scope,
                              ObservationVisibilityScopes.World,
                              StringComparison.Ordinal)
                          && observation.Visibility.AudienceIds.Count == 0;
        var audience = publicWorld
            ? Array.Empty<string>()
            : new[] { admittedAgentId };
        var admittedBindings =
            binding.State == AudienceIncarnationBindingState.Valid
                ? binding.Bindings
                    .Where(
                        item => string.Equals(
                            item.AudienceId,
                            admittedAgentId,
                            StringComparison.Ordinal))
                    .ToArray()
                : Array.Empty<
                    ObservationAudienceIncarnationBinding>();
        return new ObservationAdmissionSnapshot(
            observation.ObservationId,
            observation.WorldId,
            observation.SessionId,
            observation.Visibility.Scope,
            audience,
            binding.State,
            admittedBindings);
    }
}
