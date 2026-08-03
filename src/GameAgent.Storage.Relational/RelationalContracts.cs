using System.Data.Common;

namespace GameAgent.Storage.Relational;

public enum RelationalJournalDialect
{
    Sqlite,
    PostgreSql
}

public interface IRelationalJournalConnectionFactory : IAsyncDisposable
{
    RelationalJournalDialect Dialect { get; }

    ValueTask<DbConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default);
}

public sealed class RelationalSessionStoreOptions
{
    public string NamespaceId { get; set; } = "default";
    public int MaxEventBytes { get; set; } = 1_048_576;
    public int MaxBatchEvents { get; set; } = 4_096;
    public int MaxEventsPerRun { get; set; } = 100_000;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool DisposeConnectionFactory { get; set; } = true;

    internal RelationalSessionStoreOptions Snapshot()
    {
        ValidateId(NamespaceId, nameof(NamespaceId));
        if (MaxEventBytes is < 1_024 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEventBytes));
        }
        if (MaxBatchEvents is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBatchEvents));
        }
        if (MaxEventsPerRun is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEventsPerRun));
        }
        if (CommandTimeout < TimeSpan.FromMilliseconds(100)
            || CommandTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }
        return new RelationalSessionStoreOptions
        {
            NamespaceId = NamespaceId,
            MaxEventBytes = MaxEventBytes,
            MaxBatchEvents = MaxBatchEvents,
            MaxEventsPerRun = MaxEventsPerRun,
            CommandTimeout = CommandTimeout,
            DisposeConnectionFactory = DisposeConnectionFactory
        };
    }

    internal static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException("A bounded identifier is required.", name);
        }
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                  or >= 'A' and <= 'Z'
                  or >= '0' and <= '9'
                  or '.' or '_' or ':' or '-'))
            {
                throw new ArgumentException("The identifier contains an unsupported character.", name);
            }
        }
    }
}

public sealed class RelationalJournalSchemaException : InvalidOperationException
{
    public RelationalJournalSchemaException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
