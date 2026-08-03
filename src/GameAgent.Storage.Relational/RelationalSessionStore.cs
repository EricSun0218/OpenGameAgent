using System.Data;
using System.Data.Common;
using System.Text;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Storage.Relational;

public sealed class RelationalSessionStore :
    IDurableSessionStore,
    IOperationLedger
{
    private const string GlobalStreamId = "$global";
    private readonly IRelationalJournalConnectionFactory _factory;
    private readonly RelationalSessionStoreOptions _options;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private int _initialized;
    private int _disposed;

    public RelationalSessionStore(
        IRelationalJournalConnectionFactory factory,
        RelationalSessionStoreOptions? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = (options ?? new RelationalSessionStoreOptions()).Snapshot();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) != 0)
        {
            return;
        }
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) != 0)
            {
                return;
            }
            await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var statement in SchemaStatements())
            {
                await ExecuteAsync(connection, null, statement, cancellationToken).ConfigureAwait(false);
            }
            Volatile.Write(ref _initialized, 1);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            throw new RelationalJournalSchemaException("The relational journal schema could not be initialized.", exception);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async ValueTask AppendAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        _ = await AppendAtomicAsync(runtimeEvent, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JournalAppendResult> AppendAtomicAsync(
        RuntimeEvent runtimeEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        var result = await AppendAtomicBatchAsync(
            new[] { runtimeEvent }, expectedRunRevision, cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    public async ValueTask<IReadOnlyList<JournalAppendResult>> AppendAtomicBatchAsync(
        IReadOnlyList<RuntimeEvent> runtimeEvents,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(runtimeEvents);
        if (runtimeEvents.Count is < 1 || runtimeEvents.Count > _options.MaxBatchEvents)
        {
            throw new ArgumentException("The atomic journal batch is empty or exceeds its event limit.", nameof(runtimeEvents));
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var snapshots = new RuntimeEvent[runtimeEvents.Count];
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        string? streamId = null;
        for (var index = 0; index < snapshots.Length; index++)
        {
            var source = runtimeEvents[index] ?? throw new ArgumentException("A journal batch cannot contain null events.", nameof(runtimeEvents));
            ProtocolValidator.EnsureValid(source);
            var json = ProtocolJson.Serialize(source);
            if (Encoding.UTF8.GetByteCount(json) > _options.MaxEventBytes)
            {
                throw new ArgumentException("A runtime event exceeds the configured byte limit.", nameof(runtimeEvents));
            }
            var snapshot = ProtocolJson.DeserializeRuntimeEvent(json);
            if (!eventIds.Add(snapshot.EventId))
            {
                throw new JournalEntryConflictException("An event ID occurs more than once in an atomic batch.");
            }
            var currentStream = GetStreamId(snapshot.RunId);
            streamId ??= currentStream;
            if (!string.Equals(streamId, currentStream, StringComparison.Ordinal))
            {
                throw new ArgumentException("Every event in an atomic batch must belong to one run.", nameof(runtimeEvents));
            }
            snapshots[index] = snapshot;
        }

        await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            var duplicates = new JournalAppendResult?[snapshots.Length];
            var duplicateCount = 0;
            for (var index = 0; index < snapshots.Length; index++)
            {
                var existing = await ReadEventByIdAsync(connection, transaction, snapshots[index].EventId, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    continue;
                }
                if (!RelationalJournalSemantics.EventsEquivalent(snapshots[index], existing.Event))
                {
                    throw new JournalEntryConflictException($"Event id '{snapshots[index].EventId}' already refers to different content.");
                }
                duplicates[index] = new JournalAppendResult(existing.Sequence, existing.Revision, true);
                duplicateCount++;
            }
            if (duplicateCount == snapshots.Length)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicates.Select(static value => value!).ToArray();
            }
            if (duplicateCount != 0)
            {
                throw new JournalEntryConflictException("An atomic journal batch cannot mix duplicate and new events.");
            }

            var run = await LockRunAsync(connection, transaction, streamId!, cancellationToken).ConfigureAwait(false);
            var projection = new RelationalProjection(run.Checkpoint);
            var results = new JournalAppendResult[snapshots.Length];
            var operationDuplicateCount = 0;
            for (var index = 0; index < snapshots.Length; index++)
            {
                var runtimeEvent = snapshots[index];
                var sequence = checked(run.NextSequence + index);
                var revision = checked(run.Revision + index + 1);
                runtimeEvent.Sequence = sequence;
                var operationDuplicate = await projection.ValidateAsync(
                    this, connection, transaction, runtimeEvent, sequence, revision, cancellationToken).ConfigureAwait(false);
                if (operationDuplicate is not null)
                {
                    results[index] = operationDuplicate;
                    operationDuplicateCount++;
                }
                else
                {
                    results[index] = new JournalAppendResult(sequence, revision, false);
                }
            }
            if (operationDuplicateCount == snapshots.Length)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return results;
            }
            if (operationDuplicateCount != 0)
            {
                throw new JournalEntryConflictException(
                    "An atomic journal batch cannot mix operation duplicates and new events.");
            }
            if (expectedRunRevision.HasValue && expectedRunRevision.Value != run.Revision)
            {
                throw new RunRevisionConflictException(streamId!, expectedRunRevision.Value, run.Revision);
            }
            if (run.NextSequence + snapshots.Length > _options.MaxEventsPerRun)
            {
                throw new InvalidOperationException("The relational journal run has reached its configured event limit.");
            }

            for (var index = 0; index < snapshots.Length; index++)
            {
                await InsertEventAsync(
                    connection,
                    transaction,
                    streamId!,
                    snapshots[index],
                    results[index],
                    cancellationToken).ConfigureAwait(false);
            }
            await projection.PersistAsync(this, connection, transaction, cancellationToken).ConfigureAwait(false);
            var finalRevision = checked(run.Revision + snapshots.Length);
            var finalSequence = checked(run.NextSequence + snapshots.Length);
            var changed = await UpdateRunAsync(
                connection, transaction, streamId!, run.Revision, finalRevision, finalSequence,
                projection.Checkpoint, cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new RunRevisionConflictException(streamId!, run.Revision, await ReadRevisionAsync(connection, transaction, streamId!, cancellationToken).ConfigureAwait(false));
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return results;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the journal failure if the provider has already ended the transaction.
            }
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(string runId, CancellationToken cancellationToken)
    {
        RelationalSessionStoreOptions.ValidateId(runId, nameof(runId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT event_json FROM game_agent_events WHERE namespace_id=@namespace AND stream_id=@stream ORDER BY sequence");
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@stream", runId);
        var events = new List<RuntimeEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(ProtocolJson.DeserializeRuntimeEvent(reader.GetString(0)));
        }
        return events;
    }

    public async ValueTask<RunJournalCursor> GetRunCursorAsync(string runId, CancellationToken cancellationToken = default)
    {
        RelationalSessionStoreOptions.ValidateId(runId, nameof(runId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadRunAsync(connection, null, runId, false, cancellationToken).ConfigureAwait(false);
        return new RunJournalCursor(runId, row?.NextSequence ?? 0, row?.Revision ?? 0);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<OperationLedgerEntry?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        RelationalSessionStoreOptions.ValidateId(operationId, nameof(operationId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var operation = await ReadOperationAsync(
            connection,
            null,
            operationId,
            false,
            cancellationToken).ConfigureAwait(false);
        return operation?.ToPublic();
    }

    public async ValueTask<IReadOnlyList<OperationLedgerEntry>> ReadPendingOperationsAsync(
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (runId is not null)
        {
            RelationalSessionStoreOptions.ValidateId(runId, nameof(runId));
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = "SELECT request_json, receipt_json, request_sequence, request_revision, receipt_sequence, receipt_revision "
                  + "FROM game_agent_operations WHERE namespace_id=@namespace AND (receipt_status IS NULL OR receipt_status=@unknown)"
                  + (runId is null ? string.Empty : " AND run_id=@run")
                  + " ORDER BY request_sequence, operation_id";
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@unknown", ReceiptStatuses.Unknown);
        if (runId is not null) Add(command, "@run", runId);
        var result = new List<OperationLedgerEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadOperation(reader).ToPublic());
        }
        return result;
    }

    public async ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
        RuntimeEvent receiptEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiptEvent);
        if (!string.Equals(receiptEvent.Kind, RuntimeEventKinds.ActionReceived, StringComparison.Ordinal))
        {
            throw new ArgumentException("Reconciliation requires an action.received event.", nameof(receiptEvent));
        }
        var append = await AppendAtomicAsync(receiptEvent, expectedRunRevision, cancellationToken).ConfigureAwait(false);
        var receipt = ProtocolJson.DeserializeActionReceipt(receiptEvent.Payload.GetRawText());
        var operation = await GetOperationAsync(receipt.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new OperationLedgerConflictException(receipt.OperationId, "the operation disappeared after reconciliation.");
        return new ReceiptReconcileResult(append, operation);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _initializeGate.Dispose();
        if (_options.DisposeConnectionFactory)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IEnumerable<string> SchemaStatements()
    {
        yield return "CREATE TABLE IF NOT EXISTS game_agent_runs (namespace_id VARCHAR(256) NOT NULL, stream_id VARCHAR(256) NOT NULL, revision BIGINT NOT NULL, next_sequence BIGINT NOT NULL, checkpoint_json TEXT NULL, PRIMARY KEY(namespace_id, stream_id))";
        yield return "CREATE TABLE IF NOT EXISTS game_agent_events (namespace_id VARCHAR(256) NOT NULL, event_id VARCHAR(256) NOT NULL, stream_id VARCHAR(256) NOT NULL, sequence BIGINT NOT NULL, revision BIGINT NOT NULL, event_json TEXT NOT NULL, PRIMARY KEY(namespace_id, event_id), UNIQUE(namespace_id, stream_id, sequence))";
        yield return "CREATE TABLE IF NOT EXISTS game_agent_operations (namespace_id VARCHAR(256) NOT NULL, operation_id VARCHAR(256) NOT NULL, run_id VARCHAR(256) NOT NULL, request_json TEXT NOT NULL, receipt_json TEXT NULL, receipt_status VARCHAR(32) NULL, request_sequence BIGINT NOT NULL, request_revision BIGINT NOT NULL, receipt_sequence BIGINT NULL, receipt_revision BIGINT NULL, PRIMARY KEY(namespace_id, operation_id))";
        yield return "CREATE INDEX IF NOT EXISTS ix_game_agent_operations_pending ON game_agent_operations(namespace_id, receipt_status, run_id)";
    }

    private async ValueTask<RunRow> LockRunAsync(DbConnection connection, DbTransaction transaction, string streamId, CancellationToken cancellationToken)
    {
        await using (var insert = CreateCommand(connection, transaction,
            "INSERT INTO game_agent_runs(namespace_id,stream_id,revision,next_sequence,checkpoint_json) VALUES(@namespace,@stream,0,0,NULL) ON CONFLICT(namespace_id,stream_id) DO NOTHING"))
        {
            Add(insert, "@namespace", _options.NamespaceId);
            Add(insert, "@stream", streamId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return await ReadRunAsync(connection, transaction, streamId, true, cancellationToken).ConfigureAwait(false)
            ?? throw new RelationalJournalSchemaException("The run row could not be created.");
    }

    private async ValueTask<RunRow?> ReadRunAsync(DbConnection connection, DbTransaction? transaction, string streamId, bool forUpdate, CancellationToken cancellationToken)
    {
        var suffix = forUpdate && _factory.Dialect == RelationalJournalDialect.PostgreSql ? " FOR UPDATE" : string.Empty;
        await using var command = CreateCommand(connection, transaction,
            "SELECT revision,next_sequence,checkpoint_json FROM game_agent_runs WHERE namespace_id=@namespace AND stream_id=@stream" + suffix);
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@stream", streamId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new RunRow(
            reader.GetInt64(0), reader.GetInt64(1),
            reader.IsDBNull(2) ? null : ProtocolJson.DeserializeAgentRun(reader.GetString(2)));
    }

    private async ValueTask<StoredEvent?> ReadEventByIdAsync(DbConnection connection, DbTransaction transaction, string eventId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT sequence,revision,event_json FROM game_agent_events WHERE namespace_id=@namespace AND event_id=@event");
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@event", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new StoredEvent(reader.GetInt64(0), reader.GetInt64(1), ProtocolJson.DeserializeRuntimeEvent(reader.GetString(2)));
    }

    private async ValueTask InsertEventAsync(DbConnection connection, DbTransaction transaction, string streamId, RuntimeEvent runtimeEvent, JournalAppendResult append, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "INSERT INTO game_agent_events(namespace_id,event_id,stream_id,sequence,revision,event_json) VALUES(@namespace,@event,@stream,@sequence,@revision,@json)");
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@event", runtimeEvent.EventId);
        Add(command, "@stream", streamId);
        Add(command, "@sequence", append.Sequence);
        Add(command, "@revision", append.Revision);
        Add(command, "@json", ProtocolJson.Serialize(runtimeEvent));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> UpdateRunAsync(DbConnection connection, DbTransaction transaction, string streamId, long oldRevision, long newRevision, long nextSequence, AgentRun? checkpoint, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "UPDATE game_agent_runs SET revision=@newRevision,next_sequence=@nextSequence,checkpoint_json=@checkpoint WHERE namespace_id=@namespace AND stream_id=@stream AND revision=@oldRevision");
        Add(command, "@newRevision", newRevision);
        Add(command, "@nextSequence", nextSequence);
        Add(command, "@checkpoint", checkpoint is null ? DBNull.Value : ProtocolJson.Serialize(checkpoint));
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@stream", streamId);
        Add(command, "@oldRevision", oldRevision);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> ReadRevisionAsync(DbConnection connection, DbTransaction transaction, string streamId, CancellationToken cancellationToken)
    {
        var row = await ReadRunAsync(connection, transaction, streamId, false, cancellationToken).ConfigureAwait(false);
        return row?.Revision ?? 0;
    }

    private async ValueTask<OperationRow?> ReadOperationAsync(DbConnection connection, DbTransaction? transaction, string operationId, bool forUpdate, CancellationToken cancellationToken)
    {
        var suffix = forUpdate && _factory.Dialect == RelationalJournalDialect.PostgreSql ? " FOR UPDATE" : string.Empty;
        await using var command = CreateCommand(connection, transaction,
            "SELECT request_json,receipt_json,request_sequence,request_revision,receipt_sequence,receipt_revision FROM game_agent_operations WHERE namespace_id=@namespace AND operation_id=@operation" + suffix);
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@operation", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOperation(reader) : null;
    }

    private static OperationRow ReadOperation(DbDataReader reader) => new(
        ProtocolJson.DeserializeActionRequest(reader.GetString(0)),
        reader.IsDBNull(1) ? null : ProtocolJson.DeserializeActionReceipt(reader.GetString(1)),
        reader.GetInt64(2), reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5));

    private async ValueTask InsertOperationAsync(DbConnection connection, DbTransaction transaction, OperationMutation mutation, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "INSERT INTO game_agent_operations(namespace_id,operation_id,run_id,request_json,receipt_json,receipt_status,request_sequence,request_revision,receipt_sequence,receipt_revision) VALUES(@namespace,@operation,@run,@request,NULL,NULL,@sequence,@revision,NULL,NULL)");
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@operation", mutation.Request!.OperationId);
        Add(command, "@run", mutation.Request.RunId);
        Add(command, "@request", ProtocolJson.Serialize(mutation.Request));
        Add(command, "@sequence", mutation.Sequence);
        Add(command, "@revision", mutation.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpdateOperationReceiptAsync(DbConnection connection, DbTransaction transaction, OperationMutation mutation, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "UPDATE game_agent_operations SET receipt_json=@receipt,receipt_status=@status,receipt_sequence=@sequence,receipt_revision=@revision WHERE namespace_id=@namespace AND operation_id=@operation");
        Add(command, "@receipt", ProtocolJson.Serialize(mutation.Receipt!));
        Add(command, "@status", mutation.Receipt!.Status);
        Add(command, "@sequence", mutation.Sequence);
        Add(command, "@revision", mutation.Revision);
        Add(command, "@namespace", _options.NamespaceId);
        Add(command, "@operation", mutation.Receipt.OperationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new OperationLedgerConflictException(mutation.Receipt.OperationId, "no durable action request exists.");
        }
    }

    private DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.CommandTimeout = checked((int)Math.Ceiling(_options.CommandTimeout.TotalSeconds));
        return command;
    }

    private async ValueTask ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string GetStreamId(string? runId) => string.IsNullOrEmpty(runId) ? GlobalStreamId : runId;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record RunRow(long Revision, long NextSequence, AgentRun? Checkpoint);
    private sealed record StoredEvent(long Sequence, long Revision, RuntimeEvent Event);

    private sealed record OperationRow(
        ActionRequest Request,
        ActionReceipt? Receipt,
        long RequestSequence,
        long RequestRevision,
        long? ReceiptSequence,
        long? ReceiptRevision)
    {
        public OperationLedgerEntry ToPublic() => new(
            ProtocolJson.DeserializeActionRequest(ProtocolJson.Serialize(Request)),
            Receipt is null ? null : ProtocolJson.DeserializeActionReceipt(ProtocolJson.Serialize(Receipt)),
            RequestSequence, RequestRevision, ReceiptSequence, ReceiptRevision);
    }

    private sealed record OperationMutation(ActionRequest? Request, ActionReceipt? Receipt, long Sequence, long Revision);

    private sealed class RelationalProjection
    {
        private readonly Dictionary<string, OperationRow> _operations = new(StringComparer.Ordinal);
        private readonly List<OperationMutation> _mutations = new();
        public RelationalProjection(AgentRun? checkpoint) => Checkpoint = checkpoint;
        public AgentRun? Checkpoint { get; private set; }

        public async ValueTask<JournalAppendResult?> ValidateAsync(
            RelationalSessionStore owner,
            DbConnection connection,
            DbTransaction transaction,
            RuntimeEvent runtimeEvent,
            long sequence,
            long revision,
            CancellationToken cancellationToken)
        {
            if (RunCheckpointLifecycleValidator.IsCheckpointKind(runtimeEvent.Kind))
            {
                Checkpoint = RunCheckpointLifecycleValidator.ValidateAndClone(runtimeEvent, Checkpoint, sequence, revision);
                return null;
            }

            if (string.Equals(runtimeEvent.Kind, RuntimeEventKinds.ActionRequested, StringComparison.Ordinal))
            {
                var request = RelationalJournalSemantics.Request(runtimeEvent);
                if (!string.Equals(request.RunId, runtimeEvent.RunId, StringComparison.Ordinal)
                    || !string.Equals(request.TurnId, runtimeEvent.TurnId, StringComparison.Ordinal))
                {
                    throw new OperationLedgerConflictException(request.OperationId, "request identity does not match the journal event.");
                }
                var checkpoint = Checkpoint ?? throw new OperationLedgerConflictException(request.OperationId, "no durable run checkpoint exists.");
                if (!string.Equals(request.AgentId, checkpoint.AgentId, StringComparison.Ordinal)
                    || !string.Equals(request.WorldId, checkpoint.WorldId, StringComparison.Ordinal))
                {
                    throw new OperationLedgerConflictException(request.OperationId, "request agent or world does not match the durable run.");
                }
                var existing = await GetOperation(owner, connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (RelationalJournalSemantics.RequestsEquivalent(request, existing.Request))
                    {
                        return new JournalAppendResult(
                            existing.RequestSequence,
                            existing.RequestRevision,
                            true);
                    }
                    throw new OperationLedgerConflictException(
                        request.OperationId,
                        "a different request is already durable.");
                }
                _mutations.Add(new OperationMutation(request, null, sequence, revision));
                _operations[request.OperationId] = new OperationRow(request, null, sequence, revision, null, null);
                return null;
            }

            if (runtimeEvent.Kind is RuntimeEventKinds.ActionReceived or RuntimeEventKinds.ActionOutcomeUncertain
                or RuntimeEventKinds.ToolCompleted or RuntimeEventKinds.ToolFailed)
            {
                var receipt = RelationalJournalSemantics.Receipt(runtimeEvent);
                var operation = await GetOperation(owner, connection, transaction, receipt.OperationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new OperationLedgerConflictException(receipt.OperationId, "no durable action request exists.");
                if (!string.Equals(operation.Request.RunId, runtimeEvent.RunId, StringComparison.Ordinal)
                    || !string.Equals(operation.Request.TurnId, runtimeEvent.TurnId, StringComparison.Ordinal))
                {
                    throw new OperationLedgerConflictException(receipt.OperationId, "receipt identity does not match its request.");
                }
                var checkpoint = Checkpoint ?? throw new OperationLedgerConflictException(receipt.OperationId, "no durable run checkpoint exists.");
                receipt = ActionReceiptIngressValidator.ValidateAndClone(operation.Request, receipt, checkpoint);

                if (runtimeEvent.Kind == RuntimeEventKinds.ActionOutcomeUncertain)
                {
                    if (receipt.Status != ReceiptStatuses.Unknown || operation.Receipt is not null)
                    {
                        throw new OperationLedgerConflictException(receipt.OperationId, "action uncertainty is not valid for this operation.");
                    }
                    return null;
                }
                if (runtimeEvent.Kind is RuntimeEventKinds.ToolCompleted or RuntimeEventKinds.ToolFailed)
                {
                    if (operation.Receipt is null || !RelationalJournalSemantics.ReceiptsEquivalent(receipt, operation.Receipt))
                    {
                        throw new OperationLedgerConflictException(receipt.OperationId, "terminal receipt does not match a durable received receipt.");
                    }
                    var validStatus = runtimeEvent.Kind == RuntimeEventKinds.ToolFailed
                        ? receipt.Status == ReceiptStatuses.Failed
                        : receipt.Status is ReceiptStatuses.Succeeded or ReceiptStatuses.Rejected;
                    if (!validStatus)
                    {
                        throw new OperationLedgerConflictException(receipt.OperationId, "terminal receipt status does not match the event kind.");
                    }
                    return null;
                }
                if (operation.Receipt is not null)
                {
                    if (receipt.Revision < operation.Receipt.Revision)
                    {
                        throw new OperationLedgerConflictException(receipt.OperationId, "receipt revision moved backwards.");
                    }
                    if (receipt.Revision == operation.Receipt.Revision)
                    {
                        if (RelationalJournalSemantics.ReceiptsEquivalent(receipt, operation.Receipt))
                        {
                            return new JournalAppendResult(
                                operation.ReceiptSequence!.Value,
                                operation.ReceiptRevision!.Value,
                                true);
                        }
                        throw new OperationLedgerConflictException(
                            receipt.OperationId,
                            "the receipt revision already has different content.");
                    }
                    if (operation.Receipt.Status != ReceiptStatuses.Unknown && receipt.Status == ReceiptStatuses.Unknown)
                    {
                        throw new OperationLedgerConflictException(receipt.OperationId, "a terminal operation cannot regress to unknown.");
                    }
                }
                _mutations.Add(new OperationMutation(null, receipt, sequence, revision));
                _operations[receipt.OperationId] = operation with
                {
                    Receipt = receipt,
                    ReceiptSequence = sequence,
                    ReceiptRevision = revision
                };
            }
            return null;
        }

        public async ValueTask PersistAsync(RelationalSessionStore owner, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            foreach (var mutation in _mutations)
            {
                if (mutation.Request is not null)
                {
                    await owner.InsertOperationAsync(connection, transaction, mutation, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await owner.UpdateOperationReceiptAsync(connection, transaction, mutation, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async ValueTask<OperationRow?> GetOperation(RelationalSessionStore owner, DbConnection connection, DbTransaction transaction, string operationId, CancellationToken cancellationToken)
        {
            if (_operations.TryGetValue(operationId, out var staged)) return staged;
            var stored = await owner.ReadOperationAsync(connection, transaction, operationId, true, cancellationToken).ConfigureAwait(false);
            if (stored is not null) _operations.Add(operationId, stored);
            return stored;
        }
    }
}
