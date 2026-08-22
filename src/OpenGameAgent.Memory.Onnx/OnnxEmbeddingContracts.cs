using System.Collections.ObjectModel;

namespace OpenGameAgent.Memory.Onnx;

public enum OnnxEmbeddingOperationKind
{
    ModelLoad,
    Query,
    Documents,
}

public enum OnnxEmbeddingFailureKind
{
    None,
    InvalidConfiguration,
    MissingModelFile,
    ModelIntegrity,
    UnsupportedModel,
    QueueFull,
    QueueTimeout,
    Timeout,
    Cancelled,
    Tokenization,
    Inference,
    InvalidOutput,
    Disposed,
}

public sealed class OnnxEmbeddingOperationMetrics
{
    public OnnxEmbeddingOperationMetrics(
        OnnxEmbeddingOperationKind operation,
        OnnxEmbeddingFailureKind failure,
        double totalMilliseconds,
        double queueMilliseconds = 0,
        double tokenizationMilliseconds = 0,
        double inferenceMilliseconds = 0,
        int textCount = 0,
        int inferenceBatchCount = 0,
        int maximumInferenceBatchSize = 0,
        int tokenCount = 0,
        int truncatedTextCount = 0)
    {
        if (!Enum.IsDefined(typeof(OnnxEmbeddingOperationKind), operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (!Enum.IsDefined(typeof(OnnxEmbeddingFailureKind), failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        TotalMilliseconds = RequireDuration(totalMilliseconds, nameof(totalMilliseconds));
        QueueMilliseconds = RequireDuration(queueMilliseconds, nameof(queueMilliseconds));
        TokenizationMilliseconds = RequireDuration(tokenizationMilliseconds, nameof(tokenizationMilliseconds));
        InferenceMilliseconds = RequireDuration(inferenceMilliseconds, nameof(inferenceMilliseconds));
        if (textCount < 0
            || inferenceBatchCount < 0
            || maximumInferenceBatchSize < 0
            || tokenCount < 0
            || truncatedTextCount < 0
            || truncatedTextCount > textCount)
        {
            throw new ArgumentOutOfRangeException(nameof(textCount));
        }

        Operation = operation;
        Failure = failure;
        TextCount = textCount;
        InferenceBatchCount = inferenceBatchCount;
        MaximumInferenceBatchSize = maximumInferenceBatchSize;
        TokenCount = tokenCount;
        TruncatedTextCount = truncatedTextCount;
    }

    public OnnxEmbeddingOperationKind Operation { get; }

    public OnnxEmbeddingFailureKind Failure { get; }

    public double TotalMilliseconds { get; }

    public double QueueMilliseconds { get; }

    public double TokenizationMilliseconds { get; }

    public double InferenceMilliseconds { get; }

    public int TextCount { get; }

    public int InferenceBatchCount { get; }

    public int MaximumInferenceBatchSize { get; }

    public int TokenCount { get; }

    public int TruncatedTextCount { get; }

    public bool Succeeded => Failure == OnnxEmbeddingFailureKind.None;

    private static double RequireDuration(double value, string parameterName) =>
        value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}

public interface IOnnxEmbeddingMetricsSink
{
    void Observe(OnnxEmbeddingOperationMetrics metrics);
}

public sealed class NullOnnxEmbeddingMetricsSink : IOnnxEmbeddingMetricsSink
{
    public static NullOnnxEmbeddingMetricsSink Instance { get; } = new();

    private NullOnnxEmbeddingMetricsSink()
    {
    }

    public void Observe(OnnxEmbeddingOperationMetrics metrics) =>
        _ = metrics ?? throw new ArgumentNullException(nameof(metrics));
}

public sealed class OnnxEmbeddingException : Exception
{
    public OnnxEmbeddingException(OnnxEmbeddingFailureKind failure, string message)
        : base(message)
    {
        if (failure is OnnxEmbeddingFailureKind.None || !Enum.IsDefined(typeof(OnnxEmbeddingFailureKind), failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        Failure = failure;
    }

    public OnnxEmbeddingException(OnnxEmbeddingFailureKind failure, string message, Exception innerException)
        : base(message, innerException)
    {
        if (failure is OnnxEmbeddingFailureKind.None || !Enum.IsDefined(typeof(OnnxEmbeddingFailureKind), failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        Failure = failure;
    }

    public OnnxEmbeddingFailureKind Failure { get; }
}

public sealed class OnnxEmbeddingModelFile
{
    public OnnxEmbeddingModelFile(string relativePath, long bytes, string sha256)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Length > 512
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/', '\\').Any(value => value is "" or "." or ".."))
        {
            throw new ArgumentException("A bounded model-relative path is required.", nameof(relativePath));
        }

        if (bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        if (!OnnxEmbeddingGuards.IsSha256(sha256))
        {
            throw new ArgumentException("A lowercase SHA-256 value is required.", nameof(sha256));
        }

        RelativePath = relativePath.Replace('\\', '/');
        Bytes = bytes;
        Sha256 = sha256;
    }

    public string RelativePath { get; }

    public long Bytes { get; }

    public string Sha256 { get; }
}

public sealed class OnnxEmbeddingModelManifest
{
    public OnnxEmbeddingModelManifest(
        string modelId,
        string version,
        string preprocessingIdentity,
        int dimensions,
        int maximumTokens,
        IReadOnlyList<OnnxEmbeddingModelFile> files)
    {
        ModelId = OnnxEmbeddingGuards.Id(modelId, nameof(modelId), 512);
        Version = OnnxEmbeddingGuards.Id(version, nameof(version), 256);
        PreprocessingIdentity = OnnxEmbeddingGuards.Id(
            preprocessingIdentity,
            nameof(preprocessingIdentity),
            256);
        if (dimensions < 1 || dimensions > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }

        if (maximumTokens < 3 || maximumTokens > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        }

        if (files is null || files.Count != 4 || files.Any(value => value is null))
        {
            throw new ArgumentException("The validated model manifest must contain four required files.", nameof(files));
        }

        var copy = files.ToArray();
        if (copy.Select(value => value.RelativePath).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Model manifest paths must be unique.", nameof(files));
        }

        Dimensions = dimensions;
        MaximumTokens = maximumTokens;
        Files = new ReadOnlyCollection<OnnxEmbeddingModelFile>(copy);
    }

    public string ModelId { get; }

    public string Version { get; }

    public string PreprocessingIdentity { get; }

    public int Dimensions { get; }

    public int MaximumTokens { get; }

    public IReadOnlyList<OnnxEmbeddingModelFile> Files { get; }
}

public sealed class BgeM3OnnxEmbeddingOptions
{
    public BgeM3OnnxEmbeddingOptions(string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new ArgumentException("A local model directory is required.", nameof(modelDirectory));
        }

        ModelDirectory = modelDirectory;
    }

    public string ModelDirectory { get; }

    public string ProviderId { get; set; } = "onnx-in-process";

    public string? ModelVersion { get; set; }

    public int MaximumTokens { get; set; } = 512;

    public int MaximumCharactersPerText { get; set; } = 100_000;

    public int MaximumTextsPerCall { get; set; } = 256;

    public int MaximumBatchSize { get; set; } = 16;

    public int MaximumBatchTokens { get; set; } = 32_768;

    public long MaximumTensorBytes { get; set; } = 268_435_456;

    public long MaximumModelBytes { get; set; } = 1_073_741_824;

    public int MaximumConcurrentInferences { get; set; } = 1;

    public int MaximumQueuedOperations { get; set; } = 16;

    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan InferenceTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public int IntraOpThreads { get; set; }

    public int InterOpThreads { get; set; } = 1;

    public IReadOnlyDictionary<string, long>? ExpectedFileBytes { get; set; }

    public IReadOnlyDictionary<string, string>? ExpectedSha256 { get; set; }

    public IOnnxEmbeddingMetricsSink Metrics { get; set; } = NullOnnxEmbeddingMetricsSink.Instance;
}

internal static class OnnxEmbeddingGuards
{
    internal const string ConfigPath = "config.json";
    internal const string TokenizerConfigPath = "tokenizer_config.json";
    internal const string TokenizerModelPath = "sentencepiece.bpe.model";
    internal const string OnnxModelPath = "onnx/model_int8.onnx";

    internal static readonly string[] RequiredPaths =
    {
        ConfigPath,
        TokenizerConfigPath,
        TokenizerModelPath,
        OnnxModelPath,
    };

    internal static string Id(string value, string parameterName, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumCharacters
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-control identifier is required.", parameterName);
        }

        return value;
    }

    internal static bool IsSha256(string value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
