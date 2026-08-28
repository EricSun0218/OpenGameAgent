export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export interface GameSessionKey {
	worldId: string;
	saveId: string;
	timelineId: string;
	generation: number;
	ownerId: string;
	sessionId: string;
	actorId: string;
}

export interface GameMoment {
	tick: number;
	calendar?: string;
	phase?: string;
}

export interface GameImageAttachmentReference {
	id: string;
	sha256: string;
	mimeType: string;
	bytes: number;
	width: number;
	height: number;
}

export interface GameImageAttachment {
	reference: GameImageAttachmentReference;
	data: Uint8Array;
}

export interface GameImageAttachmentStore {
	admit(mimeType: string, data: Uint8Array, signal?: AbortSignal): Promise<GameImageAttachmentReference>;
	read(id: string, signal?: AbortSignal): Promise<GameImageAttachment | undefined>;
}

export type GameInputContent =
	| { type: "text"; text: string }
	| { type: "json"; value: JsonValue }
	| { type: "image"; mimeType: string; data: string }
	| { type: "imageRef"; attachment: GameImageAttachmentReference };

export interface GameInput {
	id: string;
	type: string;
	session: GameSessionKey;
	moment: GameMoment;
	content: readonly GameInputContent[];
	context?: JsonObject;
}

export interface GameToolDefinition {
	name: string;
	label: string;
	description: string;
	parameters: JsonObject;
	executionMode?: "parallel" | "sequential";
	risk?: "read" | "low" | "medium" | "high" | "critical";
}

export interface GameToolCall {
	id: string;
	name: string;
	arguments: JsonObject;
}

/**
 * Trusted coordinates supplied by the runtime when a model-requested tool is
 * about to execute. They are never accepted from model output or client input.
 */
export interface GameToolExecutionContext {
	input: GameInput;
	runId: string;
	turn: number;
	toolCallIndex: number;
	signal: AbortSignal;
}

export interface GameToolResult {
	content: readonly GameInputContent[];
	details?: JsonValue;
	isError?: boolean;
}

export interface GameTool {
	definition: GameToolDefinition;
	execute(call: GameToolCall, context: GameToolExecutionContext): Promise<GameToolResult>;
}

export interface GameUsage {
	input: number;
	output: number;
	cacheRead: number;
	cacheWrite: number;
	reasoning?: number;
	totalTokens: number;
	cost?: {
		input: number;
		output: number;
		cacheRead: number;
		cacheWrite: number;
		total: number;
	};
}

export interface GameResolvedModel {
	profileId: string;
	provider: string;
	model: string;
	api: string;
	reasoning: boolean;
	input: readonly ("text" | "image")[];
	contextWindow: number;
	maximumOutputTokens: number;
}

export type GameConversationContent =
	| { type: "text"; text: string; signature?: string }
	| { type: "image"; mimeType: string; data: string }
	| { type: "imageRef"; attachment: GameImageAttachmentReference }
	| { type: "reasoning"; text: string; signature?: string; redacted?: boolean }
	| { type: "toolCall"; id: string; name: string; arguments: JsonObject; signature?: string };

export type GameConversationMessage =
	| { role: "user"; content: string | readonly GameConversationContent[]; timestamp: number }
	| { role: "summary"; summary: string; tokensBefore: number; timestamp: number }
	| {
			role: "assistant";
			content: readonly GameConversationContent[];
			api: string;
			provider: string;
			model: string;
			responseId?: string;
			usage: GameUsage;
			stopReason: string;
			errorMessage?: string;
			timestamp: number;
	  }
	| {
			role: "toolResult";
			toolCallId: string;
			toolName: string;
			content: readonly GameConversationContent[];
			details?: JsonValue;
			isError: boolean;
			timestamp: number;
	  };

export interface GameConversationSnapshot {
	revision: number;
	messages: readonly GameConversationMessage[];
}

