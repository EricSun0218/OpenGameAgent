using System.Reflection;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class JournalCoordinatorTrustBoundaryTests
{
    private const string CustomEventKind = "game.custom.signal";
    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> ReservedRuntimeEventKinds()
    {
        return typeof(RuntimeEventKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(
                field => field.IsLiteral
                         && !field.IsInitOnly
                         && field.FieldType == typeof(string))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(
                field => new[]
                {
                    (object)(string)field.GetRawConstantValue()!
                });
    }

    [Theory]
    [MemberData(nameof(ReservedRuntimeEventKinds))]
    public async Task PublicDurableAppendRejectsReservedKind(string kind)
    {
        await using var store = new RecordingStore();
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var before = ProtocolJson.Serialize(run);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => journal.AppendDurableAsync(
                    run,
                    kind,
                    Json("""{"value":1}"""),
                    "turn-1",
                    "attempt-1", cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("kind", exception.ParamName);
        Assert.Equal(before, ProtocolJson.Serialize(run));
        Assert.Equal(0, store.WriteCalls);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task PublicDurableAppendAllowsCustomKind()
    {
        await using var store = new RecordingStore();
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var beforeRevision = run.Revision;

        await journal.AppendDurableAsync(
            run,
            CustomEventKind,
            Json("""{"value":1}"""),
            "turn-1",
            "attempt-1",
            eventId: "custom-event", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, store.AtomicAppendCalls);
        Assert.Equal(0, store.BatchAppendCalls);
        Assert.Equal(checked(beforeRevision + 1), run.Revision);
        var published = Assert.Single(publisher.Events);
        Assert.Equal(CustomEventKind, published.Kind);
        Assert.Equal(
            RuntimeEventIdDerivation.Derive(run.RunId, "custom-event"),
            published.EventId);
        Assert.Equal(beforeRevision, published.Sequence);
    }

    [Fact]
    public async Task TransitionKindMismatchHasNoSideEffects()
    {
        await using var store = new RecordingStore();
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var before = ProtocolJson.Serialize(run);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => journal.CommitTransitionAsync(
                    run,
                    RunStates.Completed,
                    RuntimeEventKinds.RunFailed, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("eventKind", exception.ParamName);
        Assert.Equal(before, ProtocolJson.Serialize(run));
        Assert.Equal(0, store.WriteCalls);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task TurnPreparationGenerationMismatchHasNoSideEffects()
    {
        await using var store = new RecordingStore();
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var snapshot = CreateTurnSnapshot(
            run,
            runtimeGeneration: checked(run.RuntimeGeneration + 1));
        var before = ProtocolJson.Serialize(run);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => journal.CommitTurnPreparationAsync(
                    run,
                    snapshot.TurnId,
                    "attempt-1",
                    Array.Empty<NormalizedMessage>(),
                    snapshot,
                    Timestamp,
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("snapshot", exception.ParamName);
        Assert.Equal(before, ProtocolJson.Serialize(run));
        Assert.Equal(0, store.WriteCalls);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task InvalidFreshSequenceDoesNotMutateOrPublish()
    {
        await using var store = new RecordingStore
        {
            SingleResultFactory = baseRevision =>
                new JournalAppendResult(
                    sequence: checked(baseRevision + 1),
                    revision: checked(baseRevision + 1),
                    wasDuplicate: false)
        };
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var before = ProtocolJson.Serialize(run);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.AppendDurableAsync(
                    run,
                    CustomEventKind,
                    Json("""{"value":1}"""),
                    "turn-1",
                    "attempt-1", cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(before, ProtocolJson.Serialize(run));
        Assert.Equal(1, store.AtomicAppendCalls);
        Assert.Equal(0, store.BatchAppendCalls);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task InvalidBatchBaseSequenceDoesNotMutateOrPublish()
    {
        await using var store = new RecordingStore
        {
            BatchResultFactory = (baseRevision, count) =>
                Enumerable.Range(0, count)
                    .Select(
                        index => new JournalAppendResult(
                            sequence: checked(baseRevision + index + 1),
                            revision: checked(baseRevision + index + 1),
                            wasDuplicate: false))
                    .ToArray()
        };
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var snapshot = CreateTurnSnapshot(run, run.RuntimeGeneration);
        var before = ProtocolJson.Serialize(run);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.CommitTurnPreparationAsync(
                    run,
                    snapshot.TurnId,
                    "attempt-1",
                    Array.Empty<NormalizedMessage>(),
                    snapshot,
                    Timestamp,
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(before, ProtocolJson.Serialize(run));
        Assert.Equal(0, store.AtomicAppendCalls);
        Assert.Equal(1, store.BatchAppendCalls);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task BatchResultIsSnapshottedByIndexWithoutEnumeration()
    {
        DeclaredReadOnlyList<JournalAppendResult>? source = null;
        await using var store = new RecordingStore
        {
            BatchResultFactory = (baseRevision, count) =>
                source = new DeclaredReadOnlyList<JournalAppendResult>(
                    count,
                    index => new JournalAppendResult(
                        sequence: checked(baseRevision + index),
                        revision: checked(baseRevision + index + 1),
                        wasDuplicate: false))
        };
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var snapshot = CreateTurnSnapshot(run, run.RuntimeGeneration);

        await journal.CommitTurnPreparationAsync(
            run,
            snapshot.TurnId,
            "attempt-1",
            Array.Empty<NormalizedMessage>(),
            snapshot,
            Timestamp,
            TestContext.Current.CancellationToken);

        Assert.NotNull(source);
        Assert.Equal(1, source.CountReads);
        Assert.Equal(source.DeclaredCount, source.IndexReads);
        Assert.Equal(0, source.EnumerationAttempts);
        Assert.NotEmpty(publisher.Events);
    }

    [Fact]
    public async Task BatchCountIndexMismatchDoesNotMutateOrPublish()
    {
        await using var store = new RecordingStore
        {
            BatchResultFactory = (baseRevision, count) =>
                new DeclaredReadOnlyList<JournalAppendResult>(
                    count,
                    index => index == 0
                        ? new JournalAppendResult(
                            sequence: baseRevision,
                            revision: checked(baseRevision + 1),
                            wasDuplicate: false)
                        : throw new ArgumentOutOfRangeException(
                            nameof(index)))
        };
        var publisher = new RecordingPublisher();
        using var journal = CreateCoordinator(store, publisher);
        var run = CreateRunningRun();
        var snapshot = CreateTurnSnapshot(run, run.RuntimeGeneration);
        var before = ProtocolJson.Serialize(run);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.CommitTurnPreparationAsync(
                    run,
                    snapshot.TurnId,
                    "attempt-1",
                    Array.Empty<NormalizedMessage>(),
                    snapshot,
                    Timestamp,
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(before, ProtocolJson.Serialize(run));
        Assert.Empty(publisher.Events);
    }

    private static JournalCoordinator CreateCoordinator(
        RecordingStore store,
        RecordingPublisher publisher)
    {
        return new JournalCoordinator(
            store,
            store,
            new Clock(),
            new Ids(),
            publisher);
    }

    private static AgentRun CreateRunningRun()
    {
        return new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Running,
            Revision = 5,
            RuntimeGeneration = 7,
            CreatedAt = Timestamp,
            UpdatedAt = Timestamp
        };
    }

    private static TurnSnapshot CreateTurnSnapshot(
        AgentRun run,
        long runtimeGeneration)
    {
        return new TurnSnapshot
        {
            TurnId = "turn-1",
            RunId = run.RunId,
            RuntimeGeneration = runtimeGeneration,
            ProviderId = "provider-1",
            ModelId = "model-1",
            PromptLayoutVersion = "1",
            StablePrefixHash = "stable-prefix",
            DirectToolDigest = "direct-tools",
            ContextPolicyVersion = "context-1",
            BudgetPolicyVersion = "budget-1",
            CreatedAt = Timestamp
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class Clock : IRuntimeClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }

    private sealed class Ids : IRuntimeIdGenerator
    {
        private int _value;

        public string NewId(string category)
        {
            return category + "-" + Interlocked.Increment(ref _value);
        }
    }

    private sealed class RecordingPublisher :
        INonBlockingRuntimeEventPublisher
    {
        public List<RuntimeEvent> Events { get; } = new();

        public void Publish(RuntimeEvent runtimeEvent)
        {
            Events.Add(runtimeEvent);
        }
    }

    private sealed class DeclaredReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly int _declaredCount;
        private readonly Func<int, T> _read;

        public DeclaredReadOnlyList(
            int declaredCount,
            Func<int, T> read)
        {
            _declaredCount = declaredCount;
            _read = read;
        }

        public int Count
        {
            get
            {
                CountReads++;
                return _declaredCount;
            }
        }

        public T this[int index]
        {
            get
            {
                IndexReads++;
                return _read(index);
            }
        }

        public int CountReads { get; private set; }

        public int IndexReads { get; private set; }

        public int EnumerationAttempts { get; private set; }

        public int DeclaredCount => _declaredCount;

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new NotSupportedException(
                "The trust-boundary collection cannot be enumerated.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class RecordingStore :
        IDurableSessionStore,
        IOperationLedger
    {
        public Func<long, JournalAppendResult>? SingleResultFactory
        {
            get;
            init;
        }

        public Func<long, int, IReadOnlyList<JournalAppendResult>>?
            BatchResultFactory
        {
            get;
            init;
        }

        public int LegacyAppendCalls { get; private set; }

        public int AtomicAppendCalls { get; private set; }

        public int BatchAppendCalls { get; private set; }

        public int WriteCalls =>
            LegacyAppendCalls + AtomicAppendCalls + BatchAppendCalls;

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LegacyAppendCalls++;
            return default;
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AtomicAppendCalls++;
            var baseRevision = expectedRunRevision ?? 0;
            return new ValueTask<JournalAppendResult>(
                SingleResultFactory?.Invoke(baseRevision)
                ?? new JournalAppendResult(
                    sequence: baseRevision,
                    revision: checked(baseRevision + 1),
                    wasDuplicate: false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchAppendCalls++;
            var baseRevision = expectedRunRevision ?? 0;
            var results = BatchResultFactory?.Invoke(
                              baseRevision,
                              runtimeEvents.Count)
                          ?? Enumerable.Range(0, runtimeEvents.Count)
                              .Select(
                                  index => new JournalAppendResult(
                                      sequence: checked(
                                          baseRevision + index),
                                      revision: checked(
                                          baseRevision + index + 1),
                                      wasDuplicate: false))
                              .ToArray();
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                results);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RuntimeEvent> events =
                Array.Empty<RuntimeEvent>();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(events);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(
                new RunJournalCursor(runId, 0, 0));
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<OperationLedgerEntry?>(
                result: null);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<OperationLedgerEntry> operations =
                Array.Empty<OperationLedgerEntry>();
            return new ValueTask<IReadOnlyList<OperationLedgerEntry>>(
                operations);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }
    }
}
