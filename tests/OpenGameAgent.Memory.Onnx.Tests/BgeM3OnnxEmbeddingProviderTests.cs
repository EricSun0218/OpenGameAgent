using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OpenGameAgent.Memory;
using OpenGameAgent.Memory.Onnx;
using OpenGameAgent.Persistence;
using Xunit;

namespace OpenGameAgent.Memory.Onnx.Tests;

public sealed class BgeM3OnnxEmbeddingProviderTests
{
    [Fact]
    public async Task QueryAndDocumentPathsAreBoundedBatchedAndObservable()
    {
        using var directory = new TemporaryDirectory();
        var metrics = new RecordingMetrics();
        var inference = new FakeInference();
        await using var provider = Provider(
            directory.Path,
            new LengthTokenizer(maximumTokens: 8),
            inference,
            metrics,
            maximumBatchSize: 2,
            maximumBatchTokens: 12);

        var query = await provider.EmbedQueryAsync("query", TestCancellation);
        var documents = await provider.EmbedDocumentsAsync(
            new[] { "one", "two", "three" },
            TestCancellation);

        Assert.Equal(1_024, query.Length);
        Assert.Equal(3, documents.Count);
        Assert.Equal(new[] { 1, 2, 1 }, inference.BatchSizes);
        Assert.Collection(
            metrics.Items,
            value => Assert.Equal(OnnxEmbeddingOperationKind.Query, value.Operation),
            value =>
            {
                Assert.Equal(OnnxEmbeddingOperationKind.Documents, value.Operation);
                Assert.Equal(3, value.TextCount);
                Assert.Equal(2, value.InferenceBatchCount);
                Assert.Equal(2, value.MaximumInferenceBatchSize);
                Assert.True(value.TokenCount > 0);
                Assert.True(value.Succeeded);
            });
        Assert.DoesNotContain(
            typeof(OnnxEmbeddingOperationMetrics).GetProperties(),
            property => property.Name.Contains("Text", StringComparison.OrdinalIgnoreCase)
                        && property.PropertyType == typeof(string));
    }

    [Fact]
    public async Task ProviderIntegratesWithVectorMemoryWithoutChangingAuthoritativeMemory()
    {
        using var directory = new TemporaryDirectory();
        await using var provider = Provider(
            directory.Path,
            new SemanticTokenizer(),
            new SemanticInference());
        var authoritative = new InMemoryGameMemoryStore();
        await using var store = new VectorMemoryStore(
            authoritative,
            new InMemoryVectorMemoryIndex(),
            provider);
        var memory = new GameMemory(
            "memory-1",
            "session",
            "actor",
            "world",
            GameMemoryKind.Event,
            "{\"subject\":\"feline\"}",
            new GameMoment("timeline", 1),
            searchableText: "a feline guard");

        await store.AppendAsync(memory, TestCancellation);
        var result = await store.SearchAsync(
            new GameMemoryQuery("session", 4, ownerId: "actor", text: "cat"),
            TestCancellation);

        Assert.Equal("memory-1", Assert.Single(result).MemoryId);
        Assert.Equal("memory-1", (await authoritative.SearchAsync(
            new GameMemoryQuery("session", 4, ownerId: "actor", text: "feline"),
            TestCancellation)).Single().MemoryId);
    }

    [Fact]
    public async Task FullQueueRejectsWithoutStartingAnotherInference()
    {
        using var directory = new TemporaryDirectory();
        var inference = new BlockingInference();
        await using var provider = Provider(
            directory.Path,
            new LengthTokenizer(),
            inference,
            maximumConcurrent: 1,
            maximumQueued: 0);
        var first = provider.EmbedQueryAsync("first", TestCancellation).AsTask();
        Assert.True(inference.Entered.Wait(TimeSpan.FromSeconds(2), TestCancellation));

        var exception = await Assert.ThrowsAsync<OnnxEmbeddingException>(
            () => provider.EmbedQueryAsync("second", TestCancellation).AsTask());

        Assert.Equal(OnnxEmbeddingFailureKind.QueueFull, exception.Failure);
        Assert.Equal(1, inference.Calls);
        inference.Release.Set();
        await first;
    }

