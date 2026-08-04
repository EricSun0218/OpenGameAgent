using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace GameAgent.Generation;

public sealed class FirstCapableGenerationRoutePolicy : IGenerationRoutePolicy
{
    public IGenerationProvider Select(
        GenerationRequest request,
        IReadOnlyList<IGenerationProvider> providers)
    {
        foreach (var provider in providers)
        {
            if (provider.Capabilities.Modalities.Contains(
                    request.Modality,
                    StringComparer.Ordinal))
            {
                return provider;
            }
        }

        throw new GenerationOperationException(
            "generation_provider_unavailable",
            $"No configured provider supports modality '{request.Modality}'.");
    }
}

public sealed class InMemoryGenerationJobStore : IGenerationJobStore
{
    private readonly int _maximumJobs;
    private readonly ConcurrentDictionary<string, GenerationJob> _jobs =
        new(StringComparer.Ordinal);

    public InMemoryGenerationJobStore(int maximumJobs = 4_096)
    {
        if (maximumJobs is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        }

        _maximumJobs = maximumJobs;
    }

    public ValueTask<GenerationJob?> TryGetAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs.TryGetValue(operationId, out var job);
        return new ValueTask<GenerationJob?>(
            job is null ? null : GenerationValidation.SnapshotJob(job));
    }

    public ValueTask PutAsync(
        GenerationJob job,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = GenerationValidation.SnapshotJob(job);
        while (true)
        {
            if (_jobs.TryGetValue(snapshot.OperationId, out var current))
            {
                if (snapshot.Revision != checked(current.Revision + 1))
                {
                    throw new GenerationOperationException(
                        "generation_revision_conflict",
                        "The generation job update is stale.");
                }

                if (_jobs.TryUpdate(snapshot.OperationId, snapshot, current))
                {
                    return default;
                }

                continue;
            }

            if (snapshot.Revision != 1)
            {
                throw new GenerationOperationException(
                    "generation_revision_conflict",
                    "A new generation job must start at revision one.");
            }

            if (_jobs.Count >= _maximumJobs)
            {
                throw new GenerationOperationException(
                    "generation_job_capacity_exceeded",
                    $"The job store reached its {_maximumJobs} job limit.");
            }

            if (_jobs.TryAdd(snapshot.OperationId, snapshot))
            {
                return default;
            }
        }
    }

    public ValueTask<IReadOnlyList<GenerationJob>> ListUnfinishedAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        IReadOnlyList<GenerationJob> result = new ReadOnlyCollection<GenerationJob>(
            _jobs.Values
                .Where(job => !GenerationJobStatuses.IsTerminal(job.Status))
                .OrderBy(job => job.CreatedAt)
                .ThenBy(job => job.OperationId, StringComparer.Ordinal)
                .Take(maximumCount)
                .Select(GenerationValidation.SnapshotJob)
                .ToArray());
        return new ValueTask<IReadOnlyList<GenerationJob>>(result);
    }
}

public sealed class GenerationRuntime
{
    private readonly IReadOnlyList<IGenerationProvider> _providers;
    private readonly IReadOnlyDictionary<string, IGenerationProvider> _providersByName;
    private readonly IGenerationRoutePolicy _routePolicy;
    private readonly IGenerationJobStore _jobs;
    private readonly IGenerationArtifactStore _artifacts;
    private readonly IGenerationEventSink? _events;
    private readonly GenerationRuntimeOptions _options;
    private readonly SemaphoreSlim _submissions;
    private readonly SemaphoreSlim[] _operationGates;

    public GenerationRuntime(
        IEnumerable<IGenerationProvider> providers,
        IGenerationJobStore jobs,
        IGenerationArtifactStore artifacts,
        IGenerationRoutePolicy? routePolicy = null,
        IGenerationEventSink? events = null,
        GenerationRuntimeOptions? options = null)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        _options = options ?? new GenerationRuntimeOptions();
        _options.Validate();
        var materialized = providers.Take(129).ToArray();
        if (materialized.Length is 0 or > 128
            || materialized.Any(provider => provider is null))
        {
            throw new ArgumentException(
                "Configure between 1 and 128 generation providers.",
                nameof(providers));
        }

