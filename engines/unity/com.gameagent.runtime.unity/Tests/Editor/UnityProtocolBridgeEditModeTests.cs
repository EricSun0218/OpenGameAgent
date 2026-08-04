using GameAgent.Core;
using GameAgent.Protocol;
using NUnit.Framework;

namespace GameAgent.Unity.Tests
{
    public sealed class UnityProtocolBridgeEditModeTests
    {
        [Test]
        public void StructuredObservationRoundTrips()
        {
            var observation = UnityProtocolBridge.ToProtocol(
                new UnityObservationData
                {
                    observationId = "observation-1",
                    worldId = "world-1",
                    source = "game.state",
                    kind = "snapshot",
                    contentType = "application/json",
                    contentSchemaVersion = "1",
                    payloadJson =
                        "{\"hunger\":70,\"temperature\":36.625,"
                        + "\"position\":[1.25,-3.5],"
                        + "\"nested\":{\"weight\":0.125}}",
                    hasObservedAtUnixMilliseconds = true,
                    observedAtUnixMilliseconds = 0,
                    trust = "authoritative",
                    visibilityScope = "agent",
                    audienceIds = new[] { "agent-1" },
                    audienceIncarnations = new[]
                    {
                        new UnityAudienceIncarnationData
                        {
                            audienceId = "agent-1",
                            entityId = "npc-1",
                            incarnation = 2
                        }
                    }
                });

            var json = UnityProtocolBridge.ToJson(observation);
            var roundTrip =
                UnityProtocolBridge.ObservationFromJson(json);

            Assert.That(
                roundTrip.Payload.Value.GetProperty("hunger").GetInt32(),
                Is.EqualTo(70));
            Assert.That(
                roundTrip.Payload.Value.GetProperty("temperature").GetDouble(),
                Is.EqualTo(36.625));
            Assert.That(
                roundTrip.Payload.Value.GetProperty("position")[0].GetDouble(),
                Is.EqualTo(1.25));
            Assert.That(
                roundTrip.Payload.Value.GetProperty("position")[1].GetDouble(),
                Is.EqualTo(-3.5));
            Assert.That(
                roundTrip.Payload.Value.GetProperty("nested")
                    .GetProperty("weight").GetDouble(),
                Is.EqualTo(0.125));
            Assert.That(roundTrip.ContentSchemaVersion, Is.EqualTo("1"));
            Assert.That(
                roundTrip.Extensions.ContainsKey(
                    GameAgent.Core.ObservationAudienceIncarnations
                        .ExtensionName),
                Is.True);
        }

        [Test]
        public void ResultingGameContextReceiptRoundTrips()
        {
            var coordinate = new GameContextCoordinate(
                "world-1",
                "timeline-1",
                2,
                new GameEntityIdentity("npc-1", 3),
                stateVersion: "state-2",
                sessionId: "session-1");
            var extension = GameContextEnvelope.ToJson(coordinate)
                .GetRawText();
            var receipt = UnityProtocolBridge.ToProtocol(
                new UnityActionReceiptData
                {
                    operationId = "operation-1",
                    revision = 1,
                    status = ReceiptStatuses.Succeeded,
                    extensionsJson =
                        "{\"resultingGameContext\":" + extension + "}",
                    hasReceivedAtUnixMilliseconds = true,
                    receivedAtUnixMilliseconds = 0
                });

            var roundTrip = UnityProtocolBridge.ActionReceiptFromJson(
                UnityProtocolBridge.ToJson(receipt));

            Assert.That(
                GameContextReceiptEnvelope.TryReadResulting(
                    roundTrip,
                    out var restored),
                Is.True);
            Assert.That(restored.StateVersion, Is.EqualTo("state-2"));
            Assert.That(restored.Observer.Incarnation, Is.EqualTo(3));
            Assert.That(restored.SessionId, Is.EqualTo("session-1"));
        }
    }
}