    [Fact]
    public async Task QueuedOperationTimesOutWithoutReachingInference()
    {
        using var directory = new TemporaryDirectory();
        var inference = new BlockingInference();
        await using var provider = Provider(
            directory.Path,
            new LengthTokenizer(),
            inference,
            maximumConcurrent: 1,
            maximumQueued: 1,
            queueTimeout: TimeSpan.FromMilliseconds(30));
        var first = provider.EmbedQueryAsync("first", TestCancellation).AsTask();
        Assert.True(inference.Entered.Wait(TimeSpan.FromSeconds(2), TestCancellation));

        var exception = await Assert.ThrowsAsync<OnnxEmbeddingException>(
            () => provider.EmbedQueryAsync("second", TestCancellation).AsTask());

        Assert.Equal(OnnxEmbeddingFailureKind.QueueTimeout, exception.Failure);
        Assert.Equal(1, inference.Calls);
        inference.Release.Set();
        await first;
    }

    [Fact]
    public async Task DisposeCancelsActiveInferenceAndIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var inference = new BlockingInference();
        var provider = Provider(directory.Path, new LengthTokenizer(), inference);
        var operation = provider.EmbedQueryAsync("active", TestCancellation).AsTask();
        Assert.True(inference.Entered.Wait(TimeSpan.FromSeconds(2), TestCancellation));

        await provider.DisposeAsync();
        await provider.DisposeAsync();

