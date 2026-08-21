using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Media.Tests;

public sealed class MediaMetricsTests
{
    [Fact]
    public async Task CollectorMeasuresFirstProgressAndAssetAvailability()
    {
        var now = DateTimeOffset.UnixEpoch;
        var collector = new GameMediaMetricsCollector(clock: () => now);
        var generator = new DelegateGenerator(async (request, progress, cancellationToken) =>
        {
            now = now.AddMilliseconds(8);
            await progress!(new GameMediaGenerationProgress("rendering"), cancellationToken);
            now = now.AddMilliseconds(12);
            return new GameMediaGenerationResult(new[]
            {
                new ResourceContent("memory://image", "image/png"),
            });
        });

        var result = await collector.GenerateAsync(
            generator,
            new GameMediaGenerationRequest("request", GameMediaKind.Image, "{}"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result.Outputs);
        var sample = Assert.Single(collector.Snapshot());
        Assert.True(sample.Succeeded);
        Assert.Equal(8, sample.FirstProgressMilliseconds);
        Assert.Equal(20, sample.AssetAvailableMilliseconds);
    }

    private sealed class DelegateGenerator : IGameMediaGenerator
    {
        private readonly Func<GameMediaGenerationRequest, GameMediaProgressHandler?, CancellationToken, ValueTask<GameMediaGenerationResult>> _run;

        public DelegateGenerator(Func<GameMediaGenerationRequest, GameMediaProgressHandler?, CancellationToken, ValueTask<GameMediaGenerationResult>> run)
        {
            _run = run;
        }

        public ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken) => _run(request, progress, cancellationToken);
    }
}
