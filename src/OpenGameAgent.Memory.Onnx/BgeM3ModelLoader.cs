using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace OpenGameAgent.Memory.Onnx;

internal sealed class BgeM3LoadedModel : IDisposable
{
    internal BgeM3LoadedModel(
        OnnxEmbeddingModelManifest manifest,
        IBgeM3Tokenizer tokenizer,
        IBgeM3InferenceSession inference)
    {
        Manifest = manifest;
        Tokenizer = tokenizer;
        Inference = inference;
    }

    internal OnnxEmbeddingModelManifest Manifest { get; }

    internal IBgeM3Tokenizer Tokenizer { get; }

    internal IBgeM3InferenceSession Inference { get; }

    public void Dispose() => Inference.Dispose();
}

internal static class BgeM3ModelLoader
{
    private const int Dimensions = 1_024;
    private const string PreprocessingIdentity = "xlm-roberta-spm-fairseq-offset-cls-l2-v1";

    internal static BgeM3LoadedModel Load(
        BgeM3ValidatedOptions options,
        CancellationToken cancellationToken)
    {
        var manifest = ValidateManifest(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var tokenizerPath = Resolve(options.ModelDirectory, OnnxEmbeddingGuards.TokenizerModelPath);
        SentencePieceTokenizer tokenizer;
        try
        {
            using var stream = new FileStream(
                tokenizerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1_024,
                FileOptions.SequentialScan);
            tokenizer = SentencePieceTokenizer.Create(
                stream,
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                specialTokens: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.Tokenization,
                "The local SentencePiece tokenizer could not be loaded.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var modelPath = Resolve(options.ModelDirectory, OnnxEmbeddingGuards.OnnxModelPath);
        BgeM3InferenceSession inference;
        try
        {
            inference = new BgeM3InferenceSession(modelPath, options);
        }
        catch (Exception exception) when (exception is not OnnxEmbeddingException)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                "The ONNX model could not be loaded with the required BGE-M3 contract.",
                exception);
        }

        return new BgeM3LoadedModel(
            manifest,
            new XlmRobertaSentencePieceTokenizer(tokenizer, options.MaximumTokens),
            inference);
    }

    internal static OnnxEmbeddingModelManifest ValidateManifest(
        BgeM3ValidatedOptions options,
        CancellationToken cancellationToken)
    {
        var files = new List<OnnxEmbeddingModelFile>(OnnxEmbeddingGuards.RequiredPaths.Length);
        foreach (var relativePath in OnnxEmbeddingGuards.RequiredPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(options.ModelDirectory, relativePath);
            if (!File.Exists(path))
            {
                throw new OnnxEmbeddingException(
                    OnnxEmbeddingFailureKind.MissingModelFile,
                    $"The required local model file '{relativePath}' is missing.");
            }

            var info = new FileInfo(path);
            var maximum = relativePath switch
            {
                OnnxEmbeddingGuards.ConfigPath or OnnxEmbeddingGuards.TokenizerConfigPath => 1_048_576,
                OnnxEmbeddingGuards.TokenizerModelPath => 67_108_864,
                _ => options.MaximumModelBytes,
            };
            if (info.Length is < 1 || info.Length > maximum)
            {
                throw new OnnxEmbeddingException(
                    OnnxEmbeddingFailureKind.ModelIntegrity,
                    $"The local model file '{relativePath}' violates its configured size boundary.");
            }

            if (options.ExpectedFileBytes.TryGetValue(relativePath, out var expectedBytes)
                && info.Length != expectedBytes)
            {
                throw new OnnxEmbeddingException(
                    OnnxEmbeddingFailureKind.ModelIntegrity,
                    $"The local model file '{relativePath}' does not match its expected size.");
            }

            var hash = Hash(path, cancellationToken);
            if (options.ExpectedSha256.TryGetValue(relativePath, out var expectedHash)
                && !string.Equals(hash, expectedHash, StringComparison.Ordinal))
            {
                throw new OnnxEmbeddingException(
                    OnnxEmbeddingFailureKind.ModelIntegrity,
                    $"The local model file '{relativePath}' does not match its expected SHA-256.");
            }

            files.Add(new OnnxEmbeddingModelFile(relativePath, info.Length, hash));
        }

        using var config = ReadJson(Resolve(options.ModelDirectory, OnnxEmbeddingGuards.ConfigPath));
        using var tokenizerConfig = ReadJson(Resolve(options.ModelDirectory, OnnxEmbeddingGuards.TokenizerConfigPath));
        var modelId = RequireString(config.RootElement, "_name_or_path");
        if (!string.Equals(RequireString(config.RootElement, "model_type"), "xlm-roberta", StringComparison.Ordinal)
            || RequireInt(config.RootElement, "hidden_size") != Dimensions
            || RequireInt(config.RootElement, "bos_token_id") != 0
            || RequireInt(config.RootElement, "pad_token_id") != 1
            || RequireInt(config.RootElement, "eos_token_id") != 2
            || RequireInt(config.RootElement, "type_vocab_size") != 1
            || RequireInt(config.RootElement, "vocab_size") != 250_002
            || RequireInt(config.RootElement, "max_position_embeddings") < options.MaximumTokens + 2)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                "The local model configuration is not the supported 1024-dimensional BGE-M3 XLM-RoBERTa contract.");
        }

