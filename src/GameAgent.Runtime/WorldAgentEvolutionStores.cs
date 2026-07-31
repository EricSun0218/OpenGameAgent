using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Runtime;

public sealed class WorldAgentEvolutionCheckpoint
{
    private static readonly JsonValueLimits PayloadLimits = new(
        maxUtf8Bytes: 8 * 1024 * 1024,
        maxDepth: 64,
        maxNodes: 250_000,
        maxStringUtf8Bytes: 1024 * 1024,
        maxContainerItems: 100_000);

    private readonly JsonElement _payload;

    public WorldAgentEvolutionCheckpoint(
        string commandId,
        long revision,
        string commandDigest,
        JsonElement payload)
    {
        CommandId = EvolutionGuard.Required(
            commandId,
            nameof(commandId),
            192);
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Revision = revision;
        CommandDigest = EvolutionGuard.Digest(
            commandDigest,
            nameof(commandDigest));
        JsonValueInspector.ValidateAndMeasure(
            payload,
            PayloadLimits,
            nameof(payload));
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Checkpoint payload must be a JSON object.",
                nameof(payload));
        }

        _payload = payload.Clone();
        PayloadDigest = CanonicalJsonDigest.ComputeSha256(_payload);
    }

    public string CommandId { get; }

    public long Revision { get; }

    public string CommandDigest { get; }

    public JsonElement Payload => _payload.Clone();

    public string PayloadDigest { get; }

    public JsonElement ToEnvelope()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contract",
                "game-agent.world-agent-evolution-checkpoint.v1");
            writer.WriteString("commandId", CommandId);
            writer.WriteNumber("revision", Revision);
            writer.WriteString("commandDigest", CommandDigest);
            writer.WriteString("payloadDigest", PayloadDigest);
            writer.WritePropertyName("payload");
            _payload.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    public static WorldAgentEvolutionCheckpoint FromEnvelope(
        JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Checkpoint envelope must be an object.",
                nameof(envelope));
        }

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "contract",
            "commandId",
            "revision",
            "commandDigest",
            "payloadDigest",
            "payload"
        };
        var names = envelope.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (names.Length != expected.Count
            || names.Distinct(StringComparer.Ordinal).Count()
            != names.Length
            || names.Any(name => !expected.Contains(name))
            || !string.Equals(
                envelope.GetProperty("contract").GetString(),
                "game-agent.world-agent-evolution-checkpoint.v1",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Checkpoint envelope has an unsupported shape.",
                nameof(envelope));
        }

        var checkpoint = new WorldAgentEvolutionCheckpoint(
            envelope.GetProperty("commandId").GetString()!,
            envelope.GetProperty("revision").GetInt64(),
            envelope.GetProperty("commandDigest").GetString()!,
            envelope.GetProperty("payload"));
        if (!string.Equals(
                checkpoint.PayloadDigest,
                envelope.GetProperty("payloadDigest").GetString(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Checkpoint payload digest does not match its content.",
                nameof(envelope));
        }

        return checkpoint;
    }
}

public enum WorldAgentEvolutionStoreWriteStatus
{
    Written = 0,
    Duplicate = 1,
    Conflict = 2
}

