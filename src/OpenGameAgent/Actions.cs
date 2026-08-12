using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public enum GameActionStatus
{
    Committed,
    Rejected,
    Failed,
    Uncertain,
}

public sealed class GameActionIntent
{
    public GameActionIntent(
        string operationId,
        string inputId,
        string sessionId,
        string actorId,
        string action,
        string argumentsJson,
        GameMoment moment,
        long? expectedRevision = null)
        : this(
            operationId,
            inputId,
            sessionId,
            actorId,
            action,
            argumentsJson,
            moment,
            expectedRevision,
            generationId: null)
    {
    }

    public GameActionIntent(
        string operationId,
        string inputId,
        string sessionId,
        string actorId,
        string action,
        string argumentsJson,
        GameMoment moment,
        long? expectedRevision,
        string? generationId)
    {
        OperationId = GameJson.RequireId(operationId, nameof(operationId));
        InputId = GameJson.RequireId(inputId, nameof(inputId));
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        ActorId = GameJson.RequireId(actorId, nameof(actorId));
        Action = GameJson.RequireId(action, nameof(action));
        ArgumentsJson = GameJson.RequireValid(argumentsJson, nameof(argumentsJson));
        Moment = moment.EnsureValid(nameof(moment));
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ExpectedRevision = expectedRevision;
        GenerationId = generationId is null
            ? null
            : GameJson.RequireId(generationId, nameof(generationId));
    }

    public string OperationId { get; }

    public string InputId { get; }

    public string SessionId { get; }

    public string ActorId { get; }

    public string Action { get; }

    public string ArgumentsJson { get; }

    public GameMoment Moment { get; }

    public long? ExpectedRevision { get; }

    /// <summary>
    /// Identifies the authoritative save/world generation in which this intent is valid.
    /// Hosts should change it whenever loading or replacing a world snapshot could make an old
    /// external receipt unsafe to apply.
    /// </summary>
    public string? GenerationId { get; }
}

public sealed class GameActionReceipt
{
    private const int MaximumCodeCharacters = 1_024;
    private const int MaximumMessageCharacters = 64_000;

    public GameActionReceipt(
        string operationId,
        GameActionStatus status,
        string resultJson,
        GameMoment moment,
        long? stateRevision = null,
        string? code = null,
        string? message = null)
    {
        if (!Enum.IsDefined(typeof(GameActionStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        OperationId = GameJson.RequireId(operationId, nameof(operationId));
        Status = status;
        ResultJson = GameJson.RequireValid(resultJson, nameof(resultJson));
        Moment = moment.EnsureValid(nameof(moment));
        if (stateRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateRevision));
        }

        StateRevision = stateRevision;
        if ((code?.Length ?? 0) > MaximumCodeCharacters)
        {
            throw new ArgumentException("An action receipt code is too large.", nameof(code));
        }

        if ((message?.Length ?? 0) > MaximumMessageCharacters)
        {
            throw new ArgumentException("An action receipt message is too large.", nameof(message));
        }

        Code = code;
        Message = message;
    }

    public string OperationId { get; }

    public GameActionStatus Status { get; }

    public string ResultJson { get; }

    public GameMoment Moment { get; }

    public long? StateRevision { get; }

    public string? Code { get; }

    public string? Message { get; }

    public bool IsFinal => Status != GameActionStatus.Uncertain;

    public bool Succeeded => Status == GameActionStatus.Committed;

    public static GameActionReceipt Committed(
        GameActionIntent intent,
        string resultJson,
        long? stateRevision = null) =>
        new(intent.OperationId, GameActionStatus.Committed, resultJson, intent.Moment, stateRevision);

    public static GameActionReceipt Rejected(
        GameActionIntent intent,
        string code,
        string message,
        string resultJson = "{}") =>
        new(intent.OperationId, GameActionStatus.Rejected, resultJson, intent.Moment, null, code, message);

    public static GameActionReceipt Uncertain(GameActionIntent intent, string message) =>
        new(
            intent.OperationId,
            GameActionStatus.Uncertain,
            "{}",
            intent.Moment,
            null,
            "outcome_uncertain",
            TruncateDiagnostic(message));

    private static string? TruncateDiagnostic(string? message) =>
        message is null || message.Length <= MaximumMessageCharacters
            ? message
            : message.Substring(0, MaximumMessageCharacters);
}

public sealed class GameActionJournalEntry
{
    public GameActionJournalEntry(
        GameActionIntent intent,
        GameActionReceipt? receipt,
        bool created,
        bool dispatched)
    {
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        Receipt = receipt;
        Created = created;
        Dispatched = dispatched;
    }

