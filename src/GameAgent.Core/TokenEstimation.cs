using System.Text;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Estimates model tokens without performing I/O. Implementations must be
/// thread-safe and return a positive, conservative estimate for non-empty
/// input.
/// </summary>
public interface IRuntimeTokenEstimator
{
    string EstimatorId { get; }

    string Version { get; }

    int EstimateTokens(string content);

    int EstimateOpaqueUtf8Bytes(int utf8Bytes);
}

/// <summary>
/// Optional provider-owned estimator for the provider's exact model route.
/// The provider runner uses this estimate for its context-window gate.
/// </summary>
public interface IProviderPromptTokenEstimator
{
    string EstimatorId { get; }

    string Version { get; }

    int EstimatePromptTokens(
        IReadOnlyList<NormalizedMessage> messages,
        IReadOnlyList<ToolDescriptor> tools);
}

/// <summary>
/// Optional feedback boundary for a provider-owned estimator. Observations are
/// based only on provider-reported input usage from a completed attempt.
/// </summary>
public interface ICalibratingProviderPromptTokenEstimator :
    IProviderPromptTokenEstimator
{
    void ObserveActualInputTokens(
        int estimatedTokens,
        int actualInputTokens);
}

/// <summary>
/// Conservative fallback that accounts for CJK scripts, emoji, JSON
/// punctuation, ASCII word runs, and whitespace separately.
/// </summary>
public sealed class ScriptAwareTokenEstimator : IRuntimeTokenEstimator
{
    public static ScriptAwareTokenEstimator Shared { get; } = new();

    public string EstimatorId => "script-aware";

    public string Version => "1";

    public int EstimateTokens(string content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (content.Length == 0)
        {
            return 0;
        }

        long tokens = 0;
        var asciiWordRun = 0;
        var whitespaceRun = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var value = content[index];
            if (value <= 0x7f
                && (char.IsLetterOrDigit(value) || value == '_'))
            {
                FlushWhitespace();
                asciiWordRun++;
                continue;
            }

            if (char.IsWhiteSpace(value))
            {
                FlushAsciiWord();
                whitespaceRun++;
                continue;
            }

            FlushAsciiWord();
            FlushWhitespace();
            if (char.IsHighSurrogate(value))
            {
                if (index + 1 >= content.Length
                    || !char.IsLowSurrogate(content[index + 1]))
                {
                    throw new ArgumentException(
                        "Token estimation input contains malformed Unicode.",
                        nameof(content));
                }

                index++;
                tokens = checked(tokens + 2);
                continue;
            }

            if (char.IsLowSurrogate(value))
            {
                throw new ArgumentException(
                    "Token estimation input contains malformed Unicode.",
                    nameof(content));
            }

            // JSON punctuation and non-ASCII scripts are conservatively
            // charged as one token per scalar.
            tokens = checked(tokens + 1);
        }

        FlushAsciiWord();
        FlushWhitespace();
        return checked((int)Math.Max(1, Math.Min(int.MaxValue, tokens)));

        void FlushAsciiWord()
        {
            if (asciiWordRun == 0)
            {
                return;
            }

            tokens = checked(tokens + ((asciiWordRun + 3L) / 4L));
            asciiWordRun = 0;
        }

        void FlushWhitespace()
        {
            if (whitespaceRun == 0)
            {
                return;
            }

            tokens = checked(tokens + ((whitespaceRun + 7L) / 8L));
            whitespaceRun = 0;
        }
    }

    public int EstimateOpaqueUtf8Bytes(int utf8Bytes)
    {
        if (utf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(utf8Bytes));
        }

        return utf8Bytes == 0
            ? 0
            : checked((int)Math.Min(
                int.MaxValue,
                Math.Max(1L, (utf8Bytes + 1L) / 2L)));
    }
}

internal sealed class Utf8RatioTokenEstimator : IRuntimeTokenEstimator
{
    private readonly int _bytesPerToken;

    public Utf8RatioTokenEstimator(int bytesPerToken)
    {
        if (bytesPerToken < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerToken));
        }

        _bytesPerToken = bytesPerToken;
    }

    public string EstimatorId => "utf8-ratio";

    public string Version => "1";

    public int EstimateTokens(string content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        return EstimateOpaqueUtf8Bytes(
            StrictUtf8Encoding.GetByteCount(content));
    }

    public int EstimateOpaqueUtf8Bytes(int utf8Bytes)
    {
        if (utf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(utf8Bytes));
        }

        return utf8Bytes == 0
            ? 0
            : checked(
                (utf8Bytes + _bytesPerToken - 1)
                / _bytesPerToken);
    }
}

/// <summary>
/// Applies a bounded, monotonic safety multiplier when provider-reported input
/// usage exceeds prior estimates. It never calibrates downward.
/// </summary>
public sealed class CalibratingProviderTokenEstimator :
    ICalibratingProviderPromptTokenEstimator
{
    private const double MaximumMultiplier = 4.0;
    private const double ObservationMargin = 1.10;
    private readonly IRuntimeTokenEstimator _inner;
    private readonly object _sync = new();
    private double _multiplier = 1.0;

    public CalibratingProviderTokenEstimator(
        IRuntimeTokenEstimator? inner = null)
    {
        _inner = inner ?? ScriptAwareTokenEstimator.Shared;
    }

    public string EstimatorId =>
        "calibrating:" + _inner.EstimatorId;

    public string Version => "1:" + _inner.Version;

    public int EstimatePromptTokens(
        IReadOnlyList<NormalizedMessage> messages,
        IReadOnlyList<ToolDescriptor> tools)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        long baseline = 0;
        foreach (var message in messages)
        {
            var encoded = NormalizedMessageJournalCodec.Encode(
                message ?? throw new ArgumentException(
                    "Prompt messages cannot contain null entries.",
                    nameof(messages)));
            baseline = checked(
                baseline + _inner.EstimateTokens(encoded.GetRawText()));
        }

        foreach (var tool in tools)
        {
            baseline = checked(
                baseline + _inner.EstimateTokens(
                    ProtocolJson.ToElement(
                            tool ?? throw new ArgumentException(
                                "Prompt tools cannot contain null entries.",
                                nameof(tools)))
                        .GetRawText()));
        }

        var multiplier = CurrentMultiplier;
        return checked((int)Math.Min(
            int.MaxValue,
            Math.Max(1L, (long)Math.Ceiling(baseline * multiplier))));
    }

    public void ObserveActualInputTokens(
        int estimatedTokens,
        int actualInputTokens)
    {
        if (estimatedTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        }

        if (actualInputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actualInputTokens));
        }

        if (actualInputTokens <= estimatedTokens)
        {
            return;
        }

        var observed = Math.Min(
            MaximumMultiplier,
            actualInputTokens / (double)estimatedTokens
            * ObservationMargin);
        lock (_sync)
        {
            _multiplier = Math.Max(_multiplier, observed);
        }
    }

    public double CurrentMultiplier
    {
        get
        {
            lock (_sync)
            {
                return _multiplier;
            }
        }
    }
}
