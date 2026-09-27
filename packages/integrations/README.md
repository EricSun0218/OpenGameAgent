# @opengameagent/integrations

Optional standards-based integration adapters for OpenGameAgent. The package is outside the Agent-loop core and does
not grant game authority.

## Ark Images / Seedream generation

`createArkImagesMediaProvider()` maps one host-selected Ark image model onto the existing durable media-job port. The
Adapter performs one synchronous `POST /api/v3/images/generations` request with `response_format: "b64_json"`; it does
not add another media loop, provider registry, retry, fallback route, asynchronous-task poller, or game-write authority.

```ts
import type { CredentialStore } from "@earendil-works/pi-ai";
import {
  createArkImagesMediaProvider,
  type ArkImagesProviderLimits,
} from "@opengameagent/integrations";

declare const credentials: CredentialStore;
declare const arkLimits: ArkImagesProviderLimits;

const seedream = createArkImagesMediaProvider({
  credentials,
  credentialReference: "ark-production",
  endpoint: "https://ark.cn-beijing.volces.com/api/v3/images/generations",
  modelId: "host-selected-endpoint-id",
  outputFormat: "png",
  size: "2K",
  watermark: false,
  limits: arkLimits,
});
```

The credential reference resolves exactly one `api_key` entry from the host's Pi `CredentialStore`; jobs cannot supply
credentials, endpoints, model IDs, output formats, sizes, or watermark policy. Endpoints require HTTPS and cannot contain
userinfo, a query, or a fragment. The host must configure every credential, request, response, prompt, reference-image,
output, URL, JSON-depth, and deadline bound through `ArkImagesProviderLimits`; the Adapter supplies no workload defaults.

Image requests contain exactly one non-empty text part and zero or more bounded PNG/JPEG base64 reference parts. Ark
`b64_json` results become original-byte media resources with provider ordinals preserved. Mixed valid images and
per-image provider failures produce a truthful partial result; provider messages, credentials, and raw transport errors
are never returned. Caller cancellation retains its exact reason, while the host-selected deadline fails with the stable
`request_timeout` Adapter error. Each accepted attempt reaches Ark at most once.

Provider URL outputs are disabled by default. A host can opt in with `urlOutputs.accept`, which must approve every exact
HTTPS URL before a separate bounded credential-free `GET`. That fetch never follows redirects and accepts only the
configured image media type. This policy is intended for a host-owned network-admission check; enabling it without an
appropriate destination policy weakens the default resource boundary.

## Doubao realtime speech

`createDoubaoRealtimeSpeechRecognizer()` and `createDoubaoRealtimeSpeechSynthesizer()` adapt Doubao's current
bidirectional WebSocket ASR and TTS protocols to the provider-neutral `RealtimeSpeechRecognizer` and
`RealtimeSpeechSynthesizer` seams. They are transport Adapters only. Pass them to `GameRealtimeVoiceService`, which
remains the sole owner of voice-turn identity, final-transcript projection, visible assistant text, interruption,
backpressure and runtime coordination. The Adapters do not open audio devices, run VAD, keep dialogue history, invoke
Pi, execute Tools or write game state.

```ts
import type { CredentialStore } from "@earendil-works/pi-ai";
import {
  createDoubaoRealtimeSpeechRecognizer,
  createDoubaoRealtimeSpeechSynthesizer,
  type DoubaoAsrLimits,
  type DoubaoTtsLimits,
} from "@opengameagent/integrations";

declare const credentials: CredentialStore;
declare const asrLimits: DoubaoAsrLimits;
declare const ttsLimits: DoubaoTtsLimits;
declare const hostSessionIdFactory: () => string;

const recognizer = createDoubaoRealtimeSpeechRecognizer({
  credentials,
  credentialReference: "doubao-production",
  endpoint: "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async",
  authentication: { type: "api-key" },
  resourceId: "host-selected-asr-resource",
  userId: "game-runtime",
  modelName: "host-selected-model",
  language: "zh-CN",
  inputFormat: {
    mediaType: "audio/pcm;format=s16le",
    sampleRateHz: 16_000,
    channelCount: 1,
  },
  limits: asrLimits,
});

const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
  credentials,
  credentialReference: "doubao-production",
  endpoint: "wss://openspeech.bytedance.com/api/v3/tts/bidirection",
  authentication: { type: "api-key" },
  resourceId: "host-selected-tts-resource",
  outputFormat: {
    mediaType: "audio/pcm;format=s16le",
    sampleRateHz: 24_000,
    channelCount: 1,
  },
  createSessionId: hostSessionIdFactory,
  limits: ttsLimits,
});
```

