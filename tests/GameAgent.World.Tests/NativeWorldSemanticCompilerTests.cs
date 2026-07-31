using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class NativeWorldSemanticCompilerTests
{
    [Fact]
    public void NativeWorldRuntimeRunsWithReflectionSerializationDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);

        var package = Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction()));

        Assert.Equal(
            package.CatalogDigest,
            package.CreateInitialSnapshot().Coordinate.CatalogDigest);
    }

    [Fact]
    public void CompilesEverySemanticCatalogIntoOneBoundSnapshot()
    {
        var first = Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction(),
                includeContentCatalogs: true));
        var second = Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction(),
                includeContentCatalogs: true,
                reverseFiles: true));

        Assert.Equal(first.CatalogDigest, second.CatalogDigest);
        Assert.Equal("world", first.World.WorldId);
        Assert.Single(first.Clocks);
        Assert.Equal(2, first.NumericSchemas.Count);
        Assert.Single(first.Events);
        Assert.Single(first.InteractionCatalog.Definitions);
        Assert.Equal(64, first.InteractionsDigest.Length);
        Assert.Single(first.Agents.Entries);
        Assert.Single(first.Knowledge.Entries);
        Assert.Equal(
            first.CatalogDigest,
            first.InteractionCatalog.Digest);
        var snapshot = first.CreateInitialSnapshot();
        Assert.Equal(first.CatalogDigest, snapshot.Coordinate.CatalogDigest);
        Assert.Equal(
            "0",
            snapshot.State.GetProperty("clocks")
                .GetProperty("turn")
                .GetProperty("tick")
                .GetString());
    }

    [Fact]
    public void ReportsMissingWorldAndUnknownFieldsWithoutActivation()
    {
        var missing = new NativeWorldPackageCompiler().Compile(
            new WorldPackageDefinition(
                "package",
                "1",
                Array.Empty<WorldPackageFile>()));
        var unknown = new NativeWorldPackageCompiler().Compile(
            new WorldPackageDefinition(
                "package",
                "1",
                new[]
                {
                    JsonFile(
                        "world.json",
                        """
                        {
                          "contract": "game-agent.world-definition.v1",
                          "worldId": "world",
                          "defaultTimelineId": "main",
                          "initialState": {},
                          "unexpected": true
                        }
                        """)
                }));

        Assert.False(missing.Succeeded);
        Assert.Equal(
            NativeWorldSemanticReasonCodes.WorldDefinitionMissing,
            Assert.Single(missing.Diagnostics).Code);
        Assert.False(unknown.Succeeded);
        Assert.Equal(
            NativeWorldSemanticReasonCodes.UnknownField,
            Assert.Single(unknown.Diagnostics).Code);
    }

    [Fact]
    public void MissingNumericReferenceFailsClosedWithStructuredPath()
    {
        var package = Package(
            BasicState(),
            """
            {
              "contract": "game-agent.world-events.v1",
              "events": [
                {
                  "definitionId": "bad",
                  "version": "1",
                  "priority": 0,
                  "trigger": {
                    "kind": "clock",
                    "clockId": "turn",
                    "everyTicks": "1"
                  },
                  "selector": {"kind": "entity", "entityId": "actor"},
                  "condition": {"kind": "always"},
                  "effects": [
                    {
                      "kind": "numeric",
                      "effectId": "bad",
                      "entity": "subject",
                      "path": "/score",
                      "resourceKey": "actor:score",
                      "schemaId": "missing",
                      "operation": "add",
                      "value": "1"
                    }
                  ]
                }
              ]
            }
            """);

        var result = new NativeWorldPackageCompiler().Compile(package);

        Assert.False(result.Succeeded);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(
            NativeWorldSemanticReasonCodes.ReferenceMissing,
            diagnostic.Code);
        Assert.Contains("schemaId", diagnostic.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredExtensionMustBeExplicitlyApproved()
    {
        var source = Package(BasicState(), CounterEvents());
        var package = new WorldPackageDefinition(
            source.PackageId,
            source.ContentVersion,
            source.Files,
            new[]
            {
                new WorldPackageExtensionRequirement(
                    "example.rules",
                    "[1,2)")
            });

        var result = new NativeWorldPackageCompiler().Compile(package);

        Assert.False(result.Succeeded);
        Assert.Equal(
            NativeWorldSemanticReasonCodes.MissingExtension,
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void InteractionTargetReferencesCannotEscapeTheirContract()
    {
        var compiled = new NativeWorldPackageCompiler().Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction().Replace(
                    "\"target:0\"",
                    "\"target:1\"",
                    StringComparison.Ordinal)));

        Assert.False(compiled.Succeeded);
        Assert.Contains(
            compiled.Diagnostics,
            item => item.Code
                    == NativeWorldSemanticReasonCodes.InvalidEffect
                    && item.Path.EndsWith(
                        "/effects",
                        StringComparison.Ordinal));

        var optionalTarget = new NativeWorldPackageCompiler().Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction().Replace(
                    "\"minimumTargets\": 1",
                    "\"minimumTargets\": 0",
                    StringComparison.Ordinal)));
        Assert.False(optionalTarget.Succeeded);
        Assert.Contains(
            optionalTarget.Diagnostics,
            item => item.Code
                    == NativeWorldSemanticReasonCodes.InvalidEffect);
    }

    [Fact]
    public async Task DeclarativeConditionsAndEffectsCommitAtomically()
    {
        var package = Compile(
            Package(
                BasicState(),
                RichEvents()));
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            package.CreateInitialSnapshot());
        var runner = new WorldAdvanceClockRunner(package, store);

        var result = await runner.ExecuteAsync(
            Command(package, ticks: 1));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.CompletedTicks);
        var snapshot = await store.ReadAsync(
            new WorldTimelineAddress("world", "main"),
            default);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Coordinate.SaveRevision);
        Assert.Equal(1, snapshot.Coordinate.StateVersion);
        Assert.Equal(
            "1",
            snapshot.State.GetProperty("clocks")
                .GetProperty("turn")
                .GetProperty("tick")
                .GetString());
        var actor = snapshot.State.GetProperty("entities")
            .GetProperty("actor");
        Assert.Equal("11", actor.GetProperty("score").GetString());
        Assert.Equal("95", actor.GetProperty("balance").GetString());
        Assert.Equal("active", actor.GetProperty("status").GetString());
        Assert.True(actor.GetProperty("childApplied").GetBoolean());
        Assert.False(actor.TryGetProperty("obsolete", out _));
        Assert.Equal(
            "25",
            snapshot.State.GetProperty("entities")
                .GetProperty("target")
                .GetProperty("balance")
                .GetString());
        Assert.Equal(
            "active",
            snapshot.State.GetProperty("relationships")
                .GetProperty("actor")
                .GetProperty("1")
                .GetProperty("observes")
                .GetProperty("target")
                .GetProperty("1")
                .GetProperty("state")
                .GetString());
        var receipt = Assert.Single(result.TickResults).Execution.Receipt;
        Assert.NotNull(receipt);
        Assert.Equal(
            2,
            receipt!.Effect!.TypedResult!.Value
                .GetProperty("occurrenceIds")
                .GetArrayLength());
    }

    [Fact]
    public async Task MultiTickRetryReplaysWithoutDuplicatingEffects()
    {
        var package = Compile(
            Package(BasicState(), CounterEvents()));
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            package.CreateInitialSnapshot());
        var runner = new WorldAdvanceClockRunner(package, store);
        var command = Command(package, ticks: 3);

        var first = await runner.ExecuteAsync(command);
        var retry = await runner.ExecuteAsync(command);

        Assert.True(first.Succeeded);
        Assert.True(retry.Succeeded);
        Assert.All(
            retry.TickResults,
            item => Assert.Equal(
                WorldTransactionExecutionStatus.Replayed,
                item.Execution.Status));
        var snapshot = await store.ReadAsync(
            new WorldTimelineAddress("world", "main"),
            default);
        Assert.Equal(3, snapshot!.Coordinate.SaveRevision);
        Assert.Equal(
            "3",
            snapshot.State.GetProperty("clocks")
                .GetProperty("turn")
                .GetProperty("tick")
                .GetString());
        Assert.Equal(
            "13",
            snapshot.State.GetProperty("entities")
                .GetProperty("actor")
                .GetProperty("score")
                .GetString());
    }

    [Fact]
    public async Task RetryIdentityBindsTheWholeTickBatch()
    {
        var package = Compile(
            Package(BasicState(), CounterEvents()));
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            package.CreateInitialSnapshot());
        var runner = new WorldAdvanceClockRunner(package, store);

        var first = await runner.ExecuteAsync(Command(package, ticks: 1));
        var changedPayload = await runner.ExecuteAsync(
            Command(package, ticks: 2));

        Assert.True(first.Succeeded);
        Assert.Equal(
            WorldAdvanceClockStatus.IdempotencyConflict,
            changedPayload.Status);
        Assert.Equal(0, changedPayload.CompletedTicks);
        var snapshot = await store.ReadAsync(
            new WorldTimelineAddress("world", "main"),
            default);
        Assert.Equal(1, snapshot!.Coordinate.SaveRevision);
        Assert.Equal(
            "1",
            snapshot.State.GetProperty("clocks")
                .GetProperty("turn")
                .GetProperty("tick")
                .GetString());
    }

    [Fact]
    public async Task FailedLaterTickPreservesCommittedPrefix()
    {
        var package = Compile(
            Package(
                BasicState(),
                """
                {
                  "contract": "game-agent.world-events.v1",
                  "events": [
                    {
                      "definitionId": "consume",
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
                          "effectId": "consume",
                          "entity": "subject",
                          "path": "/score",
                          "resourceKey": "actor:score",
                          "schemaId": "score",
                          "operation": "consume",
                          "value": "6"
                        }
                      ]
                    }
                  ]
                }
                """));
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            package.CreateInitialSnapshot());

        var result = await new WorldAdvanceClockRunner(package, store)
            .ExecuteAsync(Command(package, ticks: 3));

        Assert.Equal(
            WorldAdvanceClockStatus.PartiallyCompleted,
            result.Status);
        Assert.Equal(1, result.CompletedTicks);
        var snapshot = await store.ReadAsync(
            new WorldTimelineAddress("world", "main"),
            default);
        Assert.Equal(1, snapshot!.Coordinate.SaveRevision);
        Assert.Equal(
            "1",
            snapshot.State.GetProperty("clocks")
                .GetProperty("turn")
                .GetProperty("tick")
                .GetString());
        Assert.Equal(
            "4",
            snapshot.State.GetProperty("entities")
                .GetProperty("actor")
                .GetProperty("score")
                .GetString());
    }

    [Fact]
    public async Task CancellationBeforeNextTickLeavesStateUntouched()
    {
        var package = Compile(
            Package(BasicState(), CounterEvents()));
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            package.CreateInitialSnapshot());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new WorldAdvanceClockRunner(package, store)
            .ExecuteAsync(
                Command(package, ticks: 3),
                cancellation.Token);

        Assert.Equal(WorldAdvanceClockStatus.Cancelled, result.Status);
        Assert.Equal(0, result.CompletedTicks);
        var snapshot = await store.ReadAsync(
            new WorldTimelineAddress("world", "main"),
            default);
        Assert.Equal(0, snapshot!.Coordinate.SaveRevision);
        Assert.Equal(
            "0",
            snapshot.State.GetProperty("clocks")
                .GetProperty("turn")
                .GetProperty("tick")
                .GetString());
    }

    [Fact]
    public async Task CancellationAfterCommitKeepsExactlyOneTickPrefix()
    {
        var package = Compile(
            Package(BasicState(), CounterEvents()));
        var inner = new InMemoryWorldAuthoritativeTransactionStore(
            package.CreateInitialSnapshot());
        using var cancellation = new CancellationTokenSource();
        var store = new CancelAfterFirstCommitStore(inner, cancellation);

        var result = await new WorldAdvanceClockRunner(package, store)
            .ExecuteAsync(
                Command(package, ticks: 3),
                cancellation.Token);

        Assert.Equal(WorldAdvanceClockStatus.Cancelled, result.Status);
        Assert.Equal(1, result.CompletedTicks);
        var snapshot = await inner.ReadAsync(
            new WorldTimelineAddress("world", "main"),
            default);
        Assert.Equal(1, snapshot!.Coordinate.SaveRevision);
        Assert.Equal(
            "1",
            snapshot.State.GetProperty("clocks")
                .GetProperty("turn")
                .GetProperty("tick")
                .GetString());
        Assert.Equal(
            "11",
            snapshot.State.GetProperty("entities")
                .GetProperty("actor")
                .GetProperty("score")
                .GetString());
    }

    [Fact]
    public async Task NativeInteractionQueriesPlansAndCommitsWithoutHostRules()
    {
        var package = Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction()));
        var initial = package.CreateInitialSnapshot();
        var store = new InMemoryWorldAuthoritativeTransactionStore(initial);
        var actor = new GameEntityIdentity("actor", 1);
        var target = new GameEntityIdentity("target", 1);
        var query = new InteractionQueryRequest(
            "world",
            "main",
            0,
            0,
            "0",
            actor,
            "local",
            new[] { target });
        var queryResult = await new InteractionQueryService().QueryAsync(
            package.InteractionCatalog,
            query,
            package.CreateInteractionAdmissionEvaluator(initial));

        var available = Assert.Single(queryResult.Items);
        Assert.Equal(InteractionAvailabilityState.Available, available.State);
        using var parameters = JsonDocument.Parse("{}");
        var execution = new InteractionExecutionRequest(
            "interaction-command",
            "interaction-idempotency",
            "world",
            "main",
            0,
            0,
            "0",
            package.CatalogDigest,
            "mark-target",
            "1",
            actor,
            new[] { target },
            "local",
            parameters.RootElement);
        var planner = new WorldEventPlanner(
            package.EventHandlers,
            store);
        var facade = new InteractiveWorldFacade(planner);
        var planned = await facade.PlanInteractionAsync(
            package.InteractionCatalog,
            execution,
            new WorldStateFence(
                "world",
                "main",
                0,
                0,
                "0",
                package.CatalogDigest),
            new NativeWorldPlanningContext(initial));

        Assert.True(planned.Succeeded);
        var authoritative = planned.Value!.Bind(initial.Coordinate);
        var committed = await new WorldAuthoritativeEventPlanExecutor(
                store,
                package.TransactionalEffects)
            .ExecuteAsync(
                new WorldEventPlanExecutionRequest(authoritative),
                default);

        Assert.True(committed.Succeeded);
        var snapshot = await store.ReadAsync(
            new WorldTimelineAddress("world", "main"),
            default);
        Assert.Equal(
            "11",
            snapshot!.State.GetProperty("entities")
                .GetProperty("actor")
                .GetProperty("score")
                .GetString());
        Assert.True(
            snapshot.State.GetProperty("entities")
                .GetProperty("target")
                .GetProperty("noticed")
                .GetBoolean());
    }

    [Fact]
    public async Task
        NativeInteractionRejectsStaleTargetDuringQueryAndPlanning()
    {
        var package = Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction()));
        var initial = package.CreateInitialSnapshot();
        var actor = new GameEntityIdentity("actor", 1);
        var staleTarget = new GameEntityIdentity("target", 2);
        var queryResult = await new InteractionQueryService().QueryAsync(
            package.InteractionCatalog,
            new InteractionQueryRequest(
                "world",
                "main",
                0,
                0,
                "0",
                actor,
                "local",
                new[] { staleTarget }),
            package.CreateInteractionAdmissionEvaluator(initial));

        var item = Assert.Single(queryResult.Items);
        Assert.Equal(
            InteractionAvailabilityState.Unavailable,
            item.State);
        Assert.Equal(InteractiveWorldReasonCodes.StaleState, item.ReasonCode);

        using var parameters = JsonDocument.Parse("{}");
        var planned = await new InteractiveWorldFacade(
                new WorldEventPlanner(
                    package.EventHandlers,
                    new InMemoryWorldEventHistory()))
            .PlanInteractionAsync(
                package.InteractionCatalog,
                new InteractionExecutionRequest(
                    "stale-interaction-command",
                    "stale-interaction-idempotency",
                    "world",
                    "main",
                    0,
                    0,
                    "0",
                    package.CatalogDigest,
                    "mark-target",
                    "1",
                    actor,
                    new[] { staleTarget },
                    "local",
                    parameters.RootElement),
                new WorldStateFence(
                    "world",
                    "main",
                    0,
                    0,
                    "0",
                    package.CatalogDigest),
                new NativeWorldPlanningContext(initial));

        Assert.True(planned.Succeeded);
        Assert.Empty(planned.Value!.Plan.Instances);
    }

    [Fact]
    public async Task NativeInteractionCannotSpoofAuthoritativeGameTime()
    {
        var package = Compile(
            Package(
                BasicState(),
                CounterEvents(),
                BasicInteraction()));
        var initial = package.CreateInitialSnapshot();
        using var parameters = JsonDocument.Parse("{}");
        var planned = await new InteractiveWorldFacade(
                new WorldEventPlanner(
                    package.EventHandlers,
                    new InMemoryWorldEventHistory()))
            .PlanInteractionAsync(
                package.InteractionCatalog,
                new InteractionExecutionRequest(
                    "future-interaction-command",
                    "future-interaction-idempotency",
                    "world",
                    "main",
                    0,
                    0,
                    "0",
                    package.CatalogDigest,
                    "mark-target",
                    "1",
                    new GameEntityIdentity("actor", 1),
                    new[] { new GameEntityIdentity("target", 1) },
                    "local",
                    parameters.RootElement,
                    gameTime: new GameTimePoint(
                        "turn",
                        "main",
                        0,
                        1)),
                new WorldStateFence(
                    "world",
                    "main",
                    0,
                    0,
                    "0",
                    package.CatalogDigest),
                new NativeWorldPlanningContext(initial));

        Assert.True(planned.Succeeded);
        Assert.Empty(planned.Value!.Plan.Instances);
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

    private static WorldAdvanceClockCommand Command(
        ActivatedWorldPackage package,
        int ticks)
    {
        return new WorldAdvanceClockCommand(
            "advance-command",
            "advance-operation",
            package.CreateInitialSnapshot().Coordinate,
            "turn",
            expectedClockTick: 0,
            ticks);
    }

    private static WorldPackageDefinition Package(
        string state,
        string? events = null,
        string? interactions = null,
        bool includeContentCatalogs = false,
        bool reverseFiles = false)
    {
        var files = new List<WorldPackageFile>
        {
            JsonFile(
                "world.json",
                $$"""
                {
                  "contract": "game-agent.world-definition.v1",
                  "worldId": "world",
                  "defaultTimelineId": "main",
                  "entityStateRootPath": "/entities",
                  "relationshipRootPath": "/relationships",
                  "initialState": {{state}},
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
                      "schemaId": "balance",
                      "scale": 0,
                      "unitId": "balance-unit",
                      "minimum": "0",
                      "maximum": "1000",
                      "defaultValue": "0"
                    },
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
                """)
        };
        if (events is not null)
        {
            files.Add(JsonFile("events.json", events));
        }

        if (interactions is not null)
        {
            files.Add(JsonFile("interactions.json", interactions));
        }

        if (includeContentCatalogs)
        {
            files.Add(
                JsonFile(
                    "agents.json",
                    """
                    {
                      "contract": "game-agent.world-agents.v1",
                      "agents": [
                        {
                          "id": "actor-profile",
                          "version": "1",
                          "data": {"persona": "data-only"}
                        }
                      ]
                    }
                    """));
            files.Add(
                JsonFile(
                    "knowledge.json",
                    """
                    {
                      "contract": "game-agent.world-knowledge.v1",
                      "knowledge": [
                        {
                          "id": "setting",
                          "version": "1",
                          "data": {"content": "data-only"}
                        }
                      ]
                    }
                    """));
        }

        if (reverseFiles)
        {
            files.Reverse();
        }

        return new WorldPackageDefinition("package", "1", files);
    }

    private static string BasicState()
    {
        return
            """
            {
              "entities": {
                "actor": {
                  "tags": ["npc"],
                  "score": "10",
                  "balance": "100",
                  "obsolete": "yes"
                },
                "target": {
                  "tags": [],
                  "balance": "20"
                }
              },
              "relationships": {}
            }
            """;
    }

    private static string CounterEvents()
    {
        return
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
            """;
    }

    private static string RichEvents()
    {
        return
            """
            {
              "contract": "game-agent.world-events.v1",
              "events": [
                {
                  "definitionId": "boundary",
                  "version": "1",
                  "priority": 10,
                  "trigger": {
                    "kind": "clock",
                    "clockId": "turn",
                    "everyTicks": "100",
                    "offsetTicks": "1"
                  },
                  "selector": {
                    "kind": "entities_by_tag",
                    "tag": "npc",
                    "maxCandidates": 8
                  },
                  "condition": {
                    "kind": "all",
                    "conditions": [
                      {
                        "kind": "fixed_point",
                        "source": "subject",
                        "path": "/score",
                        "schemaId": "score",
                        "operator": "gte",
                        "value": "10"
                      },
                      {
                        "kind": "clock",
                        "clockId": "turn",
                        "operator": "gte",
                        "tick": "1"
                      },
                      {
                        "kind": "path",
                        "source": "subject",
                        "path": "/obsolete",
                        "operator": "exists"
                      },
                      {
                        "kind": "not",
                        "condition": {"kind": "tag", "tag": "blocked"}
                      }
                    ]
                  },
                  "effects": [
                    {
                      "kind": "set",
                      "effectId": "set-status",
                      "entity": "subject",
                      "path": "/status",
                      "resourceKey": "actor:status",
                      "value": "active"
                    },
                    {
                      "kind": "remove",
                      "effectId": "remove-old",
                      "entity": "subject",
                      "path": "/obsolete",
                      "resourceKey": "actor:obsolete"
                    },
                    {
                      "kind": "numeric",
                      "effectId": "add-score",
                      "entity": "subject",
                      "path": "/score",
                      "resourceKey": "actor:score",
                      "schemaId": "score",
                      "operation": "add",
                      "value": "1"
                    },
                    {
                      "kind": "transfer",
                      "effectId": "transfer",
                      "source": "subject",
                      "sourcePath": "/balance",
                      "sourceResourceKey": "actor:balance",
                      "target": {"entityId": "target", "incarnation": "1"},
                      "targetPath": "/balance",
                      "targetResourceKey": "target:balance",
                      "schemaId": "balance",
                      "amount": "5"
                    },
                    {
                      "kind": "relationship",
                      "effectId": "relate",
                      "source": "subject",
                      "target": {"entityId": "target", "incarnation": "1"},
                      "relationshipTypeId": "observes",
                      "resourceKey": "relation:actor:target",
                      "operation": "upsert",
                      "value": {"state": "active"}
                    },
                    {
                      "kind": "emit_event",
                      "effectId": "emit-child",
                      "eventKind": "after_tick",
                      "payload": {"source": "clock"}
                    }
                  ]
                },
                {
                  "definitionId": "child",
                  "version": "1",
                  "priority": 0,
                  "trigger": {
                    "kind": "event",
                    "eventKind": "after_tick"
                  },
                  "selector": {"kind": "singleton"},
                  "condition": {
                    "kind": "all",
                    "conditions": [
                      {
                        "kind": "path",
                        "source": "world",
                        "path": "/entities/actor/status",
                        "operator": "eq",
                        "value": "active"
                      },
                      {
                        "kind": "path",
                        "source": "trigger",
                        "path": "/source",
                        "operator": "eq",
                        "value": "clock"
                      }
                    ]
                  },
                  "effects": [
                    {
                      "kind": "set",
                      "effectId": "child-state",
                      "entity": {"entityId": "actor", "incarnation": "1"},
                      "path": "/childApplied",
                      "resourceKey": "actor:child",
                      "value": true
                    }
                  ]
                }
              ]
            }
            """;
    }

    private static string BasicInteraction()
    {
        return
            """
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
                    "kind": "all",
                    "conditions": [
                      {"kind": "tag", "tag": "npc"},
                      {
                        "kind": "fixed_point",
                        "source": "subject",
                        "path": "/score",
                        "schemaId": "score",
                        "operator": "gte",
                        "value": "0"
                      }
                    ]
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
                      "value": "1"
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
            """;
    }

    private static WorldPackageFile JsonFile(string path, string value)
    {
        return new WorldPackageFile(
            path,
            "application/json",
            Encoding.UTF8.GetBytes(value));
    }

    private sealed class CancelAfterFirstCommitStore
        : IWorldAuthoritativeTransactionStore
    {
        private readonly IWorldAuthoritativeTransactionStore _inner;

        private readonly CancellationTokenSource _cancellation;

        private int _commits;

        public CancelAfterFirstCommitStore(
            IWorldAuthoritativeTransactionStore inner,
            CancellationTokenSource cancellation)
        {
            _inner = inner;
            _cancellation = cancellation;
        }

        public ValueTask<WorldAuthoritativeStateSnapshot?> ReadAsync(
            WorldTimelineAddress address,
            CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(address, cancellationToken);
        }

        public async ValueTask<WorldTransactionBeginResult> BeginAsync(
            WorldTransactionRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _inner.BeginAsync(
                request,
                cancellationToken);
            return result.Status == WorldTransactionBeginStatus.Acquired
                ? WorldTransactionBeginResult.Acquired(
                    new CancellingTransaction(
                        result.Transaction!,
                        this))
                : result;
        }

        public ValueTask<WorldTransactionInspectionResult> InspectAsync(
            WorldTransactionScope scope,
            string operationId,
            CancellationToken cancellationToken)
        {
            return _inner.InspectAsync(
                scope,
                operationId,
                cancellationToken);
        }

        public ValueTask<WorldTransactionReconciliationResult>
            ReconcileAsync(
                WorldTransactionScope scope,
                string operationId,
                string requestFingerprint,
                CancellationToken cancellationToken)
        {
            return _inner.ReconcileAsync(
                scope,
                operationId,
                requestFingerprint,
                cancellationToken);
        }

        public ValueTask<WorldTransactionReconciliationResult>
            CancelPendingAsync(
                WorldTransactionScope scope,
                string operationId,
                string requestFingerprint,
                string outcomeCode,
                CancellationToken cancellationToken)
        {
            return _inner.CancelPendingAsync(
                scope,
                operationId,
                requestFingerprint,
                outcomeCode,
                cancellationToken);
        }

        private void OnCommit(WorldTransactionCommitResult result)
        {
            if (result.Status == WorldTransactionCommitStatus.Committed
                && Interlocked.Increment(ref _commits) == 1)
            {
                _cancellation.Cancel();
            }
        }

        private sealed class CancellingTransaction
            : IWorldAuthoritativeTransaction
        {
            private readonly IWorldAuthoritativeTransaction _inner;

            private readonly CancelAfterFirstCommitStore _owner;

            public CancellingTransaction(
                IWorldAuthoritativeTransaction inner,
                CancelAfterFirstCommitStore owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public WorldTransactionRequest Request => _inner.Request;

            public WorldAuthoritativeStateSnapshot Source => _inner.Source;

            public IWorldStateDraft Draft => _inner.Draft;

            public async ValueTask<WorldTransactionCommitResult>
                CommitEventAsync(
                    WorldEffectReceipt effect,
                    CancellationToken cancellationToken)
            {
                var result = await _inner.CommitEventAsync(
                    effect,
                    cancellationToken);
                _owner.OnCommit(result);
                return result;
            }

            public ValueTask<WorldTransactionCommitResult>
                CompleteWithoutMutationAsync(
                    WorldCommandReceiptStatus status,
                    string outcomeCode,
                    WorldEffectReceipt? effect,
                    CancellationToken cancellationToken)
            {
                return _inner.CompleteWithoutMutationAsync(
                    status,
                    outcomeCode,
                    effect,
                    cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                return _inner.DisposeAsync();
            }
        }
    }
}
