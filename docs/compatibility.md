# Compatibility

Compatibility is reported by evidence level. An adapter is not called verified
until its real engine or toolchain gate has passed.

| Component | Intended target | Current evidence |
| --- | --- | --- |
| Shared libraries | .NET Standard 2.1 | Built and tested with the .NET 8 SDK on Windows; CI covers Windows, Linux, and macOS |
| Godot host | Godot 4.7.1 .NET on Windows desktop and headless | Real Godot executable, isolated addon package, C# build, scene startup, signals, structured context, durable run/resume/control, main-thread action dispatch, and shutdown are exercised |
| Unity host | Unity 2022.3 LTS or newer, Mono or IL2CPP | Package source and samples compile as .NET Standard 2.1; host conformance and package assembly gates pass without an Editor |
| Unreal module | Unreal Engine 5 compatibility probe | Portable C++17 wire/ABI tests pass with warnings as errors; the module includes Editor automation tests but has not yet passed an Unreal Build Tool or Editor run |

## Unity validation boundary

The repository includes scripts for EditMode, PlayMode, Windows Mono Player, and
Windows IL2CPP Player gates. Those gates require a licensed Unity installation
with the matching platform modules and have not been executed for this alpha.
The current claim therefore covers the package contract and host implementation,
not verified Player compatibility. Mobile, WebGL, and console targets are not
claimed.

## Unreal validation boundary

The module defines strict bounded wire parsing, a GameThread dispatcher, host
routing, and a versioned C ABI surface. Portable tests validate those pieces
outside the engine. Packaging against a named Unreal Engine version, running
the automation suite in the Editor, and implementing a production transport
remain future work.

## Provider compatibility

The bundled adapter targets streaming chat-completions APIs that use the common
OpenAI-compatible event shape. Providers vary in authentication, streaming
details, tool-call behavior, and usage reporting. Each configured provider must
be exercised before shipping a game. A provider token belongs in a server relay
or a short-lived scoped credential for consumer builds.

Custom streaming providers should also implement
`IProviderRouteMetadataSource` with their exact model identifier and versioned
transport dialect. The runtime combines that metadata with the capability
snapshot used for the attempt and journals a deterministic route digest. If the
optional metadata interface is absent, the durable dispatch explicitly records
`unspecified` and `custom.streaming.v1`; it never guesses a fallback model.

Retries require trustworthy usage semantics. Explicitly rejected requests may be
declared known-zero. A failed attempt with reported usage is charged before the
next attempt. A dispatched attempt with unknown usage fails closed and is marked
as unaccounted in the durable run instead of silently moving to another provider.
The dispatch intent itself is journaled before provider code runs, so a process
crash before the usage callback is also detected during recovery.

## In-process callback boundary

The runtime cannot forcibly terminate arbitrary game or plugin code in the same
process. It instead bounds the damage: data-plane cancellation, lifecycle
cancellation, and synchronous event observers use independent process-wide
worker pools. A blocked callback retains its slot until it actually returns.
When a pool is full, new work is rejected or the live notification is dropped
and counted; no additional worker is created. Authoritative durable events
remain replayable from the journal.

Godot and Unity lifecycle owners reserve future cancellation capacity before
they can accept work. The current process-wide bound is 72 owners per engine
adapter: eight cancellation workers plus 64 queued owners. A reservation is
returned only after the real cancellation and owner-drain tasks finish, even
when the public shutdown wait has already timed out.

Operation reconciliation follows the same fail-closed rule. At most 64 queries
may be detached process-wide, and the same world/run/operation identity cannot
be queried twice concurrently. A cancelled caller may resume later, but an
uncooperative prior query must finish before that identity is admitted again.

## Headless lifecycle boundary

The compact headless loop does not resume an existing run. A run ID is
single-use within its session store: an active duplicate is rejected by the
runtime instance, and a completed ID is rejected when journal history is found.
Each admitted run owns an event sequence beginning at zero.

Each headless runtime instance also has a default in-process limit of 256 active
runs and 256 in-flight host actions. An action continues to occupy its slot
after timeout or cancellation until the host task actually finishes.
Cancellation callback cleanup is observed independently and cannot hold a
finished action slot. `HeadlessAgentRuntimeLimits` also bounds the observation
count, tool count, aggregate encoded input bytes, and JSON shape before provider
dispatch. Applications can lower these limits; engine adapters can enforce a
stricter host-facing limit as well. This boundary is fail-fast and is not a
distributed quota.

Each headless action is bounded by the smaller of its tool timeout and remaining
run deadline. Time spent persisting the write-ahead request consumes that same
absolute deadline; an expired request is failed without host dispatch. If the
in-flight cap is full, the request receives `action_capacity_exceeded` without
host dispatch. If a dispatched host action ignores cancellation, the call
returns `reconciling` with the operation ID; a late receipt is not silently
adopted by that call.

The history check and first append are not an atomic cross-process operation.
Multiple runtime instances that share a session store must use an external
run-ID admission mechanism, or use the durable runtime, when they can start the
same run concurrently.

## Versioning

The framework is pre-1.0. Protocol schemas carry explicit versions, but source
and wire compatibility may change between alpha releases. Pin exact package
versions and run the repository conformance fixtures when upgrading.