The credential reference resolves exactly one Pi `api_key` entry. Current API-key authentication emits `X-Api-Key`;
the explicit `legacy` mode instead combines the stored access key with a host-configured app key. Credentials and raw
provider errors are never included in Adapter errors. Non-loopback endpoints require WSS, redirects are disabled, and
the default `ws` transport bounds complete messages, fragments, pending frames and buffered chunks.

Every count, byte budget and deadline in `DoubaoAsrLimits` and `DoubaoTtsLimits` is mandatory host configuration. The
Adapters add no provider retry, fallback, packet cadence, silence threshold or timeout default. They enforce a complete
operation deadline as well as connect, message and graceful-close deadlines; preserve exact caller cancellation; abort
non-cooperative transports; close unfinished input iterators; reject late ASR sequences and malformed event/session
ordering; and stream ASR hypotheses and TTS audio without waiting for the opposite producer to finish. `maxJsonDepth`
applies to both emitted control JSON and provider control responses.

## Durable manual speech generation

`createRealtimeSpeechMediaProvider()` adapts one host-selected `RealtimeSpeechSynthesizer` to the existing durable
media-job port. It is for an explicit developer or game workflow that generates a reusable voice asset, not the
ephemeral reply audio owned by `GameRealtimeVoiceService`.

```ts
import { createRealtimeSpeechMediaProvider } from "@opengameagent/integrations";

const manualSpeech = createRealtimeSpeechMediaProvider({
  synthesizer,
  voiceProfileId: "host-selected-npc-voice",
  outputFormat: {
    mediaType: "audio/pcm;format=s16le",
    sampleRateHz: 24_000,
    channelCount: 1,
  },
  limits: {
    maxIdentifierBytes: hostIdentifierBudget,
    maxTextBytes: hostTextBudget,
    maxOutputChunks: hostChunkCountBudget,
    maxOutputChunkBytes: hostChunkByteBudget,
    maxOutputAudioBytes: hostAudioByteBudget,
  },
});
```

Register this provider once in `MediaJobService`, then submit an `audio` job whose input is exactly
`{ text: "..." }`. The Adapter makes one synthesis call, consumes and closes its bounded PCM16 stream, and returns one
ordinal-zero `audio/wav` resource. The existing media service remains the only owner of job IDs, attempts, submission
keys, restart policy, immutable publication, result bundles, and resource selection. The Adapter does not choose a
voice, provider, filename or game import path, and exposes neither provider polling nor cancellation methods.

Invalid input fails before synthesis. Cancellation preserves the caller's exact reason and publishes no completed
resource. If process recovery finds an in-flight `submitting` attempt with no durable provider handle, the host calls
`MediaJobService.reconcile()` once to mark it `outcome_unknown` without resubmission. The host must explicitly abandon
that attempt before restarting with the service-issued new submission key; it must not infer that the external
synthesis did not happen.

## Pi OpenRouter image generation

`createPiOpenRouterImagesMediaProvider()` maps one host-selected model from Pi's maintained `ImagesModels` collection
onto the durable media-job port. Pi remains responsible for OpenRouter authentication, model catalog data, request
construction, one provider call, and response parsing. The Adapter contributes no provider registry, fallback route,
poller, retry loop, or game-write authority.

```ts
import { createImagesModels, type CredentialStore } from "@earendil-works/pi-ai";
import { openrouterImagesProvider } from "@earendil-works/pi-ai/providers/openrouter-images";
import { createPiOpenRouterImagesMediaProvider } from "@opengameagent/integrations";

declare const maxProviderResponseBytes: number;
declare const credentials: CredentialStore;

const images = createImagesModels({ credentials });
images.setProvider(openrouterImagesProvider());

const provider = createPiOpenRouterImagesMediaProvider({
  models: images,
  modelId: "openai/gpt-image-1",
  maxResponseBytes: maxProviderResponseBytes,
  health: {
    id: "world-art",
    requirement: "optional",
    maxObservationAgeMs: hostSelectedEvidenceLifetime,
    now: hostMonotonicClock,
  },
});

const mediaHealth = provider.healthDependencies();
```

The media request body is a JSON-representable Pi image context:

```ts
{
  input: [
    { type: "text", text: "A moonlit village" },
    { type: "image", mimeType: "image/png", data: "<strict base64; padding optional, tail bits canonical>" },
  ],
}
```

