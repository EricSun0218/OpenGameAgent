import {
	Agent,
	type AgentEvent,
	type AgentLoopTurnUpdate,
	type AgentMessage,
	type AgentTool,
} from "@earendil-works/pi-agent-core";
import type {
	AssistantMessage,
	ImageContent,
	Message,
	StopReason,
	TextContent,
	ThinkingContent,
	ToolCall,
	ToolResultMessage,
	Usage,
	UserMessage,
} from "@earendil-works/pi-ai";
import { sameGameSession } from "@opengameagent/kernel";
import type {
	GameAgentEvent,
	GameAgentKernelPort,
	GameControlResult,
	GameConversationContent,
	GameConversationMessage,
	GameConversationStore,
	GameImageAttachmentReference,
	GameImageAttachmentStore,
	GameInput,
	GameInputContent,
	GameKernelRunRequest,
	GameRunCoordinate,
	GameSessionKey,
	GameTool,
	GameToolExecutionContext,
	GameUsage,
	JsonObject,
	JsonValue,
} from "@opengameagent/protocol";
import type { GameConversationCompactor } from "@opengameagent/transcript";
import type { TSchema } from "typebox";
import { AsyncQueue } from "./async-queue.js";
import type { PiGameModelResolver, ResolvedPiGameModel } from "./model-registry.js";

export interface PiGameAgentKernelOptions {
	models: PiGameModelResolver;
	conversationStore?: GameConversationStore;
	conversationCompactor?: GameConversationCompactor;
	imageAttachments?: GameImageAttachmentStore;
	maximumInlineImageCharacters?: number;
}

type GameAgentEventPayload<T> = T extends GameAgentEvent
	? Omit<T, "sequence" | "eventId" | "runId" | "turn" | "audience" | "timestamp">
	: never;
type ProjectedGameAgentEvent = GameAgentEventPayload<GameAgentEvent>;

interface PiGameSummaryMessage {
	role: "gameSummary";
	summary: string;
	tokensBefore: number;
	timestamp: number;
}

declare module "@earendil-works/pi-agent-core" {
	interface CustomAgentMessages {
		gameSummary: PiGameSummaryMessage;
	}
}

interface ActivePiRun {
	agent?: Agent;
	session: GameSessionKey;
	runId: string;
	turn: number;
	toolCallIndex: number;
	closed: boolean;
	aborted: boolean;
	usage?: GameUsage;
}
type ActiveReadyPiRun = ActivePiRun & { agent: Agent };

function toGameUsage(usage: Usage): GameUsage {
	return {
		input: usage.input,
		output: usage.output,
		cacheRead: usage.cacheRead,
		cacheWrite: usage.cacheWrite,
		...(usage.reasoning === undefined ? {} : { reasoning: usage.reasoning }),
		totalTokens: usage.totalTokens,
		cost: { ...usage.cost },
	};
}

function addGameUsage(left: GameUsage | undefined, right: GameUsage): GameUsage {
	if (!left) return right;
	const cost =
		left.cost === undefined || right.cost === undefined
			? undefined
			: {
					input: left.cost.input + right.cost.input,
					output: left.cost.output + right.cost.output,
					cacheRead: left.cost.cacheRead + right.cost.cacheRead,
					cacheWrite: left.cost.cacheWrite + right.cost.cacheWrite,
					total: left.cost.total + right.cost.total,
				};
	return {
		input: left.input + right.input,
		output: left.output + right.output,
		cacheRead: left.cacheRead + right.cacheRead,
		cacheWrite: left.cacheWrite + right.cacheWrite,
		...(left.reasoning === undefined && right.reasoning === undefined
			? {}
			: { reasoning: (left.reasoning ?? 0) + (right.reasoning ?? 0) }),
		totalTokens: left.totalTokens + right.totalTokens,
		...(cost === undefined ? {} : { cost }),
	};
}

