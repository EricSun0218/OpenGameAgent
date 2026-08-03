using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameAgent.Hosting;

public interface IRemoteTransportAuthorizer
{
    ValueTask<RemoteTransportIdentity?> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);
}

public static class RemoteActionEndpointExtensions
{
    public static IEndpointConventionBuilder MapGameAgentRemoteActionBridge(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/game-agent/v1/game-host")
    {
        if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
        return endpoints.Map(pattern, async context =>
        {
            var authorizer = context.RequestServices.GetRequiredService<IRemoteTransportAuthorizer>();
            var identity = await authorizer.AuthorizeAsync(context, context.RequestAborted).ConfigureAwait(false);
            if (identity is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var limiter = context.RequestServices.GetRequiredService<TenantRateLimiter>();
            var rate = limiter.TryAcquire(identity.TenantId, DateTimeOffset.UtcNow);
            if (!rate.Allowed)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(rate.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var broker = context.RequestServices.GetRequiredService<RemoteActionBroker>();
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            try
            {
                await broker.RunConnectionAsync(identity, socket, context.RequestAborted).ConfigureAwait(false);
            }
            catch (TenantCapacityExceededException)
            {
                if (socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await socket.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                        "route_capacity",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        });
    }
}
