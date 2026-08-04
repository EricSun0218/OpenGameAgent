using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ObservationAdmissionIncarnationTests
{
    [Fact]
    public void RequiredBindingMatchesObserverThroughAudienceId()
    {
        var run = Run(new GameEntityIdentity("npc-17", 4));
        var observation = RestrictedObservation();
        ObservationAudienceIncarnations.Attach(
            observation,
            new[]
            {
                new ObservationAudienceIncarnationBinding(
                    run.AgentId,
                    new GameEntityIdentity("npc-17", 4))
            });

        ObservationAdmission.EnsureVisibleToRun(
            observation,
            run,
            requireAudienceIncarnation: true);

        Assert.True(
            ObservationAudienceIncarnations.TryRead(
                observation,
                out var bindings));
        var binding = Assert.Single(bindings);
        Assert.Equal("agent-1", binding.AudienceId);
        Assert.True(
            run.Extensions.Count == 1
            && new GameEntityIdentity("npc-17", 4)
                .IsSameIncarnation(binding.Entity));
    }

    [Fact]
    public void MissingAndMismatchedIncarnationsHaveStableReasons()
    {
        var run = Run(new GameEntityIdentity("npc-17", 4));
        var missing = RestrictedObservation();

        var missingError = Assert.Throws<ObservationAdmissionException>(
            () => ObservationAdmission.EnsureVisibleToRun(
                missing,
                run,
                requireAudienceIncarnation: true));

        var mismatch = RestrictedObservation();
        ObservationAudienceIncarnations.Attach(
            mismatch,
            new[]
            {
                new ObservationAudienceIncarnationBinding(
                    run.AgentId,
                    new GameEntityIdentity("npc-17", 3))
            });
        var mismatchError = Assert.Throws<ObservationAdmissionException>(
            () => ObservationAdmission.EnsureVisibleToRun(
                mismatch,
                run,
                requireAudienceIncarnation: true));

        Assert.Equal(
            ObservationAdmissionReasonCodes.AudienceIncarnationMissing,
            missingError.ReasonCode);
        Assert.Equal(
            ObservationAdmissionReasonCodes.AudienceIncarnationMismatch,
            mismatchError.ReasonCode);
    }

    [Fact]
    public void MalformedBindingFailsClosedOnlyWhenStrictModeIsEnabled()
    {
        var run = Run(new GameEntityIdentity("npc-17", 4));
        var observation = RestrictedObservation();
        observation.Extensions[
            ObservationAudienceIncarnations.ExtensionName] =
            ProtocolJson.ParseElement(
                """
                [
                  {
                    "audienceId":"agent-1",
                    "entityId":"npc-17",
                    "incarnation":4,
                    "unexpected":true
                  }
                ]
                """);

        ObservationAdmission.EnsureVisibleToRun(observation, run);
        var error = Assert.Throws<ObservationAdmissionException>(
            () => ObservationAdmission.EnsureVisibleToRun(
                observation,
                run,
                requireAudienceIncarnation: true));

        Assert.Equal(
            ObservationAdmissionReasonCodes.AudienceIncarnationInvalid,
            error.ReasonCode);
    }

    [Fact]
    public void PublicWorldObservationDoesNotRequireObserverOrBinding()
    {
        var run = new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1"
        };
        var observation = RestrictedObservation();
        observation.SessionId = null;
        observation.Visibility = new VisibilityRule
        {
            Scope = ObservationVisibilityScopes.World
        };

        ObservationAdmission.EnsureVisibleToRun(
            observation,
            run,
            requireAudienceIncarnation: true);
    }

    [Fact]
    public void CandidateCloneAndDurableCodecRetainAdmissionBinding()
    {
        var originalRun = Run(new GameEntityIdentity("npc-17", 4));
        var observation = RestrictedObservation();
        ObservationAudienceIncarnations.Attach(
            observation,
            new[]
            {
                new ObservationAudienceIncarnationBinding(
                    originalRun.AgentId,
                    new GameEntityIdentity("npc-17", 4))
            });
        var candidate = ContextCandidate.FromObservation(
            observation,
            originalRun,
            required: true,
            canDefer: false);
        var recovered = Assert.Single(
            DurableRunInputJournalCodec.Decode(
                    DurableRunInputJournalCodec.Encode(
                        new[] { candidate.Clone() },
                        Array.Empty<SkillReference>()))
                .Context);
        var reusedRun = Run(new GameEntityIdentity("npc-17", 5));

        var error = Assert.Throws<ObservationAdmissionException>(
            () => ObservationAdmission.EnsureVisibleToRun(
                recovered.ObservationAdmissionMetadata!,
                reusedRun,
                requireAudienceIncarnation: true));

        Assert.Equal(
            ObservationAdmissionReasonCodes.AudienceIncarnationMismatch,
            error.ReasonCode);
    }

    [Fact]
    public void BindingAttachmentBoundsUntrustedEnumeration()
    {
        var observation = RestrictedObservation();
        var binding = new ObservationAudienceIncarnationBinding(
            "agent-1",
            new GameEntityIdentity("npc-17", 4));

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => ObservationAudienceIncarnations.Attach(
                observation,
                InfiniteBindings(binding)));

        Assert.Equal(
            "observation_audience_incarnation_count_exceeded",
            error.LimitCode);
        Assert.False(
            observation.Extensions.ContainsKey(
                ObservationAudienceIncarnations.ExtensionName));
    }

    [Fact]
    public void BindingAttachmentReservesExtensionCapacityAtomically()
    {
        var observation = RestrictedObservation();
        for (var index = 0;
             index < ProtocolLimits.MaxProtocolExtensions;
             index++)
        {
            observation.Extensions["extension-" + index] =
                ProtocolJson.ParseElement("true");
        }

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => ObservationAudienceIncarnations.Attach(
                observation,
                new[]
                {
                    new ObservationAudienceIncarnationBinding(
                        "agent-1",
                        new GameEntityIdentity("npc-17", 4))
                }));

        Assert.Equal("observation_extensions_exceeded", error.LimitCode);
        Assert.Equal(
            ProtocolLimits.MaxProtocolExtensions,
            observation.Extensions.Count);
        Assert.False(
            observation.Extensions.ContainsKey(
                ObservationAudienceIncarnations.ExtensionName));
    }

    [Fact]
    public void BindingAttachmentRollsBackAProtocolOversizeResult()
    {
        var observation = RestrictedObservation();
        observation.Visibility.AudienceIds = Enumerable.Range(0, 2_048)
            .Select(index => "agent-" + index)
            .ToList();
        var bindings = observation.Visibility.AudienceIds
            .Select(
                audienceId =>
                    new ObservationAudienceIncarnationBinding(
                        audienceId,
                        new GameEntityIdentity(audienceId, 1)))
            .ToArray();

        Assert.Throws<JsonException>(
            () => ObservationAudienceIncarnations.Attach(
                observation,
                bindings));

        Assert.False(
            observation.Extensions.ContainsKey(
                ObservationAudienceIncarnations.ExtensionName));
    }

    private static AgentRun Run(GameEntityIdentity observer)
    {
        var run = new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1"
        };
        GameContextEnvelope.Attach(
            run,
            new GameContextCoordinate(
                run.WorldId,
                "prime",
                saveRevision: 1,
                observer));
        return run;
    }

    private static ObservationEnvelope RestrictedObservation()
    {
        return new ObservationEnvelope
        {
            ObservationId = "observation-1",
            WorldId = "world-1",
            SessionId = "session-1",
            Source = "game",
            Kind = ObservationKinds.Event,
            Payload = ProtocolJson.ParseElement("""{"event":"secret"}"""),
            ObservedAt = DateTimeOffset.UnixEpoch,
            Visibility = new VisibilityRule
            {
                Scope = ObservationVisibilityScopes.Private,
                AudienceIds = new List<string> { "agent-1" }
            }
        };
    }

    private static IEnumerable<ObservationAudienceIncarnationBinding>
        InfiniteBindings(
            ObservationAudienceIncarnationBinding binding)
    {
        while (true)
        {
            yield return binding;
        }
    }
}