The model is fixed when the Adapter is created; a job cannot select a provider, endpoint, model, or credential. The
collection uses Pi's own credential and ambient-auth semantics. This Adapter does not apply the separate Provider
Profile `reference` or `none` binding rules.

Every provider-reaching durable attempt invokes `ImagesModels.generateImages()` exactly once with the caller signal and
`maxRetries: 0`; locally rejected requests invoke it zero times.
The host must provide the maximum complete HTTP response size; the shared bounded Fetch implementation enforces it
before Pi materializes JSON and base64. Output bytes are then decoded lazily, so the existing media resource and bundle
limits run before decoded-byte allocation. No timeout or output-size default is added here; a host can bound the
operation with the caller `AbortSignal`.

Only Pi image blocks become original-byte media resources. Text, usage, response IDs, timestamps, and raw errors are
discarded. A successful Pi result with no valid image is a malformed terminal result; mixed valid and malformed images
retain their image-slot ordinals as a partial result. Pi currently flattens authentication, HTTP, transport, and parsing
failures into one error result, so the Adapter does not infer a failure class from error text or claim that submission
did not occur. Those attempts remain `outcome_unknown` until a host explicitly abandons them and starts a new attempt.
Caller cancellation preserves the exact abort reason but likewise does not claim provider-side cancellation. When a
terminal Pi result wins the race, the media job retains that result before cancellation returns the caller's reason.
This one-shot Adapter therefore exposes neither `read` nor `cancel`.

Optional media health is passive and cannot create a paid request. Exact Pi catalog selection initially reports
`available`; a real successful generation reports `ready`, a partial result reports `degraded`, and terminal failure or
an unresolved provider outcome reports `unavailable` with a closed reason. Expired evidence becomes `available` with
reason `stale`. Calling `healthDependencies()` or inspecting its definitions never invokes image generation. The host
must supply the clock and evidence lifetime; there is no freshness default.

## Provider profiles

`createProviderProfileModelStream()` resolves an ordered host configuration through Pi's `Provider`, `CredentialStore`,
`MutableModels`, and `streamSimple` contracts. It creates one Pi model collection per immutable configuration-object
identity, captures every target and referenced provider once for that revision, and starts the first target only after crossing the Agent's actual-attempt
boundary. When provider provenance is configured on the Agent, that boundary persists the request record before provider
start. Host replacement affects the next provider request, never an in-flight request. Pi remains the only provider
registry and Agent loop.

The `configuration` callback must retain and return the same immutable object for the lifetime of one active revision.
Returning a new object is an explicit revision change and builds a new registry; OGA does not compare provider objects or
credentials by value. `ProviderProfileLifecycle.configuration()` already supplies this stable opaque identity.

```ts
import type { CredentialStore, Model, Provider } from "@earendil-works/pi-ai";
import { createProviderProfileModelStream } from "@opengameagent/integrations";

declare const provider: Provider;
declare const requestedModel: Model<string>;
declare const credentials: CredentialStore;

const modelStream = createProviderProfileModelStream({
  credentialReferences: credentials,
  health: {
    id: "primary-dialogue-provider",
    requirement: "required",
    maxObservationAgeMs: hostSelectedEvidenceLifetime,
    now: hostMonotonicClock,
  },
  configuration: () => ({
    profiles: [{
      provider,
      credential: { kind: "reference", reference: "game-primary" },
    }],
    targets: [{
      profileId: provider.id,
      modelId: requestedModel.id,
      supportsTools: true,
      pricing: { kind: "pi-catalog" },
    }],
  }),
});

const providerHealth = modelStream.healthDependencies();
```

Credential binding is explicit. `ambient` preserves the provider's Pi-native environment, file, and platform API-key
resolution. Stored OAuth credentials use `reference`, which maps the provider to one opaque entry in a host-owned
`CredentialStore`; a missing entry fails and never falls through to ambient credentials. `none` uses only a provider's
keyless API-key resolver under an empty auth context and rejects returned keys, headers, endpoint overrides, or provider
environment. Request-level `apiKey` and `env` overrides cannot bypass `reference` or `none`. This package does not bundle
a plaintext backend. A Windows desktop/self-hosted sidecar can inject the separate optional
`@opengameagent/credentials-windows` CurrentUser DPAPI Adapter; other hosts can supply any `CredentialStore` that preserves
Pi's serialized `modify` contract.

Every target also declares Tool support and one pricing fact: Pi catalog rates, a host rate card, explicitly free, or
unpriced. `captureProviderProfileSnapshot()` returns only immutable display-safe target facts; it omits provider objects,
endpoints, headers, auth handlers, secrets, credential references, and failure evidence.