        if (!string.Equals(
                RequireString(tokenizerConfig.RootElement, "tokenizer_class"),
                "XLMRobertaTokenizer",
                StringComparison.Ordinal)
            || RequireInt64(tokenizerConfig.RootElement, "model_max_length") < options.MaximumTokens)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                "The local tokenizer configuration is not the supported BGE-M3 XLM-RoBERTa tokenizer.");
        }

        var modelHash = files.Single(value => value.RelativePath == OnnxEmbeddingGuards.OnnxModelPath).Sha256;
        var tokenizerHash = files.Single(value => value.RelativePath == OnnxEmbeddingGuards.TokenizerModelPath).Sha256;
        var version = options.ModelVersion
            ?? $"int8-{modelHash.Substring(0, 16)}-{tokenizerHash.Substring(0, 16)}";
        var preprocessing = $"{PreprocessingIdentity}-max{options.MaximumTokens}";
        return new OnnxEmbeddingModelManifest(
            modelId,
            version,
            preprocessing,
            Dimensions,
            options.MaximumTokens,
            files);
    }

    private static string Resolve(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.InvalidConfiguration,
                "A model file resolved outside the configured local model directory.");
        }

        return combined;
    }

    private static string Hash(string path, CancellationToken cancellationToken)
    {
        using var algorithm = SHA256.Create();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1_048_576,
            FileOptions.SequentialScan);
        var buffer = new byte[1_048_576];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            algorithm.TransformBlock(buffer, 0, read, null, 0);
        }

        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return string.Concat((algorithm.Hash ?? throw new CryptographicException()).Select(value => value.ToString("x2")));
    }

    private static JsonDocument ReadJson(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.ModelIntegrity,
                "A local model configuration file is unreadable or invalid.",
                exception);
        }
    }

    private static string RequireString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new JsonException()
            : throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                $"The local model configuration is missing required field '{propertyName}'.");

    private static int RequireInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                $"The local model configuration is missing required integer field '{propertyName}'.");

    private static long RequireInt64(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result
            : throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                $"The local tokenizer configuration is missing required integer field '{propertyName}'.");
}

internal interface IBgeM3Tokenizer
{
    BgeM3TokenizedText Encode(string text, CancellationToken cancellationToken);
}

internal sealed class BgeM3TokenizedText
{
    internal BgeM3TokenizedText(long[] inputIds, bool truncated)
    {
        InputIds = inputIds;
        Truncated = truncated;
    }

    internal long[] InputIds { get; }

    internal bool Truncated { get; }
}

internal sealed class XlmRobertaSentencePieceTokenizer : IBgeM3Tokenizer
{
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly int _maximumTokens;

    internal XlmRobertaSentencePieceTokenizer(SentencePieceTokenizer tokenizer, int maximumTokens)
    {
        _tokenizer = tokenizer;
        _maximumTokens = maximumTokens;
    }

    public BgeM3TokenizedText Encode(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contentLimit = _maximumTokens - 2;
        var pieces = _tokenizer.EncodeToIds(
            text,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            maxTokenCount: contentLimit + 1,
            out _,
            out _,
            considerPreTokenization: true,
            considerNormalization: true);
        var truncated = pieces.Count > contentLimit;
        var count = Math.Min(contentLimit, pieces.Count);
        var ids = MapSentencePieceIds(pieces, count);
        return new BgeM3TokenizedText(ids, truncated);
    }

    internal static long[] MapSentencePieceIds(IReadOnlyList<int> pieces, int count)
    {
        if (pieces is null || count < 0 || count > pieces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var ids = new long[count + 2];
        ids[0] = 0;
        for (var index = 0; index < count; index++)
        {
            var sentencePieceId = pieces[index];
            if (sentencePieceId < 0)
            {
                throw new ArgumentException("SentencePiece token IDs cannot be negative.", nameof(pieces));
            }

            ids[index + 1] = sentencePieceId == 0 ? 3 : checked(sentencePieceId + 1L);
        }

        ids[^1] = 2;
        return ids;
    }
}
