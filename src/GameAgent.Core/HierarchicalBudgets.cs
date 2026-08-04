using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GameAgent.Core;

public static class BudgetScopeKinds
{
    public const string World = "world";
    public const string Scene = "scene";
    public const string Group = "group";
    public const string AgentTree = "agent_tree";
    public const string Actor = "actor";
    public const string Workflow = "workflow";
    public const string Run = "run";

    internal static bool IsKnown(string value) =>
        value == World
        || value == Scene
        || value == Group
        || value == AgentTree
        || value == Actor
        || value == Workflow
        || value == Run;
}

public sealed class HierarchicalBudgetAmount
{
    public long ModelCalls { get; set; }

    public long NonCachedInputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long ToolActions { get; set; }

    public long HostSideEffects { get; set; }

    public long ResidentMilliseconds { get; set; }

    public string CostUsd { get; set; } = "0";

    public string MediaCostUsd { get; set; } = "0";
}

public sealed class HierarchicalBudgetLimit
{
    public long? MaxModelCalls { get; set; }

    public long? MaxNonCachedInputTokens { get; set; }

    public long? MaxCachedInputTokens { get; set; }

    public long? MaxOutputTokens { get; set; }

    public long? MaxToolActions { get; set; }

    public long? MaxHostSideEffects { get; set; }

    public long? MaxResidentMilliseconds { get; set; }

    public string? MaxCostUsd { get; set; }

    public string? MaxMediaCostUsd { get; set; }
}

public sealed class HierarchicalBudgetScope
{
    public string ScopeId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? ParentScopeId { get; set; }

    public HierarchicalBudgetLimit Limit { get; set; } = new();

    public HierarchicalBudgetAmount Used { get; set; } = new();
}

public sealed class HierarchicalBudgetCharge
{
    public string ChargeId { get; set; } = string.Empty;

    public string ScopeId { get; set; } = string.Empty;

    public HierarchicalBudgetAmount Amount { get; set; } = new();
}

public sealed class HierarchicalBudgetChargeRecord
{
    public string ChargeId { get; set; } = string.Empty;

    public string ScopeId { get; set; } = string.Empty;

    public string ChargeDigest { get; set; } = string.Empty;
}

public sealed class HierarchicalBudgetState
{
    public IReadOnlyList<HierarchicalBudgetScope> Scopes { get; set; } =
        Array.Empty<HierarchicalBudgetScope>();

    public IReadOnlyList<HierarchicalBudgetChargeRecord> Charges { get; set; } =
        Array.Empty<HierarchicalBudgetChargeRecord>();

    public long Revision { get; set; }
}

public interface IHierarchicalBudgetStore
{
    ValueTask<HierarchicalBudgetState> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> TryPutAsync(
        HierarchicalBudgetState state,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class HierarchicalBudgetException : Exception
{
    public HierarchicalBudgetException(
        string reasonCode,
        string message,
        string? scopeId = null)
        : base(message)
    {
        ReasonCode = reasonCode;
        ScopeId = scopeId;
    }

    public string ReasonCode { get; }

    public string? ScopeId { get; }
}

public sealed class HierarchicalBudgetLedgerOptions
{
    public int MaxScopes { get; set; } = 65_536;

    public int MaxCharges { get; set; } = 262_144;

    public int MaxDepth { get; set; } = 32;

    public int MaxCommitRetries { get; set; } = 32;

    internal void Validate()
    {
        if (MaxScopes is < 1 or > 1_000_000
            || MaxCharges is < 1 or > 1_000_000
            || MaxDepth is < 1 or > 128
            || MaxCommitRetries is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HierarchicalBudgetLedgerOptions));
        }
    }
}

/// <summary>
/// Charges one scope and every ancestor in a single compare-and-swap update.
/// Children therefore share their root budget and cannot create allowance by
/// spawning, unloading, or restoring another runtime instance.
/// </summary>
public sealed class HierarchicalBudgetLedger
{
    private readonly IHierarchicalBudgetStore _store;
    private readonly HierarchicalBudgetLedgerOptions _options;