`ProviderProfileLifecycle` adds an optional staged management boundary around the same captured configuration. A host
creates or updates one complete draft, validates it, performs a one-shot `test`, and activates it with the exact draft and
active revision tokens returned by `snapshot()`. Activation always probes the captured draft again and atomically replaces
the active configuration only when both revisions are still current. `delete` affects only the draft; `revoke` affects only
the active configuration.

The host-injected probe decides whether the complete captured target set is acceptable and returns a shared runtime-health
snapshot. The lifecycle does not generate a model response, retain test or health observations, retry, poll, cache, impose a
TTL, or manage endpoint processes. A late `test` or `health` result remains labelled with the revision it observed and never
writes lifecycle state. `configuration()` returns an opaque request-ready capture for
`createProviderProfileModelStream()`; activation affects the next request while an in-flight request keeps its original
capture. Only the display-safe snapshot is enumerable or serializable from that opaque value.

Ordered fallback is explicit per target through `fallbackOn`. It can admit only typed throttling, timeout, transport,
capacity, or provider-internal failures reported by a provider Adapter through
`createProviderFailureEvidenceChannel()`. Evidence is bound to the exact terminal Pi message object; localized error text,
diagnostics, stacks, status-shaped objects, and unknown failures never trigger fallback. A candidate can advance only
before any text, reasoning, Tool call, deferred handle, or other semantic output becomes visible. Caller cancellation,
terminal request/auth/quota/protocol failures, provenance persistence failure, and target-list exhaustion all stop on the
current attempt. Every candidate is snapshotted and capability-checked before target one starts, every actual Pi route
attempt receives `maxRetries: 0`, and discarded candidate starts never enter the Agent transcript.

The same pricing fact is captured by the Agent's actual-attempt settlement. Catalog and host-rate-card responses retain
Pi's exact terminal USD cost only when the Adapter reports terminal usage; otherwise their cost is explicitly unknown.
Explicit free records a zero-valued `free` fact, and unpriced records contain no USD amount. Because Pi requires numeric
model rates, an unpriced request uses zero only as an internal Pi placeholder; accounting must read the response-provenance
cost and usage discriminants and must not interpret that placeholder as free or reported usage.

Optional provider health is also passive. It starts `declared`, records `ready` only after a valid primary terminal
response, records `degraded` after a valid successful fallback, and maps typed terminal failures to closed
`unavailable` reasons. Untyped start or event-stream failure becomes `unavailable` with reason `unknown`. Caller
cancellation does not overwrite the last observation. Expired evidence becomes
`available` with reason `stale`. Reading the health definition performs no provider request, and the host must provide
the clock and evidence lifetime.

## Provider endpoint health

`createProviderEndpointHealthDependency()` adapts one explicitly configured OpenAI-compatible, Ollama, or llama.cpp
endpoint to the shared runtime-health contract. The inspection is deliberately non-generative: it never sends a model
request, pulls or loads a model, starts a process, retries, polls, or supplies its own timeout.

OpenAI-compatible inspection performs one bounded `GET /models`; an exact model match proves only `available`. Ollama
uses its official client to read installed inventory and then, only for an installed model, the loaded-model list;
installed models are `available` and loaded models are `ready`. llama.cpp reads the host-selected health URL once;
documented healthy and loading responses map to `ready` and `available`. Explicit event streams are rejected because
these health resources are single JSON responses.

```ts
import { createProviderEndpointHealthDependency } from "@opengameagent/integrations";

declare const hostHealthResponseBudget: number;

const localModel = createProviderEndpointHealthDependency({
  id: "local-chat",
  kind: "local-endpoint",
  requirement: "optional",
  protocol: "ollama",
  baseUrl: "http://127.0.0.1:11434",
  modelId: "game-model:latest",
  maxResponseBytes: hostHealthResponseBudget,
});
```

The host must supply the complete-response byte budget and the outer `RuntimeHealthMonitor` timeout and concurrency.
HTTP is accepted only for exact loopback hosts; non-loopback services require HTTPS. A health definition contains no
process ownership, so it cannot stop, restart, attach to, or otherwise manage an externally operated endpoint.

## Owned local-model processes

`OwnedLocalModelProcessLauncher` is a narrow lifecycle wrapper around Execa for local-model processes started by the
current host. It can stop only handles returned by its own `startOwned()` calls. It has no PID or port attachment,
process discovery, model installation, restart policy, readiness polling, or log buffering. Process liveness is also not
endpoint readiness; use the endpoint-health adapter above for an explicit readiness observation.

