using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeInspectorTests
{
    [Fact]
    public async Task FiltersClonedEventsWithoutNarrowingAnalysis()
    {
        var events = new[]
        {
            Event(0, RuntimeEventKinds.RunStarted),
            Event(1, RuntimeEventKinds.ProviderDispatchStarted),
            Event(2, RuntimeEventKinds.RunCompleted)
        };
        var inspector = new RuntimeInspector(new InspectionStore(events));

        var result = await inspector.InspectAsync(
            "run",
            new RuntimeInspectionQuery
            {
                AfterSequence = 0,
                EventKinds = new[] { RuntimeEventKinds.RunCompleted },
                MaxReturnedEvents = 10
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.TotalDurableEvents);
        Assert.Single(result.Events);
        Assert.Equal(2, result.Events[0].Sequence);
        Assert.False(result.Truncated);
        result.Events[0].Kind = "mutated";
        Assert.Equal(RuntimeEventKinds.RunCompleted, events[2].Kind);
    }

    [Fact]
    public async Task ExportUsesRedactionAndIntegrityDigest()
    {
        var sensitive = Event(0, RuntimeEventKinds.RunStarted);
        sensitive.Payload = ProtocolJson.ParseElement(
            "{\"apiKey\":\"do-not-export\",\"safe\":\"visible\"}");
        var inspector = new RuntimeInspector(
            new InspectionStore(new[] { sensitive }));

        var export = await inspector.ExportAsync("run", cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("do-not-export", export.JsonLines);
        Assert.Contains("visible", export.JsonLines);
        Assert.Equal(64, export.Digest.Length);
    }

    private static RuntimeEvent Event(long sequence, string kind) => new()
    {
        EventId = "event-" + sequence,
        RunId = "run",
        Sequence = sequence,
        Kind = kind,
        Durability = EventDurabilities.Durable,
        RuntimeGeneration = 1,
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
        Payload = ProtocolJson.ParseElement("{}")
    };

    private sealed class InspectionStore : ISessionStore
    {
        private readonly IReadOnlyList<RuntimeEvent> _events;

        internal InspectionStore(IReadOnlyList<RuntimeEvent> events)
        {
            _events = events;
        }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(_events);
        }
    }
}
