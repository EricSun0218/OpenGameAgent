import {
	type Api,
	type AssistantMessage,
	type AssistantMessageEvent,
	type AssistantMessageEventStream,
	type AuthCheck,
	type AuthContext,
	type Context,
	type CredentialStore,
	createAssistantMessageEventStream,
	createModels,
	type Model,
	type ModelCost,
	type Provider,
	type ProviderAuth,
} from "@earendil-works/pi-ai";
import type {
	GameProviderAttempt,
	GameProviderAttemptLifecycle,
	GameProviderAttemptStreamFn,
	GameProviderUsageObservation,
} from "@opengameagent/agent";
import {
	RuntimePassiveHealthDependency,
	type RuntimeDependencyDefinition,
	type RuntimeDependencyInspection,
} from "@opengameagent/health";
import {
	createProviderCredentialStore,
	type ProviderCredentialBinding,
	requireReferencedCredential,
} from "./provider-credential-store.js";
import {
	classifyProviderFailure,
	isProviderFallbackEligible,
	isProviderFallbackFailureKind,
	type ProviderFailureEvidenceReader,
	type ProviderFailureClassification,
	type ProviderFailureFact,
	type ProviderFallbackFailureKind,
	readProviderFailureEvidence,
} from "./provider-failures.js";

export type ProviderProfilePricing =
	| Readonly<{ kind: "pi-catalog" }>
	| Readonly<{ kind: "host-rate-card"; rates: ModelCost }>
	| Readonly<{ kind: "free" }>
	| Readonly<{ kind: "unpriced" }>;

export type ProviderProfilePricingSnapshot =
	| Readonly<{ kind: "pi-catalog"; rates: Readonly<ModelCost> }>
	| Readonly<{ kind: "host-rate-card"; rates: Readonly<ModelCost> }>
	| Readonly<{ kind: "free" }>
	| Readonly<{ kind: "unpriced" }>;

export interface ProviderProfile {
	readonly provider: Provider;
	readonly credential: ProviderCredentialBinding;
	/** Optional identity channel populated by a provider Adapter before Pi flattens a terminal failure. */
	readonly failureEvidence?: ProviderFailureEvidenceReader;
}

/** Safe ordered route configuration. Provider internals and credential references live only in `profiles`. */
export interface ProviderProfileTarget {
	readonly profileId: string;
	readonly modelId: string;
	readonly supportsTools: boolean;
	readonly pricing: ProviderProfilePricing;
	/** Typed presemantic failure kinds that may advance to the next ordered target. */
	readonly fallbackOn?: readonly ProviderFallbackFailureKind[];
}

export interface ProviderProfileTargetSnapshot {
	readonly profileId: string;
	readonly modelId: string;
	readonly name: string;
	readonly api: Api;
	readonly reasoning: boolean;
	readonly input: readonly ("text" | "image")[];
	readonly contextWindow: number;
	readonly maxTokens: number;
	readonly supportsTools: boolean;
	readonly pricing: ProviderProfilePricingSnapshot;
	readonly fallbackOn?: readonly ProviderFallbackFailureKind[];
}

export interface ProviderProfileSnapshot {
	readonly targets: readonly ProviderProfileTargetSnapshot[];
}

export interface ProviderProfileConfiguration {
	readonly profiles: readonly ProviderProfile[];
	readonly targets: readonly ProviderProfileTarget[];
}

declare const capturedProviderProfileConfigurationBrand: unique symbol;

/**
 * Request-ready provider configuration whose public surface is intentionally limited to the safe target snapshot.
 * Create it with `captureProviderProfileConfiguration`; it cannot be reconstructed from the snapshot alone.
 */
export interface CapturedProviderProfileConfiguration {
	readonly snapshot: ProviderProfileSnapshot;
	readonly [capturedProviderProfileConfigurationBrand]: never;
}

export interface ProviderProfileModelStreamOptions {
	/** Called synchronously once per actual model request. */
	readonly configuration: () => Readonly<ProviderProfileConfiguration> | Readonly<CapturedProviderProfileConfiguration>;
	/** Host-owned opaque-reference backend. It is consulted only by `reference` bindings. */
	readonly credentialReferences?: CredentialStore;
	/** Optional passive health projection; it never performs a provider request. */
	readonly health?: {
		readonly id: string;
		readonly requirement: "required" | "optional";
		readonly maxObservationAgeMs: number;
		readonly now: () => number;
	};
}

export interface ProviderProfileModelStream {
	readonly kind: "attempts";
	readonly stream: GameProviderAttemptStreamFn;
	healthDependencies(): readonly RuntimeDependencyDefinition[];
}

export type ProviderProfileErrorCode =
	| "invalid_configuration"
	| "duplicate_profile"
	| "profile_unavailable"
	| "model_unavailable"
	| "tools_unsupported"
	| "images_unsupported"
	| "credential_binding_invalid";

export class ProviderProfileError extends TypeError {
	constructor(public readonly code: ProviderProfileErrorCode) {
		super(code);
		this.name = "ProviderProfileError";
	}
}

interface CapturedProviderProfile {
	readonly binding: ProviderCredentialBinding;
	readonly failureEvidence: ProviderFailureEvidenceReader | undefined;
	readonly models: readonly Model<Api>[];
	readonly provider: Provider;
}

interface ConfiguredProviderProfile {
	readonly provider: Provider;
	readonly providerId: string;
	readonly binding: ProviderCredentialBinding;
	readonly failureEvidence: ProviderFailureEvidenceReader | undefined;
}

interface CapturedProviderTarget {
	readonly fallbackOn: ReadonlySet<ProviderFallbackFailureKind>;
	readonly failureEvidence: ProviderFailureEvidenceReader | undefined;
	readonly model: Model<Api>;
	readonly snapshot: ProviderProfileTargetSnapshot;
	readonly supportsTools: boolean;
}

interface CapturedProviderTargetConfiguration {
	readonly profileId: string;
	readonly modelId: string;
	readonly supportsTools: boolean;
	readonly pricing: ProviderProfilePricing;
	readonly fallbackOn: readonly ProviderFallbackFailureKind[];
	readonly profile: ConfiguredProviderProfile;
	readonly selectedModelIds: ReadonlySet<string>;
}