public sealed class WorldAgentEvolutionStoreWriteResult
{
    public WorldAgentEvolutionStoreWriteResult(
        WorldAgentEvolutionStoreWriteStatus status,
        WorldAgentEvolutionCheckpoint current)
    {
        if (!Enum.IsDefined(
                typeof(WorldAgentEvolutionStoreWriteStatus),
                status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Current = current
                  ?? throw new ArgumentNullException(nameof(current));
    }

    public WorldAgentEvolutionStoreWriteStatus Status { get; }

    public WorldAgentEvolutionCheckpoint Current { get; }
}

public interface IWorldAgentEvolutionStore
{
    ValueTask<WorldAgentEvolutionCheckpoint?> ReadAsync(
        string commandId,
        CancellationToken cancellationToken = default);

    ValueTask<WorldAgentEvolutionStoreWriteResult> CompareExchangeAsync(
        WorldAgentEvolutionCheckpoint checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryWorldAgentEvolutionStore
    : IWorldAgentEvolutionStore
{
    private readonly object _sync = new();

    private readonly Dictionary<string, WorldAgentEvolutionCheckpoint>
        _checkpoints = new(StringComparer.Ordinal);

    public ValueTask<WorldAgentEvolutionCheckpoint?> ReadAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        commandId = EvolutionGuard.Required(
            commandId,
            nameof(commandId),
            192);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return new ValueTask<WorldAgentEvolutionCheckpoint?>(
                _checkpoints.TryGetValue(commandId, out var checkpoint)
                    ? Clone(checkpoint)
                    : null);
        }
    }

    public ValueTask<WorldAgentEvolutionStoreWriteResult>
        CompareExchangeAsync(
            WorldAgentEvolutionCheckpoint checkpoint,
            long expectedRevision,
            CancellationToken cancellationToken = default)
    {
        if (checkpoint is null)
        {
            throw new ArgumentNullException(nameof(checkpoint));
        }

        if (expectedRevision < 0
            || checkpoint.Revision != expectedRevision + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _checkpoints.TryGetValue(
                checkpoint.CommandId,
                out var current);
            var currentRevision = current?.Revision ?? 0;
            if (currentRevision != expectedRevision)
            {
                return new ValueTask<WorldAgentEvolutionStoreWriteResult>(
                    new WorldAgentEvolutionStoreWriteResult(
                        IsSame(current, checkpoint)
                            ? WorldAgentEvolutionStoreWriteStatus.Duplicate
                            : WorldAgentEvolutionStoreWriteStatus.Conflict,
                        Clone(current
                              ?? throw new InvalidOperationException(
                                  "A revision conflict requires a current checkpoint."))));
            }

            var stored = Clone(checkpoint);
            _checkpoints[checkpoint.CommandId] = stored;
            return new ValueTask<WorldAgentEvolutionStoreWriteResult>(
                new WorldAgentEvolutionStoreWriteResult(
                    WorldAgentEvolutionStoreWriteStatus.Written,
                    Clone(stored)));
        }
    }

    private static bool IsSame(
        WorldAgentEvolutionCheckpoint? left,
        WorldAgentEvolutionCheckpoint right)
    {
        return left is not null
               && left.Revision == right.Revision
               && string.Equals(
                   left.CommandDigest,
                   right.CommandDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.PayloadDigest,
                   right.PayloadDigest,
                   StringComparison.Ordinal);
    }

    private static WorldAgentEvolutionCheckpoint Clone(
        WorldAgentEvolutionCheckpoint checkpoint)
    {
        return WorldAgentEvolutionCheckpoint.FromEnvelope(
            checkpoint.ToEnvelope());
    }
}

/// <summary>
/// Stores evolution checkpoints in a dedicated durable session journal. The
/// supplied journal must not be shared with agent-run events.
/// </summary>
public sealed class JournalWorldAgentEvolutionStore
    : IWorldAgentEvolutionStore
{
    public const string EventKind = "world_evolution_checkpoint";

    private const int DefaultMaximumCheckpointEventsPerCommand = 65_536;
    private const int LockStripeCount = 64;

    private readonly IDurableSessionStore _journal;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly int _maximumCheckpointEventsPerCommand;
    private readonly SemaphoreSlim[] _gates =
        Enumerable.Range(0, LockStripeCount)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();

    public JournalWorldAgentEvolutionStore(
        IDurableSessionStore journal,
        Func<DateTimeOffset>? utcNow = null,
        int maximumCheckpointEventsPerCommand =
            DefaultMaximumCheckpointEventsPerCommand)
    {
        if (maximumCheckpointEventsPerCommand is < 1
            or > DefaultMaximumCheckpointEventsPerCommand)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCheckpointEventsPerCommand));
        }

