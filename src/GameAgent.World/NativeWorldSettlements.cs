using System.Collections.ObjectModel;
using GameAgent.Core;

namespace GameAgent.World;

public static class NativeWorldSettlementReasonCodes
{
    public const string ClaimMismatch =
        "native_world_settlement_claim_mismatch";
    public const string IncarnationMismatch =
        "native_world_settlement_incarnation_mismatch";
    public const string AudiencePolicyRequired =
        "native_world_settlement_audience_policy_required";
    public const string AudiencePolicyDenied =
        "native_world_settlement_audience_policy_denied";
}

/// <summary>
/// Trusted receipt source backed directly by the active native-world
/// authoritative ledger. Caller-authored evidence is never consulted.
/// </summary>
public sealed class NativeWorldCommittedEvidenceSource
    : ICommittedWorldPresentationEvidenceSource
{
    private readonly NativeWorldEngineSession _session;

    public NativeWorldCommittedEvidenceSource(
        NativeWorldEngineSession session)
    {
        _session = session
                   ?? throw new ArgumentNullException(nameof(session));
    }

    public async ValueTask<CommittedWorldPresentationEvidence?>
        ReadCommittedAsync(
            string worldReceiptId,
            CancellationToken cancellationToken = default)
    {
        var capture = await _session.ReadReceiptAsync(
                worldReceiptId,
                cancellationToken)
            .ConfigureAwait(false);
        return CreateEvidence(
            capture,
            capture?.Receipt.Request.EventOccurrence?.OccurredAt);
    }

    internal static CommittedWorldPresentationEvidence? CreateEvidence(
        NativeWorldEngineReceiptRead? capture,
        GameTimePoint? gameTime = null)
    {
        if (capture is null)
        {
            return null;
        }

        var receipt = capture.Receipt;
        if (receipt.Status != WorldCommandReceiptStatus.Applied
            || receipt.ResultingCoordinate is null
            || receipt.ResultingStateDigest is null
            || receipt.Effect is null
            || !receipt.Effect.Applied)
        {
            return null;
        }

        try
        {
            return WorldCommandPresentationEvidence.CreateApplied(
                receipt,
                gameTime);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Immutable native authority captured while admission is paused. A game
/// policy may inspect the authoritative state but cannot mutate its store.
/// </summary>
public sealed class NativeWorldSettlementPolicyRequest
{
    internal NativeWorldSettlementPolicyRequest(
        WorldSettlementAuthorityRequest request,
        long sessionGeneration,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        Request = request
                  ?? throw new ArgumentNullException(nameof(request));
        SessionGeneration = sessionGeneration;
        Snapshot = snapshot
                   ?? throw new ArgumentNullException(nameof(snapshot));
        EntityIncarnations =
            new ReadOnlyDictionary<string, long>(
                new Dictionary<string, long>(
                    snapshot.EntityIncarnations,
                    StringComparer.Ordinal));
    }

    public WorldSettlementAuthorityRequest Request { get; }

    public long SessionGeneration { get; }

    public WorldAuthoritativeStateSnapshot Snapshot { get; }

    public IReadOnlyDictionary<string, long> EntityIncarnations { get; }
}

/// <summary>
/// Optional game-owned membership, visibility, ownership, and consent
/// boundary. The returned policy lease must keep every policy fact used by
/// an allowed claim stable until it is disposed.
/// A policy that also owns a settlement sink must use an owner-aware,
/// reentrant, or handoff-capable lease. It must not hold a non-reentrant
/// mutation gate that blocks the coordinator's call to that same sink.
/// </summary>
public interface INativeWorldSettlementAudiencePolicy
{
    ValueTask<INativeWorldSettlementAudiencePolicyLease?> AcquireAsync(
        NativeWorldSettlementPolicyRequest request,
        CancellationToken cancellationToken = default);
}

public interface INativeWorldSettlementAudiencePolicyLease
    : IAsyncDisposable
{
    ValueTask<WorldSettlementAuthorityDecision> ValidateAsync(
        WorldSettlementDeliveryClaim claim,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Native implementation of the settlement authority guard. It combines an
/// exclusive engine-session coordinate fence with an optional game-policy
/// lease. Without a game policy, only a single-member private audience can
/// be admitted after exact incarnation validation.
/// </summary>
public sealed class NativeWorldSettlementAuthorityGuard
    : IWorldSettlementAuthorityGuard
{
    private readonly NativeWorldEngineSession _session;
    private readonly INativeWorldSettlementAudiencePolicy? _policy;

    public NativeWorldSettlementAuthorityGuard(
        NativeWorldEngineSession session,
        INativeWorldSettlementAudiencePolicy? policy = null)
    {
        _session = session
                   ?? throw new ArgumentNullException(nameof(session));
        _policy = policy;
    }

    public async ValueTask<IWorldSettlementAuthorityLease?> AcquireAsync(
        WorldSettlementAuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var nativeLease = await _session.AcquireSettlementLeaseAsync(
                request.Binding,
                cancellationToken)
            .ConfigureAwait(false);
        if (nativeLease is null)
        {
            return null;
        }

        INativeWorldSettlementAudiencePolicyLease? policyLease = null;
        var transferOwnership = false;
        try
        {
            var receiptCapture = await nativeLease.ReadReceiptAsync(
                    request.Source.WorldReceiptId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!ReceiptAndEvidenceMatch(
                    request,
                    nativeLease.Snapshot,
                    receiptCapture))
            {
                return null;
            }

            var policy = _policy;
            if (policy is not null)
            {
                var policyRequest =
                    new NativeWorldSettlementPolicyRequest(
                        request,
                        nativeLease.Generation,
                        nativeLease.Snapshot);
                policyLease = await _session
                    .InvokeSettlementPolicyCallbackAsync(
                        () => policy.AcquireAsync(
                            policyRequest,
                            cancellationToken))
                    .ConfigureAwait(false);
                if (policyLease is null)
                {
                    return null;
                }
            }

            var authorityLease = new AuthorityLease(
                _session,
                request,
                nativeLease,
                policyLease);
            transferOwnership = true;
            return authorityLease;
        }
        finally
        {
            if (!transferOwnership)
            {
                if (policyLease is not null)
                {
                    try
                    {
                        await _session
                            .InvokeSettlementPolicyCallbackAsync(
                                () => policyLease.DisposeAsync())
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        await nativeLease.DisposeAsync()
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    await nativeLease.DisposeAsync()
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private static bool ReceiptAndEvidenceMatch(
        WorldSettlementAuthorityRequest request,
        WorldAuthoritativeStateSnapshot snapshot,
        NativeWorldEngineReceiptRead? receiptCapture)
    {
        if (receiptCapture is null)
        {
            return false;
        }

        var receipt = receiptCapture.Receipt;
        var coordinate = receipt.ResultingCoordinate;
        if (receipt.Status != WorldCommandReceiptStatus.Applied
            || coordinate is null
            || receipt.ResultingStateDigest is null
            || receipt.Effect is null
            || !receipt.Effect.Applied
            || !coordinate.IsExactMatch(snapshot.Coordinate)
            || !string.Equals(
                receipt.ResultingStateDigest,
                snapshot.StateDigest,
                StringComparison.Ordinal)
            || !GameTimeMatches(
                request.Binding.GameTime,
                receipt.Request.EventOccurrence?.OccurredAt))
        {
            return false;
        }

        var projected =
            NativeWorldCommittedEvidenceSource.CreateEvidence(
                receiptCapture,
                request.Binding.GameTime);
        return projected is not null
               && projected.Source.IsSameAs(request.Source)
               && projected.Binding.IsSameAs(request.Binding)
               && string.Equals(
                   projected.SemanticDigest,
                   request.EvidenceDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   projected.SemanticDigest,
                   request.Plan.Evidence.SemanticDigest,
                   StringComparison.Ordinal);
    }

    private static bool GameTimeMatches(
        GameTimePoint? claimed,
        GameTimePoint? receiptTime)
    {
        if (claimed is null)
        {
            return true;
        }

        return receiptTime is not null
               && string.Equals(
                   claimed.ClockId,
                   receiptTime.ClockId,
                   StringComparison.Ordinal)
               && string.Equals(
                   claimed.TimelineId,
                   receiptTime.TimelineId,
                   StringComparison.Ordinal)
               && claimed.Epoch == receiptTime.Epoch
               && claimed.Tick == receiptTime.Tick;
    }

    private sealed class AuthorityLease
        : IWorldSettlementAuthorityLease
    {
        private readonly IReadOnlyDictionary<
            string,
            ClaimIdentity> _claims;
        private readonly NativeWorldEngineSession _session;
        private readonly WorldAuthoritativeStateSnapshot _snapshot;
        private LeaseOwnership? _ownership;
        private Task? _disposalTask;

        public AuthorityLease(
            NativeWorldEngineSession session,
            WorldSettlementAuthorityRequest request,
            NativeWorldEngineSettlementLease nativeLease,
            INativeWorldSettlementAudiencePolicyLease? policyLease)
        {
            _session = session;
            _ownership = new LeaseOwnership(
                nativeLease,
                policyLease);
            _snapshot = nativeLease.Snapshot;
            _claims = new ReadOnlyDictionary<
                string,
                ClaimIdentity>(
                request.Plan.Deliveries.ToDictionary(
                    item => item.OperationId,
                    item => new ClaimIdentity(
                        item.Kind,
                        item.SemanticDigest),
                    StringComparer.Ordinal));
        }

        public async ValueTask<WorldSettlementAuthorityDecision>
            ValidateAsync(
                WorldSettlementDeliveryClaim claim,
                CancellationToken cancellationToken = default)
        {
            if (claim is null)
            {
                throw new ArgumentNullException(nameof(claim));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var ownership = Volatile.Read(ref _ownership);
            if (ownership is null)
            {
                throw new ObjectDisposedException(nameof(AuthorityLease));
            }

            if (!_claims.TryGetValue(
                    claim.OperationId,
                    out var identity)
                || identity.Kind != claim.Kind
                || !string.Equals(
                    identity.DeliveryDigest,
                    claim.DeliveryDigest,
                    StringComparison.Ordinal))
            {
                return WorldSettlementAuthorityDecision.Deny(
                    NativeWorldSettlementReasonCodes.ClaimMismatch);
            }

            foreach (var member in claim.Audience.Members)
            {
                if (!_snapshot.TryGetIncarnation(
                        member.EntityId,
                        out var incarnation)
                    || incarnation != member.Incarnation)
                {
                    return WorldSettlementAuthorityDecision.Deny(
                        NativeWorldSettlementReasonCodes
                            .IncarnationMismatch);
                }
            }

            var policyLease = ownership.PolicyLease;
            if (policyLease is not null)
            {
                var decision = await _session
                    .InvokeSettlementPolicyCallbackAsync(
                        () => policyLease.ValidateAsync(
                            claim,
                            cancellationToken))
                    .ConfigureAwait(false);
                return decision
                       ?? WorldSettlementAuthorityDecision.Deny(
                           NativeWorldSettlementReasonCodes
                               .AudiencePolicyDenied);
            }

            return claim.Audience.Members.Count == 1
                   && string.Equals(
                       claim.Audience.PrivacyClass,
                       WorldSettlementPrivacyClasses.Private,
                       StringComparison.Ordinal)
                ? WorldSettlementAuthorityDecision.Allow()
                : WorldSettlementAuthorityDecision.Deny(
                    NativeWorldSettlementReasonCodes
                        .AudiencePolicyRequired);
        }

        public ValueTask DisposeAsync()
        {
            var existing = Volatile.Read(ref _disposalTask);
            if (existing is not null)
            {
                return new ValueTask(existing);
            }

            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            existing = Interlocked.CompareExchange(
                ref _disposalTask,
                completion.Task,
                comparand: null);
            if (existing is not null)
            {
                return new ValueTask(existing);
            }

            _ = completion.Task.ContinueWith(
                static faulted =>
                {
                    _ = faulted.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            _ = DisposeOwnershipAsync(completion);
            return new ValueTask(completion.Task);
        }

        private async Task DisposeOwnershipAsync(
            TaskCompletionSource<object?> completion)
        {
            try
            {
                var ownership =
                    Interlocked.Exchange(ref _ownership, null);
                if (ownership is not null)
                {
                    if (ownership.PolicyLease is null)
                    {
                        await ownership.NativeLease.DisposeAsync()
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            await _session
                                .InvokeSettlementPolicyCallbackAsync(
                                    () => ownership.PolicyLease
                                        .DisposeAsync())
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            await ownership.NativeLease.DisposeAsync()
                                .ConfigureAwait(false);
                        }
                    }
                }

                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private sealed class LeaseOwnership
        {
            public LeaseOwnership(
                NativeWorldEngineSettlementLease nativeLease,
                INativeWorldSettlementAudiencePolicyLease? policyLease)
            {
                NativeLease = nativeLease;
                PolicyLease = policyLease;
            }

            public NativeWorldEngineSettlementLease NativeLease { get; }

            public INativeWorldSettlementAudiencePolicyLease? PolicyLease
            {
                get;
            }
        }

        private sealed class ClaimIdentity
        {
            public ClaimIdentity(
                WorldSettlementSinkKind kind,
                string deliveryDigest)
            {
                Kind = kind;
                DeliveryDigest = deliveryDigest;
            }

            public WorldSettlementSinkKind Kind { get; }

            public string DeliveryDigest { get; }
        }
    }
}

/// <summary>
/// Small native composition root for receipt evidence, exact authority, and
/// the existing durable settlement coordinator.
/// </summary>
public sealed class NativeWorldSettlementComposition
{
    public NativeWorldSettlementComposition(
        NativeWorldEngineSession session,
        INativeWorldSettlementAudiencePolicy? policy = null)
    {
        EvidenceSource = new NativeWorldCommittedEvidenceSource(
            session
            ?? throw new ArgumentNullException(nameof(session)));
        AuthorityGuard = new NativeWorldSettlementAuthorityGuard(
            session,
            policy);
    }

    public NativeWorldCommittedEvidenceSource EvidenceSource { get; }

    public NativeWorldSettlementAuthorityGuard AuthorityGuard { get; }

    public WorldSettlementCoordinator CreateCoordinator(
        IWorldSettlementStore store,
        IIdempotentAtomicMemoryBatchStore? memory = null,
        IGroupInteractionStore? groups = null,
        IWorldPresentationStore? presentations = null,
        WorldSettlementCoordinatorOptions? options = null)
    {
        return new WorldSettlementCoordinator(
            EvidenceSource,
            AuthorityGuard,
            store
            ?? throw new ArgumentNullException(nameof(store)),
            memory,
            groups,
            presentations,
            options);
    }
}