function toPiUsage(usage: GameUsage): Usage {
	return {
		input: usage.input,
		output: usage.output,
		cacheRead: usage.cacheRead,
		cacheWrite: usage.cacheWrite,
		...(usage.reasoning === undefined ? {} : { reasoning: usage.reasoning }),
		totalTokens: usage.totalTokens,
		cost: usage.cost ?? { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
	};
}

const DEFAULT_MAXIMUM_INLINE_IMAGE_CHARACTERS = 24 * 1024 * 1024;

function requireInlineImage(value: string, maximumCharacters: number): Uint8Array {
	if (value.length < 4 || value.length > maximumCharacters || value.length % 4 !== 0) {
		throw new RangeError("Inline image base64 is outside its configured bound.");
	}
	if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) {
		throw new TypeError("Inline image data must be canonical base64.");
	}
	return new Uint8Array(Buffer.from(value, "base64"));
}

function sameAttachmentReference(left: GameImageAttachmentReference, right: GameImageAttachmentReference): boolean {
	return (
		left.id === right.id &&
		left.sha256 === right.sha256 &&
		left.mimeType === right.mimeType &&
		left.bytes === right.bytes &&
		left.width === right.width &&
		left.height === right.height
	);
}

async function resolveAttachment(
	reference: GameImageAttachmentReference,
	store: GameImageAttachmentStore | undefined,
	signal?: AbortSignal,
): Promise<ImageContent> {
	if (!store) throw new Error("Image attachment references require an image attachment store.");
	const image = await store.read(reference.id, signal);
	if (!image || !sameAttachmentReference(image.reference, reference)) {
		throw new Error("Image attachment reference is missing or does not match durable metadata.");
	}
	return { type: "image", data: Buffer.from(image.data).toString("base64"), mimeType: reference.mimeType };
}

async function inputToMessage(
	input: GameInput,
	store: GameImageAttachmentStore | undefined,
	maximumInlineImageCharacters: number,
	signal?: AbortSignal,
): Promise<UserMessage> {
	const images: ImageContent[] = [];
	for (const item of input.content) {
		if (item.type === "image") {
			const bytes = requireInlineImage(item.data, maximumInlineImageCharacters);
			if (store) {
				const reference = await store.admit(item.mimeType, bytes, signal);
				images.push(await resolveAttachment(reference, store, signal));
			} else {
				images.push({ type: "image", data: item.data, mimeType: item.mimeType });
			}
		} else if (item.type === "imageRef") {
			images.push(await resolveAttachment(item.attachment, store, signal));
		}
	}
	const envelope = {
		type: input.type,
		moment: input.moment,
		content: input.content
			.filter((item) => item.type !== "image" && item.type !== "imageRef")
			.map((item) => (item.type === "text" ? { type: item.type, text: item.text } : item)),
		...(input.context === undefined ? {} : { context: input.context }),
	};
	const text = { type: "text" as const, text: JSON.stringify(envelope) };
	return { role: "user", content: images.length === 0 ? text.text : [text, ...images], timestamp: Date.now() };
}

function controlInputToMessage(input: GameInput, maximumInlineImageCharacters: number): UserMessage {
	if (input.content.some((item) => item.type === "imageRef")) {
		throw new Error("Exact live control accepts inline images only; resolve durable image references before steering.");
	}
	const images = input.content
		.filter((item): item is Extract<GameInputContent, { type: "image" }> => item.type === "image")
		.map((item) => {
			requireInlineImage(item.data, maximumInlineImageCharacters);
			return { type: "image" as const, data: item.data, mimeType: item.mimeType };
		});
	const envelope = {
		type: input.type,
		moment: input.moment,
		content: input.content
			.filter((item) => item.type !== "image" && item.type !== "imageRef")
			.map((item) => (item.type === "text" ? { type: item.type, text: item.text } : item)),
		...(input.context === undefined ? {} : { context: input.context }),
	};
	const text = { type: "text" as const, text: JSON.stringify(envelope) };
	return { role: "user", content: images.length === 0 ? text.text : [text, ...images], timestamp: Date.now() };
}

