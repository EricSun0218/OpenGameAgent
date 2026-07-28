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
                    payloadJson = "{\"hunger\":70}",
                    trust = "authoritative",
                    visibilityScope = "agent",
                    audienceIds = new[] { "agent-1" }
                });

            var json = UnityProtocolBridge.ToJson(observation);
            var roundTrip =
                UnityProtocolBridge.ObservationFromJson(json);

            Assert.That(
                roundTrip.Payload.Value.GetProperty("hunger").GetInt32(),
                Is.EqualTo(70));
            Assert.That(roundTrip.ContentSchemaVersion, Is.EqualTo("1"));
        }
    }
}
