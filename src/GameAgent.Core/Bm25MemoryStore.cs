using System.Collections.ObjectModel;
using System.Text;

namespace GameAgent.Core;

internal interface IPreflightMemoryIndex
{
    void ValidateUpsert(
        MemoryRecord record,
        CancellationToken cancellationToken);

    void ValidateAtomicBatch(
        IReadOnlyList<MemoryMutation> mutations,
        CancellationToken cancellationToken);
}

/// <summary>
/// Hard bounds and scoring weights for <see cref="Bm25MemoryStore"/>.
/// </summary>
public sealed class Bm25MemoryStoreOptions
{
    public Bm25MemoryStoreOptions(
        int maxDocumentUtf8Bytes = 196_608,
        int maxDocumentTerms = 4_096,
        int maxUniqueDocumentTerms = 2_048,
        int maxQueryUtf8Bytes = 32_768,
        int maxQueryTerms = 512,
        int maxUniqueQueryTerms = 256,
        int maxTermUtf8Bytes = 128,
        long maxIndexUtf8Bytes = 256L * 1024 * 1024,
        long maxIndexTerms = 10_000_000,
        long maxComparisonsPerSearch = 2_000_000,
        double contentWeight = 1,
        double tagWeight = 2,
        double contentLengthNormalization = 0.75,
        double tagLengthNormalization = 0.25,
        double k1 = 1.2,
        int scoreScale = 10_000)
    {
        if (maxDocumentUtf8Bytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDocumentUtf8Bytes));
        }

        ValidateTermLimits(
            maxDocumentTerms,
            maxUniqueDocumentTerms,
            nameof(maxDocumentTerms),
            nameof(maxUniqueDocumentTerms));
        if (maxQueryUtf8Bytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxQueryUtf8Bytes));
        }

        ValidateTermLimits(
            maxQueryTerms,
            maxUniqueQueryTerms,
            nameof(maxQueryTerms),
            nameof(maxUniqueQueryTerms));
        if (maxTermUtf8Bytes is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTermUtf8Bytes));
        }

        if (maxIndexUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxIndexUtf8Bytes));
        }

        if (maxIndexTerms < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIndexTerms));
        }

        if (maxComparisonsPerSearch < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxComparisonsPerSearch));
        }

        ValidatePositiveFinite(contentWeight, nameof(contentWeight));
        ValidatePositiveFinite(tagWeight, nameof(tagWeight));
        ValidateNormalization(
            contentLengthNormalization,
            nameof(contentLengthNormalization));
        ValidateNormalization(
            tagLengthNormalization,
            nameof(tagLengthNormalization));

        // The scorer validates k1 and scoreScale.
        _ = new DeterministicBm25Scorer(k1, scoreScale);

        MaxDocumentUtf8Bytes = maxDocumentUtf8Bytes;
        MaxDocumentTerms = maxDocumentTerms;
        MaxUniqueDocumentTerms = maxUniqueDocumentTerms;
        MaxQueryUtf8Bytes = maxQueryUtf8Bytes;
        MaxQueryTerms = maxQueryTerms;
        MaxUniqueQueryTerms = maxUniqueQueryTerms;
        MaxTermUtf8Bytes = maxTermUtf8Bytes;
        MaxIndexUtf8Bytes = maxIndexUtf8Bytes;
        MaxIndexTerms = maxIndexTerms;
        MaxComparisonsPerSearch = maxComparisonsPerSearch;
        ContentWeight = contentWeight;
        TagWeight = tagWeight;
        ContentLengthNormalization = contentLengthNormalization;
        TagLengthNormalization = tagLengthNormalization;
        K1 = k1;
        ScoreScale = scoreScale;
    }

    public int MaxDocumentUtf8Bytes { get; }

    public int MaxDocumentTerms { get; }

    public int MaxUniqueDocumentTerms { get; }

    public int MaxQueryUtf8Bytes { get; }

    public int MaxQueryTerms { get; }

    public int MaxUniqueQueryTerms { get; }

    public int MaxTermUtf8Bytes { get; }

    public long MaxIndexUtf8Bytes { get; }

    public long MaxIndexTerms { get; }

    public long MaxComparisonsPerSearch { get; }

    public double ContentWeight { get; }

    public double TagWeight { get; }

    public double ContentLengthNormalization { get; }

    public double TagLengthNormalization { get; }

    public double K1 { get; }

    public int ScoreScale { get; }

    private static void ValidateTermLimits(
        int maxTerms,
        int maxUniqueTerms,
        string termsParameter,
        string uniqueTermsParameter)
    {
        if (maxTerms is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(termsParameter);
        }

        if (maxUniqueTerms < 1 || maxUniqueTerms > maxTerms)
        {
            throw new ArgumentOutOfRangeException(uniqueTermsParameter);
        }
    }

    private static void ValidatePositiveFinite(
        double value,
        string parameterName)
    {
        if (double.IsNaN(value)
            || double.IsInfinity(value)
            || value <= 0
            || value > 1_000)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateNormalization(
        double value,
        string parameterName)
    {
        if (double.IsNaN(value)
            || double.IsInfinity(value)
            || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// A bounded local BM25F memory store that requires no embedding model.
/// Content and tags are independent weighted fields, while all game-world
/// visibility constraints continue to use <see cref="MemoryQuery"/>.
/// </summary>
public sealed class Bm25MemoryStore :
    IRuntimeAuthoritativeMemoryBatchStore,
    ILegacyRuntimeMemoryBatchReplayStore,
    IMemoryIndexDiagnosticsProvider,
    IPreflightMemoryIndex
{
    public const string IndexIdentity = "bm25f-memory";
    public const string IndexVersion = "1";

    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Bm25MemoryStoreOptions _options;
    private readonly DeterministicUnicodeTokenizer _documentTokenizer;
    private readonly DeterministicUnicodeTokenizer _queryTokenizer;
    private readonly DeterministicBm25Scorer _scorer;
    private Dictionary<string, IndexedRecord> _records =
        new(StringComparer.Ordinal);
    private Dictionary<string, int> _documentFrequencies =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idempotentBatchDigests =
        new(StringComparer.Ordinal);
    private long _indexedUtf8Bytes;
    private long _totalContentTerms;
    private long _totalTagTerms;
    private long _sourceRevision;

    public Bm25MemoryStore(
        string providerId = "bm25-local",
        int capacity = 10_000,
        Bm25MemoryStoreOptions? options = null)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        if (capacity is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _options = options ?? new Bm25MemoryStoreOptions();
        _documentTokenizer = new DeterministicUnicodeTokenizer(
            new DeterministicUnicodeTokenizerLimits(
                _options.MaxDocumentUtf8Bytes,
                maxTextSegments: 8_192,
                _options.MaxDocumentTerms,
                _options.MaxUniqueDocumentTerms,
                _options.MaxTermUtf8Bytes));
        _queryTokenizer = new DeterministicUnicodeTokenizer(
            new DeterministicUnicodeTokenizerLimits(
                _options.MaxQueryUtf8Bytes,
                maxTextSegments: 8_192,
                _options.MaxQueryTerms,
                _options.MaxUniqueQueryTerms,
                _options.MaxTermUtf8Bytes));
        _scorer = new DeterministicBm25Scorer(
            _options.K1,
            _options.ScoreScale,
            maxFieldsPerTerm: 2);
    }

    public string ProviderId { get; }

    public int RuntimeMutationContractVersion =>
        RuntimeMemoryMutationContract.CurrentVersion;

    public MemoryIndexDiagnostics IndexDiagnostics
    {
        get
        {
            lock (_sync)
            {
                return new MemoryIndexDiagnostics(
                    IndexIdentity,
                    IndexVersion,
                    DeterministicUnicodeTokenizer.Identity,
                    DeterministicUnicodeTokenizer.Version,
                    _sourceRevision,
                    MemoryIndexStatus.Ready);
            }
        }
    }

    public ValueTask UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var indexed = Index(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _records.TryGetValue(record.MemoryId, out var existing);
            MemoryMutationAdmission.EnsureCanApplyUnconditionalUpsert(
                MemoryMutation.Upsert(record),
                existing?.Record);
            var staged = StageUpsert(
                record.MemoryId,
                indexed,
                _records,
                _documentFrequencies,
                _indexedUtf8Bytes,
                _totalContentTerms,
                _totalTagTerms);
            Commit(staged);
            _sourceRevision = checked(_sourceRevision + 1);
        }

        return default;
    }

    public ValueTask<bool> DeleteAsync(
        string memoryId,
        CancellationToken cancellationToken)
    {
        ValidateMemoryId(memoryId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_records.TryGetValue(memoryId, out var existing))
            {
                return new ValueTask<bool>(false);
            }

            var records = new Dictionary<string, IndexedRecord>(
                _records,
                StringComparer.Ordinal);
            var frequencies = new Dictionary<string, int>(
                _documentFrequencies,
                StringComparer.Ordinal);
            records.Remove(memoryId);
            RemoveDocumentFrequencies(frequencies, existing);
            _records = records;
            _documentFrequencies = frequencies;
            _indexedUtf8Bytes -= existing.IndexedUtf8Bytes;
            _totalContentTerms -= existing.Content.TermCount;
            _totalTagTerms -= existing.Tags.TermCount;
            _sourceRevision = checked(_sourceRevision + 1);
            return new ValueTask<bool>(true);
        }
    }

    public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var queryUtf8Bytes = Encoding.UTF8.GetByteCount(
            query.Query.GetRawText());
        if (queryUtf8Bytes > _options.MaxQueryUtf8Bytes)
        {
            throw new LexicalSearchLimitException(
                nameof(query),
                LexicalSearchReasonCodes.QueryBytesExceeded,
                $"A memory query exceeds "
                + $"{_options.MaxQueryUtf8Bytes} UTF-8 bytes.");
        }

        var queryTerms = _queryTokenizer.TokenizeJson(
            query.Query,
            nameof(query));
        IndexedRecord[] records;
        Dictionary<string, int> documentFrequencies;
        long totalContentTerms;
        long totalTagTerms;
        lock (_sync)
        {
            records = _records.Values.ToArray();
            documentFrequencies = new Dictionary<string, int>(
                _documentFrequencies,
                StringComparer.Ordinal);
            totalContentTerms = _totalContentTerms;
            totalTagTerms = _totalTagTerms;
        }

        if (records.Length == 0)
        {
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(
                Array.Empty<MemorySearchResult>());
        }

        var orderedQueryTerms = queryTerms.Frequencies.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var averageContentLength = Math.Max(
            1.0,
            totalContentTerms / (double)records.Length);
        var averageTagLength = Math.Max(
            1.0,
            totalTagTerms / (double)records.Length);
        var comparisons = 0L;
        var ranked = new List<MemorySearchResult>();
        foreach (var indexed in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MemoryQueryFilter.Matches(indexed.Record, query))
            {
                continue;
            }

            var matched = orderedQueryTerms.Length == 0;
            var score = 0L;
            foreach (var term in orderedQueryTerms)
            {
                comparisons = checked(comparisons + 1);
                if (comparisons > _options.MaxComparisonsPerSearch)
                {
                    throw new LexicalSearchLimitException(
                        nameof(query),
                        LexicalSearchReasonCodes.ComparisonsExceeded,
                        $"Memory search exceeds "
                        + $"{_options.MaxComparisonsPerSearch} term/document "
                        + "comparisons.");
                }

                var contentFrequency =
                    indexed.Content.Frequencies.TryGetValue(
                        term,
                        out var contentValue)
                        ? contentValue
                        : 0;
                var tagFrequency =
                    indexed.Tags.Frequencies.TryGetValue(
                        term,
                        out var tagValue)
                        ? tagValue
                        : 0;
                if (contentFrequency == 0 && tagFrequency == 0)
                {
                    continue;
                }

                matched = true;
                var documentFrequency =
                    documentFrequencies.TryGetValue(
                        term,
                        out var frequency)
                        ? frequency
                        : 0;
                score += _scorer.ScoreTerm(
                    records.Length,
                    documentFrequency,
                    new[]
                    {
                        new Bm25FieldMatch(
                            contentFrequency,
                            indexed.Content.TermCount,
                            averageContentLength,
                            _options.ContentWeight,
                            _options.ContentLengthNormalization),
                        new Bm25FieldMatch(
                            tagFrequency,
                            indexed.Tags.TermCount,
                            averageTagLength,
                            _options.TagWeight,
                            _options.TagLengthNormalization)
                    });
            }

            if (!matched)
            {
                continue;
            }

            score += indexed.Record.Importance * 10L;
            ranked.Add(
                new MemorySearchResult(
                    indexed.Record,
                    SaturatingInt(score)));
        }

        ranked.Sort(CompareResults);
        var selected = SelectWithinResultBounds(ranked, query);
        return new ValueTask<IReadOnlyList<MemorySearchResult>>(
            new ReadOnlyCollection<MemorySearchResult>(selected));
    }

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyAtomicBatchAsync(
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        var prepared = Prepare(snapshot, cancellationToken);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staged = StageBatch(
                snapshot,
                prepared,
                allowLegacyReplay: false,
                cancellationToken);
            if (staged.Changed)
            {
                Commit(staged);
                _sourceRevision = checked(_sourceRevision + 1);
            }

            return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
                new ReadOnlyCollection<MemoryMutationResult>(
                    staged.Results));
        }
    }

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return ApplyIdempotentAtomicBatchCoreAsync(
            commitId,
            mutations,
            allowLegacyReplay: false,
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyLegacyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return ApplyIdempotentAtomicBatchCoreAsync(
            commitId,
            mutations,
            allowLegacyReplay: true,
            cancellationToken);
    }

    private ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchCoreAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            bool allowLegacyReplay,
            CancellationToken cancellationToken)
    {
        commitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        var digest =
            RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(snapshot);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_idempotentBatchDigests.TryGetValue(
                    commitId,
                    out var existingDigest))
            {
                if (!string.Equals(
                        existingDigest,
                        digest,
                        StringComparison.Ordinal))
                {
                    throw new MemoryBatchIdempotencyConflictException(
                        commitId);
                }

                return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
                    new ReadOnlyCollection<MemoryMutationResult>(
                        snapshot.Select(
                                mutation => new MemoryMutationResult(
                                    mutation.Kind,
                                    mutation.MemoryId,
                                    changed: false))
                            .ToArray()));
            }

            if (_idempotentBatchDigests.Count
                >= MemoryBatchLimits.MaxInMemoryIdempotencyKeys)
            {
                throw new RuntimeContentLimitException(
                    nameof(commitId),
                    MemoryBatchReasonCodes.IdempotencyCapacityExceeded,
                    "Memory batch idempotency capacity is exhausted.");
            }

            var prepared = Prepare(snapshot, cancellationToken);
            var staged = StageBatch(
                snapshot,
                prepared,
                allowLegacyReplay,
                cancellationToken);
            if (staged.Changed)
            {
                Commit(staged);
            }

            _idempotentBatchDigests.Add(commitId, digest);
            _sourceRevision = checked(_sourceRevision + 1);
            return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
                new ReadOnlyCollection<MemoryMutationResult>(
                    staged.Results));
        }
    }

    void IPreflightMemoryIndex.ValidateUpsert(
        MemoryRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var indexed = Index(record);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = StageUpsert(
                record.MemoryId,
                indexed,
                _records,
                _documentFrequencies,
                _indexedUtf8Bytes,
                _totalContentTerms,
                _totalTagTerms);
        }
    }

    void IPreflightMemoryIndex.ValidateAtomicBatch(
        IReadOnlyList<MemoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        var prepared = Prepare(snapshot, cancellationToken);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = StageBatch(
                snapshot,
                prepared,
                allowLegacyReplay: false,
                cancellationToken);
        }
    }

    private Dictionary<string, IndexedRecord> Prepare(
        IReadOnlyList<MemoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<string, IndexedRecord>(
            StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutation.Kind == MemoryMutationKind.Upsert)
            {
                prepared.Add(
                    mutation.MemoryId,
                    Index(
                        mutation.Record
                        ?? throw new InvalidOperationException(
                            "An upsert mutation requires a record.")));
            }
        }

        return prepared;
    }

    private StagedState StageBatch(
        IReadOnlyList<MemoryMutation> mutations,
        IReadOnlyDictionary<string, IndexedRecord> prepared,
        bool allowLegacyReplay,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, IndexedRecord>(
            _records,
            StringComparer.Ordinal);
        var frequencies = new Dictionary<string, int>(
            _documentFrequencies,
            StringComparer.Ordinal);
        var indexedBytes = _indexedUtf8Bytes;
        var contentTerms = _totalContentTerms;
        var tagTerms = _totalTagTerms;
        var results = new MemoryMutationResult[mutations.Count];
        var changed = false;
        for (var index = 0; index < mutations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = mutations[index];
            records.TryGetValue(mutation.MemoryId, out var current);
            if (allowLegacyReplay)
            {
                MemoryMutationAdmission.EnsureCanReplayLegacy(mutation);
            }
            else
            {
                MemoryMutationAdmission.EnsureCanApply(
                    mutation,
                    current?.Record);
            }
            if (mutation.Kind == MemoryMutationKind.Upsert)
            {
                var indexed = prepared[mutation.MemoryId];
                if (records.TryGetValue(
                        mutation.MemoryId,
                        out var existing))
                {
                    RemoveDocumentFrequencies(frequencies, existing);
                    indexedBytes -= existing.IndexedUtf8Bytes;
                    contentTerms -= existing.Content.TermCount;
                    tagTerms -= existing.Tags.TermCount;
                }

                records[mutation.MemoryId] = indexed;
                AddDocumentFrequencies(frequencies, indexed);
                indexedBytes = checked(
                    indexedBytes + indexed.IndexedUtf8Bytes);
                contentTerms = checked(
                    contentTerms + indexed.Content.TermCount);
                tagTerms = checked(tagTerms + indexed.Tags.TermCount);
                results[index] = new MemoryMutationResult(
                    mutation.Kind,
                    mutation.MemoryId,
                    changed: true);
                changed = true;
            }
            else
            {
                var deleted = records.TryGetValue(
                    mutation.MemoryId,
                    out var existing);
                if (deleted)
                {
                    records.Remove(mutation.MemoryId);
                    RemoveDocumentFrequencies(frequencies, existing!);
                    indexedBytes -= existing!.IndexedUtf8Bytes;
                    contentTerms -= existing.Content.TermCount;
                    tagTerms -= existing.Tags.TermCount;
                }

                results[index] = new MemoryMutationResult(
                    mutation.Kind,
                    mutation.MemoryId,
                    deleted);
                changed |= deleted;
            }
        }

        ValidateStagedCapacity(
            records.Count,
            indexedBytes,
            contentTerms,
            tagTerms);
        return new StagedState(
            records,
            frequencies,
            indexedBytes,
            contentTerms,
            tagTerms,
            results,
            changed);
    }

    private StagedState StageUpsert(
        string memoryId,
        IndexedRecord indexed,
        Dictionary<string, IndexedRecord> currentRecords,
        Dictionary<string, int> currentFrequencies,
        long currentBytes,
        long currentContentTerms,
        long currentTagTerms)
    {
        var records = new Dictionary<string, IndexedRecord>(
            currentRecords,
            StringComparer.Ordinal);
        var frequencies = new Dictionary<string, int>(
            currentFrequencies,
            StringComparer.Ordinal);
        if (records.TryGetValue(memoryId, out var existing))
        {
            RemoveDocumentFrequencies(frequencies, existing);
            currentBytes -= existing.IndexedUtf8Bytes;
            currentContentTerms -= existing.Content.TermCount;
            currentTagTerms -= existing.Tags.TermCount;
        }

        records[memoryId] = indexed;
        AddDocumentFrequencies(frequencies, indexed);
        var indexedBytes = checked(
            currentBytes + indexed.IndexedUtf8Bytes);
        var contentTerms = checked(
            currentContentTerms + indexed.Content.TermCount);
        var tagTerms = checked(
            currentTagTerms + indexed.Tags.TermCount);
        ValidateStagedCapacity(
            records.Count,
            indexedBytes,
            contentTerms,
            tagTerms);
        return new StagedState(
            records,
            frequencies,
            indexedBytes,
            contentTerms,
            tagTerms,
            Array.Empty<MemoryMutationResult>(),
            changed: true);
    }

    private IndexedRecord Index(MemoryRecord record)
    {
        var contentBytes = Encoding.UTF8.GetByteCount(
            record.Content.GetRawText());
        var tagBytes = 0L;
        foreach (var tag in record.Tags)
        {
            tagBytes = checked(
                tagBytes + Encoding.UTF8.GetByteCount(tag));
        }

        var documentBytes = checked(contentBytes + tagBytes);
        if (documentBytes > _options.MaxDocumentUtf8Bytes)
        {
            throw new LexicalSearchLimitException(
                nameof(record),
                LexicalSearchReasonCodes.DocumentBytesExceeded,
                $"A memory document exceeds "
                + $"{_options.MaxDocumentUtf8Bytes} UTF-8 bytes.");
        }

        var content = _documentTokenizer.TokenizeJson(
            record.Content,
            nameof(record));
        var tags = _documentTokenizer.TokenizeTextSegments(
            record.Tags,
            nameof(record));
        if ((long)content.TermCount + tags.TermCount
            > _options.MaxDocumentTerms)
        {
            throw new LexicalSearchLimitException(
                nameof(record),
                LexicalSearchReasonCodes.TermsExceeded,
                $"A memory document exceeds "
                + $"{_options.MaxDocumentTerms} terms.");
        }

        var unique = new HashSet<string>(
            content.Frequencies.Keys,
            StringComparer.Ordinal);
        foreach (var term in tags.Frequencies.Keys)
        {
            unique.Add(term);
            if (unique.Count > _options.MaxUniqueDocumentTerms)
            {
                throw new LexicalSearchLimitException(
                    nameof(record),
                    LexicalSearchReasonCodes.UniqueTermsExceeded,
                    $"A memory document exceeds "
                    + $"{_options.MaxUniqueDocumentTerms} unique terms.");
            }
        }

        return new IndexedRecord(
            record,
            content,
            tags,
            unique,
            documentBytes);
    }

    private void Commit(StagedState staged)
    {
        _records = staged.Records;
        _documentFrequencies = staged.DocumentFrequencies;
        _indexedUtf8Bytes = staged.IndexedUtf8Bytes;
        _totalContentTerms = staged.TotalContentTerms;
        _totalTagTerms = staged.TotalTagTerms;
    }

    private void ValidateStagedCapacity(
        int recordCount,
        long indexedBytes,
        long contentTerms,
        long tagTerms)
    {
        if (recordCount > _capacity)
        {
            throw new RuntimeContentLimitException(
                "records",
                "memory_capacity_exceeded",
                $"Memory capacity exceeds {_capacity} records.");
        }

        if (indexedBytes > _options.MaxIndexUtf8Bytes)
        {
            throw new LexicalSearchLimitException(
                "records",
                LexicalSearchReasonCodes.IndexBytesExceeded,
                $"Memory index exceeds "
                + $"{_options.MaxIndexUtf8Bytes} source UTF-8 bytes.");
        }

        if (contentTerms > _options.MaxIndexTerms - tagTerms)
        {
            throw new LexicalSearchLimitException(
                "records",
                LexicalSearchReasonCodes.IndexTermsExceeded,
                $"Memory index exceeds "
                + $"{_options.MaxIndexTerms} term occurrences.");
        }
    }

    private static void AddDocumentFrequencies(
        IDictionary<string, int> frequencies,
        IndexedRecord indexed)
    {
        foreach (var term in indexed.UniqueTerms)
        {
            frequencies[term] = frequencies.TryGetValue(
                term,
                out var value)
                ? checked(value + 1)
                : 1;
        }
    }

    private static void RemoveDocumentFrequencies(
        IDictionary<string, int> frequencies,
        IndexedRecord indexed)
    {
        foreach (var term in indexed.UniqueTerms)
        {
            var value = frequencies[term];
            if (value == 1)
            {
                frequencies.Remove(term);
            }
            else
            {
                frequencies[term] = value - 1;
            }
        }
    }

    private static int CompareResults(
        MemorySearchResult left,
        MemorySearchResult right)
    {
        var score = right.Score.CompareTo(left.Score);
        if (score != 0)
        {
            return score;
        }

        var updated = right.Record.UpdatedAt.CompareTo(
            left.Record.UpdatedAt);
        return updated != 0
            ? updated
            : StringComparer.Ordinal.Compare(
                left.Record.MemoryId,
                right.Record.MemoryId);
    }

    private static MemorySearchResult[] SelectWithinResultBounds(
        IReadOnlyList<MemorySearchResult> ranked,
        MemoryQuery query)
    {
        var selected = new List<MemorySearchResult>();
        var bytes = 0L;
        foreach (var result in ranked)
        {
            if (selected.Count >= query.MaxResults)
            {
                break;
            }

            var itemBytes = Encoding.UTF8.GetByteCount(
                result.Record.Content.GetRawText());
            if (bytes + itemBytes > query.MaxUtf8Bytes)
            {
                continue;
            }

            selected.Add(result);
            bytes += itemBytes;
        }

        return selected.ToArray();
    }

    private static int SaturatingInt(long value)
    {
        return value >= int.MaxValue
            ? int.MaxValue
            : checked((int)value);
    }

    private static void ValidateMemoryId(string memoryId)
    {
        _ = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
    }

    private sealed class IndexedRecord
    {
        public IndexedRecord(
            MemoryRecord record,
            TokenizedTerms content,
            TokenizedTerms tags,
            HashSet<string> uniqueTerms,
            long indexedUtf8Bytes)
        {
            Record = record;
            Content = content;
            Tags = tags;
            UniqueTerms = uniqueTerms;
            IndexedUtf8Bytes = indexedUtf8Bytes;
        }

        public MemoryRecord Record { get; }

        public TokenizedTerms Content { get; }

        public TokenizedTerms Tags { get; }

        public HashSet<string> UniqueTerms { get; }

        public long IndexedUtf8Bytes { get; }
    }

    private sealed class StagedState
    {
        public StagedState(
            Dictionary<string, IndexedRecord> records,
            Dictionary<string, int> documentFrequencies,
            long indexedUtf8Bytes,
            long totalContentTerms,
            long totalTagTerms,
            MemoryMutationResult[] results,
            bool changed)
        {
            Records = records;
            DocumentFrequencies = documentFrequencies;
            IndexedUtf8Bytes = indexedUtf8Bytes;
            TotalContentTerms = totalContentTerms;
            TotalTagTerms = totalTagTerms;
            Results = results;
            Changed = changed;
        }

        public Dictionary<string, IndexedRecord> Records { get; }

        public Dictionary<string, int> DocumentFrequencies { get; }

        public long IndexedUtf8Bytes { get; }

        public long TotalContentTerms { get; }

        public long TotalTagTerms { get; }

        public MemoryMutationResult[] Results { get; }

        public bool Changed { get; }
    }
}
