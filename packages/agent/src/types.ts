import type { AgentEvent, AgentMessage, AgentTool, AgentToolResult, StreamFn } from "@earendil-works/pi-agent-core";
import type {
	Api,
	AssistantMessage,
	AssistantMessageEventStream,
	ImageContent,
	Model,
	UserMessage,
} from "@earendil-works/pi-ai";
import type {
	CanonicalEvent,
	CanonicalEventData,
	DurableTranscriptDeferredOpenedRecord,
	ImageMediaType,
	ImageReferenceContent,
	JsonValue,
	MessageContent,
	RunAdmissionAuthority,
	RunCoordinate,
	StreamKey,
} from "@opengameagent/protocol";
import type { GameDeferredResponseAdapter } from "./deferred-response.js";
import type { GameAgentTranscriptBinding } from "./durable-transcript.js";
import type { GameProvenanceAlias } from "./provenance-alias.js";
import type { TranscriptCompactor } from "./transcript-compaction.js";

export interface GameInput<TPayload = unknown> extends StreamKey {
	inputId: string;
	payload: TPayload;
	admissionAuthority?: RunAdmissionAuthority;
}

export type NormalizedGameInput = GameInput<JsonValue>;

export interface GameContextReference {
	readonly referenceId: string;
	readonly revision: string;
	readonly content: JsonValue;
}

export const GAME_MODEL_CONTEXT_SOURCE_KINDS = [
	"context",
	"memory",
	"skill",
	"artifact",
	"knowledge",
	"mcp",
	"extension",
	"observation",
] as const;

export type GameModelContextSourceKind = (typeof GAME_MODEL_CONTEXT_SOURCE_KINDS)[number];

export interface GameContextImage {
	readonly contextReferenceId: string;
	readonly content: ImageContent;
	readonly reference: ImageReferenceContent;
}

export interface GameContextContribution {
	readonly references: readonly GameContextReference[];
	readonly images?: readonly GameContextImage[];
}

export interface GameContextSource {
	readonly sourceId: string;
	readonly provenanceKind?: GameModelContextSourceKind;
	load(
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
	): GameContextContribution | Promise<GameContextContribution>;
}

export interface GameContextMediaLimits {
	readonly maxContentParts: number;
	readonly maxImageBytes: number;
	readonly maxTotalImageBytes: number;
}

export interface GameContextLimits {
	readonly maxReferencesPerSnapshot: number;
	readonly maxSnapshotBytes: number;
	readonly media?: GameContextMediaLimits;
}

export type GameContextTransformer = (messages: AgentMessage[], signal?: AbortSignal) => Promise<AgentMessage[]>;

export type GameToolSource = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
) => readonly AgentTool[] | Promise<readonly AgentTool[]>;

export interface GameToolCatalogLimits {
	readonly maxTools: number;
	readonly maxCatalogBytes: number;
	readonly maxSchemaDepth: number;
}

export interface GameToolSnapshotLimits extends GameToolCatalogLimits {
	readonly maxRequestSources: number;
	readonly maxPolicyRevisionBytes: number;
	readonly maxArgumentsBytes: number;
	readonly maxArgumentsDepth: number;
	readonly maxResultContentParts: number;
	readonly maxResultBytes: number;
	readonly maxResultDepth: number;
	readonly maxResultUpdates: number;
}

export interface GameToolDescriptor {
	readonly name: string;
	readonly label: string;
	readonly description: string;
	readonly parameters: JsonValue;
	readonly executionMode: "parallel" | "sequential";
	readonly fingerprint: string;
}

export interface GameToolPolicyDecision {
	/** Opaque host-owned revision for the complete visibility and execution policy. */
	readonly revision: string;
	/** Candidate names the model may see and request under this policy revision. */
	readonly visibleToolNames: readonly string[];
}

/**
 * Decide which candidate Tools are model-visible for one exact provider request.
 *
 * This is a pure snapshot read, not an approval or lease-consumption hook. Pi may
 * cause it to be called repeatedly and concurrently while preparing a request or
 * executing parallel Tools, so implementations must be idempotent, reentrant,
 * side-effect free, and concurrency safe. It must return the same revision and
 * visible set until that request's Tool calls have settled. Put effectful or
 * argument-level authorization in `beforeToolExecution` instead.
 */
export type GameToolPolicy = (
	input: Readonly<NormalizedGameInput>,
	candidates: readonly Readonly<GameToolDescriptor>[],
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
) => GameToolPolicyDecision | Promise<GameToolPolicyDecision>;