    public HierarchicalBudgetLedger(
        IHierarchicalBudgetStore store,
        HierarchicalBudgetLedgerOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new HierarchicalBudgetLedgerOptions();
        _options.Validate();
    }

    public async ValueTask<HierarchicalBudgetScope> DefineScopeAsync(
        HierarchicalBudgetScope scope,
        CancellationToken cancellationToken = default)
    {
        var admitted = SnapshotAndValidate(scope);
        if (!IsZero(admitted.Used))
        {
            throw new HierarchicalBudgetException(
                "hierarchical_budget_initial_usage_invalid",
                "A newly defined budget scope must start with zero usage; usage is recorded through durable charges.",
                admitted.ScopeId);
        }

        for (var attempt = 0; attempt < _options.MaxCommitRetries; attempt++)
        {
            var current = await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var scopes = current.Scopes.ToDictionary(
                item => item.ScopeId,
                Snapshot,
                StringComparer.Ordinal);
            if (scopes.TryGetValue(admitted.ScopeId, out var existing))
            {
                if (Equivalent(existing, admitted))
                {
                    return Snapshot(existing);
                }

                throw new HierarchicalBudgetException(
                    "hierarchical_budget_scope_conflict",
                    "The budget scope ID is bound to a different definition.",
                    admitted.ScopeId);
            }

            if (scopes.Count >= _options.MaxScopes)
            {
                throw new HierarchicalBudgetException(
                    "hierarchical_budget_scope_capacity",
                    "The hierarchical budget scope store is full.");
            }

            if (admitted.ParentScopeId is not null
                && !scopes.ContainsKey(admitted.ParentScopeId))
            {
                throw new HierarchicalBudgetException(
                    "hierarchical_budget_parent_missing",
                    "The parent budget scope does not exist.",
                    admitted.ScopeId);
            }

            scopes.Add(admitted.ScopeId, Snapshot(admitted));
            EnsureAcyclic(scopes, admitted.ScopeId);
            var next = new HierarchicalBudgetState
            {
                Scopes = new ReadOnlyCollection<HierarchicalBudgetScope>(
                    scopes.Values.OrderBy(item => item.ScopeId, StringComparer.Ordinal)
                        .Select(Snapshot).ToArray()),
                Charges = new ReadOnlyCollection<HierarchicalBudgetChargeRecord>(
                    current.Charges.Select(Snapshot).ToArray()),
                Revision = checked(current.Revision + 1)
            };
            if (await _store.TryPutAsync(next, current.Revision, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Snapshot(admitted);
            }
        }

        throw new HierarchicalBudgetException(
            "hierarchical_budget_contention",
            "The hierarchical budget state changed too often to define a scope.");
    }

    public async ValueTask<IReadOnlyList<HierarchicalBudgetScope>> ChargeAsync(
        HierarchicalBudgetCharge charge,
        CancellationToken cancellationToken = default)
    {
        var admitted = SnapshotAndValidate(charge);
        var digest = ChargeDigest(admitted);
        for (var attempt = 0; attempt < _options.MaxCommitRetries; attempt++)
        {
            var current = await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var prior = current.Charges.FirstOrDefault(item =>
                item.ChargeId == admitted.ChargeId);
            if (prior is not null)
            {
                if (prior.ScopeId == admitted.ScopeId
                    && prior.ChargeDigest == digest)
                {
                    return new ReadOnlyCollection<HierarchicalBudgetScope>(
                        BuildChain(current.Scopes, admitted.ScopeId)
                            .Select(Snapshot).ToArray());
                }

                throw new HierarchicalBudgetException(
                    "hierarchical_budget_charge_conflict",
                    "The charge ID is bound to different usage.",
                    admitted.ScopeId);
            }

            if (current.Charges.Count >= _options.MaxCharges)
            {
                throw new HierarchicalBudgetException(
                    "hierarchical_budget_charge_capacity",
                    "The durable charge ledger is full.");
            }

            var scopes = current.Scopes.ToDictionary(
                item => item.ScopeId,
                Snapshot,
                StringComparer.Ordinal);
            var chain = BuildChain(scopes.Values, admitted.ScopeId);
            foreach (var scope in chain)
            {
                var nextUsed = Add(scope.Used, admitted.Amount);
                EnsureWithinLimit(scope, nextUsed);
                scope.Used = nextUsed;
            }

            var charges = current.Charges.Select(Snapshot).ToList();
            charges.Add(new HierarchicalBudgetChargeRecord
            {
                ChargeId = admitted.ChargeId,
                ScopeId = admitted.ScopeId,
                ChargeDigest = digest
            });
            var next = new HierarchicalBudgetState
            {
                Scopes = new ReadOnlyCollection<HierarchicalBudgetScope>(
                    scopes.Values.OrderBy(item => item.ScopeId, StringComparer.Ordinal)
                        .Select(Snapshot).ToArray()),
                Charges = new ReadOnlyCollection<HierarchicalBudgetChargeRecord>(
                    charges.OrderBy(item => item.ChargeId, StringComparer.Ordinal)
                        .Select(Snapshot).ToArray()),
                Revision = checked(current.Revision + 1)
            };
            if (await _store.TryPutAsync(next, current.Revision, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new ReadOnlyCollection<HierarchicalBudgetScope>(
                    chain.Select(Snapshot).ToArray());
            }
        }

        throw new HierarchicalBudgetException(
            "hierarchical_budget_contention",
            "The hierarchical budget state changed too often to record usage.");
    }

    private IReadOnlyList<HierarchicalBudgetScope> BuildChain(
        IEnumerable<HierarchicalBudgetScope> source,
        string scopeId)
    {
        var scopes = source.ToDictionary(
            item => item.ScopeId,
            item => item,
            StringComparer.Ordinal);
        var chain = new List<HierarchicalBudgetScope>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var nextId = scopeId;
        while (true)
        {
            if (!scopes.TryGetValue(nextId, out var scope))
            {
                throw new HierarchicalBudgetException(
                    "hierarchical_budget_scope_missing",
                    "The charged budget scope does not exist.",
                    nextId);
            }

            if (!visited.Add(nextId) || chain.Count >= _options.MaxDepth)
            {
                throw new HierarchicalBudgetException(
                    "hierarchical_budget_cycle",
                    "The budget scope hierarchy is cyclic or too deep.",
                    nextId);
            }

            chain.Add(scope);
            if (scope.ParentScopeId is null)
            {
                return chain;
            }

            nextId = scope.ParentScopeId;
        }
    }

    private void EnsureAcyclic(
        IReadOnlyDictionary<string, HierarchicalBudgetScope> scopes,
        string scopeId)
    {
        _ = BuildChain(scopes.Values, scopeId);
    }

    private static void EnsureWithinLimit(
        HierarchicalBudgetScope scope,
        HierarchicalBudgetAmount used)
    {
        var limit = scope.Limit;
        if (Exceeds(used.ModelCalls, limit.MaxModelCalls)
            || Exceeds(used.NonCachedInputTokens, limit.MaxNonCachedInputTokens)
            || Exceeds(used.CachedInputTokens, limit.MaxCachedInputTokens)
            || Exceeds(used.OutputTokens, limit.MaxOutputTokens)
            || Exceeds(used.ToolActions, limit.MaxToolActions)
            || Exceeds(used.HostSideEffects, limit.MaxHostSideEffects)
            || Exceeds(used.ResidentMilliseconds, limit.MaxResidentMilliseconds)
            || Exceeds(ParseMoney(used.CostUsd), ParseOptionalMoney(limit.MaxCostUsd))
            || Exceeds(
                ParseMoney(used.MediaCostUsd),
                ParseOptionalMoney(limit.MaxMediaCostUsd)))
        {
            throw new HierarchicalBudgetException(
                "hierarchical_budget_exceeded",
                $"Budget scope '{scope.ScopeId}' would exceed its configured limit.",
                scope.ScopeId);
        }
    }

    private static bool Exceeds(long value, long? limit) =>
        limit.HasValue && value > limit.Value;

    private static bool Exceeds(decimal value, decimal? limit) =>
        limit.HasValue && value > limit.Value;

    private static bool IsZero(HierarchicalBudgetAmount amount) =>
        amount.ModelCalls == 0
        && amount.NonCachedInputTokens == 0
        && amount.CachedInputTokens == 0
        && amount.OutputTokens == 0
        && amount.ToolActions == 0
        && amount.HostSideEffects == 0
        && amount.ResidentMilliseconds == 0
        && ParseMoney(amount.CostUsd) == 0
        && ParseMoney(amount.MediaCostUsd) == 0;

    private static HierarchicalBudgetAmount Add(
        HierarchicalBudgetAmount left,
        HierarchicalBudgetAmount right) => new()
        {
            ModelCalls = checked(left.ModelCalls + right.ModelCalls),
            NonCachedInputTokens = checked(
            left.NonCachedInputTokens + right.NonCachedInputTokens),
            CachedInputTokens = checked(left.CachedInputTokens + right.CachedInputTokens),
            OutputTokens = checked(left.OutputTokens + right.OutputTokens),
            ToolActions = checked(left.ToolActions + right.ToolActions),
            HostSideEffects = checked(left.HostSideEffects + right.HostSideEffects),
            ResidentMilliseconds = checked(
            left.ResidentMilliseconds + right.ResidentMilliseconds),
            CostUsd = Money(ParseMoney(left.CostUsd) + ParseMoney(right.CostUsd)),
            MediaCostUsd = Money(
            ParseMoney(left.MediaCostUsd) + ParseMoney(right.MediaCostUsd))
        };

    private static string ChargeDigest(HierarchicalBudgetCharge charge) =>
        CanonicalJsonDigest.ComputeSha256(JsonArrayBuilder.Object(
            ("chargeId", JsonArrayBuilder.String(charge.ChargeId)),
            ("scopeId", JsonArrayBuilder.String(charge.ScopeId)),
            ("modelCalls", JsonArrayBuilder.Number(charge.Amount.ModelCalls)),
            ("nonCachedInputTokens", JsonArrayBuilder.Number(
                charge.Amount.NonCachedInputTokens)),
            ("cachedInputTokens", JsonArrayBuilder.Number(
                charge.Amount.CachedInputTokens)),
            ("outputTokens", JsonArrayBuilder.Number(charge.Amount.OutputTokens)),
            ("toolActions", JsonArrayBuilder.Number(charge.Amount.ToolActions)),
            ("hostSideEffects", JsonArrayBuilder.Number(
                charge.Amount.HostSideEffects)),
            ("residentMilliseconds", JsonArrayBuilder.Number(
                charge.Amount.ResidentMilliseconds)),
            ("costUsd", JsonArrayBuilder.String(charge.Amount.CostUsd)),
            ("mediaCostUsd", JsonArrayBuilder.String(charge.Amount.MediaCostUsd))));

    private static HierarchicalBudgetScope SnapshotAndValidate(
        HierarchicalBudgetScope scope)
    {
        if (scope is null || scope.Limit is null || scope.Used is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var kind = Required(scope.Kind, nameof(scope.Kind), 64);
        if (!BudgetScopeKinds.IsKnown(kind))
        {
            throw new ArgumentException("The budget scope kind is invalid.", nameof(scope));
        }

        var result = new HierarchicalBudgetScope
        {
            ScopeId = Required(scope.ScopeId, nameof(scope.ScopeId), 256),
            Kind = kind,
            ParentScopeId = scope.ParentScopeId is null
                ? null
                : Required(scope.ParentScopeId, nameof(scope.ParentScopeId), 256),
            Limit = SnapshotAndValidate(scope.Limit),
            Used = SnapshotAndValidate(scope.Used)
        };
        EnsureWithinLimit(result, result.Used);
        return result;
    }

    private static HierarchicalBudgetCharge SnapshotAndValidate(
        HierarchicalBudgetCharge charge)
    {
        if (charge is null || charge.Amount is null)
        {
            throw new ArgumentNullException(nameof(charge));
        }

        return new HierarchicalBudgetCharge
        {
            ChargeId = Required(charge.ChargeId, nameof(charge.ChargeId), 256),
            ScopeId = Required(charge.ScopeId, nameof(charge.ScopeId), 256),
            Amount = SnapshotAndValidate(charge.Amount)
        };
    }

    private static HierarchicalBudgetLimit SnapshotAndValidate(
        HierarchicalBudgetLimit limit)
    {
        EnsureNonNegative(limit.MaxModelCalls);
        EnsureNonNegative(limit.MaxNonCachedInputTokens);
        EnsureNonNegative(limit.MaxCachedInputTokens);
        EnsureNonNegative(limit.MaxOutputTokens);
        EnsureNonNegative(limit.MaxToolActions);
        EnsureNonNegative(limit.MaxHostSideEffects);
        EnsureNonNegative(limit.MaxResidentMilliseconds);
        _ = ParseOptionalMoney(limit.MaxCostUsd);
        _ = ParseOptionalMoney(limit.MaxMediaCostUsd);
        return new HierarchicalBudgetLimit
        {
            MaxModelCalls = limit.MaxModelCalls,
            MaxNonCachedInputTokens = limit.MaxNonCachedInputTokens,
            MaxCachedInputTokens = limit.MaxCachedInputTokens,
            MaxOutputTokens = limit.MaxOutputTokens,
            MaxToolActions = limit.MaxToolActions,
            MaxHostSideEffects = limit.MaxHostSideEffects,
            MaxResidentMilliseconds = limit.MaxResidentMilliseconds,
            MaxCostUsd = limit.MaxCostUsd is null
                ? null
                : Money(ParseMoney(limit.MaxCostUsd)),
            MaxMediaCostUsd = limit.MaxMediaCostUsd is null
                ? null
                : Money(ParseMoney(limit.MaxMediaCostUsd))
        };
    }

    private static HierarchicalBudgetAmount SnapshotAndValidate(
        HierarchicalBudgetAmount amount)
    {
        if (amount.ModelCalls < 0
            || amount.NonCachedInputTokens < 0
            || amount.CachedInputTokens < 0
            || amount.OutputTokens < 0
            || amount.ToolActions < 0
            || amount.HostSideEffects < 0
            || amount.ResidentMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        return new HierarchicalBudgetAmount
        {
            ModelCalls = amount.ModelCalls,
            NonCachedInputTokens = amount.NonCachedInputTokens,
            CachedInputTokens = amount.CachedInputTokens,
            OutputTokens = amount.OutputTokens,
            ToolActions = amount.ToolActions,
            HostSideEffects = amount.HostSideEffects,
            ResidentMilliseconds = amount.ResidentMilliseconds,
            CostUsd = Money(ParseMoney(amount.CostUsd)),
            MediaCostUsd = Money(ParseMoney(amount.MediaCostUsd))
        };
    }

    internal static HierarchicalBudgetState Snapshot(HierarchicalBudgetState state) =>
        new()
        {
            Scopes = new ReadOnlyCollection<HierarchicalBudgetScope>(
                state.Scopes.Select(Snapshot).ToArray()),
            Charges = new ReadOnlyCollection<HierarchicalBudgetChargeRecord>(
                state.Charges.Select(Snapshot).ToArray()),
            Revision = state.Revision
        };

    internal static HierarchicalBudgetScope Snapshot(HierarchicalBudgetScope scope) =>
        new()
        {
            ScopeId = scope.ScopeId,
            Kind = scope.Kind,
            ParentScopeId = scope.ParentScopeId,
            Limit = new HierarchicalBudgetLimit
            {
                MaxModelCalls = scope.Limit.MaxModelCalls,
                MaxNonCachedInputTokens = scope.Limit.MaxNonCachedInputTokens,
                MaxCachedInputTokens = scope.Limit.MaxCachedInputTokens,
                MaxOutputTokens = scope.Limit.MaxOutputTokens,
                MaxToolActions = scope.Limit.MaxToolActions,
                MaxHostSideEffects = scope.Limit.MaxHostSideEffects,
                MaxResidentMilliseconds = scope.Limit.MaxResidentMilliseconds,
                MaxCostUsd = scope.Limit.MaxCostUsd,
                MaxMediaCostUsd = scope.Limit.MaxMediaCostUsd
            },
            Used = new HierarchicalBudgetAmount
            {
                ModelCalls = scope.Used.ModelCalls,
                NonCachedInputTokens = scope.Used.NonCachedInputTokens,
                CachedInputTokens = scope.Used.CachedInputTokens,
                OutputTokens = scope.Used.OutputTokens,
                ToolActions = scope.Used.ToolActions,
                HostSideEffects = scope.Used.HostSideEffects,
                ResidentMilliseconds = scope.Used.ResidentMilliseconds,
                CostUsd = scope.Used.CostUsd,
                MediaCostUsd = scope.Used.MediaCostUsd
            }
        };

    internal static HierarchicalBudgetChargeRecord Snapshot(
        HierarchicalBudgetChargeRecord charge) => new()
        {
            ChargeId = charge.ChargeId,
            ScopeId = charge.ScopeId,
            ChargeDigest = charge.ChargeDigest
        };

    private static bool Equivalent(
        HierarchicalBudgetScope left,
        HierarchicalBudgetScope right) =>
        left.ScopeId == right.ScopeId
        && left.Kind == right.Kind
        && left.ParentScopeId == right.ParentScopeId
        && LimitDigest(left.Limit) == LimitDigest(right.Limit);

    private static string LimitDigest(HierarchicalBudgetLimit limit) =>
        string.Join(
            "|",
            limit.MaxModelCalls,
            limit.MaxNonCachedInputTokens,
            limit.MaxCachedInputTokens,
            limit.MaxOutputTokens,
            limit.MaxToolActions,
            limit.MaxHostSideEffects,
            limit.MaxResidentMilliseconds,
            limit.MaxCostUsd,
            limit.MaxMediaCostUsd);

    private static decimal ParseMoney(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var result)
            || result < 0)
        {
            throw new ArgumentException("A non-negative decimal money value is required.");
        }

        return result;
    }

    private static decimal? ParseOptionalMoney(string? value) =>
        value is null ? null : ParseMoney(value);

    private static string Money(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static void EnsureNonNegative(long? value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static string Required(string value, string name, int maximum) =>
        RuntimeGuard.RequiredUtf8(value, maximum, name);
}

public sealed class InMemoryHierarchicalBudgetStore : IHierarchicalBudgetStore
{
    private readonly object _gate = new();
    private HierarchicalBudgetState _state = new();

    public ValueTask<HierarchicalBudgetState> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new ValueTask<HierarchicalBudgetState>(
                HierarchicalBudgetLedger.Snapshot(_state));
        }
    }

    public ValueTask<bool> TryPutAsync(
        HierarchicalBudgetState state,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state.Revision != expectedRevision)
            {
                return new ValueTask<bool>(false);
            }

            _state = HierarchicalBudgetLedger.Snapshot(state);
            return new ValueTask<bool>(true);
        }
    }
}