interface CapturedProviderRequest {
	readonly bindings: ReadonlyMap<string, ProviderCredentialBinding>;
	readonly providers: readonly Provider[];
	readonly snapshot: ProviderProfileSnapshot;
	readonly targets: readonly CapturedProviderTarget[];
}

const capturedProviderRequests = new WeakMap<object, CapturedProviderRequest>();

type ApiKeyAuth = NonNullable<ProviderAuth["apiKey"]>;
type ApiKeyCheckInput = Parameters<NonNullable<ApiKeyAuth["check"]>>[0];
type ApiKeyResolveInput = Parameters<ApiKeyAuth["resolve"]>[0];

const NO_AMBIENT_AUTH: AuthContext = Object.freeze({
	env: async () => undefined,
	fileExists: async () => false,
});

const FREE_RATES: ModelCost = Object.freeze({ input: 0, output: 0, cacheRead: 0, cacheWrite: 0 });
const UNPRICED_PI_COST_PLACEHOLDER: ModelCost = Object.freeze({
	input: 0,
	output: 0,
	cacheRead: 0,
	cacheWrite: 0,
});

const EMPTY_USAGE = Object.freeze({
	input: 0,
	output: 0,
	cacheRead: 0,
	cacheWrite: 0,
	totalTokens: 0,
	cost: Object.freeze({ input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 }),
});

type DoneEvent = Extract<AssistantMessageEvent, { type: "done" }>;
type ErrorEvent = Extract<AssistantMessageEvent, { type: "error" }>;

const DONE_STOP_REASONS = Object.freeze({
	stop: true,
	length: true,
	toolUse: true,
	deferred: true,
} satisfies Readonly<Record<DoneEvent["reason"], true>>);
const ERROR_STOP_REASONS = Object.freeze({
	error: true,
	aborted: true,
} satisfies Readonly<Record<ErrorEvent["reason"], true>>);

export function createProviderProfileModelStream(
	options: ProviderProfileModelStreamOptions,
): ProviderProfileModelStream {
	const configuration = options.configuration;
	const credentialReferences = options.credentialReferences;
	const healthOptions = options.health;
	if (typeof configuration !== "function") throw new ProviderProfileError("invalid_configuration");
	const health = healthOptions ? new RuntimePassiveHealthDependency({ ...healthOptions, kind: "provider" }) : undefined;
	const healthDependencies = Object.freeze(health ? [health.definition] : []);
	const capturedByConfiguration = new WeakMap<object, CapturedProviderRequest>();
	const modelsByConfiguration = new WeakMap<CapturedProviderRequest, ReturnType<typeof createModels>>();

	const stream: GameProviderAttemptStreamFn = async (_requestedModel, context, streamOptions, attempts) => {
		streamOptions?.signal?.throwIfAborted();
		const captured = resolveProviderRequest(configuration(), capturedByConfiguration);
		const target = captured.targets[0];
		if (!target) throw new ProviderProfileError("invalid_configuration");
		if (captured.targets.some((candidate) => !candidate.supportsTools) && (context.tools?.length ?? 0) > 0) {
			throw new ProviderProfileError("tools_unsupported");
		}
		if (
			captured.targets.some((candidate) => !candidate.model.input.includes("image")) &&
			context.messages.some(messageContainsImage)
		) {
			throw new ProviderProfileError("images_unsupported");
		}

		let models = modelsByConfiguration.get(captured);
		if (!models) {
			models = createModels({
				credentials: createProviderCredentialStore({
					bindings: captured.bindings,
					...(credentialReferences ? { references: credentialReferences } : {}),
				}),
			});
			for (const provider of captured.providers) models.setProvider(provider);
			modelsByConfiguration.set(captured, models);
		}

		const initial = await dispatchProviderTarget({
			target,
			context,
			streamOptions,
			attempts,
			models,
			bindings: captured.bindings,
			health,
		});
		const output = createAssistantMessageEventStream();
		void relayProviderTargets({
			output,
			captured,
			index: 0,
			initialModel: target.model,
			attempt: initial.attempt,
			source: initial.source,
			context,
			streamOptions,
			attempts,
			models,
			health,
		});
		return output;
	};

	return Object.freeze({
		kind: "attempts" as const,
		stream,
		healthDependencies: () => healthDependencies,
	});
}

interface DispatchedProviderTarget {
	readonly attempt: GameProviderAttempt;
	readonly source: AssistantMessageEventStream;
}

interface RelayOutput {
	readonly stream: AssistantMessageEventStream;
	started: boolean;
}

type ProviderTargetRelayDecision =
	| Readonly<{ kind: "advance"; message: AssistantMessage }>
	| Readonly<{ kind: "terminal" }>;

async function dispatchProviderTarget(input: {
	readonly target: CapturedProviderTarget;
	readonly context: Parameters<GameProviderAttemptStreamFn>[1];
	readonly streamOptions: Parameters<GameProviderAttemptStreamFn>[2];
	readonly attempts: GameProviderAttemptLifecycle;
	readonly models: ReturnType<typeof createModels>;
	readonly bindings: ReadonlyMap<string, ProviderCredentialBinding>;
	readonly health: RuntimePassiveHealthDependency | undefined;
}): Promise<DispatchedProviderTarget> {
	input.streamOptions?.signal?.throwIfAborted();
	let enteredProviderStart = false;
	try {
		const attempt = input.attempts.begin(input.target.model, input.target.snapshot.pricing);
		const source = await attempt.commit(() => {
			const requestOptions = {
				...captureRequestOptions(input.streamOptions, input.bindings.get(input.target.model.provider)),
				maxRetries: 0,
			};
			enteredProviderStart = true;
			return input.models.streamSimple(input.target.model, input.context, requestOptions);
		});
		return { attempt, source };
	} catch (error) {
		if (enteredProviderStart && !input.streamOptions?.signal?.aborted) {
			observeProviderHealth(input.health, { state: "unavailable", reason: "unknown" });
		}
		throw error;
	}
}