```ts
import { OwnedLocalModelProcessLauncher } from "@opengameagent/integrations";

const localModelProcesses = new OwnedLocalModelProcessLauncher({
  maxOwnedProcesses: hostSelectedOwnedProcessCapacity,
});
const modelServer = await localModelProcesses.startOwned({
  executable: hostSelectedExecutable,
  arguments: hostSelectedArguments,
  cwd: hostSelectedWorkingDirectory,
  env: hostSelectedEnvironment,
  stdio: { stdin: "ignore", stdout: "inherit", stderr: "inherit" },
  forceKillAfterDelayMs: hostSelectedShutdownDelay,
});

await modelServer.stop();
await localModelProcesses.close();
```

The host must provide a positive safe-integer owned-process capacity. Pending starts and published handles share that
capacity; excess starts fail immediately and are never queued. The host must also choose either a positive
forced-shutdown delay within Node's timer range or `false` to disable that fallback. The wrapper passes the executable
and argument vector separately with Execa's `shell: false`; on Windows,
Execa can still use its escaped `cmd.exe` launcher for command shims and other files that Windows cannot execute
directly. `env` is the complete child environment: ambient sidecar variables are never merged, so the host must include
every variable the executable needs. Arguments, environment, working directory, and standard-I/O choices are captured
for the launch; later caller mutation does not alter the owned process. Closing the launcher prevents new starts,
cancels unpublished starts, and stops every published process still running. Descendant termination follows Execa's
best-effort platform behavior; in particular, Windows relies on `taskkill /T /F` and falls back to the direct process if
that facility is unavailable or fails. Externally started processes remain outside this ownership boundary.

## OpenAI background Responses

`createOpenAIBackgroundResponsesApi()` adds the official OpenAI background lifecycle to Pi's
`openai-responses` API without replacing Pi's Agent loop or its foreground provider path. A host opts in for an exact
provider request by passing `deferred: true` to Pi `streamSimple`; ordinary requests continue through Pi unchanged.

The Adapter asks Pi to build the complete request payload and then applies the three provider requirements for durable
recovery: `background: true`, `stream: true`, and `store: true`. The host's existing `onPayload` hook runs once against
that actual candidate; a replacement that disables any required field is rejected before submission.
It stops reading after OpenAI returns `response.created`, saves the response ID and provider `sequence_number` in Pi's
opaque deferred handle, and relies on the existing OpenGameAgent durable transcript to retain that handle privately.
The existing `onResponse` hook still runs as soon as HTTP headers arrive; if it fails after provider admission, the
returned deferred message keeps its recoverable handle and records the hook failure as a Pi diagnostic.

```ts
import { createModels, createProvider } from "@earendil-works/pi-ai";
import { openaiProvider } from "@earendil-works/pi-ai/providers/openai";
import { createPiModelsDeferredResponseAdapter } from "@opengameagent/agent";
import { createOpenAIBackgroundResponsesApi } from "@opengameagent/integrations";

const builtin = openaiProvider();
const models = createModels();
models.setProvider(createProvider({
  id: builtin.id,
  name: builtin.name,
  baseUrl: builtin.baseUrl,
  auth: builtin.auth,
  models: builtin.getModels(),
  api: createOpenAIBackgroundResponsesApi(),
}));

const modelStream = (model, context, options) =>
  models.streamSimple(model, context, { ...options, deferred: true });
const deferredResponses = createPiModelsDeferredResponseAdapter(models);
```

Each explicit fetch first performs one status read. `queued` and `in_progress` return the same handle; a terminal
response resumes the provider event stream after the saved cursor and feeds those events back through Pi's maintained
Responses converter. The Adapter does not poll, sleep, or retry. In particular, it disables the OpenAI SDK's automatic
POST retries because the official client does not attach an idempotency key to Responses creation. Hosts reconcile an
uncertain submission through their own admission boundary and explicitly resume known handles. `wait` is therefore a
provider-neutral hint with no OpenAI long-poll behavior here. Explicit abandonment commits locally before this Adapter
attempts one best-effort provider cancellation.

Background mode necessarily stores response data at OpenAI for durable retrieval. Do not opt in when that provider
storage policy is unsuitable for the session.

