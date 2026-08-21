using System.Text.Json;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.Server;

public static partial class ServerEndpoints
{
    private static void MapGameToolApprovalEndpoints(
        IEndpointRouteBuilder endpoints,
        int maximumRequestBodyBytes)
    {
        endpoints.MapPost(
            "/v1/approvals/pending",
            (HttpContext context, CancellationToken cancellationToken) =>
                ListPendingApprovalsAsync(context, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/approvals/respond",
            (HttpContext context, CancellationToken cancellationToken) =>
                RespondToApprovalAsync(context, maximumRequestBodyBytes, cancellationToken));
    }

    private static async Task<IResult> ListPendingApprovalsAsync(
        HttpContext context,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ApprovalListRequest request;
        GameSessionKey key;
        try
        {
            using var document = await ReadRequestDocumentAsync(context.Request, maximumRequestBodyBytes, cancellationToken);
            request = ParseRequest<ApprovalListRequest>(document.RootElement);
            key = request.ToKey();
            if (request.Limit < 1 || request.Limit > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(request.Limit));
            }
        }
        catch (Exception exception) when (IsApprovalRequestFailure(exception))
        {
            return ApprovalRequestFailure(exception);
        }

        var failure = await AuthenticateAndAuthorizeApprovalAsync(
            context,
            request.Credential,
            key,
            GameAgentServerOperation.ReadToolApprovals,
            cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var broker = context.RequestServices.GetService<IGameToolApprovalBroker>();
        if (broker is null)
        {
            return ApprovalUnavailable();
        }

        var pending = await broker.ListPendingAsync(key, request.Limit, cancellationToken);
        return Results.Json(new { approvals = pending.Select(ToApprovalDocument).ToArray() });
    }

    private static async Task<IResult> RespondToApprovalAsync(
        HttpContext context,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ApprovalResponseRequest request;
        GameSessionKey key;
        GameToolApprovalResponseKind responseKind;
        try
        {
            using var document = await ReadRequestDocumentAsync(context.Request, maximumRequestBodyBytes, cancellationToken);
            request = ParseRequest<ApprovalResponseRequest>(document.RootElement);
            key = request.ToKey();
            if (!Enum.TryParse(request.Response, ignoreCase: true, out responseKind)
                || !Enum.IsDefined(typeof(GameToolApprovalResponseKind), responseKind))
            {
                throw new ArgumentException("The approval response must be approve or deny.", nameof(request.Response));
            }
        }
        catch (Exception exception) when (IsApprovalRequestFailure(exception))
        {
            return ApprovalRequestFailure(exception);
        }

        var failure = await AuthenticateAndAuthorizeApprovalAsync(
            context,
            request.Credential,
            key,
            GameAgentServerOperation.RespondToolApproval,
            cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var broker = context.RequestServices.GetService<IGameToolApprovalBroker>();
        if (broker is null)
        {
            return ApprovalUnavailable();
        }

        try
        {
            var updated = await broker.RespondAsync(
                new GameToolApprovalResponse(key, request.ApprovalId, request.ExpectedRevision, responseKind, request.Reason),
                cancellationToken);
            return Results.Json(new { approval = ToApprovalDocument(updated) });
        }
        catch (KeyNotFoundException exception)
        {
            return RequestError(StatusCodes.Status404NotFound, "approval_not_found", exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return RequestError(StatusCodes.Status403Forbidden, "forbidden", "The approval belongs to another owner.");
        }
        catch (InvalidOperationException exception)
        {
            return RequestError(StatusCodes.Status409Conflict, "approval_conflict", exception.Message);
        }
    }

    private static async ValueTask<IResult?> AuthenticateAndAuthorizeApprovalAsync(
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
        return authenticationFailure ?? await GetAuthorizationFailureAsync(context, key, operation, cancellationToken);
    }

    private static object ToApprovalDocument(GameToolApprovalRecord value) => new
    {
        approvalId = value.Request.ApprovalId,
        policyId = value.Request.PolicyId,
        sessionId = value.Request.SessionId,
        actorId = value.Request.ActorId,
        inputId = value.Request.InputId,
        runId = value.Request.RunId,
        turn = value.Request.Turn,
        toolCallId = value.Request.ToolCallId,
        toolName = value.Request.ToolName,
        risk = value.Request.Risk.ToString(),
        arguments = ParseApprovalArguments(value.Request.CanonicalArgumentsJson),
        argumentsDigest = value.Request.ArgumentsDigest,
        timelineId = value.Request.Moment.TimelineId,
        tick = value.Request.Moment.Tick,
        generationId = value.Request.World.GenerationId,
        worldRevision = value.Request.World.Revision,
        taskId = value.Request.TaskId,
        requestedAt = value.Request.RequestedAt,
        expiresAt = value.Request.ExpiresAt,
        status = value.Status.ToString(),
        revision = value.Revision,
        updatedAt = value.UpdatedAt,
        reason = value.Reason,
    };

    private static JsonElement ParseApprovalArguments(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static bool IsApprovalRequestFailure(Exception exception) =>
        exception is RequestBodyTooLargeException
            or UnsupportedRequestContentTypeException
            or ArgumentException
            or JsonException
            or GameRuntimeLimitException;

    private static IResult ApprovalRequestFailure(Exception exception) => exception switch
    {
        RequestBodyTooLargeException => RequestError(StatusCodes.Status413PayloadTooLarge, "request_too_large", exception.Message),
        UnsupportedRequestContentTypeException => RequestError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", exception.Message),
        _ => RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message),
    };

    private static IResult ApprovalUnavailable() => RequestError(
        StatusCodes.Status501NotImplemented,
        "approval_broker_unavailable",
        "The host did not configure a tool approval broker.");

    private sealed class ApprovalListRequest
    {
        public string? Credential { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public int Limit { get; set; } = 32;
        public GameSessionKey ToKey() => new(SessionId, ActorId);
    }

    private sealed class ApprovalResponseRequest
    {
        public string? Credential { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public string ApprovalId { get; set; } = string.Empty;
        public long ExpectedRevision { get; set; }
        public string Response { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public GameSessionKey ToKey() => new(SessionId, ActorId);
    }
}
