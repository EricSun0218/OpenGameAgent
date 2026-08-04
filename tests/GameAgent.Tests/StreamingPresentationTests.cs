using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class StreamingPresentationTests
{
    [Fact]
    public void CoalescesSmallDeltasUntilTargetSize()
    {
        var coalescer = Coalescer(target: 5, maximum: 10);
        var now = DateTimeOffset.UnixEpoch;

        Assert.Empty(coalescer.Push("ab", now));
        var chunks = coalescer.Push("cde", now.AddMilliseconds(1));

        Assert.Equal("abcde", Assert.Single(chunks).Text);
        Assert.False(chunks[0].IsFinal);
    }

    [Fact]
    public void FlushesOnIdleAndParagraphBoundary()
    {
        var coalescer = Coalescer(target: 100, maximum: 200);
        var now = DateTimeOffset.UnixEpoch;

        Assert.Empty(coalescer.Push("first", now));
        Assert.Equal(
            "first",
            Assert.Single(
                coalescer.FlushIdle(now.AddMilliseconds(51))).Text);
        Assert.Equal(
            "paragraph\n\n",
            Assert.Single(
                coalescer.Push(
                    "paragraph\n\n",
                    now.AddMilliseconds(52))).Text);
    }

    [Fact]
    public void PreservesUnicodeWhenSplittingByUtf8Bytes()
    {
        var coalescer = Coalescer(target: 4, maximum: 8);
        var chunks = coalescer.Push(
            "你好吗",
            DateTimeOffset.UnixEpoch);
        var final = coalescer.Complete("你好吗");

        Assert.Equal(
            "你好吗",
            string.Concat(chunks.Concat(final).Select(item => item.Text)));
        Assert.DoesNotContain(
            chunks.Concat(final),
            item => item.Text.Contains('\uFFFD'));
    }

    [Fact]
    public void RetainsASurrogatePairSplitAcrossProviderDeltas()
    {
        var coalescer = Coalescer(target: 4, maximum: 8);
        var high = new string('\uD83D', 1);
        var low = new string('\uDE42', 1);

        var first = coalescer.Push(
            high,
            DateTimeOffset.UnixEpoch);
        var idle = coalescer.FlushIdle(
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var second = coalescer.Push(
            low,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var final = coalescer.Complete("🙂");
        var chunks = first.Concat(idle).Concat(second).Concat(final).ToArray();

        Assert.Empty(first);
        Assert.Empty(idle);
        Assert.Equal("🙂", string.Concat(chunks.Select(item => item.Text)));
        Assert.DoesNotContain(
            chunks,
            item => item.Text.Contains('\uFFFD'));
        Assert.DoesNotContain(
            chunks,
            item => item.Text.Any(char.IsSurrogate)
                    && item.Text.Length == 1);
    }

    [Fact]
    public void CompletionDoesNotRepeatAlreadyPresentedText()
    {
        var coalescer = Coalescer(target: 5, maximum: 10);
        _ = coalescer.Push("hello", DateTimeOffset.UnixEpoch);

        var final = Assert.Single(coalescer.Complete("hello world"));

        Assert.Equal(" world", final.Text);
        Assert.True(final.IsFinal);
        Assert.False(final.ReplacesPriorText);
    }

    [Fact]
    public void CompletionPreservesBufferedTextBeforeAppendingFinalSuffix()
    {
        var coalescer = Coalescer(target: 10, maximum: 20);

        Assert.Empty(coalescer.Push("hel", DateTimeOffset.UnixEpoch));
        var final = Assert.Single(coalescer.Complete("hello"));

        Assert.Equal("hello", final.Text);
        Assert.True(final.IsFinal);
        Assert.False(final.ReplacesPriorText);
    }

    [Fact]
    public void CompletionSignalsReplacementWhenProviderFinalDiffers()
    {
        var coalescer = Coalescer(target: 5, maximum: 10);
        _ = coalescer.Push("hello", DateTimeOffset.UnixEpoch);

        var final = coalescer.Complete("corrected");

        Assert.Equal("corrected", string.Concat(final.Select(item => item.Text)));
        Assert.True(final[^1].IsFinal);
        Assert.True(final[0].ReplacesPriorText);
        Assert.DoesNotContain(
            final.Skip(1),
            item => item.ReplacesPriorText);
    }

    [Fact]
    public void OversizedDeltaIsIncrementallySplitIntoBoundedChunks()
    {
        const int targetBytes = 1_024;
        var coalescer = Coalescer(
            target: targetBytes,
            maximum: targetBytes * 4);
        var delta = new string('x', 1024 * 1024);

        var chunks = coalescer.Push(delta, DateTimeOffset.UnixEpoch);
        var final = coalescer.Complete(delta);

        Assert.Equal(delta, string.Concat(final.Select(item => item.Text)));
        Assert.True(final[0].ReplacesPriorText);
        Assert.All(
            chunks.Concat(final),
            item => Assert.InRange(
                System.Text.Encoding.UTF8.GetByteCount(item.Text),
                1,
                targetBytes));
        Assert.True(final[^1].IsFinal);
        Assert.True(final.Count <= 1_024);
    }

    [Fact]
    public void EvidenceOverflowForcesBoundedFinalReplacement()
    {
        var coalescer = Coalescer(target: 4, maximum: 8);
        var now = DateTimeOffset.UnixEpoch;

        _ = coalescer.Push("abcd", now);
        _ = coalescer.Push("efgh", now.AddMilliseconds(1));
        _ = coalescer.Push("ijkl", now.AddMilliseconds(2));
        var final = coalescer.Complete("abcdefghijkl");

        Assert.Equal(
            "abcdefghijkl",
            string.Concat(final.Select(item => item.Text)));
        Assert.True(final[^1].IsFinal);
        Assert.True(final[0].ReplacesPriorText);
    }

    [Fact]
    public void OversizedSingleDeltaFailsBeforeChunkMaterialization()
    {
        var coalescer = new StreamingTextCoalescer(
            new StreamingPresentationOptions
            {
                TargetChunkUtf8Bytes = 1_024,
                MaximumBufferedUtf8Bytes = 4_096,
                MaxInputDeltaUtf8Bytes = 8_192,
                MaxFinalTextUtf8Bytes = 8_192,
                MaxChunksPerCall = 8
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => coalescer.Push(
                new string('x', 8_193),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("stream_delta_bytes_exceeded", error.LimitCode);
    }

    [Fact]
    public void ChunkLimitFailurePreservesPendingTextAndSequence()
    {
        var coalescer = new StreamingTextCoalescer(
            new StreamingPresentationOptions
            {
                TargetChunkUtf8Bytes = 2,
                MaximumBufferedUtf8Bytes = 4,
                IdleFlushInterval = TimeSpan.FromMilliseconds(10),
                MaxInputDeltaUtf8Bytes = 2,
                MaxFinalTextUtf8Bytes = 2,
                MaxChunksPerCall = 1
            });
        var now = DateTimeOffset.UnixEpoch;
        Assert.Empty(coalescer.Push("a", now));

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => coalescer.Push(
                "\n\n",
                now.AddMilliseconds(10)));

        Assert.Equal("stream_chunks_per_call_exceeded", error.LimitCode);
        var pending = Assert.Single(
            coalescer.FlushIdle(now.AddMilliseconds(10)));
        Assert.Equal(0, pending.Sequence);
        Assert.Equal("a", pending.Text);
        var retried = Assert.Single(
            coalescer.Push("\n\n", now.AddMilliseconds(11)));
        Assert.Equal(1, retried.Sequence);
        Assert.Equal("\n\n", retried.Text);
    }

    [Fact]
    public void DefaultChunkBudgetCanMaterializeMaximumAsciiFinalText()
    {
        var options = new StreamingPresentationOptions();
        var coalescer = new StreamingTextCoalescer(options);
        var text = new string('x', options.MaxFinalTextUtf8Bytes);

        var chunks = coalescer.Complete(text);

        Assert.Equal(options.MaxChunksPerCall, chunks.Count);
        Assert.True(chunks[^1].IsFinal);
        Assert.Equal(text.Length, chunks.Sum(item => item.Text.Length));
    }

    [Fact]
    public void ReplaysRetainedChunksFromNextExpectedConsumerCursor()
    {
        var coalescer = new StreamingTextCoalescer(
            new StreamingPresentationOptions
            {
                TargetChunkUtf8Bytes = 2,
                MaximumBufferedUtf8Bytes = 8,
                MaxReplayChunks = 4,
                MaxReplayUtf8Bytes = 8
            });

        _ = coalescer.Push("abcdef", DateTimeOffset.UnixEpoch);

        var firstPage = coalescer.ReplayFrom(0, maximumChunks: 2);
        Assert.Equal(
            StreamingPresentationReplayStatus.Available,
            firstPage.Status);
        Assert.Equal(new long[] { 0, 1 }, firstPage.Chunks.Select(x => x.Sequence));
        Assert.Equal("abcd", string.Concat(firstPage.Chunks.Select(x => x.Text)));
        Assert.Equal(2, firstPage.ContinuationSequence);
        Assert.Equal(3, firstPage.ProducedSequenceExclusive);
        Assert.False(firstPage.IsComplete);

        var secondPage = coalescer.ReplayFrom(
            firstPage.ContinuationSequence,
            maximumChunks: 2);
        Assert.Equal("ef", Assert.Single(secondPage.Chunks).Text);
        Assert.Equal(3, secondPage.ContinuationSequence);
    }

    [Fact]
    public void ReplayCursorExpiresWhenBoundedWindowEvictsIt()
    {
        var coalescer = new StreamingTextCoalescer(
            new StreamingPresentationOptions
            {
                TargetChunkUtf8Bytes = 2,
                MaximumBufferedUtf8Bytes = 8,
                MaxReplayChunks = 2,
                MaxReplayUtf8Bytes = 4
            });

        _ = coalescer.Push("abcdef", DateTimeOffset.UnixEpoch);

        var expired = coalescer.ReplayFrom(0);
        Assert.Equal(
            StreamingPresentationReplayStatus.CursorExpired,
            expired.Status);
        Assert.Empty(expired.Chunks);
        Assert.Equal(1, expired.EarliestAvailableSequence);
        Assert.Equal(0, expired.ContinuationSequence);

        var retained = coalescer.ReplayFrom(expired.EarliestAvailableSequence);
        Assert.Equal("cdef", string.Concat(retained.Chunks.Select(x => x.Text)));
    }

    [Fact]
    public void ReplayReportsAheadCursorAndTerminalCatchUp()
    {
        var coalescer = Coalescer(target: 4, maximum: 8);
        var final = Assert.Single(coalescer.Complete("done"));

        var ahead = coalescer.ReplayFrom(final.Sequence + 2);
        Assert.Equal(
            StreamingPresentationReplayStatus.CursorAhead,
            ahead.Status);
        Assert.Empty(ahead.Chunks);
        Assert.True(ahead.IsComplete);

        var replay = coalescer.ReplayFrom(final.Sequence);
        Assert.Equal("done", Assert.Single(replay.Chunks).Text);
        Assert.True(replay.IsComplete);
        var caughtUp = coalescer.ReplayFrom(replay.ContinuationSequence);
        Assert.Empty(caughtUp.Chunks);
        Assert.Equal(
            StreamingPresentationReplayStatus.Available,
            caughtUp.Status);
        Assert.True(caughtUp.IsComplete);
    }

    [Fact]
    public void ReplayOptionsAndCursorAreBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamingTextCoalescer(
                new StreamingPresentationOptions
                {
                    TargetChunkUtf8Bytes = 8,
                    MaximumBufferedUtf8Bytes = 8,
                    MaxReplayChunks = 0
                }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamingTextCoalescer(
                new StreamingPresentationOptions
                {
                    TargetChunkUtf8Bytes = 8,
                    MaximumBufferedUtf8Bytes = 8,
                    MaxReplayUtf8Bytes = 7
                }));

        var coalescer = Coalescer(target: 4, maximum: 8);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => coalescer.ReplayFrom(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => coalescer.ReplayFrom(0, 513));
    }

    [Fact]
    public void RetrySupersedesPartialAttemptBeforePresentingReplacement()
    {
        var coordinator = AttemptCoordinator();
        var first = Identity(
            "run-1",
            "turn-1",
            "primary",
            "provider-attempt-1",
            "stream-attempt-1");
        var second = Identity(
            "run-1",
            "turn-1",
            "primary",
            "provider-attempt-2",
            "stream-attempt-2");
        var view = new System.Text.StringBuilder();

        Apply(view, coordinator.BeginAttempt(first));
        Apply(
            view,
            coordinator.Push(
                first,
                "wrong partial",
                DateTimeOffset.UnixEpoch));
        Assert.StartsWith("wrong", view.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(view.ToString());

        var superseded = Assert.Single(
            coordinator.ApplyLifecycle(
                "run-1",
                "turn-1",
                new ProviderAttemptNotice
                {
                    Kind = ProviderAttemptNoticeKinds.Retry,
                    ProviderId = "primary",
                    ProviderAttemptId = "provider-attempt-1",
                    StreamAttemptId = "stream-attempt-1",
                    AttemptNumber = 1,
                    ErrorCode = "provider_transient",
                    ErrorCategory = "transport"
                }));
        Apply(view, new[] { superseded });

        Assert.Equal(
            AttemptStreamingPresentationChunkKinds.Superseded,
            superseded.Kind);
        Assert.True(superseded.ReplacesPriorText);
        Assert.Equal(
            "stream-attempt-1",
            superseded.SupersededStreamAttemptId);
        Assert.Empty(view.ToString());

        Apply(view, coordinator.BeginAttempt(second));
        Assert.Empty(
            coordinator.Push(
                first,
                "late stale text",
                DateTimeOffset.UnixEpoch.AddMilliseconds(1)));
        Assert.Empty(coordinator.BeginAttempt(first));
        Apply(
            view,
            coordinator.Push(
                second,
                "authoritative",
                DateTimeOffset.UnixEpoch.AddMilliseconds(2)));
        Apply(view, coordinator.Complete(second, "authoritative B"));

        Assert.Equal("authoritative B", view.ToString());
        Assert.Empty(
            coordinator.BeginAttempt(
                Identity(
                    "run-1",
                    "turn-1",
                    "primary",
                    "provider-attempt-3",
                    "stream-attempt-3")));
        Assert.Equal("authoritative B", view.ToString());
        var replay = coordinator.ReplayFrom("run-1", "turn-1", 0);
        Assert.Contains(
            replay.Chunks,
            chunk => chunk.Kind
                     == AttemptStreamingPresentationChunkKinds.Superseded);
        Assert.All(
            replay.Chunks,
            chunk =>
            {
                Assert.Equal("run-1", chunk.Identity.RunId);
                Assert.Equal("turn-1", chunk.Identity.TurnId);
            });
    }

    [Fact]
    public void LifecycleBeforeDispatchRetiresAttemptWithoutReopeningIt()
    {
        var coordinator = AttemptCoordinator();
        var abandoned = Identity(
            "run-reordered",
            "turn-1",
            "provider",
            "provider-attempt-a",
            "stream-attempt-a");
        var replacement = Identity(
            "run-reordered",
            "turn-1",
            "provider",
            "provider-attempt-b",
            "stream-attempt-b");

        var marker = Assert.Single(
            coordinator.ApplyLifecycle(
                "run-reordered",
                "turn-1",
                new ProviderAttemptNotice
                {
                    Kind = ProviderAttemptNoticeKinds.Retry,
                    ProviderId = "provider",
                    ProviderAttemptId = "provider-attempt-a",
                    StreamAttemptId = "stream-attempt-a",
                    AttemptNumber = 1,
                    ErrorCode = "provider_transient",
                    ErrorCategory = "transport"
                }));

        Assert.Equal(
            AttemptStreamingPresentationChunkKinds.Superseded,
            marker.Kind);
        Assert.Empty(coordinator.BeginAttempt(abandoned));
        Assert.Single(coordinator.BeginAttempt(replacement));
        Assert.Empty(
            coordinator.Push(
                abandoned,
                "late",
                DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void FallbackSupersedesPrimaryAndKeepsProviderIdentity()
    {
        var coordinator = AttemptCoordinator();
        var primary = Identity(
            "run-fallback",
            "turn-1",
            "primary",
            "primary-attempt",
            "primary-stream");
        var fallback = Identity(
            "run-fallback",
            "turn-1",
            "fallback",
            "fallback-attempt",
            "fallback-stream");
        var view = new System.Text.StringBuilder();

        Apply(view, coordinator.BeginAttempt(primary));
        Apply(
            view,
            coordinator.Push(
                primary,
                "primary partial",
                DateTimeOffset.UnixEpoch));
        Apply(
            view,
            coordinator.ApplyLifecycle(
                "run-fallback",
                "turn-1",
                new ProviderAttemptNotice
                {
                    Kind = ProviderAttemptNoticeKinds.Fallback,
                    ProviderId = "primary",
                    NextProviderId = "fallback",
                    ProviderAttemptId = "primary-attempt",
                    StreamAttemptId = "primary-stream",
                    AttemptNumber = 1,
                    ErrorCode = "provider_unavailable",
                    ErrorCategory = "provider"
                }));
        Apply(view, coordinator.BeginAttempt(fallback));
        Apply(view, coordinator.Complete(fallback, "fallback answer"));

        Assert.Equal("fallback answer", view.ToString());
        var chunks = coordinator
            .ReplayFrom("run-fallback", "turn-1", 0)
            .Chunks;
        Assert.Contains(
            chunks,
            chunk => chunk.Identity.ProviderId == "primary"
                     && chunk.Kind
                     == AttemptStreamingPresentationChunkKinds.Superseded);
        Assert.Contains(
            chunks,
            chunk => chunk.Identity.ProviderId == "fallback"
                     && chunk.Kind
                     == AttemptStreamingPresentationChunkKinds.Final);
    }

    [Fact]
    public async Task ConcurrentRunsHaveIndependentAttemptAndSequenceState()
    {
        var coordinator = AttemptCoordinator(maxTrackedTurns: 128);
        var results = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(
                    index => Task.Run(
                        () =>
                        {
                            var runId = "run-" + index;
                            var identity = Identity(
                                runId,
                                "turn-1",
                                "provider",
                                "provider-attempt-" + index,
                                "stream-attempt-" + index);
                            var view = new System.Text.StringBuilder();
                            Apply(
                                view,
                                coordinator.BeginAttempt(identity));
                            Apply(
                                view,
                                coordinator.Push(
                                    identity,
                                    "value-" + index,
                                    DateTimeOffset.UnixEpoch));
                            Apply(
                                view,
                                coordinator.Complete(
                                    identity,
                                    "value-" + index));
                            var replay = coordinator.ReplayFrom(
                                runId,
                                "turn-1",
                                0);
                            return (
                                Index: index,
                                View: view.ToString(),
                                Replay: replay);
                        })));

        foreach (var result in results)
        {
            Assert.Equal("value-" + result.Index, result.View);
            Assert.Equal(0, result.Replay.Chunks[0].Sequence);
            Assert.All(
                result.Replay.Chunks,
                chunk => Assert.Equal(
                    "run-" + result.Index,
                    chunk.Identity.RunId));
        }
    }

    [Fact]
    public void SlowConsumerGetsExplicitExpiredCursorFromBoundedReplay()
    {
        var coordinator = new AttemptSafeStreamingPresentationCoordinator(
            new AttemptSafeStreamingPresentationOptions
            {
                Stream = new StreamingPresentationOptions
                {
                    TargetChunkUtf8Bytes = 1,
                    MaximumBufferedUtf8Bytes = 4,
                    MaxReplayChunks = 4,
                    MaxReplayUtf8Bytes = 4
                },
                MaxReplayChunksPerTurn = 3,
                MaxReplayUtf8BytesPerTurn = 3
            });
        var identity = Identity(
            "run-slow",
            "turn-1",
            "provider",
            "provider-attempt",
            "stream-attempt");

        _ = coordinator.BeginAttempt(identity);
        _ = coordinator.Push(
            identity,
            "abcdef",
            DateTimeOffset.UnixEpoch);
        _ = coordinator.Complete(identity, "abcdef");

        var expired = coordinator.ReplayFrom(
            "run-slow",
            "turn-1",
            0);

        Assert.Equal(
            StreamingPresentationReplayStatus.CursorExpired,
            expired.Status);
        Assert.Empty(expired.Chunks);
        Assert.True(expired.EarliestAvailableSequence > 0);
        var retained = coordinator.ReplayFrom(
            "run-slow",
            "turn-1",
            expired.EarliestAvailableSequence);
        Assert.Equal(
            StreamingPresentationReplayStatus.Available,
            retained.Status);
        Assert.InRange(retained.Chunks.Count, 1, 3);
    }

    [Fact]
    public void CapacityNeverEvictsTurnBetweenSupersedeAndReplacement()
    {
        var coordinator = AttemptCoordinator(maxTrackedTurns: 1);
        var first = Identity(
            "run-capacity-a",
            "turn-1",
            "provider",
            "provider-attempt-a",
            "stream-attempt-a");
        var replacement = Identity(
            "run-capacity-a",
            "turn-1",
            "provider",
            "provider-attempt-b",
            "stream-attempt-b");
        var other = Identity(
            "run-capacity-b",
            "turn-1",
            "provider",
            "provider-attempt-c",
            "stream-attempt-c");

        _ = coordinator.BeginAttempt(first);
        _ = coordinator.Supersede(first, "provider_retry");
        var error = Assert.Throws<RuntimeContentLimitException>(
            () => coordinator.BeginAttempt(other));

        Assert.Equal(
            "stream_tracked_turns_exceeded",
            error.LimitCode);
        Assert.Single(coordinator.BeginAttempt(replacement));
        _ = coordinator.CloseTurn(
            "run-capacity-a",
            "turn-1",
            "run_failed");
        Assert.Single(coordinator.BeginAttempt(other));
    }

    private static AttemptSafeStreamingPresentationCoordinator
        AttemptCoordinator(int maxTrackedTurns = 16)
    {
        return new AttemptSafeStreamingPresentationCoordinator(
            new AttemptSafeStreamingPresentationOptions
            {
                Stream = new StreamingPresentationOptions
                {
                    TargetChunkUtf8Bytes = 4,
                    MaximumBufferedUtf8Bytes = 32,
                    IdleFlushInterval = TimeSpan.FromMilliseconds(50),
                    MaxReplayChunks = 32,
                    MaxReplayUtf8Bytes = 256
                },
                MaxTrackedTurns = maxTrackedTurns,
                MaxReplayChunksPerTurn = 64,
                MaxReplayUtf8BytesPerTurn = 1_024
            });
    }

    private static StreamingPresentationAttemptIdentity Identity(
        string runId,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId)
    {
        return new StreamingPresentationAttemptIdentity(
            runId,
            turnId,
            providerId,
            providerAttemptId,
            streamAttemptId);
    }

    private static void Apply(
        System.Text.StringBuilder view,
        IReadOnlyList<AttemptStreamingPresentationChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.ReplacesPriorText)
            {
                view.Clear();
            }

            view.Append(chunk.Text);
        }
    }

    private static StreamingTextCoalescer Coalescer(
        int target,
        int maximum)
    {
        return new StreamingTextCoalescer(
            new StreamingPresentationOptions
            {
                TargetChunkUtf8Bytes = target,
                MaximumBufferedUtf8Bytes = maximum,
                IdleFlushInterval = TimeSpan.FromMilliseconds(50)
            });
    }
}