export interface GameConversationStore {
	read(session: GameSessionKey, signal?: AbortSignal): Promise<GameConversationSnapshot>;
	save(
		session: GameSessionKey,
		expectedRevision: number,
		messages: readonly GameConversationMessage[],
		signal?: AbortSignal,
	): Promise<GameConversationSnapshot>;
}

export type GameAudience =
	| { visibility: "internal" }
	| { visibility: "owner" }
	| { visibility: "public" }
	| { visibility: "recipient"; recipientId: string };

export interface GameRunCoordinate {
	runId: string;
	turn: number;
}

interface GameAgentEventBase {
	sequence: number;
	eventId: string;
	runId: string;
	turn: number;
	audience: GameAudience;
	timestamp: number;
}

export type GameAgentEvent =
	| (GameAgentEventBase & { type: "run.started"; inputId: string; model: GameResolvedModel })
	| (GameAgentEventBase & { type: "run.completed"; usage?: GameUsage })
	| (GameAgentEventBase & { type: "run.failed"; category: string; message: string })
	| (GameAgentEventBase & { type: "run.aborted" })
	| (GameAgentEventBase & { type: "turn.started" })
	| (GameAgentEventBase & { type: "turn.completed" })
	| (GameAgentEventBase & { type: "message.delta"; text: string })
	| (GameAgentEventBase & {
			type: "message.completed";
			text: string;
			usage?: GameUsage;
			provider?: string;
			model?: string;
			responseId?: string;
	  })
	| (GameAgentEventBase & { type: "tool.started"; call: GameToolCall })
	| (GameAgentEventBase & { type: "tool.progress"; callId: string; update: JsonValue })
	| (GameAgentEventBase & { type: "tool.completed"; callId: string; result: GameToolResult });

export interface GameKernelRunRequest {
	runId: string;
	input: GameInput;
	signal: AbortSignal;
	systemPrompt: string;
	tools: readonly GameTool[];
	modelProfileId: string;
	maximumTurns: number;
	prepareNextTurn?: (
		context: GameKernelTurnPreparationContext,
		signal: AbortSignal,
	) => Promise<GameKernelTurnUpdate | undefined>;
	/**
	 * Awaited event boundary. Kernels invoke this before publishing an event or
	 * performing work that is causally after it, including Tool execution after
	 * `tool.started`. The runtime isolates hook failures from the Agent loop.
	 */
	beforeEvent?: (event: GameAgentEvent, signal: AbortSignal) => Promise<void>;
}

export interface GameKernelTurnPreparationContext {
	input: GameInput;
	runId: string;
	turn: number;
	hadToolResults: boolean;
}

export interface GameKernelTurnUpdate {
	systemPrompt: string;
	tools: readonly GameTool[];
}

export interface GameControlResult {
	accepted: boolean;
	reason?: "not-active" | "run-mismatch" | "turn-mismatch" | "closed";
}

export interface GameActionIdentity {
	session: GameSessionKey;
	inputId: string;
	runId: string;
	turn: number;
	toolCallIndex: number;
	action: string;
}

export interface GameActionIntent extends GameActionIdentity {
	operationId: string;
	args: JsonObject;
	moment: GameMoment;
	expectedRevision: number;
	conflictKey?: string;
}

export type GameActionTerminalStatus = "committed" | "rejected" | "failed";
export type GameActionJournalStatus = "prepared" | "dispatched" | "uncertain" | GameActionTerminalStatus;

export interface GameActionReceipt {
	operationId: string;
	session: GameSessionKey;
	action: string;
	expectedRevision: number;
	stateRevision: number;
	status: GameActionTerminalStatus;
	result: JsonValue;
}

export interface GameActionJournalEntry {
	intent: GameActionIntent;
	status: GameActionJournalStatus;
	attempt: number;
	preparedAt: number;
	dispatchedAt?: number;
	receipt?: GameActionReceipt;
}

export interface GameAgentKernelPort {
	run(request: GameKernelRunRequest): AsyncIterable<GameAgentEvent>;
	steer(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput): GameControlResult;
	followUp(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput): GameControlResult;
	abort(session: GameSessionKey, expected: GameRunCoordinate): GameControlResult;
}