        var exception = await Assert.ThrowsAsync<OnnxEmbeddingException>(() => operation);
        Assert.Equal(OnnxEmbeddingFailureKind.Cancelled, exception.Failure);
        Assert.True(inference.Disposed);
    }

    [Fact]
    public async Task ObserverFailureNeverChangesEmbeddingResult()
    {
        using var directory = new TemporaryDirectory();
        await using var provider = Provider(
            directory.Path,
            new LengthTokenizer(),
            new FakeInference(),
            new ThrowingMetrics());

        var vector = await provider.EmbedQueryAsync("safe", TestCancellation);

        Assert.Equal(1_024, vector.Length);
    }

    [Fact]
    public void XlmRobertaMappingAddsFairseqOffsetAndSpecialTokens()
    {
        var mapped = XlmRobertaSentencePieceTokenizer.MapSentencePieceIds(
            new[] { 0, 1, 41 },
            3);

        Assert.Equal(new long[] { 0, 3, 2, 42, 2 }, mapped);
    }

    [Fact]
    public void ManifestValidationRequiresAllFilesAndChecksIntegrity()
    {
        using var directory = new TemporaryDirectory();
        WriteValidManifest(directory.Path);
        var options = Options(directory.Path);
        var validated = BgeM3ValidatedOptions.Create(options);

        var manifest = BgeM3ModelLoader.ValidateManifest(validated, TestCancellation);

        Assert.Equal(1_024, manifest.Dimensions);
        Assert.Equal(4, manifest.Files.Count);
        Assert.All(manifest.Files, file => Assert.Equal(64, file.Sha256.Length));

        var mismatch = Options(directory.Path);
        mismatch.ExpectedSha256 = new Dictionary<string, string>
        {
            [OnnxEmbeddingGuards.OnnxModelPath] = new string('0', 64),
        };
        var exception = Assert.Throws<OnnxEmbeddingException>(
            () => BgeM3ModelLoader.ValidateManifest(
                BgeM3ValidatedOptions.Create(mismatch),
                TestCancellation));
        Assert.Equal(OnnxEmbeddingFailureKind.ModelIntegrity, exception.Failure);
    }

    [Fact]
    public void MissingManifestFileFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        WriteValidManifest(directory.Path);
        File.Delete(System.IO.Path.Combine(directory.Path, "config.json"));

        var exception = Assert.Throws<OnnxEmbeddingException>(
            () => BgeM3ModelLoader.ValidateManifest(
                BgeM3ValidatedOptions.Create(Options(directory.Path)),
                TestCancellation));

        Assert.Equal(OnnxEmbeddingFailureKind.MissingModelFile, exception.Failure);
    }

    [Fact]
    public void PublicApiExposesProviderWithoutAnAlternateMemoryStore()
    {
        var exported = typeof(BgeM3OnnxEmbeddingProvider).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("OpenGameAgent.Memory.Onnx.BgeM3OnnxEmbeddingProvider", exported);
        Assert.DoesNotContain(exported, value => value?.Contains("VectorMemoryStore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RealBgeM3ModelProducesNormalizedDistinctVectorsWhenConfigured()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("OGA_BGE_M3_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
        {
            return;
        }

        var options = Options(modelDirectory);
        options.MaximumTokens = 64;
        options.MaximumBatchSize = 2;
        options.MaximumBatchTokens = 128;
        options.LoadTimeout = TimeSpan.FromMinutes(5);
        options.InferenceTimeout = TimeSpan.FromMinutes(2);
        await using var provider = await BgeM3OnnxEmbeddingProvider.CreateAsync(options, TestCancellation);

        var vectors = await provider.EmbedDocumentsAsync(
            new[] { "一只猫守在门口", "一只小猫正在看门", "今天市场里的铁矿价格上涨" },
            TestCancellation);

        Assert.All(vectors, vector => Assert.InRange(Norm(vector.Span), 0.999, 1.001));
        Assert.True(Dot(vectors[0].Span, vectors[1].Span) > Dot(vectors[0].Span, vectors[2].Span));
    }

    private static BgeM3OnnxEmbeddingProvider Provider(
        string directory,
        IBgeM3Tokenizer tokenizer,
        IBgeM3InferenceSession inference,
        IOnnxEmbeddingMetricsSink? metrics = null,
        int maximumBatchSize = 16,
        int maximumBatchTokens = 32_768,
        int maximumConcurrent = 1,
        int maximumQueued = 16,
        TimeSpan? queueTimeout = null)
    {
        var options = Options(directory);
        options.MaximumBatchSize = maximumBatchSize;
        options.MaximumBatchTokens = maximumBatchTokens;
        options.MaximumConcurrentInferences = maximumConcurrent;
        options.MaximumQueuedOperations = maximumQueued;
        options.QueueTimeout = queueTimeout ?? TimeSpan.FromSeconds(1);
        options.InferenceTimeout = TimeSpan.FromSeconds(2);
        options.Metrics = metrics ?? NullOnnxEmbeddingMetricsSink.Instance;
        var validated = BgeM3ValidatedOptions.Create(options);
        return new BgeM3OnnxEmbeddingProvider(validated, Manifest(), tokenizer, inference);
    }

    private static BgeM3OnnxEmbeddingOptions Options(string directory) => new(directory)
    {
        MaximumTokens = 8,
        MaximumBatchTokens = 32,
    };

    private static OnnxEmbeddingModelManifest Manifest() => new(
        "BAAI/bge-m3",
        "test",
        "test-preprocessing",
        1_024,
        8,
        OnnxEmbeddingGuards.RequiredPaths.Select(path =>
            new OnnxEmbeddingModelFile(path, 1, new string('a', 64))).ToArray());

    private static void WriteValidManifest(string root)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(root, "onnx"));
        File.WriteAllText(
            System.IO.Path.Combine(root, "config.json"),
            "{\"_name_or_path\":\"BAAI/bge-m3\",\"model_type\":\"xlm-roberta\",\"hidden_size\":1024,\"bos_token_id\":0,\"pad_token_id\":1,\"eos_token_id\":2,\"type_vocab_size\":1,\"vocab_size\":250002,\"max_position_embeddings\":8194}");
        File.WriteAllText(
            System.IO.Path.Combine(root, "tokenizer_config.json"),
            "{\"tokenizer_class\":\"XLMRobertaTokenizer\",\"model_max_length\":8192}");
        File.WriteAllBytes(System.IO.Path.Combine(root, "sentencepiece.bpe.model"), new byte[] { 1 });
        File.WriteAllBytes(System.IO.Path.Combine(root, "onnx", "model_int8.onnx"), new byte[] { 2 });
    }

    private static double Norm(ReadOnlySpan<float> vector)
    {
        var result = 0d;
        foreach (var value in vector)
        {
            result += value * value;
        }

        return Math.Sqrt(result);
    }

    private static double Dot(ReadOnlySpan<float> first, ReadOnlySpan<float> second)
    {
        var result = 0d;
        for (var index = 0; index < first.Length; index++)
        {
            result += first[index] * second[index];
        }

        return result;
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private sealed class LengthTokenizer : IBgeM3Tokenizer
    {
        private readonly int _maximumTokens;

        internal LengthTokenizer(int maximumTokens = 8)
        {
            _maximumTokens = maximumTokens;
        }

        public BgeM3TokenizedText Encode(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(_maximumTokens, Math.Max(3, text.Length));
            return new BgeM3TokenizedText(
                Enumerable.Range(0, count).Select(value => (long)value).ToArray(),
                text.Length > _maximumTokens);
        }
    }

    private sealed class SemanticTokenizer : IBgeM3Tokenizer
    {
        public BgeM3TokenizedText Encode(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var semantic = text.Contains("cat", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("feline", StringComparison.OrdinalIgnoreCase)
                ? 1L
                : 2L;
            return new BgeM3TokenizedText(new[] { 0L, semantic, 2L }, false);
        }
    }

    private class FakeInference : IBgeM3InferenceSession
    {
        private readonly ConcurrentQueue<int> _batchSizes = new();

        internal int[] BatchSizes => _batchSizes.ToArray();

        public virtual IReadOnlyList<ReadOnlyMemory<float>> Run(
            IReadOnlyList<BgeM3TokenizedText> texts,
            long maximumTensorBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _batchSizes.Enqueue(texts.Count);
            return texts.Select(_ => UnitVector(0)).ToArray();
        }

        public virtual void Dispose()
        {
        }
    }

    private sealed class SemanticInference : FakeInference
    {
        public override IReadOnlyList<ReadOnlyMemory<float>> Run(
            IReadOnlyList<BgeM3TokenizedText> texts,
            long maximumTensorBytes,
            CancellationToken cancellationToken) =>
            texts.Select(value => UnitVector((int)value.InputIds[1])).ToArray();
    }

    private sealed class BlockingInference : FakeInference
    {
        internal ManualResetEventSlim Entered { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        internal int Calls { get; private set; }

        internal bool Disposed { get; private set; }

        public override IReadOnlyList<ReadOnlyMemory<float>> Run(
            IReadOnlyList<BgeM3TokenizedText> texts,
            long maximumTensorBytes,
            CancellationToken cancellationToken)
        {
            Calls++;
            Entered.Set();
            Release.Wait(cancellationToken);
            return base.Run(texts, maximumTensorBytes, cancellationToken);
        }

        public override void Dispose()
        {
            Disposed = true;
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class RecordingMetrics : IOnnxEmbeddingMetricsSink
    {
        internal List<OnnxEmbeddingOperationMetrics> Items { get; } = new();

        public void Observe(OnnxEmbeddingOperationMetrics metrics) => Items.Add(metrics);
    }

    private sealed class ThrowingMetrics : IOnnxEmbeddingMetricsSink
    {
        public void Observe(OnnxEmbeddingOperationMetrics metrics) => throw new InvalidOperationException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oga-onnx-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static ReadOnlyMemory<float> UnitVector(int index)
    {
        var vector = new float[1_024];
        vector[index] = 1;
        return vector;
    }
}
