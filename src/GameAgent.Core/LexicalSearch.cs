using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

/// <summary>
/// Stable reason codes for bounded lexical-search failures.
/// </summary>
public static class LexicalSearchReasonCodes
{
    public const string InputBytesExceeded =
        "lexical_input_bytes_exceeded";
    public const string TextSegmentsExceeded =
        "lexical_text_segments_exceeded";
    public const string TermBytesExceeded =
        "lexical_term_bytes_exceeded";
    public const string TermsExceeded =
        "lexical_terms_exceeded";
    public const string UniqueTermsExceeded =
        "lexical_unique_terms_exceeded";
    public const string DocumentBytesExceeded =
        "lexical_document_bytes_exceeded";
    public const string QueryBytesExceeded =
        "lexical_query_bytes_exceeded";
    public const string IndexBytesExceeded =
        "lexical_index_bytes_exceeded";
    public const string IndexTermsExceeded =
        "lexical_index_terms_exceeded";
    public const string ComparisonsExceeded =
        "lexical_comparisons_exceeded";
}

/// <summary>
/// Reports that a deterministic lexical operation crossed a configured hard
/// bound.
/// </summary>
public sealed class LexicalSearchLimitException : ArgumentException
{
    internal LexicalSearchLimitException(
        string parameterName,
        string reasonCode,
        string message)
        : base($"{reasonCode}: {message}", parameterName)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Hard limits applied by <see cref="DeterministicUnicodeTokenizer"/>.
/// </summary>
public sealed class DeterministicUnicodeTokenizerLimits
{
    public DeterministicUnicodeTokenizerLimits(
        int maxInputUtf8Bytes = 131_072,
        int maxTextSegments = 8_192,
        int maxTerms = 4_096,
        int maxUniqueTerms = 2_048,
        int maxTermUtf8Bytes = 128)
    {
        if (maxInputUtf8Bytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInputUtf8Bytes));
        }

        if (maxTextSegments is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTextSegments));
        }

        if (maxTerms is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTerms));
        }

        if (maxUniqueTerms < 1 || maxUniqueTerms > maxTerms)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUniqueTerms));
        }

        if (maxTermUtf8Bytes is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTermUtf8Bytes));
        }

        MaxInputUtf8Bytes = maxInputUtf8Bytes;
        MaxTextSegments = maxTextSegments;
        MaxTerms = maxTerms;
        MaxUniqueTerms = maxUniqueTerms;
        MaxTermUtf8Bytes = maxTermUtf8Bytes;
    }

    public int MaxInputUtf8Bytes { get; }

    public int MaxTextSegments { get; }

    public int MaxTerms { get; }

    public int MaxUniqueTerms { get; }

    public int MaxTermUtf8Bytes { get; }
}

/// <summary>
/// A bounded, culture-invariant Unicode tokenizer. Text is normalized with
/// NFKC, letters and digits form word terms, and adjacent CJK code points form
/// unigram and bigram terms.
/// </summary>
public sealed class DeterministicUnicodeTokenizer
{
    public const string Identity = "unicode-lexical";
    public const string Version = "nfkc-lower-cjk-bigram-v1";

    private readonly DeterministicUnicodeTokenizerLimits _limits;

    public DeterministicUnicodeTokenizer(
        DeterministicUnicodeTokenizerLimits? limits = null)
    {
        _limits = limits ?? new DeterministicUnicodeTokenizerLimits();
    }

    public DeterministicUnicodeTokenizerLimits Limits => _limits;

    /// <summary>
    /// Tokenizes one string. Duplicate terms are retained in occurrence order
    /// so callers can derive bounded term frequencies.
    /// </summary>
    public IReadOnlyList<string> Tokenize(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var accumulator = new TokenAccumulator(_limits, nameof(value));
        accumulator.AddText(value);
        return new ReadOnlyCollection<string>(
            accumulator.Terms.ToArray());
    }

    internal TokenizedTerms TokenizeJson(
        JsonElement value,
        string parameterName)
    {
        var accumulator = new TokenAccumulator(_limits, parameterName);
        VisitJson(value, accumulator, depth: 0);
        return accumulator.Complete();
    }

    internal TokenizedTerms TokenizeTextSegments(
        IEnumerable<string> values,
        string parameterName)
    {
        if (values is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var accumulator = new TokenAccumulator(_limits, parameterName);
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException(
                    "A lexical text segment cannot be null.",
                    parameterName);
            }

            accumulator.AddText(value);
        }