export interface GameRequestContribution {
	readonly context?: GameContextContribution;
	readonly tools?: readonly AgentTool[];
	readonly transformToolResult?: GameToolResultTransformer;
	/** Minimum final whole-result admission required by Tools this source contributes. */
	readonly requiredToolResultBytes?: number;
	/** Aggregate UTF-8 text admitted only before this source transforms Tool-result content. */
	readonly transformToolResultTextBytes?: number;
}

/**
 * Collect one optional capability at the exact Pi provider-request boundary.
 *
 * Sources receive only the names of tools already admitted for this request,
 * never their executable functions. They may contribute model context and
 * additional tools as one immutable request snapshot.
 */
export interface GameRequestSource {
	readonly sourceId: string;
	readonly provenanceKind?: GameModelContextSourceKind;
	collect(
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
		visibleToolNames: readonly string[],
	): GameRequestContribution | Promise<GameRequestContribution>;
}

export interface GameModelContextSourceProvenance {
	readonly kind: GameModelContextSourceKind;
	readonly sourceId: GameProvenanceAlias;
	readonly references: readonly {
		readonly referenceId: GameProvenanceAlias;
		readonly revision: GameProvenanceAlias;
		readonly contentFingerprint: string;
	}[];
}

export interface GameModelToolProvenance {
	readonly name: GameProvenanceAlias;
	readonly sourceId: GameProvenanceAlias;
	readonly fingerprint: string;
}

export interface GameModelImageProvenance {
	readonly messageIndex: number;
	readonly contentIndex: number;
	readonly mediaType: ImageMediaType;
	readonly bytes: number;
	readonly sha256: string;
	readonly context?: {
		readonly sourceId: GameProvenanceAlias;
		readonly referenceId: GameProvenanceAlias;
		readonly revision: GameProvenanceAlias;
	};
}

export interface GameModelContextLineageReference {
	readonly sourceId: string;
	readonly referenceId: string;
	readonly revision: string;
	readonly contentFingerprint: string;
}

export interface GameModelContextLineageProvenance {
	readonly sourceId: GameProvenanceAlias;
	readonly referenceId: GameProvenanceAlias;
	readonly revision: GameProvenanceAlias;
	readonly contentFingerprint: string;
}

export interface GameModelRequestCoordinateProvenance {
	readonly scopeId: GameProvenanceAlias;
	readonly streamId: GameProvenanceAlias;
	readonly runId: GameProvenanceAlias;
	readonly turn: string;
}

export interface GameModelRouteProvenance {
	readonly provider: GameProvenanceAlias;
	readonly api: GameProvenanceAlias;
	readonly model: GameProvenanceAlias;
}

export interface GameModelAttemptProvenance {
	readonly index: string;
	readonly targetRoute: GameModelRouteProvenance;
}

export interface GameExactToolRepeatAdvisoryProvenance {
	readonly eventId: GameProvenanceAlias;
	readonly eventRevision: string;
	readonly toolName: GameProvenanceAlias;
	readonly consecutiveCount: string;
	readonly contentFingerprint: string;
}

export interface GameModelRequestProvenance {
	readonly operation: "inference";
	readonly coordinate: GameModelRequestCoordinateProvenance;
	readonly startedAt: string;
	readonly requestedRoute: GameModelRouteProvenance;
	readonly attempt: GameModelAttemptProvenance;
	readonly modelContextFingerprint: string;
	readonly transcript: {
		readonly revision: string;
		readonly checkpointRevision?: string;
	};
	readonly lineage: {
		readonly baseInstructions: GameModelContextLineageProvenance;
		readonly contextTransformer?: GameModelContextLineageProvenance;
		readonly exactToolRepeatPolicy?: GameModelContextLineageProvenance;
	};
	readonly policyContext?: {
		readonly exactToolRepeat: GameExactToolRepeatAdvisoryProvenance;
	};
	readonly toolSnapshot?: {
		readonly revision: GameProvenanceAlias;
		readonly policyRevision: GameProvenanceAlias;
	};
	readonly sources: readonly GameModelContextSourceProvenance[];
	readonly tools: readonly GameModelToolProvenance[];
	readonly images: readonly GameModelImageProvenance[];
}

