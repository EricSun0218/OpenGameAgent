using System.Collections.ObjectModel;
using System.Diagnostics;

namespace OpenGameAgent.Memory.Onnx;

public sealed class BgeM3OnnxEmbeddingProvider : IMemoryEmbeddingProvider
{
    private readonly object _lifecycleGate = new();
    private readonly BgeM3ValidatedOptions _options;
    private readonly IBgeM3Tokenizer _tokenizer;
    private readonly IBgeM3InferenceSession _inference;
    private readonly SemaphoreSlim _admission;
    private readonly SemaphoreSlim _execution;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool>? _idle;
    private int _activeOperations;
    private bool _disposed;

    private BgeM3OnnxEmbeddingProvider(BgeM3ValidatedOptions options, BgeM3LoadedModel loaded)
        : this(options, loaded.Manifest, loaded.Tokenizer, loaded.Inference)
    {
    }

    internal BgeM3OnnxEmbeddingProvider(
        BgeM3ValidatedOptions options,
        OnnxEmbeddingModelManifest manifest,
        IBgeM3Tokenizer tokenizer,
        IBgeM3InferenceSession inference)
    {
        _options = options;
        Manifest = manifest;
        _tokenizer = tokenizer;
        _inference = inference;
        _admission = new SemaphoreSlim(
            checked(options.MaximumConcurrentInferences + options.MaximumQueuedOperations),
            checked(options.MaximumConcurrentInferences + options.MaximumQueuedOperations));
        _execution = new SemaphoreSlim(
            options.MaximumConcurrentInferences,
            options.MaximumConcurrentInferences);
        Identity = new MemoryEmbeddingIdentity(
            options.ProviderId,
            manifest.ModelId,
            $"{manifest.Version}+{manifest.PreprocessingIdentity}",
            manifest.Dimensions);
    }

    public MemoryEmbeddingIdentity Identity { get; }

    public OnnxEmbeddingModelManifest Manifest { get; }

