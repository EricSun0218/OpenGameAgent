using GameAgent.Protocol;

namespace GameAgent.Hosting;

public sealed class AgentEventReplayOptions
{
    public int MaxRoutes { get; set; } = 4_096;
    public int CapacityPerRoute { get; set; } = 1_024;

    internal AgentEventReplayOptions Snapshot()
    {
        if (MaxRoutes is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRoutes));
        }
        if (CapacityPerRoute is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(CapacityPerRoute));
        }
        return new AgentEventReplayOptions { MaxRoutes = MaxRoutes, CapacityPerRoute = CapacityPerRoute };
    }
}

public sealed class AgentEventCursorExpiredException : InvalidOperationException
{
    public AgentEventCursorExpiredException(long oldestAvailableSequence)
        : base("The requested event cursor has expired; rebuild from durable state.")
    {
        OldestAvailableSequence = oldestAvailableSequence;
    }

    public long OldestAvailableSequence { get; }
}

public sealed class AgentEventReplayBuffer
{
    private readonly AgentEventReplayOptions _options;
    private readonly AgentTransportCodec _codec;
    private readonly Dictionary<string, RouteBuffer> _routes = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public AgentEventReplayBuffer(
        AgentEventReplayOptions? options = null,
        AgentTransportCodec? codec = null)
    {
        _options = (options ?? new AgentEventReplayOptions()).Snapshot();
        _codec = codec ?? new AgentTransportCodec();
    }

    public AgentTransportEnvelope Publish(string routeId, AgentTransportEnvelope envelope)
    {
        ValidateRoute(routeId);
        ArgumentNullException.ThrowIfNull(envelope);
        _codec.Validate(envelope);
        lock (_sync)
        {
            if (!_routes.TryGetValue(routeId, out var route))
            {
                if (_routes.Count >= _options.MaxRoutes)
                {
                    throw new TenantCapacityExceededException("max_event_routes", "The live event replay route limit is full.");
                }
                route = new RouteBuffer(_options.CapacityPerRoute);
                _routes.Add(routeId, route);
            }

            var stored = Clone(envelope);
            stored.Sequence = route.NextSequence++;
            route.Events.Enqueue(stored);
            while (route.Events.Count > _options.CapacityPerRoute)
            {
                route.Events.Dequeue();
            }
            return Clone(stored);
        }
    }

    public IReadOnlyList<AgentTransportEnvelope> ReadAfter(string routeId, long sequence, int maximumItems = 256)
    {
        ValidateRoute(routeId);
        if (sequence < -1 || maximumItems is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(sequence < -1 ? nameof(sequence) : nameof(maximumItems));
        }

        lock (_sync)
        {
            if (!_routes.TryGetValue(routeId, out var route) || route.Events.Count == 0)
            {
                return Array.Empty<AgentTransportEnvelope>();
            }

            var oldest = route.Events.Peek().Sequence;
            if (sequence < oldest - 1)
            {
                throw new AgentEventCursorExpiredException(oldest);
            }

            return route.Events.Where(value => value.Sequence > sequence).Take(maximumItems).Select(Clone).ToArray();
        }
    }

    public bool RemoveRoute(string routeId)
    {
        ValidateRoute(routeId);
        lock (_sync)
        {
            return _routes.Remove(routeId);
        }
    }

    private static void ValidateRoute(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId) || routeId.Length > 512)
        {
            throw new ArgumentException("A bounded route ID is required.", nameof(routeId));
        }
    }

    private static AgentTransportEnvelope Clone(AgentTransportEnvelope value) => new()
    {
        Version = value.Version,
        MessageId = value.MessageId,
        Type = value.Type,
        TenantId = value.TenantId,
        WorldId = value.WorldId,
        RunId = value.RunId,
        CorrelationId = value.CorrelationId,
        Sequence = value.Sequence,
        Payload = value.Payload.Clone()
    };

    private sealed class RouteBuffer
    {
        public RouteBuffer(int capacity) => Events = new Queue<AgentTransportEnvelope>(capacity);
        public Queue<AgentTransportEnvelope> Events { get; }
        public long NextSequence { get; set; }
    }
}
