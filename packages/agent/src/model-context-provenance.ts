import { createHash } from "node:crypto";
import type { Api, AssistantMessage, Context, Model, Tool } from "@earendil-works/pi-ai";
import {
	assertImageMediaType,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { decodeCanonicalBase64 } from "./canonical-base64.js";
import type { LoadedGameContextSource, PreparedGameContextSnapshot } from "./context-snapshot.js";
import { type CapturedExactToolRepeatPolicy, canonicalExactToolRepeatPolicyDefinition } from "./exact-tool-repeat.js";
import { createGameProvenanceAlias, type GameProvenanceAlias, isGameProvenanceAlias } from "./provenance-alias.js";
import type { VisibleToolSnapshot } from "./tool-snapshot.js";
import type {
	GameModelContextLineageProvenance,
	GameModelContextLineageReference,
	GameModelContextSourceKind,
	GameModelContextSourceProvenance,
	GameModelCostProvenance,
	GameModelImageProvenance,
	GameModelRequestProvenance,
	GameModelResponseProvenance,
	GameModelToolProvenance,
	GameProviderPricingBasis,
	GameProviderProvenanceBinding,
	GameProviderProvenanceSink,
	GameProviderUsageObservation,
} from "./types.js";
import { GAME_MODEL_CONTEXT_SOURCE_KINDS } from "./types.js";

export interface CapturedGameProviderProvenanceBinding {
	readonly sink: GameProviderProvenanceSink;
	readonly scopeId: string;
	readonly baseInstructions: GameModelContextLineageReference;
	readonly contextTransformer?: GameModelContextLineageReference;
	readonly exactToolRepeatPolicy?: GameModelContextLineageReference;
}

export interface ExactToolRepeatAdvisoryProvenanceInput {
	readonly eventId: string;
	readonly eventRevision: string;
	readonly toolName: string;
	readonly consecutiveCount: string;
	readonly content: string;
}

export type ModelContextToolOwner = { readonly kind: "host" } | { readonly kind: "source"; readonly sourceId: string };

export type GameModelRequestProvenanceTemplate = Omit<GameModelRequestProvenance, "startedAt" | "attempt">;

export function captureProviderProvenanceBinding(
	binding: GameProviderProvenanceBinding | undefined,
	options: {
		readonly hasTranscript: boolean;
		readonly systemPrompt: string;
		readonly hasContextTransformer: boolean;
		readonly exactToolRepeatPolicy?: CapturedExactToolRepeatPolicy;
	},
): CapturedGameProviderProvenanceBinding | undefined {
	if (binding === undefined) return undefined;
	if (
		typeof binding !== "object" ||
		binding === null ||
		Array.isArray(binding) ||
		typeof binding.sink !== "object" ||
		binding.sink === null ||
		typeof binding.sink.recordRequest !== "function" ||
		typeof binding.sink.recordResponse !== "function" ||
		typeof binding.scopeId !== "string" ||
		binding.scopeId.length === 0
	) {
		throw new TypeError("The provider provenance binding is invalid.");
	}
	if (!options.hasTranscript) throw new TypeError("Provider provenance requires a durable transcript.");
	const baseInstructions =
		binding.baseInstructions ??
		(options.systemPrompt.length === 0
			? {
					sourceId: "agent",
					referenceId: "empty-base-instructions",
					revision: "empty",
					contentFingerprint: sha256Text(""),
				}
			: undefined);
	if (!baseInstructions) {
		throw new TypeError("Non-empty base instructions require an immutable provenance reference.");
	}
	assertLineageReference(baseInstructions);
	if (baseInstructions.contentFingerprint !== sha256Text(options.systemPrompt)) {
		throw new TypeError("The base-instruction provenance fingerprint does not match the configured prompt.");
	}
	if (options.hasContextTransformer !== (binding.contextTransformer !== undefined)) {
		throw new TypeError("Context-transformer provenance must match the configured transformer.");
	}
	if (binding.contextTransformer) assertLineageReference(binding.contextTransformer);
	const exactToolRepeatPolicy = options.exactToolRepeatPolicy
		? Object.freeze({
				sourceId: "opengameagent",
				referenceId: "exact-tool-repeat-policy",
				revision: "1",
				contentFingerprint: sha256Text(canonicalExactToolRepeatPolicyDefinition(options.exactToolRepeatPolicy)),
			})
		: undefined;
	return Object.freeze({
		sink: Object.freeze({
			recordRequest: binding.sink.recordRequest.bind(binding.sink),
			recordResponse: binding.sink.recordResponse.bind(binding.sink),
		}),
		scopeId: binding.scopeId,
		baseInstructions: Object.freeze({ ...baseInstructions }),
		...(binding.contextTransformer ? { contextTransformer: Object.freeze({ ...binding.contextTransformer }) } : {}),
		...(exactToolRepeatPolicy ? { exactToolRepeatPolicy } : {}),
	});
}

export function prepareModelRequestProvenance(input: {
	readonly requestedModel: Model<Api>;
	readonly targetModel: Model<Api>;
	readonly attemptIndex: string;
	readonly startedAt: string;
	readonly context: Context;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly transcriptRevision: string;
	readonly checkpointRevision?: string;
	readonly provenance: CapturedGameProviderProvenanceBinding;
	readonly contextSnapshot?: PreparedGameContextSnapshot;
	readonly contextMessageIndex?: number;
	readonly loadedContextSources: readonly LoadedGameContextSource[];
	readonly visibleToolSnapshot?: VisibleToolSnapshot;
	readonly toolOwners: ReadonlyMap<string, ModelContextToolOwner>;
	readonly exactToolRepeatAdvisory?: ExactToolRepeatAdvisoryProvenanceInput;
}): GameModelRequestProvenance {
	return prepareModelRequestAttemptProvenance(
		prepareModelRequestProvenanceTemplate(input),
		input.targetModel,
		input.attemptIndex,
		input.startedAt,
	);
}

export function prepareModelRequestProvenanceTemplate(input: {
	readonly requestedModel: Model<Api>;
	readonly context: Context;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly transcriptRevision: string;
	readonly checkpointRevision?: string;
	readonly provenance: CapturedGameProviderProvenanceBinding;
	readonly contextSnapshot?: PreparedGameContextSnapshot;
	readonly contextMessageIndex?: number;
	readonly loadedContextSources: readonly LoadedGameContextSource[];
	readonly visibleToolSnapshot?: VisibleToolSnapshot;
	readonly toolOwners: ReadonlyMap<string, ModelContextToolOwner>;
	readonly exactToolRepeatAdvisory?: ExactToolRepeatAdvisoryProvenanceInput;
}): GameModelRequestProvenanceTemplate {
	const sources = prepareSources(
		input.loadedContextSources,
		input.contextSnapshot,
		input.visibleToolSnapshot,
		input.toolOwners,
	);
	const tools = prepareTools(input.context.tools ?? [], input.visibleToolSnapshot, input.toolOwners);
	const images = prepareImages(input.context, input.contextSnapshot, input.contextMessageIndex);
	const exactToolRepeat = input.exactToolRepeatAdvisory
		? prepareExactToolRepeatAdvisoryProvenance(input.exactToolRepeatAdvisory, input.context)
		: undefined;
	return Object.freeze({
		operation: "inference",
		coordinate: Object.freeze({
			scopeId: createGameProvenanceAlias("scope", input.provenance.scopeId),
			streamId: createGameProvenanceAlias("stream", `${input.coordinate.sessionId}\0${input.coordinate.actorId}`),
			runId: createGameProvenanceAlias("run", input.coordinate.runId),
			turn: input.coordinate.turn,
		}),
		requestedRoute: prepareModelRoute(input.requestedModel),
		modelContextFingerprint: fingerprintGameModelContextWithImages(input.context, images),
		transcript: Object.freeze({
			revision: input.transcriptRevision,
			...(input.checkpointRevision === undefined ? {} : { checkpointRevision: input.checkpointRevision }),
		}),
		lineage: Object.freeze({
			baseInstructions: prepareLineageReference(input.provenance.baseInstructions),
			...(input.provenance.contextTransformer
				? { contextTransformer: prepareLineageReference(input.provenance.contextTransformer) }
				: {}),
			...(input.provenance.exactToolRepeatPolicy
				? { exactToolRepeatPolicy: prepareLineageReference(input.provenance.exactToolRepeatPolicy) }
				: {}),
		}),
		...(exactToolRepeat ? { policyContext: Object.freeze({ exactToolRepeat }) } : {}),
		...(input.visibleToolSnapshot
			? {
					toolSnapshot: Object.freeze({
						revision: createGameProvenanceAlias("revision", input.visibleToolSnapshot.revision),
						policyRevision: createGameProvenanceAlias("revision", input.visibleToolSnapshot.policyRevision),
					}),
				}
			: {}),
		sources,
		tools,
		images,
	});
}

function prepareExactToolRepeatAdvisoryProvenance(
	input: ExactToolRepeatAdvisoryProvenanceInput,
	context: Context,
): NonNullable<GameModelRequestProvenance["policyContext"]>["exactToolRepeat"] {
	assertOpaqueIdentifier(input.eventId);
	assertOpaqueIdentifier(input.toolName);
	if (!canonicalDecimal(input.eventRevision) || !canonicalPositiveSafeInteger(input.consecutiveCount)) {
		throw new TypeError("The exact Tool-repeat advisory provenance is invalid.");
	}
	const finalMessage = context.messages.at(-1);
	if (finalMessage?.role !== "user" || finalMessage.content !== input.content) {
		throw new TypeError("The exact Tool-repeat advisory is absent from the provider context.");
	}
	return Object.freeze({
		eventId: createGameProvenanceAlias("event", input.eventId),
		eventRevision: input.eventRevision,
		toolName: createGameProvenanceAlias("tool", input.toolName),
		consecutiveCount: input.consecutiveCount,
		contentFingerprint: sha256Text(input.content),
	});
}

export function prepareModelResponseProvenance(
	message: AssistantMessage,
	targetModel: Pick<Model<Api>, "provider" | "api" | "id">,
	completedAt: string,
	pricingBasis: Readonly<GameProviderPricingBasis>,
	usageObservation: GameProviderUsageObservation,
): GameModelResponseProvenance {
	const stopReason = message.stopReason;
	if (stopReason === "pending") throw new TypeError("A pending Pi message cannot settle provider provenance.");
	if (usageObservation === "unreported" && stopReason !== "error" && stopReason !== "aborted") {
		throw new TypeError("Successful provider responses require reported usage.");
	}
	if (
		message.provider !== targetModel.provider ||
		message.api !== targetModel.api ||
		message.model !== targetModel.id
	) {
		throw new TypeError("The Pi response does not match its committed provider attempt.");
	}
	const outcome =
		stopReason === "error"
			? "failed"
			: stopReason === "aborted"
				? "cancelled"
				: stopReason === "deferred"
					? "deferred"
					: "completed";
	return Object.freeze({
		completedAt,
		servedModel: createGameProvenanceAlias("model", message.responseModel ?? message.model),
		stopReason,
		outcome,
		errorKind: stopReason === "error" ? "provider" : stopReason === "aborted" ? "cancelled" : "none",
		cost: captureModelCost(message, pricingBasis, usageObservation),
		usage: captureModelUsage(message, usageObservation),
	});
}

function captureModelUsage(
	message: Readonly<AssistantMessage>,
	usageObservation: GameProviderUsageObservation,
): GameModelResponseProvenance["usage"] {
	switch (usageObservation) {
		case "reported":
			return Object.freeze({
				kind: "reported",
				input: message.usage.input,
				output: message.usage.output,
				cacheRead: message.usage.cacheRead,
				cacheWrite: message.usage.cacheWrite,
				...(message.usage.cacheWrite1h === undefined ? {} : { cacheWrite1h: message.usage.cacheWrite1h }),
				...(message.usage.reasoning === undefined ? {} : { reasoning: message.usage.reasoning }),
				totalTokens: message.usage.totalTokens,
			});
		case "unreported":
			return Object.freeze({ kind: "unreported" });
	}
}

function captureModelCost(
	message: Readonly<AssistantMessage>,
	pricingBasis: Readonly<GameProviderPricingBasis>,
	usageObservation: GameProviderUsageObservation,
): GameModelCostProvenance {
	switch (pricingBasis.kind) {
		case "pi-catalog":
		case "host-rate-card": {
			const source = pricingBasis.kind;
			if (usageObservation === "unreported") return Object.freeze({ kind: "unknown", source });
			return Object.freeze({
				kind: "calculated",
				source,
				usd: Object.freeze({
					input: message.usage.cost.input,
					output: message.usage.cost.output,
					cacheRead: message.usage.cost.cacheRead,
					cacheWrite: message.usage.cost.cacheWrite,
					total: message.usage.cost.total,
				}),
			});
		}
		case "free":
			return Object.freeze({
				kind: "free",
				usd: Object.freeze({ input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 }),
			});
		case "unpriced":
			return Object.freeze({ kind: "unpriced" });
	}
}

export function prepareModelRequestAttemptProvenance(
	template: GameModelRequestProvenanceTemplate,
	targetModel: Pick<Model<Api>, "provider" | "api" | "id">,
	attemptIndex: string,
	startedAt: string,
): GameModelRequestProvenance {
	return Object.freeze({
		...template,
		startedAt,
		attempt: Object.freeze({
			index: attemptIndex,
			targetRoute: prepareModelRoute(targetModel),
		}),
	});
}

function prepareModelRoute(model: Pick<Model<Api>, "provider" | "api" | "id">) {
	return Object.freeze({
		provider: createGameProvenanceAlias("provider", model.provider),
		api: createGameProvenanceAlias("api", model.api),
		model: createGameProvenanceAlias("model", model.id),
	});
}

function prepareSources(
	loaded: readonly LoadedGameContextSource[],
	snapshot: PreparedGameContextSnapshot | undefined,
	visibleTools: VisibleToolSnapshot | undefined,
	toolOwners: ReadonlyMap<string, ModelContextToolOwner>,
): readonly GameModelContextSourceProvenance[] {
	const kindBySource = new Map(loaded.map((source) => [source.sourceId, source.provenanceKind]));
	const grouped = new Map<string, GameModelContextSourceProvenance["references"][number][]>();
	for (const reference of snapshot?.provenance.references ?? []) {
		const references = grouped.get(reference.sourceId) ?? [];
		references.push(
			Object.freeze({
				referenceId: createGameProvenanceAlias("reference", `${reference.sourceId}\0${reference.referenceId}`),
				revision: createGameProvenanceAlias(
					"revision",
					`${reference.sourceId}\0${reference.referenceId}\0${reference.revision}`,
				),
				contentFingerprint: reference.contentFingerprint,
			}),
		);
		grouped.set(reference.sourceId, references);
	}
	for (const descriptor of visibleTools?.descriptors ?? []) {
		const owner = toolOwners.get(descriptor.name);
		if (!owner) throw new TypeError("The provider Tool provenance owner is unavailable.");
		if (owner.kind === "source" && !grouped.has(owner.sourceId)) grouped.set(owner.sourceId, []);
	}
	const sources: GameModelContextSourceProvenance[] = [];
	for (const [sourceId, references] of grouped) {
		const kind = kindBySource.get(sourceId) ?? "extension";
		sources.push(
			Object.freeze({
				kind,
				sourceId: createGameProvenanceAlias("source", sourceId),
				references: Object.freeze(references),
			}),
		);
	}
	return Object.freeze(sources);
}

function prepareTools(
	tools: readonly Tool[],
	visible: VisibleToolSnapshot | undefined,
	owners: ReadonlyMap<string, ModelContextToolOwner>,
): readonly GameModelToolProvenance[] {
	if (!visible) return Object.freeze([]);
	const values: GameModelToolProvenance[] = [];
	for (let index = 0; index < tools.length; index += 1) {
		const tool = tools[index];
		const descriptor = visible.descriptors[index];
		if (!tool || !descriptor || tool.name !== descriptor.name) {
			throw new TypeError("The provider Tool provenance is not aligned.");
		}
		const owner = owners.get(descriptor.name);
		if (!owner) throw new TypeError("The provider Tool provenance owner is unavailable.");
		values.push(
			Object.freeze({
				name: createGameProvenanceAlias("tool", descriptor.name),
				sourceId:
					owner.kind === "host"
						? createGameProvenanceAlias("host-tool-owner", "host")
						: createGameProvenanceAlias("source", owner.sourceId),
				fingerprint: descriptor.fingerprint,
			}),
		);
	}
	return Object.freeze(values);
}

function prepareImages(
	context: Context,
	snapshot: PreparedGameContextSnapshot | undefined,
	contextMessageIndex: number | undefined,
): readonly GameModelImageProvenance[] {
	const images: GameModelImageProvenance[] = [];
	const contextImages = new Map(
		snapshot === undefined || contextMessageIndex === undefined
			? []
			: snapshot.images.map((image, index) => [`${contextMessageIndex}:${index + 1}`, image] as const),
	);
	for (let messageIndex = 0; messageIndex < context.messages.length; messageIndex += 1) {
		const message = context.messages[messageIndex];
		if (!message || message.role === "assistant" || typeof message.content === "string") continue;
		for (let contentIndex = 0; contentIndex < message.content.length; contentIndex += 1) {
			const content = message.content[contentIndex];
			if (content?.type !== "image") continue;
			assertImageMediaType(content.mimeType);
			const lineage = contextImages.get(`${messageIndex}:${contentIndex}`);
			let bytes: number;
			let sha256: string;
			if (lineage) {
				if (content.mimeType !== lineage.content.mimeType || content.data !== lineage.content.data) {
					throw new TypeError("The provider context image is not aligned with its prepared snapshot.");
				}
				bytes = lineage.bytes;
				sha256 = lineage.reference.attachmentId.slice("img_".length);
			} else {
				const decoded = decodeCanonicalBase64(content.data);
				if (!decoded) throw new TypeError("The provider context image data is not canonical base64.");
				bytes = decoded.byteLength;
				sha256 = createHash("sha256").update(decoded).digest("hex");
			}
			images.push(
				Object.freeze({
					messageIndex,
					contentIndex,
					mediaType: content.mimeType,
					bytes,
					sha256,
					...(lineage
						? {
								context: Object.freeze({
									sourceId: createGameProvenanceAlias("source", lineage.sourceId),
									referenceId: createGameProvenanceAlias(
										"reference",
										`${lineage.sourceId}\0${lineage.contextReferenceId}`,
									),
									revision: createGameProvenanceAlias(
										"revision",
										`${lineage.sourceId}\0${lineage.contextReferenceId}\0${lineage.revision}`,
									),
								}),
							}
						: {}),
				}),
			);
		}
	}
	return Object.freeze(images);
}

export function fingerprintGameModelContext(context: Context): string {
	return fingerprintGameModelContextWithImages(context, prepareImages(context, undefined, undefined));
}

function fingerprintGameModelContextWithImages(context: Context, images: readonly GameModelImageProvenance[]): string {
	const imageByPosition = new Map(
		images.map((image) => [`${image.messageIndex}:${image.contentIndex}`, image] as const),
	);
	const tools = (context.tools ?? []).map((tool) => ({
		name: tool.name,
		description: tool.description,
		parameters: tool.parameters,
		...(tool.constrainedSampling === undefined ? {} : { constrainedSampling: tool.constrainedSampling }),
	}));
	const providerNeutral = {
		...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
		messages: context.messages.map((message, index) => providerNeutralMessage(message, index, imageByPosition)),
		tools,
	};
	const value = canonicalizeJsonValue(providerNeutral);
	return createHash("sha256").update(value).digest("hex");
}

function providerNeutralMessage(
	message: Context["messages"][number],
	messageIndex: number,
	images: ReadonlyMap<string, GameModelImageProvenance>,
): unknown {
	const content =
		typeof message.content === "string"
			? message.content
			: message.content.map((part, contentIndex) => {
					if (part.type !== "image") return part;
					const image = images.get(`${messageIndex}:${contentIndex}`);
					if (!image) throw new TypeError("The provider context image fingerprint is unavailable.");
					return { type: "image", mediaType: image.mediaType, bytes: image.bytes, sha256: image.sha256 };
				});
	if (message.role === "user") {
		return { role: message.role, content };
	}
	if (message.role === "toolResult") {
		return {
			role: message.role,
			toolCallId: message.toolCallId,
			toolName: message.toolName,
			content,
			isError: message.isError,
			...(message.addedToolNames === undefined ? {} : { addedToolNames: [...message.addedToolNames] }),
		};
	}
	return {
		role: message.role,
		content,
		api: message.api,
		provider: message.provider,
		model: message.model,
		...(message.responseId === undefined ? {} : { responseId: message.responseId }),
		...(message.providerThinkingLevel === undefined ? {} : { providerThinkingLevel: message.providerThinkingLevel }),
		stopReason: message.stopReason,
	};
}

export function modelContextSourceKind(
	value: unknown,
	fallback: GameModelContextSourceKind,
): GameModelContextSourceKind {
	if (value === undefined) return fallback;
	const kinds: readonly string[] = GAME_MODEL_CONTEXT_SOURCE_KINDS;
	if (!kinds.includes(value as string)) {
		throw new TypeError("The model-context source kind is invalid.");
	}
	return value as GameModelContextSourceKind;
}

export function assertProviderRequestRecordId(value: string): asserts value is GameProvenanceAlias {
	if (!isGameProvenanceAlias(value)) throw new TypeError("The provider provenance record identity is invalid.");
}

function prepareLineageReference(value: GameModelContextLineageReference): GameModelContextLineageProvenance {
	return Object.freeze({
		sourceId: createGameProvenanceAlias("source", value.sourceId),
		referenceId: createGameProvenanceAlias("reference", `${value.sourceId}\0${value.referenceId}`),
		revision: createGameProvenanceAlias("revision", `${value.sourceId}\0${value.referenceId}\0${value.revision}`),
		contentFingerprint: value.contentFingerprint,
	});
}

function assertLineageReference(value: GameModelContextLineageReference): void {
	if (
		typeof value !== "object" ||
		value === null ||
		typeof value.sourceId !== "string" ||
		typeof value.referenceId !== "string" ||
		typeof value.revision !== "string" ||
		!/^[0-9a-f]{64}$/u.test(value.contentFingerprint)
	) {
		throw new TypeError("The model-context lineage reference is invalid.");
	}
	assertOpaqueIdentifier(value.sourceId);
	assertOpaqueIdentifier(value.referenceId);
	assertOpaqueIdentifier(value.revision);
}

function canonicalDecimal(value: string): boolean {
	return /^(0|[1-9][0-9]*)$/u.test(value);
}

function canonicalPositiveSafeInteger(value: string): boolean {
	return /^[1-9][0-9]*$/u.test(value) && Number.isSafeInteger(Number(value));
}

function sha256Text(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}
