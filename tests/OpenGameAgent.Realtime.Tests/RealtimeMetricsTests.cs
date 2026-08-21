namespace OpenGameAgent.Realtime.Tests;

using Xunit;

public sealed class RealtimeMetricsTests
{
    [Fact]
    public async Task CollectorMeasuresTranscriptAudioAndBargeInWithoutChangingEvents()
    {
        var now = DateTimeOffset.UnixEpoch;
        var collector = new RealtimeMetricsCollector(clock: () => now);
        var cancellationToken = TestContext.Current.CancellationToken;
        await collector.HandleAsync(new RealtimeConversationEvent(RealtimeConversationEventKind.InputSpeechStarted), cancellationToken);
        now = now.AddMilliseconds(10);
        await collector.HandleAsync(new RealtimeConversationEvent(RealtimeConversationEventKind.InputTranscriptDelta, text: "a"), cancellationToken);
        now = now.AddMilliseconds(5);
        await collector.HandleAsync(new RealtimeConversationEvent(RealtimeConversationEventKind.InputTranscriptDone, text: "all"), cancellationToken);
        now = now.AddMilliseconds(5);
        await collector.HandleAsync(new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseStarted, responseId: "response"), cancellationToken);
        now = now.AddMilliseconds(7);
        await collector.HandleAsync(new RealtimeConversationEvent(
            RealtimeConversationEventKind.AudioOutput,
            audio: new RealtimeAudioFrame(new byte[] { 0, 0 }),
            responseId: "response"), cancellationToken);
        now = now.AddMilliseconds(3);
        collector.MarkBargeInRequested("response");
        now = now.AddMilliseconds(4);
        await collector.HandleAsync(new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseCancelled, responseId: "response"), cancellationToken);

        var samples = collector.Snapshot();
        Assert.Collection(
            samples,
            value =>
            {
                Assert.Equal(RealtimeLatencyKind.FirstInputTranscript, value.Kind);
                Assert.Equal(10, value.DurationMilliseconds);
            },
            value =>
            {
                Assert.Equal(RealtimeLatencyKind.FinalInputTranscript, value.Kind);
                Assert.Equal(15, value.DurationMilliseconds);
            },
            value =>
            {
                Assert.Equal(RealtimeLatencyKind.FirstOutputAudio, value.Kind);
                Assert.Equal(7, value.DurationMilliseconds);
            },
            value =>
            {
                Assert.Equal(RealtimeLatencyKind.BargeInCancellation, value.Kind);
                Assert.Equal(4, value.DurationMilliseconds);
            });
    }

    [Fact]
    public async Task CollectorRecordsAudioCompletionOnlyAfterAudioWasObserved()
    {
        var now = DateTimeOffset.UnixEpoch;
        var collector = new RealtimeMetricsCollector(clock: () => now);
        var cancellationToken = TestContext.Current.CancellationToken;

        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseStarted, responseId: "text-only"),
            cancellationToken);
        now = now.AddMilliseconds(2);
        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseDone, responseId: "text-only"),
            cancellationToken);

        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseStarted, responseId: "audio"),
            cancellationToken);
        now = now.AddMilliseconds(3);
        await collector.HandleAsync(
            new RealtimeConversationEvent(
                RealtimeConversationEventKind.AudioOutput,
                audio: new RealtimeAudioFrame(new byte[] { 0, 0 }),
                responseId: "audio"),
            cancellationToken);
        now = now.AddMilliseconds(4);
        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseDone, responseId: "audio"),
            cancellationToken);

        Assert.DoesNotContain(collector.Snapshot(), value => value.ResponseId == "text-only");
        Assert.Contains(
            collector.Snapshot(),
            value => value.ResponseId == "audio"
                     && value.Kind == RealtimeLatencyKind.CompleteOutputAudio
                     && value.DurationMilliseconds == 7);
    }

    [Fact]
    public async Task CollectorBoundsIncompleteResponseAndBargeInState()
    {
        var now = DateTimeOffset.UnixEpoch;
        var collector = new RealtimeMetricsCollector(capacity: 1, clock: () => now);
        var cancellationToken = TestContext.Current.CancellationToken;

        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseStarted, responseId: "old"),
            cancellationToken);
        now = now.AddMilliseconds(1);
        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseStarted, responseId: "current"),
            cancellationToken);
        await collector.HandleAsync(
            new RealtimeConversationEvent(
                RealtimeConversationEventKind.AudioOutput,
                audio: new RealtimeAudioFrame(new byte[] { 0, 0 }),
                responseId: "old"),
            cancellationToken);
        now = now.AddMilliseconds(2);
        await collector.HandleAsync(
            new RealtimeConversationEvent(
                RealtimeConversationEventKind.AudioOutput,
                audio: new RealtimeAudioFrame(new byte[] { 0, 0 }),
                responseId: "current"),
            cancellationToken);

        collector.MarkBargeInRequested("old");
        now = now.AddMilliseconds(1);
        collector.MarkBargeInRequested("current");
        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseCancelled, responseId: "old"),
            cancellationToken);
        now = now.AddMilliseconds(3);
        await collector.HandleAsync(
            new RealtimeConversationEvent(RealtimeConversationEventKind.ResponseCancelled, responseId: "current"),
            cancellationToken);

        Assert.Collection(
            collector.Snapshot(),
            sample =>
            {
                Assert.Equal(RealtimeLatencyKind.BargeInCancellation, sample.Kind);
                Assert.Equal("current", sample.ResponseId);
                Assert.Equal(3, sample.DurationMilliseconds);
            });
    }
}