function visibleText(message: AgentMessage): string {
	if (message.role !== "assistant") return "";
	return message.content
		.filter((part) => part.type === "text")
		.map((part) => part.text)
		.join("");
}

function toJsonValue(value: unknown): JsonValue {
	if (value === undefined) return null;
	return JSON.parse(JSON.stringify(value)) as JsonValue;
}

async function toGameContent(
	content: readonly (TextContent | ImageContent | ThinkingContent | ToolCall)[],
	store: GameImageAttachmentStore | undefined,
	maximumInlineImageCharacters: number,
	signal?: AbortSignal,
): Promise<GameConversationContent[]> {
	return await Promise.all(
		content.map(async (part): Promise<GameConversationContent> => {
			switch (part.type) {
				case "text":
					return {
						type: "text",
						text: String(part.text ?? ""),
						...(typeof part.textSignature === "string" ? { signature: part.textSignature } : {}),
					};
				case "image":
					if (!store) return { type: "image", data: String(part.data ?? ""), mimeType: String(part.mimeType ?? "") };
					return {
						type: "imageRef",
						attachment: await store.admit(
							String(part.mimeType ?? ""),
							requireInlineImage(String(part.data ?? ""), maximumInlineImageCharacters),
							signal,
						),
					};
				case "thinking":
					return {
						type: "reasoning",
						text: String(part.thinking ?? ""),
						...(typeof part.thinkingSignature === "string" ? { signature: part.thinkingSignature } : {}),
						...(part.redacted === true ? { redacted: true } : {}),
					};
				case "toolCall":
					return {
						type: "toolCall",
						id: String(part.id ?? ""),
						name: String(part.name ?? ""),
						arguments: toJsonValue(part.arguments) as JsonObject,
						...(typeof part.thoughtSignature === "string" ? { signature: part.thoughtSignature } : {}),
					};
				default:
					return part;
			}
		}),
	);
}

async function toGameMessage(
	message: AgentMessage,
	store: GameImageAttachmentStore | undefined,
	maximumInlineImageCharacters: number,
	signal?: AbortSignal,
): Promise<GameConversationMessage> {
	if (message.role === "gameSummary") {
		return {
			role: "summary",
			summary: message.summary,
			tokensBefore: message.tokensBefore,
			timestamp: message.timestamp,
		};
	}
	if (message.role === "user") {
		return {
			role: "user",
			content:
				typeof message.content === "string"
					? message.content
					: await toGameContent(message.content, store, maximumInlineImageCharacters, signal),
			timestamp: message.timestamp,
		};
	}
	if (message.role === "assistant") {
		return {
			role: "assistant",
			content: await toGameContent(message.content, store, maximumInlineImageCharacters, signal),
			api: message.api,
			provider: message.provider,
			model: message.model,
			...(message.responseId === undefined ? {} : { responseId: message.responseId }),
			usage: toGameUsage(message.usage),
			stopReason: message.stopReason,
			...(message.errorMessage === undefined ? {} : { errorMessage: message.errorMessage }),
			timestamp: message.timestamp,
		};
	}
	if (message.role === "toolResult") {
		return {
			role: "toolResult",
			toolCallId: message.toolCallId,
			toolName: message.toolName,
			content: await toGameContent(message.content, store, maximumInlineImageCharacters, signal),
			...(message.details === undefined ? {} : { details: toJsonValue(message.details) }),
			isError: message.isError,
			timestamp: message.timestamp,
		};
	}
	throw new Error("Unsupported custom Agent message in persistent conversation state.");
}

async function toPiContent(
	content: readonly GameConversationContent[],
	store: GameImageAttachmentStore | undefined,
	signal?: AbortSignal,
): Promise<
	Array<
		| { type: "text"; text: string; textSignature?: string }
		| { type: "image"; data: string; mimeType: string }
		| { type: "thinking"; thinking: string; thinkingSignature?: string; redacted?: boolean }
		| { type: "toolCall"; id: string; name: string; arguments: JsonObject; thoughtSignature?: string }
	>
