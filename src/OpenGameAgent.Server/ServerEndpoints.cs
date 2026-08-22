using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Runtime.Hosting;

namespace OpenGameAgent.Server;

public static partial class ServerEndpoints
{
    public const int DefaultMaximumRequestBodyBytes = 8_000_000;

    public static IApplicationBuilder UseOpenGameAgentApiKey(
        this IApplicationBuilder app,
        string? apiKey,
        string headerName = "Authorization",
        string scheme = "Bearer")
    {
        ArgumentNullException.ThrowIfNull(app);

        if (string.IsNullOrEmpty(apiKey))
        {
            return app;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A configured API key cannot contain only whitespace.", nameof(apiKey));
        }

        if (apiKey.Length > 65_536)
        {
            throw new ArgumentException("A configured API key cannot exceed 65536 characters.", nameof(apiKey));
        }

        if (!IsValidHeaderName(headerName) || headerName.Length > 256)
        {
            throw new ArgumentException("A valid API key header name is required.", nameof(headerName));
        }

        if (apiKey.Contains('\r')
            || apiKey.Contains('\n')
            || apiKey.Contains('\0')
            || (scheme?.Contains('\r') ?? false)
            || (scheme?.Contains('\n') ?? false)
            || (scheme?.Contains('\0') ?? false)
            || (scheme?.Length ?? 0) > 256)
        {
            throw new ArgumentException("API key credentials contain invalid characters or exceed their size limit.", nameof(apiKey));
        }

        var expected = string.IsNullOrWhiteSpace(scheme) ? apiKey : scheme + " " + apiKey;
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1/run")
                && !context.Request.Path.StartsWithSegments("/v1/control")
                && !context.Request.Path.StartsWithSegments("/v1/actions")
                && !context.Request.Path.StartsWithSegments("/v1/usage")
                && !context.Request.Path.StartsWithSegments("/v1/transcript")
                && !context.Request.Path.StartsWithSegments("/v1/approvals")
                && !context.Request.Path.StartsWithSegments("/v1/attachments")
                && !context.Request.Path.StartsWithSegments("/v1/health")
                && !context.Request.Path.StartsWithSegments("/runtime/v1"))
            {
                await next(context);
                return;
            }