async function relayProviderTargets(input: {
	readonly output: AssistantMessageEventStream;
	readonly captured: CapturedProviderRequest;
	readonly index: number;
	readonly initialModel: Readonly<Model<Api>>;
	readonly attempt: GameProviderAttempt;
	readonly source: AssistantMessageEventStream;
	readonly context: Parameters<GameProviderAttemptStreamFn>[1];
	readonly streamOptions: Parameters<GameProviderAttemptStreamFn>[2];
	readonly attempts: GameProviderAttemptLifecycle;
	readonly models: ReturnType<typeof createModels>;
	readonly health: RuntimePassiveHealthDependency | undefined;
}): Promise<void> {
	const relay: RelayOutput = { stream: input.output, started: false };
	let index = input.index;
	let attempt = input.attempt;
	let source = input.source;
	let currentModel = input.initialModel;
	try {
		while (true) {
			const target = input.captured.targets[index];
			if (!target) throw new ProviderProfileError("invalid_configuration");
			currentModel = target.model;
			const decision = await consumeProviderTarget({
				relay,
				target,
				attempt,
				source,
				hasNext: index + 1 < input.captured.targets.length,
				signal: input.streamOptions?.signal,
				health: input.health,
				usedFallback: index > 0,
			});
			if (decision.kind === "terminal") return;
			const failedMessage = decision.message;
			try {
				await attempt.settle(failedMessage);
			} catch {
				pushTerminal(relay, target.model, {
					type: "error",
					reason: failedMessage.stopReason === "aborted" ? "aborted" : "error",
					error: failedMessage,
				});
				return;
			}
			if (input.streamOptions?.signal?.aborted) {
				pushSyntheticFailure(relay, target.model, "aborted", failedMessage);
				return;
			}

			index += 1;
			const next = input.captured.targets[index];
			if (!next) throw new ProviderProfileError("invalid_configuration");
			const dispatched = await dispatchProviderTarget({
				target: next,
				context: input.context,
				streamOptions: input.streamOptions,
				attempts: input.attempts,
				models: input.models,
				bindings: input.captured.bindings,
				health: input.health,
			});
			currentModel = next.model;
			attempt = dispatched.attempt;
			source = dispatched.source;
		}
	} catch {
		pushSyntheticFailure(relay, currentModel, input.streamOptions?.signal?.aborted ? "aborted" : "error");
	}
}

async function consumeProviderTarget(input: {
	readonly relay: RelayOutput;
	readonly target: CapturedProviderTarget;
	readonly attempt: GameProviderAttempt;
	readonly source: AssistantMessageEventStream;
	readonly hasNext: boolean;
	readonly signal: AbortSignal | undefined;
	readonly health: RuntimePassiveHealthDependency | undefined;
	readonly usedFallback: boolean;
}): Promise<ProviderTargetRelayDecision> {
	let sawStart = false;
	let semantic = false;
	let settledResult = false;
	let sourceResult: AssistantMessage | undefined;
	void input.source.result().then(
		(message) => {
			settledResult = true;
			sourceResult = message;
		},
		() => {
			settledResult = true;
		},
	);

	for await (const event of observeProviderEvents(input.source, input.signal, input.health)) {
		const eventType = event.type;
		switch (eventType) {
			case "start":
				if (input.signal?.aborted) return consumeCallerAbort(input);
				if (sawStart || !matchesTarget(input.target.model, event.partial)) {
					observeProviderHealth(input.health, { state: "unavailable", reason: "dependency-failure" });
					const failure = syntheticFailureMessage(input.target.model, "error");
					captureSyntheticTerminal(input.attempt, failure);
					pushSyntheticFailure(input.relay, input.target.model, "error", failure);
					return { kind: "terminal" };
				}
				sawStart = true;
				break;
			case "text_start":
			case "text_delta":
			case "text_end":
			case "thinking_start":
			case "thinking_delta":
			case "thinking_end":
			case "toolcall_start":
			case "toolcall_delta":
			case "toolcall_end":
				if (input.signal?.aborted) return consumeCallerAbort(input);
				if (!sawStart || !matchesTarget(input.target.model, event.partial)) {
					observeProviderHealth(input.health, { state: "unavailable", reason: "dependency-failure" });
					const failure = syntheticFailureMessage(input.target.model, "error");
					captureSyntheticTerminal(input.attempt, failure);
					pushSyntheticFailure(input.relay, input.target.model, "error", failure);
					return { kind: "terminal" };
				}
				semantic = true;
				pushSyntheticStart(input.relay, input.target.model, event.partial.timestamp);
				input.relay.stream.push(event);
				break;
			case "done": {
				const capturedEvent = captureDoneEvent(event);
				if (input.signal?.aborted) {
					return consumeCallerAbort(input, abortEvidenceForDoneEvent(input.target, capturedEvent));
				}
				if (!validDoneEvent(input.target.model, capturedEvent)) {
					observeProviderHealth(input.health, { state: "unavailable", reason: "dependency-failure" });
					const failure = syntheticFailureMessage(input.target.model, "error");
					captureSyntheticTerminal(input.attempt, failure);
					pushSyntheticFailure(input.relay, input.target.model, "error", failure);
					return { kind: "terminal" };
				}
				observeProviderHealth(
					input.health,
					input.usedFallback ? { state: "degraded", reason: "dependency-failure" } : { state: "ready" },
				);
				pushTerminal(input.relay, input.target.model, capturedEvent);
				return { kind: "terminal" };
			}
			case "error": {
				const captured = captureErrorEvent(input.target, event);
				if (input.signal?.aborted) {
					return consumeCallerAbort(input, abortEvidenceForErrorEvent(input.target, captured.event, captured.fact));
				}
				return consumeProviderError({ ...input, event: captured.event, fact: captured.fact, semantic });
			}
			default: {
				if (input.signal?.aborted) return consumeCallerAbort(input);
				observeProviderHealth(input.health, { state: "unavailable", reason: "dependency-failure" });
				const failure = syntheticFailureMessage(input.target.model, "error");
				captureSyntheticTerminal(input.attempt, failure);
				pushSyntheticFailure(input.relay, input.target.model, "error", failure);
				return { kind: "terminal" };
			}
		}
	}

	await Promise.resolve();
	const capturedResult = settledResult && sourceResult ? captureProviderResult(input.target, sourceResult) : undefined;
	if (input.signal?.aborted) return consumeCallerAbort(input, abortEvidenceForResult(input.target, capturedResult));
	if (capturedResult) {
		const stopReason = capturedResult.message.stopReason;
		if (isErrorStopReason(stopReason)) {
			return consumeProviderError({
				...input,
				event: { type: "error", reason: stopReason, error: capturedResult.message },
				fact: capturedResult.fact,
				semantic,
			});
		}
		const event: DoneEvent = {
			type: "done",
			reason: stopReason as DoneEvent["reason"],
			message: capturedResult.message,
		};
		if (isDoneStopReason(stopReason) && validDoneEvent(input.target.model, event)) {
			observeProviderHealth(
				input.health,
				input.usedFallback ? { state: "degraded", reason: "dependency-failure" } : { state: "ready" },
			);
			pushTerminal(input.relay, input.target.model, event);
			return { kind: "terminal" };
		}
	}
	observeProviderHealth(input.health, { state: "unavailable", reason: "dependency-failure" });
	const failure = syntheticFailureMessage(input.target.model, "error");
	captureSyntheticTerminal(input.attempt, failure);
	pushSyntheticFailure(input.relay, input.target.model, "error", failure);
	return { kind: "terminal" };
}