> {
	return await Promise.all(
		content.map(async (part) => {
			switch (part.type) {
				case "text":
					return {
						type: "text",
						text: part.text,
						...(part.signature === undefined ? {} : { textSignature: part.signature }),
					};
				case "image":
					return { type: "image", data: part.data, mimeType: part.mimeType };
				case "imageRef":
					return await resolveAttachment(part.attachment, store, signal);
				case "reasoning":
					return {
						type: "thinking",
						thinking: part.text,
						...(part.signature === undefined ? {} : { thinkingSignature: part.signature }),
						...(part.redacted === undefined ? {} : { redacted: part.redacted }),
					};
				case "toolCall":
					return {
						type: "toolCall",
						id: part.id,
						name: part.name,
						arguments: part.arguments,
						...(part.signature === undefined ? {} : { thoughtSignature: part.signature }),
					};
				default: {
					const neverPart: never = part;
					throw new Error(`Unsupported persistent conversation content '${String(neverPart)}'.`);
				}
			}
		}),
	);
}

async function toPiMessage(
	message: GameConversationMessage,
	store: GameImageAttachmentStore | undefined,
	signal?: AbortSignal,
): Promise<AgentMessage> {
	if (message.role === "summary") {
		return {
			role: "gameSummary",
			summary: message.summary,
			tokensBefore: message.tokensBefore,
			timestamp: message.timestamp,
		};
	}
	if (message.role === "user") {
		const content =
			typeof message.content === "string" ? message.content : await toPiContent(message.content, store, signal);
		return {
			role: "user",
			content:
				typeof content === "string"
					? content
					: (content.filter((part) => part.type === "text" || part.type === "image") as UserMessage["content"]),
			timestamp: message.timestamp,
		};
	}
	if (message.role === "assistant") {
		const content = await toPiContent(message.content, store, signal);
		return {
			role: "assistant",
			content: content.filter((part) => part.type !== "image") as AssistantMessage["content"],
			api: message.api,
			provider: message.provider,
			model: message.model,
			...(message.responseId === undefined ? {} : { responseId: message.responseId }),
			usage: toPiUsage(message.usage),
			stopReason: message.stopReason as StopReason,
			...(message.errorMessage === undefined ? {} : { errorMessage: message.errorMessage }),
			timestamp: message.timestamp,
		};
	}
	const content = await toPiContent(message.content, store, signal);
	return {
		role: "toolResult",
		toolCallId: message.toolCallId,
		toolName: message.toolName,
		content: content.filter((part) => part.type === "text" || part.type === "image") as ToolResultMessage["content"],
		...(message.details === undefined ? {} : { details: message.details }),
		isError: message.isError,
		timestamp: message.timestamp,
	};
}

function toPiLlmMessages(messages: AgentMessage[]): Message[] {
	const projected: Message[] = [];
	for (const message of messages) {
		if (message.role === "gameSummary") {
			projected.push({
				role: "user",
				content: `<conversation-summary>\n${message.summary}\n</conversation-summary>`,
				timestamp: message.timestamp,
			});
		} else if (message.role === "user" || message.role === "assistant" || message.role === "toolResult") {
			projected.push(message);
		}
	}
	return projected;
}