        var names = new Dictionary<string, IGenerationProvider>(StringComparer.Ordinal);
        foreach (var provider in materialized)
        {
            var name = GenerationValidation.Identifier(
                provider.Name,
                nameof(providers),
                128);
            if (!names.TryAdd(name, provider))
            {
                throw new ArgumentException(
                    $"Generation provider '{name}' is configured more than once.",
                    nameof(providers));
            }

            var capabilities = provider.Capabilities;
            if (capabilities is null
                || capabilities.Modalities is null
                || capabilities.Modalities.Count > 16
                || capabilities.Modalities.Distinct(StringComparer.Ordinal).Count()
                != capabilities.Modalities.Count
                || capabilities.Modalities.Any(
                    modality => !GenerationModalities.IsKnown(modality)))
            {
                throw new ArgumentException(
                    $"Generation provider '{name}' exposes invalid capabilities.",
                    nameof(providers));
            }
        }

        _providers = new ReadOnlyCollection<IGenerationProvider>(materialized);
        _providersByName = new ReadOnlyDictionary<string, IGenerationProvider>(names);
        _routePolicy = routePolicy ?? new FirstCapableGenerationRoutePolicy();
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _events = events;
        _submissions = new SemaphoreSlim(
            _options.MaxConcurrentSubmissions,
            _options.MaxConcurrentSubmissions);
        _operationGates = Enumerable.Range(0, 257)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    public async ValueTask<GenerationJob> SubmitAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshot = GenerationValidation.SnapshotRequest(request, _options);
        var gate = GetOperationGate(snapshot.OperationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _jobs
                .TryGetAsync(snapshot.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureSameOperation(existing, snapshot);
                if (existing.Status != GenerationJobStatuses.Queued
                    || existing.Acceptance != GenerationAcceptance.NotAccepted)
                {
                    return existing;
                }
            }

            IGenerationProvider provider = existing is not null
                ? _providersByName.TryGetValue(existing.Provider, out var persistedProvider)
                    ? persistedProvider
                    : throw new GenerationOperationException(
                        "generation_provider_unavailable",
                        $"Configured provider '{existing.Provider}' is unavailable.")
                : _routePolicy.Select(snapshot, _providers)
                           ?? throw new GenerationOperationException(
                               "generation_provider_unavailable",
                               "The generation route policy returned no provider.");
            if (!_providersByName.TryGetValue(provider.Name, out var configured)
                || !ReferenceEquals(provider, configured))
            {
                throw new GenerationOperationException(
                    "generation_route_invalid",
                    "The generation route policy returned an unconfigured provider.");
            }

            var job = existing;
            if (job is null)
            {
                var now = DateTimeOffset.UtcNow;
                job = new GenerationJob
                {
                    OperationId = snapshot.OperationId,
                    RequestDigest = GenerationValidation.ComputeRequestDigest(snapshot),
                    Modality = snapshot.Modality,
                    Provider = provider.Name,
                    Acceptance = GenerationAcceptance.NotAccepted,
                    Status = GenerationJobStatuses.Queued,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AuthorityId = snapshot.AuthorityId,
                    Revision = 1
                };
                try
                {
                    await _jobs.PutAsync(job, cancellationToken).ConfigureAwait(false);
                    await PublishAsync(job, "submitted", null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (GenerationOperationException exception) when (
                    exception.ReasonCode == "generation_revision_conflict")
                {
                    job = await RequireJobAsync(snapshot.OperationId, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureSameOperation(job, snapshot);
                    if (job.Status != GenerationJobStatuses.Queued
                        || job.Acceptance != GenerationAcceptance.NotAccepted)
                    {
                        return job;
                    }

                    if (!_providersByName.TryGetValue(job.Provider, out provider))
                    {
                        throw new GenerationOperationException(
                            "generation_provider_unavailable",
                            $"Configured provider '{job.Provider}' is unavailable.");
                    }
                }
            }

            try
            {
                await _submissions.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job = CloneWith(
                    job,
                    acceptance: GenerationAcceptance.NotAccepted,
                    status: GenerationJobStatuses.Cancelled,
                    errorCode: "generation_submission_cancelled_locally",
                    errorMessage: "Generation was cancelled before provider dispatch.");
                await _jobs.PutAsync(job, CancellationToken.None).ConfigureAwait(false);
                await PublishAsync(
                        job,
                        "submission_cancelled",
                        job.ErrorCode,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            try
            {
                var claim = await ClaimProviderDispatchAsync(job, snapshot)
                    .ConfigureAwait(false);
                job = claim.Job;
                if (!claim.Claimed)
                {
                    return GenerationValidation.SnapshotJob(job);
                }

                await PublishAsync(job, "dispatch_started", null, CancellationToken.None)
                    .ConfigureAwait(false);

                GenerationSubmission submission;
                try
                {
                    submission = await provider
                        .SubmitAsync(snapshot, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (GenerationProviderException exception)
                {
                    job = ApplyProviderFailure(job, exception);
                    await _jobs.PutAsync(job, CancellationToken.None)
                        .ConfigureAwait(false);
                    await PublishAsync(job, "submission_failed", exception.ReasonCode, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new GenerationOperationException(
                        exception.ReasonCode,
                        exception.Message,
                        exception.Acceptance != GenerationAcceptance.NotAccepted,
                        exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    job = ApplyUnknownFailure(
                        job,
                        "generation_submission_outcome_unknown",
                        exception.Message);
                    await _jobs.PutAsync(job, CancellationToken.None)
                        .ConfigureAwait(false);
                    await PublishAsync(job, "submission_uncertain", job.ErrorCode, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new GenerationOperationException(
                        job.ErrorCode!,
                        "The provider submission outcome is unknown; reconcile this operation instead of retrying it.",
                        outcomeUncertain: true,
                        exception);
                }
                catch (OperationCanceledException exception)
                {
                    job = ApplyUnknownFailure(
                        job,
                        "generation_submission_cancelled_uncertain",
                        "Cancellation interrupted provider submission; acceptance is unknown.");
                    await _jobs.PutAsync(job, CancellationToken.None)
                        .ConfigureAwait(false);
                    await PublishAsync(job, "submission_uncertain", job.ErrorCode, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new GenerationOperationException(
                        job.ErrorCode!,
                        job.ErrorMessage!,
                        outcomeUncertain: true,
                        exception);
                }

                job = await PersistProviderResultAsync(
                        job,
                        submission,
                        "submission_updated")
                    .ConfigureAwait(false);
                return GenerationValidation.SnapshotJob(job);
            }
            finally
            {
                _submissions.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GenerationJob?> TryGetAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        GenerationValidation.Identifier(operationId, nameof(operationId), 128);
        return await _jobs.TryGetAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<GenerationJob> RefreshAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetOperationGate(operationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await RequireJobAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            if (GenerationJobStatuses.IsTerminal(job.Status))
            {
                return job;
            }

            if (job.Status == GenerationJobStatuses.Materializing
                && job.PendingArtifacts.Count > 0)
            {
                var recovered = await ResumeMaterializationAsync(job)
                    .ConfigureAwait(false);
                return GenerationValidation.SnapshotJob(recovered);
            }

            if (job.ProviderJobId is null
                || !_providersByName.TryGetValue(job.Provider, out var provider))
            {
                throw new GenerationOperationException(
                    "generation_reconciliation_required",
                    "The job has no provider identity and cannot be polled automatically.",
                    outcomeUncertain: true);
            }

            var result = await provider
                .GetAsync(job.ProviderJobId, job.Modality, cancellationToken)
                .ConfigureAwait(false);
            var updated = await PersistProviderResultAsync(
                    job,
                    new GenerationSubmission
                    {
                        Acceptance = GenerationAcceptance.Accepted,
                        Result = result
                    },
                    "progress")
                .ConfigureAwait(false);
            return GenerationValidation.SnapshotJob(updated);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GenerationJob> WaitForCompletionAsync(
        string operationId,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? _options.MaximumWait;
        var effectivePoll = pollInterval ?? _options.DefaultPollInterval;
        if (effectiveTimeout <= TimeSpan.Zero
            || effectiveTimeout > _options.MaximumWait
            || effectivePoll < _options.MinimumPollInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Wait and polling intervals exceed configured bounds.");
        }

        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var current = await RequireJobAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            if (GenerationJobStatuses.IsTerminal(current.Status))
            {
                return current;
            }

            if (DateTimeOffset.UtcNow - started >= effectiveTimeout)
            {
                throw new TimeoutException(
                    "The local wait expired. The provider job was not cancelled.");
            }

            await Task.Delay(effectivePoll, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<GenerationJob> RequestCancellationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetOperationGate(operationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await RequireJobAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            if (GenerationJobStatuses.IsTerminal(job.Status))
            {
                return job;
            }

            if (job.ProviderJobId is null
                || !_providersByName.TryGetValue(job.Provider, out var provider))
            {
                return await MarkCancelRequestedAsync(job, cancellationToken)
                    .ConfigureAwait(false);
            }

            GenerationCancelResult result;
            try
            {
                result = await provider
                    .CancelAsync(job.ProviderJobId, job.Modality, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var pending = CloneWith(
                    job,
                    status: GenerationJobStatuses.CancelRequested,
                    errorCode: "generation_cancel_outcome_unknown",
                    errorMessage: "The provider cancellation outcome is unknown.");
                await _jobs.PutAsync(pending, CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(
                        pending,
                        "cancel_uncertain",
                        pending.ErrorCode,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw new GenerationOperationException(
                    pending.ErrorCode!,
                    pending.ErrorMessage!,
                    outcomeUncertain: true,
                    exception);
            }

            if (result is null
                || !GenerationJobStatuses.IsKnown(result.Status)
                || result.Status is not GenerationJobStatuses.Cancelled
                    and not GenerationJobStatuses.CancelRequested
                    and not GenerationJobStatuses.Unknown)
            {
                var pending = CloneWith(
                    job,
                    status: GenerationJobStatuses.CancelRequested,
                    errorCode: "generation_cancel_contract_invalid",
                    errorMessage: "The provider returned an invalid cancellation result.");
                await _jobs.PutAsync(pending, CancellationToken.None)
                    .ConfigureAwait(false);
                throw new GenerationOperationException(
                    pending.ErrorCode!,
                    pending.ErrorMessage!,
                    outcomeUncertain: true);
            }

            var status = result.Status == GenerationJobStatuses.Cancelled
                ? GenerationJobStatuses.Cancelled
                : GenerationJobStatuses.CancelRequested;
            var updated = CloneWith(job, status: status);
            await _jobs.PutAsync(updated, CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(updated, "cancel_requested", null, CancellationToken.None)
                .ConfigureAwait(false);
            return GenerationValidation.SnapshotJob(updated);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GenerationJob>> RecoverUnfinishedAsync(
        int maximumCount = 256,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount < 1 || maximumCount > _options.MaxTrackedJobs)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var unfinished = await _jobs
            .ListUnfinishedAsync(maximumCount, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<GenerationJob>(unfinished.Count);
        foreach (var job in unfinished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (job.ProviderJobId is null
                && (job.Status != GenerationJobStatuses.Materializing
                    || job.PendingArtifacts.Count == 0))
            {
                results.Add(job);
                continue;
            }

            try
            {
                results.Add(
                    await RefreshAsync(job.OperationId, cancellationToken)
                        .ConfigureAwait(false));
            }
            catch (GenerationProviderException)
            {
                results.Add(
                    await RequireJobAsync(job.OperationId, cancellationToken)
                        .ConfigureAwait(false));
            }
            catch (GenerationOperationException exception) when (
                exception.OutcomeUncertain)
            {
                results.Add(
                    await RequireJobAsync(job.OperationId, cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        return new ReadOnlyCollection<GenerationJob>(results);
    }

    private async ValueTask<GenerationJob> ResumeMaterializationAsync(
        GenerationJob job)
    {
        var result = new GenerationProviderResult
        {
            Status = GenerationJobStatuses.Succeeded,
            ProviderJobId = job.ProviderJobId,
            Progress = job.Progress,
            Output = job.Output?.Clone(),
            Artifacts = job.PendingArtifacts,
            ErrorCode = job.ErrorCode,
            ErrorMessage = job.ErrorMessage,
            Retryable = job.Retryable,
            CostUsd = job.CostUsd
        };

        try
        {
            var completed = await ApplyProviderResultAsync(
                    job,
                    job.Acceptance,
                    result,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ValidateTransition(job.Status, completed.Status);
            await _jobs.PutAsync(completed, CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(completed, "artifact_materialized", null, CancellationToken.None)
                .ConfigureAwait(false);
            return completed;
        }
        catch (Exception exception)
        {
            var pending = CloneWith(
                job,
                status: GenerationJobStatuses.Materializing,
                errorCode: "generation_artifact_import_incomplete",
                errorMessage: "Generated artifacts were not fully imported; reconcile the operation.");
            await _jobs.PutAsync(pending, CancellationToken.None).ConfigureAwait(false);
            throw new GenerationOperationException(
                pending.ErrorCode!,
                pending.ErrorMessage!,
                outcomeUncertain: true,
                exception);
        }
    }

    private async ValueTask<GenerationJob> ApplyProviderResultAsync(
        GenerationJob job,
        string acceptance,
        GenerationProviderResult result,
        CancellationToken cancellationToken)
    {
        var imported = new List<GenerationArtifact>(result.Artifacts.Count);
        if (result.Status == GenerationJobStatuses.Succeeded)
        {
            for (var index = 0; index < result.Artifacts.Count; index++)
            {
                imported.Add(
                    await _artifacts.ImportAsync(
                            job.OperationId,
                            index,
                            result.Artifacts[index],
                            cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        return new GenerationJob
        {
            OperationId = job.OperationId,
            RequestDigest = job.RequestDigest,
            Modality = job.Modality,
            Provider = job.Provider,
            ProviderJobId = result.ProviderJobId ?? job.ProviderJobId,
            Acceptance = acceptance,
            Status = result.Status,
            Progress = result.Progress,
            CreatedAt = job.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Output = result.Output?.Clone(),
            Artifacts = new ReadOnlyCollection<GenerationArtifact>(imported),
            PendingArtifacts = Array.Empty<GenerationArtifactSource>(),
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            Retryable = result.Retryable,
            CostUsd = result.CostUsd,
            AuthorityId = job.AuthorityId,
            Revision = checked(job.Revision + 1)
        };
    }

    private async ValueTask<DispatchClaim> ClaimProviderDispatchAsync(
        GenerationJob job,
        GenerationRequest request)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            if (job.Status != GenerationJobStatuses.Queued
                || job.Acceptance != GenerationAcceptance.NotAccepted)
            {
                return new DispatchClaim(false, job);
            }

            var dispatching = CloneWith(
                job,
                acceptance: GenerationAcceptance.Unknown,
                status: GenerationJobStatuses.Unknown,
                errorCode: "generation_dispatch_in_progress",
                errorMessage: "Provider dispatch started; acceptance is not yet durably known.");
            try
            {
                await _jobs.PutAsync(dispatching, CancellationToken.None)
                    .ConfigureAwait(false);
                return new DispatchClaim(true, dispatching);
            }
            catch (GenerationOperationException exception) when (
                exception.ReasonCode == "generation_revision_conflict")
            {
                job = await RequireJobAsync(request.OperationId, CancellationToken.None)
                    .ConfigureAwait(false);
                EnsureSameOperation(job, request);
            }
        }

        throw new GenerationOperationException(
            "generation_dispatch_contention",
            "The generation dispatch changed too frequently to claim safely.");
    }

    private async ValueTask<GenerationJob> PersistProviderResultAsync(
        GenerationJob job,
        GenerationSubmission submission,
        string eventKind)
    {
        try
        {
            if (submission is null
                || (submission.Acceptance != GenerationAcceptance.Accepted
                    && submission.Acceptance != GenerationAcceptance.NotAccepted
                    && submission.Acceptance != GenerationAcceptance.Unknown))
            {
                throw new GenerationOperationException(
                    "generation_provider_contract_invalid",
                    "The provider returned an invalid submission acceptance value.");
            }

            GenerationValidation.ValidateProviderResult(
                submission.Result,
                _options);
            if (submission.Result.Status == GenerationJobStatuses.Materializing
                || submission.Acceptance == GenerationAcceptance.NotAccepted
                && submission.Result.Status is not GenerationJobStatuses.Failed
                    and not GenerationJobStatuses.Cancelled
                || !GenerationJobStatuses.IsTerminal(submission.Result.Status)
                && submission.Result.ProviderJobId is null
                && job.ProviderJobId is null)
            {
                throw new GenerationOperationException(
                    "generation_provider_contract_invalid",
                    "The provider returned an inconsistent generation result.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var unknown = ApplyUnknownFailure(
                job,
                "generation_provider_contract_invalid",
                "The provider response could not be durably interpreted.");
            if (TryProviderJobId(submission?.Result?.ProviderJobId, out var providerJobId))
            {
                unknown.ProviderJobId = providerJobId;
            }

            await _jobs.PutAsync(unknown, CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(
                    unknown,
                    "provider_contract_invalid",
                    unknown.ErrorCode,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new GenerationOperationException(
                "generation_provider_contract_invalid",
                "The provider returned an invalid result; reconcile this operation instead of retrying it.",
                outcomeUncertain: true,
                exception);
        }

        var result = submission.Result;
        var requiresImport = result.Status == GenerationJobStatuses.Succeeded
                             && result.Artifacts.Count > 0;
        var checkpoint = CreateProviderCheckpoint(
            job,
            submission.Acceptance,
            result,
            requiresImport
                ? GenerationJobStatuses.Materializing
                : result.Status);
        ValidateTransition(job.Status, checkpoint.Status);
        await _jobs.PutAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
        if (!requiresImport)
        {
            await PublishAsync(checkpoint, eventKind, null, CancellationToken.None)
                .ConfigureAwait(false);
            return checkpoint;
        }

        try
        {
            var completed = await ApplyProviderResultAsync(
                    checkpoint,
                    submission.Acceptance,
                    result,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ValidateTransition(checkpoint.Status, completed.Status);
            await _jobs.PutAsync(completed, CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(completed, eventKind, null, CancellationToken.None)
                .ConfigureAwait(false);
            return completed;
        }
        catch (Exception exception)
        {
            var pending = CloneWith(
                checkpoint,
                status: GenerationJobStatuses.Materializing,
                errorCode: "generation_artifact_import_incomplete",
                errorMessage: "Generated artifacts were not fully imported; reconcile the operation.");
            await _jobs.PutAsync(pending, CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(
                    pending,
                    "artifact_import_incomplete",
                    pending.ErrorCode,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new GenerationOperationException(
                pending.ErrorCode!,
                pending.ErrorMessage!,
                outcomeUncertain: true,
                exception);
        }
    }

    private static GenerationJob CreateProviderCheckpoint(
        GenerationJob job,
        string acceptance,
        GenerationProviderResult result,
        string status) =>
        new()
        {
            OperationId = job.OperationId,
            RequestDigest = job.RequestDigest,
            Modality = job.Modality,
            Provider = job.Provider,
            ProviderJobId = result.ProviderJobId ?? job.ProviderJobId,
            Acceptance = acceptance,
            Status = status,
            Progress = result.Progress,
            CreatedAt = job.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Output = result.Output?.Clone(),
            Artifacts = job.Artifacts,
            PendingArtifacts = RequiresPendingArtifacts(status)
                ? new ReadOnlyCollection<GenerationArtifactSource>(
                    result.Artifacts
                        .Select(GenerationValidation.SnapshotArtifactSource)
                        .ToArray())
                : Array.Empty<GenerationArtifactSource>(),
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            Retryable = result.Retryable,
            CostUsd = result.CostUsd,
            AuthorityId = job.AuthorityId,
            Revision = checked(job.Revision + 1)
        };

    private static bool RequiresPendingArtifacts(string status) =>
        status == GenerationJobStatuses.Materializing;

    private static bool TryProviderJobId(string? value, out string providerJobId)
    {
        if (value is { Length: > 0 and <= 256 }
            && !value.Any(char.IsControl))
        {
            providerJobId = value;
            return true;
        }

        providerJobId = string.Empty;
        return false;
    }

    private static GenerationJob ApplyProviderFailure(
        GenerationJob job,
        GenerationProviderException exception)
    {
        var status = exception.Acceptance == GenerationAcceptance.NotAccepted
            ? GenerationJobStatuses.Failed
            : GenerationJobStatuses.Unknown;
        return CloneWith(
            job,
            acceptance: exception.Acceptance,
            status: status,
            errorCode: exception.ReasonCode,
            errorMessage: exception.Message,
            retryable: exception.Retryable);
    }

    private static GenerationJob ApplyUnknownFailure(
        GenerationJob job,
        string reasonCode,
        string message) =>
        CloneWith(
            job,
            acceptance: GenerationAcceptance.Unknown,
            status: GenerationJobStatuses.Unknown,
            errorCode: reasonCode,
            errorMessage: message);

    private static GenerationJob CloneWith(
        GenerationJob job,
        string? acceptance = null,
        string? status = null,
        string? errorCode = null,
        string? errorMessage = null,
        bool? retryable = null) =>
        new()
        {
            OperationId = job.OperationId,
            RequestDigest = job.RequestDigest,
            Modality = job.Modality,
            Provider = job.Provider,
            ProviderJobId = job.ProviderJobId,
            Acceptance = acceptance ?? job.Acceptance,
            Status = status ?? job.Status,
            Progress = job.Progress,
            CreatedAt = job.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Output = job.Output?.Clone(),
            Artifacts = job.Artifacts,
            PendingArtifacts = job.PendingArtifacts,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Retryable = retryable ?? job.Retryable,
            CostUsd = job.CostUsd,
            AuthorityId = job.AuthorityId,
            Revision = checked(job.Revision + 1)
        };

    private async ValueTask<GenerationJob> MarkCancelRequestedAsync(
        GenerationJob job,
        CancellationToken cancellationToken)
    {
        var updated = CloneWith(
            job,
            status: GenerationJobStatuses.CancelRequested);
        await _jobs.PutAsync(updated, cancellationToken).ConfigureAwait(false);
        await PublishAsync(updated, "cancel_requested", null, cancellationToken)
            .ConfigureAwait(false);
        return GenerationValidation.SnapshotJob(updated);
    }

    private async ValueTask<GenerationJob> RequireJobAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        GenerationValidation.Identifier(operationId, nameof(operationId), 128);
        return await _jobs.TryGetAsync(operationId, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new KeyNotFoundException(
                   $"Generation operation '{operationId}' was not found.");
    }

    private SemaphoreSlim GetOperationGate(string operationId)
    {
        GenerationValidation.Identifier(operationId, nameof(operationId), 128);
        var hash = StringComparer.Ordinal.GetHashCode(operationId) & int.MaxValue;
        return _operationGates[hash % _operationGates.Length];
    }

    private async ValueTask PublishAsync(
        GenerationJob job,
        string kind,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        if (_events is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_options.EventPublishTimeout);
        try
        {
            var publish = _events.PublishAsync(
                    new GenerationEvent
                    {
                        OperationId = job.OperationId,
                        Kind = kind,
                        Status = job.Status,
                        Progress = job.Progress,
                        OccurredAt = DateTimeOffset.UtcNow,
                        ReasonCode = reasonCode
                    },
                    timeout.Token)
                .AsTask();
            var completed = await Task.WhenAny(
                    publish,
                    Task.Delay(_options.EventPublishTimeout, CancellationToken.None))
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, publish))
            {
                await publish.ConfigureAwait(false);
            }
            else
            {
                timeout.Cancel();
                _ = ObserveEventFailureAsync(publish);
            }
        }
        catch
        {
            // Observability callbacks cannot change provider or host outcomes.
        }
    }

    private static async Task ObserveEventFailureAsync(Task publish)
    {
        try
        {
            await publish.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void EnsureSameOperation(
        GenerationJob existing,
        GenerationRequest request)
    {
        if (!string.Equals(
                existing.RequestDigest,
                GenerationValidation.ComputeRequestDigest(request),
                StringComparison.Ordinal))
        {
            throw new GenerationOperationException(
                "generation_operation_conflict",
                "The operation identity is already bound to a different request.");
        }
    }

    private static void ValidateTransition(string previous, string next)
    {
        if (GenerationJobStatuses.IsTerminal(previous) && previous != next)
        {
            throw new GenerationOperationException(
                "generation_terminal_state_conflict",
                "A terminal generation job cannot transition to another state.");
        }


        if (previous == GenerationJobStatuses.Materializing
            && next is not GenerationJobStatuses.Materializing
                and not GenerationJobStatuses.Succeeded)
        {
            throw new GenerationOperationException(
                "generation_materialization_state_conflict",
                "A provider-complete job can only remain materializing or succeed locally.");
        }
    }

    private readonly struct DispatchClaim
    {
        public DispatchClaim(bool claimed, GenerationJob job)
        {
            Claimed = claimed;
            Job = job;
        }

        public bool Claimed { get; }

        public GenerationJob Job { get; }
    }
}
