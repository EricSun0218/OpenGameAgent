using System.Data.Common;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Storage.Relational;
using Npgsql;

namespace GameAgent.Storage.Postgres;

public sealed class PostgresSessionStore : IDurableSessionStore, IOperationLedger
{
    private readonly RelationalSessionStore _inner;

    public PostgresSessionStore(string connectionString, RelationalSessionStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        }
        _inner = new RelationalSessionStore(new PostgresConnectionFactory(connectionString), options);
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

    private sealed class PostgresConnectionFactory : IRelationalJournalConnectionFactory
    {
        private readonly NpgsqlDataSource _dataSource;

        public PostgresConnectionFactory(string connectionString)
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            _dataSource = builder.Build();
        }

        public RelationalJournalDialect Dialect => RelationalJournalDialect.PostgreSql;

        public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
    }
}
