using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Generation;

public sealed class GeneratedScriptAsset
{
    public string ScriptId { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string SourceText { get; set; } = string.Empty;

    public string? EntryPoint { get; set; }
}

public sealed class GeneratedContentManifest
{
    public string ContentId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string SourceOperationId { get; set; } = string.Empty;

    public JsonElement Data { get; set; }

    public IReadOnlyList<GenerationArtifact> Artifacts { get; set; } =
        Array.Empty<GenerationArtifact>();

    public IReadOnlyList<GeneratedScriptAsset> Scripts { get; set; } =
        Array.Empty<GeneratedScriptAsset>();

    public IReadOnlyList<string> Dependencies { get; set; } =
        Array.Empty<string>();

    public Dictionary<string, string> Provenance { get; set; } =
        new(StringComparer.Ordinal);

    public string Digest { get; internal set; } = string.Empty;
}

public static class ContentTransactionStates
{
    public const string Prepared = "prepared";
    public const string Staging = "staging";
    public const string Staged = "staged";
    public const string Validating = "validating";
    public const string Validated = "validated";
    public const string Committing = "committing";
    public const string Committed = "committed";
    public const string Aborting = "aborting";
    public const string Aborted = "aborted";
    public const string Rejected = "rejected";
    public const string Unknown = "unknown";
}

public sealed class ContentValidationResult
{
    public bool Accepted { get; set; }

    public string? ReasonCode { get; set; }

    public JsonElement? Details { get; set; }
}

public sealed class ContentCommitResult
{
    public string HostReceiptId { get; set; } = string.Empty;

    public JsonElement? Result { get; set; }
}

public sealed class ContentHostStatus
{
    public string State { get; set; } = ContentTransactionStates.Unknown;

    public string? HostReceiptId { get; set; }

    public JsonElement? Result { get; set; }
}

public sealed class GeneratedContentTransaction
{
    public string TransactionId { get; set; } = string.Empty;

    public GeneratedContentManifest Manifest { get; set; } = new();

    public string State { get; set; } = ContentTransactionStates.Prepared;

    public string? HostReceiptId { get; set; }

    public JsonElement? HostResult { get; set; }

    public string? ReasonCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public long Revision { get; set; }
}

public sealed class GeneratedContentLimits
{
    public int MaxManifestUtf8Bytes { get; set; } = 4 * 1024 * 1024;

    public int MaxArtifacts { get; set; } = 128;

    public int MaxScripts { get; set; } = 64;

    public int MaxScriptUtf8Bytes { get; set; } = 1024 * 1024;

    public int MaxDependencies { get; set; } = 256;

    public int MaxProvenanceEntries { get; set; } = 64;

    internal void Validate()
    {
        if (MaxManifestUtf8Bytes is < 1_024 or > 64 * 1024 * 1024
            || MaxArtifacts is < 0 or > 4_096
            || MaxScripts is < 0 or > 1_024
            || MaxScriptUtf8Bytes is < 0 or > 16 * 1024 * 1024
            || MaxDependencies is < 0 or > 4_096
            || MaxProvenanceEntries is < 0 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(GeneratedContentLimits));
        }
    }
}

public interface IGeneratedContentHost
{
    ValueTask StageAsync(
        string transactionId,
        GeneratedContentManifest manifest,
        CancellationToken cancellationToken);

    ValueTask<ContentValidationResult> ValidateAsync(
        string transactionId,
        GeneratedContentManifest manifest,
        CancellationToken cancellationToken);

    ValueTask<ContentCommitResult> CommitAsync(
        string transactionId,
        GeneratedContentManifest manifest,
        CancellationToken cancellationToken);

    ValueTask AbortAsync(
        string transactionId,
        GeneratedContentManifest manifest,
        CancellationToken cancellationToken);

    ValueTask<ContentHostStatus> GetStatusAsync(
        string transactionId,
        CancellationToken cancellationToken);
}

public interface IGeneratedContentTransactionStore
{
    ValueTask<GeneratedContentTransaction?> TryGetAsync(
        string transactionId,
        CancellationToken cancellationToken);