    public GameActionIntent Intent { get; }

    public GameActionReceipt? Receipt { get; }

    public bool Created { get; }

    public bool Dispatched { get; }
}

public interface IGameActionJournal
{
    ValueTask<GameActionJournalEntry> ReserveAsync(GameActionIntent intent, CancellationToken cancellationToken);

    ValueTask<GameActionJournalEntry?> FindAsync(string operationId, CancellationToken cancellationToken);

    ValueTask<bool> MarkDispatchedAsync(string operationId, CancellationToken cancellationToken);

    ValueTask SaveReceiptAsync(GameActionReceipt receipt, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameActionIntent>> ListPendingAsync(int limit, CancellationToken cancellationToken);
}

public interface IGameActionHandler
{
    ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken);

    ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken);
}

public delegate string GameActionOperationIdFactory(
    GameInput input,
    JsonElement arguments,
    ToolExecutionContext execution);

/// <summary>
/// Creates stable, bounded operation identifiers for authoritative game actions.
/// </summary>
public static class GameActionOperationIds
{
    public const string Version2Prefix = "oga-action-v2:";

    public static string CreateV2(
        GameInput input,
        string action,
        ToolExecutionContext execution,
        string? generationId = null)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (execution is null)
        {
            throw new ArgumentNullException(nameof(execution));
        }
        return CreateV2(
            input.SessionId,
            input.ActorId,
            input.InputId,
            execution.Turn,
            execution.ToolCallIndex,
            action,
            input.Moment,
            generationId);
    }

    public static string CreateV2(
        string sessionId,
        string actorId,
        string inputId,
        int turn,
        int toolCallIndex,
        string action,
        GameMoment moment,
        string? generationId = null)
    {
        if (turn < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turn));
        }

        if (toolCallIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toolCallIndex));
        }

        moment = moment.EnsureValid(nameof(moment));
        using var canonical = new MemoryStream();
        using (var writer = new BinaryWriter(canonical, Encoding.UTF8, leaveOpen: true))
        {
            WriteComponent(writer, "OpenGameAgent.GameActionOperationId.v2", "version");
            WriteComponent(writer, sessionId, nameof(sessionId));
            WriteComponent(writer, actorId, nameof(actorId));
            WriteComponent(writer, inputId, nameof(inputId));
            writer.Write(turn);
            writer.Write(toolCallIndex);
            WriteComponent(writer, action, nameof(action));
            WriteComponent(writer, moment.TimelineId, nameof(moment));
            writer.Write(moment.Tick);
            WriteNullableComponent(writer, generationId, nameof(generationId));
        }

        canonical.Position = 0;
        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(canonical);
        return Version2Prefix + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>
    /// Reproduces the pre-v2 default ID for a controlled migration window. Do not use it for new
    /// save namespaces because it does not isolate sessions, actors, timelines, or actions.
    /// </summary>
    public static string CreateLegacyV1(
        GameInput input,
        JsonElement arguments,
        ToolExecutionContext execution)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (execution is null)
        {
            throw new ArgumentNullException(nameof(execution));
        }
        _ = arguments;
        return CreateLegacyV1(input.InputId, execution.Turn, execution.ToolCallIndex);
    }

    public static string CreateLegacyV1(string inputId, int turn, int toolCallIndex)
    {
        if (turn < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turn));
        }

        if (toolCallIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toolCallIndex));
        }

        return GameJson.JoinIds(
            RequireComponent(inputId, nameof(inputId)),
            turn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            toolCallIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static bool IsVersion2(string operationId) =>
        operationId?.StartsWith(Version2Prefix, StringComparison.Ordinal) == true
        && operationId.Length == Version2Prefix.Length + 64
        && operationId.Skip(Version2Prefix.Length).All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteComponent(BinaryWriter writer, string value, string parameterName)
    {
        var bytes = Encoding.UTF8.GetBytes(RequireComponent(value, parameterName));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableComponent(BinaryWriter writer, string? value, string parameterName)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteComponent(writer, value, parameterName);
        }
    }

    private static string RequireComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16_384)
        {
            throw new ArgumentException(
                "An operation identity component must contain between 1 and 16384 characters.",
                parameterName);
        }

        return value;
    }
}

