using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class ContextResourceReference
{
    public ContextResourceReference(
        string uri,
        string mediaType,
        string? digest = null,
        long? sizeBytes = null)
    {
        Uri = RuntimeGuard.RequiredUtf8(uri, 2_048, nameof(uri));
        MediaType = RuntimeGuard.RequiredUtf8(mediaType, 128, nameof(mediaType));
        if (digest is not null)
        {
            RuntimeGuard.RequiredUtf8(digest, 256, nameof(digest));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        Digest = digest;
        SizeBytes = sizeBytes;
    }

    public string Uri { get; }

    public string MediaType { get; }

    public string? Digest { get; }

    public long? SizeBytes { get; }

    internal ResourceReference ToProtocol()
    {
        return new ResourceReference
        {
            Uri = Uri,
            MediaType = MediaType,
            Digest = Digest,
            SizeBytes = SizeBytes
        };
    }
}

public sealed class ContextCandidate
{
    public ContextCandidate(
        string id,
        string category,
        JsonElement content,
        int priority = 0,
        bool required = false,
        bool canDefer = true,
        int? estimatedTokens = null,
        DateTimeOffset? expiresAt = null,
        string? provenance = null)
        : this(
            id,
            category,
            content.Clone(),
            null,
            priority,
            required,
            canDefer,
            estimatedTokens,
            expiresAt,
            provenance)
    {
    }

    public ContextCandidate(
        string id,
        string category,
        ContextResourceReference resource,
        int priority = 0,
        bool required = false,
        bool canDefer = true,
        int? estimatedTokens = null,
        DateTimeOffset? expiresAt = null,
        string? provenance = null)
        : this(
            id,
            category,
            null,
            resource ?? throw new ArgumentNullException(nameof(resource)),
            priority,
            required,
            canDefer,
            estimatedTokens,
            expiresAt,
            provenance)
    {
    }

    private ContextCandidate(
        string id,
        string category,
        JsonElement? content,
        ContextResourceReference? resource,
        int priority,
        bool required,
        bool canDefer,
        int? estimatedTokens,
        DateTimeOffset? expiresAt,
        string? provenance)
    {
        Id = RuntimeGuard.RequiredUtf8(id, 128, nameof(id));
        Category = RuntimeGuard.RequiredUtf8(category, 64, nameof(category));
        if (estimatedTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        }

        if (provenance is not null)
        {
            RuntimeGuard.RequiredUtf8(provenance, 256, nameof(provenance));
        }

        Content = content;
        Resource = resource;
        Priority = priority;
        Required = required;
        CanDefer = canDefer;
        EstimatedTokens = estimatedTokens;
        ExpiresAt = expiresAt;
        Provenance = provenance;
    }

    public string Id { get; }

    public string Category { get; }

    public JsonElement? Content { get; }

    public ContextResourceReference? Resource { get; }

    public int Priority { get; }

    public bool Required { get; }

    public bool CanDefer { get; }

    public int? EstimatedTokens { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public string? Provenance { get; }

    public static ContextCandidate FromObservation(
        ObservationEnvelope observation,
        bool required = false,
        bool canDefer = true,
        int? estimatedTokens = null)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        ProtocolValidator.EnsureValid(observation);
        DateTimeOffset? expiresAt = null;
        if (observation.TtlMs.HasValue)
        {
            expiresAt = observation.ObservedAt.AddMilliseconds(observation.TtlMs.Value);
        }

        var provenance = $"{observation.Source}:{observation.Trust}";
        if (observation.Payload.HasValue)
        {
            return new ContextCandidate(
                observation.ObservationId,
                observation.Kind,
                observation.Payload.Value,
                observation.Priority,
                required,
                canDefer,
                estimatedTokens,
                expiresAt,
                provenance);
        }

        var resource = observation.ResourceRef
                       ?? throw new ArgumentException(
                           "A valid observation must contain payload or resourceRef.",
                           nameof(observation));
        return new ContextCandidate(
            observation.ObservationId,
            observation.Kind,
            new ContextResourceReference(
                resource.Uri,
                resource.MediaType,
                resource.Digest,
                resource.SizeBytes),
            observation.Priority,
            required,
            canDefer,
            estimatedTokens,
            expiresAt,
            provenance);
    }
}

public sealed class ContextCompilerOptions
{
    public ContextCompilerOptions(
        int maxCandidates = 512,
        int maxSelectedItems = 128,
        int maxEstimatedTokens = 8_000,
        int maxUtf8Bytes = 262_144,
        int estimatedBytesPerToken = 4,
        JsonValueLimits? candidateJsonLimits = null)
    {
        if (maxCandidates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        }

        if (maxSelectedItems < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSelectedItems));
        }

        if (maxEstimatedTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEstimatedTokens));
        }

        if (maxUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        }

        if (estimatedBytesPerToken < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedBytesPerToken));
        }

        MaxCandidates = maxCandidates;
        MaxSelectedItems = maxSelectedItems;
        MaxEstimatedTokens = maxEstimatedTokens;
        MaxUtf8Bytes = maxUtf8Bytes;
        EstimatedBytesPerToken = estimatedBytesPerToken;
        CandidateJsonLimits = candidateJsonLimits ?? new JsonValueLimits(
            maxUtf8Bytes: Math.Min(maxUtf8Bytes, 131_072));
    }

    public int MaxCandidates { get; }

    public int MaxSelectedItems { get; }

    public int MaxEstimatedTokens { get; }

    public int MaxUtf8Bytes { get; }

    public int EstimatedBytesPerToken { get; }

    public JsonValueLimits CandidateJsonLimits { get; }
}