function toPiTool(
	tool: GameTool,
	request: GameKernelRunRequest,
	active: ActivePiRun,
	store: GameImageAttachmentStore | undefined,
	maximumInlineImageCharacters: number,
	outcomes: WeakMap<object, boolean>,
): AgentTool<TSchema, JsonValue> {
	return {
		name: tool.definition.name,
		label: tool.definition.label,
		description: tool.definition.description,
		parameters: tool.definition.parameters as TSchema,
		...(tool.definition.executionMode === undefined ? {} : { executionMode: tool.definition.executionMode }),
		async execute(toolCallId, params, signal) {
			const context: GameToolExecutionContext = {
				input: request.input,
				runId: request.runId,
				turn: active.turn,
				toolCallIndex: active.toolCallIndex,
				signal: signal === undefined ? request.signal : AbortSignal.any([signal, request.signal]),
			};
			active.toolCallIndex += 1;
			const result = await tool.execute(
				{ id: toolCallId, name: tool.definition.name, arguments: params as JsonObject },
				context,
			);
			const projected = {
				content: await Promise.all(
					result.content.map(async (item) => {
						if (item.type === "image") {
							const bytes = requireInlineImage(item.data, maximumInlineImageCharacters);
							if (store) {
								const reference = await store.admit(item.mimeType, bytes, context.signal);
								return await resolveAttachment(reference, store, context.signal);
							}
							return { type: "image" as const, data: item.data, mimeType: item.mimeType };
						}
						if (item.type === "imageRef") return await resolveAttachment(item.attachment, store, context.signal);
						if (item.type === "text") return { type: "text" as const, text: item.text };
						return { type: "text" as const, text: JSON.stringify(item.value) };
					}),
				),
				details: result.details ?? null,
			};
			outcomes.set(projected, result.isError === true);
			return projected;
		},
	};
}

export class PiGameAgentKernel implements GameAgentKernelPort {
	private readonly activeRuns = new Map<string, ActivePiRun>();
	private readonly maximumInlineImageCharacters: number;

	constructor(private readonly options: PiGameAgentKernelOptions) {
		this.maximumInlineImageCharacters = options.maximumInlineImageCharacters ?? DEFAULT_MAXIMUM_INLINE_IMAGE_CHARACTERS;
		if (!Number.isSafeInteger(this.maximumInlineImageCharacters) || this.maximumInlineImageCharacters < 4) {
			throw new RangeError("maximumInlineImageCharacters must be a positive base64 character bound.");
		}
	}

	run(request: GameKernelRunRequest): AsyncIterable<GameAgentEvent> {
		if (this.activeRuns.has(request.runId)) throw new Error(`Run '${request.runId}' is already active.`);
		if (!Number.isInteger(request.maximumTurns) || request.maximumTurns < 1) {
			throw new RangeError("maximumTurns must be a positive integer.");
		}

		const queue = new AsyncQueue<GameAgentEvent>();
		let sequence = 0;
		let lastVisibleText = "";
		const active: ActivePiRun = {
			session: request.input.session,
			runId: request.runId,
			turn: 0,
			toolCallIndex: 0,
			closed: false,
			aborted: false,
		};
		this.activeRuns.set(request.runId, active);
		const makeEvent = (event: ProjectedGameAgentEvent): GameAgentEvent => {
			sequence += 1;
			return {
				...event,
				sequence,
				eventId: `${request.runId}:${sequence}`,
				runId: request.runId,
				turn: active.turn,
				audience: { visibility: "owner" },
				timestamp: Date.now(),
			} as GameAgentEvent;
		};

		void this.runAgent(
			request,
			active,
			makeEvent,
			queue,
			() => lastVisibleText,
			(text) => {
				lastVisibleText = text;
			},
		)
			.catch((error: unknown) => {
				queue.push(
					makeEvent({
						type: "run.failed",
						category: "kernel",
						message: error instanceof Error ? error.message : "Unknown kernel failure.",
					}),
				);
			})
			.finally(() => {
				active.closed = true;
				this.activeRuns.delete(request.runId);
				queue.end();
			});

		return queue;
	}

	steer(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput): GameControlResult {
		return this.control(session, expected, (run) =>
			run.agent.steer(controlInputToMessage(input, this.maximumInlineImageCharacters)),
		);
	}

	followUp(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput): GameControlResult {
		return this.control(session, expected, (run) =>
			run.agent.followUp(controlInputToMessage(input, this.maximumInlineImageCharacters)),
		);
	}