public sealed class InMemoryGameActionJournal : IGameActionJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MutableEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public InMemoryGameActionJournal(int capacity = 10_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask<GameActionJournalEntry> ReserveAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(intent.OperationId, out var existing))
            {
                EnsureSameIntent(existing.Intent, intent);
                return new ValueTask<GameActionJournalEntry>(
                    new GameActionJournalEntry(
                        existing.Intent,
                        existing.Receipt,
                        created: false,
                        dispatched: existing.Dispatched));
            }

            if (_entries.Count >= _capacity)
            {
                throw new GameRuntimeLimitException(nameof(_capacity), "The action journal reached its capacity.");
            }

            _entries.Add(intent.OperationId, new MutableEntry(intent));
            return new ValueTask<GameActionJournalEntry>(
                new GameActionJournalEntry(intent, null, created: true, dispatched: false));
        }
    }

    public ValueTask<GameActionJournalEntry?> FindAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameJson.RequireId(operationId, nameof(operationId));

        lock (_gate)
        {
            return new ValueTask<GameActionJournalEntry?>(
                _entries.TryGetValue(operationId, out var entry)
                    ? new GameActionJournalEntry(
                        entry.Intent,
                        entry.Receipt,
                        created: false,
                        dispatched: entry.Dispatched)
                    : null);
        }
    }

    public ValueTask<bool> MarkDispatchedAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameJson.RequireId(operationId, nameof(operationId));

        lock (_gate)
        {
            if (!_entries.TryGetValue(operationId, out var entry))
            {
                throw new InvalidOperationException("Cannot dispatch an action without a matching intent.");
            }

            if (entry.Receipt is not null || entry.Dispatched)
            {
                return new ValueTask<bool>(false);
            }

            entry.Dispatched = true;
            return new ValueTask<bool>(true);
        }
    }

    public ValueTask SaveReceiptAsync(GameActionReceipt receipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        if (!receipt.IsFinal)
        {
            throw new ArgumentException("An uncertain receipt cannot close a journal entry.", nameof(receipt));
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(receipt.OperationId, out var entry))
            {
                throw new InvalidOperationException("Cannot save a receipt without a matching action intent.");
            }

            if (!entry.Dispatched)
            {
                throw new InvalidOperationException("Cannot save a receipt before the action is marked as dispatched.");
            }

            EnsureReceiptMatchesIntent(entry.Intent, receipt);

            if (entry.Receipt is not null && !ReceiptEquals(entry.Receipt, receipt))
            {
                throw new InvalidOperationException("A final action receipt is immutable.");
            }

            entry.Receipt = receipt;
        }

        return default;
    }

    public ValueTask<IReadOnlyList<GameActionIntent>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit < 0 || limit > _capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (_gate)
        {
            return new ValueTask<IReadOnlyList<GameActionIntent>>(
                _entries.Values
                    .Where(entry => entry.Receipt is null)
                    .Select(entry => entry.Intent)
                    .OrderBy(intent => intent.OperationId, StringComparer.Ordinal)
                    .Take(limit)
                    .ToArray());
        }
    }

    private static void EnsureSameIntent(GameActionIntent expected, GameActionIntent actual)
    {
        if (!string.Equals(expected.OperationId, actual.OperationId, StringComparison.Ordinal)
            || !string.Equals(expected.InputId, actual.InputId, StringComparison.Ordinal)
            || !string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
            || !string.Equals(expected.ActorId, actual.ActorId, StringComparison.Ordinal)
            || !string.Equals(expected.Action, actual.Action, StringComparison.Ordinal)
            || !string.Equals(expected.ArgumentsJson, actual.ArgumentsJson, StringComparison.Ordinal)
            || expected.Moment != actual.Moment
            || expected.ExpectedRevision != actual.ExpectedRevision
            || !string.Equals(expected.GenerationId, actual.GenerationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The operation ID is already reserved for a different action intent.");
        }
    }

    private static void EnsureReceiptMatchesIntent(GameActionIntent intent, GameActionReceipt receipt)
    {
        if (!string.Equals(intent.OperationId, receipt.OperationId, StringComparison.Ordinal)
            || intent.Moment != receipt.Moment)
        {
            throw new InvalidOperationException("The action receipt does not match its reserved intent.");
        }
    }

    private static bool ReceiptEquals(GameActionReceipt left, GameActionReceipt right) =>
        left.Status == right.Status
        && left.Moment == right.Moment
        && left.StateRevision == right.StateRevision
        && string.Equals(left.ResultJson, right.ResultJson, StringComparison.Ordinal)
        && string.Equals(left.Code, right.Code, StringComparison.Ordinal)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

    private sealed class MutableEntry
    {
        public MutableEntry(GameActionIntent intent)
        {
            Intent = intent;
        }

        public GameActionIntent Intent { get; }

        public GameActionReceipt? Receipt { get; set; }

        public bool Dispatched { get; set; }
    }
}

