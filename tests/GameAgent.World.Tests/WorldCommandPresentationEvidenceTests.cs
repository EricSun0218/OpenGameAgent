using System.Text.Json;
using GameAgent.Core;
using GameAgent.World;

namespace GameAgent.World.Tests;

public sealed class WorldCommandPresentationEvidenceTests
{
    [Fact]
    public void AppliedReceiptProjectsExactBindingWithoutRawEffectPayload()
    {
        var receipt = AppliedReceipt();
        var gameTime = new GameTimePoint(
            "calendar.month",
            "timeline",
            epoch: 3,
            tick: 17);

        var evidence =
            WorldCommandPresentationEvidence.CreateApplied(
                receipt,
                gameTime);

        Assert.Equal(receipt.ReceiptId, evidence.Source.WorldReceiptId);
        Assert.Equal(receipt.EventInstanceId, evidence.Source.OccurrenceId);
        Assert.Equal(receipt.CommandId, evidence.Source.ActionId);
        Assert.Equal(receipt.OperationId, evidence.Source.OperationId);
        Assert.True(
            receipt.ResultingCoordinate!.IsExactMatch(
                new WorldAuthoritativeCoordinate(
                    evidence.Binding.WorldId,
                    evidence.Binding.TimelineId,
                    evidence.Binding.TimelineEpoch,
                    evidence.Binding.SaveRevision,
                    evidence.Binding.StateVersion,
                    evidence.Binding.CatalogDigest)));
        Assert.Equal(
            receipt.ResultingStateDigest,
            evidence.Binding.CommittedStateDigest);
        Assert.Equal(gameTime.Tick, evidence.Binding.GameTime!.Tick);
        var projected = evidence.ReceiptEvidence!.Value;
        Assert.Equal(
            WorldCommandPresentationEvidence.ContractId,
            projected.GetProperty("contract").GetString());
        Assert.Equal(
            CanonicalJsonDigest.ComputeSha256(projected),
            evidence.Source.WorldReceiptDigest);
        Assert.DoesNotContain(
            "private-effect-value",
            projected.GetRawText(),
            StringComparison.Ordinal);
        Assert.True(
            CanonicalJsonDigest.IsSha256(
                projected.GetProperty("effect")
                    .GetProperty("typedResultDigest")
                    .GetString()));
    }

    [Fact]
    public void ProjectionIsDeterministicAndRejectsUnsafeReceiptShapes()
    {
        var receipt = AppliedReceipt();
        var first =
            WorldCommandPresentationEvidence.CreateApplied(receipt);
        var second =
            WorldCommandPresentationEvidence.CreateApplied(receipt);
        Assert.Equal(first.SemanticDigest, second.SemanticDigest);

        var rejectedRequest = Request();
        var rejected = new WorldCommandReceipt(
            rejectedRequest,
            WorldCommandReceiptStatus.Rejected,
            "denied",
            resultingCoordinate: null,
            resultingStateDigest: null,
            new WorldEffectReceipt(false, "denied"),
            eventInstanceId: null);
        _ = Assert.Throws<ArgumentException>(
            () => WorldCommandPresentationEvidence.CreateApplied(
                rejected));
        _ = Assert.Throws<ArgumentException>(
            () => WorldCommandPresentationEvidence.CreateApplied(
                receipt,
                new GameTimePoint(
                    "calendar.month",
                    "another-timeline",
                    epoch: 3,
                    tick: 17)));
        _ = Assert.Throws<ArgumentException>(
            () => WorldCommandPresentationEvidence.CreateApplied(
                receipt,
                new GameTimePoint(
                    "another-clock",
                    "timeline",
                    epoch: 3,
                    tick: 17)));
        _ = Assert.Throws<ArgumentException>(
            () => WorldCommandPresentationEvidence.CreateApplied(
                receipt,
                new GameTimePoint(
                    "calendar.month",
                    "timeline",
                    epoch: 3,
                    tick: 18)));
        _ = Assert.Throws<ArgumentException>(
            () => WorldCommandPresentationEvidence.CreateApplied(
                AppliedReceipt(hasAuthoritativeTime: false),
                new GameTimePoint(
                    "calendar.month",
                    "timeline",
                    epoch: 3,
                    tick: 17)));
        Assert.NotNull(
            WorldCommandPresentationEvidence.CreateApplied(
                AppliedReceipt(hasAuthoritativeTime: false)));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, true)]
    [InlineData(192, true)]
    [InlineData(193, false)]
    public void SourceOperationIdUsesWorldOperationBoundary(
        int utf8Bytes,
        bool accepted)
    {
        var operationId = new string('o', utf8Bytes);
        if (!accepted)
        {
            _ = Assert.ThrowsAny<ArgumentException>(
                () => new WorldPresentationSource(
                    "receipt",
                    new string('a', 64),
                    operationId: operationId));
            return;
        }

        var source = new WorldPresentationSource(
            "receipt",
            new string('a', 64),
            operationId: operationId);

        Assert.Equal(operationId, source.OperationId);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(192)]
    public void ProjectionPreservesLegalLongOperationId(
        int utf8Bytes)
    {
        var operationId = new string('o', utf8Bytes);
        var commandId = new string('c', utf8Bytes);
        var evidence = WorldCommandPresentationEvidence.CreateApplied(
            AppliedReceipt(operationId, commandId));

        Assert.Equal(operationId, evidence.Source.OperationId);
        Assert.Null(evidence.Source.ActionId);
    }

    private static WorldCommandReceipt AppliedReceipt(
        string operationId = "operation",
        string commandId = "command",
        bool hasAuthoritativeTime = true)
    {
        var request = Request(
            operationId,
            commandId,
            hasAuthoritativeTime);
        return new WorldCommandReceipt(
            request,
            WorldCommandReceiptStatus.Applied,
            "world_action_applied",
            request.ExpectedCoordinate.Advance(stateChanged: true),
            new string('b', 64),
            new WorldEffectReceipt(
                true,
                "world_action_applied",
                Json("""{"secret":"private-effect-value"}""")),
            request.EventOccurrence!.InstanceId);
    }

    private static WorldTransactionRequest Request(
        string operationId = "operation",
        string commandId = "command",
        bool hasAuthoritativeTime = true)
    {
        var coordinate = new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            timelineEpoch: 3,
            saveRevision: 7,
            stateVersion: 11,
            new string('a', 64));
        var occurrence = new WorldEventHistoryRecord(
            "event-instance",
            new WorldEventDefinitionKey(
                coordinate.WorldId,
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                "event",
                "1"),
            "trigger",
            "resolution",
            new string('c', 64),
            hasAuthoritativeTime
                ? new GameTimePoint(
                    "calendar.month",
                    coordinate.TimelineId,
                    coordinate.TimelineEpoch,
                    tick: 17)
                : null);
        return new WorldTransactionRequest(
            operationId,
            commandId,
            new string('d', 64),
            coordinate,
            new[]
            {
                new WorldEntityIncarnationExpectation("npc", 1)
            },
            occurrence);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
