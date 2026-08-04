using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class DurableRunRequestSnapshotterTests
{
    [Fact]
    public void BackendBoundaryPreservesSemanticValidationAuthority()
    {
        var source = new AgentRun
        {
            RunId = string.Empty,
            AgentId = string.Empty,
            WorldId = string.Empty,
            Trigger = null!,
            TriggerObservationIds = null!,
            State = "backend-defined",
            RuntimeGeneration = 0,
            Budget = null!,
            Usage = null!,
            PendingOperationIds = null!
        };

        var snapshot = DurableRunRequestSnapshotter
            .SnapshotForBackendBoundary(source, CancellationToken.None);

        Assert.NotSame(source, snapshot);
        Assert.Equal("backend-defined", snapshot.State);
        Assert.Null(snapshot.Trigger);
        Assert.Null(snapshot.TriggerObservationIds);
        Assert.Null(snapshot.Budget);
        Assert.Null(snapshot.Usage);
        Assert.Null(snapshot.PendingOperationIds);
        Assert.Throws<JsonException>(
            () => DurableRunRequestSnapshotter.Snapshot(
                source,
                CancellationToken.None));
    }

    [Fact]
    public void BackendBoundaryOwnsNestedMutableState()
    {
        var source = new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            TriggerObservationIds = new List<string> { "observation-1" },
            PendingOperationIds = new List<string> { "operation-1" },
            Extensions = new Dictionary<string, JsonElement>
            {
                ["coordinate"] = ProtocolJson.ParseElement(
                    """{"month":7}""")
            }
        };

        var snapshot = DurableRunRequestSnapshotter
            .SnapshotForBackendBoundary(source, CancellationToken.None);

        source.TriggerObservationIds[0] = "mutated-observation";
        source.PendingOperationIds[0] = "mutated-operation";
        source.Extensions["coordinate"] = ProtocolJson.ParseElement(
            """{"month":99}""");
        source.Trigger.Type = "mutated-trigger";
        source.Budget.MaxTurns = 99;
        source.Usage.Turns = 99;

        Assert.Equal("observation-1", snapshot.TriggerObservationIds[0]);
        Assert.Equal("operation-1", snapshot.PendingOperationIds[0]);
        Assert.Equal(
            7,
            snapshot.Extensions["coordinate"]
                .GetProperty("month")
                .GetInt32());
        Assert.NotEqual("mutated-trigger", snapshot.Trigger.Type);
        Assert.NotEqual(99, snapshot.Budget.MaxTurns);
        Assert.NotEqual(99, snapshot.Usage.Turns);
    }

    [Fact]
    public void BackendBoundaryRejectsOversizedRunCollectionBeforeEncoding()
    {
        var source = new AgentRun
        {
            PendingOperationIds = Enumerable.Range(0, 2_049)
                .Select(index => $"operation-{index}")
                .ToList()
        };

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                source,
                CancellationToken.None));

        Assert.Equal("agent_run_items_exceeded", error.LimitCode);
    }

    [Fact]
    public void BackendBoundaryRejectsInvalidUnicodeBeforeEncoding()
    {
        var source = new AgentRun
        {
            RunId = "run-\ud800"
        };

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                source,
                CancellationToken.None));

        Assert.Equal("invalid_unicode", error.LimitCode);
    }
}
