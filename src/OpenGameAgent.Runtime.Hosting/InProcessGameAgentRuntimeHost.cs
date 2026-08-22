using OpenGameAgent.Kernel;
using OpenGameAgent.Runtime.Protocol;

namespace OpenGameAgent.Runtime.Hosting;

public delegate ValueTask GameRuntimeEventHandler(
    GameRuntimeEventEnvelope value,
    CancellationToken cancellationToken);

public sealed class InProcessGameAgentRuntimeHost
{
    private readonly GameAgentRuntime _runtime;
    private readonly InMemoryGameRuntimeEventJournal _events;

    public InProcessGameAgentRuntimeHost(
        GameAgentRuntime runtime,
        InMemoryGameRuntimeEventJournal? events = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _events = events ?? new InMemoryGameRuntimeEventJournal();
    }

    public GameRuntimeInitializeResponse Initialize(GameRuntimeInitializeRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.MinimumVersion > GameRuntimeProtocol.Version
            || request.MaximumVersion < GameRuntimeProtocol.Version)
        {
            throw new NotSupportedException("No compatible OpenGameAgent Runtime Protocol version is available.");
        }

        return new GameRuntimeInitializeResponse(
            GameRuntimeProtocol.Version,
            GameRuntimeProtocol.Capabilities,
            "OpenGameAgent",
            typeof(InProcessGameAgentRuntimeHost).Assembly.GetName().Version?.ToString() ?? "0.0.0");
    }

    public async Task<GameAgentRunResult> RunAsync(
        GameRuntimeStartRequest request,
        GameRuntimeEventHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var input = GameAgentWire.ParseInput(request.InputJson);
        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var projector = new GameRuntimeAgentEventProjector(key, input.InputId);
        var result = await _runtime.RunAsync(
            input,
            async (_, agentEvent, token) =>
            {
                var draft = projector.Project(agentEvent, GameAgentWire.SerializeEvent(agentEvent));
                foreach (var value in _events.Publish(draft))
                {
                    if (handler is not null)
                    {
                        await handler(value, token).ConfigureAwait(false);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var value in _events.Publish(GameRuntimeEventProjection.ProjectResult(
                     input,
                     result,
                     GameAgentWire.SerializeResult(result))))
        {
            if (handler is not null)
            {
                await handler(value, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return result;
    }

    public GameRuntimeEventPage ReadEvents(
        string sessionId,
        string actorId,
        long afterSequence = 0,
        int maximum = 256) =>
        _events.Read(new GameSessionKey(sessionId, actorId), afterSequence, maximum);

    public ValueTask<long> WaitForEventsAsync(
        string sessionId,
        string actorId,
        long afterSequence,
        CancellationToken cancellationToken) =>
        _events.WaitForChangeAsync(
            new GameSessionKey(sessionId, actorId),
            afterSequence,
            cancellationToken);

    public GameRuntimeControlResponse Steer(GameRuntimeControlRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.MessageJson is null)
        {
            throw new ArgumentException("Steering requires a serialized AgentMessage.", nameof(request));
        }

        if (!string.Equals(
                request.ExpectedTurnId,
                GameRuntimeIds.Turn(request.ExpectedRunId, request.ExpectedTurn),
                StringComparison.Ordinal))
        {
            return new GameRuntimeControlResponse(GameRuntimeControlStatus.TurnMismatch);
        }

        var message = AgentMessage.UserJson(request.MessageJson);
        var result = _runtime.TrySteer(
            new GameSessionKey(request.SessionId, request.ActorId),
            message,
            request.ExpectedRunId,
            request.ExpectedTurn);
        return Project(result);
    }

    public GameRuntimeControlResponse Interrupt(GameRuntimeControlRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!string.Equals(
                request.ExpectedTurnId,
                GameRuntimeIds.Turn(request.ExpectedRunId, request.ExpectedTurn),
                StringComparison.Ordinal))
        {
            return new GameRuntimeControlResponse(GameRuntimeControlStatus.TurnMismatch);
        }

        var result = _runtime.TryAbort(
            new GameSessionKey(request.SessionId, request.ActorId),
            request.ExpectedRunId,
            request.ExpectedTurn);
        return Project(result);
    }

    private static GameRuntimeControlResponse Project(AgentControlResult result) => new(
        result.Status switch
        {
            AgentControlStatus.Accepted => GameRuntimeControlStatus.Accepted,
            AgentControlStatus.Idle => GameRuntimeControlStatus.Idle,
            AgentControlStatus.RunNotStarted => GameRuntimeControlStatus.RunNotStarted,
            AgentControlStatus.RunMismatch => GameRuntimeControlStatus.RunMismatch,
            AgentControlStatus.TurnMismatch => GameRuntimeControlStatus.TurnMismatch,
            _ => GameRuntimeControlStatus.ControlClosed,
        },
        result.ActiveRun?.RunId,
        result.ActiveRun?.Turn);
}
