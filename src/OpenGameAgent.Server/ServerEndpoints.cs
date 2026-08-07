using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Server;

public static class ServerEndpoints
{
    public static IApplicationBuilder UseOpenGameAgentApiKey(
        this IApplicationBuilder app,
        string? apiKey,
        string headerName = "Authorization",
        string scheme = "Bearer")
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return app;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A configured API key cannot contain only whitespace.", nameof(apiKey));
        }

        if (!IsValidHeaderName(headerName))
        {
            throw new ArgumentException("A valid API key header name is required.", nameof(headerName));
        }

        if (apiKey.Contains('\r')
            || apiKey.Contains('\n')
            || (scheme?.Contains('\r') ?? false)
            || (scheme?.Contains('\n') ?? false))
        {
            throw new ArgumentException("API key credentials cannot contain line breaks.", nameof(apiKey));
        }

        var expected = string.IsNullOrWhiteSpace(scheme) ? apiKey : scheme + " " + apiKey;
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1/run")
                && !context.Request.Path.StartsWithSegments("/v1/control"))
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

    public static IEndpointRouteBuilder MapOpenGameAgent(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
        endpoints.MapGet("/v1/capabilities", () => Results.Ok(new
        {
            name = "OpenGameAgent",
            protocolVersion = "1",
            transports = new[] { "json", "sse" },
            input = new[] { "text", "json", "resource-reference" },
            routes = new[] { "quick", "agent", "workflow" },
            execution = new[] { "in-process", "server" },
            control = new[] { "steer", "abort" },
        }));
        endpoints.MapPost("/v1/run", RunAsync);
        endpoints.MapPost("/v1/run/stream", StreamAsync);
        endpoints.MapPost("/v1/control/steer", Steer);
        endpoints.MapPost("/v1/control/abort", Abort);
        return endpoints;
    }

    private static IResult Steer(JsonElement requestDocument, GameAgentRuntime runtime)
    {
        try
        {
            var request = ParseRequest<ControlRequest>(requestDocument);
            var accepted = runtime.TrySteer(
                request.ToKey(),
                AgentMessage.UserJson(request.GetPayloadJson()));
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

    private static IResult Abort(JsonElement requestDocument, GameAgentRuntime runtime)
    {
        try
        {
            var request = ParseRequest<ControlRequest>(requestDocument);
            var accepted = runtime.TryAbort(request.ToKey());
            return accepted
                ? Results.Ok(new { accepted = true })
                : Results.NotFound(new { accepted = false, error = "actor_not_running" });
        }
        catch (ArgumentException exception)
        {
            return Results.Json(
                new { accepted = false, error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> RunAsync(
        JsonElement requestDocument,
        GameAgentRuntime runtime,
        CancellationToken cancellationToken)
    {
        GameInput input;
        try
        {
            var request = ParseRequest<RunRequest>(requestDocument);
            input = request.ToInput();
        }
        catch (Exception exception) when (exception is ArgumentException or GameRuntimeLimitException or JsonException)
        {
            return Results.Json(
                new { error = "invalid_request", message = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

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
        return Results.Text(GameAgentWire.SerializeResult(result), "application/json", Encoding.UTF8);
    }

    private static async Task StreamAsync(
        JsonElement requestDocument,
        GameAgentRuntime runtime,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        GameInput input;
        try
        {
            var request = ParseRequest<RunRequest>(requestDocument);
            input = request.ToInput();
        }
        catch (Exception exception) when (exception is ArgumentException or GameRuntimeLimitException or JsonException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(
                new { error = "invalid_request", message = exception.Message },
                cancellationToken);
            return;
        }

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
                    await WriteEventAsync(response, "agent", GameAgentWire.SerializeEvent(agentEvent), token);
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

        var result = await pendingRun;
        await WriteEventAsync(response, "result", GameAgentWire.SerializeResult(result), cancellationToken);
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
        using var algorithm = SHA256.Create();
        var suppliedHash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private static bool IsInvalidRequest(Exception exception) =>
        exception is ArgumentException or AgentLimitException or GameRuntimeLimitException or JsonException;

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

}

public sealed class RunRequest
{
    public string? InputId { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }

    public string TimelineId { get; set; } = "default";

    public long Tick { get; set; }

    public JsonElement? Calendar { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }

    public GameInput ToInput() => new(
        SessionId,
        ActorId,
        Type,
        Payload.ValueKind == JsonValueKind.Undefined ? "{}" : Payload.GetRawText(),
        new GameMoment(
            TimelineId,
            Tick,
            Calendar is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } calendar
                ? calendar.GetRawText()
                : null),
        InputId,
        Metadata);
}

public sealed class ControlRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }

    public GameSessionKey ToKey() => new(SessionId, ActorId);

    public string GetPayloadJson() =>
        Payload.ValueKind == JsonValueKind.Undefined ? "{}" : Payload.GetRawText();
}