public sealed class ContextCompilationRequest
{
    public ContextCompilationRequest(
        string runId,
        string turnId,
        IEnumerable<ContextCandidate> candidates,
        DateTimeOffset now,
        SkillDisclosurePlan? skills = null,
        int maxCandidates = 512,
        CancellationToken cancellationToken = default)
    {
        RunId = RuntimeGuard.RequiredId(runId, nameof(runId));
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        Candidates = new ReadOnlyCollection<ContextCandidate>(
            RuntimeInputGuard.CopyBounded(
                candidates,
                maxCandidates,
                candidate => candidate,
                nameof(candidates),
                "context_candidate_count_exceeded",
                cancellationToken));
        Now = now;
        Skills = skills;
    }

    public string RunId { get; }

    public string TurnId { get; }

    public IReadOnlyList<ContextCandidate> Candidates { get; }

    public DateTimeOffset Now { get; }

    public SkillDisclosurePlan? Skills { get; }
}

public sealed class CompiledContextItem
{
    internal CompiledContextItem(ContextCandidate candidate, int utf8Bytes, int estimatedTokens)
    {
        Candidate = candidate;
        Utf8Bytes = utf8Bytes;
        EstimatedTokens = estimatedTokens;
    }

    public ContextCandidate Candidate { get; }

    public int Utf8Bytes { get; }

    public int EstimatedTokens { get; }
}

public sealed class CompiledContext
{
    internal CompiledContext(
        IReadOnlyList<CompiledContextItem> selected,
        SkillDisclosurePlan? skills,
        ContextBudgetReport budgetReport,
        int utf8Bytes)
    {
        Selected = selected;
        Skills = skills;
        BudgetReport = budgetReport;
        Utf8Bytes = utf8Bytes;
    }

    public IReadOnlyList<CompiledContextItem> Selected { get; }

    public SkillDisclosurePlan? Skills { get; }

    public ContextBudgetReport BudgetReport { get; }

    public int Utf8Bytes { get; }
}

public sealed class ContextBudgetExceededException : InvalidOperationException
{
    public ContextBudgetExceededException(string budgetCode, string message)
        : base(message)
    {
        BudgetCode = budgetCode;
    }

    public string BudgetCode { get; }
}

public sealed class ContextCompiler
{
    private readonly ContextCompilerOptions _options;

    public ContextCompiler(ContextCompilerOptions? options = null)
    {
        _options = options ?? new ContextCompilerOptions();
    }