`DirectoryGameSkillSource` uses Pi's maintained Agent Skills loader and invocation formatter. It discovers trusted
host-installed `SKILL.md` directories without executing package code, publishes only selected names and descriptions,
and loads full instructions or referenced text resources through one ordinary Pi Tool. Absolute host paths never enter
model context or Tool results.

The host must provide every environment-dependent count and byte budget. Discovery confines symlinks and resources to
their configured roots, bounds traversal and aggregate reads, rejects incomplete I/O observations, skips malformed
skills, and resolves duplicate names by configured root order with path-free diagnostics. The optional selector runs on
every provider request and receives the normalized game input, exact run coordinate, and only the names of already
admitted Tools. It cannot execute those Tools or expand their authority.

For an atomically published immutable catalog, the host may provide `catalogRevision`. The source reuses only the last
complete successful scan while that opaque revision is unchanged and rescans after it changes. Without this callback,
every load scans the directory. A failed or incomplete scan is never cached; the host owns advancing the revision only
after publishing a complete directory.

`disable-model-invocation: true` removes a skill from model catalogs and the generated Tool snapshot. A trusted host can
still call `loadExplicit()` deliberately. Resource access accepts only forward-slash relative paths inside the selected
skill directory and returns UTF-8 text within the configured limits.

## Portable plugin directories