async function* observeProviderEvents(
	source: AssistantMessageEventStream,
	signal: AbortSignal | undefined,
	health: RuntimePassiveHealthDependency | undefined,
): AsyncIterable<AssistantMessageEvent> {
	try {
		yield* source;
	} catch (error) {
		if (!signal?.aborted) observeProviderHealth(health, { state: "unavailable", reason: "unknown" });
		throw error;
	}
}

interface ProviderTerminalEvidence {
	readonly message: AssistantMessage;
	readonly usage: GameProviderUsageObservation;
}

interface CapturedErrorEvent {
	readonly event: ErrorEvent;
	readonly fact: ProviderFailureFact | undefined;
}

interface CapturedProviderResult {
	readonly message: AssistantMessage;
	readonly fact: ProviderFailureFact | undefined;
}

function consumeCallerAbort(
	input: {
		readonly relay: RelayOutput;
		readonly target: CapturedProviderTarget;
		readonly attempt: GameProviderAttempt;
	},
	evidence?: ProviderTerminalEvidence,
): ProviderTargetRelayDecision {
	const failure = syntheticFailureFromEvidence(input.target.model, "aborted", evidence);
	captureSyntheticTerminal(input.attempt, failure, evidence?.usage);
	pushSyntheticFailure(input.relay, input.target.model, "aborted", failure);
	return { kind: "terminal" };
}

function abortEvidenceForDoneEvent(
	target: CapturedProviderTarget,
	event: DoneEvent,
): ProviderTerminalEvidence | undefined {
	return validDoneEvent(target.model, event) ? { message: event.message, usage: "reported" } : undefined;
}

function abortEvidenceForErrorEvent(
	target: CapturedProviderTarget,
	event: ErrorEvent,
	fact: ProviderFailureFact | undefined,
): ProviderTerminalEvidence | undefined {
	return validErrorEvent(target.model, event)
		? { message: event.error, usage: fact?.usage ?? "unreported" }
		: undefined;
}

function abortEvidenceForResult(
	target: CapturedProviderTarget,
	result: CapturedProviderResult | undefined,
): ProviderTerminalEvidence | undefined {
	if (!result) return undefined;
	const stopReason = result.message.stopReason;
	if (isErrorStopReason(stopReason)) {
		return abortEvidenceForErrorEvent(
			target,
			{ type: "error", reason: stopReason, error: result.message },
			result.fact,
		);
	}
	if (!isDoneStopReason(stopReason)) return undefined;
	return abortEvidenceForDoneEvent(target, { type: "done", reason: stopReason, message: result.message });
}

function consumeProviderError(input: {
	readonly relay: RelayOutput;
	readonly target: CapturedProviderTarget;
	readonly attempt: GameProviderAttempt;
	readonly event: ErrorEvent;
	readonly fact: ProviderFailureFact | undefined;
	readonly semantic: boolean;
	readonly hasNext: boolean;
	readonly signal: AbortSignal | undefined;
	readonly health: RuntimePassiveHealthDependency | undefined;
	readonly usedFallback: boolean;
}): ProviderTargetRelayDecision {
	const { event, fact, target, signal } = input;
	if (!validErrorEvent(target.model, event)) {
		observeProviderHealth(input.health, { state: "unavailable", reason: "dependency-failure" });
		const failure = syntheticFailureMessage(target.model, "error");
		captureSyntheticTerminal(input.attempt, failure);
		pushSyntheticFailure(input.relay, target.model, "error", failure);
		return { kind: "terminal" };
	}
	const classification = classifyProviderFailure(
		event.reason === "aborted" ? { type: "aborted", reason: signal?.reason, usage: fact?.usage ?? "unreported" } : fact,
		signal,
	);
	if (classification.kind === "aborted") {
		if (event.reason === "aborted") {
			input.attempt.captureTerminal(event.error, fact?.usage ?? "unreported");
			pushTerminal(input.relay, target.model, event);
		} else {
			const evidence = { message: event.error, usage: fact?.usage ?? "unreported" } satisfies ProviderTerminalEvidence;
			const failure = syntheticFailureFromEvidence(target.model, "aborted", evidence);
			captureSyntheticTerminal(input.attempt, failure, evidence.usage);
			pushSyntheticFailure(input.relay, target.model, "aborted", failure);
		}
		return { kind: "terminal" };
	}
	const terminalSemantic = input.semantic || event.error.content.length > 0 || event.error.deferred !== undefined;
	if (!terminalSemantic && input.hasNext && isProviderFallbackEligible(classification, target.fallbackOn)) {
		input.attempt.captureTerminal(event.error, fact?.usage ?? "unreported");
		return { kind: "advance", message: event.error };
	}
	observeProviderHealth(input.health, providerFailureHealth(classification));
	input.attempt.captureTerminal(event.error, fact?.usage ?? "unreported");
	pushTerminal(input.relay, target.model, event);
	return { kind: "terminal" };
}

