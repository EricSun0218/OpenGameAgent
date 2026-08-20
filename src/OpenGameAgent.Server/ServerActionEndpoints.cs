using System.Text.Json;

namespace OpenGameAgent.Server;

public static partial class ServerEndpoints
{
    private static void MapGameActionExchangeEndpoints(
        IEndpointRouteBuilder endpoints,
        int maximumRequestBodyBytes)
    {
        endpoints.MapPost(
            "/v1/actions/claim",
            (HttpContext context, CancellationToken cancellationToken) =>
                ClaimActionsAsync(context, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/actions/stream",
            (HttpContext context, CancellationToken cancellationToken) =>
                StreamActionsAsync(context, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/actions/receipt",
            (HttpContext context, CancellationToken cancellationToken) =>
                SubmitActionReceiptAsync(context, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/actions/reconcile",
            (HttpContext context, CancellationToken cancellationToken) =>
                ReconcileActionAsync(context, maximumRequestBodyBytes, cancellationToken));
    }

    private static async Task<IResult> ClaimActionsAsync(
        HttpContext context,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ActionClaimRequest request;
        GameSessionKey key;
        try
        {
            using var document = await ReadRequestDocumentAsync(
                context.Request,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<ActionClaimRequest>(document.RootElement);
            key = request.ToKey();
        }
        catch (Exception exception) when (IsActionRequestFailure(exception))
        {
            return ActionRequestFailure(exception);
        }

        var failure = await AuthenticateAndAuthorizeActionAsync(
            context,
            request.Credential,
            key,
            GameAgentServerOperation.ClaimActions,
            cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var exchange = context.RequestServices.GetService<GameActionExchange>();
        if (exchange is null)
        {
            return ActionExchangeUnavailable();
        }

        try
        {
            var deliveries = await exchange.ClaimPendingAsync(key, request.Limit, cancellationToken);
            return Results.Json(new { actions = deliveries.Select(ToActionDocument).ToArray() });
        }
        catch (Exception exception) when (exception is ArgumentException or GameRuntimeLimitException)
        {
            return RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
        }
    }

    private static async Task StreamActionsAsync(
        HttpContext context,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ActionClaimRequest request;
        GameSessionKey key;
        try
        {
            using var document = await ReadRequestDocumentAsync(
                context.Request,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<ActionClaimRequest>(document.RootElement);
            key = request.ToKey();
        }
        catch (Exception exception) when (IsActionRequestFailure(exception))
        {
            await ActionRequestFailure(exception).ExecuteAsync(context);
            return;
        }

        var failure = await AuthenticateAndAuthorizeActionAsync(
            context,
            request.Credential,
            key,
            GameAgentServerOperation.StreamActions,
            cancellationToken);
        if (failure is not null)
        {
            await failure.ExecuteAsync(context);
            return;
        }

        var exchange = context.RequestServices.GetService<GameActionExchange>();
        if (exchange is null)
        {
            await ActionExchangeUnavailable().ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        try
        {
            await foreach (var delivery in exchange.StreamPendingAsync(key, request.Limit, cancellationToken))
            {
                await WriteEventAsync(
                    context.Response,
                    "action",
                    JsonSerializer.Serialize(ToActionDocument(delivery), ActionJsonOptions),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<IResult> SubmitActionReceiptAsync(
        HttpContext context,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ActionReceiptRequest request;
        GameSessionKey key;
        GameActionReceipt receipt;
        try
        {
            using var document = await ReadRequestDocumentAsync(
                context.Request,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<ActionReceiptRequest>(document.RootElement);
            key = request.ToKey();
            receipt = request.ToReceipt();
        }
        catch (Exception exception) when (IsActionRequestFailure(exception))
        {
            return ActionRequestFailure(exception);
        }

        var failure = await AuthenticateAndAuthorizeActionAsync(
            context,
            request.Credential,
            key,
            GameAgentServerOperation.SubmitActionReceipt,
            cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var exchange = context.RequestServices.GetService<GameActionExchange>();
        if (exchange is null)
        {
            return ActionExchangeUnavailable();
        }

        try
        {
            var stored = await exchange.SubmitReceiptAsync(
                key,
                request.ExpectedRevision,
                request.GenerationId,
                receipt,
                cancellationToken);
            return Results.Json(new { receipt = ToReceiptDocument(stored) });
        }
        catch (KeyNotFoundException exception)
        {
            return RequestError(StatusCodes.Status404NotFound, "operation_not_found", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return RequestError(StatusCodes.Status409Conflict, "receipt_rejected", exception.Message);
        }
    }

    private static async Task<IResult> ReconcileActionAsync(
        HttpContext context,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ActionReconcileRequest request;
        GameSessionKey key;
        try
        {
            using var document = await ReadRequestDocumentAsync(
                context.Request,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<ActionReconcileRequest>(document.RootElement);
            key = request.ToKey();
            RequireActionIdentifier(request.OperationId, nameof(request.OperationId));
        }
        catch (Exception exception) when (IsActionRequestFailure(exception))
        {
            return ActionRequestFailure(exception);
        }

        var failure = await AuthenticateAndAuthorizeActionAsync(
            context,
            request.Credential,
            key,
            GameAgentServerOperation.ReconcileAction,
            cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var exchange = context.RequestServices.GetService<GameActionExchange>();
        if (exchange is null)
        {
            return ActionExchangeUnavailable();
        }

        try
        {
            var state = await exchange.ReconcileAsync(key, request.OperationId, cancellationToken);
            return state is null
                ? RequestError(StatusCodes.Status404NotFound, "operation_not_found", "The action operation does not exist.")
                : Results.Json(ToActionStateDocument(state));
        }
        catch (InvalidOperationException exception)
        {
            return RequestError(StatusCodes.Status409Conflict, "operation_mismatch", exception.Message);
        }
    }

    private static async ValueTask<IResult?> AuthenticateAndAuthorizeActionAsync(
        HttpContext context,
        string? credential,
        GameSessionKey key,
        GameAgentServerOperation operation,
        CancellationToken cancellationToken)
    {
        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            context,
            credential,
            key,
            operation,
            cancellationToken);
        return authenticationFailure ?? await GetAuthorizationFailureAsync(
            context,
            key,
            operation,
            cancellationToken);
    }

    private static object ToActionDocument(GameActionDelivery delivery) => ToIntentDocument(
        delivery.Intent,
        delivery.RequiresReconciliation);

    private static object ToIntentDocument(GameActionIntent intent, bool requiresReconciliation) => new
    {
        operationId = intent.OperationId,
        sessionId = intent.SessionId,
        actorId = intent.ActorId,
        inputId = intent.InputId,
        action = intent.Action,
        arguments = ParseJsonElement(intent.ArgumentsJson),
        timelineId = intent.Moment.TimelineId,
        tick = intent.Moment.Tick,
        calendar = intent.Moment.CalendarJson is null ? (JsonElement?)null : ParseJsonElement(intent.Moment.CalendarJson),
        generationId = intent.GenerationId,
        conflictKey = intent.ConflictKey,
        expectedRevision = intent.ExpectedRevision,
        requiresReconciliation,
    };

    private static object ToReceiptDocument(GameActionReceipt receipt) => new
    {
        operationId = receipt.OperationId,
        status = receipt.Status.ToString().ToLowerInvariant(),
        result = ParseJsonElement(receipt.ResultJson),
        timelineId = receipt.Moment.TimelineId,
        tick = receipt.Moment.Tick,
        calendar = receipt.Moment.CalendarJson is null ? (JsonElement?)null : ParseJsonElement(receipt.Moment.CalendarJson),
        stateRevision = receipt.StateRevision,
        code = receipt.Code,
        message = receipt.Message,
    };

    private static object ToActionStateDocument(GameActionExchangeState state) => new
    {
        status = state.Status.ToString().ToLowerInvariant(),
        action = ToIntentDocument(state.Intent, state.RequiresReconciliation),
        receipt = state.Receipt is null ? null : ToReceiptDocument(state.Receipt),
    };

    private static bool IsActionRequestFailure(Exception exception) =>
        exception is RequestBodyTooLargeException
            or UnsupportedRequestContentTypeException
            or ArgumentException
            or JsonException
            or GameRuntimeLimitException;

    private static IResult ActionRequestFailure(Exception exception) => exception switch
    {
        RequestBodyTooLargeException => RequestError(
            StatusCodes.Status413PayloadTooLarge,
            "request_too_large",
            exception.Message),
        UnsupportedRequestContentTypeException => RequestError(
            StatusCodes.Status415UnsupportedMediaType,
            "unsupported_media_type",
            exception.Message),
        _ => RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message),
    };

    private static IResult ActionExchangeUnavailable() => RequestError(
        StatusCodes.Status501NotImplemented,
        "action_exchange_unavailable",
        "The host did not configure an external action exchange.");

    private static string RequireActionIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16_384)
        {
            throw new ArgumentException("A non-empty action identifier of at most 16384 characters is required.", parameterName);
        }

        return value;
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static readonly JsonSerializerOptions ActionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ActionClaimRequest
    {
        public string? Credential { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public int Limit { get; set; } = 32;

        public GameSessionKey ToKey() => new(SessionId, ActorId);
    }

    private sealed class ActionReconcileRequest
    {
        public string? Credential { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string OperationId { get; set; } = string.Empty;

        public GameSessionKey ToKey() => new(SessionId, ActorId);
    }

    private sealed class ActionReceiptRequest
    {
        public string? Credential { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string OperationId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public JsonElement Result { get; set; }

        public string TimelineId { get; set; } = string.Empty;

        public long Tick { get; set; }

        public JsonElement Calendar { get; set; }

        public string? GenerationId { get; set; }

        public long? ExpectedRevision { get; set; }

        public long? StateRevision { get; set; }

        public string? Code { get; set; }

        public string? Message { get; set; }

        public GameSessionKey ToKey() => new(SessionId, ActorId);

        public GameActionReceipt ToReceipt()
        {
            if (!Enum.TryParse<GameActionStatus>(Status, ignoreCase: true, out var status)
                || !Enum.IsDefined(typeof(GameActionStatus), status)
                || status == GameActionStatus.Uncertain)
            {
                throw new ArgumentException("The receipt status must be committed, rejected, or failed.", nameof(Status));
            }

            var resultJson = Result.ValueKind == JsonValueKind.Undefined ? "{}" : Result.GetRawText();
            var calendarJson = Calendar.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : Calendar.GetRawText();
            return new GameActionReceipt(
                OperationId,
                status,
                resultJson,
                new GameMoment(TimelineId, Tick, calendarJson),
                StateRevision,
                Code,
                Message);
        }
    }
}
