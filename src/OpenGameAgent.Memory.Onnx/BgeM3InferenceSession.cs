using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OpenGameAgent.Memory.Onnx;

internal interface IBgeM3InferenceSession : IDisposable
{
    IReadOnlyList<ReadOnlyMemory<float>> Run(
        IReadOnlyList<BgeM3TokenizedText> texts,
        long maximumTensorBytes,
        CancellationToken cancellationToken);
}

internal sealed class BgeM3InferenceSession : IBgeM3InferenceSession
{
    private const int Dimensions = 1_024;
    private readonly InferenceSession _session;

    internal BgeM3InferenceSession(string modelPath, BgeM3ValidatedOptions options)
    {
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = options.InterOpThreads,
        };
        if (options.IntraOpThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = options.IntraOpThreads;
        }

        _session = new InferenceSession(modelPath, sessionOptions);
        ValidateContract();
    }

    public IReadOnlyList<ReadOnlyMemory<float>> Run(
        IReadOnlyList<BgeM3TokenizedText> texts,
        long maximumTensorBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0)
        {
            return Array.Empty<ReadOnlyMemory<float>>();
        }

        var sequenceLength = texts.Max(value => value.InputIds.Length);
        var estimatedBytes = checked(
            (long)texts.Count * sequenceLength * (sizeof(long) * 3L + Dimensions * sizeof(float)));
        if (estimatedBytes > maximumTensorBytes)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.InvalidConfiguration,
                "The embedding batch exceeds the configured tensor-memory boundary.");
        }

        var dimensions = new[] { texts.Count, sequenceLength };
        var inputIds = new DenseTensor<long>(dimensions);
        var attentionMask = new DenseTensor<long>(dimensions);
        var tokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids")
            ? new DenseTensor<long>(dimensions)
            : null;
        for (var batch = 0; batch < texts.Count; batch++)
        {
            var ids = texts[batch].InputIds;
            for (var token = 0; token < sequenceLength; token++)
            {
                inputIds[batch, token] = token < ids.Length ? ids[token] : 1;
                attentionMask[batch, token] = token < ids.Length ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };
        if (tokenTypeIds is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using var runOptions = new RunOptions();
        using var cancellation = cancellationToken.Register(
            static state => ((RunOptions)state!).Terminate = true,
            runOptions);
        try
        {
            using var results = _session.Run(inputs, new[] { "last_hidden_state" }, runOptions);
            cancellationToken.ThrowIfCancellationRequested();
            var output = results.Single().AsTensor<float>();
            var outputDimensions = output.Dimensions.ToArray();
            if (outputDimensions.Length != 3
                || outputDimensions[0] != texts.Count
                || outputDimensions[1] != sequenceLength
                || outputDimensions[2] != Dimensions)
            {
                throw new OnnxEmbeddingException(
                    OnnxEmbeddingFailureKind.InvalidOutput,
                    "The ONNX model returned an invalid last_hidden_state shape.");
            }

            var vectors = new ReadOnlyMemory<float>[texts.Count];
            for (var batch = 0; batch < texts.Count; batch++)
            {
                var vector = new float[Dimensions];
                var norm = 0d;
                for (var dimension = 0; dimension < Dimensions; dimension++)
                {
                    var value = output[batch, 0, dimension];
                    if (float.IsNaN(value) || float.IsInfinity(value))
                    {
                        throw new OnnxEmbeddingException(
                            OnnxEmbeddingFailureKind.InvalidOutput,
                            "The ONNX model returned a non-finite embedding value.");
                    }

                    vector[dimension] = value;
                    norm += (double)value * value;
                }

                if (norm <= 0 || double.IsNaN(norm) || double.IsInfinity(norm))
                {
                    throw new OnnxEmbeddingException(
                        OnnxEmbeddingFailureKind.InvalidOutput,
                        "The ONNX model returned a zero or invalid embedding norm.");
                }

                var scale = 1d / Math.Sqrt(norm);
                for (var dimension = 0; dimension < Dimensions; dimension++)
                {
                    vector[dimension] = (float)(vector[dimension] * scale);
                }

                vectors[batch] = vector;
            }

            return vectors;
        }
        catch (OnnxEmbeddingException)
        {
            throw;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ONNX embedding inference was cancelled.", exception, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.Inference,
                "The ONNX embedding inference failed.",
                exception);
        }
    }

    public void Dispose() => _session.Dispose();

    private void ValidateContract()
    {
        var requiredInputs = new HashSet<string>(new[] { "input_ids", "attention_mask" }, StringComparer.Ordinal);
        var supportedInputs = new HashSet<string>(
            new[] { "input_ids", "attention_mask", "token_type_ids" },
            StringComparer.Ordinal);
        if (!requiredInputs.IsSubsetOf(_session.InputMetadata.Keys)
            || _session.InputMetadata.Keys.Any(value => !supportedInputs.Contains(value)))
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                "The ONNX model has an unsupported input contract.");
        }

        foreach (var input in _session.InputMetadata)
        {
            if (!input.Value.IsTensor || input.Value.ElementDataType != TensorElementType.Int64)
            {
                throw new OnnxEmbeddingException(
                    OnnxEmbeddingFailureKind.UnsupportedModel,
                    "The ONNX model inputs must be Int64 tensors.");
            }
        }

        if (!_session.OutputMetadata.TryGetValue("last_hidden_state", out var output)
            || !output.IsTensor
            || output.ElementDataType != TensorElementType.Float
            || output.Dimensions.Length != 3
            || output.Dimensions[^1] != Dimensions)
        {
            throw new OnnxEmbeddingException(
                OnnxEmbeddingFailureKind.UnsupportedModel,
                "The ONNX model must expose a 1024-dimensional float last_hidden_state tensor.");
        }
    }
}
