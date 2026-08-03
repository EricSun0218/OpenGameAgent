# Deployment and remote hosting

The same Agent Runtime supports three placements. Placement changes transport
and credential ownership; it does not change tool contracts or move game rules
into the framework.

| Placement | Use it when | Durable store | Action path |
| --- | --- | --- | --- |
| Engine embedded | offline, BYOK, lowest latency | file; SQLite on compatible .NET 8 hosts | direct main-thread game host |
| Game server embedded | authoritative multiplayer or funded model use | PostgreSQL recommended | direct server game host |
| Sidecar/service | independent scaling or process isolation | PostgreSQL recommended | authenticated WebSocket receipt bridge |

`GameAgent.Hosting` supplies lifecycle health, bounded tenant admission,
rate limiting, circuit breaking, a kill switch, replay cursors, and the remote
action broker. `GameAgent.Remote.Client` is `netstandard2.1` and runs in Godot
or Unity. It receives action requests, executes an `IGameHost`, and sends the
authoritative receipt back. Several actions may be in flight concurrently;
duplicate `operationId` values share one local execution.

The bridge covers the action authority boundary, not a game's complete network
API. A hosted game defines its own authenticated endpoints for observations,
run creation, saves, matchmaking, and player sessions, then invokes the normal
runtime APIs. This keeps account and game protocol choices out of the framework.

## Server setup

```csharp
builder.Services.AddGameAgentHosting(
    configureAdmission: value => value.MaxConcurrentRunsPerTenant = 8,
    configureRemoteActions: value => value.MaxPendingActionsPerConnection = 64);
builder.Services.AddSingleton<IRemoteTransportAuthorizer, GameTokenAuthorizer>();

var app = builder.Build();
app.UseWebSockets();
app.MapHealthChecks("/health/ready");
app.MapGameAgentRemoteActionBridge();
```

The authorizer must derive `tenantId` and `worldId` from validated game
authentication. Do not accept those identities merely because the client put
them in a query string. No permissive authorizer is registered by default.

Bind a runtime's game host to one connected route:

```csharp
var identity = new RemoteTransportIdentity(tenantId, worldId);
var gameHost = new RemoteGameHost(
    broker.CreateChannel(identity),
    runtimeClock);
```

If delivery occurred but the socket closed before a receipt arrived,
`RemoteGameHost` produces an `unknown` receipt. Reconcile that operation with
the game before retrying it.

## Engine client

```csharp
var connector = new RemoteGameHostClient(new RemoteGameHostClientOptions
{
    Endpoint = new Uri("wss://game.example/game-agent/v1/game-host"),
    TenantId = tenantId,
    WorldId = worldId,
    BearerToken = shortLivedGameToken,
    MaxConcurrentActions = 16
});
await connector.RunAsync(engineMainThreadGameHost, shutdownToken);
```

Plain `ws` is rejected except on loopback. Treat the bearer token as a
short-lived, route-scoped credential. The connector never persists it.

## Storage

- `GameAgent.Storage.Sqlite` is for one local process. WAL, full synchronous
  commits, busy timeout, CAS revisions, operation receipts, and restart are
  covered by the contract suite.
- `GameAgent.Storage.Postgres` uses `NpgsqlDataSource` and row locks for
  multi-instance writers. Use a unique `NamespaceId` per environment or game
  partition. Call `InitializeAsync` during deployment startup before accepting
  traffic; v0.2 creates its isolated tables and indexes idempotently.
- The relational adapters target .NET 8. They are server/Godot-.NET options,
  not assemblies bundled into the Unity package.
- The original file journal remains appropriate for simple embedded games.

Both relational adapters implement `IDurableSessionStore` and
`IOperationLedger`; changing the adapter does not change runtime code.

## Operational rules

1. Apply the kill switch before planned maintenance or credential compromise.
2. Reject work when tenant admission or rate limits are full; do not grow an
   unbounded queue.
3. Export the closed runtime metrics and health checks.
4. Drain hosted services and runtime journals during shutdown.
5. Keep permanent commercial provider credentials on a server or gateway.
6. Do not treat a disconnected client as evidence that its last action failed.