        _journal = journal
                   ?? throw new ArgumentNullException(nameof(journal));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _maximumCheckpointEventsPerCommand =
            maximumCheckpointEventsPerCommand;
    }

    public async ValueTask<WorldAgentEvolutionCheckpoint?> ReadAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        commandId = EvolutionGuard.Required(
            commandId,
            nameof(commandId),
            192);
        var events = await _journal.ReadRunAsync(
                StreamId(commandId),
                cancellationToken)
            .ConfigureAwait(false);
        if (events.Count > _maximumCheckpointEventsPerCommand)
        {
            throw new InvalidDataException(
                "Evolution checkpoint history exceeds its item limit.");
        }

        WorldAgentEvolutionCheckpoint? latest = null;
        var enumerated = 0;
        using var enumerator = events.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                break;
            }

            enumerated++;
            if (enumerated > _maximumCheckpointEventsPerCommand)
            {
                throw new InvalidDataException(
                    "Evolution checkpoint history exceeds its item limit.");
            }

            var runtimeEvent = enumerator.Current;
            if (runtimeEvent is null
                || !string.Equals(
                    runtimeEvent.Kind,
                    EventKind,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Evolution journal contains an unsupported event.");
            }

            WorldAgentEvolutionCheckpoint decoded;
            try
            {
                decoded =
                    WorldAgentEvolutionCheckpoint.FromEnvelope(
                        runtimeEvent.Payload);
            }
            catch (Exception exception)
                when (exception is ArgumentException
                      or InvalidOperationException
                      or FormatException)
            {
                throw new InvalidDataException(
                    "Evolution journal contains an invalid checkpoint.",
                    exception);
            }

            if (!string.Equals(
                    decoded.CommandId,
                    commandId,
                    StringComparison.Ordinal)
                || decoded.Revision != (latest?.Revision ?? 0) + 1
                || latest is not null
                && !string.Equals(
                    decoded.CommandDigest,
                    latest.CommandDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Evolution checkpoint history is inconsistent.");
            }

            latest = decoded;
        }

        return latest;
    }

    public async ValueTask<WorldAgentEvolutionStoreWriteResult>
        CompareExchangeAsync(
            WorldAgentEvolutionCheckpoint checkpoint,
            long expectedRevision,
            CancellationToken cancellationToken = default)
    {
        if (checkpoint is null)
        {
            throw new ArgumentNullException(nameof(checkpoint));
        }

        if (expectedRevision < 0
            || checkpoint.Revision != expectedRevision + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision));
        }

        var gate = _gates[LockStripe(checkpoint.CommandId)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(
                    checkpoint.CommandId,
                    cancellationToken)
                .ConfigureAwait(false);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                return Existing(current, checkpoint);
            }

            var runtimeEvent = new RuntimeEvent
            {
                EventId = EventId(checkpoint),
                RunId = StreamId(checkpoint.CommandId),
                Sequence = 0,
                Kind = EventKind,
                Durability = EventDurabilities.Durable,
                RuntimeGeneration = 1,
                Timestamp = _utcNow(),
                Payload = checkpoint.ToEnvelope()
            };
            try
            {
                var append = await _journal.AppendAtomicAsync(
                        runtimeEvent,
                        expectedRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                var stored = await ReadAsync(
                        checkpoint.CommandId,
                        cancellationToken)
                    .ConfigureAwait(false)
                             ?? throw new InvalidDataException(
                                 "The appended checkpoint is not readable.");
                return new WorldAgentEvolutionStoreWriteResult(
                    append.WasDuplicate
                        ? WorldAgentEvolutionStoreWriteStatus.Duplicate
                        : WorldAgentEvolutionStoreWriteStatus.Written,
                    stored);
            }
            catch (RunRevisionConflictException)
            {
                current = await ReadAsync(
                        checkpoint.CommandId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Existing(current, checkpoint);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static WorldAgentEvolutionStoreWriteResult Existing(
        WorldAgentEvolutionCheckpoint? current,
        WorldAgentEvolutionCheckpoint candidate)
    {
        if (current is null)
        {
            throw new InvalidDataException(
                "The journal revision conflicts without a checkpoint.");
        }

        var duplicate = current.Revision == candidate.Revision
                        && string.Equals(
                            current.CommandDigest,
                            candidate.CommandDigest,
                            StringComparison.Ordinal)
                        && string.Equals(
                            current.PayloadDigest,
                            candidate.PayloadDigest,
                            StringComparison.Ordinal);
        return new WorldAgentEvolutionStoreWriteResult(
            duplicate
                ? WorldAgentEvolutionStoreWriteStatus.Duplicate
                : WorldAgentEvolutionStoreWriteStatus.Conflict,
            current);
    }

    private static string StreamId(string commandId)
    {
        return "world-evolution-"
               + DigestString(commandId);
    }

    private static string EventId(
        WorldAgentEvolutionCheckpoint checkpoint)
    {
        return "world-evolution-"
               + DigestString(checkpoint.CommandId)[..24]
               + "-"
               + checkpoint.Revision.ToString(
                   "D20",
                   System.Globalization.CultureInfo.InvariantCulture)
               + "-"
               + checkpoint.PayloadDigest[..24];
    }

    private static string DigestString(string value)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStringValue(value);
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return CanonicalJsonDigest.ComputeSha256(document.RootElement);
    }

    private static int LockStripe(string commandId)
    {
        var digest = DigestString(commandId);
        return Convert.ToInt32(digest[..8], 16)
               & (LockStripeCount - 1);
    }
}
