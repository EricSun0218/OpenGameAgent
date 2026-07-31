using GameAgent.World;

namespace GameAgent.World.Tests;

public sealed class WorldDeterministicEntropyTests
{
    [Fact]
    public void DeriveDigestMatchesThePortableCanonicalJsonVector()
    {
        var digest = WorldDeterministicEntropy.DeriveDigest(
            WorldDeterministicEntropy.Version1,
            "seed-42",
            "timeline.main",
            "occurrence.7",
            "chance.primary");

        Assert.Equal(
            "a40eba87fd05c868325b1e3c0d8e17dd84acf47482357dc5cb3a7927c5bae29d",
            digest);
    }

    [Fact]
    public void NamedRollsAreStableAndIndependentOfCallOrder()
    {
        var firstA = Roll("a");
        var firstB = Roll("b");
        var secondB = Roll("b");
        var secondA = Roll("a");

        Assert.Equal(firstA, secondA);
        Assert.Equal(firstB, secondB);
        Assert.NotEqual(firstA, firstB);
    }

    [Fact]
    public void TimelineAndOccurrenceIdentityChangeTheRoll()
    {
        var baseline = Digest("timeline.main", "occurrence.1");

        Assert.NotEqual(
            baseline,
            Digest("timeline.branch", "occurrence.1"));
        Assert.NotEqual(
            baseline,
            Digest("timeline.main", "occurrence.2"));
    }

    [Fact]
    public void WeightedSelectionUsesPositiveIntegralWeights()
    {
        var selected = WorldDeterministicEntropy.SelectWeightedIndex(
            WorldDeterministicEntropy.Version1,
            "seed-42",
            "timeline.main",
            "occurrence.7",
            "weighted.primary",
            new long[] { 2, 3, 5 });

        Assert.InRange(selected, 0, 2);
        Assert.Equal(
            selected,
            WorldDeterministicEntropy.SelectWeightedIndex(
                WorldDeterministicEntropy.Version1,
                "seed-42",
                "timeline.main",
                "occurrence.7",
                "weighted.primary",
                new long[] { 2, 3, 5 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SampleRejectsNonPositiveUpperBounds(long upperBound)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorldDeterministicEntropy.SampleInt64(
                WorldDeterministicEntropy.Version1,
                "seed",
                "timeline",
                "occurrence",
                "roll",
                upperBound));
    }

    [Fact]
    public void WeightedSelectionRejectsInvalidOrOverflowingWeights()
    {
        Assert.Throws<ArgumentException>(
            () => Select(new long[] { 1, 0 }));
        Assert.Throws<ArgumentException>(
            () => Select(new[] { long.MaxValue, 1L }));
    }

    private static long Roll(string key)
    {
        return WorldDeterministicEntropy.SampleInt64(
            WorldDeterministicEntropy.Version1,
            "seed",
            "timeline",
            "occurrence",
            key,
            1_000_000);
    }

    private static string Digest(
        string timelineId,
        string occurrenceId)
    {
        return WorldDeterministicEntropy.DeriveDigest(
            WorldDeterministicEntropy.Version1,
            "seed",
            timelineId,
            occurrenceId,
            "roll");
    }

    private static int Select(IReadOnlyList<long> weights)
    {
        return WorldDeterministicEntropy.SelectWeightedIndex(
            WorldDeterministicEntropy.Version1,
            "seed",
            "timeline",
            "occurrence",
            "roll",
            weights);
    }
}
