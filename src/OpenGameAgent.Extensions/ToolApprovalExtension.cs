using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public enum GameToolApprovalMode
{
    Disabled,
    ExplicitOnly,
    ConfirmOnce,
    AllowedInTask,
}

public enum GameToolApprovalStatus
{
    Pending,
    Approved,
    Denied,
    TimedOut,
    Cancelled,
    Consumed,
    Expired,
}

public enum GameToolApprovalResponseKind
{
    Approve,
    Deny,
}

public sealed class GameToolApprovalRule
{
    public GameToolApprovalRule(
        string id,
        GameToolApprovalMode mode,
        string? toolName = null,
        ToolRisk? minimumRisk = null)
    {
        Id = RequireId(id, nameof(id), 256);
        if (!Enum.IsDefined(typeof(GameToolApprovalMode), mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (toolName is not null)
        {
            ToolName = RequireId(toolName, nameof(toolName), 128);
        }

        if (minimumRisk is { } risk && !Enum.IsDefined(typeof(ToolRisk), risk))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRisk));
        }

        if (ToolName is null && minimumRisk is null)
        {
            throw new ArgumentException("An approval rule must match a tool name, a minimum risk, or both.");
        }

        Mode = mode;
        MinimumRisk = minimumRisk;
    }

    public string Id { get; }

    public GameToolApprovalMode Mode { get; }

    public string? ToolName { get; }

    public ToolRisk? MinimumRisk { get; }

    public bool Matches(string toolName, ToolRisk risk) =>
        (ToolName is null || string.Equals(ToolName, toolName, StringComparison.Ordinal))
        && (MinimumRisk is null || risk >= MinimumRisk.Value);

    private static string RequireId(string value, string name, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"A non-empty value of at most {maximum} characters is required.", name)
            : value;
}

/// <summary>
/// Host-attested invocation intent. The model and tool arguments cannot create this scope.
/// </summary>
public sealed class GameToolInvocationScope
{
    public GameToolInvocationScope(
        IEnumerable<string>? explicitlyRequestedTools = null,
        string? taskId = null,
        IEnumerable<string>? taskAllowedTools = null)
    {
        ExplicitlyRequestedTools = CopyTools(explicitlyRequestedTools, nameof(explicitlyRequestedTools));
        TaskId = taskId is null ? null : RequireId(taskId, nameof(taskId), 1_024);
        TaskAllowedTools = CopyTools(taskAllowedTools, nameof(taskAllowedTools));
        if (TaskId is null && TaskAllowedTools.Count > 0)
        {
            throw new ArgumentException("Task-allowed tools require a host-attested task ID.", nameof(taskAllowedTools));
        }
    }

    public IReadOnlyCollection<string> ExplicitlyRequestedTools { get; }

    public string? TaskId { get; }

    public IReadOnlyCollection<string> TaskAllowedTools { get; }

    public bool IsExplicit(string toolName) => ExplicitlyRequestedTools.Contains(toolName, StringComparer.Ordinal);

    public bool IsAllowedInTask(string toolName) =>
        TaskId is not null && TaskAllowedTools.Contains(toolName, StringComparer.Ordinal);

    private static IReadOnlyCollection<string> CopyTools(IEnumerable<string>? source, string name)
    {
        var values = source?.ToArray() ?? Array.Empty<string>();
        if (values.Length > 1_024
            || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException("Tool names must be bounded, non-empty, and unique.", name);
        }

        return Array.AsReadOnly(values);
    }

    private static string RequireId(string value, string name, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"A non-empty value of at most {maximum} characters is required.", name)
            : value;
}

public sealed class GameToolInvocationScopeContext
{
    public GameToolInvocationScopeContext(GameInput input, ToolCallContent call, ToolRisk risk)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Call = call ?? throw new ArgumentNullException(nameof(call));
        Risk = risk;
    }

    public GameInput Input { get; }

    public ToolCallContent Call { get; }

    public ToolRisk Risk { get; }
}

public interface IGameToolInvocationScopeProvider
{
    ValueTask<GameToolInvocationScope> ResolveAsync(
        GameToolInvocationScopeContext context,
        CancellationToken cancellationToken);
}