    public static async ValueTask<BgeM3OnnxEmbeddingProvider> CreateAsync(
        BgeM3OnnxEmbeddingOptions options,
        CancellationToken cancellationToken = default)
    {
        var validated = BgeM3ValidatedOptions.Create(options);
        var started = Stopwatch.StartNew();
        BgeM3LoadedModel? loaded = null;
        OnnxEmbeddingFailureKind failure = OnnxEmbeddingFailureKind.None;
        using var timeout = new CancellationTokenSource(validated.LoadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var loadTask = Task.Run(
            () => BgeM3ModelLoader.Load(validated, linked.Token),
            CancellationToken.None);
        try
        {
            loaded = await loadTask.ConfigureAwait(false);
            return new BgeM3OnnxEmbeddingProvider(validated, loaded);
        }
        catch (OperationCanceledException exception)
        {
            failure = cancellationToken.IsCancellationRequested
                ? OnnxEmbeddingFailureKind.Cancelled
                : OnnxEmbeddingFailureKind.Timeout;
            _ = loadTask.ContinueWith(
                static task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        task.Result.Dispose();
                    }

                    _ = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new OnnxEmbeddingException(
                failure,
                failure == OnnxEmbeddingFailureKind.Timeout
                    ? "The local ONNX embedding model exceeded its load deadline."
                    : "The local ONNX embedding model load was cancelled.",
                exception);
        }
        catch (OnnxEmbeddingException exception)
        {
            failure = exception.Failure;
            throw;
        }
        catch (Exception exception)
        {
            failure = OnnxEmbeddingFailureKind.UnsupportedModel;
            throw new OnnxEmbeddingException(
                failure,
                "The local ONNX embedding model could not be initialized.",
                exception);
        }
        finally
        {
            Observe(
                validated.Metrics,
                new OnnxEmbeddingOperationMetrics(
                    OnnxEmbeddingOperationKind.ModelLoad,
                    failure,
                    started.Elapsed.TotalMilliseconds));
        }
    }

    public ValueTask<ReadOnlyMemory<float>> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return EmbedQueryCoreAsync(text, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (texts is null)
        {
            throw new ArgumentNullException(nameof(texts));
        }

        if (texts.Count is < 1 || texts.Count > _options.MaximumTextsPerCall || texts.Any(value => value is null))
        {
            throw new ArgumentException("A bounded non-null document batch is required.", nameof(texts));
        }

        return await RunAsync(OnnxEmbeddingOperationKind.Documents, texts, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task? wait;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            if (_activeOperations == 0)
            {
                wait = null;
            }
            else
            {
                _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _idle.Task;
            }
        }

        if (wait is not null)
        {
            await wait.ConfigureAwait(false);
        }

        _inference.Dispose();
        _execution.Dispose();
        _admission.Dispose();
        _lifetime.Dispose();
    }

    private async ValueTask<ReadOnlyMemory<float>> EmbedQueryCoreAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var results = await RunAsync(
            OnnxEmbeddingOperationKind.Query,
            new[] { text },
            cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    private async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> RunAsync(
        OnnxEmbeddingOperationKind kind,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        BeginOperation();
        var total = Stopwatch.StartNew();
        var queueMilliseconds = 0d;
        var tokenizationMilliseconds = 0d;
        var inferenceMilliseconds = 0d;
        var tokenCount = 0;
        var truncated = 0;
        var inferenceBatches = 0;
        var maximumInferenceBatch = 0;
        var failure = OnnxEmbeddingFailureKind.None;
        var admitted = false;
        var executing = false;
        try
        {
            ValidateTexts(texts);
            if (!_admission.Wait(0))
            {
                failure = OnnxEmbeddingFailureKind.QueueFull;
                throw new OnnxEmbeddingException(failure, "The local embedding queue is full.");
            }

            admitted = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var queue = Stopwatch.StartNew();
            var acquired = await _execution.WaitAsync(_options.QueueTimeout, linked.Token).ConfigureAwait(false);
            queueMilliseconds = queue.Elapsed.TotalMilliseconds;
            if (!acquired)
            {
                failure = OnnxEmbeddingFailureKind.QueueTimeout;
                throw new OnnxEmbeddingException(failure, "The local embedding queue exceeded its deadline.");
            }

            executing = true;
            using var inferenceTimeout = new CancellationTokenSource(_options.InferenceTimeout);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                linked.Token,
                inferenceTimeout.Token);
            var execution = await Task.Run(
                () => Execute(
                    texts,
                    operation.Token,
                    out tokenizationMilliseconds,
                    out inferenceMilliseconds,
                    out tokenCount,
                    out truncated,
                    out inferenceBatches,
                    out maximumInferenceBatch),
                CancellationToken.None).ConfigureAwait(false);
            return new ReadOnlyCollection<ReadOnlyMemory<float>>(execution);
        }
        catch (OperationCanceledException exception)
        {
            failure = cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested
                ? OnnxEmbeddingFailureKind.Cancelled
                : OnnxEmbeddingFailureKind.Timeout;
            throw new OnnxEmbeddingException(
                failure,
                failure == OnnxEmbeddingFailureKind.Timeout
                    ? "The local embedding operation exceeded its deadline."
                    : "The local embedding operation was cancelled.",
                exception);
        }
        catch (OnnxEmbeddingException exception)
        {
            failure = exception.Failure;
            throw;
        }
        catch (Exception exception)
        {
            failure = OnnxEmbeddingFailureKind.Inference;
            throw new OnnxEmbeddingException(
                failure,
                "The local embedding operation failed.",
                exception);
        }
        finally
        {
            if (executing)
            {
                _execution.Release();
            }

            if (admitted)
            {
                _admission.Release();
            }

            EndOperation();
            Observe(
                _options.Metrics,
                new OnnxEmbeddingOperationMetrics(
                    kind,
                    failure,
                    total.Elapsed.TotalMilliseconds,
                    queueMilliseconds,
                    tokenizationMilliseconds,
                    inferenceMilliseconds,
                    texts.Count,
                    inferenceBatches,
                    maximumInferenceBatch,
                    tokenCount,
                    truncated));
        }
    }

    private ReadOnlyMemory<float>[] Execute(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken,
        out double tokenizationMilliseconds,
        out double inferenceMilliseconds,
        out int tokenCount,
        out int truncated,
        out int inferenceBatches,
        out int maximumInferenceBatch)
    {
        var tokenization = Stopwatch.StartNew();
        var encoded = new BgeM3TokenizedText[texts.Count];
        tokenCount = 0;
        truncated = 0;
        try
        {
            for (var index = 0; index < texts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                encoded[index] = _tokenizer.Encode(texts[index], cancellationToken);
                tokenCount = checked(tokenCount + encoded[index].InputIds.Length);
                if (encoded[index].Truncated)
                {
                    truncated++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OnnxEmbeddingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.Tokenization,
                "The local embedding tokenizer failed.",
                exception);
        }
        finally
        {
            tokenizationMilliseconds = tokenization.Elapsed.TotalMilliseconds;
        }

        if (tokenCount > checked(_options.MaximumTextsPerCall * _options.MaximumTokens))
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.InvalidConfiguration,
                "The local embedding call exceeds its configured token boundary.");
        }

        var output = new ReadOnlyMemory<float>[texts.Count];
        var inference = Stopwatch.StartNew();
        inferenceBatches = 0;
        maximumInferenceBatch = 0;
        try
        {
            var start = 0;
            while (start < encoded.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = 0;
                var batchTokens = 0;
                while (start + count < encoded.Length && count < _options.MaximumBatchSize)
                {
                    var next = encoded[start + count].InputIds.Length;
                    if (count > 0 && checked(batchTokens + next) > _options.MaximumBatchTokens)
                    {
                        break;
                    }

                    batchTokens = checked(batchTokens + next);
                    count++;
                }

                var batch = new ArraySegment<BgeM3TokenizedText>(encoded, start, count).ToArray();
                var vectors = _inference.Run(batch, _options.MaximumTensorBytes, cancellationToken);
                if (vectors.Count != count)
                {
                    throw new OnnxEmbeddingException(
                        OnnxEmbeddingFailureKind.InvalidOutput,
                        "The local embedding model returned an invalid batch size.");
                }

                for (var index = 0; index < count; index++)
                {
                    output[start + index] = vectors[index];
                }

                inferenceBatches++;
                maximumInferenceBatch = Math.Max(maximumInferenceBatch, count);
                start += count;
            }
        }
        finally
        {
            inferenceMilliseconds = inference.Elapsed.TotalMilliseconds;
        }

        return output;
    }