	abort(session: GameSessionKey, expected: GameRunCoordinate): GameControlResult {
		return this.control(session, expected, (run) => {
			run.aborted = true;
			run.agent.abort();
		});
	}

	private control(
		session: GameSessionKey,
		expected: GameRunCoordinate,
		action: (run: ActiveReadyPiRun) => void,
	): GameControlResult {
		const run = this.activeRuns.get(expected.runId);
		if (!run) return { accepted: false, reason: "not-active" };
		if (run.closed) return { accepted: false, reason: "closed" };
		if (!run.agent) return { accepted: false, reason: "not-active" };
		if (!sameGameSession(run.session, session)) return { accepted: false, reason: "run-mismatch" };
		if (run.turn !== expected.turn) return { accepted: false, reason: "turn-mismatch" };
		action(run as ActiveReadyPiRun);
		return { accepted: true };
	}

	private async runAgent(
		request: GameKernelRunRequest,
		active: ActivePiRun,
		makeEvent: (event: ProjectedGameAgentEvent) => GameAgentEvent,
		queue: AsyncQueue<GameAgentEvent>,
		getLastVisibleText: () => string,
		setLastVisibleText: (text: string) => void,
	): Promise<void> {
		const resolved = this.options.models.resolve(request.modelProfileId);
		const operationSignal = request.signal;
		let conversation = await this.options.conversationStore?.read(request.input.session, operationSignal);
		if (conversation && this.options.conversationStore && this.options.conversationCompactor) {
			const compacted = await this.options.conversationCompactor.compact(
				{ session: request.input.session, snapshot: conversation, model: resolved.descriptor },
				operationSignal,
			);
			if (compacted.usage !== undefined) active.usage = addGameUsage(active.usage, compacted.usage);
			if (compacted.changed) {
				conversation = await this.options.conversationStore.save(
					request.input.session,
					conversation.revision,
					compacted.messages,
					operationSignal,
				);
			}
		}
		const prepareNextTurn = request.prepareNextTurn;
		const toolOutcomes = new WeakMap<object, boolean>();
		const agent = new Agent({
			streamFn: resolved.streamFn,
			convertToLlm: toPiLlmMessages,
			initialState: {
				systemPrompt: request.systemPrompt,
				model: resolved.model,
				thinkingLevel: resolved.thinkingLevel,
				tools: request.tools.map((tool) =>
					toPiTool(
						tool,
						request,
						active,
						this.options.imageAttachments,
						this.maximumInlineImageCharacters,
						toolOutcomes,
					),
				),
				messages:
					conversation === undefined
						? []
						: await Promise.all(
								conversation.messages.map((message) =>
									toPiMessage(message, this.options.imageAttachments, operationSignal),
								),
							),
			},
			afterToolCall: async ({ result }) => {
				const isError = toolOutcomes.get(result);
				return isError === undefined ? undefined : { isError };
			},
			sessionId: request.input.session.sessionId,
			shouldStopAfterTurn: () => active.turn >= request.maximumTurns,
			...(prepareNextTurn
				? {
						prepareNextTurnWithContext: async (context, signal): Promise<AgentLoopTurnUpdate | undefined> => {
							const update = await prepareNextTurn(
								{
									input: request.input,
									runId: request.runId,
									turn: active.turn,
									hadToolResults: context.toolResults.length > 0,
								},
								signal === undefined ? request.signal : AbortSignal.any([signal, request.signal]),
							);
							if (!update) return undefined;
							return {
								context: {
									...context.context,
									systemPrompt: update.systemPrompt,
									tools: update.tools.map((tool) =>
										toPiTool(
											tool,
											request,
											active,
											this.options.imageAttachments,
											this.maximumInlineImageCharacters,
											toolOutcomes,
										),
									),
								},
							};
						},
					}
				: {}),
		});
		active.agent = agent;
		const agentSignal = agent.signal ?? request.signal;
		agent.subscribe(async (event, eventSignal) => {
			if (event.type === "agent_end" && this.options.conversationStore) {
				await this.options.conversationStore.save(
					request.input.session,
					conversation?.revision ?? 0,
					await Promise.all(
						agent.state.messages.map((message) =>
							toGameMessage(message, this.options.imageAttachments, this.maximumInlineImageCharacters, agentSignal),
						),
					),
					AbortSignal.any([agentSignal, request.signal]),
				);
			}
			await this.projectEvent(
				event,
				active,
				request,
				resolved,
				makeEvent,
				queue,
				getLastVisibleText(),
				setLastVisibleText,
				AbortSignal.any([request.signal, eventSignal]),
			);
		});
		const abort = () => {
			active.aborted = true;
			agent.abort();
		};
		request.signal.addEventListener("abort", abort, { once: true });
		try {
			if (request.signal.aborted) agent.abort();
			else {
				await agent.prompt(
					await inputToMessage(
						request.input,
						this.options.imageAttachments,
						this.maximumInlineImageCharacters,
						AbortSignal.any([agentSignal, request.signal]),
					),
				);
			}
		} finally {
			request.signal.removeEventListener("abort", abort);
		}
	}