        return accumulator.Complete();
    }

    private static void VisitJson(
        JsonElement value,
        TokenAccumulator accumulator,
        int depth)
    {
        if (depth > 64)
        {
            throw new ArgumentException(
                "Lexical JSON nesting exceeds 64 levels.",
                accumulator.ParameterName);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    accumulator.AddText(property.Name);
                    VisitJson(property.Value, accumulator, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    VisitJson(item, accumulator, depth + 1);
                }

                break;
            case JsonValueKind.String:
                accumulator.AddText(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                accumulator.AddText(value.GetRawText());
                break;
        }
    }

    internal sealed class TokenAccumulator
    {
        private readonly DeterministicUnicodeTokenizerLimits _limits;
        private readonly Dictionary<string, int> _frequencies =
            new(StringComparer.Ordinal);
        private readonly List<string> _terms = new();
        private int _inputUtf8Bytes;
        private int _textSegments;

        public TokenAccumulator(
            DeterministicUnicodeTokenizerLimits limits,
            string parameterName)
        {
            _limits = limits;
            ParameterName = parameterName;
        }

        public string ParameterName { get; }

        public IReadOnlyList<string> Terms => _terms;

        public void AddText(string value)
        {
            _textSegments = checked(_textSegments + 1);
            if (_textSegments > _limits.MaxTextSegments)
            {
                throw Limit(
                    LexicalSearchReasonCodes.TextSegmentsExceeded,
                    $"Lexical input exceeds {_limits.MaxTextSegments} "
                    + "text segments.");
            }

            var bytes = MeasureUtf8BytesBounded(
                value,
                _limits.MaxInputUtf8Bytes - _inputUtf8Bytes);
            _inputUtf8Bytes = checked(_inputUtf8Bytes + bytes);

            string normalized;
            try
            {
                normalized = value.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "Lexical input contains invalid Unicode.",
                    ParameterName,
                    exception);
            }

            var word = new StringBuilder();
            var wordBytes = 0;
            string? previousCjk = null;
            for (var index = 0; index < normalized.Length;)
            {
                var characterCount =
                    char.IsHighSurrogate(normalized[index])
                    && index + 1 < normalized.Length
                    && char.IsLowSurrogate(normalized[index + 1])
                        ? 2
                        : 1;
                var text = normalized.Substring(index, characterCount);
                var codePoint = characterCount == 1
                    ? normalized[index]
                    : char.ConvertToUtf32(
                        normalized[index],
                        normalized[index + 1]);
                if (IsCjk(codePoint))
                {
                    FlushWord(word, ref wordBytes);
                    AddTerm(text);
                    if (previousCjk is not null)
                    {
                        AddTerm(previousCjk + text);
                    }

                    previousCjk = text;
                    index += characterCount;
                    continue;
                }

                var category = CharUnicodeInfo.GetUnicodeCategory(
                    normalized,
                    index);
                if (IsWordCategory(category))
                {
                    previousCjk = null;
                    var lowered = text.ToLowerInvariant();
                    var runeBytes = Encoding.UTF8.GetByteCount(lowered);
                    if (wordBytes > _limits.MaxTermUtf8Bytes - runeBytes)
                    {
                        throw Limit(
                            LexicalSearchReasonCodes.TermBytesExceeded,
                            $"A lexical term exceeds "
                            + $"{_limits.MaxTermUtf8Bytes} UTF-8 bytes.");
                    }

                    word.Append(lowered);
                    wordBytes += runeBytes;
                    index += characterCount;
                    continue;
                }

                FlushWord(word, ref wordBytes);
                previousCjk = null;
                index += characterCount;
            }

            FlushWord(word, ref wordBytes);
        }

        public TokenizedTerms Complete()
        {
            return new TokenizedTerms(
                new Dictionary<string, int>(
                    _frequencies,
                    StringComparer.Ordinal),
                _terms.Count,
                _inputUtf8Bytes);
        }

        private void FlushWord(StringBuilder word, ref int wordBytes)
        {
            if (word.Length == 0)
            {
                return;
            }

            AddTerm(word.ToString());
            word.Clear();
            wordBytes = 0;
        }

        private void AddTerm(string term)
        {
            if (_terms.Count >= _limits.MaxTerms)
            {
                throw Limit(
                    LexicalSearchReasonCodes.TermsExceeded,
                    $"Lexical input exceeds {_limits.MaxTerms} terms.");
            }

            if (!_frequencies.TryGetValue(term, out var frequency))
            {
                if (_frequencies.Count >= _limits.MaxUniqueTerms)
                {
                    throw Limit(
                        LexicalSearchReasonCodes.UniqueTermsExceeded,
                        $"Lexical input exceeds "
                        + $"{_limits.MaxUniqueTerms} unique terms.");
                }

                _frequencies.Add(term, 1);
            }
            else
            {
                _frequencies[term] = checked(frequency + 1);
            }

            _terms.Add(term);
        }

        private int MeasureUtf8BytesBounded(
            string value,
            int remainingBytes)
        {
            var bytes = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                int additional;
                if (character <= '\u007f')
                {
                    additional = 1;
                }
                else if (character <= '\u07ff')
                {
                    additional = 2;
                }
                else if (char.IsHighSurrogate(character)
                         && index + 1 < value.Length
                         && char.IsLowSurrogate(value[index + 1]))
                {
                    additional = 4;
                    index++;
                }
                else if (char.IsSurrogate(character))
                {
                    throw new ArgumentException(
                        "Lexical input contains invalid Unicode.",
                        ParameterName);
                }
                else
                {
                    additional = 3;
                }

                if (bytes > remainingBytes - additional)
                {
                    throw Limit(
                        LexicalSearchReasonCodes.InputBytesExceeded,
                        $"Lexical input exceeds "
                        + $"{_limits.MaxInputUtf8Bytes} UTF-8 bytes.");
                }

                bytes += additional;
            }

            return bytes;
        }

        private LexicalSearchLimitException Limit(
            string reasonCode,
            string message)
        {
            return new LexicalSearchLimitException(
                ParameterName,
                reasonCode,
                message);
        }

        private static bool IsWordCategory(UnicodeCategory category)
        {
            return category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber
                or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark;
        }

        private static bool IsCjk(int value)
        {
            return value is >= 0x3400 and <= 0x4dbf
                or >= 0x4e00 and <= 0x9fff
                or >= 0xf900 and <= 0xfaff
                or >= 0x20000 and <= 0x2fa1f
                or >= 0x3040 and <= 0x30ff
                or >= 0x31f0 and <= 0x31ff
                or >= 0xac00 and <= 0xd7af;
        }
    }
}

