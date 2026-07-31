using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class GameContextAdvancementTests
{
    [Fact]
    public void TerminalReceiptRoundTripsResultingCoordinate()
    {
        var receipt = Receipt("operation-1");
        var resulting = Coordinate(
            stateVersion: "state-2",
            saveRevision: 2,
            sessionId: "session-1");

        GameContextReceiptEnvelope.AttachResulting(receipt, resulting);

        Assert.True(
            GameContextReceiptEnvelope.TryReadResulting(
                receipt,
                out var restored));
        Assert.Equal("state-2", restored!.StateVersion);
        Assert.Equal("session-1", restored.SessionId);
    }

    [Fact]
    public void PublicTryReadRejectsUnknownAndIncompleteEnvelopes()
    {
        var unknown = Receipt(
            "operation-1",
            ReceiptStatuses.Unknown);
        unknown.Extensions[
                GameContextReceiptEnvelope.ResultingExtensionName] =
            GameContextEnvelope.ToJson(Coordinate());

        Assert.False(
            GameContextReceiptEnvelope.TryReadResulting(
                unknown,
                out _));

        var incomplete = Receipt("operation-2");
        incomplete.Extensions[
                GameContextReceiptEnvelope.PreviousExtensionName] =
            GameContextEnvelope.ToJson(Coordinate());
        Assert.False(
            GameContextReceiptEnvelope.TryReadResulting(
                incomplete,
                out _));
    }

    [Fact]
    public void MissingResultingExtensionPreservesExistingBehavior()
    {
        var run = Run();
        var current = Current(run);
        var before = run.Extensions[
                GameContextEnvelope.ExtensionName]
            .GetRawText();
        var request = Request(run, "operation-1", current);
        var plan = GameContextAdvancementPlanner.Plan(
            run,
            new[] { request },
            new[] { Receipt("operation-1") });

        Assert.Null(plan);
        Assert.Equal("state-1", Current(run).StateVersion);
        Assert.Equal(
            before,
            run.Extensions[GameContextEnvelope.ExtensionName].GetRawText());
    }

    [Fact]
    public void ResultingCoordinateAdvancesBoundRequest()
    {
        var run = Run();
        var current = Current(run);
        var request = Request(run, "operation-1", current);
        var receipt = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            Coordinate(
                stateVersion: "state-2",
                saveRevision: 2,
                sessionId: "session-1"));

        var plan = GameContextAdvancementPlanner.Plan(
            run,
            new[] { request },
            new[] { receipt });

        Assert.NotNull(plan);
        Assert.Equal("state-1", plan!.Previous.StateVersion);
        Assert.Equal("state-2", plan.Resulting.StateVersion);
    }

    [Fact]
    public void IngressAcceptsTerminalResultingCoordinate()
    {
        var run = Run();
        var current = Current(run);
        var request = Request(run, "operation-1", current);
        var receipt = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            Coordinate(
                stateVersion: "state-2",
                saveRevision: 2,
                sessionId: "session-1"));

        var admitted = ActionReceiptIngressValidator.ValidateAndClone(
            request,
            receipt,
            run);

        Assert.True(
            GameContextReceiptEnvelope.TryReadResulting(
                admitted,
                out var resulting));
        Assert.Equal("state-2", resulting!.StateVersion);
    }

    [Fact]
    public void IngressBindsMissingReceiptSessionsToTheRun()
    {
        var run = Run();
        var current = Current(run);
        var request = Request(run, "operation-1", current);
        var receipt = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            Coordinate(
                stateVersion: "state-2",
                saveRevision: 2),
            new GameContextCoordinate(
                current.WorldId,
                current.TimelineId,
                current.SaveRevision,
                current.Observer,
                current.SceneId,
                current.RegionId,
                current.StateVersion,
                current.GameTime,
                current.Causality));

        var admitted = ActionReceiptIngressValidator.ValidateAndClone(
            request,
            receipt,
            run);
        var plan = GameContextAdvancementPlanner.Plan(
            run,
            new[] { request },
            new[] { admitted });

        Assert.NotNull(plan);
        Assert.Equal("session-1", plan!.Previous.SessionId);
        Assert.Equal("session-1", plan.Resulting.SessionId);
        Assert.True(
            GameContextReceiptEnvelope.TryReadResulting(
                admitted,
                out var admittedResulting));
        Assert.Equal("session-1", admittedResulting!.SessionId);
        var admittedPrevious =
            GameContextReceiptEnvelope.ReadCoordinate(
                admitted.Extensions[
                    GameContextReceiptEnvelope.PreviousExtensionName],
                GameContextReceiptEnvelope.PreviousExtensionName);
        Assert.Equal("session-1", admittedPrevious.SessionId);
    }

    [Fact]
    public void IngressRejectsAnExplicitCrossSessionReceiptCoordinate()
    {
        var run = Run();
        var current = Current(run);
        var request = Request(run, "operation-1", current);
        var receipt = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            Coordinate(
                stateVersion: "state-2",
                saveRevision: 2,
                sessionId: "session-other"));

        var exception = Assert.Throws<GameContextAdvancementException>(
            () => ActionReceiptIngressValidator.ValidateAndClone(
                request,
                receipt,
                run));

        Assert.Equal(
            GameContextAdvancementReasonCodes.IdentityMismatch,
            exception.ReasonCode);
    }

    [Fact]
    public void BasedOnFenceMustMatchExactRequestCoordinate()
    {
        var run = Run();
        var current = Current(run);
        var request = Request(run, "operation-1", current);
        request.BasedOnStateVersion = "stale-state";
        var receipt = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            Coordinate(
                stateVersion: "state-2",
                saveRevision: 2,
                sessionId: "session-1"));

        var exception = Assert.Throws<GameContextAdvancementException>(
            () => GameContextAdvancementPlanner.Plan(
                run,
                new[] { request },
                new[] { receipt }));

        Assert.Equal(
            GameContextAdvancementReasonCodes.TransitionConflict,
            exception.ReasonCode);
    }

    [Fact]
    public void DecisionWindowRequiresOneResultingCoordinate()
    {
        var run = Run();
        var current = Current(run);
        var request1 = Request(run, "operation-1", current);
        var request2 = Request(run, "operation-2", current);
        var receipt1 = Receipt("operation-1");
        var receipt2 = Receipt("operation-2");
        GameContextReceiptEnvelope.AttachResulting(
            receipt1,
            Coordinate(
                stateVersion: "state-2",
                saveRevision: 2,
                sessionId: "session-1"));
        GameContextReceiptEnvelope.AttachResulting(
            receipt2,
            Coordinate(
                stateVersion: "state-3",
                saveRevision: 3,
                sessionId: "session-1"));

        var exception = Assert.Throws<GameContextAdvancementException>(
            () => GameContextAdvancementPlanner.Plan(
                run,
                new[] { request1, request2 },
                new[] { receipt1, receipt2 }));

        Assert.Equal(
            GameContextAdvancementReasonCodes.TransitionConflict,
            exception.ReasonCode);
    }

    [Fact]
    public void DecisionWindowAcceptsMatchingFinalCoordinates()
    {
        var run = Run();
        var current = Current(run);
        var request1 = Request(run, "operation-1", current);
        var request2 = Request(run, "operation-2", current);
        var resulting = Coordinate(
            stateVersion: "state-2",
            saveRevision: 2,
            sessionId: "session-1");
        var receipt1 = Receipt("operation-1");
        var receipt2 = Receipt("operation-2");
        GameContextReceiptEnvelope.AttachResulting(receipt1, resulting);
        GameContextReceiptEnvelope.AttachResulting(receipt2, resulting);

        var plan = GameContextAdvancementPlanner.Plan(
            run,
            new[] { request1, request2 },
            new[] { receipt1, receipt2 });

        Assert.NotNull(plan);
        Assert.Equal(
            new[] { "operation-1", "operation-2" },
            plan!.OperationIds);
    }

    [Fact]
    public void EveryRequestInAdvancingWindowMustMatchSourceCoordinate()
    {
        var run = Run();
        var current = Current(run);
        var request1 = Request(run, "operation-1", current);
        var request2 = Request(run, "operation-2", current);
        request2.Extensions[GameContextEnvelope.ExtensionName] =
            GameContextEnvelope.ToJson(
                Coordinate(
                    stateVersion: "forged-state",
                    saveRevision: 1,
                    sessionId: "session-1"));
        var advancing = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            advancing,
            Coordinate(
                stateVersion: "state-2",
                saveRevision: 2,
                sessionId: "session-1"));

        var exception = Assert.Throws<GameContextAdvancementException>(
            () => GameContextAdvancementPlanner.Plan(
                run,
                new[] { request1, request2 },
                new[] { advancing, Receipt("operation-2") }));

        Assert.Equal(
            GameContextAdvancementReasonCodes.TransitionConflict,
            exception.ReasonCode);
    }

    [Theory]
    [InlineData("world-other", "timeline-1", "session-1", 1)]
    [InlineData("world-1", "timeline-other", "session-1", 1)]
    [InlineData("world-1", "timeline-1", "session-other", 1)]
    [InlineData("world-1", "timeline-1", "session-1", 0)]
    public void IdentityEscapeAndSaveRegressionFailClosed(
        string worldId,
        string timelineId,
        string sessionId,
        long saveRevision)
    {
        var run = Run();
        var current = Current(run);
        var receipt = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            new GameContextCoordinate(
                worldId,
                timelineId,
                saveRevision,
                current.Observer,
                stateVersion: "state-2",
                sessionId: sessionId));

        Assert.Throws<GameContextAdvancementException>(
            () => GameContextAdvancementPlanner.Plan(
                run,
                new[] { Request(run, "operation-1", current) },
                new[] { receipt }));
    }

    [Fact]
    public void ObserverIncarnationCannotChange()
    {
        var run = Run();
        var current = Current(run);
        var receipt = Receipt("operation-1");
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            new GameContextCoordinate(
                "world-1",
                "timeline-1",
                2,
                new GameEntityIdentity("npc-1", 2),
                stateVersion: "state-2",
                sessionId: "session-1"));

        var exception = Assert.Throws<GameContextAdvancementException>(
            () => GameContextAdvancementPlanner.Plan(
                run,
                new[] { Request(run, "operation-1", current) },
                new[] { receipt }));

        Assert.Equal(
            GameContextAdvancementReasonCodes.IdentityMismatch,
            exception.ReasonCode);
    }

    [Fact]
    public void GameTimeAllowsNewEpochButRejectsRewind()
    {
        var previous = Coordinate(
            gameTime: new GameTimePoint(
                "world-clock",
                "timeline-1",
                4,
                900),
            sessionId: "session-1");
        var nextEpoch = Coordinate(
            stateVersion: "state-2",
            saveRevision: 2,
            gameTime: new GameTimePoint(
                "world-clock",
                "timeline-1",
                5,
                0),
            sessionId: "session-1");

        GameContextAdvancementPlanner.ValidateForward(
            previous,
            nextEpoch,
            run: null);

        var rewind = Coordinate(
            stateVersion: "state-2",
            saveRevision: 2,
            gameTime: new GameTimePoint(
                "world-clock",
                "timeline-1",
                3,
                999),
            sessionId: "session-1");
        Assert.Throws<GameContextAdvancementException>(
            () => GameContextAdvancementPlanner.ValidateForward(
                previous,
                rewind,
                run: null));
    }

    [Fact]
    public void CausalAdvanceRequiresPreviousEventAsParent()
    {
        var previous = Coordinate(
            causality: new GameCausalityStamp(
                "event-1",
                "state-1"),
            sessionId: "session-1");
        var valid = Coordinate(
            stateVersion: "state-2",
            saveRevision: 2,
            causality: new GameCausalityStamp(
                "event-2",
                "state-2",
                new[] { "event-1" }),
            sessionId: "session-1");
        GameContextAdvancementPlanner.ValidateForward(
            previous,
            valid,
            run: null);

        var invalid = Coordinate(
            stateVersion: "state-2",
            saveRevision: 2,
            causality: new GameCausalityStamp(
                "event-2",
                "state-2",
                new[] { "event-other" }),
            sessionId: "session-1");
        Assert.Throws<GameContextAdvancementException>(
            () => GameContextAdvancementPlanner.ValidateForward(
                previous,
                invalid,
                run: null));
    }

    [Fact]
    public void CoordinateJsonIsStrictlyBoundedAndRejectsUnknownFields()
    {
        var receipt = Receipt("operation-1");
        receipt.Extensions[
                GameContextReceiptEnvelope.ResultingExtensionName] =
            ProtocolJson.ParseElement(
                """
                {
                  "worldId":"world-1",
                  "timelineId":"timeline-1",
                  "saveRevision":2,
                  "stateVersion":"state-2",
                  "sessionId":"session-1",
                  "unexpected":true
                }
                """);

        Assert.False(
            GameContextReceiptEnvelope.TryReadResulting(receipt, out _));

        receipt.Extensions[
                GameContextReceiptEnvelope.ResultingExtensionName] =
            ProtocolJson.ParseElement(
                $$"""
                {
                  "worldId":"world-1",
                  "timelineId":"timeline-1",
                  "saveRevision":2,
                  "stateVersion":"{{new string('x', 17_000)}}",
                  "sessionId":"session-1"
                }
                """);
        Assert.False(
            GameContextReceiptEnvelope.TryReadResulting(receipt, out _));
    }

    private static AgentRun Run()
    {
        var run = new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.WaitingForAction,
            Revision = 1,
            CurrentTurnId = "turn-1",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
        GameContextEnvelope.Attach(run, Coordinate());
        return run;
    }

    private static GameContextCoordinate Current(AgentRun run)
    {
        Assert.True(GameContextEnvelope.TryRead(run, out var coordinate));
        return coordinate!;
    }

    private static GameContextCoordinate Coordinate(
        string stateVersion = "state-1",
        long saveRevision = 1,
        GameTimePoint? gameTime = null,
        GameCausalityStamp? causality = null,
        string? sessionId = null)
    {
        return new GameContextCoordinate(
            "world-1",
            "timeline-1",
            saveRevision,
            new GameEntityIdentity("npc-1", 1),
            stateVersion: stateVersion,
            gameTime: gameTime,
            causality: causality,
            sessionId: sessionId);
    }

    private static ActionRequest Request(
        AgentRun run,
        string operationId,
        GameContextCoordinate source)
    {
        return new ActionRequest
        {
            OperationId = operationId,
            RunId = run.RunId,
            TurnId = run.CurrentTurnId!,
            ToolCallId = "call-" + operationId,
            AgentId = run.AgentId,
            WorldId = run.WorldId,
            ActionName = "game.act",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            BasedOnStateVersion = source.StateVersion,
            RequestedAt = DateTimeOffset.UnixEpoch,
            Extensions = new Dictionary<string, System.Text.Json.JsonElement>(
                StringComparer.Ordinal)
            {
                [GameContextEnvelope.ExtensionName] =
                    GameContextEnvelope.ToJson(source)
            }
        };
    }

    private static ActionReceipt Receipt(
        string operationId,
        string status = ReceiptStatuses.Succeeded)
    {
        return new ActionReceipt
        {
            OperationId = operationId,
            Revision = 1,
            Status = status,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
    }
}