function providerFailureHealth(
	classification: Exclude<ProviderFailureClassification, { readonly kind: "aborted" }>,
): RuntimeDependencyInspection {
	switch (classification.kind) {
		case "credential":
		case "authentication":
			return { state: "unavailable", reason: "authentication-required" };
		case "permission":
			return { state: "unavailable", reason: "permission-denied" };
		case "timeout":
			return { state: "unavailable", reason: "timeout" };
		case "transport":
			return { state: "unavailable", reason: "transport" };
		case "quota":
		case "throttled":
		case "capacity":
			return { state: "unavailable", reason: "capacity" };
		case "invalid-request":
		case "context-window":
			return { state: "unavailable", reason: "misconfigured" };
		case "unsupported":
			return { state: "unavailable", reason: "capability-missing" };
		case "provider-internal":
		case "protocol-invalid":
			return { state: "unavailable", reason: "dependency-failure" };
		case "unknown":
			return { state: "unavailable", reason: "unknown" };
	}
}

function observeProviderHealth(
	health: RuntimePassiveHealthDependency | undefined,
	inspection: RuntimeDependencyInspection,
): void {
	try {
		health?.observe(inspection);
	} catch {
		// A host health clock must never change provider stream authority.
	}
}

function captureDoneEvent(source: DoneEvent): DoneEvent {
	const reason = source.reason as unknown;
	const message = source.message as unknown;
	return {
		type: "done",
		reason: reason as DoneEvent["reason"],
		message: structuredClone(message) as AssistantMessage,
	};
}

function captureErrorEvent(target: CapturedProviderTarget, source: ErrorEvent): CapturedErrorEvent {
	const reason = source.reason as unknown;
	const originalMessage = source.error as unknown;
	const fact = readProviderFailureEvidence(target.failureEvidence, originalMessage as AssistantMessage);
	return {
		event: {
			type: "error",
			reason: reason as ErrorEvent["reason"],
			error: structuredClone(originalMessage) as AssistantMessage,
		},
		fact,
	};
}

function captureProviderResult(
	target: CapturedProviderTarget,
	originalMessage: AssistantMessage,
): CapturedProviderResult {
	const fact = readProviderFailureEvidence(target.failureEvidence, originalMessage);
	return { message: structuredClone(originalMessage), fact };
}

function validDoneEvent(model: Readonly<Model<Api>>, event: DoneEvent): boolean {
	const reason = event.reason as unknown;
	const message = event.message;
	return isDoneStopReason(reason) && reason === message.stopReason && matchesTarget(model, message);
}

function validErrorEvent(model: Readonly<Model<Api>>, event: ErrorEvent): boolean {
	const reason = event.reason as unknown;
	const message = event.error;
	return isErrorStopReason(reason) && reason === message.stopReason && matchesTarget(model, message);
}

function isDoneStopReason(value: unknown): value is DoneEvent["reason"] {
	return typeof value === "string" && Object.hasOwn(DONE_STOP_REASONS, value);
}

function isErrorStopReason(value: unknown): value is ErrorEvent["reason"] {
	return typeof value === "string" && Object.hasOwn(ERROR_STOP_REASONS, value);
}

function matchesTarget(model: Readonly<Model<Api>>, message: Readonly<AssistantMessage>): boolean {
	return message.provider === model.provider && message.api === model.api && message.model === model.id;
}

function pushTerminal(relay: RelayOutput, model: Readonly<Model<Api>>, event: DoneEvent | ErrorEvent): void {
	pushSyntheticStart(relay, model, event.type === "done" ? event.message.timestamp : event.error.timestamp);
	relay.stream.push(event);
}

function pushSyntheticStart(relay: RelayOutput, model: Readonly<Model<Api>>, timestamp: number): void {
	if (relay.started) return;
	relay.started = true;
	relay.stream.push({
		type: "start",
		partial: {
			role: "assistant",
			content: [],
			api: model.api,
			provider: model.provider,
			model: model.id,
			usage: EMPTY_USAGE,
			stopReason: "pending",
			timestamp,
		},
	});
}

function pushSyntheticFailure(
	relay: RelayOutput,
	model: Readonly<Model<Api>>,
	reason: "error" | "aborted",
	base?: Readonly<AssistantMessage>,
): void {
	const message =
		base?.stopReason === reason && matchesTarget(model, base)
			? (base as AssistantMessage)
			: syntheticFailureMessage(model, reason);
	pushSyntheticStart(relay, model, message.timestamp);
	relay.stream.push({ type: "error", reason, error: message });
}

function syntheticFailureMessage(model: Readonly<Model<Api>>, reason: "error" | "aborted"): AssistantMessage {
	return {
		role: "assistant",
		content: [],
		api: model.api,
		provider: model.provider,
		model: model.id,
		usage: EMPTY_USAGE,
		stopReason: reason,
		errorMessage: reason === "aborted" ? "Provider request was aborted." : "Provider stream failed.",
		timestamp: Date.now(),
	};
}

function syntheticFailureFromEvidence(
	model: Readonly<Model<Api>>,
	reason: "error" | "aborted",
	evidence: ProviderTerminalEvidence | undefined,
): AssistantMessage {
	const message = syntheticFailureMessage(model, reason);
	if (!evidence) return message;
	return {
		...message,
		...(evidence.message.responseModel === undefined ? {} : { responseModel: evidence.message.responseModel }),
		...(evidence.message.responseId === undefined ? {} : { responseId: evidence.message.responseId }),
		usage: evidence.usage === "reported" ? structuredClone(evidence.message.usage) : EMPTY_USAGE,
		timestamp: evidence.message.timestamp,
	};
}

function captureSyntheticTerminal(
	attempt: GameProviderAttempt,
	message: AssistantMessage,
	usage: GameProviderUsageObservation = "unreported",
): void {
	attempt.captureTerminal(message, usage);
}

export function captureProviderProfileSnapshot(
	configuration: Readonly<ProviderProfileConfiguration>,
): ProviderProfileSnapshot {
	return captureProviderRequest(configuration).snapshot;
}