    ValueTask PutAsync(
        GeneratedContentTransaction transaction,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GeneratedContentTransaction>> ListUnfinishedAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGeneratedContentTransactionStore
    : IGeneratedContentTransactionStore
{
    private readonly ConcurrentDictionary<string, GeneratedContentTransaction> _items =
        new(StringComparer.Ordinal);
    private readonly int _maximumTransactions;
    private readonly GeneratedContentLimits _limits;

    public InMemoryGeneratedContentTransactionStore(
        int maximumTransactions = 4_096,
        GeneratedContentLimits? limits = null)
    {
        if (maximumTransactions is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTransactions));
        }

        _maximumTransactions = maximumTransactions;
        _limits = limits ?? new GeneratedContentLimits();
        _limits.Validate();
    }

    public ValueTask<GeneratedContentTransaction?> TryGetAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryGetValue(transactionId, out var transaction);
        return new ValueTask<GeneratedContentTransaction?>(
            transaction is null ? null : ContentValidation.Snapshot(transaction));
    }

    public ValueTask PutAsync(
        GeneratedContentTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ContentValidation.ValidateTransaction(transaction, _limits);
        while (true)
        {
            if (_items.TryGetValue(snapshot.TransactionId, out var current))
            {
                if (snapshot.Revision != checked(current.Revision + 1))
                {
                    throw new GenerationOperationException(
                        "content_transaction_revision_conflict",
                        "The content transaction update is stale.");
                }

                if (_items.TryUpdate(snapshot.TransactionId, snapshot, current))
                {
                    return default;
                }

                continue;
            }

            if (snapshot.Revision != 1)
            {
                throw new GenerationOperationException(
                    "content_transaction_revision_conflict",
                    "A new content transaction must start at revision one.");
            }

            if (_items.Count >= _maximumTransactions)
            {
                throw new GenerationOperationException(
                    "content_transaction_capacity_exceeded",
                    "The content transaction store is full.");
            }

            if (_items.TryAdd(snapshot.TransactionId, snapshot))
            {
                return default;
            }
        }
    }

    public ValueTask<IReadOnlyList<GeneratedContentTransaction>> ListUnfinishedAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        IReadOnlyList<GeneratedContentTransaction> result =
            new ReadOnlyCollection<GeneratedContentTransaction>(
                _items.Values
                    .Where(value => value.State is not ContentTransactionStates.Committed
                        and not ContentTransactionStates.Aborted
                        and not ContentTransactionStates.Rejected)
                    .OrderBy(value => value.CreatedAt)
                    .ThenBy(value => value.TransactionId, StringComparer.Ordinal)
                    .Take(maximumCount)
                    .Select(ContentValidation.Snapshot)
                    .ToArray());
        return new ValueTask<IReadOnlyList<GeneratedContentTransaction>>(result);
    }
}

public sealed class GeneratedContentCoordinator
{
    private readonly IGeneratedContentHost _host;
    private readonly IGeneratedContentTransactionStore _store;
    private readonly GeneratedContentLimits _limits;
    private readonly SemaphoreSlim[] _gates;