            var supplied = context.Request.Headers[headerName].ToString();
            if (!FixedTimeEquals(supplied, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (context.User.Identity?.IsAuthenticated != true)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "server-api-key") },
                    "OpenGameAgent.ApiKey"));
            }

            await next(context);
        });
    }

    private static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage();
            return request.Headers.TryAddWithoutValidation(name, "value");
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static IEndpointRouteBuilder MapOpenGameAgent(
        this IEndpointRouteBuilder endpoints,
        int maximumRequestBodyBytes = DefaultMaximumRequestBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (maximumRequestBodyBytes < 2 || maximumRequestBodyBytes > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequestBodyBytes));
        }

        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
        endpoints.MapGet(
            "/v1/health",
            async (HttpContext context, CancellationToken cancellationToken) =>
            {
                var monitor = context.RequestServices.GetService<IGameRuntimeHealthMonitor>();
                var snapshot = monitor is null
                    ? new GameRuntimeHealthSnapshot(
                        GameRuntimeComponentState.Ready,
                        DateTimeOffset.UtcNow,
                        new[]
                        {
                            new GameRuntimeComponentHealth(
                                GameRuntimeComponentKind.Runtime,
                                "agent-runtime",
                                required: true,
                                GameRuntimeComponentState.Ready,
                                DateTimeOffset.UtcNow,
                                elapsedMilliseconds: 0),
                        })
                    : await monitor.ReadAsync(cancellationToken);
                return Results.Ok(new
                {
                    state = snapshot.State.ToString(),
                    snapshot.CheckedAt,
                    components = snapshot.Components.Select(component => new
                    {
                        kind = component.Kind.ToString(),
                        component.Name,
                        component.Required,
                        state = component.State.ToString(),
                        component.CheckedAt,
                        component.ElapsedMilliseconds,
                        component.DiagnosticCode,
                        component.Detail,
                    }),
                });
            });
        var runtime = new GameRuntimeServerState();
        MapGameRuntimeEndpoints(endpoints, runtime, maximumRequestBodyBytes);
        endpoints.MapGet("/v1/capabilities", () => Results.Ok(new
        {
            name = "OpenGameAgent",
            protocolVersion = "1",
            transports = new[] { "json", "sse" },
            input = new[] { "text", "json", "resource-reference", "image" },
            routes = new[] { "quick", "agent", "workflow" },
            execution = new[] { "in-process", "server" },
            control = new[] { "steer", "abort" },
            audience = new[] { "internal", "owner", "public", "recipient" },
            actions = new[] { "claim", "stream", "receipt", "reconcile" },
            usage = new[] { "session-ledger", "by-cause", "itemized-cost" },
            transcript = new[] { "bounded-pages", "revision-bound-cursors", "attachment-metadata" },
            attachments = new[] { "content-addressed-images", "session-authorized-read" },
            approvals = new[] { "owner-authorized-pending", "one-time-response", "world-bound-consumption" },
            health = new[] { "component-snapshot", "bounded-probes", "server-client" },
        }));
        endpoints.MapPost(
            "/v1/run",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                RunAsync(request, runtime, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/run/stream",
            (HttpRequest request, GameAgentRuntime runtime, HttpResponse response, CancellationToken cancellationToken) =>
                StreamAsync(request, runtime, response, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/control/steer",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                SteerAsync(request, runtime, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/control/abort",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                AbortAsync(request, runtime, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/usage",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                ReadUsageAsync(request, runtime, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/transcript",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                ReadTranscriptAsync(request, runtime, maximumRequestBodyBytes, cancellationToken));
        endpoints.MapPost(
            "/v1/attachments/read",
            (HttpRequest request, GameAgentRuntime runtime, CancellationToken cancellationToken) =>
                ReadAttachmentAsync(request, runtime, maximumRequestBodyBytes, cancellationToken));
        MapGameActionExchangeEndpoints(endpoints, maximumRequestBodyBytes);
        MapGameToolApprovalEndpoints(endpoints, maximumRequestBodyBytes);
        return endpoints;
    }

    private static async Task<IResult> ReadAttachmentAsync(
        HttpRequest httpRequest,
        GameAgentRuntime runtime,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        AttachmentReadRequest request;
        GameSessionKey key;
        try
        {
            using var requestDocument = await ReadRequestDocumentAsync(
                httpRequest,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<AttachmentReadRequest>(requestDocument.RootElement);
            key = request.ToKey();
            request.EnsureValid();
        }
        catch (RequestBodyTooLargeException exception)
        {
            return RequestError(StatusCodes.Status413PayloadTooLarge, "request_too_large", exception.Message);
        }
        catch (UnsupportedRequestContentTypeException exception)
        {
            return RequestError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
        }

        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            httpRequest.HttpContext,
            request.Credential,
            key,
            GameAgentServerOperation.ReadAttachment,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            httpRequest.HttpContext,
            key,
            GameAgentServerOperation.ReadAttachment,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var stored = await runtime.ReadImageAttachmentAsync(key, request.AttachmentId, cancellationToken);
        return stored is null
            ? Results.NotFound(new { error = "attachment_not_found" })
            : Results.Json(new
            {
                attachment = new
                {
                    attachmentId = stored.Attachment.AttachmentId,
                    mediaType = stored.Attachment.MediaType,
                    bytes = stored.Attachment.Bytes,
                    width = stored.Attachment.Width,
                    height = stored.Attachment.Height,
                    name = stored.Attachment.Name,
                },
                data = Convert.ToBase64String(stored.Data.ToArray()),
            });
    }

    private static async Task<IResult> ReadUsageAsync(
        HttpRequest httpRequest,
        GameAgentRuntime runtime,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ControlRequest request;
        GameSessionKey key;
        try
        {
            using var requestDocument = await ReadRequestDocumentAsync(
                httpRequest,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<ControlRequest>(requestDocument.RootElement);
            key = request.ToKey();
        }
        catch (RequestBodyTooLargeException exception)
        {
            return RequestError(StatusCodes.Status413PayloadTooLarge, "request_too_large", exception.Message);
        }
        catch (UnsupportedRequestContentTypeException exception)
        {
            return RequestError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return Results.Json(
                new { error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            httpRequest.HttpContext,
            request.Credential,
            key,
            GameAgentServerOperation.ReadUsage,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            httpRequest.HttpContext,
            key,
            GameAgentServerOperation.ReadUsage,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var usage = await runtime.ReadUsageAsync(key, cancellationToken);
        return usage is null
            ? Results.NotFound(new { error = "session_not_found" })
            : Results.Text(GameAgentWire.SerializeUsage(usage), "application/json", Encoding.UTF8);
    }

    private static async Task<IResult> ReadTranscriptAsync(
        HttpRequest httpRequest,
        GameAgentRuntime runtime,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        TranscriptReadRequest request;
        GameSessionKey key;
        try
        {
            using var requestDocument = await ReadRequestDocumentAsync(
                httpRequest,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<TranscriptReadRequest>(requestDocument.RootElement);
            key = request.ToKey();
            request.EnsureValid();
        }
        catch (RequestBodyTooLargeException exception)
        {
            return RequestError(StatusCodes.Status413PayloadTooLarge, "request_too_large", exception.Message);
        }
        catch (UnsupportedRequestContentTypeException exception)
        {
            return RequestError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return RequestError(StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
        }

        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            httpRequest.HttpContext,
            request.Credential,
            key,
            GameAgentServerOperation.ReadTranscript,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            httpRequest.HttpContext,
            key,
            GameAgentServerOperation.ReadTranscript,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        try
        {
            var page = await runtime.ReadTranscriptAsync(
                key,
                request.PageSize,
                request.Cursor,
                cancellationToken);
            if (page is null)
            {
                return Results.NotFound(new { error = "session_not_found" });
            }

            var projection = await CreateAudienceProjectionAsync(
                httpRequest.HttpContext,
                key,
                cancellationToken);
            var json = projection is null
                ? GameAgentWire.SerializeTranscriptPage(page)
                : await projection.ProjectTranscriptAsync(page, cancellationToken);
            return Results.Text(json, "application/json", Encoding.UTF8);
        }
        catch (GameSessionTranscriptChangedException exception)
        {
            return RequestError(StatusCodes.Status409Conflict, "transcript_changed", exception.Message);
        }
        catch (GameSessionTranscriptPageTooLargeException exception)
        {
            return RequestError(StatusCodes.Status413PayloadTooLarge, "transcript_message_too_large", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return RequestError(StatusCodes.Status400BadRequest, "invalid_cursor", exception.Message);
        }
    }

    private static async Task<IResult> SteerAsync(
        HttpRequest httpRequest,
        GameAgentRuntime runtime,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ControlRequest request;
        GameSessionKey key;
        try
        {
            using var requestDocument = await ReadRequestDocumentAsync(
                httpRequest,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<ControlRequest>(requestDocument.RootElement);
            key = request.ToKey();
        }
        catch (RequestBodyTooLargeException exception)
        {
            return RequestError(StatusCodes.Status413PayloadTooLarge, "request_too_large", exception.Message);
        }
        catch (UnsupportedRequestContentTypeException exception)
        {
            return RequestError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException
            or AgentLimitException
            or GameRuntimeLimitException
            or JsonException)
        {
            return Results.Json(
                new { accepted = false, error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            httpRequest.HttpContext,
            request.Credential,
            key,
            GameAgentServerOperation.Steer,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            httpRequest.HttpContext,
            key,
            GameAgentServerOperation.Steer,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        try
        {
            var accepted = runtime.TrySteer(key, AgentMessage.UserJson(request.GetPayloadJson()));
            return accepted
                ? Results.Ok(new { accepted = true })
                : Results.NotFound(new { accepted = false, error = "actor_not_running" });
        }
        catch (Exception exception) when (exception is ArgumentException
            or AgentLimitException
            or GameRuntimeLimitException
            or JsonException)
        {
            return Results.Json(
                new { accepted = false, error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> AbortAsync(
        HttpRequest httpRequest,
        GameAgentRuntime runtime,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        ControlRequest request;
        GameSessionKey key;
        try
        {
            using var requestDocument = await ReadRequestDocumentAsync(
                httpRequest,
                maximumRequestBodyBytes,
                cancellationToken);
            request = ParseRequest<ControlRequest>(requestDocument.RootElement);
            key = request.ToKey();
        }
        catch (RequestBodyTooLargeException exception)
        {
            return RequestError(StatusCodes.Status413PayloadTooLarge, "request_too_large", exception.Message);
        }
        catch (UnsupportedRequestContentTypeException exception)
        {
            return RequestError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return Results.Json(
                new { accepted = false, error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            httpRequest.HttpContext,
            request.Credential,
            key,
            GameAgentServerOperation.Abort,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            httpRequest.HttpContext,
            key,
            GameAgentServerOperation.Abort,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        try
        {
            var accepted = runtime.TryAbort(key);
            return accepted
                ? Results.Ok(new { accepted = true })
                : Results.NotFound(new { accepted = false, error = "actor_not_running" });
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return Results.Json(
                new { accepted = false, error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> RunAsync(
        HttpRequest httpRequest,
        GameAgentRuntime runtime,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        GameInput input;
        string? credential;
        try
        {
            using var requestDocument = await ReadRequestDocumentAsync(
                httpRequest,
                maximumRequestBodyBytes,
                cancellationToken);
            EnsureRequestIsUnambiguous(requestDocument.RootElement);
            credential = GetPresentedCredential(requestDocument.RootElement);
            input = GameAgentWire.ParseInput(requestDocument.RootElement.GetRawText());
        }
        catch (RequestBodyTooLargeException exception)
        {
            return RequestError(StatusCodes.Status413PayloadTooLarge, "request_too_large", exception.Message);
        }
        catch (UnsupportedRequestContentTypeException exception)
        {
            return RequestError(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or GameRuntimeLimitException or JsonException)
        {
            return Results.Json(
                new { error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            httpRequest.HttpContext,
            credential,
            key,
            GameAgentServerOperation.Run,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            httpRequest.HttpContext,
            key,
            GameAgentServerOperation.Run,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var audienceProjection = await CreateAudienceProjectionAsync(
            httpRequest.HttpContext,
            key,
            cancellationToken);

        Task<GameAgentRunResult> pendingRun;
        try
        {
            pendingRun = runtime.RunAsync(input, cancellationToken);
        }
        catch (Exception exception) when (IsInvalidRequest(exception))
        {
            return Results.Json(
                new { error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await pendingRun;
        var resultJson = audienceProjection is null
            ? GameAgentWire.SerializeResult(result)
            : await audienceProjection.ProjectResultAsync(result, cancellationToken);
        return Results.Text(resultJson, "application/json", Encoding.UTF8);
    }

    private static async Task StreamAsync(
        HttpRequest httpRequest,
        GameAgentRuntime runtime,
        HttpResponse response,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        GameInput input;
        string? credential;
        try
        {
            using var requestDocument = await ReadRequestDocumentAsync(
                httpRequest,
                maximumRequestBodyBytes,
                cancellationToken);
            EnsureRequestIsUnambiguous(requestDocument.RootElement);
            credential = GetPresentedCredential(requestDocument.RootElement);
            input = GameAgentWire.ParseInput(requestDocument.RootElement.GetRawText());
        }
        catch (RequestBodyTooLargeException exception)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await response.WriteAsJsonAsync(
                new { error = "request_too_large", message = exception.Message },
                cancellationToken);
            return;
        }
        catch (UnsupportedRequestContentTypeException exception)
        {
            response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await response.WriteAsJsonAsync(
                new { error = "unsupported_media_type", message = exception.Message },
                cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is ArgumentException or GameRuntimeLimitException or JsonException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(
                new { error = "invalid_request", message = exception.Message },
                cancellationToken);
            return;
        }

        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var authenticationFailure = await AuthenticatePresentedCredentialAsync(
            httpRequest.HttpContext,
            credential,
            key,
            GameAgentServerOperation.Stream,
            cancellationToken);
        if (authenticationFailure is not null)
        {
            await authenticationFailure.ExecuteAsync(httpRequest.HttpContext);
            return;
        }

        var authorizationFailure = await GetAuthorizationFailureAsync(
            httpRequest.HttpContext,
            key,
            GameAgentServerOperation.Stream,
            cancellationToken);
        if (authorizationFailure is not null)
        {
            await authorizationFailure.ExecuteAsync(httpRequest.HttpContext);
            return;
        }

        var audienceProjection = await CreateAudienceProjectionAsync(
            httpRequest.HttpContext,
            key,
            cancellationToken);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        Task<GameAgentRunResult> pendingRun;
        try
        {
            pendingRun = runtime.RunAsync(
                input,
                async (_, agentEvent, token) =>
                {
                    var eventJson = audienceProjection is null
                        ? GameAgentWire.SerializeEvent(agentEvent)
                        : await audienceProjection.ProjectEventAsync(agentEvent, token);
                    if (eventJson is not null)
                    {
                        await WriteEventAsync(response, "agent", eventJson, token);
                    }
                },
                cancellationToken);
        }
        catch (Exception exception) when (IsInvalidRequest(exception))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            response.ContentType = "application/json";
            response.Headers.Remove("Cache-Control");
            response.Headers.Remove("Connection");
            await response.WriteAsJsonAsync(
                new { error = "invalid_request", message = exception.Message },
                cancellationToken);
            return;
        }

        try
        {
            var result = await pendingRun;
            var resultJson = audienceProjection is null
                ? GameAgentWire.SerializeResult(result)
                : await audienceProjection.ProjectResultAsync(result, cancellationToken);
            await WriteEventAsync(response, "result", resultJson, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await WriteEventAsync(
                response,
                "error",
                "{\"error\":\"run_failed\"}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        string eventName,
        string json,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync("event: " + eventName + "\n", cancellationToken);
        await response.WriteAsync("data: " + json + "\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private static bool IsInvalidRequest(Exception exception) =>
        exception is ArgumentException or AgentLimitException or GameRuntimeLimitException or JsonException;

    private static IResult RequestError(int statusCode, string error, string message) =>
        Results.Json(new { error, message }, statusCode: statusCode);

    private static string? GetPresentedCredential(JsonElement root)
    {
        if (!root.TryGetProperty("credential", out var value))
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "credential", StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    break;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("The presented credential must be a string.", nameof(root));
        }

        return value.GetString();
    }

    private static async ValueTask<IResult?> AuthenticatePresentedCredentialAsync(
        HttpContext httpContext,
        string? credential,
        GameSessionKey key,
        GameAgentServerOperation operation,
        CancellationToken cancellationToken)
    {
        if (credential is null)
        {
            return null;
        }

        GameAgentPresentedCredentialContext credentialContext;
        try
        {
            credentialContext = new GameAgentPresentedCredentialContext(credential, key, operation);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var authenticator = httpContext.RequestServices.GetService<IGameAgentPresentedCredentialAuthenticator>();
        if (authenticator is null)
        {
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var principal = await authenticator.AuthenticateAsync(credentialContext, cancellationToken);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        httpContext.User = principal;
        return null;
    }

    private static async ValueTask<IResult?> GetAuthorizationFailureAsync(
        HttpContext httpContext,
        GameSessionKey key,
        GameAgentServerOperation operation,
        CancellationToken cancellationToken)
    {
        var authorizer = httpContext.RequestServices.GetService<IGameAgentOwnerAuthorizer>();
        if (authorizer is null)
        {
            return null;
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Json(
                new { error = "unauthorized" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var allowed = await authorizer.AuthorizeAsync(
            new GameAgentAuthorizationContext(httpContext.User, key, operation),
            cancellationToken);
        return allowed
            ? null
            : Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static async ValueTask<GameAgentAudienceProjection?> CreateAudienceProjectionAsync(
        HttpContext httpContext,
        GameSessionKey key,
        CancellationToken cancellationToken)
    {
        var policy = httpContext.RequestServices.GetService<IGameAgentAudiencePolicy>();
        if (policy is null)
        {
            return null;
        }

        var viewer = await policy.ResolveViewerAsync(httpContext.User, key, cancellationToken)
            ?? throw new InvalidOperationException("The game audience policy returned no viewer.");
        return new GameAgentAudienceProjection(policy, viewer, key);
    }

    private static async Task<JsonDocument> ReadRequestDocumentAsync(
        HttpRequest request,
        int maximumRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            throw new UnsupportedRequestContentTypeException();
        }

        if (request.ContentLength > maximumRequestBodyBytes)
        {
            throw new RequestBodyTooLargeException(maximumRequestBodyBytes);
        }

        var initialCapacity = request.ContentLength is > 0
            ? (int)Math.Min(request.ContentLength.Value, maximumRequestBodyBytes)
            : Math.Min(4096, maximumRequestBodyBytes);
        using var body = new MemoryStream(initialCapacity);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(81_920, maximumRequestBodyBytes + 1));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = maximumRequestBodyBytes - body.Length;
                var requested = (int)Math.Min(buffer.Length, remaining + 1);
                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (body.Length + read > maximumRequestBodyBytes)
                {
                    throw new RequestBodyTooLargeException(maximumRequestBodyBytes);
                }

                body.Write(buffer, 0, read);
            }

            if (body.Length == 0)
            {
                throw new JsonException("The request body is empty.");
            }

            return JsonDocument.Parse(
                body.ToArray(),
                new JsonDocumentOptions { MaxDepth = 128 });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static T ParseRequest<T>(JsonElement document)
    {
        EnsureRequestIsUnambiguous(document);
        return JsonSerializer.Deserialize<T>(document.GetRawText(), RequestJsonOptions)
            ?? throw new ArgumentException("The request body is empty.", nameof(document));
    }

    private static void EnsureRequestIsUnambiguous(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The request must contain a JSON object.", nameof(root));
        }

        EnsureObject(root, StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureNestedIsUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            EnsureObject(value, StringComparer.Ordinal);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureNestedIsUnambiguous(item);
            }
        }
    }

    private static void EnsureObject(JsonElement value, StringComparer comparer)
    {
        var names = new HashSet<string>(comparer);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new ArgumentException("The request cannot contain duplicate JSON properties.", nameof(value));
            }

            EnsureNestedIsUnambiguous(property.Value);
        }
    }

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class RequestBodyTooLargeException : Exception
    {
        public RequestBodyTooLargeException(int maximumRequestBodyBytes)
            : base($"The request body exceeded {maximumRequestBodyBytes} bytes.")
        {
        }
    }

    private sealed class UnsupportedRequestContentTypeException : Exception
    {
        public UnsupportedRequestContentTypeException()
            : base("The request content type must be application/json.")
        {
        }
    }

}

public sealed class ControlRequest
{
    public string? Credential { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }

    public GameSessionKey ToKey() => new(SessionId, ActorId);

    public string GetPayloadJson() =>
        Payload.ValueKind == JsonValueKind.Undefined ? "{}" : Payload.GetRawText();
}

public sealed class AttachmentReadRequest
{
    public string? Credential { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public string AttachmentId { get; set; } = string.Empty;

    public GameSessionKey ToKey() => new(SessionId, ActorId);

    public void EnsureValid()
    {
        _ = ToKey();
        if (string.IsNullOrWhiteSpace(AttachmentId)
            || AttachmentId.Length > 256
            || AttachmentId.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("A bounded attachment ID is required.", nameof(AttachmentId));
        }
    }
}

public sealed class TranscriptReadRequest
{
    public string? Credential { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public int PageSize { get; set; } = 50;

    public string? Cursor { get; set; }

    public GameSessionKey ToKey() => new(SessionId, ActorId);

    public void EnsureValid()
    {
        _ = ToKey();
        if (PageSize is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }

        if (Cursor is { Length: > 256 } || Cursor?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The transcript cursor is invalid.", nameof(Cursor));
        }
    }
}