export function captureProviderProfileConfiguration(
	configuration: Readonly<ProviderProfileConfiguration>,
): CapturedProviderProfileConfiguration {
	const captured = captureProviderRequest(configuration);
	const opaque = Object.freeze({ snapshot: captured.snapshot }) as CapturedProviderProfileConfiguration;
	capturedProviderRequests.set(opaque, captured);
	return opaque;
}

function resolveProviderRequest(
	configuration: Readonly<ProviderProfileConfiguration> | Readonly<CapturedProviderProfileConfiguration>,
	capturedByConfiguration?: WeakMap<object, CapturedProviderRequest>,
): CapturedProviderRequest {
	if (typeof configuration === "object" && configuration !== null) {
		const captured = capturedProviderRequests.get(configuration);
		if (captured) return captured;
		const cached = capturedByConfiguration?.get(configuration);
		if (cached) return cached;
	}
	const captured = captureProviderRequest(configuration as Readonly<ProviderProfileConfiguration>);
	if (typeof configuration === "object" && configuration !== null)
		capturedByConfiguration?.set(configuration, captured);
	return captured;
}

function captureProviderRequest(configuration: Readonly<ProviderProfileConfiguration>): CapturedProviderRequest {
	const profiles = configuration.profiles;
	const configuredTargets = configuration.targets;
	if (!Array.isArray(profiles) || !Array.isArray(configuredTargets)) {
		throw new ProviderProfileError("invalid_configuration");
	}
	if (configuredTargets.length === 0) throw new ProviderProfileError("invalid_configuration");

	const configured = new Map<string, ConfiguredProviderProfile>();
	for (const profile of profiles) {
		const provider = profile.provider;
		const profileId = provider.id;
		const binding = freezeBinding(profile.credential);
		const failureEvidence = profile.failureEvidence;
		assertNonEmptyIdentifier(profileId);
		const existing = configured.get(profileId);
		if (existing) {
			if (
				existing.provider !== provider ||
				!sameBinding(existing.binding, binding) ||
				existing.failureEvidence !== failureEvidence
			) {
				throw new ProviderProfileError("duplicate_profile");
			}
			continue;
		}
		configured.set(profileId, { provider, providerId: profileId, binding, failureEvidence });
	}

	const targetConfigurations: CapturedProviderTargetConfiguration[] = [];
	const targetModels = new Map<string, Set<string>>();
	for (const target of configuredTargets) {
		const profileId = target.profileId;
		const modelId = target.modelId;
		const supportsTools = target.supportsTools;
		const pricing = target.pricing;
		const fallbackOn = captureFallbackKinds(target.fallbackOn);
		assertNonEmptyIdentifier(profileId);
		assertNonEmptyIdentifier(modelId);
		if (typeof supportsTools !== "boolean") throw new ProviderProfileError("invalid_configuration");
		let modelsForProfile = targetModels.get(profileId);
		if (!modelsForProfile) {
			modelsForProfile = new Set();
			targetModels.set(profileId, modelsForProfile);
		}
		modelsForProfile.add(modelId);

		const configuredProfile = configured.get(profileId);
		if (!configuredProfile) throw new ProviderProfileError("profile_unavailable");
		targetConfigurations.push(
			Object.freeze({
				profileId,
				modelId,
				supportsTools,
				pricing,
				fallbackOn,
				profile: configuredProfile,
				selectedModelIds: modelsForProfile,
			}),
		);
	}

	const captured = new Map<string, CapturedProviderProfile>();
	const providers: Provider[] = [];
	const bindings = new Map<string, ProviderCredentialBinding>();
	const targets: CapturedProviderTarget[] = [];
	for (const target of targetConfigurations) {
		let profileSnapshot = captured.get(target.profileId);
		if (!profileSnapshot) {
			profileSnapshot = captureProviderProfile(target.profile, target.selectedModelIds);
			captured.set(target.profileId, profileSnapshot);
			providers.push(profileSnapshot.provider);
			bindings.set(target.profileId, profileSnapshot.binding);
		}
		const model = profileSnapshot.models.find((candidate) => candidate.id === target.modelId);
		if (!model) throw new ProviderProfileError("model_unavailable");
		targets.push(captureProviderTarget(target, model, profileSnapshot.failureEvidence));
	}
	const snapshot = Object.freeze({ targets: Object.freeze(targets.map((target) => target.snapshot)) });

	return Object.freeze({
		bindings,
		providers: Object.freeze(providers),
		snapshot,
		targets: Object.freeze(targets),
	});
}

function captureProviderProfile(
	profile: ConfiguredProviderProfile,
	selectedModelIds: ReadonlySet<string>,
): CapturedProviderProfile {
	const { provider, providerId, binding } = profile;
	const failureEvidence = profile.failureEvidence;
	const name = provider.name;
	const baseUrl = provider.baseUrl;
	const providerHeaders = provider.headers;
	const auth = provider.auth;
	const getModels = provider.getModels;
	const stream = provider.stream;
	const streamSimple = provider.streamSimple;
	const fetchDeferred = provider.fetchDeferred;
	const cancelDeferred = provider.cancelDeferred;
	let rawModels: readonly Model<Api>[];
	try {
		rawModels = getModels.call(provider);
	} catch {
		throw new ProviderProfileError("model_unavailable");
	}
	if (!Array.isArray(rawModels)) throw new ProviderProfileError("invalid_configuration");
	const selectedModels = new Map<string, Model<Api>>();
	for (const model of rawModels) {
		if (!selectedModelIds.has(model.id) || selectedModels.has(model.id)) continue;
		if (model.provider !== providerId) throw new ProviderProfileError("invalid_configuration");
		selectedModels.set(model.id, freezeModel(model));
	}
	const models = Object.freeze([...selectedModels.values()]);
	return Object.freeze({
		binding,
		failureEvidence,
		models,
		provider: captureProviderView({
			provider,
			providerId,
			name,
			baseUrl,
			providerHeaders,
			auth,
			stream,
			streamSimple,
			fetchDeferred,
			cancelDeferred,
			models,
			binding,
		}),
	});
}

