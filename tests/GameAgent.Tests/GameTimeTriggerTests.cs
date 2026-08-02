using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class GameTimeTriggerTests
{
    [Theory]
    [InlineData(GameTriggerCatchUpPolicies.All, 3, 0)]
    [InlineData(GameTriggerCatchUpPolicies.Once, 1, 2)]
    [InlineData(GameTriggerCatchUpPolicies.Skip, 0, 3)]
    [InlineData(GameTriggerCatchUpPolicies.Coalesce, 1, 0)]
    public async Task Missed_months_follow_declared_catch_up_policy(
        string policy,
        int expectedLaunches,
        int expectedSkipped)
    {
        var coordinator = new GameTriggerCoordinator(
            new InMemoryGameTriggerStateStore());
        var admission = await coordinator.AdmitAsync(
            Definition(policy, GameTriggerOverlapPolicies.Queue),
            Occurrences(1, 2, 3));

        Assert.Equal(expectedLaunches, admission.Launches.Count);
        Assert.Equal(expectedSkipped, admission.SkippedOccurrenceIds.Count);
        Assert.Equal(3, admission.State.LastOccurrenceSequence);
        if (policy == GameTriggerCatchUpPolicies.Coalesce)
        {
            Assert.Equal(3, admission.Launches[0].OccurrenceIds.Count);
        }
    }

    [Theory]
    [InlineData(GameTriggerOverlapPolicies.Skip, 0, 1, false)]
    [InlineData(GameTriggerOverlapPolicies.Coalesce, 0, 0, true)]
    [InlineData(GameTriggerOverlapPolicies.Queue, 1, 0, false)]
    [InlineData(GameTriggerOverlapPolicies.Replace, 1, 0, false)]
    public async Task Active_launches_follow_overlap_policy(
        string policy,
        int expectedNewLaunches,
        int expectedSkipped,
        bool expectedCoalesced)
    {
        var store = new InMemoryGameTriggerStateStore();
        var coordinator = new GameTriggerCoordinator(store);
        var definition = Definition(GameTriggerCatchUpPolicies.All, policy);
        var first = await coordinator.AdmitAsync(definition, Occurrences(1));
        var second = await coordinator.AdmitAsync(definition, Occurrences(2));

        Assert.Equal(expectedNewLaunches, second.Launches.Count);
        Assert.Equal(expectedSkipped, second.SkippedOccurrenceIds.Count);
        Assert.Equal(expectedCoalesced, second.CoalescedIntoLaunchId is not null);
        if (policy == GameTriggerOverlapPolicies.Replace)
        {
            Assert.Equal(first.Launches[0].LaunchId, second.CancellationLaunchIds[0]);
            Assert.Contains(
                second.State.Launches,
                launch => launch.LaunchId == first.Launches[0].LaunchId
                          && launch.State == GameTriggerLaunchStates.CancelRequested);
        }
    }

    [Fact]
    public async Task Replaying_same_occurrences_is_idempotent()
    {
        var coordinator = new GameTriggerCoordinator(
            new InMemoryGameTriggerStateStore());
        var definition = Definition(
            GameTriggerCatchUpPolicies.All,
            GameTriggerOverlapPolicies.Queue);
        var first = await coordinator.AdmitAsync(definition, Occurrences(1, 2));
        var replay = await coordinator.AdmitAsync(definition, Occurrences(1, 2));

        Assert.Equal(2, first.Launches.Count);
        Assert.Empty(replay.Launches);
        Assert.Equal(first.State.Revision, replay.State.Revision);
    }

    [Fact]
    public async Task Coalesce_never_mutates_input_already_consumed_by_a_running_launch()
    {
        var coordinator = new GameTriggerCoordinator(
            new InMemoryGameTriggerStateStore());
        var definition = Definition(
            GameTriggerCatchUpPolicies.All,
            GameTriggerOverlapPolicies.Coalesce);
        var first = await coordinator.AdmitAsync(definition, Occurrences(1));
        var running = await coordinator.RecordLaunchStateAsync(
            definition.TriggerId,
            definition.ScopeKey,
            first.Launches[0].LaunchId,
            GameTriggerLaunchStates.Running,
            first.Launches[0].Revision);

        var next = await coordinator.AdmitAsync(definition, Occurrences(2, 3));

        var successor = Assert.Single(next.Launches);
        Assert.Equal(new[] { "month-2", "month-3" }, successor.OccurrenceIds);
        Assert.Null(next.CoalescedIntoLaunchId);
        var persistedRunning = Assert.Single(
            next.State.Launches,
            item => item.LaunchId == running.LaunchId);
        Assert.Equal(new[] { "month-1" }, persistedRunning.OccurrenceIds);
    }

    [Fact]
    public async Task Concurrent_replay_creates_one_durable_launch()
    {
        var store = new InMemoryGameTriggerStateStore();
        var coordinator = new GameTriggerCoordinator(store);
        var definition = Definition(
            GameTriggerCatchUpPolicies.All,
            GameTriggerOverlapPolicies.Queue);

        var admissions = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            coordinator.AdmitAsync(definition, Occurrences(1)).AsTask()));
        var state = await store.TryGetAsync(admissions[0].State.StateKey, default);

        Assert.Equal(1, admissions.Sum(item => item.Launches.Count));
        Assert.Single(state!.Launches);
    }

    [Fact]
    public async Task State_keys_are_unambiguous_when_identifiers_contain_separators()
    {
        var coordinator = new GameTriggerCoordinator(
            new InMemoryGameTriggerStateStore());
        var left = Definition(
            GameTriggerCatchUpPolicies.All,
            GameTriggerOverlapPolicies.Queue);
        left.TriggerId = "a:b";
        left.ScopeKey = "c";
        var right = Definition(
            GameTriggerCatchUpPolicies.All,
            GameTriggerOverlapPolicies.Queue);
        right.TriggerId = "a";
        right.ScopeKey = "b:c";

        var leftAdmission = await coordinator.AdmitAsync(left, Occurrences(1));
        var rightAdmission = await coordinator.AdmitAsync(right, Occurrences(1));

        Assert.NotEqual(leftAdmission.State.StateKey, rightAdmission.State.StateKey);
        Assert.Single(leftAdmission.Launches);
        Assert.Single(rightAdmission.Launches);
    }

    [Fact]
    public async Task Retention_never_discards_non_terminal_launches()
    {
        var coordinator = new GameTriggerCoordinator(
            new InMemoryGameTriggerStateStore());
        var definition = Definition(
            GameTriggerCatchUpPolicies.All,
            GameTriggerOverlapPolicies.Queue);
        definition.MaxRetainedLaunches = 1;

        var exception = await Assert.ThrowsAsync<GameTriggerException>(async () =>
            await coordinator.AdmitAsync(definition, Occurrences(1, 2)));

        Assert.Equal("game_trigger_active_launch_limit", exception.ReasonCode);
    }

    [Fact]
    public async Task Terminal_launch_state_is_idempotent_but_cannot_reopen()
    {
        var coordinator = new GameTriggerCoordinator(
            new InMemoryGameTriggerStateStore());
        var admission = await coordinator.AdmitAsync(
            Definition(GameTriggerCatchUpPolicies.All, GameTriggerOverlapPolicies.Queue),
            Occurrences(1));
        var launch = Assert.Single(admission.Launches);
        var completed = await coordinator.RecordLaunchStateAsync(
            launch.TriggerId,
            launch.ScopeKey,
            launch.LaunchId,
            GameTriggerLaunchStates.Completed,
            launch.Revision);
        var replay = await coordinator.RecordLaunchStateAsync(
            launch.TriggerId,
            launch.ScopeKey,
            launch.LaunchId,
            GameTriggerLaunchStates.Completed,
            launch.Revision);

        Assert.Equal(completed.Revision, replay.Revision);
        var exception = await Assert.ThrowsAsync<GameTriggerException>(async () =>
            await coordinator.RecordLaunchStateAsync(
                launch.TriggerId,
                launch.ScopeKey,
                launch.LaunchId,
                GameTriggerLaunchStates.Running,
                completed.Revision));
        Assert.Equal("game_trigger_transition_invalid", exception.ReasonCode);
    }

    [Fact]
    public async Task Timeline_fork_cannot_share_a_trigger_coordinate()
    {
        var coordinator = new GameTriggerCoordinator(
            new InMemoryGameTriggerStateStore());
        var occurrences = Occurrences(1, 2).ToArray();
        occurrences[1].OccurredAt = new GameTimePoint("month", "fork", 0, 2);

        var exception = await Assert.ThrowsAsync<GameTriggerException>(
            async () => await coordinator.AdmitAsync(
                Definition(
                    GameTriggerCatchUpPolicies.All,
                    GameTriggerOverlapPolicies.Queue),
                occurrences));
        Assert.Equal("game_trigger_time_incompatible", exception.ReasonCode);
    }

    private static GameTriggerDefinition Definition(
        string catchUp,
        string overlap) => new()
        {
            TriggerId = "month-elapsed",
            ScopeKey = "world-1:npc-1",
            ActorId = "npc-1",
            CatchUpPolicy = catchUp,
            OverlapPolicy = overlap,
            MaxCatchUpOccurrences = 16,
            MaxRetainedLaunches = 32
        };

    private static IEnumerable<GameTriggerOccurrence> Occurrences(
        params long[] sequences) => sequences.Select(sequence =>
        new GameTriggerOccurrence
        {
            OccurrenceId = "month-" + sequence,
            Sequence = sequence,
            OccurredAt = new GameTimePoint("month", "main", 0, sequence),
            Payload = Json("{\"month\":" + sequence + ",\"growth\":1.5}")
        });

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