	private async projectEvent(
		event: AgentEvent,
		active: ActivePiRun,
		request: GameKernelRunRequest,
		resolved: ResolvedPiGameModel,
		makeEvent: (event: ProjectedGameAgentEvent) => GameAgentEvent,
		queue: AsyncQueue<GameAgentEvent>,
		lastVisibleText: string,
		setLastVisibleText: (text: string) => void,
		signal: AbortSignal,
	): Promise<void> {
		const publish = async (payload: ProjectedGameAgentEvent): Promise<void> => {
			const projected = makeEvent(payload);
			await request.beforeEvent?.(projected, signal);
			queue.push(projected);
		};
		switch (event.type) {
			case "agent_start":
				await publish({ type: "run.started", inputId: request.input.id, model: resolved.descriptor });
				break;
			case "turn_start":
				active.turn += 1;
				active.toolCallIndex = 0;
				setLastVisibleText("");
				await publish({ type: "turn.started" });
				break;
			case "message_update": {
				const text = visibleText(event.message);
				const delta = text.startsWith(lastVisibleText) ? text.slice(lastVisibleText.length) : text;
				if (delta.length > 0) await publish({ type: "message.delta", text: delta });
				setLastVisibleText(text);
				break;
			}
			case "message_end":
				if (event.message.role === "assistant") {
					if (event.message.stopReason === "aborted") active.aborted = true;
					const usage = toGameUsage(event.message.usage);
					active.usage = addGameUsage(active.usage, usage);
					await publish({
						type: "message.completed",
						text: visibleText(event.message),
						usage,
						provider: event.message.provider,
						model: event.message.model,
						...(event.message.responseId === undefined ? {} : { responseId: event.message.responseId }),
					});
				}
				break;
			case "tool_execution_start":
				await publish({
					type: "tool.started",
					call: { id: event.toolCallId, name: event.toolName, arguments: toJsonValue(event.args) as JsonObject },
				});
				break;
			case "tool_execution_update":
				await publish({ type: "tool.progress", callId: event.toolCallId, update: toJsonValue(event.partialResult) });
				break;
			case "tool_execution_end": {
				const result = event.result as {
					content?: Array<{ type: "text"; text: string } | { type: "image"; data: string; mimeType: string }>;
					details?: unknown;
				};
				await publish({
					type: "tool.completed",
					callId: event.toolCallId,
					result: {
						content: result.content ?? [],
						...(result.details === undefined ? {} : { details: toJsonValue(result.details) }),
						isError: event.isError,
					},
				});
				break;
			}
			case "turn_end":
				await publish({ type: "turn.completed" });
				break;
			case "agent_end":
				await publish(
					active.aborted
						? { type: "run.aborted" }
						: { type: "run.completed", ...(active.usage === undefined ? {} : { usage: active.usage }) },
				);
				break;
			case "message_start":
				break;
		}
	}
}
