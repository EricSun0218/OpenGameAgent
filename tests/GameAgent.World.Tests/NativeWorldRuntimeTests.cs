using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class NativeWorldRuntimeTests
{
    [Fact]
    public async Task InMemoryModeComposesOneBoundRuntime()
    {
        var package = Compile(Package());
        var initial = package.CreateInitialSnapshot("alternate", 2);
        var runtime = NativeWorldRuntime.CreateInMemoryFromSnapshot(
            package,
            initial);

        var snapshot = await runtime.ReadSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.IsType<InMemoryWorldAuthoritativeTransactionStore>(
            runtime.TransactionStore);
        Assert.Same(package, runtime.Package);
        Assert.Equal("world", runtime.Address.WorldId);
        Assert.Equal("alternate", runtime.Address.TimelineId);
        Assert.Equal(2, runtime.TimelineEpoch);
        Assert.Equal(package.CatalogDigest, snapshot!.Coordinate.CatalogDigest);
        Assert.NotNull(runtime.Planner);
        Assert.NotNull(runtime.AuthoritativeExecutor);
        Assert.NotNull(runtime.InteractiveWorld);
        Assert.NotNull(runtime.ClockRunner);

        var wrongEpoch = new WorldAdvanceClockCommand(
            "wrong-epoch-command",
            "wrong-epoch-operation",
            new WorldAuthoritativeCoordinate(
                "world",
                "alternate",
                timelineEpoch: 3,
                saveRevision: 0,
                stateVersion: 0,
                package.CatalogDigest),
            "turn",
            expectedClockTick: 0,
            ticks: 1);
        Assert.Throws<ArgumentException>(
            () => runtime.AdvanceClockAsync(wrongEpoch));
    }

    [Fact]
    public async Task FileModeRunsEveryLayerAndReplaysAfterReload()
    {
        var package = Compile(Package());
        var path = TemporaryStorePath();
        try
        {
            var runtime = await NativeWorldRuntime.CreateFileAsync(
                package,
                path);
            var initial = Assert.IsType<WorldAuthoritativeStateSnapshot>(
                await runtime.ReadSnapshotAsync());
            var actor = new GameEntityIdentity("actor", 1);
            var target = new GameEntityIdentity("target", 1);
            var query = await runtime.QueryInteractionsAsync(
                Query(initial, actor, target));

            Assert.True(query.Succeeded);
            var available = Assert.Single(query.Value!.Items);
            Assert.Equal(
                InteractionAvailabilityState.Available,
                available.State);

            var planned = await runtime.PlanInteractionAsync(
                Execution(
                    initial,
                    package.CatalogDigest,
                    actor,
                    target));

            Assert.True(planned.Succeeded);
            Assert.Single(planned.Value!.Plan.Plan.Instances);
            Assert.True(
                planned.Value.ExpectedCoordinate.IsExactMatch(
                    initial.Coordinate));

            var execution = await runtime.ExecuteInteractionAsync(
                planned.Value);

            Assert.True(execution.Succeeded);
            Assert.True(execution.Value!.Succeeded);
            Assert.Equal(1, execution.Value.Coordinate.SaveRevision);

            var advanceCommand = new WorldAdvanceClockCommand(
                "advance-command",
                "advance-operation",
                execution.Value.Coordinate,
                "turn",
                expectedClockTick: 0,
                ticks: 1);
            var advanced = await runtime.AdvanceClockAsync(advanceCommand);

            Assert.True(advanced.Succeeded);
            var committed = Assert.IsType<WorldAuthoritativeStateSnapshot>(
                await runtime.ReadSnapshotAsync());
            Assert.Equal(2, committed.Coordinate.SaveRevision);
            Assert.Equal(
                "1",
                committed.State.GetProperty("clocks")
                    .GetProperty("turn")
                    .GetProperty("tick")
                    .GetString());
            Assert.Equal(
                "12",
                committed.State.GetProperty("entities")
                    .GetProperty("actor")
                    .GetProperty("score")
                    .GetString());
            Assert.True(
                committed.State.GetProperty("entities")
                    .GetProperty("target")
                    .GetProperty("noticed")
                    .GetBoolean());

            var reloaded = await NativeWorldRuntime.CreateFileAsync(
                package,
                path);
            var beforeReplay =
                Assert.IsType<WorldAuthoritativeStateSnapshot>(
                    await reloaded.ReadSnapshotAsync());
            var interactionReplay =
                await reloaded.ExecuteInteractionAsync(planned.Value);
            var clockReplay =
                await reloaded.AdvanceClockAsync(advanceCommand);
            var afterReplay =
                Assert.IsType<WorldAuthoritativeStateSnapshot>(
                    await reloaded.ReadSnapshotAsync());

            Assert.True(interactionReplay.Succeeded);
            Assert.True(interactionReplay.Value!.Succeeded);
            Assert.All(
                interactionReplay.Value.Executions,
                item => Assert.Equal(
                    WorldTransactionExecutionStatus.Replayed,
                    item.Result.Status));
            Assert.True(clockReplay.Succeeded);
            Assert.All(
                clockReplay.TickResults,
                item => Assert.Equal(
                    WorldTransactionExecutionStatus.Replayed,
                    item.Execution.Status));
            Assert.Equal(beforeReplay.StateDigest, afterReplay.StateDigest);
            Assert.True(
                beforeReplay.Coordinate.IsExactMatch(
                    afterReplay.Coordinate));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileModeRejectsWrongWorldEpochOrCatalog()
    {
        var path = TemporaryStorePath();
        try
        {
            _ = await NativeWorldRuntime.CreateFileAsync(
                Compile(Package()),
                path);
            var changed = Compile(Package(interactionReward: "2"));

            var catalogException =
                await Assert.ThrowsAsync<ArgumentException>(
                    async () => await NativeWorldRuntime.CreateFileAsync(
                        changed,
                        path));
            var epochException =
                await Assert.ThrowsAsync<ArgumentException>(
                    async () => await NativeWorldRuntime.CreateFileAsync(
                        Compile(Package()),
                        path,
                        timelineEpoch: 1));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await NativeWorldRuntime.CreateFileAsync(
                    Compile(Package(worldId: "another-world")),
                    path));

            Assert.Equal("path", catalogException.ParamName);
            Assert.Equal("path", epochException.ParamName);
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileModeDispatchesLockingIoOffTheCallingThread()
    {
        var package = Compile(Package());
        var path = TemporaryStorePath();
        try
        {
            var runtime = await NativeWorldRuntime.CreateFileAsync(
                package,
                path);
            var storeOptions =
                new FileWorldAuthoritativeTransactionStoreOptions(
                    lockTimeout: TimeSpan.FromSeconds(2));
            ValueTask<NativeWorldRuntime> open;
            ValueTask<WorldAuthoritativeStateSnapshot?> read;
            using (new FileStream(
                       path + ".lock",
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                var stopwatch = Stopwatch.StartNew();
                open = NativeWorldRuntime.CreateFileAsync(
                    package,
                    path,
                    storeOptions: storeOptions);
                read = runtime.ReadSnapshotAsync();
                stopwatch.Stop();

                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                    "File-backed async APIs performed locking I/O on the "
                    + "calling thread.");
                Assert.False(open.IsCompleted);
                Assert.False(read.IsCompleted);
            }

            Assert.NotNull(
                await open.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(
                await read.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    private static InteractionQueryRequest Query(
        WorldAuthoritativeStateSnapshot snapshot,
        GameEntityIdentity actor,
        GameEntityIdentity target)
    {
        var coordinate = snapshot.Coordinate;
        return new InteractionQueryRequest(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(CultureInfo.InvariantCulture),
            actor,
            "local",
            new[] { target });
    }

    private static InteractionExecutionRequest Execution(
        WorldAuthoritativeStateSnapshot snapshot,
        string catalogDigest,
        GameEntityIdentity actor,
        GameEntityIdentity target)
    {
        var coordinate = snapshot.Coordinate;
        using var parameters = JsonDocument.Parse("{}");
        return new InteractionExecutionRequest(
            "interaction-command",
            "interaction-operation",
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(CultureInfo.InvariantCulture),
            catalogDigest,
            "mark-target",
            "1",
            actor,
            new[] { target },
            "local",
            parameters.RootElement);
    }

    private static ActivatedWorldPackage Compile(
        WorldPackageDefinition package)
    {
        var result = new NativeWorldPackageCompiler().Compile(package);
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(
                    item => item.Code
                            + " "
                            + item.Path
                            + " "
                            + item.Message)));
        return Assert.IsType<ActivatedWorldPackage>(result.Package);
    }

    private static WorldPackageDefinition Package(
        string interactionReward = "1",
        string worldId = "world")
    {
        return new WorldPackageDefinition(
            "runtime-test-package",
            "1",
            new[]
            {
                JsonFile(
                    "world.json",
                    $$"""
                    {
                      "contract": "game-agent.world-definition.v1",
                      "worldId": "{{worldId}}",
                      "defaultTimelineId": "main",
                      "entityStateRootPath": "/entities",
                      "relationshipRootPath": "/relationships",
                      "initialState": {
                        "entities": {
                          "actor": {
                            "tags": ["npc"],
                            "score": "10"
                          },
                          "target": {
                            "tags": []
                          }
                        },
                        "relationships": {}
                      },
                      "entityIncarnations": {
                        "actor": "1",
                        "target": "1"
                      }
                    }
                    """),
                JsonFile(
                    "clocks.json",
                    """
                    {
                      "contract": "game-agent.world-clocks.v1",
                      "clocks": [
                        {
                          "clockId": "turn",
                          "statePath": "/clocks/turn/tick",
                          "initialTick": "0"
                        }
                      ]
                    }
                    """),
                JsonFile(
                    "numerics.json",
                    """
                    {
                      "contract": "game-agent.world-numerics.v1",
                      "schemas": [
                        {
                          "schemaId": "score",
                          "scale": 0,
                          "unitId": "score-unit",
                          "minimum": "0",
                          "maximum": "100",
                          "defaultValue": "0"
                        }
                      ]
                    }
                    """),
                JsonFile(
                    "events.json",
                    """
                    {
                      "contract": "game-agent.world-events.v1",
                      "events": [
                        {
                          "definitionId": "increment",
                          "version": "1",
                          "priority": 0,
                          "trigger": {
                            "kind": "clock",
                            "clockId": "turn",
                            "everyTicks": "1"
                          },
                          "selector": {
                            "kind": "entity",
                            "entityId": "actor",
                            "incarnation": "1"
                          },
                          "condition": {"kind": "always"},
                          "effects": [
                            {
                              "kind": "numeric",
                              "effectId": "increment-score",
                              "entity": "subject",
                              "path": "/score",
                              "resourceKey": "actor:score",
                              "schemaId": "score",
                              "operation": "add",
                              "value": "1"
                            }
                          ]
                        }
                      ]
                    }
                    """),
                JsonFile(
                    "interactions.json",
                    $$"""
                    {
                      "contract": "game-agent.world-interactions.v1",
                      "interactions": [
                        {
                          "interactionId": "mark-target",
                          "version": "1",
                          "contentRevision": "1",
                          "priority": 0,
                          "parameterSchemaId": "mark-target.input",
                          "parameterSchemaVersion": "1",
                          "parameterSchema": {
                            "type": "object",
                            "properties": {},
                            "additionalProperties": false
                          },
                          "target": {
                            "schemaId": "entity",
                            "minimumTargets": 1,
                            "maximumTargets": 1
                          },
                          "channelIds": ["local"],
                          "tags": ["social"],
                          "requiredCapabilities": [],
                          "availability": {
                            "kind": "tag",
                            "tag": "npc"
                          },
                          "effects": [
                            {
                              "kind": "numeric",
                              "effectId": "reward-actor",
                              "entity": "subject",
                              "path": "/score",
                              "resourceKey": "actor:score",
                              "schemaId": "score",
                              "operation": "add",
                              "value": "{{interactionReward}}"
                            },
                            {
                              "kind": "set",
                              "effectId": "mark-target",
                              "entity": "target:0",
                              "path": "/noticed",
                              "resourceKey": "target:noticed",
                              "value": true
                            }
                          ],
                          "presentation": {"label": "Mark"}
                        }
                      ]
                    }
                    """)
            });
    }

    private static WorldPackageFile JsonFile(string path, string value)
    {
        return new WorldPackageFile(
            path,
            "application/json",
            Encoding.UTF8.GetBytes(value));
    }

    private static string TemporaryStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "game-agent-native-world-tests",
            Guid.NewGuid().ToString("N"),
            "world-store.json");
    }

    private static void DeleteTemporaryStore(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
