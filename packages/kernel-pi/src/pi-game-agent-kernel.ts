import { Agent, type AgentEvent, type AgentMessage, type AgentTool } from "@earendil-works/pi-agent-core";
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
import type { TSchema } from "typebox";
import { AsyncQueue } from "./async-queue.js";
import type { PiGameModelResolver, ResolvedPiGameModel } from "./model-registry.js";

export interface PiGameAgentKernelOptions {
	models: PiGameModelResolver;
	conversationStore?: GameConversationStore;
}

type GameAgentEventPayload<T> = T extends GameAgentEvent
	? Omit<T, "sequence" | "eventId" | "runId" | "turn" | "audience" | "timestamp">
	: never;
type ProjectedGameAgentEvent = GameAgentEventPayload<GameAgentEvent>;

interface ActivePiRun {
	agent?: Agent;
	session: GameSessionKey;
	runId: string;
	turn: number;
	toolCallIndex: number;
	closed: boolean;
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

function inputToMessage(input: GameInput): UserMessage {
	const images: ImageContent[] = input.content
		.filter((item): item is Extract<GameInputContent, { type: "image" }> => item.type === "image")
		.map((item) => ({ type: "image", data: item.data, mimeType: item.mimeType }));
	const envelope = {
		type: input.type,
		moment: input.moment,
		content: input.content
			.filter((item) => item.type !== "image")
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

function toGameContent(
	content: readonly (TextContent | ImageContent | ThinkingContent | ToolCall)[],
): GameConversationContent[] {
	return content.map((part) => {
		switch (part.type) {
			case "text":
				return {
					type: "text",
					text: String(part.text ?? ""),
					...(typeof part.textSignature === "string" ? { signature: part.textSignature } : {}),
				};
			case "image":
				return { type: "image", data: String(part.data ?? ""), mimeType: String(part.mimeType ?? "") };
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
	});
}

function toGameMessage(message: AgentMessage): GameConversationMessage {
	if (message.role === "user") {
		return {
			role: "user",
			content: typeof message.content === "string" ? message.content : toGameContent(message.content),
			timestamp: message.timestamp,
		};
	}
	if (message.role === "assistant") {
		return {
			role: "assistant",
			content: toGameContent(message.content),
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
			content: toGameContent(message.content),
			...(message.details === undefined ? {} : { details: toJsonValue(message.details) }),
			isError: message.isError,
			timestamp: message.timestamp,
		};
	}
	throw new Error("Unsupported custom Agent message in persistent conversation state.");
}

function toPiContent(
	content: readonly GameConversationContent[],
): Array<
	| { type: "text"; text: string; textSignature?: string }
	| { type: "image"; data: string; mimeType: string }
	| { type: "thinking"; thinking: string; thinkingSignature?: string; redacted?: boolean }
	| { type: "toolCall"; id: string; name: string; arguments: JsonObject; thoughtSignature?: string }
> {
	return content.map((part) => {
		switch (part.type) {
			case "text":
				return {
					type: "text",
					text: part.text,
					...(part.signature === undefined ? {} : { textSignature: part.signature }),
				};
			case "image":
				return { type: "image", data: part.data, mimeType: part.mimeType };
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
	});
}

function toPiMessage(message: GameConversationMessage): Message {
	if (message.role === "user") {
		return {
			role: "user",
			content:
				typeof message.content === "string"
					? message.content
					: (toPiContent(message.content).filter(
							(part) => part.type === "text" || part.type === "image",
						) as UserMessage["content"]),
			timestamp: message.timestamp,
		};
	}
	if (message.role === "assistant") {
		return {
			role: "assistant",
			content: toPiContent(message.content).filter((part) => part.type !== "image") as AssistantMessage["content"],
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
	return {
		role: "toolResult",
		toolCallId: message.toolCallId,
		toolName: message.toolName,
		content: toPiContent(message.content).filter(
			(part) => part.type === "text" || part.type === "image",
		) as ToolResultMessage["content"],
		...(message.details === undefined ? {} : { details: message.details }),
		isError: message.isError,
		timestamp: message.timestamp,
	};
}

function toPiTool(tool: GameTool, request: GameKernelRunRequest, active: ActivePiRun): AgentTool<TSchema, JsonValue> {
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
				signal: signal ?? new AbortController().signal,
			};
			active.toolCallIndex += 1;
			const result = await tool.execute(
				{ id: toolCallId, name: tool.definition.name, arguments: params as JsonObject },
				context,
			);
			return {
				content: result.content.map((item) => {
					if (item.type === "image") return { type: "image" as const, data: item.data, mimeType: item.mimeType };
					if (item.type === "text") return { type: "text" as const, text: item.text };
					return { type: "text" as const, text: JSON.stringify(item.value) };
				}),
				details: result.details ?? null,
			};
		},
	};
}

export class PiGameAgentKernel implements GameAgentKernelPort {
	private readonly activeRuns = new Map<string, ActivePiRun>();

	constructor(private readonly options: PiGameAgentKernelOptions) {}

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
		return this.control(session, expected, (run) => run.agent.steer(inputToMessage(input)));
	}

	followUp(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput): GameControlResult {
		return this.control(session, expected, (run) => run.agent.followUp(inputToMessage(input)));
	}

	abort(session: GameSessionKey, expected: GameRunCoordinate): GameControlResult {
		return this.control(session, expected, (run) => run.agent.abort());
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
		const conversation = await this.options.conversationStore?.read(request.input.session);
		const resolved = this.options.models.resolve(request.modelProfileId);
		const agent = new Agent({
			streamFn: resolved.streamFn,
			initialState: {
				systemPrompt: request.systemPrompt,
				model: resolved.model,
				thinkingLevel: resolved.thinkingLevel,
				tools: request.tools.map((tool) => toPiTool(tool, request, active)),
				messages: conversation?.messages.map(toPiMessage) ?? [],
			},
			sessionId: request.input.session.sessionId,
			shouldStopAfterTurn: () => active.turn >= request.maximumTurns,
		});
		active.agent = agent;
		agent.subscribe(async (event) => {
			if (event.type === "agent_end" && this.options.conversationStore) {
				await this.options.conversationStore.save(
					request.input.session,
					conversation?.revision ?? 0,
					agent.state.messages.map(toGameMessage),
					agent.signal,
				);
			}
			this.projectEvent(event, active, request, resolved, makeEvent, queue, getLastVisibleText(), setLastVisibleText);
		});
		await agent.prompt(inputToMessage(request.input));
	}

	private projectEvent(
		event: AgentEvent,
		active: ActivePiRun,
		request: GameKernelRunRequest,
		resolved: ResolvedPiGameModel,
		makeEvent: (event: ProjectedGameAgentEvent) => GameAgentEvent,
		queue: AsyncQueue<GameAgentEvent>,
		lastVisibleText: string,
		setLastVisibleText: (text: string) => void,
	): void {
		switch (event.type) {
			case "agent_start":
				queue.push(makeEvent({ type: "run.started", inputId: request.input.id, model: resolved.descriptor }));
				break;
			case "turn_start":
				active.turn += 1;
				active.toolCallIndex = 0;
				setLastVisibleText("");
				queue.push(makeEvent({ type: "turn.started" }));
				break;
			case "message_update": {
				const text = visibleText(event.message);
				const delta = text.startsWith(lastVisibleText) ? text.slice(lastVisibleText.length) : text;
				if (delta.length > 0) queue.push(makeEvent({ type: "message.delta", text: delta }));
				setLastVisibleText(text);
				break;
			}
			case "message_end":
				if (event.message.role === "assistant") {
					const usage = toGameUsage(event.message.usage);
					active.usage = addGameUsage(active.usage, usage);
					queue.push(
						makeEvent({
							type: "message.completed",
							text: visibleText(event.message),
							usage,
							provider: event.message.provider,
							model: event.message.model,
							...(event.message.responseId === undefined ? {} : { responseId: event.message.responseId }),
						}),
					);
				}
				break;
			case "tool_execution_start":
				queue.push(
					makeEvent({
						type: "tool.started",
						call: { id: event.toolCallId, name: event.toolName, arguments: toJsonValue(event.args) as JsonObject },
					}),
				);
				break;
			case "tool_execution_update":
				queue.push(
					makeEvent({ type: "tool.progress", callId: event.toolCallId, update: toJsonValue(event.partialResult) }),
				);
				break;
			case "tool_execution_end": {
				const result = event.result as {
					content?: Array<{ type: "text"; text: string } | { type: "image"; data: string; mimeType: string }>;
					details?: unknown;
				};
				queue.push(
					makeEvent({
						type: "tool.completed",
						callId: event.toolCallId,
						result: {
							content: result.content ?? [],
							...(result.details === undefined ? {} : { details: toJsonValue(result.details) }),
							isError: event.isError,
						},
					}),
				);
				break;
			}
			case "turn_end":
				queue.push(makeEvent({ type: "turn.completed" }));
				break;
			case "agent_end":
				queue.push(
					makeEvent({ type: "run.completed", ...(active.usage === undefined ? {} : { usage: active.usage }) }),
				);
				break;
			case "message_start":
				break;
		}
	}
}