    internal int MaxCandidates => _options.MaxCandidates;

    internal void ValidateCandidate(ContextCandidate candidate)
    {
        _ = Measure(candidate);
    }

    public CompiledContext Compile(ContextCompilationRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Candidates.Count > _options.MaxCandidates)
        {
            throw new RuntimeContentLimitException(
                nameof(request.Candidates),
                "context_candidate_count_exceeded",
                $"Context candidates exceed {_options.MaxCandidates}.");
        }

        var prepared = Prepare(request);
        var selected = new List<CompiledContextItem>();
        var deferred = new List<string>();
        var pruned = new List<PrunedContextItem>();
        var externalized = new List<ResourceReference>();
        var reasonCodes = new HashSet<string>(StringComparer.Ordinal);
        var usedBytes = request.Skills?.EstimatedUtf8Bytes ?? 0;
        var usedTokens = EstimateTokens(usedBytes);

        EnsureSkillBudget(usedBytes, usedTokens);

        foreach (var item in prepared.Where(value => value.Candidate.Required))
        {
            if (IsExpired(item.Candidate, request.Now))
            {
                throw new ContextBudgetExceededException(
                    "required_context_expired",
                    $"Required context '{item.Candidate.Id}' is expired.");
            }

            EnsureRequiredFits(item, selected.Count, usedBytes, usedTokens);
            Select(item, selected, externalized);
            usedBytes = checked(usedBytes + item.Utf8Bytes);
            usedTokens = checked(usedTokens + item.EstimatedTokens);
        }

        foreach (var item in prepared.Where(value => !value.Candidate.Required))
        {
            if (IsExpired(item.Candidate, request.Now))
            {
                AddPruned(pruned, reasonCodes, item.Candidate, "expired");
                continue;
            }

            if (Fits(item, selected.Count, usedBytes, usedTokens))
            {
                Select(item, selected, externalized);
                usedBytes = checked(usedBytes + item.Utf8Bytes);
                usedTokens = checked(usedTokens + item.EstimatedTokens);
                continue;
            }

            if (item.Candidate.CanDefer)
            {
                deferred.Add(item.Candidate.Id);
                reasonCodes.Add("deferred_budget");
            }
            else
            {
                AddPruned(pruned, reasonCodes, item.Candidate, "budget_exceeded");
            }
        }

        var report = new ContextBudgetReport
        {
            RunId = request.RunId,
            TurnId = request.TurnId,
            InputCount = request.Candidates.Count,
            SelectedIds = selected.Select(item => item.Candidate.Id).ToList(),
            DeferredIds = deferred,
            Pruned = pruned,
            Externalized = externalized,
            EstimatedTokens = usedTokens,
            BudgetLimit = _options.MaxEstimatedTokens,
            ReasonCodes = reasonCodes.OrderBy(item => item, StringComparer.Ordinal).ToList()
        };