function captureProviderView(input: {
	provider: Readonly<Provider>;
	providerId: string;
	name: string;
	baseUrl: string | undefined;
	providerHeaders: Readonly<Record<string, string | null>> | undefined;
	auth: Readonly<ProviderAuth>;
	stream: Provider["stream"];
	streamSimple: Provider["streamSimple"];
	fetchDeferred: Provider["fetchDeferred"];
	cancelDeferred: Provider["cancelDeferred"];
	models: readonly Model<Api>[];
	binding: ProviderCredentialBinding;
}): Provider {
	const auth = captureProviderAuth(input.auth, input.binding);
	const headers = input.providerHeaders ? Object.freeze({ ...input.providerHeaders }) : undefined;
	const fetchDeferred = input.fetchDeferred;
	const cancelDeferred = input.cancelDeferred;
	let captured: Provider;
	captured = {
		id: input.providerId,
		name: input.name,
		...(input.baseUrl === undefined ? {} : { baseUrl: input.baseUrl }),
		...(headers === undefined ? {} : { headers }),
		auth,
		getModels: () => input.models,
		stream: (model, context, options) =>
			normalizeProviderEventStream(model, input.stream.call(captured, model, context, options)),
		streamSimple: (model, context, options) =>
			normalizeProviderEventStream(model, input.streamSimple.call(captured, model, context, options)),
		...(fetchDeferred
			? {
					fetchDeferred: (model, handle, options) => fetchDeferred.call(captured, model, handle, options),
				}
			: {}),
		...(cancelDeferred
			? {
					cancelDeferred: (model, handle, options) => cancelDeferred.call(captured, model, handle, options),
				}
			: {}),
	} satisfies Provider;
	return Object.freeze(captured);
}

function normalizeProviderEventStream(
	model: Readonly<Model<Api>>,
	source: AssistantMessageEventStream,
): AssistantMessageEventStream {
	const output = createAssistantMessageEventStream();
	void forwardNormalizedProviderEvents(model, source, output);
	return output;
}

async function forwardNormalizedProviderEvents(
	model: Readonly<Model<Api>>,
	source: AssistantMessageEventStream,
	output: AssistantMessageEventStream,
): Promise<void> {
	let resultSettled = false;
	let result: AssistantMessage | undefined;
	void source.result().then(
		(message) => {
			resultSettled = true;
			result = message;
		},
		() => {
			resultSettled = true;
		},
	);
	try {
		for await (const event of source) {
			output.push(event);
			if (event.type === "done" || event.type === "error") return;
		}
		await Promise.resolve();
		if (resultSettled && result && result.stopReason !== "pending") {
			if (result.stopReason === "error" || result.stopReason === "aborted") {
				output.push({ type: "error", reason: result.stopReason, error: result });
			} else {
				output.push({ type: "done", reason: result.stopReason, message: result });
			}
			return;
		}
	} catch {
		// Pi exposes provider failures as terminal message values, not thrown iteration errors.
	}
	const failure = syntheticFailureMessage(model, "error");
	output.push({ type: "error", reason: "error", error: failure });
}

function captureProviderAuth(auth: Readonly<ProviderAuth>, binding: ProviderCredentialBinding): ProviderAuth {
	if (binding.kind === "ambient") return captureAmbientAuth(auth);
	const apiKey = auth.apiKey;
	if (binding.kind === "none") {
		if (!apiKey) throw new ProviderProfileError("credential_binding_invalid");
		const name = apiKey.name;
		const check = apiKey.check;
		const resolve = apiKey.resolve;
		let capturedApiKey: ApiKeyAuth;
		capturedApiKey = {
			name,
			...(check
				? {
						check: (input: ApiKeyCheckInput): Promise<AuthCheck | undefined> => {
							const { credential: _credential, ...keylessInput } = input;
							return check.call(capturedApiKey, { ...keylessInput, ctx: NO_AMBIENT_AUTH });
						},
					}
				: {}),
			resolve: async (input: ApiKeyResolveInput) => {
				const { credential: _credential, ...keylessInput } = input;
				const resolved = await resolve.call(capturedApiKey, {
					...keylessInput,
					ctx: NO_AMBIENT_AUTH,
				});
				if (
					!resolved ||
					resolved.auth.apiKey !== undefined ||
					resolved.auth.baseUrl !== undefined ||
					resolved.auth.headers !== undefined ||
					resolved.env !== undefined
				) {
					throw new ProviderProfileError("credential_binding_invalid");
				}
				return resolved;
			},
		};
		return Object.freeze({
			apiKey: Object.freeze(capturedApiKey),
		} satisfies ProviderAuth);
	}

	const oauth = auth.oauth;
	if (!apiKey) return Object.freeze(oauth ? { oauth: captureOAuth(oauth) } : {});
	const name = apiKey.name;
	const check = apiKey.check;
	const resolve = apiKey.resolve;
	let capturedApiKey: ApiKeyAuth;
	capturedApiKey = {
		name,
		...(check
			? {
					check: (input: ApiKeyCheckInput) => {
						requireReferencedCredential(input.credential);
						return check.call(capturedApiKey, input);
					},
				}
			: {}),
		resolve: (input: ApiKeyResolveInput) => {
			requireReferencedCredential(input.credential);
			return resolve.call(capturedApiKey, input);
		},
	};
	return Object.freeze({
		apiKey: Object.freeze(capturedApiKey),
		...(oauth ? { oauth: captureOAuth(oauth) } : {}),
	} satisfies ProviderAuth);
}

function captureAmbientAuth(auth: Readonly<ProviderAuth>): ProviderAuth {
	const apiKey = auth.apiKey;
	const oauth = auth.oauth;
	if (!apiKey) return Object.freeze(oauth ? { oauth: captureOAuth(oauth) } : {});
	const name = apiKey.name;
	const login = apiKey.login;
	const check = apiKey.check;
	const resolve = apiKey.resolve;
	let capturedApiKey: ApiKeyAuth;
	capturedApiKey = {
		name,
		...(login ? { login: (interaction) => login.call(capturedApiKey, interaction) } : {}),
		...(check ? { check: (input) => check.call(capturedApiKey, input) } : {}),
		resolve: (input) => resolve.call(capturedApiKey, input),
	};
	return Object.freeze({
		apiKey: Object.freeze(capturedApiKey),
		...(oauth ? { oauth: captureOAuth(oauth) } : {}),
	} satisfies ProviderAuth);
}