public sealed class DurableGameActionDispatcher
{
    private readonly IGameActionJournal _journal;
    private readonly IGameActionHandler _handler;
    private readonly SemaphoreSlim[] _operationGates;
    private readonly int _receiptCommitTimeoutMilliseconds;

    public DurableGameActionDispatcher(
        IGameActionJournal journal,
        IGameActionHandler handler,
        int concurrencyStripes = 64,
        int receiptCommitTimeoutMilliseconds = 10_000)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (concurrencyStripes <= 0 || concurrencyStripes > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrencyStripes));
        }

        if (receiptCommitTimeoutMilliseconds < 100 || receiptCommitTimeoutMilliseconds > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(receiptCommitTimeoutMilliseconds));
        }

        _operationGates = Enumerable.Range(0, concurrencyStripes)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
        _receiptCommitTimeoutMilliseconds = receiptCommitTimeoutMilliseconds;
    }

    public async ValueTask<GameActionReceipt> ExecuteAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        var gate = SelectGate(intent.OperationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reservation = await _journal.ReserveAsync(intent, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The action journal returned null while reserving an intent.");
            ValidateEntry(reservation, intent);
            if (reservation.Receipt is not null)
            {
                return reservation.Receipt;
            }

            if (reservation.Dispatched)
            {
                return await TryRecoverAsync(reservation.Intent, cancellationToken).ConfigureAwait(false);
            }

            var claimed = await _journal.MarkDispatchedAsync(
                reservation.Intent.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                var current = await _journal.FindAsync(
                    reservation.Intent.OperationId,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The reserved action disappeared from its journal.");
                ValidateEntry(current, intent);
                if (!current.Dispatched && current.Receipt is null)
                {
                    throw new InvalidOperationException("The action journal rejected dispatch without recording another dispatcher or receipt.");
                }

                return current.Receipt ?? await TryRecoverAsync(current.Intent, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var receipt = await _handler.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false);
                return await CloseDurablyAsync(intent, receipt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return GameActionReceipt.Uncertain(
                    intent,
                    "The game action handler failed after dispatch; the game must reconcile the operation. " + exception.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameActionReceipt> ReconcileAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        var gate = SelectGate(GameJson.RequireId(operationId, nameof(operationId)));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await _journal.FindAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The action operation does not exist.");
            ValidateEntry(entry, expectedIntent: null);
            if (entry.Receipt is not null)
            {
                return entry.Receipt;
            }

            if (!entry.Dispatched)
            {
                var claimed = await _journal.MarkDispatchedAsync(
                    entry.Intent.OperationId,
                    cancellationToken).ConfigureAwait(false);
                if (claimed)
                {
                    try
                    {
                        var receipt = await _handler.ExecuteAsync(entry.Intent, cancellationToken).ConfigureAwait(false);
                        return await CloseDurablyAsync(entry.Intent, receipt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        return GameActionReceipt.Uncertain(
                            entry.Intent,
                            "The game action handler failed after dispatch; the game must reconcile the operation. " + exception.Message);
                    }
                }

                var current = await _journal.FindAsync(operationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The reserved action disappeared from its journal.");
                ValidateEntry(current, entry.Intent);
                if (current.Receipt is not null)
                {
                    return current.Receipt;
                }

                if (!current.Dispatched)
                {
                    throw new InvalidOperationException("The action journal rejected dispatch without recording another dispatcher or receipt.");
                }
            }

            return await TryRecoverAsync(entry.Intent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<GameActionReceipt> CloseAsync(
        GameActionIntent intent,
        GameActionReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt is null)
        {
            throw new InvalidOperationException("The game action handler returned a null receipt.");
        }

        if (!string.Equals(intent.OperationId, receipt.OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The action receipt operation ID does not match its intent.");
        }


        if (intent.Moment != receipt.Moment)
        {
            throw new InvalidOperationException("The action receipt game moment does not match its intent.");
        }

        if (!receipt.IsFinal)
        {
            return receipt;
        }

        await _journal.SaveReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        var stored = await _journal.FindAsync(intent.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The action journal lost a receipt after saving it.");
        ValidateEntry(stored, intent);
        if (stored.Receipt is null || !ReceiptsEqual(stored.Receipt, receipt))
        {
            throw new InvalidOperationException("The action journal did not retain the saved receipt.");
        }

        return receipt;
    }

    private async ValueTask<GameActionReceipt> CloseDurablyAsync(
        GameActionIntent intent,
        GameActionReceipt receipt)
    {
        using var settlementCancellation = new CancellationTokenSource(_receiptCommitTimeoutMilliseconds);
        try
        {
            return await CloseAsync(intent, receipt, settlementCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (settlementCancellation.IsCancellationRequested)
        {
            return GameActionReceipt.Uncertain(
                intent,
                "The game action returned a final receipt, but its durable journal commit timed out. Reconcile the operation before retrying.");
        }
        catch (Exception exception)
        {
            return GameActionReceipt.Uncertain(
                intent,
                "The game action returned a receipt, but its durable journal commit failed. Reconcile the operation before retrying. "
                + exception.Message);
        }
    }

    private async ValueTask<GameActionReceipt> RecoverAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        var recovered = await _handler.RecoverAsync(intent, cancellationToken).ConfigureAwait(false);
        return recovered is null
            ? GameActionReceipt.Uncertain(
                intent,
                "The action was dispatched, but its outcome is not yet known.")
            : await CloseDurablyAsync(intent, recovered).ConfigureAwait(false);
    }

    private async ValueTask<GameActionReceipt> TryRecoverAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RecoverAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return GameActionReceipt.Uncertain(
                intent,
                "The game action recovery failed; the game must reconcile the operation. " + exception.Message);
        }
    }

    private SemaphoreSlim SelectGate(string operationId)
    {
        var hash = StringComparer.Ordinal.GetHashCode(operationId) & int.MaxValue;
        return _operationGates[hash % _operationGates.Length];
    }

    private static void ValidateEntry(GameActionJournalEntry entry, GameActionIntent? expectedIntent)
    {
        if (entry.Intent is null)
        {
            throw new InvalidOperationException("The action journal returned an entry without an intent.");
        }

        if (expectedIntent is not null)
        {
            EnsureIntentsEqual(entry.Intent, expectedIntent);
        }

        if (entry.Created && (entry.Dispatched || entry.Receipt is not null))
        {
            throw new InvalidOperationException("A newly created journal entry cannot already be dispatched or completed.");
        }

        if (entry.Receipt is not null
            && (!entry.Dispatched
                || !entry.Receipt.IsFinal
                || !string.Equals(entry.Intent.OperationId, entry.Receipt.OperationId, StringComparison.Ordinal)
                || entry.Intent.Moment != entry.Receipt.Moment))
        {
            throw new InvalidOperationException("The action journal returned an inconsistent receipt.");
        }
    }

    private static void EnsureIntentsEqual(GameActionIntent left, GameActionIntent right)
    {
        if (!string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            || !string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
            || !string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            || !string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal)
            || !string.Equals(left.Action, right.Action, StringComparison.Ordinal)
            || !string.Equals(left.ArgumentsJson, right.ArgumentsJson, StringComparison.Ordinal)
            || left.Moment != right.Moment
            || left.ExpectedRevision != right.ExpectedRevision
            || !string.Equals(left.GenerationId, right.GenerationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The action journal returned a different reserved intent.");
        }
    }

    private static bool ReceiptsEqual(GameActionReceipt left, GameActionReceipt right) =>
        left.Status == right.Status
        && left.Moment == right.Moment
        && left.StateRevision == right.StateRevision
        && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
        && string.Equals(left.ResultJson, right.ResultJson, StringComparison.Ordinal)
        && string.Equals(left.Code, right.Code, StringComparison.Ordinal)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal);
}

public static class GameActionTool
{
    public static AgentTool Create(
        GameInput input,
        string action,
        string description,
        string inputSchemaJson,
        DurableGameActionDispatcher dispatcher,
        ToolRisk risk = ToolRisk.NonIdempotentWrite,
        Func<JsonElement, string?>? conflictKey = null,
        long? expectedRevision = null,
        GameActionOperationIdFactory? operationIdFactory = null)
        => Create(
            input,
            action,
            description,
            inputSchemaJson,
            dispatcher,
            risk,
            conflictKey,
            expectedRevision,
            operationIdFactory,
            generationId: null);

    public static AgentTool Create(
        GameInput input,
        string action,
        string description,
        string inputSchemaJson,
        DurableGameActionDispatcher dispatcher,
        ToolRisk risk,
        Func<JsonElement, string?>? conflictKey,
        long? expectedRevision,
        GameActionOperationIdFactory? operationIdFactory,
        string? generationId)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        var definition = new ToolDefinition(action, description, inputSchemaJson);
        return new AgentTool(
            definition,
            async (arguments, execution, cancellationToken) =>
            {
                var operationId = operationIdFactory is null
                    ? GameActionOperationIds.CreateV2(
                        input,
                        action,
                        execution,
                        generationId)
                    : GameJson.RequireId(operationIdFactory(input, arguments, execution), nameof(operationIdFactory));
                var intent = new GameActionIntent(
                    operationId: operationId,
                    inputId: input.InputId,
                    sessionId: input.SessionId,
                    actorId: input.ActorId,
                    action: action,
                    argumentsJson: arguments.GetRawText(),
                    moment: input.Moment,
                    expectedRevision: expectedRevision,
                    generationId: generationId);
                var receipt = await dispatcher.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(new ReceiptPayload(receipt), ReceiptJsonOptions);
                return new ToolResult(
                    new AgentContent[] { new JsonContent(json) },
                    isError: !receipt.Succeeded,
                    detailsJson: json,
                    outcomeUncertain: receipt.Status == GameActionStatus.Uncertain);
            },
            risk,
            conflictKey: conflictKey);
    }

    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ReceiptPayload
    {
        public ReceiptPayload(GameActionReceipt receipt)
        {
            OperationId = receipt.OperationId;
            Status = receipt.Status.ToString().ToLowerInvariant();
            Result = GameJson.ParseElement(receipt.ResultJson);
            StateRevision = receipt.StateRevision;
            Code = receipt.Code;
            Message = receipt.Message;
            TimelineId = receipt.Moment.TimelineId;
            Tick = receipt.Moment.Tick;
        }

        public string OperationId { get; }

        public string Status { get; }

        public JsonElement Result { get; }

        public long? StateRevision { get; }

        public string? Code { get; }

        public string? Message { get; }

        public string TimelineId { get; }

        public long Tick { get; }
    }
}