internal sealed class TokenizedTerms
{
    public TokenizedTerms(
        Dictionary<string, int> frequencies,
        int termCount,
        int inputUtf8Bytes)
    {
        Frequencies = frequencies;
        TermCount = termCount;
        InputUtf8Bytes = inputUtf8Bytes;
    }

    public Dictionary<string, int> Frequencies { get; }

    public int TermCount { get; }

    public int InputUtf8Bytes { get; }
}

/// <summary>
/// One field's contribution to a BM25F term score.
/// </summary>
public readonly struct Bm25FieldMatch
{
    public Bm25FieldMatch(
        int termFrequency,
        int fieldLength,
        double averageFieldLength,
        double weight = 1,
        double lengthNormalization = 0.75)
    {
        if (termFrequency < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(termFrequency));
        }

        if (fieldLength < 0 || termFrequency > fieldLength)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldLength));
        }

        if (!IsFinitePositive(averageFieldLength)
            || averageFieldLength > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(averageFieldLength));
        }

        if (!IsFinitePositive(weight) || weight > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        if (double.IsNaN(lengthNormalization)
            || double.IsInfinity(lengthNormalization)
            || lengthNormalization is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lengthNormalization));
        }

        TermFrequency = termFrequency;
        FieldLength = fieldLength;
        AverageFieldLength = averageFieldLength;
        Weight = weight;
        LengthNormalization = lengthNormalization;
    }

    public int TermFrequency { get; }

    public int FieldLength { get; }

    public double AverageFieldLength { get; }

    public double Weight { get; }

    public double LengthNormalization { get; }

    private static bool IsFinitePositive(double value)
    {
        return !double.IsNaN(value)
               && !double.IsInfinity(value)
               && value > 0;
    }
}

/// <summary>
/// Deterministic BM25/BM25F term scorer. Floating-point output is quantized
/// immediately to a stable integer before scores are combined or compared.
/// </summary>
public sealed class DeterministicBm25Scorer
{
    private readonly double _k1;
    private readonly int _scoreScale;
    private readonly int _maxFieldsPerTerm;

    public DeterministicBm25Scorer(
        double k1 = 1.2,
        int scoreScale = 10_000,
        int maxFieldsPerTerm = 16)
    {
        if (double.IsNaN(k1)
            || double.IsInfinity(k1)
            || k1 is <= 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(k1));
        }

