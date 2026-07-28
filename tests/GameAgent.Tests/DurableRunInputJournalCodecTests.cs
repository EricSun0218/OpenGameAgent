using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class DurableRunInputJournalCodecTests
{
    [Fact]
    public void AggregateInputIsRejectedByExactBoundedEncoding()
    {
        var content = ProtocolJson.ParseElement(
            "\"" + new string('x', 60_000) + "\"");
        var context = Enumerable.Range(0, 5)
            .Select(
                index => new ContextCandidate(
                    $"candidate-{index}",
                    "world",
                    content))
            .ToArray();

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => DurableRunInputJournalCodec.Encode(
                context,
                Array.Empty<SkillReference>()));

        Assert.Equal(
            "durable_run_input_bytes_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void ResourceMetadataOverheadIsIncludedInAggregateLimit()
    {
        var context = Enumerable.Range(
                0,
                DurableRunInputJournalCodec.MaxContextCandidates)
            .Select(
                index => new ContextCandidate(
                    $"resource-{index}",
                    "world",
                    new ContextResourceReference(
                        "memory://" + new string('u', 330),
                        "x",
                        sizeBytes: long.MaxValue),
                    estimatedTokens: int.MaxValue,
                    expiresAt: DateTimeOffset.UnixEpoch,
                    provenance: "p"))
            .ToArray();

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => DurableRunInputJournalCodec.ValidateEncodedSize(
                context,
                Array.Empty<SkillReference>()));

        Assert.Equal(
            "durable_run_input_bytes_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void AggregateJsonNodeLimitMatchesCommittedEncoding()
    {
        var content = ProtocolJson.ParseElement(
            "[" + string.Join(",", Enumerable.Repeat("0", 2_000)) + "]");
        var context = Enumerable.Range(0, 5)
            .Select(
                index => new ContextCandidate(
                    $"candidate-{index}",
                    "world",
                    content))
            .ToArray();

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => DurableRunInputJournalCodec.ValidateEncodedSize(
                context,
                Array.Empty<SkillReference>()));

        Assert.Equal("json_nodes_exceeded", error.LimitCode);
    }

    [Fact]
    public void MisreportedCountIsNotReadAndEnumerationRemainsBounded()
    {
        var candidate = new ContextCandidate(
            "candidate",
            "world",
            ProtocolJson.ParseElement("""{"value":1}"""));
        var context =
            new MisreportedInfiniteReadOnlyList<ContextCandidate>(candidate);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => DurableRunInputJournalCodec.Encode(
                context,
                Array.Empty<SkillReference>()));

        Assert.Equal("context_candidate_count_exceeded", error.LimitCode);
    }

    private sealed class MisreportedInfiniteReadOnlyList<T>
        : IReadOnlyList<T>
    {
        private readonly T _item;

        public MisreportedInfiniteReadOnlyList(T item)
        {
            _item = item;
        }

        public int Count =>
            throw new InvalidOperationException("Count must not be read.");

        public T this[int index] => _item;

        public IEnumerator<T> GetEnumerator()
        {
            while (true)
            {
                yield return _item;
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
