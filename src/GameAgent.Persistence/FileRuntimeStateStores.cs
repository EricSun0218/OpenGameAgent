using System.Collections.ObjectModel;
using GameAgent.Core;

namespace GameAgent.Persistence;

public sealed class FileRuntimeStateStoreOptions
{
    public int MaximumEntries { get; set; } = 100_000;

    public DurableLocalStateFileOptions DurableFile { get; set; } = new();

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        }

        (DurableFile ?? throw new ArgumentNullException(nameof(DurableFile))).Validate();
    }
}

internal sealed class ModelContextBaselineFileState
{
    public List<ModelContextSectionBaseline> Baselines { get; set; } = new();

    public long Revision { get; set; }
}

internal sealed class ExternalAttentionFileState
{
    public List<ExternalAttentionRecord> Records { get; set; } = new();

    public long Revision { get; set; }
}

internal sealed class GameTriggerFileState
{
    public List<GameTriggerState> States { get; set; } = new();

    public long Revision { get; set; }
}

internal sealed class MemoryDistillationFileState
{
    public List<DistilledMemoryRecord> Records { get; set; } = new();

    public long Revision { get; set; }
}

public sealed class FileModelContextSectionBaselineStore :
    IModelContextSectionBaselineStore,
    IAsyncDisposable
{
    private readonly int _maximumEntries;
    private readonly DurableLocalStateFile<ModelContextBaselineFileState> _file;

    public FileModelContextSectionBaselineStore(
        string path,
        FileRuntimeStateStoreOptions? options = null)
    {
        options ??= new FileRuntimeStateStoreOptions();
        options.Validate();
        _maximumEntries = options.MaximumEntries;
        _file = new DurableLocalStateFile<ModelContextBaselineFileState>(
            path,
            new ModelContextBaselineFileState(),
            PersistenceJsonContext.Default.ModelContextBaselineFileState,
            Clone,
            state => state.Revision,
            options.DurableFile);
    }

    public async ValueTask<ModelContextSectionBaseline?> TryGetAsync(
        string baselineKey,
        CancellationToken cancellationToken)
    {
        var state = await _file.ReadAsync(cancellationToken).ConfigureAwait(false);
        var baseline = state.Baselines.SingleOrDefault(item => item.BaselineKey == baselineKey);
        return baseline is null ? null : Clone(baseline);
    }

    public async ValueTask PutAsync(
        ModelContextSectionBaseline baseline,
        string? expectedContentDigest,
        CancellationToken cancellationToken)
    {
        if (baseline is null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        var admitted = Clone(baseline);
        _ = await _file.MutateAsync(
            state =>
            {
                var index = state.Baselines.FindIndex(item =>
                    item.BaselineKey == admitted.BaselineKey);
                if (index >= 0)
                {
                    if (!string.Equals(
                            state.Baselines[index].ContentDigest,
                            expectedContentDigest,
                            StringComparison.Ordinal))
                    {
                        throw new ModelContextSectionException(
                            "context_section_baseline_conflict",
                            "The context section disclosure baseline changed before commit.");
                    }

                    state.Baselines[index] = admitted;
                }
                else
                {
                    if (expectedContentDigest is not null)
                    {
                        throw new ModelContextSectionException(
                            "context_section_baseline_conflict",
                            "The expected context section disclosure baseline is missing.");
                    }

                    if (state.Baselines.Count >= _maximumEntries)
                    {
                        throw new ModelContextSectionException(
                            "context_section_baseline_capacity",
                            "The context section baseline store is full.");
                    }

                    state.Baselines.Add(admitted);
                }

                state.Baselines.Sort((left, right) =>
                    StringComparer.Ordinal.Compare(left.BaselineKey, right.BaselineKey));
                state.Revision++;
                return new DurableStateMutation<ModelContextBaselineFileState, bool>(
                    true, state, true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();

    private static ModelContextBaselineFileState Clone(
        ModelContextBaselineFileState state) => new()
        {
            Baselines = state.Baselines.Select(Clone).ToList(),
            Revision = state.Revision
        };

    private static ModelContextSectionBaseline Clone(
        ModelContextSectionBaseline baseline) => new()
        {
            BaselineKey = baseline.BaselineKey,
            ViewKey = baseline.ViewKey,
            SectionId = baseline.SectionId,
            SchemaVersion = baseline.SchemaVersion,
            Scope = baseline.Scope,
            ScopeKey = baseline.ScopeKey,
            AuthorityId = baseline.AuthorityId,
            TimelineId = baseline.TimelineId,
            IncarnationId = baseline.IncarnationId,
            ModelCapabilitiesDigest = baseline.ModelCapabilitiesDigest,
            Revision = baseline.Revision,
            ExpectedBaseRevision = baseline.ExpectedBaseRevision,
            RetainThroughCompaction = baseline.RetainThroughCompaction,
            Content = baseline.Content.Clone(),
            ContentDigest = baseline.ContentDigest
        };
}

public sealed class FileExternalAttentionStore :
    IExternalAttentionStore,
    IAsyncDisposable
{
    private readonly int _maximumEntries;
    private readonly DurableLocalStateFile<ExternalAttentionFileState> _file;

    public FileExternalAttentionStore(
        string path,
        FileRuntimeStateStoreOptions? options = null)
    {
        options ??= new FileRuntimeStateStoreOptions();
        options.Validate();
        _maximumEntries = options.MaximumEntries;
        _file = new DurableLocalStateFile<ExternalAttentionFileState>(
            path,
            new ExternalAttentionFileState(),
            PersistenceJsonContext.Default.ExternalAttentionFileState,
            Clone,
            state => state.Revision,
            options.DurableFile);
    }

    public async ValueTask<ExternalAttentionRecord?> TryGetAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        var state = await _file.ReadAsync(cancellationToken).ConfigureAwait(false);
        var record = state.Records.SingleOrDefault(item =>
            item.Request.RequestId == requestId);
        return record is null ? null : ExternalAttentionCoordinator.Snapshot(record);
    }

    public async ValueTask PutAsync(
        ExternalAttentionRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        var admitted = ExternalAttentionCoordinator.Snapshot(record);
        _ = await _file.MutateAsync(
            state =>
            {
                var index = state.Records.FindIndex(item =>
                    item.Request.RequestId == admitted.Request.RequestId);
                if (index >= 0)
                {
                    if (state.Records[index].Revision != expectedRevision)
                    {
                        throw new ExternalAttentionException(
                            "external_attention_revision_conflict",
                            "The external-attention record revision changed.");
                    }

                    state.Records[index] = admitted;
                }
                else
                {
                    if (expectedRevision is not null)
                    {
                        throw new ExternalAttentionException(
                            "external_attention_revision_conflict",
                            "The expected external-attention record is missing.");
                    }

                    if (state.Records.Count >= _maximumEntries)
                    {
                        throw new ExternalAttentionException(
                            "external_attention_capacity",
                            "The external-attention store is full.");
                    }

                    state.Records.Add(admitted);
                }

                state.Records.Sort((left, right) => StringComparer.Ordinal.Compare(
                    left.Request.RequestId, right.Request.RequestId));
                state.Revision++;
                return new DurableStateMutation<ExternalAttentionFileState, bool>(
                    true, state, true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ExternalAttentionRecord>> ListPendingAsync(
        string? worldId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var state = await _file.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ReadOnlyCollection<ExternalAttentionRecord>(
            state.Records
                .Where(record =>
                    record.State == ExternalAttentionStates.Pending
                    && (worldId is null || record.Request.WorldId == worldId))
                .OrderBy(record => record.Request.CreatedAt.Epoch)
                .ThenBy(record => record.Request.CreatedAt.Tick)
                .ThenBy(record => record.Request.RequestId, StringComparer.Ordinal)
                .Take(maximumCount)
                .Select(ExternalAttentionCoordinator.Snapshot)
                .ToArray());
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();

    private static ExternalAttentionFileState Clone(ExternalAttentionFileState state) =>
        new()
        {
            Records = state.Records
                .Select(ExternalAttentionCoordinator.Snapshot)
                .ToList(),
            Revision = state.Revision
        };
}

public sealed class FileGameTriggerStateStore :
    IGameTriggerStateStore,
    IAsyncDisposable
{
    private readonly int _maximumEntries;
    private readonly DurableLocalStateFile<GameTriggerFileState> _file;

    public FileGameTriggerStateStore(
        string path,
        FileRuntimeStateStoreOptions? options = null)
    {
        options ??= new FileRuntimeStateStoreOptions();
        options.Validate();
        _maximumEntries = options.MaximumEntries;
        _file = new DurableLocalStateFile<GameTriggerFileState>(
            path,
            new GameTriggerFileState(),
            PersistenceJsonContext.Default.GameTriggerFileState,
            Clone,
            state => state.Revision,
            options.DurableFile);
    }

    public async ValueTask<GameTriggerState?> TryGetAsync(
        string stateKey,
        CancellationToken cancellationToken)
    {
        var state = await _file.ReadAsync(cancellationToken).ConfigureAwait(false);
        var found = state.States.SingleOrDefault(item => item.StateKey == stateKey);
        return found is null ? null : GameTriggerCoordinator.Snapshot(found);
    }

    public async ValueTask PutAsync(
        GameTriggerState triggerState,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        var admitted = GameTriggerCoordinator.Snapshot(triggerState);
        _ = await _file.MutateAsync(
            state =>
            {
                var index = state.States.FindIndex(item =>
                    item.StateKey == admitted.StateKey);
                if (index >= 0)
                {
                    if (state.States[index].Revision != expectedRevision)
                    {
                        throw new GameTriggerException(
                            "game_trigger_revision_conflict",
                            "The game trigger state revision changed.");
                    }

                    state.States[index] = admitted;
                }
                else
                {
                    if (expectedRevision is not null)
                    {
                        throw new GameTriggerException(
                            "game_trigger_revision_conflict",
                            "The expected game trigger state is missing.");
                    }

                    if (state.States.Count >= _maximumEntries)
                    {
                        throw new GameTriggerException(
                            "game_trigger_capacity",
                            "The game trigger state store is full.");
                    }

                    state.States.Add(admitted);
                }

                state.States.Sort((left, right) =>
                    StringComparer.Ordinal.Compare(left.StateKey, right.StateKey));
                state.Revision++;
                return new DurableStateMutation<GameTriggerFileState, bool>(
                    true, state, true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();

    private static GameTriggerFileState Clone(GameTriggerFileState state) => new()
    {
        States = state.States.Select(GameTriggerCoordinator.Snapshot).ToList(),
        Revision = state.Revision
    };
}

public sealed class FileHierarchicalBudgetStore :
    IHierarchicalBudgetStore,
    IAsyncDisposable
{
    private readonly DurableLocalStateFile<HierarchicalBudgetState> _file;

    public FileHierarchicalBudgetStore(
        string path,
        DurableLocalStateFileOptions? options = null)
    {
        _file = new DurableLocalStateFile<HierarchicalBudgetState>(
            path,
            new HierarchicalBudgetState(),
            PersistenceJsonContext.Default.HierarchicalBudgetState,
            HierarchicalBudgetLedger.Snapshot,
            state => state.Revision,
            options);
    }

    public ValueTask<HierarchicalBudgetState> ReadAsync(
        CancellationToken cancellationToken) => _file.ReadAsync(cancellationToken);

    public ValueTask<bool> TryPutAsync(
        HierarchicalBudgetState state,
        long expectedRevision,
        CancellationToken cancellationToken) =>
        _file.MutateAsync(
            current => current.Revision != expectedRevision
                ? new DurableStateMutation<HierarchicalBudgetState, bool>(
                    false, current, false)
                : new DurableStateMutation<HierarchicalBudgetState, bool>(
                    true, HierarchicalBudgetLedger.Snapshot(state), true),
            cancellationToken);

    public ValueTask DisposeAsync() => _file.DisposeAsync();
}

public sealed class FilePersistentAgentGraphStore :
    IPersistentAgentGraphStore,
    IAsyncDisposable
{
    private readonly DurableLocalStateFile<PersistentAgentGraphState> _file;

    public FilePersistentAgentGraphStore(
        string path,
        DurableLocalStateFileOptions? options = null)
    {
        _file = new DurableLocalStateFile<PersistentAgentGraphState>(
            path,
            new PersistentAgentGraphState(),
            PersistenceJsonContext.Default.PersistentAgentGraphState,
            PersistentAgentGraph.Snapshot,
            state => state.Revision,
            options);
    }

    public ValueTask<PersistentAgentGraphState> ReadAsync(
        CancellationToken cancellationToken) => _file.ReadAsync(cancellationToken);

    public ValueTask<bool> TryPutAsync(
        PersistentAgentGraphState state,
        long expectedRevision,
        CancellationToken cancellationToken) =>
        _file.MutateAsync(
            current => current.Revision != expectedRevision
                ? new DurableStateMutation<PersistentAgentGraphState, bool>(
                    false, current, false)
                : new DurableStateMutation<PersistentAgentGraphState, bool>(
                    true, PersistentAgentGraph.Snapshot(state), true),
            cancellationToken);

    public ValueTask DisposeAsync() => _file.DisposeAsync();
}

public sealed class FileMemoryDistillationStore :
    IMemoryDistillationStore,
    IAsyncDisposable
{
    private readonly int _maximumEntries;
    private readonly DurableLocalStateFile<MemoryDistillationFileState> _file;

    public FileMemoryDistillationStore(
        string path,
        FileRuntimeStateStoreOptions? options = null)
    {
        options ??= new FileRuntimeStateStoreOptions();
        options.Validate();
        _maximumEntries = options.MaximumEntries;
        _file = new DurableLocalStateFile<MemoryDistillationFileState>(
            path,
            new MemoryDistillationFileState(),
            PersistenceJsonContext.Default.MemoryDistillationFileState,
            Clone,
            state => state.Revision,
            options.DurableFile);
    }

    public async ValueTask<DistilledMemoryRecord?> TryGetAsync(
        string distillationId,
        CancellationToken cancellationToken)
    {
        var state = await _file.ReadAsync(cancellationToken).ConfigureAwait(false);
        var record = state.Records.SingleOrDefault(item =>
            item.DistillationId == distillationId);
        return record is null ? null : MemoryDistillationCoordinator.Snapshot(record);
    }

    public async ValueTask PutAsync(
        DistilledMemoryRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        var admitted = MemoryDistillationCoordinator.Snapshot(record);
        _ = await _file.MutateAsync(
            state =>
            {
                var index = state.Records.FindIndex(item =>
                    item.DistillationId == admitted.DistillationId);
                if (index >= 0)
                {
                    if (state.Records[index].Revision != expectedRevision)
                    {
                        throw Conflict();
                    }

                    state.Records[index] = admitted;
                }
                else
                {
                    if (expectedRevision is not null)
                    {
                        throw Conflict();
                    }

                    if (state.Records.Count >= _maximumEntries)
                    {
                        throw new MemoryDistillationException(
                            "memory_distillation_capacity",
                            "The distilled memory store is full.");
                    }

                    state.Records.Add(admitted);
                }

                state.Records.Sort((left, right) => StringComparer.Ordinal.Compare(
                    left.DistillationId, right.DistillationId));
                state.Revision++;
                return new DurableStateMutation<MemoryDistillationFileState, bool>(
                    true, state, true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DistilledMemoryRecord>> ListAsync(
        string? scope,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var state = await _file.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ReadOnlyCollection<DistilledMemoryRecord>(state.Records
            .Where(item => scope is null || item.Scope == scope)
            .OrderByDescending(item => item.Salience)
            .ThenByDescending(item => item.UsageCount)
            .ThenBy(item => item.DistillationId, StringComparer.Ordinal)
            .Take(maximumCount)
            .Select(MemoryDistillationCoordinator.Snapshot)
            .ToArray());
    }

    public async ValueTask<IReadOnlyList<DistilledMemoryRecord>> ListDueAsync(
        string? scope,
        GameTimePoint now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (now is null)
        {
            throw new ArgumentNullException(nameof(now));
        }

        if (maximumCount is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var state = await _file.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ReadOnlyCollection<DistilledMemoryRecord>(state.Records
            .Where(item =>
                (scope is null || item.Scope == scope)
                && item.State == DistilledMemoryStates.Active
                && item.RetainUntil is not null
                && item.RetainUntil.IsComparableTo(now)
                && item.RetainUntil.CompareTo(now) <= 0)
            .OrderBy(item => item.RetainUntil!.Tick)
            .ThenBy(item => item.DistillationId, StringComparer.Ordinal)
            .Take(maximumCount)
            .Select(MemoryDistillationCoordinator.Snapshot)
            .ToArray());
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();

    private static MemoryDistillationFileState Clone(
        MemoryDistillationFileState state) => new()
        {
            Records = state.Records
                .Select(MemoryDistillationCoordinator.Snapshot)
                .ToList(),
            Revision = state.Revision
        };

    private static MemoryDistillationException Conflict() =>
        new(
            "memory_distillation_revision_conflict",
            "The distilled memory record revision changed.");
}