export interface GameModelResponseProvenance {
	readonly completedAt: string;
	readonly servedModel: GameProvenanceAlias;
	readonly stopReason: "stop" | "length" | "toolUse" | "error" | "aborted" | "deferred";
	readonly outcome: "completed" | "failed" | "cancelled" | "deferred";
	readonly errorKind: "none" | "provider" | "cancelled";
	readonly cost: GameModelCostProvenance;
	readonly usage:
		| Readonly<{
				kind: "reported";
				input: number;
				output: number;
				cacheRead: number;
				cacheWrite: number;
				cacheWrite1h?: number;
				reasoning?: number;
				totalTokens: number;
		  }>
		| Readonly<{ kind: "unreported" }>;
}

export type GameProviderUsageObservation = "reported" | "unreported";

export type GameProviderPricingBasis =
	| Readonly<{ kind: "pi-catalog" }>
	| Readonly<{ kind: "host-rate-card" }>
	| Readonly<{ kind: "free" }>
	| Readonly<{ kind: "unpriced" }>;

export interface GameModelUsdCostProvenance {
	readonly input: number;
	readonly output: number;
	readonly cacheRead: number;
	readonly cacheWrite: number;
	readonly total: number;
}

export type GameModelCostProvenance =
	| Readonly<{
			kind: "calculated";
			source: "pi-catalog" | "host-rate-card";
			usd: Readonly<GameModelUsdCostProvenance>;
	  }>
	| Readonly<{ kind: "unknown"; source: "pi-catalog" | "host-rate-card" }>
	| Readonly<{ kind: "free"; usd: Readonly<GameModelUsdCostProvenance> }>
	| Readonly<{ kind: "unpriced" }>;

export interface GameProviderProvenanceSink {
	recordRequest(request: Readonly<GameModelRequestProvenance>): Promise<GameProvenanceAlias>;
	recordResponse(requestRecordId: GameProvenanceAlias, response: Readonly<GameModelResponseProvenance>): Promise<void>;
}

/**
 * Bind provider provenance to a durable transcript and one private local scope.
 *
 * `scopeId` and lineage identifiers are host-owned lookup values. The Agent
 * persists only deterministic local correlation aliases for them. A non-empty
 * system prompt requires a matching `baseInstructions` fingerprint, and a
 * context transformer requires an immutable deterministic implementation
 * reference.
 */
export interface GameProviderProvenanceBinding {
	readonly sink: GameProviderProvenanceSink;
	readonly scopeId: string;
	readonly baseInstructions?: GameModelContextLineageReference;
	readonly contextTransformer?: GameModelContextLineageReference;
}

export interface GameProviderAttempt {
	readonly index: string;
	/** Discard only a synchronously rejected, uncommitted target. It creates no provenance record. */
	discard(): void;
	/** Persist this target before starting its Pi-compatible provider stream. */
	commit(start: () => ReturnType<StreamFn>): Promise<AssistantMessageEventStream>;
	/** Capture whether the exact terminal Pi message contains provider-reported usage before settlement. */
	captureTerminal(message: AssistantMessage, usageObservation: GameProviderUsageObservation): void;
	/** Settle one consumed attempt. The final returned attempt is settled by GameAgent after transcript commit. */
	settle(message: AssistantMessage): Promise<void>;
}

export interface GameProviderAttemptLifecycle {
	begin(targetModel: Model<Api>, pricingBasis?: Readonly<GameProviderPricingBasis>): GameProviderAttempt;
}

export type GameProviderAttemptStreamFn = (
	requestedModel: Parameters<StreamFn>[0],
	context: Parameters<StreamFn>[1],
	options: Parameters<StreamFn>[2],
	attempts: GameProviderAttemptLifecycle,
) => ReturnType<StreamFn>;

export type GameModelStream = StreamFn | { readonly kind: "attempts"; readonly stream: GameProviderAttemptStreamFn };

export interface GameToolExecution {
	readonly coordinate: RunCoordinate;
	readonly toolCallId: string;
	readonly toolName: string;
	readonly arguments: Readonly<Record<string, JsonValue>>;
	readonly descriptorFingerprint: string;
	readonly catalogRevision: string;
	readonly policyRevision: string;
}

export interface GameToolResultTransformContext extends GameToolExecution {
	readonly input: Readonly<NormalizedGameInput>;
	readonly isError: boolean;
}

/**
 * Optionally replace only the model-visible content of one already-normalized Pi Tool result.
 * Omitted content preserves the result exactly; all other result fields remain Pi-owned.
 */
export type GameToolResultTransformer = (
	result: Readonly<AgentToolResult<unknown>>,
	context: Readonly<GameToolResultTransformContext>,
	signal: AbortSignal,
) => AgentToolResult<unknown>["content"] | undefined | Promise<AgentToolResult<unknown>["content"] | undefined>;

