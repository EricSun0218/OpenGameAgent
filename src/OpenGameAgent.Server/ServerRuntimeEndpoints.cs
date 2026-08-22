using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Runtime.Hosting;
using OpenGameAgent.Runtime.Protocol;

namespace OpenGameAgent.Server;

public static partial class ServerEndpoints
{
    private static void MapGameRuntimeEndpoints(
        IEndpointRouteBuilder endpoints,
        GameRuntimeServerState state,
        int maximumRequestBodyBytes)
    {
        endpoints.MapPost(
            "/runtime/v1/initialize",
            (HttpRequest request, CancellationToken cancellationToken) =>
                InitializeRuntimeAsync(request, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/runtime/v1/run/stream",
            (HttpRequest request, GameAgentRuntime runtime, HttpResponse response, CancellationToken cancellationToken) =>
                StreamRuntimeAsync(
                    request,
                    runtime,
                    state,
                    response,
                    maximumRequestBodyBytes,
                    cancellationToken));
        endpoints.MapPost(
            "/runtime/v1/events",
            (HttpRequest request, CancellationToken cancellationToken) =>
                ReadRuntimeEventsAsync(request, state, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/runtime/v1/control/steer",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                ControlRuntimeAsync(
                    request,
                    runtime,
                    steer: true,
                    maximumRequestBodyBytes,
                    cancellationToken));
        endpoints.MapPost(
            "/runtime/v1/control/interrupt",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                ControlRuntimeAsync(
                    request,
                    runtime,
                    steer: false,
                    maximumRequestBodyBytes,
                    cancellationToken));
    }

    private static async Task<IResult> InitializeRuntimeAsync(
        HttpRequest request,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadRequestDocumentAsync(
                request,
                maximumRequestBodyBytes,
                cancellationToken);
            EnsureRequestIsUnambiguous(document.RootElement);
            var value = GameRuntimeJson.Deserialize<GameRuntimeInitializeRequest>(
                document.RootElement.GetRawText());
            if (value.MinimumVersion > GameRuntimeProtocol.Version
                || value.MaximumVersion < GameRuntimeProtocol.Version)
            {
                return Results.Json(
                    new { error = "unsupported_protocol_version", supported = GameRuntimeProtocol.Version },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Text(
                GameRuntimeJson.Serialize(new GameRuntimeInitializeResponse(
                    GameRuntimeProtocol.Version,
                    GameRuntimeProtocol.Capabilities,
                    "OpenGameAgent.Server",
                    typeof(ServerEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0")),
                "application/json",
                Encoding.UTF8);
        }
        catch (Exception exception) when (IsRuntimeRequestError(exception))
        {
            return RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
        }
    }

    private static async Task StreamRuntimeAsync(
        HttpRequest request,
        GameAgentRuntime runtime,
        GameRuntimeServerState state,
        HttpResponse response,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        GameRuntimeStartRequest start;
        GameInput input;
        string? credential;
        try
        {
            using var document = await ReadRequestDocumentAsync(
                request,
                maximumRequestBodyBytes,
                cancellationToken);
            EnsureRequestIsUnambiguous(document.RootElement);
            credential = GetPresentedCredential(document.RootElement);
            start = ParseRuntimeRequest<GameRuntimeStartRequest>(document.RootElement, allowCredential: true);
            input = GameAgentWire.ParseInput(start.InputJson);
        }
        catch (Exception exception) when (IsRuntimeRequestError(exception))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(
                new { error = "invalid_request", message = exception.Message },
                cancellationToken);
            return;
        }

        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            request.HttpContext,
            credential,
            key,
            GameAgentServerOperation.Stream,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            await authenticationFailure.ExecuteAsync(request.HttpContext);
            return;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            request.HttpContext,
            key,
            GameAgentServerOperation.Stream,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            await authorizationFailure.ExecuteAsync(request.HttpContext);
            return;
        }

        var audience = await CreateAudienceProjectionAsync(request.HttpContext, key, cancellationToken);
        GameRuntimeServerRun run;
        try
        {
            run = state.GetOrStart(runtime, start, input);
        }
        catch (Exception exception) when (IsInvalidRequest(exception))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(
                new { error = "invalid_request", message = exception.Message },
                cancellationToken);
            return;
        }
        catch (InvalidOperationException exception)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await response.WriteAsJsonAsync(
                new { error = "runtime_capacity", message = exception.Message },
                cancellationToken);
            return;
        }

        var after = run.StartAfterSequence;
        var forcedGap = false;
        var lastEventId = request.Headers["Last-Event-ID"].ToString();
        if (!string.IsNullOrEmpty(lastEventId))
        {
            if (!GameRuntimeIds.TryReadEventSequence(lastEventId, out after)
                || !state.Events.IsKnownEvent(key, lastEventId, input.InputId))
            {
                after = run.StartAfterSequence;
                forcedGap = true;
            }
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        try
        {
            while (true)
            {
                var page = state.Events.Read(key, after, GameRuntimeProtocol.MaximumPageSize);
                if (forcedGap || page.Gap)
                {
                    await WriteRuntimeGapAsync(
                        response,
                        page.FirstRetainedSequence,
                        page.LastSequence,
                        cancellationToken);
                    forcedGap = false;
                }

                foreach (var stored in page.Events)
                {
                    after = stored.Sequence;
                    if (!string.Equals(stored.InputId, input.InputId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var projected = await state.ProjectAsync(stored, audience, cancellationToken);
                    if (projected is not null)
                    {
                        await WriteRuntimeEventAsync(response, projected, cancellationToken);
                    }

                    if (stored.Terminal)
                    {
                        return;
                    }
                }

                if (run.Completion.IsCompleted && page.LastSequence <= after)
                {
                    var terminalPage = state.Events.Read(key, after, GameRuntimeProtocol.MaximumPageSize);
                    if (terminalPage.Events.Count == 0)
                    {
                        await WriteRuntimeGapAsync(response, after + 1, after, cancellationToken);
                        return;
                    }

                    continue;
                }

                await state.Events.WaitForChangeAsync(key, after, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client disconnected. The run is deliberately not cancelled; reconnect with Last-Event-ID.
        }
        catch (IOException)
        {
            // A disconnected response stream must not cancel or remove the authoritative run.
        }
    }

    private static async Task<IResult> ReadRuntimeEventsAsync(
        HttpRequest request,
        GameRuntimeServerState state,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        GameRuntimeReadEventsRequest read;
        string? credential;
        try
        {
            using var document = await ReadRequestDocumentAsync(
                request,
                maximumRequestBodyBytes,
                cancellationToken);
            EnsureRequestIsUnambiguous(document.RootElement);
            credential = GetPresentedCredential(document.RootElement);
            read = ParseRuntimeRequest<GameRuntimeReadEventsRequest>(document.RootElement, allowCredential: true);
        }
        catch (Exception exception) when (IsRuntimeRequestError(exception))
        {
            return RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
        }

        var key = new GameSessionKey(read.SessionId, read.ActorId);
        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            request.HttpContext,
            credential,
            key,
            GameAgentServerOperation.Stream,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            request.HttpContext,
            key,
            GameAgentServerOperation.Stream,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var audience = await CreateAudienceProjectionAsync(request.HttpContext, key, cancellationToken);
        var page = state.Events.Read(key, read.AfterSequence, read.Maximum);
        var projected = new List<GameRuntimeEventEnvelope>();
        foreach (var value in page.Events)
        {
            var visible = await state.ProjectAsync(value, audience, cancellationToken);
            if (visible is not null)
            {
                projected.Add(visible);
            }
        }

        return Results.Text(
            GameRuntimeJson.Serialize(new GameRuntimeEventPage(
                page.SessionId,
                page.ActorId,
                page.RequestedAfterSequence,
                page.FirstRetainedSequence,
                page.LastSequence,
                page.NextAfterSequence,
                page.Gap,
                projected)),
            "application/json",
            Encoding.UTF8);
    }

    private static async Task<IResult> ControlRuntimeAsync(
        HttpRequest request,
        GameAgentRuntime runtime,
        bool steer,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        GameRuntimeControlRequest control;
        string? credential;
        try
        {
            using var document = await ReadRequestDocumentAsync(
                request,
                maximumRequestBodyBytes,
                cancellationToken);
            EnsureRequestIsUnambiguous(document.RootElement);
            credential = GetPresentedCredential(document.RootElement);
            control = ParseRuntimeRequest<GameRuntimeControlRequest>(document.RootElement, allowCredential: true);
            if (steer && control.MessageJson is null)
            {
                throw new ArgumentException("Steering requires messageJson.");
            }
        }
        catch (Exception exception) when (IsRuntimeRequestError(exception))
        {
            return RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
        }

        var key = new GameSessionKey(control.SessionId, control.ActorId);
        var operation = steer ? GameAgentServerOperation.Steer : GameAgentServerOperation.Abort;
        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            request.HttpContext,
            credential,
            key,
            operation,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            request.HttpContext,
            key,
            operation,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        AgentControlResult result;
        if (!string.Equals(
                control.ExpectedTurnId,
                GameRuntimeIds.Turn(control.ExpectedRunId, control.ExpectedTurn),
                StringComparison.Ordinal))
        {
            var active = runtime.ReadActiveRun(key);
            result = active is null
                ? new AgentControlResult(AgentControlStatus.Idle)
                : !string.Equals(active.RunId, control.ExpectedRunId, StringComparison.Ordinal)
                    ? new AgentControlResult(AgentControlStatus.RunMismatch, active)
                    : new AgentControlResult(AgentControlStatus.TurnMismatch, active);
        }
        else
        {
            result = steer
            ? runtime.TrySteer(
                key,
                AgentMessage.UserJson(control.MessageJson!),
                control.ExpectedRunId,
                control.ExpectedTurn)
            : runtime.TryAbort(key, control.ExpectedRunId, control.ExpectedTurn);
        }
        if (!steer && result.Accepted)
        {
            await runtime.WaitForIdleAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Results.Text(
            GameRuntimeJson.Serialize(ProjectControl(result)),
            "application/json",
            Encoding.UTF8);
    }

    private static GameRuntimeControlResponse ProjectControl(AgentControlResult result) => new(
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

    private static T ParseRuntimeRequest<T>(JsonElement root, bool allowCredential)
    {
        if (!allowCredential || !root.EnumerateObject().Any(static property =>
                string.Equals(property.Name, "credential", StringComparison.OrdinalIgnoreCase)))
        {
            return GameRuntimeJson.Deserialize<T>(root.GetRawText());
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "credential", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return GameRuntimeJson.Deserialize<T>(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static async Task WriteRuntimeEventAsync(
        HttpResponse response,
        GameRuntimeEventEnvelope value,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync("id: " + value.EventId + "\n", cancellationToken);
        await response.WriteAsync("event: runtime\n", cancellationToken);
        await response.WriteAsync("data: " + GameRuntimeJson.Serialize(value) + "\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteRuntimeGapAsync(
        HttpResponse response,
        long firstRetainedSequence,
        long lastSequence,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync("event: gap\n", cancellationToken);
        await response.WriteAsync(
            "data: " + JsonSerializer.Serialize(new
            {
                firstRetainedSequence,
                lastSequence,
                requiresTranscriptReconciliation = true,
            }) + "\n\n",
            cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static bool IsRuntimeRequestError(Exception exception) =>
        exception is ArgumentException
            or JsonException
            or RequestBodyTooLargeException
            or UnsupportedRequestContentTypeException;
}

internal sealed class GameRuntimeServerState
{
    private const int MaximumRuns = 10_000;
    private const int MaximumSourcesPerSession = 10_000;
    private readonly object _gate = new();
    private readonly Dictionary<RunKey, GameRuntimeServerRun> _runs = new();
    private readonly Queue<RunKey> _completedRuns = new();
    private readonly Dictionary<GameSessionKey, Dictionary<string, EventSource>> _sources = new();
    private readonly Dictionary<GameSessionKey, Queue<string>> _sourceOrder = new();

    internal GameRuntimeServerState()
    {
        Events = new InMemoryGameRuntimeEventJournal(
            maximumSessions: 10_000,
            maximumEventsPerSession: MaximumSourcesPerSession);
    }

    internal InMemoryGameRuntimeEventJournal Events { get; }

    internal GameRuntimeServerRun GetOrStart(
        GameAgentRuntime runtime,
        GameRuntimeStartRequest request,
        GameInput input)
    {
        var key = new RunKey(input.SessionId, input.ActorId, input.InputId);
        lock (_gate)
        {
            if (_runs.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.RequestId, request.RequestId, StringComparison.Ordinal)
                    || !string.Equals(existing.InputJson, request.InputJson, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A runtime input ID cannot be reused with a different request.");
                }

                return existing;
            }

            while (_runs.Count >= MaximumRuns && _completedRuns.Count > 0)
            {
                _runs.Remove(_completedRuns.Dequeue());
            }

            if (_runs.Count >= MaximumRuns)
            {
                throw new InvalidOperationException("The runtime run registry is full.");
            }

            var startAfterSequence = Events.Read(
                new GameSessionKey(input.SessionId, input.ActorId),
                afterSequence: 0,
                maximum: 1).LastSequence;
            var run = new GameRuntimeServerRun(
                request.RequestId,
                request.InputJson,
                startAfterSequence);
            _runs.Add(key, run);
            run.Completion = ExecuteAsync(runtime, input, run, key);
            return run;
        }
    }

    internal async ValueTask<GameRuntimeEventEnvelope?> ProjectAsync(
        GameRuntimeEventEnvelope stored,
        GameAgentAudienceProjection? audience,
        CancellationToken cancellationToken)
    {
        EventSource? source = null;
        lock (_gate)
        {
            _sources.TryGetValue(new GameSessionKey(stored.SessionId, stored.ActorId), out var values);
            values?.TryGetValue(stored.EventId, out source);
        }

        if (source is null)
        {
            return null;
        }

        string? payload;
        if (source.AgentEvent is not null)
        {
            payload = audience is null
                ? GameAgentWire.SerializeEvent(source.AgentEvent)
                : await audience.ProjectEventAsync(source.AgentEvent, cancellationToken);
        }
        else if (source.Result is not null)
        {
            payload = audience is null
                ? GameAgentWire.SerializeResult(source.Result)
                : await audience.ProjectResultAsync(source.Result, cancellationToken);
        }
        else
        {
            payload = source.SafePayloadJson;
        }

        return payload is null ? null : Clone(stored, payload);
    }

    private async Task<GameAgentRunResult?> ExecuteAsync(
        GameAgentRuntime runtime,
        GameInput input,
        GameRuntimeServerRun run,
        RunKey runKey)
    {
        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var projector = new GameRuntimeAgentEventProjector(key, input.InputId);
        try
        {
            var result = await runtime.RunAsync(
                input,
                (_, agentEvent, _) =>
                {
                    var published = Events.Publish(projector.Project(agentEvent, "{}"));
                    StoreSources(key, published, EventSource.Agent(agentEvent));
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
            var terminal = Events.Publish(GameRuntimeEventProjection.ProjectResult(input, result, "{}"));
            StoreSources(key, terminal, EventSource.ForResult(result));
            return result;
        }
        catch
        {
            var terminal = Events.Publish(new GameRuntimeEventDraft(
                key,
                input.InputId,
                GameRuntimeEventKind.Result,
                GameRuntimeLifecycle.Completed,
                "result_failed",
                "{\"error\":\"run_failed\"}",
                runtime.ReadActiveRun(key)?.RunId,
                terminal: true));
            StoreSources(key, terminal, EventSource.Safe("{\"error\":\"run_failed\"}"));
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _completedRuns.Enqueue(runKey);
            }
        }
    }

    private void StoreSources(
        GameSessionKey key,
        IReadOnlyList<GameRuntimeEventEnvelope> published,
        EventSource source)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(key, out var values))
            {
                values = new Dictionary<string, EventSource>(StringComparer.Ordinal);
                _sources.Add(key, values);
                _sourceOrder.Add(key, new Queue<string>());
            }

            var order = _sourceOrder[key];
            foreach (var value in published)
            {
                var effective = value.Name == "item_interrupted"
                    ? EventSource.Safe(value.PayloadJson)
                    : source;
                values[value.EventId] = effective;
                order.Enqueue(value.EventId);
            }

            while (order.Count > MaximumSourcesPerSession)
            {
                values.Remove(order.Dequeue());
            }
        }
    }

    private static GameRuntimeEventEnvelope Clone(GameRuntimeEventEnvelope value, string payloadJson) => new(
        value.ProtocolVersion,
        value.EventId,
        value.Sequence,
        value.OccurredAt,
        value.SessionId,
        value.ActorId,
        value.InputId,
        value.EventKind,
        value.Lifecycle,
        value.Name,
        payloadJson,
        value.RunId,
        value.Turn,
        value.TurnId,
        value.ItemId,
        value.ItemKind,
        value.Terminal);

    private readonly record struct RunKey(string SessionId, string ActorId, string InputId);

    private sealed class EventSource
    {
        private EventSource(AgentEvent? agentEvent, GameAgentRunResult? result, string? safePayloadJson)
        {
            AgentEvent = agentEvent;
            Result = result;
            SafePayloadJson = safePayloadJson;
        }

        internal AgentEvent? AgentEvent { get; }
        internal GameAgentRunResult? Result { get; }
        internal string? SafePayloadJson { get; }

        internal static EventSource Agent(AgentEvent value) => new(value, null, null);
        internal static EventSource ForResult(GameAgentRunResult value) => new(null, value, null);
        internal static EventSource Safe(string value) => new(null, null, value);
    }
}

internal sealed class GameRuntimeServerRun
{
    internal GameRuntimeServerRun(string requestId, string inputJson, long startAfterSequence)
    {
        if (startAfterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startAfterSequence));
        }

        RequestId = requestId;
        InputJson = inputJson;
        StartAfterSequence = startAfterSequence;
    }

    internal string RequestId { get; }
    internal string InputJson { get; }
    internal long StartAfterSequence { get; }
    internal Task<GameAgentRunResult?> Completion { get; set; } = Task.FromResult<GameAgentRunResult?>(null);
}
