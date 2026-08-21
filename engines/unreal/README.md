# OpenGameAgent for Unreal Engine 5.8

This optional native C++ plugin connects Unreal Engine to an OpenGameAgent sidecar or trusted server over the public JSON/SSE protocol. It does not embed a CLR or provider credential in the Unreal process.

Copy `Plugins/OpenGameAgent` into the Unreal project's `Plugins` directory, enable the plugin, and obtain `UOpenGameAgentSubsystem` from the game instance. Call `ConfigureRemote`, then submit canonical `GameAgentWire` JSON through `RunJson`. The native C++ subsystem is the stable adapter surface; its `UFUNCTION` and multicast-delegate annotations provide a thin Blueprint projection of the same API.

`RunJson` preserves arbitrary structured payloads, floating-point values, game timeline/tick fields, metadata, and supported content. `SteerActor` injects a structured observation into an active actor, `AbortActor` stops the authoritative run, and `CancelRun` only cancels the local HTTP caller. All delegates are delivered on the Unreal game thread.

For authoritative world mutations, use `ClaimActions` or `StartActionStream`, reconcile each returned operation against the Unreal save/operation ledger, execute or resume it on the game thread, and call `SubmitActionReceiptJson`. `ReconcileAction` reads the durable exchange state after a restart or uncertain delivery. Action responses arrive through `OnActionResponse`; streamed deliveries arrive through `OnActionStreamEvent`. Stopping a delivery stream does not settle or cancel an action.

`ReadUsage`, `ReadTranscript`, and `ReadImageAttachment` expose the server's authorized, bounded read APIs through `OnQueryResponse`. Transcript cursors remain opaque and revision-bound. Attachment metadata can be read with the transcript; attachment bytes are fetched explicitly. `ReadServerCapabilities` lets a client inspect the currently deployed server surface without coupling gameplay code to a server build.

The sidecar owns the agent runtime, providers, persistence, and durable action exchange. Unreal remains authoritative for gameplay. A world mutation should be claimed through the action exchange, reconciled against the game's operation ledger, executed on the game thread, and answered with a final receipt. Do not treat streamed text or cancellation as proof that a mutation committed or did not commit.

Plain HTTP is accepted by default only for `localhost`, `127.0.0.1`, or `[::1]`. Remote services should use HTTPS and short-lived player/session credentials. A permanent model-provider key must not ship in the game client.

The adapter does not depend on Blueprint as its implementation layer. Early Unreal Engine 6 builds can continue using the C++ and Blueprint surfaces; a future Verse projection can bind the same native subsystem and wire protocol without moving the agent runtime into the engine process.

Run the real-editor build and automation gate with:

```powershell
./engines/unreal/test-plugin.ps1 -UnrealRoot '<UE_5.8>'
```