function captureOAuth(oauth: NonNullable<ProviderAuth["oauth"]>): NonNullable<ProviderAuth["oauth"]> {
	const name = oauth.name;
	const isSubscription = oauth.isSubscription;
	const loginLabel = oauth.loginLabel;
	const login = oauth.login;
	const refresh = oauth.refresh;
	const toAuth = oauth.toAuth;
	let captured: NonNullable<ProviderAuth["oauth"]>;
	captured = {
		name,
		...(isSubscription === undefined ? {} : { isSubscription }),
		...(loginLabel === undefined ? {} : { loginLabel }),
		login: (interaction) => login.call(captured, interaction),
		refresh: (credential, signal) => refresh.call(captured, credential, signal),
		toAuth: (credential) => toAuth.call(captured, credential),
	};
	return Object.freeze(captured);
}

function captureProviderTarget(
	target: Readonly<ProviderProfileTarget>,
	model: Readonly<Model<Api>>,
	failureEvidence: ProviderFailureEvidenceReader | undefined,
): CapturedProviderTarget {
	const { pricedModel, pricing } = capturePricing(model, target.pricing);
	const snapshot: ProviderProfileTargetSnapshot = Object.freeze({
		profileId: target.profileId,
		modelId: pricedModel.id,
		name: pricedModel.name,
		api: pricedModel.api,
		reasoning: pricedModel.reasoning,
		input: Object.freeze([...pricedModel.input]),
		contextWindow: pricedModel.contextWindow,
		maxTokens: pricedModel.maxTokens,
		supportsTools: target.supportsTools,
		pricing,
		...(target.fallbackOn && target.fallbackOn.length > 0 ? { fallbackOn: Object.freeze([...target.fallbackOn]) } : {}),
	});
	return Object.freeze({
		model: pricedModel,
		snapshot,
		supportsTools: target.supportsTools,
		failureEvidence,
		fallbackOn: new Set(target.fallbackOn),
	});
}

function capturePricing(
	model: Readonly<Model<Api>>,
	pricing: ProviderProfilePricing,
): { pricedModel: Model<Api>; pricing: ProviderProfilePricingSnapshot } {
	switch (pricing.kind) {
		case "pi-catalog": {
			const rates = deepFreeze(structuredClone(model.cost));
			return { pricedModel: model, pricing: Object.freeze({ kind: "pi-catalog", rates }) };
		}
		case "host-rate-card": {
			const rates = deepFreeze(structuredClone(pricing.rates));
			return {
				pricedModel: Object.freeze({ ...model, cost: rates }),
				pricing: Object.freeze({ kind: "host-rate-card", rates }),
			};
		}
		case "free":
			return {
				pricedModel: Object.freeze({ ...model, cost: FREE_RATES }),
				pricing: Object.freeze({ kind: "free" }),
			};
		case "unpriced":
			return {
				pricedModel: Object.freeze({ ...model, cost: UNPRICED_PI_COST_PLACEHOLDER }),
				pricing: Object.freeze({ kind: "unpriced" }),
			};
	}
}

function freezeBinding(binding: ProviderCredentialBinding): ProviderCredentialBinding {
	switch (binding.kind) {
		case "ambient":
		case "none":
			return Object.freeze({ kind: binding.kind });
		case "reference":
			return Object.freeze({ kind: "reference", reference: binding.reference });
	}
}

function sameBinding(left: ProviderCredentialBinding, right: ProviderCredentialBinding): boolean {
	if (left.kind !== right.kind) return false;
	return left.kind !== "reference" || (right.kind === "reference" && left.reference === right.reference);
}

function freezeModel<TApi extends Api>(model: Readonly<Model<TApi>>): Model<TApi> {
	const captured = structuredClone(model);
	if (captured.api === "openai-completions") {
		const compatible = captured as Model<"openai-completions">;
		compatible.compat = {
			...compatible.compat,
			supportsStrictMode: compatible.compat?.supportsStrictMode ?? false,
		};
	}
	return deepFreeze(captured);
}

function deepFreeze<T>(value: T): T {
	if (typeof value !== "object" || value === null) return value;
	const pending: object[] = [value];
	const seen = new WeakSet<object>();
	while (pending.length > 0) {
		const current = pending.pop();
		if (current === undefined || seen.has(current)) continue;
		seen.add(current);
		for (const child of Object.values(current)) {
			if (typeof child === "object" && child !== null) pending.push(child);
		}
		Object.freeze(current);
	}
	return value;
}

function captureRequestOptions(
	options: Parameters<GameProviderAttemptStreamFn>[2],
	binding: ProviderCredentialBinding | undefined,
): Parameters<GameProviderAttemptStreamFn>[2] {
	if (binding?.kind === "ambient") return options;
	const requestOptions = (options ?? {}) as NonNullable<Parameters<GameProviderAttemptStreamFn>[2]> & {
		readonly transformHeaders?: unknown;
	};
	const {
		apiKey: _apiKey,
		env: _env,
		headers: _headers,
		transformHeaders: _transformHeaders,
		...credentialBoundOptions
	} = requestOptions;
	return credentialBoundOptions;
}

function assertNonEmptyIdentifier(value: string): void {
	if (typeof value !== "string" || value.length === 0) throw new ProviderProfileError("invalid_configuration");
}

function captureFallbackKinds(
	configured: readonly ProviderFallbackFailureKind[] | undefined,
): readonly ProviderFallbackFailureKind[] {
	if (configured === undefined) return Object.freeze([]);
	if (!Array.isArray(configured) || configured.some((kind) => !isProviderFallbackFailureKind(kind))) {
		throw new ProviderProfileError("invalid_configuration");
	}
	return Object.freeze([...configured]);
}

function messageContainsImage(message: Context["messages"][number]): boolean {
	return Array.isArray(message.content) && message.content.some((part) => part.type === "image");
}