    public GeneratedContentCoordinator(
        IGeneratedContentHost host,
        IGeneratedContentTransactionStore store,
        GeneratedContentLimits? limits = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _limits = limits ?? new GeneratedContentLimits();
        _limits.Validate();
        _gates = Enumerable.Range(0, 257)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    public async ValueTask<GeneratedContentTransaction> StageValidateAndCommitAsync(
        string transactionId,
        GeneratedContentManifest manifest,
        CancellationToken cancellationToken = default)
    {
        GenerationValidation.Identifier(transactionId, nameof(transactionId), 128);
        var snapshot = ContentValidation.ValidateManifest(manifest, _limits);
        var gate = GateFor(transactionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _store
                .TryGetAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Manifest.Digest != snapshot.Digest)
                {
                    throw new GenerationOperationException(
                        "content_transaction_conflict",
                        "The transaction identity is already bound to another manifest.");
                }

                if (ContentValidation.IsTerminalState(existing.State))
                {
                    return existing;
                }

                if (existing.State != ContentTransactionStates.Prepared)
                {
                    existing = await ReconcileLockedAsync(existing, cancellationToken)
                        .ConfigureAwait(false);
                    if (existing.State is not ContentTransactionStates.Staged
                        and not ContentTransactionStates.Validated)
                    {
                        return existing;
                    }
                }
            }

            var now = DateTimeOffset.UtcNow;
            var transaction = existing ?? new GeneratedContentTransaction
            {
                TransactionId = transactionId,
                Manifest = snapshot,
                State = ContentTransactionStates.Prepared,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            };
            if (existing is null)
            {
                await _store.PutAsync(transaction, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (transaction.State == ContentTransactionStates.Prepared)
            {
                transaction = await BeforeHostCallAsync(
                        transaction,
                        ContentTransactionStates.Staging,
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await _host.StageAsync(transactionId, snapshot, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await MarkUnknownAsync(transaction, "content_stage_outcome_unknown")
                        .ConfigureAwait(false);
                    throw Uncertain("content_stage_outcome_unknown", exception);
                }

                transaction = await MarkAsync(
                        transaction,
                        ContentTransactionStates.Staged,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (transaction.State == ContentTransactionStates.Staged)
            {
                transaction = await BeforeHostCallAsync(
                        transaction,
                        ContentTransactionStates.Validating,
                        cancellationToken)
                    .ConfigureAwait(false);
                ContentValidationResult validation;
                try
                {
                    validation = await _host
                        .ValidateAsync(transactionId, snapshot, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await MarkUnknownAsync(transaction, "content_validation_outcome_unknown")
                        .ConfigureAwait(false);
                    throw Uncertain("content_validation_outcome_unknown", exception);
                }

                if (validation is null)
                {
                    await MarkUnknownAsync(transaction, "content_host_contract_invalid")
                        .ConfigureAwait(false);
                    throw new GenerationOperationException(
                        "content_host_contract_invalid",
                        "The content host returned no validation result.",
                        outcomeUncertain: true);
                }

                if (!validation.Accepted)
                {
                    var reason = string.IsNullOrWhiteSpace(validation.ReasonCode)
                        ? "content_rejected"
                        : GenerationValidation.Identifier(
                            validation.ReasonCode,
                            nameof(validation.ReasonCode),
                            256);
                    transaction = await AbortLockedAsync(
                            transaction,
                            snapshot,
                            reason,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return transaction;
                }

                transaction = await MarkAsync(
                        transaction,
                        ContentTransactionStates.Validated,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (transaction.State == ContentTransactionStates.Validated)
            {
                transaction = await BeforeHostCallAsync(
                        transaction,
                        ContentTransactionStates.Committing,
                        cancellationToken)
                    .ConfigureAwait(false);
                ContentCommitResult commit;
                try
                {
                    commit = await _host
                        .CommitAsync(transactionId, snapshot, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await MarkUnknownAsync(transaction, "content_commit_outcome_unknown")
                        .ConfigureAwait(false);
                    throw Uncertain("content_commit_outcome_unknown", exception);
                }

                if (commit is null || string.IsNullOrWhiteSpace(commit.HostReceiptId))
                {
                    await MarkUnknownAsync(transaction, "content_commit_receipt_missing")
                        .ConfigureAwait(false);
                    throw new GenerationOperationException(
                        "content_commit_receipt_missing",
                        "The host did not return a durable content commit receipt.",
                        outcomeUncertain: true);
                }

                var receiptId = GenerationValidation.Identifier(
                    commit.HostReceiptId,
                    nameof(commit.HostReceiptId),
                    256);
                transaction = Copy(
                    transaction,
                    ContentTransactionStates.Committed,
                    null,
                    receiptId,
                    commit.Result);
                await _store.PutAsync(transaction, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return ContentValidation.Snapshot(transaction);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GeneratedContentTransaction> ReconcileAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        GenerationValidation.Identifier(transactionId, nameof(transactionId), 128);
        var gate = GateFor(transactionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await _store
                                  .TryGetAsync(transactionId, cancellationToken)
                                  .ConfigureAwait(false)
                              ?? throw new KeyNotFoundException(
                                  $"Content transaction '{transactionId}' was not found.");
            return await ReconcileLockedAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<GeneratedContentTransaction> ReconcileLockedAsync(
        GeneratedContentTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (ContentValidation.IsTerminalState(transaction.State))
        {
            return ContentValidation.Snapshot(transaction);
        }

        var status = await _host
            .GetStatusAsync(transaction.TransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (status is null)
        {
            throw new GenerationOperationException(
                "content_host_contract_invalid",
                "The content host returned no transaction status.");
        }

        var state = status.State switch
        {
            ContentTransactionStates.Staged => ContentTransactionStates.Staged,
            ContentTransactionStates.Validated => ContentTransactionStates.Validated,
            ContentTransactionStates.Committed => ContentTransactionStates.Committed,
            ContentTransactionStates.Aborted => ContentTransactionStates.Aborted,
            ContentTransactionStates.Rejected => ContentTransactionStates.Rejected,
            _ => ContentTransactionStates.Unknown
        };
        if (state == ContentTransactionStates.Committed
            && string.IsNullOrWhiteSpace(status.HostReceiptId))
        {
            throw new GenerationOperationException(
                "content_host_contract_invalid",
                "The content host reported a commit without a durable receipt.",
                outcomeUncertain: true);
        }

        var receiptId = status.HostReceiptId is null
            ? null
            : GenerationValidation.Identifier(
                status.HostReceiptId,
                nameof(status.HostReceiptId),
                256);
        var updated = Copy(
            transaction,
            state,
            state == ContentTransactionStates.Unknown
                ? "content_host_status_unknown"
                : null,
            receiptId,
            status.Result);
        await _store.PutAsync(updated, cancellationToken).ConfigureAwait(false);
        return ContentValidation.Snapshot(updated);
    }

    private async ValueTask<GeneratedContentTransaction> AbortLockedAsync(
        GeneratedContentTransaction transaction,
        GeneratedContentManifest manifest,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        transaction = await BeforeHostCallAsync(
                transaction,
                ContentTransactionStates.Aborting,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _host.AbortAsync(
                    transaction.TransactionId,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await MarkUnknownAsync(transaction, "content_abort_outcome_unknown")
                .ConfigureAwait(false);
            throw Uncertain("content_abort_outcome_unknown", exception);
        }

        return await MarkAsync(
                transaction,
                ContentTransactionStates.Rejected,
                reasonCode,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<GeneratedContentTransaction> BeforeHostCallAsync(
        GeneratedContentTransaction transaction,
        string state,
        CancellationToken cancellationToken) =>
        MarkAsync(transaction, state, null, cancellationToken);

    private async ValueTask<GeneratedContentTransaction> MarkAsync(
        GeneratedContentTransaction transaction,
        string state,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        var updated = Copy(transaction, state, reasonCode, null, null);
        await _store.PutAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async ValueTask MarkUnknownAsync(
        GeneratedContentTransaction transaction,
        string reasonCode)
    {
        var updated = Copy(
            transaction,
            ContentTransactionStates.Unknown,
            reasonCode,
            null,
            null);
        await _store.PutAsync(updated, CancellationToken.None).ConfigureAwait(false);
    }

    private static GeneratedContentTransaction Copy(
        GeneratedContentTransaction source,
        string state,
        string? reasonCode,
        string? receiptId,
        JsonElement? result) =>
        new()
        {
            TransactionId = source.TransactionId,
            Manifest = source.Manifest,
            State = state,
            HostReceiptId = receiptId ?? source.HostReceiptId,
            HostResult = result?.Clone() ?? source.HostResult?.Clone(),
            ReasonCode = reasonCode,
            CreatedAt = source.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Revision = checked(source.Revision + 1)
        };

    private static GenerationOperationException Uncertain(
        string reasonCode,
        Exception exception) =>
        new(
            reasonCode,
            "The host outcome is unknown; reconcile this transaction instead of repeating it.",
            outcomeUncertain: true,
            exception);

    private SemaphoreSlim GateFor(string transactionId)
    {
        var hash = StringComparer.Ordinal.GetHashCode(transactionId) & int.MaxValue;
        return _gates[hash % _gates.Length];
    }
}

internal static class ContentValidation
{
    public static bool IsTerminalState(string state) =>
        state is ContentTransactionStates.Committed
            or ContentTransactionStates.Aborted
            or ContentTransactionStates.Rejected;

    public static bool IsKnownState(string state) =>
        state is ContentTransactionStates.Prepared
            or ContentTransactionStates.Staging
            or ContentTransactionStates.Staged
            or ContentTransactionStates.Validating
            or ContentTransactionStates.Validated
            or ContentTransactionStates.Committing
            or ContentTransactionStates.Committed
            or ContentTransactionStates.Aborting
            or ContentTransactionStates.Aborted
            or ContentTransactionStates.Rejected
            or ContentTransactionStates.Unknown;

    public static GeneratedContentTransaction ValidateTransaction(
        GeneratedContentTransaction value,
        GeneratedContentLimits limits)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var transactionId = GenerationValidation.Identifier(
            value.TransactionId,
            nameof(value.TransactionId),
            128);
        var manifest = ValidateManifest(value.Manifest, limits);
        if (!string.IsNullOrEmpty(value.Manifest.Digest)
            && !string.Equals(
                value.Manifest.Digest,
                manifest.Digest,
                StringComparison.Ordinal))
        {
            throw new GenerationOperationException(
                "content_manifest_digest_mismatch",
                "The generated content manifest digest is invalid.");
        }

        if (!IsKnownState(value.State)
            || value.Revision < 1
            || value.CreatedAt == default
            || value.UpdatedAt < value.CreatedAt
            || value.HostReceiptId is { Length: > 256 }
            || value.ReasonCode is { Length: > 256 }
            || value.HostResult is { ValueKind: JsonValueKind.Undefined }
            || value.HostResult.HasValue
            && Encoding.UTF8.GetByteCount(value.HostResult.Value.GetRawText())
            > limits.MaxManifestUtf8Bytes)
        {
            throw new GenerationOperationException(
                "content_transaction_invalid",
                "The generated content transaction contains invalid durable state.");
        }

        if (value.State == ContentTransactionStates.Committed
            && string.IsNullOrWhiteSpace(value.HostReceiptId))
        {
            throw new GenerationOperationException(
                "content_transaction_receipt_missing",
                "A committed content transaction requires a durable host receipt.");
        }

        if (value.HostReceiptId is not null)
        {
            GenerationValidation.Identifier(
                value.HostReceiptId,
                nameof(value.HostReceiptId),
                256);
        }

        if (value.ReasonCode is not null)
        {
            GenerationValidation.Identifier(
                value.ReasonCode,
                nameof(value.ReasonCode),
                256);
        }

        return new GeneratedContentTransaction
        {
            TransactionId = transactionId,
            Manifest = manifest,
            State = value.State,
            HostReceiptId = value.HostReceiptId,
            HostResult = value.HostResult?.Clone(),
            ReasonCode = value.ReasonCode,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt,
            Revision = value.Revision
        };
    }

    public static GeneratedContentManifest ValidateManifest(
        GeneratedContentManifest manifest,
        GeneratedContentLimits limits)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        if (manifest.Artifacts is null
            || manifest.Scripts is null
            || manifest.Dependencies is null
            || manifest.Provenance is null)
        {
            throw new GenerationOperationException(
                "content_manifest_collection_missing",
                "Generated content manifest collections cannot be null.");
        }

        if (manifest.Data.ValueKind == JsonValueKind.Undefined
            || manifest.Artifacts.Count > limits.MaxArtifacts
            || manifest.Scripts.Count > limits.MaxScripts
            || manifest.Dependencies.Count > limits.MaxDependencies
            || manifest.Provenance.Count > limits.MaxProvenanceEntries)
        {
            throw new GenerationOperationException(
                "content_manifest_limit_exceeded",
                "Generated content manifest exceeds configured collection limits.");
        }

        var snapshot = new GeneratedContentManifest
        {
            ContentId = GenerationValidation.Identifier(
                manifest.ContentId,
                nameof(manifest.ContentId),
                128),
            Kind = GenerationValidation.Identifier(
                manifest.Kind,
                nameof(manifest.Kind),
                128),
            Version = GenerationValidation.Identifier(
                manifest.Version,
                nameof(manifest.Version),
                64),
            SourceOperationId = GenerationValidation.Identifier(
                manifest.SourceOperationId,
                nameof(manifest.SourceOperationId),
                128),
            Data = manifest.Data.Clone(),
            Artifacts = new ReadOnlyCollection<GenerationArtifact>(
                manifest.Artifacts.Select(SnapshotArtifact).ToArray()),
            Scripts = new ReadOnlyCollection<GeneratedScriptAsset>(
                manifest.Scripts.Select(script => SnapshotScript(script, limits)).ToArray()),
            Dependencies = new ReadOnlyCollection<string>(
                manifest.Dependencies
                    .Select(value => GenerationValidation.Identifier(
                        value,
                        nameof(manifest.Dependencies),
                        256))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()),
            Provenance = manifest.Provenance
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => GenerationValidation.Identifier(
                        pair.Key,
                        nameof(manifest.Provenance),
                        128),
                    pair => pair.Value is not null && pair.Value.Length <= 2_048
                        ? pair.Value
                        : throw new GenerationOperationException(
                            "content_manifest_provenance_invalid",
                            "Generated content provenance value is too large."),
                    StringComparer.Ordinal)
        };
        EnsureUnique(
            snapshot.Artifacts.Select(value => value.ArtifactId),
            "content_manifest_artifact_duplicate");
        EnsureUnique(
            snapshot.Scripts.Select(value => value.ScriptId),
            "content_manifest_script_duplicate");
        EnsureUnique(
            snapshot.Dependencies,
            "content_manifest_dependency_duplicate");
        snapshot.Digest = ComputeDigest(snapshot, limits.MaxManifestUtf8Bytes);
        return snapshot;
    }

    public static GeneratedContentTransaction Snapshot(
        GeneratedContentTransaction value) =>
        new()
        {
            TransactionId = value.TransactionId,
            Manifest = SnapshotManifest(value.Manifest),
            State = value.State,
            HostReceiptId = value.HostReceiptId,
            HostResult = value.HostResult?.Clone(),
            ReasonCode = value.ReasonCode,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt,
            Revision = value.Revision
        };

    private static GeneratedContentManifest SnapshotManifest(
        GeneratedContentManifest value) =>
        new()
        {
            ContentId = value.ContentId,
            Kind = value.Kind,
            Version = value.Version,
            SourceOperationId = value.SourceOperationId,
            Data = value.Data.Clone(),
            Artifacts = new ReadOnlyCollection<GenerationArtifact>(
                value.Artifacts.Select(SnapshotArtifact).ToArray()),
            Scripts = new ReadOnlyCollection<GeneratedScriptAsset>(
                value.Scripts.Select(script => new GeneratedScriptAsset
                {
                    ScriptId = script.ScriptId,
                    Language = script.Language,
                    SourceText = script.SourceText,
                    EntryPoint = script.EntryPoint
                }).ToArray()),
            Dependencies = new ReadOnlyCollection<string>(
                value.Dependencies.ToArray()),
            Provenance = new Dictionary<string, string>(
                value.Provenance,
                StringComparer.Ordinal),
            Digest = value.Digest
        };

    private static GeneratedScriptAsset SnapshotScript(
        GeneratedScriptAsset script,
        GeneratedContentLimits limits)
    {
        if (script is null)
        {
            throw new GenerationOperationException(
                "content_manifest_script_invalid",
                "Generated content scripts cannot contain null entries.");
        }

        if (string.IsNullOrEmpty(script.SourceText)
            || Encoding.UTF8.GetByteCount(script.SourceText) > limits.MaxScriptUtf8Bytes)
        {
            throw new GenerationOperationException(
                "content_manifest_script_too_large",
                "A generated script exceeds the configured byte limit.");
        }

        return new GeneratedScriptAsset
        {
            ScriptId = GenerationValidation.Identifier(
                script.ScriptId,
                nameof(script.ScriptId),
                128),
            Language = GenerationValidation.Identifier(
                script.Language,
                nameof(script.Language),
                64),
            SourceText = script.SourceText,
            EntryPoint = script.EntryPoint is null
                ? null
                : GenerationValidation.Identifier(
                    script.EntryPoint,
                    nameof(script.EntryPoint),
                    256)
        };
    }

    private static GenerationArtifact SnapshotArtifact(GenerationArtifact artifact)
    {
        if (artifact is null
            || artifact.SizeBytes < 1
            || artifact.MediaType is null
            || artifact.MediaType.Length is < 1 or > 255
            || artifact.MediaType.Any(char.IsControl)
            || artifact.Sha256 is null
            || artifact.Sha256.Length != 64
            || artifact.Sha256.Any(character => !Uri.IsHexDigit(character))
            || artifact.Uri is null
            || artifact.Uri.Length > 4_096
            || !Uri.TryCreate(artifact.Uri, UriKind.Absolute, out _)
            || artifact.FileName is { Length: > 255 })
        {
            throw new GenerationOperationException(
                "content_manifest_artifact_invalid",
                "Generated content contains invalid artifact metadata.");
        }

        return new GenerationArtifact
        {
            ArtifactId = GenerationValidation.Identifier(
                artifact.ArtifactId,
                nameof(artifact.ArtifactId),
                256),
            Uri = artifact.Uri,
            MediaType = artifact.MediaType,
            Sha256 = artifact.Sha256.ToLowerInvariant(),
            SizeBytes = artifact.SizeBytes,
            FileName = artifact.FileName,
            SourceExpiresAt = artifact.SourceExpiresAt
        };
    }

    private static void EnsureUnique(
        IEnumerable<string> values,
        string reasonCode)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (values.Any(value => !seen.Add(value)))
        {
            throw new GenerationOperationException(
                reasonCode,
                "Generated content identifiers must be unique.");
        }
    }

    private static string ComputeDigest(
        GeneratedContentManifest manifest,
        int maximumBytes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contentId", manifest.ContentId);
            writer.WriteString("kind", manifest.Kind);
            writer.WriteString("version", manifest.Version);
            writer.WriteString("sourceOperationId", manifest.SourceOperationId);
            writer.WritePropertyName("data");
            WriteCanonical(writer, manifest.Data);
            writer.WriteStartArray("artifacts");
            foreach (var artifact in manifest.Artifacts.OrderBy(
                         value => value.ArtifactId,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", artifact.ArtifactId);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteString("type", artifact.MediaType);
                writer.WriteNumber("size", artifact.SizeBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("scripts");
            foreach (var script in manifest.Scripts.OrderBy(
                         value => value.ScriptId,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", script.ScriptId);
                writer.WriteString("language", script.Language);
                writer.WriteString("source", script.SourceText);
                writer.WriteString("entryPoint", script.EntryPoint);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("dependencies");
            foreach (var dependency in manifest.Dependencies)
            {
                writer.WriteStringValue(dependency);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("provenance");
            foreach (var pair in manifest.Provenance.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > maximumBytes)
        {
            throw new GenerationOperationException(
                "content_manifest_too_large",
                $"Generated content manifest exceeds {maximumBytes} bytes.");
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(buffer.WrittenSpan.ToArray());
        return Hex(hash);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string Hex(byte[] bytes)
    {
        var characters = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = alphabet[bytes[index] >> 4];
            characters[index * 2 + 1] = alphabet[bytes[index] & 15];
        }

        return new string(characters);
    }
}