`DirectoryAgentPluginCatalog` loads host-installed directories that follow the published
[Agent Plugins 1.0 specification](https://github.com/agentplugins/agent-plugins-spec/blob/main/spec/1.0.0.md). The loader
recognizes only the portable `plugin.json`, immediate `skills/` root, and `mcp.json` components. It does not search a
registry, install packages, import JavaScript, run hooks, or start MCP processes. Unsupported schema versions are
rejected locally without fetching schemas during discovery.

Manifest failure rejects one plugin. A bad Skill location disables only Skills for that plugin, a bad `mcp.json`
disables only MCP, and a bad MCP entry does not hide independent entries. Package paths are resolved through the
filesystem and confined to the plugin root. JSON, files, aggregate reads, diagnostics, installations, server entries,
paths, and placeholder-expanded values all use mandatory host budgets.

```ts
import {
  createAgentPluginMcpServerDefinition,
  DirectoryAgentPluginCatalog,
  type DirectoryAgentPluginLimits,
} from "@opengameagent/integrations";

declare const pluginLimits: DirectoryAgentPluginLimits;

const plugins = await new DirectoryAgentPluginCatalog({
  cwd: process.cwd(),
  limits: pluginLimits,
  installations: [{
    installationId: "trusted-world-tools",
    rootPath: "./installed/world-tools",
    dataPath: "./plugin-data/world-tools",
  }],
}).load(signal);

// `plugins.skillRoots` can be passed to DirectoryGameSkillSource.
const admittedServer = createAgentPluginMcpServerDefinition(plugins.mcpServers[0], {
  serverId: "world-tools",
  description: "Host-approved world tools",
  requirement: "optional",
  transport: {
    type: "stdio",
    baseEnv: hostSelectedEnvironment,
    stderr: "inherit",
    maxMessageBytes: hostMcpMessageLimit,
  },
});
```

The last conversion is the explicit trust boundary: discovery alone cannot execute a bundled MCP server. For stdio,
the adapter performs the standard single-pass `${PLUGIN_ROOT}` / `${PLUGIN_DATA}` expansion, creates the dedicated data
directory, overlays package environment entries on the host-selected base, then replaces reserved variables. For HTTP,
host headers and authorization replace same-name package headers. Roots, working directories, and bundled commands are
checked again when a connection starts so an update cannot reuse a stale discovery snapshot. Legacy HTTP+SSE is skipped;
the supported portable transports are stdio and Streamable HTTP.

Portable v1 plugins contain data, Skills, and MCP declarations only. Trusted TypeScript code extensions remain ordinary
host dependencies that the host installs, verifies, and imports explicitly; this package never discovers executable
extension modules.

## Signed plugin directory distribution

`buildSignedAgentPluginPackage()` adds an offline distribution envelope to an ordinary Agent Plugins 1.0 directory.
The package remains a directory rather than an archive. Its reserved `io.opengameagent.package/` directory contains one
canonical manifest and a sorted list of detached Ed25519 signatures. The signed manifest binds the exact plugin name and
required version, a host-issued positive-decimal release sequence, the complete sorted file set, every byte length and
SHA-256 digest, and explicit executable intent. Source symlinks, junctions, special files, unsafe portable paths, and a
pre-existing destination that is not the exact resumable incomplete draft are rejected. The published manifest and signature JSON Schemas are available through the
package's `schemas/agent-plugin-package-*-v1.schema.json` exports; canonical ordering, NFC/portable-path checks, byte
budgets, and case-insensitive collision checks remain runtime requirements beyond JSON Schema.

Publication first reserves a host-owned sibling state file with exclusive creation, so a concurrent or pre-existing
directory is never replaced. The state file is named
`.oga-plugin-publication-<SHA-256-of-normalized-destination>`. It begins as `incomplete` and fences the draft from
installers and `DirectoryAgentPluginCatalog`. Only after the exact target tree has been reread, verified, sealed, and
synced does publication atomically replace that state with a `complete` record binding the manifest digest and exact
directory device/inode identity. The complete record persists, so publication is monotonic and a late contender cannot
hide a completed root by recreating an incomplete marker. Cancellation is honored between complete file writes, so it
cannot leave a caller-cancelled partial file. A later identical operation may resume an incomplete draft by accepting
only byte-identical, correctly sealed files and filling missing files. A mismatched draft stays inert for explicit host
recovery; the library does not recursively delete a path after a mutable path-identity check. Publication state remains
outside the destination and never changes the completed package or installed-root shape. A copied package directory
without its host-owned sibling record remains a valid transport artifact and is fully reverified by the installer.

`installSignedAgentPluginPackage()` verifies a host-selected threshold of distinct trusted key IDs, rechecks the complete
directory, and writes only verified plugin files into `<storeRoot>/<manifest SHA-256>`. Package metadata is not copied
into the installed root, and the separately configured `PLUGIN_DATA` directory is never read or changed. An optional
current-installation value rejects a different plugin and rejects a different package at an equal or lower release
sequence. Reinstalling the exact package is idempotent only after the existing object is verified again.

```ts
import { sign } from "node:crypto";
import {
  agentPluginPackagePublicationNextStatePath,
  agentPluginPackagePublicationStatePath,
  buildSignedAgentPluginPackage,
  installSignedAgentPluginPackage,
  type AgentPluginPackageLimits,
} from "@opengameagent/integrations";

declare const packageLimits: AgentPluginPackageLimits;
declare const releasePrivateKey: import("node:crypto").KeyObject;
declare const releasePublicKey: import("node:crypto").KeyObject;

const built = await buildSignedAgentPluginPackage({
  cwd: process.cwd(),
  sourceRoot: "./plugins/world-tools-source",
  packageRoot: "./packages/world-tools",
  releaseSequence: "42",
  executablePaths: ["server.mjs"],
  signers: [{
    keyId: "studio-release-2026",
    sign: (message) => new Uint8Array(sign(null, message, releasePrivateKey)),
  }],
  limits: packageLimits,
}, signal);

const installed = await installSignedAgentPluginPackage({
  cwd: process.cwd(),
  packageRoot: built.packageRoot,
  storeRoot: "./installed/plugin-objects",
  trustedKeys: [{ keyId: "studio-release-2026", publicKey: releasePublicKey }],
  requiredSignatures: 1,
  limits: packageLimits,
}, signal);

// Pass `installed.rootPath` to DirectoryAgentPluginCatalog with a separate dataPath.
const publicationState = agentPluginPackagePublicationStatePath(installed.rootPath);
const pendingPublicationState = agentPluginPackagePublicationNextStatePath(installed.rootPath);
```

Every traversal, complete path, path-segment, file, aggregate, metadata, signer/key, and JSON bound is mandatory host
configuration; there are no package workload defaults. The segment bound must fit every target filesystem rather than
assuming one Windows or POSIX component limit. The package does not own keys, fetch a registry, poll for updates, schedule
transport retries,
execute a file, import a module, start MCP, or claim an in-process JavaScript sandbox. Package and store parents are
host-owned ordinary directories. On POSIX, published directories and files are sealed to read-only/read-executable
modes. On Windows, files are marked read-only, but a host that needs an adversarial same-account boundary must also
protect the object-store directory with an ACL or equivalent isolation. The generic directory catalog checks a managed
root's complete publication state and exact filesystem identity before and after discovery; an incomplete or malformed
state makes the root unavailable. A root without host-owned publication state is treated as an explicitly supplied
legacy root. The catalog does not reverify package signatures or file hashes on every load. Hosts must expose only roots
admitted by the installer from their protected store (or explicitly accepted legacy
roots), and may call the installer idempotently to reverify an existing object. A retention owner must deliberately make
an obsolete object removable before deleting it. The host remains responsible for release selection, trust rotation,
distribution transport, retention, data directories, and explicit MCP/executable admission.
Recovery and retention must first quiesce catalog/install/build access, then operate on the exact root plus the two paths
returned by the publication-state helpers. Delete the root before deleting its complete or incomplete state, and remove
the pending state candidate last. This keeps an object unavailable throughout explicit recovery and avoids reproducing
the destination-normalization/hash algorithm in host code.

## MCP Tools

`GameMcpRequestSource` connects the official MCP client to the same Pi request boundary. MCP Tools remain ordinary Pi
Tools; the integration does not create another Agent loop, planner, or authority layer. On every provider request, the
host selects an exact subset of the current catalog and chooses direct or lazy exposure. Lazy exposure adds bounded
search, describe, and call Tools so large catalogs do not have to enter model context in full. The host configures separate
`callParallel` and `callSequential` names. Only the call modes used by the selected lazy Tools are exposed, so a lazy
catalog contributes three or four Pi Tools. Describe returns each remote Tool's `executionMode` and exact `callTool`;
calling it through the other mode fails before authorization or remote dispatch.

Parallel-only calls can run concurrently even when the catalog also contains sequential Tools. If an assistant batch
actually calls a sequential Tool, Pi executes the whole batch sequentially. This orders already planned calls; a next
step that depends on a result must be chosen by the model in a later request after that result is returned.

The selector is not authorization. `authorizeCall` runs again immediately before every remote dispatch with the game
input, run coordinate, Tool call ID, normalized arguments, and untrusted MCP annotations. Throwing denies the call.
Server task-required Tools are not exposed, interactive server requests are not advertised, stale catalog snapshots are
fenced, and an interrupted Tool call is never replayed automatically.

```ts
import {
  createGameMcpStdioTransportFactory,
  GameMcpRequestSource,
  type GameMcpLimits,
} from "@opengameagent/integrations";

declare const hostMcpLimits: GameMcpLimits;
declare const maxMcpMessageBytes: number;
declare const gamePolicy: {
  authorizeExternalTool(request: unknown, signal: AbortSignal): Promise<void>;
};

const mcp = new GameMcpRequestSource({
  sourceId: "mcp",
  limits: hostMcpLimits,
  lazyToolNames: {
    search: "mcp_search",
    describe: "mcp_describe",
    callParallel: "mcp_call_parallel",
    callSequential: "mcp_call_sequential",
  },
  servers: [{
    serverId: "world-tools",
    description: "Read-only world utilities",
    requirement: "optional",
    createTransport: createGameMcpStdioTransportFactory({
      command: process.execPath,
      args: ["./world-mcp-server.js"],
      env: {},
      stderr: "inherit",
      maxMessageBytes: maxMcpMessageBytes,
    }),
  }],
  select: ({ candidates }) => candidates.map(({ serverId, name }) => ({
    serverId,
    name,
    exposure: "lazy" as const,
    executionMode: "sequential" as const,
  })),
  authorizeCall: async (request, signal) => {
    await gamePolicy.authorizeExternalTool(request, signal);
  },
});
```

`mcp.healthDependencies()` exposes one shared runtime-health definition per admitted server. Each explicit inspection
uses an isolated official MCP client to load that server's real Tool catalog, so readiness means the transport,
negotiated Tools capability, pagination, schemas, per-schema limits, and that catalog's complete byte budget all
succeeded. The request source still checks the combined catalog budget before selection. Health does not add a ping,
retry, polling, or cache loop. The outer `RuntimeHealthMonitor` owns host-configured inspection concurrency and timeout,
while caller cancellation closes the isolated probe without disrupting a live request connection. A server's
`requirement` is also its runtime-health requirement: an unavailable required server makes the runtime not ready, while
an unavailable optional server degrades it.

All counts, byte budgets, concurrency limits, pagination limits, and idle/total timeouts are mandatory host
configuration; this package supplies no workload defaults. Stdio receives an explicit environment and message bound.
Streamable HTTP accepts HTTPS or exact loopback HTTP, rejects redirects and URL credentials, bounds complete responses
and individual SSE events, and uses an explicit reconnection policy whose delays remain within Node's timer ceiling.
Valid server-provided SSE `retry` values must also fit the host-selected maximum delay before the official SDK receives
the frame. Host-approved static headers are injected only
for the exact MCP resource endpoint; OAuth metadata, registration, and token endpoints never inherit them. Tool
arguments, schemas, catalogs, text, media, resources, structured output, and final Pi results are independently bounded.