    private void ValidateTexts(IReadOnlyList<string> texts)
    {
        foreach (var text in texts)
        {
            if (text.Length > _options.MaximumCharactersPerText || text.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Embedding text exceeds its configured character boundary.", nameof(texts));
            }
        }
    }

    private void BeginOperation()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                throw new OnnxEmbeddingException(
                    OnnxEmbeddingFailureKind.Disposed,
                    "The local embedding provider is disposed.");
            }

            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_lifecycleGate)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0)
            {
                idle = _idle;
            }
        }

        idle?.TrySetResult(true);
    }

    private static void Observe(IOnnxEmbeddingMetricsSink sink, OnnxEmbeddingOperationMetrics metrics)
    {
        try
        {
            sink.Observe(metrics);
        }
        catch
        {
            // Observation must never change inference results or expose input content through a secondary failure.
        }
    }
}

internal sealed class BgeM3ValidatedOptions
{
    private BgeM3ValidatedOptions(BgeM3OnnxEmbeddingOptions source)
    {
        ModelDirectory = Path.GetFullPath(source.ModelDirectory);
        ProviderId = OnnxEmbeddingGuards.Id(source.ProviderId, nameof(source.ProviderId), 256);
        ModelVersion = source.ModelVersion is null
            ? null
            : OnnxEmbeddingGuards.Id(source.ModelVersion, nameof(source.ModelVersion), 128);
        MaximumTokens = Range(source.MaximumTokens, 3, 8_192, nameof(source.MaximumTokens));
        MaximumCharactersPerText = Range(
            source.MaximumCharactersPerText,
            1,
            1_000_000,
            nameof(source.MaximumCharactersPerText));
        MaximumTextsPerCall = Range(source.MaximumTextsPerCall, 1, 4_096, nameof(source.MaximumTextsPerCall));
        MaximumBatchSize = Range(source.MaximumBatchSize, 1, 256, nameof(source.MaximumBatchSize));
        MaximumBatchTokens = Range(source.MaximumBatchTokens, MaximumTokens, 1_048_576, nameof(source.MaximumBatchTokens));
        MaximumTensorBytes = Range(source.MaximumTensorBytes, 4_194_304, 2_147_483_648, nameof(source.MaximumTensorBytes));
        MaximumModelBytes = Range(source.MaximumModelBytes, 1_048_576, 4_294_967_296, nameof(source.MaximumModelBytes));
        MaximumConcurrentInferences = Range(
            source.MaximumConcurrentInferences,
            1,
            32,
            nameof(source.MaximumConcurrentInferences));
        MaximumQueuedOperations = Range(
            source.MaximumQueuedOperations,
            0,
            4_096,
            nameof(source.MaximumQueuedOperations));
        QueueTimeout = Duration(source.QueueTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(5), nameof(source.QueueTimeout));
        InferenceTimeout = Duration(source.InferenceTimeout, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(30), nameof(source.InferenceTimeout));
        LoadTimeout = Duration(source.LoadTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(30), nameof(source.LoadTimeout));
        IntraOpThreads = Range(source.IntraOpThreads, 0, 256, nameof(source.IntraOpThreads));
        InterOpThreads = Range(source.InterOpThreads, 1, 256, nameof(source.InterOpThreads));
        ExpectedFileBytes = CopyBytes(source.ExpectedFileBytes);
        ExpectedSha256 = CopyHashes(source.ExpectedSha256);
        Metrics = source.Metrics ?? throw new ArgumentNullException(nameof(source.Metrics));

        if (!Directory.Exists(ModelDirectory))
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.InvalidConfiguration,
                "The configured local model directory does not exist.");
        }
    }

    internal string ModelDirectory { get; }

    internal string ProviderId { get; }

    internal string? ModelVersion { get; }

    internal int MaximumTokens { get; }

    internal int MaximumCharactersPerText { get; }

    internal int MaximumTextsPerCall { get; }

    internal int MaximumBatchSize { get; }

    internal int MaximumBatchTokens { get; }

    internal long MaximumTensorBytes { get; }

    internal long MaximumModelBytes { get; }

    internal int MaximumConcurrentInferences { get; }

    internal int MaximumQueuedOperations { get; }

    internal TimeSpan QueueTimeout { get; }

    internal TimeSpan InferenceTimeout { get; }

    internal TimeSpan LoadTimeout { get; }

    internal int IntraOpThreads { get; }

    internal int InterOpThreads { get; }

    internal IReadOnlyDictionary<string, long> ExpectedFileBytes { get; }

    internal IReadOnlyDictionary<string, string> ExpectedSha256 { get; }

    internal IOnnxEmbeddingMetricsSink Metrics { get; }

    internal static BgeM3ValidatedOptions Create(BgeM3OnnxEmbeddingOptions options) =>
        new(options ?? throw new ArgumentNullException(nameof(options)));

    private static int Range(int value, int minimum, int maximum, string parameterName) =>
        value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static long Range(long value, long minimum, long maximum, string parameterName) =>
        value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static TimeSpan Duration(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string parameterName) =>
        value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static IReadOnlyDictionary<string, long> CopyBytes(IReadOnlyDictionary<string, long>? source)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (source is null)
        {
            return new ReadOnlyDictionary<string, long>(result);
        }

        foreach (var pair in source)
        {
            var path = NormalizeExpectedPath(pair.Key);
            if (pair.Value < 1 || !result.TryAdd(path, pair.Value))
            {
                throw new ArgumentException("Expected model file sizes must be positive and unique.", nameof(source));
            }
        }

        return new ReadOnlyDictionary<string, long>(result);
    }

    private static IReadOnlyDictionary<string, string> CopyHashes(IReadOnlyDictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null)
        {
            return new ReadOnlyDictionary<string, string>(result);
        }

        foreach (var pair in source)
        {
            var path = NormalizeExpectedPath(pair.Key);
            var hash = pair.Value?.ToLowerInvariant() ?? string.Empty;
            if (!OnnxEmbeddingGuards.IsSha256(hash) || !result.TryAdd(path, hash))
            {
                throw new ArgumentException("Expected model hashes must be lowercase SHA-256 values with unique paths.", nameof(source));
            }
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static string NormalizeExpectedPath(string value)
    {
        var normalized = value?.Replace('\\', '/') ?? string.Empty;
        if (!OnnxEmbeddingGuards.RequiredPaths.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Integrity expectations may reference only required model files.", nameof(value));
        }

        return normalized;
    }
}