        if (scoreScale is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(scoreScale));
        }

        if (maxFieldsPerTerm is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFieldsPerTerm));
        }

        _k1 = k1;
        _scoreScale = scoreScale;
        _maxFieldsPerTerm = maxFieldsPerTerm;
    }

    /// <summary>
    /// Scores one query term. Supplying one field yields BM25; supplying
    /// multiple fields yields BM25F.
    /// </summary>
    public int ScoreTerm(
        int documentCount,
        int documentFrequency,
        IEnumerable<Bm25FieldMatch> fields)
    {
        if (documentCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(documentCount));
        }

        if (documentFrequency is < 0
            || documentFrequency > documentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentFrequency));
        }

        if (fields is null)
        {
            throw new ArgumentNullException(nameof(fields));
        }

        var fieldCount = 0;
        var weightedFrequency = 0.0;
        foreach (var field in fields)
        {
            fieldCount++;
            if (fieldCount > _maxFieldsPerTerm)
            {
                throw new LexicalSearchLimitException(
                    nameof(fields),
                    LexicalSearchReasonCodes.TermsExceeded,
                    $"A BM25F term exceeds {_maxFieldsPerTerm} fields.");
            }

            if (field.TermFrequency < 0
                || field.FieldLength < 0
                || field.TermFrequency > field.FieldLength
                || double.IsNaN(field.AverageFieldLength)
                || double.IsInfinity(field.AverageFieldLength)
                || field.AverageFieldLength <= 0
                || field.AverageFieldLength > int.MaxValue
                || double.IsNaN(field.Weight)
                || double.IsInfinity(field.Weight)
                || field.Weight <= 0
                || field.Weight > 1_000
                || double.IsNaN(field.LengthNormalization)
                || double.IsInfinity(field.LengthNormalization)
                || field.LengthNormalization < 0
                || field.LengthNormalization > 1)
            {
                throw new ArgumentException(
                    "A BM25 field is invalid.",
                    nameof(fields));
            }

            if (field.TermFrequency == 0)
            {
                continue;
            }

            var lengthRatio = field.FieldLength
                              / field.AverageFieldLength;
            var normalization =
                1.0
                - field.LengthNormalization
                + field.LengthNormalization * lengthRatio;
            weightedFrequency +=
                field.Weight * field.TermFrequency / normalization;
        }

        if (fieldCount == 0)
        {
            throw new ArgumentException(
                "At least one BM25 field is required.",
                nameof(fields));
        }

        if (documentFrequency == 0 || weightedFrequency <= 0)
        {
            return 0;
        }

        var inverseDocumentFrequency = Math.Log(
            1.0
            + (documentCount - documentFrequency + 0.5)
            / (documentFrequency + 0.5));
        var saturatedFrequency =
            weightedFrequency * (_k1 + 1.0)
            / (weightedFrequency + _k1);
        var scaled =
            inverseDocumentFrequency * saturatedFrequency * _scoreScale;
        if (scaled >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return checked(
            (int)Math.Round(
                scaled,
                MidpointRounding.AwayFromZero));
    }
}

public enum MemoryIndexStatus
{
    Rebuilding = 0,
    Ready = 1,
    Faulted = 2,
    Disposed = 3
}

/// <summary>
/// Immutable, implementation-neutral diagnostics for a local memory index.
/// </summary>
public sealed class MemoryIndexDiagnostics
{
    public MemoryIndexDiagnostics(
        string identity,
        string version,
        string tokenizerIdentity,
        string tokenizerVersion,
        long sourceRevision,
        MemoryIndexStatus status)
    {
        Identity = RuntimeGuard.RequiredUtf8(
            identity,
            128,
            nameof(identity));
        Version = RuntimeGuard.RequiredUtf8(
            version,
            64,
            nameof(version));
        TokenizerIdentity = RuntimeGuard.RequiredUtf8(
            tokenizerIdentity,
            128,
            nameof(tokenizerIdentity));
        TokenizerVersion = RuntimeGuard.RequiredUtf8(
            tokenizerVersion,
            64,
            nameof(tokenizerVersion));
        if (sourceRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRevision));
        }

        if (status is < MemoryIndexStatus.Rebuilding
            or > MemoryIndexStatus.Disposed)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        SourceRevision = sourceRevision;
        Status = status;
    }

    public string Identity { get; }

    public string Version { get; }

    public string TokenizerIdentity { get; }

    public string TokenizerVersion { get; }

    public long SourceRevision { get; }

    public MemoryIndexStatus Status { get; }
}

public interface IMemoryIndexDiagnosticsProvider
{
    MemoryIndexDiagnostics IndexDiagnostics { get; }
}