/**
 * Perform effectful, argument-level authorization immediately before execution.
 * Pi may invoke this concurrently for parallel Tools; use sequential Tool mode or
 * make the guard concurrency safe when authorization state is shared.
 */
export type GameToolExecutionGuard = (
	input: Readonly<NormalizedGameInput>,
	execution: Readonly<GameToolExecution>,
	signal: AbortSignal,
) => void | Promise<void>;

/**
 * Detect one exact sequence of equal, fully prepared Tool calls within a game run.
 *
 * Enabling the policy requires both host-selected thresholds. The Agent supplies
 * no cadence defaults. Exempt Tools are ignored without resetting a tracked
 * sequence so polling cannot hide a repeating effectful call.
 */
export interface GameExactToolRepeatPolicy {
	readonly advisoryThreshold: number;
	readonly terminationThreshold: number;
	readonly exemptToolNames?: readonly string[];
}

export interface GameProjectedInput {
	readonly message: UserMessage;
	readonly transcriptContent: readonly MessageContent[];
}

export interface GameInputProjectionLimits {
	readonly maxContentParts: number;
	readonly maxTextBytes: number;
	/** Aggregate canonical JSON bytes for every image-reference metadata record in one projected input. */
	readonly maxReferenceBytes: number;
	readonly maxImageBytes: number;
	readonly maxTotalImageBytes: number;
}

export type GameInputProjector = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
) => GameProjectedInput | Promise<GameProjectedInput>;

export type GameTurnStopPolicy = (
	input: Readonly<NormalizedGameInput>,
	completedCoordinate: Readonly<RunCoordinate>,
	signal: AbortSignal,
) => boolean | Promise<boolean>;

export type CanonicalEventSink = (
	coordinate: Readonly<RunCoordinate>,
	data: Readonly<CanonicalEventData>,
) => Promise<CanonicalEvent>;

export interface GameAgentOptions extends StreamKey {
	model: Model<Api>;
	modelStream: GameModelStream;
	emitEvent: CanonicalEventSink;
	systemPrompt?: string;
	toolSource?: GameToolSource;
	requestSources?: readonly GameRequestSource[];
	toolPolicy?: GameToolPolicy;
	toolLimits?: GameToolSnapshotLimits;
	exactToolRepeatPolicy?: GameExactToolRepeatPolicy;
	beforeToolExecution?: GameToolExecutionGuard;
	contextSources?: readonly GameContextSource[];
	contextLimits?: GameContextLimits;
	transformContext?: GameContextTransformer;
	projectInput?: GameInputProjector;
	inputProjectionLimits?: GameInputProjectionLimits;
	/** Stop before another provider turn once completed assistant messages report this many tokens in one run. */
	maxReportedTokensPerRun?: number;
	shouldStopAfterTurn?: GameTurnStopPolicy;
	transcript?: GameAgentTranscriptBinding;
	transcriptCompactor?: TranscriptCompactor;
	deferredResponses?: GameDeferredResponseAdapter;
	providerProvenance?: GameProviderProvenanceBinding;
	newId?: () => string;
	now?: () => number;
	/** Finite monotonic milliseconds used only for content-free run performance events. */
	metricsNow?: () => number;
}

export interface GameRunHandle {
	runId: string;
	initialCoordinate: RunCoordinate;
	completion: Promise<GameRunResult>;
}

export type GameRunResult =
	| { runId: string; status: "completed"; finalOutput?: string }
	| { runId: string; status: "aborted" | "failed" }
	| { runId: string; status: "deferred"; operationId: string };

export type GameDeferredOperation = Readonly<DurableTranscriptDeferredOpenedRecord>;

export interface GameDeferredAbandonResult {
	operationId: string;
	providerCancellation: "completed" | "failed" | "unavailable";
}

export type GameControlCommand =
	| { type: "abort"; reason?: unknown }
	| { type: "steer"; text: string }
	| { type: "follow-up"; text: string };

export type GameControlResult =
	| { accepted: true; coordinate: RunCoordinate }
	| {
			accepted: false;
			reason: "no-active-run" | "coordinate-mismatch" | "invalid-message" | "run-not-active";
			activeCoordinate?: RunCoordinate;
	  };

export type CanonicalEventListener = (event: Readonly<CanonicalEvent>) => void | Promise<void>;

export type PiAgentEvent = AgentEvent;