        return new CompiledContext(
            new ReadOnlyCollection<CompiledContextItem>(selected),
            request.Skills,
            report,
            usedBytes);
    }

    private IReadOnlyList<PreparedCandidate> Prepare(ContextCompilationRequest request)
    {
        var result = new List<PreparedCandidate>(request.Candidates.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in request.Candidates)
        {
            if (candidate is null)
            {
                throw new ArgumentException(
                    "A context candidate cannot be null.",
                    nameof(request));
            }

            if (!ids.Add(candidate.Id))
            {
                throw new ArgumentException(
                    $"Context candidate '{candidate.Id}' appears more than once.",
                    nameof(request));
            }

            var bytes = Measure(candidate);
            var measuredTokens = EstimateTokens(bytes);
            var tokens = candidate.EstimatedTokens.HasValue
                ? Math.Max(candidate.EstimatedTokens.Value, measuredTokens)
                : measuredTokens;
            result.Add(new PreparedCandidate(candidate, bytes, tokens));
        }

        result.Sort((left, right) =>
        {
            var required = right.Candidate.Required.CompareTo(left.Candidate.Required);
            if (required != 0)
            {
                return required;
            }

            var priority = right.Candidate.Priority.CompareTo(left.Candidate.Priority);
            return priority != 0
                ? priority
                : StringComparer.Ordinal.Compare(left.Candidate.Id, right.Candidate.Id);
        });
        return result;
    }

    private int Measure(ContextCandidate candidate)
    {
        if (candidate.Content.HasValue)
        {
            return JsonValueInspector.ValidateAndMeasure(
                candidate.Content.Value,
                _options.CandidateJsonLimits,
                candidate.Id);
        }

        var resource = candidate.Resource
                       ?? throw new ArgumentException(
                           "A context candidate must contain content or a resource reference.",
                           nameof(candidate));
        return checked(
            Encoding.UTF8.GetByteCount(resource.Uri)
            + Encoding.UTF8.GetByteCount(resource.MediaType)
            + (resource.Digest is null ? 0 : Encoding.UTF8.GetByteCount(resource.Digest))
            + 24);
    }

    private void EnsureSkillBudget(int bytes, int tokens)
    {
        if (bytes > _options.MaxUtf8Bytes)
        {
            throw new ContextBudgetExceededException(
                "skill_context_bytes_exceeded",
                "Skill disclosure alone exceeds the context byte budget.");
        }

        if (tokens > _options.MaxEstimatedTokens)
        {
            throw new ContextBudgetExceededException(
                "skill_context_tokens_exceeded",
                "Skill disclosure alone exceeds the context token budget.");
        }
    }

    private void EnsureRequiredFits(
        PreparedCandidate item,
        int selectedCount,
        int usedBytes,
        int usedTokens)
    {
        if (!Fits(item, selectedCount, usedBytes, usedTokens))
        {
            throw new ContextBudgetExceededException(
                "required_context_budget_exceeded",
                $"Required context '{item.Candidate.Id}' does not fit the configured budget.");
        }
    }

    private bool Fits(
        PreparedCandidate item,
        int selectedCount,
        int usedBytes,
        int usedTokens)
    {
        return selectedCount < _options.MaxSelectedItems
               && (long)usedBytes + item.Utf8Bytes <= _options.MaxUtf8Bytes
               && (long)usedTokens + item.EstimatedTokens <= _options.MaxEstimatedTokens;
    }

    private static bool IsExpired(ContextCandidate candidate, DateTimeOffset now)
    {
        return candidate.ExpiresAt.HasValue && candidate.ExpiresAt.Value <= now;
    }

    private static void Select(
        PreparedCandidate item,
        ICollection<CompiledContextItem> selected,
        ICollection<ResourceReference> externalized)
    {
        selected.Add(new CompiledContextItem(
            item.Candidate,
            item.Utf8Bytes,
            item.EstimatedTokens));
        if (item.Candidate.Resource is not null)
        {
            externalized.Add(item.Candidate.Resource.ToProtocol());
        }
    }

    private static void AddPruned(
        ICollection<PrunedContextItem> pruned,
        ISet<string> reasonCodes,
        ContextCandidate candidate,
        string reasonCode)
    {
        pruned.Add(new PrunedContextItem
        {
            Id = candidate.Id,
            Category = candidate.Category,
            ReasonCode = reasonCode
        });
        reasonCodes.Add(reasonCode);
    }

    private int EstimateTokens(int bytes)
    {
        if (bytes <= 0)
        {
            return 0;
        }

        return Math.Max(1, checked((bytes + _options.EstimatedBytesPerToken - 1)
                                   / _options.EstimatedBytesPerToken));
    }

    private sealed class PreparedCandidate
    {
        public PreparedCandidate(ContextCandidate candidate, int utf8Bytes, int estimatedTokens)
        {
            Candidate = candidate;
            Utf8Bytes = utf8Bytes;
            EstimatedTokens = estimatedTokens;
        }

        public ContextCandidate Candidate { get; }

        public int Utf8Bytes { get; }

        public int EstimatedTokens { get; }
    }
}