public readonly struct GameToolApprovalWorldState : IEquatable<GameToolApprovalWorldState>
{
    public GameToolApprovalWorldState(string generationId, long revision)
    {
        GenerationId = string.IsNullOrWhiteSpace(generationId)
                       || generationId.Length > 1_024
                       || generationId.Any(char.IsControl)
            ? throw new ArgumentException("A bounded world generation ID is required.", nameof(generationId))
            : generationId;
        Revision = revision >= 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public string GenerationId { get; }

    public long Revision { get; }

    public bool Equals(GameToolApprovalWorldState other) =>
        Revision == other.Revision
        && string.Equals(GenerationId, other.GenerationId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GameToolApprovalWorldState other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GenerationId, Revision);
}

public interface IGameToolApprovalWorldStateProvider
{
    ValueTask<GameToolApprovalWorldState> ReadAsync(GameInput input, CancellationToken cancellationToken);
}

public sealed class GameToolApprovalRequest
{
    public GameToolApprovalRequest(
        string approvalId,
        string policyId,
        string sessionId,
        string actorId,
        string inputId,
        string runId,
        int turn,
        string toolCallId,
        string toolName,
        ToolRisk risk,
        string canonicalArgumentsJson,
        string argumentsDigest,
        GameMoment moment,
        GameToolApprovalWorldState world,
        string? taskId,
        DateTimeOffset requestedAt,
        DateTimeOffset expiresAt)
    {
        ApprovalId = Require(approvalId, nameof(approvalId), 256);
        PolicyId = Require(policyId, nameof(policyId), 256);
        SessionId = Require(sessionId, nameof(sessionId), 1_024);
        ActorId = Require(actorId, nameof(actorId), 1_024);
        InputId = Require(inputId, nameof(inputId), 1_024);
        RunId = Require(runId, nameof(runId), 1_024);
        Turn = turn > 0 ? turn : throw new ArgumentOutOfRangeException(nameof(turn));
        ToolCallId = Require(toolCallId, nameof(toolCallId), 1_024);
        ToolName = Require(toolName, nameof(toolName), 128);
        if (!Enum.IsDefined(typeof(ToolRisk), risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        if (string.IsNullOrWhiteSpace(canonicalArgumentsJson) || canonicalArgumentsJson.Length > 1_000_000)
        {
            throw new ArgumentException("Canonical arguments must contain at most 1000000 characters.", nameof(canonicalArgumentsJson));
        }

        using (var document = JsonDocument.Parse(canonicalArgumentsJson, new JsonDocumentOptions { MaxDepth = 128 }))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Tool arguments must be a JSON object.", nameof(canonicalArgumentsJson));
            }
        }

        ArgumentsDigest = Require(argumentsDigest, nameof(argumentsDigest), 128);
        CanonicalArgumentsJson = canonicalArgumentsJson;
        Moment = moment;
        World = world;
        TaskId = taskId is null ? null : Require(taskId, nameof(taskId), 1_024);
        RequestedAt = requestedAt;
        ExpiresAt = expiresAt > requestedAt
            ? expiresAt
            : throw new ArgumentException("Approval expiry must follow its request time.", nameof(expiresAt));
        Risk = risk;
    }

    public string ApprovalId { get; }
    public string PolicyId { get; }
    public string SessionId { get; }
    public string ActorId { get; }
    public string InputId { get; }
    public string RunId { get; }
    public int Turn { get; }
    public string ToolCallId { get; }
    public string ToolName { get; }
    public ToolRisk Risk { get; }
    public string CanonicalArgumentsJson { get; }
    public string ArgumentsDigest { get; }
    public GameMoment Moment { get; }
    public GameToolApprovalWorldState World { get; }
    public string? TaskId { get; }
    public DateTimeOffset RequestedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public GameSessionKey Owner => new(SessionId, ActorId);

    private static string Require(string value, string name, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"A non-empty value of at most {maximum} characters is required.", name)
            : value;
}

public sealed class GameToolApprovalRecord
{
    public GameToolApprovalRecord(
        GameToolApprovalRequest request,
        GameToolApprovalStatus status,
        long revision,
        DateTimeOffset updatedAt,
        string? reason = null,
        string? credentialDigest = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (!Enum.IsDefined(typeof(GameToolApprovalStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Revision = revision >= 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
        UpdatedAt = updatedAt;
        Reason = reason is null ? null : reason.Length <= 4_096 ? reason : reason.Substring(0, 4_096);
        CredentialDigest = credentialDigest;
    }

    public GameToolApprovalRequest Request { get; }
    public GameToolApprovalStatus Status { get; }
    public long Revision { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string? Reason { get; }
    public string? CredentialDigest { get; }
}

public interface IGameToolApprovalStore
{
    ValueTask<GameToolApprovalRecord?> ReadAsync(
        GameSessionKey owner,
        string approvalId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameToolApprovalRecord>> ListAsync(
        GameSessionKey owner,
        GameToolApprovalStatus? status,
        int maximum,
        CancellationToken cancellationToken);

    /// <summary>Creates when expectedRevision is null; otherwise performs a revision CAS update.</summary>
    ValueTask<GameToolApprovalRecord> SaveAsync(
        GameToolApprovalRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameToolApprovalStore : IGameToolApprovalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameToolApprovalRecord> _records = new(StringComparer.Ordinal);

    public ValueTask<GameToolApprovalRecord?> ReadAsync(
        GameSessionKey owner,
        string approvalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _records.TryGetValue(approvalId, out var value);
            if (value is not null && !value.Request.Owner.Equals(owner))
            {
                value = null;
            }
            return new ValueTask<GameToolApprovalRecord?>(value);
        }
    }

    public ValueTask<IReadOnlyList<GameToolApprovalRecord>> ListAsync(
        GameSessionKey owner,
        GameToolApprovalStatus? status,
        int maximum,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximum < 1 || maximum > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        lock (_gate)
        {
            var values = _records.Values
                .Where(value => value.Request.Owner.Equals(owner) && (status is null || value.Status == status))
                .OrderBy(value => value.Request.RequestedAt)
                .ThenBy(value => value.Request.ApprovalId, StringComparer.Ordinal)
                .Take(maximum)
                .ToArray();
            return new ValueTask<IReadOnlyList<GameToolApprovalRecord>>(Array.AsReadOnly(values));
        }
    }

    public ValueTask<GameToolApprovalRecord> SaveAsync(
        GameToolApprovalRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        lock (_gate)
        {
            _records.TryGetValue(record.Request.ApprovalId, out var current);
            if (expectedRevision is null)
            {
                if (current is not null)
                {
                    if (!GameToolApprovalBroker.EquivalentRequest(current.Request, record.Request))
                    {
                        throw new InvalidOperationException("The approval ID is already bound to a different request.");
                    }

                    return new ValueTask<GameToolApprovalRecord>(current);
                }

                if (record.Revision != 0 || record.Status != GameToolApprovalStatus.Pending)
                {
                    throw new ArgumentException("A new approval must start pending at revision zero.", nameof(record));
                }
            }
            else if (current is null || current.Revision != expectedRevision.Value)
            {
                throw new InvalidOperationException("The approval revision changed.");
            }
            else if (!GameToolApprovalBroker.EquivalentRequest(current.Request, record.Request)
                     || record.Revision != checked(current.Revision + 1)
                     || !IsValidTransition(current.Status, record.Status))
            {
                throw new InvalidOperationException("The approval update changes immutable identity or has an invalid transition.");
            }

            _records[record.Request.ApprovalId] = record;
            return new ValueTask<GameToolApprovalRecord>(record);
        }
    }

    private static bool IsValidTransition(GameToolApprovalStatus current, GameToolApprovalStatus next) =>
        current == GameToolApprovalStatus.Pending
            ? next is GameToolApprovalStatus.Approved
                or GameToolApprovalStatus.Denied
                or GameToolApprovalStatus.TimedOut
                or GameToolApprovalStatus.Cancelled
                or GameToolApprovalStatus.Expired
            : current == GameToolApprovalStatus.Approved
              && next is GameToolApprovalStatus.Consumed or GameToolApprovalStatus.Expired;
}

public sealed class GameToolApprovalResponse
{
    public GameToolApprovalResponse(
        GameSessionKey owner,
        string approvalId,
        long expectedRevision,
        GameToolApprovalResponseKind response,
        string? reason = null)
    {
        Owner = owner;
        ApprovalId = string.IsNullOrWhiteSpace(approvalId) || approvalId.Length > 256
            ? throw new ArgumentException("A bounded approval ID is required.", nameof(approvalId))
            : approvalId;
        ExpectedRevision = expectedRevision >= 0
            ? expectedRevision
            : throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!Enum.IsDefined(typeof(GameToolApprovalResponseKind), response))
        {
            throw new ArgumentOutOfRangeException(nameof(response));
        }

        Response = response;
        Reason = reason is null ? null : reason.Length <= 4_096 ? reason : reason.Substring(0, 4_096);
    }

    public GameSessionKey Owner { get; }
    public string ApprovalId { get; }
    public long ExpectedRevision { get; }
    public GameToolApprovalResponseKind Response { get; }
    public string? Reason { get; }
}

public sealed class GameToolApprovalWaitResult
{
    internal GameToolApprovalWaitResult(GameToolApprovalRecord record, string? credential)
    {
        Record = record;
        Credential = credential;
    }

    public GameToolApprovalRecord Record { get; }

    internal string? Credential { get; }
}

public interface IGameToolApprovalBroker
{
    ValueTask<GameToolApprovalWaitResult> WaitForDecisionAsync(
        GameToolApprovalRequest request,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameToolApprovalRecord>> ListPendingAsync(
        GameSessionKey owner,
        int maximum,
        CancellationToken cancellationToken);

    ValueTask<GameToolApprovalRecord> RespondAsync(
        GameToolApprovalResponse response,
        CancellationToken cancellationToken);

    ValueTask<GameToolApprovalRecord> ConsumeAsync(
        GameToolApprovalRequest request,
        string credential,
        long expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<GameToolApprovalRecord> InvalidateAsync(
        GameToolApprovalRequest request,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<GameToolApprovalRecord> CancelAsync(
        GameToolApprovalRequest request,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken);
}

/// <summary>
/// Durable host broker. Only credential digests are persisted. Plaintext one-time credentials
/// remain process-local and are consumed by the waiting authorization hook.
/// </summary>
public sealed class GameToolApprovalBroker : IGameToolApprovalBroker
{
    private readonly IGameToolApprovalStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _signals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _grants = new(StringComparer.Ordinal);

    public GameToolApprovalBroker(IGameToolApprovalStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<GameToolApprovalWaitResult> WaitForDecisionAsync(
        GameToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await ExpireOrphanedApprovalsAsync(request.Owner, cancellationToken).ConfigureAwait(false);
        var signal = _signals.GetOrAdd(request.ApprovalId, static _ =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        var pending = new GameToolApprovalRecord(request, GameToolApprovalStatus.Pending, 0, _clock());
        var current = await _store.SaveAsync(pending, null, cancellationToken).ConfigureAwait(false);
        try
        {
            while (current.Status == GameToolApprovalStatus.Pending)
            {
                var remaining = request.ExpiresAt - _clock();
                if (remaining <= TimeSpan.Zero)
                {
                    current = await TrySettleAsync(
                        current,
                        GameToolApprovalStatus.TimedOut,
                        "Approval timed out.",
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, delayCancellation.Token);
                var completed = await Task.WhenAny(signal.Task, delay).ConfigureAwait(false);
                if (completed == signal.Task)
                {
                    delayCancellation.Cancel();
                }

                cancellationToken.ThrowIfCancellationRequested();
                current = await _store.ReadAsync(request.Owner, request.ApprovalId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The approval request disappeared from its store.");
            }

            _grants.TryGetValue(request.ApprovalId, out var credential);
            if (current.Status == GameToolApprovalStatus.Approved && credential is null)
            {
                var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken))
                    .ConfigureAwait(false);
                if (completed == signal.Task)
                {
                    _grants.TryGetValue(request.ApprovalId, out credential);
                }

                if (credential is null)
                {
                    current = await TrySettleAsync(
                        current,
                        GameToolApprovalStatus.Expired,
                        "The process-local approval credential is no longer available.",
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            return new GameToolApprovalWaitResult(current, credential);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            current = await _store.ReadAsync(request.Owner, request.ApprovalId, CancellationToken.None).ConfigureAwait(false) ?? current;
            if (current.Status == GameToolApprovalStatus.Pending)
            {
                current = await TrySettleAsync(
                    current,
                    GameToolApprovalStatus.Cancelled,
                    "Approval wait was cancelled.",
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (current.Status == GameToolApprovalStatus.Approved)
            {
                await TrySettleAsync(
                    current,
                    GameToolApprovalStatus.Expired,
                    "Approval was invalidated when its waiting run was cancelled.",
                    null,
                    CancellationToken.None).ConfigureAwait(false);
                _grants.TryRemove(request.ApprovalId, out _);
            }

            throw;
        }
        finally
        {
            _signals.TryRemove(request.ApprovalId, out _);
        }
    }

    public async ValueTask<IReadOnlyList<GameToolApprovalRecord>> ListPendingAsync(
        GameSessionKey owner,
        int maximum,
        CancellationToken cancellationToken)
    {
        await ExpireOrphanedApprovalsAsync(owner, cancellationToken).ConfigureAwait(false);
        var values = await _store.ListAsync(owner, GameToolApprovalStatus.Pending, maximum, cancellationToken)
            .ConfigureAwait(false);
        var result = new List<GameToolApprovalRecord>(values.Count);
        foreach (var value in values)
        {
            if (value.Request.ExpiresAt <= _clock())
            {
                await TrySettleAsync(
                    value,
                    GameToolApprovalStatus.TimedOut,
                    "Approval timed out.",
                    null,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            result.Add(value);
        }

        return new ReadOnlyCollection<GameToolApprovalRecord>(result);
    }

    private async ValueTask ExpireOrphanedApprovalsAsync(
        GameSessionKey owner,
        CancellationToken cancellationToken)
    {
        var approved = await _store.ListAsync(
            owner,
            GameToolApprovalStatus.Approved,
            10_000,
            cancellationToken).ConfigureAwait(false);
        foreach (var value in approved)
        {
            if (_signals.ContainsKey(value.Request.ApprovalId)
                || _grants.ContainsKey(value.Request.ApprovalId))
            {
                continue;
            }

            await TrySettleAsync(
                value,
                GameToolApprovalStatus.Expired,
                "The approving process ended before the one-time credential was consumed.",
                null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<GameToolApprovalRecord> RespondAsync(
        GameToolApprovalResponse response,
        CancellationToken cancellationToken)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var current = await _store.ReadAsync(response.Owner, response.ApprovalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The approval request does not exist.");
        if (!current.Request.Owner.Equals(response.Owner))
        {
            throw new UnauthorizedAccessException("The approval request belongs to a different session or actor.");
        }

        if (current.Revision != response.ExpectedRevision || current.Status != GameToolApprovalStatus.Pending)
        {
            throw new InvalidOperationException("The approval request is no longer pending at the expected revision.");
        }

        if (current.Request.ExpiresAt <= _clock())
        {
            return await TrySettleAsync(
                current,
                GameToolApprovalStatus.TimedOut,
                "Approval timed out.",
                null,
                cancellationToken).ConfigureAwait(false);
        }

        string? credential = null;
        string? credentialDigest = null;
        var status = GameToolApprovalStatus.Denied;
        if (response.Response == GameToolApprovalResponseKind.Approve)
        {
            if (!_signals.ContainsKey(response.ApprovalId))
            {
                throw new InvalidOperationException("The run waiting for this approval is no longer active; deny or let the request expire.");
            }

            var bytes = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            credential = Convert.ToBase64String(bytes);
            credentialDigest = Sha256(credential);
            status = GameToolApprovalStatus.Approved;
        }

        var updated = await TrySettleAsync(
            current,
            status,
            response.Reason,
            credentialDigest,
            cancellationToken).ConfigureAwait(false);
        if (credential is not null
            && updated.Status == GameToolApprovalStatus.Approved
            && updated.CredentialDigest is not null
            && FixedTimeEquals(updated.CredentialDigest, credentialDigest!))
        {
            _grants[response.ApprovalId] = credential;
            var verified = await _store.ReadAsync(
                response.Owner,
                response.ApprovalId,
                CancellationToken.None).ConfigureAwait(false);
            if (verified is null
                || verified.Status != GameToolApprovalStatus.Approved
                || verified.Revision != updated.Revision
                || verified.CredentialDigest is null
                || !FixedTimeEquals(verified.CredentialDigest, credentialDigest!))
            {
                _grants.TryRemove(response.ApprovalId, out _);
            }
        }
        else if (updated.Status != GameToolApprovalStatus.Approved)
        {
            _grants.TryRemove(response.ApprovalId, out _);
        }

        if (_signals.TryGetValue(response.ApprovalId, out var signal))
        {
            signal.TrySetResult(true);
        }

        return updated;
    }

    public async ValueTask<GameToolApprovalRecord> ConsumeAsync(
        GameToolApprovalRequest request,
        string credential,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(credential) || credential.Length > 256)
        {
            throw new ArgumentException("A request and bounded approval credential are required.");
        }

        var current = await _store.ReadAsync(request.Owner, request.ApprovalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The approval request does not exist.");
        if (!EquivalentRequest(current.Request, request)
            || current.Revision != expectedRevision
            || current.Status != GameToolApprovalStatus.Approved
            || current.CredentialDigest is null
            || !FixedTimeEquals(current.CredentialDigest, Sha256(credential)))
        {
            throw new InvalidOperationException("The approval credential is invalid, stale, or already consumed.");
        }

        var consumed = new GameToolApprovalRecord(
            current.Request,
            GameToolApprovalStatus.Consumed,
            checked(current.Revision + 1),
            _clock(),
            current.Reason,
            null);
        var saved = await _store.SaveAsync(consumed, current.Revision, cancellationToken).ConfigureAwait(false);
        _grants.TryRemove(request.ApprovalId, out _);
        return saved;
    }

    public async ValueTask<GameToolApprovalRecord> InvalidateAsync(
        GameToolApprovalRequest request,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var current = await _store.ReadAsync(request.Owner, request.ApprovalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The approval request does not exist.");
        if (!EquivalentRequest(current.Request, request)
            || current.Revision != expectedRevision
            || current.Status is not GameToolApprovalStatus.Pending and not GameToolApprovalStatus.Approved)
        {
            throw new InvalidOperationException("The approval request is stale or already terminal.");
        }

        var expired = await TrySettleAsync(
            current,
            GameToolApprovalStatus.Expired,
            reason,
            null,
            cancellationToken).ConfigureAwait(false);
        _grants.TryRemove(request.ApprovalId, out _);
        if (_signals.TryGetValue(request.ApprovalId, out var signal))
        {
            signal.TrySetResult(true);
        }

        return expired;
    }

    public async ValueTask<GameToolApprovalRecord> CancelAsync(
        GameToolApprovalRequest request,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var current = await _store.ReadAsync(request.Owner, request.ApprovalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The approval request does not exist.");
        if (!EquivalentRequest(current.Request, request)
            || current.Revision != expectedRevision
            || current.Status is not GameToolApprovalStatus.Pending and not GameToolApprovalStatus.Approved)
        {
            throw new InvalidOperationException("The approval request is stale or already terminal.");
        }

        var cancelled = await TrySettleAsync(
            current,
            GameToolApprovalStatus.Cancelled,
            reason,
            null,
            cancellationToken).ConfigureAwait(false);
        _grants.TryRemove(request.ApprovalId, out _);
        if (_signals.TryGetValue(request.ApprovalId, out var signal))
        {
            signal.TrySetResult(true);
        }

        return cancelled;
    }

    public static bool EquivalentRequest(GameToolApprovalRequest left, GameToolApprovalRequest right) =>
        string.Equals(left.ApprovalId, right.ApprovalId, StringComparison.Ordinal)
        && string.Equals(left.PolicyId, right.PolicyId, StringComparison.Ordinal)
        && left.Owner.Equals(right.Owner)
        && string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
        && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && left.Turn == right.Turn
        && string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal)
        && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
        && left.Risk == right.Risk
        && string.Equals(left.ArgumentsDigest, right.ArgumentsDigest, StringComparison.Ordinal)
        && left.Moment.Equals(right.Moment)
        && left.World.Equals(right.World)
        && string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal);

    private async ValueTask<GameToolApprovalRecord> TrySettleAsync(
        GameToolApprovalRecord current,
        GameToolApprovalStatus status,
        string? reason,
        string? credentialDigest,
        CancellationToken cancellationToken)
    {
        var next = new GameToolApprovalRecord(
            current.Request,
            status,
            checked(current.Revision + 1),
            _clock(),
            reason,
            credentialDigest);
        try
        {
            return await _store.SaveAsync(next, current.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return await _store.ReadAsync(current.Request.Owner, current.Request.ApprovalId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The approval record disappeared during a revision conflict.", exception);
        }
    }

    private static string Sha256(string value)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < leftBytes.Length; index++)
        {
            difference |= leftBytes[index] ^ rightBytes[index];
        }

        return difference == 0;
    }
}

public sealed class GameToolApprovalEvent
{
    public GameToolApprovalEvent(
        string approvalId,
        string sessionId,
        string actorId,
        string inputId,
        GameMoment moment,
        string runId,
        string toolCallId,
        string toolName,
        GameToolApprovalStatus status,
        TimeSpan waitDuration)
    {
        ApprovalId = approvalId;
        SessionId = sessionId;
        ActorId = actorId;
        InputId = inputId;
        Moment = moment;
        RunId = runId;
        ToolCallId = toolCallId;
        ToolName = toolName;
        Status = status;
        WaitDuration = waitDuration < TimeSpan.Zero ? TimeSpan.Zero : waitDuration;
    }

    public string ApprovalId { get; }
    public string SessionId { get; }
    public string ActorId { get; }
    public string InputId { get; }
    public GameMoment Moment { get; }
    public string RunId { get; }
    public string ToolCallId { get; }
    public string ToolName { get; }
    public GameToolApprovalStatus Status { get; }
    public TimeSpan WaitDuration { get; }
}

public sealed class GameToolApprovalOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public Func<DateTimeOffset> OperationalClock { get; set; } = () => DateTimeOffset.UtcNow;

    internal GameToolApprovalOptions CopyAndValidate()
    {
        var copy = (GameToolApprovalOptions)MemberwiseClone();
        if (copy.Timeout < TimeSpan.FromMilliseconds(1) || copy.Timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        if (copy.OperationalClock is null)
        {
            throw new ArgumentNullException(nameof(OperationalClock));
        }
        return copy;
    }
}

/// <summary>
/// Provider-neutral, fail-closed execution approval gate. Game rules and UI remain host-owned.
/// </summary>
public sealed class ToolApprovalExtension : IGameAgentExtension
{
    private readonly IReadOnlyList<GameToolApprovalRule> _rules;
    private readonly IGameToolApprovalBroker _broker;
    private readonly IGameToolApprovalWorldStateProvider _world;
    private readonly IGameToolInvocationScopeProvider? _scope;
    private readonly GameToolApprovalOptions _options;

    public ToolApprovalExtension(
        IEnumerable<GameToolApprovalRule> rules,
        IGameToolApprovalBroker broker,
        IGameToolApprovalWorldStateProvider worldStateProvider,
        IGameToolInvocationScopeProvider? scopeProvider = null,
        GameToolApprovalOptions? options = null)
    {
        var copied = (rules ?? throw new ArgumentNullException(nameof(rules))).ToArray();
        if (copied.Any(value => value is null)
            || copied.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Approval rules must be non-null and have unique IDs.", nameof(rules));
        }

        _rules = Array.AsReadOnly(copied);
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _world = worldStateProvider ?? throw new ArgumentNullException(nameof(worldStateProvider));
        _scope = scopeProvider;
        _options = (options ?? new GameToolApprovalOptions()).CopyAndValidate();
    }

    public static GameAgentExtensionChannel<GameToolApprovalEvent> ApprovalChanged { get; } =
        new("tool.approval.changed");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.tool-approval",
        "1.0.0",
        "Durable, one-time, world-bound approvals for high-risk tool execution.",
        new[] { "tool-policy", "approval", "audit" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.RegisterService("tool-approval-broker", _broker, priority: 1_000);
        api.RegisterAgentHooks(
            "tool-approval-final-gate",
            runContext => new AgentHooks
            {
                AuthorizeToolCallAsync = async (context, cancellationToken) =>
                {
                    var tool = context.Context.Tools.FirstOrDefault(value =>
                        string.Equals(value.Definition.Name, context.ToolCall.Name, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("The prepared tool is no longer in the active tool catalog.");
                    var rule = _rules.FirstOrDefault(value => value.Matches(context.ToolCall.Name, tool.Risk));
                    if (rule is null)
                    {
                        return null;
                    }

                    var scope = _scope is null
                        ? new GameToolInvocationScope()
                        : await _scope.ResolveAsync(
                            new GameToolInvocationScopeContext(runContext.Input, context.ToolCall, tool.Risk),
                            cancellationToken).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("The invocation scope provider returned null.");
                    switch (rule.Mode)
                    {
                        case GameToolApprovalMode.Disabled:
                            return ToolCallDecision.Block($"Tool '{context.ToolCall.Name}' is disabled by host policy.");
                        case GameToolApprovalMode.ExplicitOnly:
                            return scope.IsExplicit(context.ToolCall.Name)
                                ? null
                                : ToolCallDecision.Block($"Tool '{context.ToolCall.Name}' requires an explicit host-attested request.");
                        case GameToolApprovalMode.AllowedInTask:
                            return scope.IsAllowedInTask(context.ToolCall.Name)
                                ? null
                                : ToolCallDecision.Block($"Tool '{context.ToolCall.Name}' is not allowed in the current host-attested task.");
                        case GameToolApprovalMode.ConfirmOnce:
                            return await ConfirmAsync(api, runContext, context, tool.Risk, rule, scope, cancellationToken)
                                .ConfigureAwait(false);
                        default:
                            throw new InvalidOperationException("Unsupported tool approval mode.");
                    }
                },
            },
            priority: int.MaxValue);
    }

    private async ValueTask<ToolCallDecision?> ConfirmAsync(
        GameAgentExtensionApi api,
        GameAgentExtensionRunContext runContext,
        AuthorizeToolCallContext context,
        ToolRisk risk,
        GameToolApprovalRule rule,
        GameToolInvocationScope scope,
        CancellationToken cancellationToken)
    {
        var startedAt = _options.OperationalClock();
        var initialWorld = await _world.ReadAsync(runContext.Input, cancellationToken).ConfigureAwait(false);
        var canonicalArguments = Canonicalize(context.Arguments);
        var digest = Sha256(canonicalArguments);
        var approvalId = CreateApprovalId(
            rule.Id,
            runContext.Input,
            context,
            digest,
            initialWorld,
            scope.TaskId);
        var request = new GameToolApprovalRequest(
            approvalId,
            rule.Id,
            runContext.Input.SessionId,
            runContext.Input.ActorId,
            runContext.Input.InputId,
            context.RunId,
            context.Turn,
            context.ToolCall.Id,
            context.ToolCall.Name,
            risk,
            canonicalArguments,
            digest,
            runContext.Input.Moment,
            initialWorld,
            scope.TaskId,
            startedAt,
            startedAt + _options.Timeout);
        await api.PublishAsync(
            ApprovalChanged,
            new GameToolApprovalEvent(
                approvalId,
                request.SessionId,
                request.ActorId,
                request.InputId,
                request.Moment,
                context.RunId,
                context.ToolCall.Id,
                context.ToolCall.Name,
                GameToolApprovalStatus.Pending,
                TimeSpan.Zero),
            CancellationToken.None).ConfigureAwait(false);

        GameToolApprovalWaitResult decision;
        try
        {
            decision = await _broker.WaitForDecisionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PublishCompletedAsync(api, request, GameToolApprovalStatus.Cancelled, startedAt).ConfigureAwait(false);
            throw;
        }

        if (decision.Record.Status != GameToolApprovalStatus.Approved || decision.Credential is null)
        {
            await PublishCompletedAsync(api, request, decision.Record.Status, startedAt).ConfigureAwait(false);
            return ToolCallDecision.Block(decision.Record.Reason ?? "Tool approval was not granted.");
        }

        GameToolApprovalWorldState currentWorld;
        try
        {
            currentWorld = await _world.ReadAsync(runContext.Input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _broker.CancelAsync(
                request,
                decision.Record.Revision,
                "Approval was cancelled before execution.",
                CancellationToken.None).ConfigureAwait(false);
            await PublishCompletedAsync(api, request, GameToolApprovalStatus.Cancelled, startedAt).ConfigureAwait(false);
            throw;
        }
        if (!currentWorld.Equals(initialWorld))
        {
            await _broker.InvalidateAsync(
                request,
                decision.Record.Revision,
                "The authoritative world changed while tool approval was pending.",
                CancellationToken.None).ConfigureAwait(false);
            await PublishCompletedAsync(api, request, GameToolApprovalStatus.Expired, startedAt).ConfigureAwait(false);
            return ToolCallDecision.Block("The authoritative world changed while tool approval was pending.");
        }

        try
        {
            var consumed = await _broker.ConsumeAsync(
                request,
                decision.Credential,
                decision.Record.Revision,
                cancellationToken).ConfigureAwait(false);
            await PublishCompletedAsync(api, request, consumed.Status, startedAt).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _broker.CancelAsync(
                    request,
                    decision.Record.Revision,
                    "Approval was cancelled before execution.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cancellationFailure) when (cancellationFailure is InvalidOperationException or KeyNotFoundException)
            {
            }

            await PublishCompletedAsync(api, request, GameToolApprovalStatus.Cancelled, startedAt).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            try
            {
                await _broker.InvalidateAsync(
                    request,
                    decision.Record.Revision,
                    "The one-time approval was stale before tool execution.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception invalidationFailure) when (invalidationFailure is InvalidOperationException or KeyNotFoundException)
            {
            }

            await PublishCompletedAsync(api, request, GameToolApprovalStatus.Expired, startedAt).ConfigureAwait(false);
            return ToolCallDecision.Block("The one-time tool approval was stale or already consumed.");
        }
    }

    private ValueTask PublishCompletedAsync(
        GameAgentExtensionApi api,
        GameToolApprovalRequest request,
        GameToolApprovalStatus status,
        DateTimeOffset startedAt) =>
        api.PublishAsync(
            ApprovalChanged,
            new GameToolApprovalEvent(
                request.ApprovalId,
                request.SessionId,
                request.ActorId,
                request.InputId,
                request.Moment,
                request.RunId,
                request.ToolCallId,
                request.ToolName,
                status,
                _options.OperationalClock() - startedAt),
            CancellationToken.None);

    private static string CreateApprovalId(
        string policyId,
        GameInput input,
        AuthorizeToolCallContext context,
        string argumentsDigest,
        GameToolApprovalWorldState world,
        string? taskId) =>
        "approval-v1-" + Sha256(string.Join("\n", new[]
        {
            policyId,
            input.SessionId,
            input.ActorId,
            input.InputId,
            context.RunId,
            context.Turn.ToString(CultureInfo.InvariantCulture),
            context.ToolCall.Id,
            context.ToolCall.Name,
            argumentsDigest,
            input.Moment.TimelineId,
            input.Moment.Tick.ToString(CultureInfo.InvariantCulture),
            world.GenerationId,
            world.Revision.ToString(CultureInfo.InvariantCulture),
            taskId ?? string.Empty,
        })).Replace("/", "_", StringComparison.Ordinal).Replace("+", "-", StringComparison.Ordinal).TrimEnd('=');

    private static string Canonicalize(JsonElement value)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value, 0);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 128)
        {
            throw new JsonException("Tool arguments exceed the canonicalization depth limit.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var element in value.EnumerateArray())
                {
                    WriteCanonical(writer, element, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported JSON value in tool arguments.");
        }
    }

    private static string Sha256(string value)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
}
