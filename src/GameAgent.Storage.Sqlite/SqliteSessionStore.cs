using System.Data.Common;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Storage.Relational;
using Microsoft.Data.Sqlite;

namespace GameAgent.Storage.Sqlite;

public sealed class SqliteSessionStore : IDurableSessionStore, IOperationLedger
{
    private readonly RelationalSessionStore _inner;

    public SqliteSessionStore(string connectionString, RelationalSessionStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A SQLite connection string is required.", nameof(connectionString));
        }
        _inner = new RelationalSessionStore(new SqliteConnectionFactory(connectionString), options);
    }

    public RelationalSessionStore Store => _inner;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _inner.InitializeAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public ValueTask AppendAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken) =>
        _inner.AppendAsync(runtimeEvent, cancellationToken);

    public ValueTask<JournalAppendResult> AppendAtomicAsync(RuntimeEvent runtimeEvent, long? expectedRunRevision = null, CancellationToken cancellationToken = default) =>
        _inner.AppendAtomicAsync(runtimeEvent, expectedRunRevision, cancellationToken);

    public ValueTask<IReadOnlyList<JournalAppendResult>> AppendAtomicBatchAsync(IReadOnlyList<RuntimeEvent> runtimeEvents, long? expectedRunRevision = null, CancellationToken cancellationToken = default) =>
        _inner.AppendAtomicBatchAsync(runtimeEvents, expectedRunRevision, cancellationToken);

    public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(string runId, CancellationToken cancellationToken) =>
        _inner.ReadRunAsync(runId, cancellationToken);

    public ValueTask<RunJournalCursor> GetRunCursorAsync(string runId, CancellationToken cancellationToken = default) =>
        _inner.GetRunCursorAsync(runId, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

    public ValueTask<OperationLedgerEntry?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        _inner.GetOperationAsync(operationId, cancellationToken);

    public ValueTask<IReadOnlyList<OperationLedgerEntry>> ReadPendingOperationsAsync(string? runId = null, CancellationToken cancellationToken = default) =>
        _inner.ReadPendingOperationsAsync(runId, cancellationToken);

    public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(RuntimeEvent receiptEvent, long? expectedRunRevision = null, CancellationToken cancellationToken = default) =>
        _inner.ReconcileReceiptAsync(receiptEvent, expectedRunRevision, cancellationToken);

    private sealed class SqliteConnectionFactory : IRelationalJournalConnectionFactory
    {
        private readonly string _connectionString;

        public SqliteConnectionFactory(string connectionString)
        {
            var builder = new SqliteConnectionStringBuilder(connectionString)
            {
                ForeignKeys = true,
                DefaultTimeout = 30
            };
            _connectionString = builder.ConnectionString;
        }

        public RelationalJournalDialect Dialect => RelationalJournalDialect.Sqlite;

        public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=30000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
