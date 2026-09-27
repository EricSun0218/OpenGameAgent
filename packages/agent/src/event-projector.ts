import { createHash } from "node:crypto";
import type { AgentEvent, AgentMessage } from "@earendil-works/pi-agent-core";
import type { Api, StopReason, ThinkingContent } from "@earendil-works/pi-ai";
import type {
	AssistantMessageContent,
	CanonicalEventData,
	CanonicalItem,
	FinishReason,
	MessageContent,
	ToolResultContent,
} from "@opengameagent/protocol";
import { canonicalAssistantText } from "@opengameagent/protocol";
import { decodeCanonicalBase64 } from "./canonical-base64.js";

interface ProjectorOptions {
	initialTurn?: bigint;
	initialFinalOutput?: string;
	newId: () => string;
	setTurn: (turn: bigint) => void;
	emit: (data: CanonicalEventData) => Promise<void>;
}

interface ActiveMessage {
	readonly itemId: string;
	readonly role: "user" | "assistant";
	readonly transcriptContent?: readonly MessageContent[];
}

interface ToolCallSource {
	readonly parentMessageItemId: string;
	readonly sourceIndex: number;
}

export class PiEventProjector {
	private turn: bigint;
	private activeMessage: ActiveMessage | undefined;
	private pendingUserContent: readonly MessageContent[] | undefined;
	private readonly toolCallSources = new Map<string, ToolCallSource>();
	private readonly toolCalls = new Map<string, Extract<CanonicalItem, { itemType: "tool-call" }>>();
	private readonly toolResults = new Map<string, Extract<CanonicalItem, { itemType: "tool-result" }>>();
	private readonly toolUpdateOrdinals = new Map<string, bigint>();
	private lastStopReason: StopReason = "stop";
	private finalOutput: string | undefined;

	constructor(private readonly options: ProjectorOptions) {
		this.turn = options.initialTurn ?? 0n;
		this.finalOutput = options.initialFinalOutput;
	}

	setNextUserContent(content: readonly MessageContent[]): void {
		if (this.pendingUserContent) throw new TypeError("A projected user message is already pending.");
		this.pendingUserContent = content.map((part) => Object.freeze({ ...part }));
	}

	clearPendingUserContent(): void {
		this.pendingUserContent = undefined;
	}

	async project(event: AgentEvent): Promise<void> {
		switch (event.type) {
			case "agent_start":
			case "agent_end":
				return;
			case "turn_start":
				this.turn += 1n;
				this.options.setTurn(this.turn);
				await this.options.emit({ type: "turn.started" });
				return;
			case "message_start":
				await this.startMessage(event.message);
				return;
			case "message_update":
				if (event.assistantMessageEvent.type === "text_delta" && this.activeMessage?.role === "assistant") {
					await this.options.emit({
						type: "item.delta",
						itemId: this.activeMessage.itemId,
						audience: { visibility: "owner" },
						delta: {
							type: "text",
							contentIndex: event.assistantMessageEvent.contentIndex,
							text: event.assistantMessageEvent.delta,
						},
					});
				}
				return;
			case "message_end":
				await this.completeMessage(event.message);
				return;
			case "tool_execution_start":
				await this.startToolCall(event.toolCallId, event.toolName);
				return;
			case "tool_execution_update":
				await this.updateToolCall(event.toolCallId, event.partialResult);
				return;
			case "tool_execution_end":
				await this.completeToolCall(event.toolCallId, event.toolName, event.result, event.isError);
				return;
			case "turn_end":
				if (event.message.role === "assistant") this.lastStopReason = event.message.stopReason;
				await this.options.emit({
					type: "turn.completed",
					finishReason: mapFinishReason(this.lastStopReason),
					toolResultItemIds: event.toolResults.flatMap((result) => {
						const item = this.toolResults.get(result.toolCallId);
						return item ? [item.itemId] : [];
					}),
				});
				return;
		}
	}

	completionStatus(overrideFailure: boolean): "completed" | "aborted" | "failed" {
		if (overrideFailure || this.lastStopReason === "error") return "failed";
		if (this.lastStopReason === "aborted") return "aborted";
		return "completed";
	}

	readFinalOutput(): string | undefined {
		return this.finalOutput;
	}

	private async startMessage(message: AgentMessage): Promise<void> {
		if (message.role !== "user" && message.role !== "assistant") return;
		const transcriptContent = message.role === "user" ? this.pendingUserContent : undefined;
		if (message.role === "user") this.pendingUserContent = undefined;
		const itemId = this.options.newId();
		const item = this.messageItem(message, itemId, "started", transcriptContent);
		this.activeMessage = {
			itemId,
			role: message.role,
			...(transcriptContent ? { transcriptContent } : {}),
		};
		await this.options.emit({ type: "item.started", item });
	}

	private async completeMessage(message: AgentMessage): Promise<void> {
		if (message.role !== "user" && message.role !== "assistant") return;
		const active = this.activeMessage;
		if (!active || active.role !== message.role) return;
		if (message.role === "assistant") this.captureToolCallSources(message, active.itemId);
		this.activeMessage = undefined;
		const item = this.messageItem(message, active.itemId, "completed", active.transcriptContent);
		const text = canonicalAssistantText(item);
		if (text !== undefined) this.finalOutput = text;
		await this.options.emit({
			type: "item.completed",
			item,
		});
	}

	private messageItem(
		message: Extract<AgentMessage, { role: "user" | "assistant" }>,
		itemId: string,
		phase: "started" | "completed",
		transcriptContent?: readonly MessageContent[],
	): Extract<CanonicalItem, { itemType: "message" }> {
		if (message.role === "user") {
			return {
				itemType: "message",
				itemId,
				role: "user",
				content: transcriptContent ? transcriptContent.map((part) => ({ ...part })) : visibleUserContent(message),
				audience: { visibility: "owner" },
			};
		}
		return {
			itemType: "message",
			itemId,
			role: "assistant",
			content: phase === "completed" ? visibleAssistantContent(message) : [],
			audience: { visibility: "owner" },
		};
	}

	private captureToolCallSources(message: Extract<AgentMessage, { role: "assistant" }>, itemId: string): void {
		for (const [sourceIndex, part] of message.content.entries()) {
			if (part.type === "toolCall") {
				this.toolCallSources.set(part.id, { parentMessageItemId: itemId, sourceIndex });
			}
		}
	}

	private async startToolCall(toolCallId: string, toolName: string): Promise<void> {
		const source = this.toolCallSources.get(toolCallId);
		const item = {
			itemType: "tool-call" as const,
			itemId: this.options.newId(),
			toolCallId,
			toolName,
			...(source ?? {}),
			audience: { visibility: "internal" as const },
		};
		this.toolCalls.set(toolCallId, item);
		this.toolUpdateOrdinals.set(toolCallId, 0n);
		await this.options.emit({ type: "item.started", item });
	}

	private async updateToolCall(toolCallId: string, partialResult: unknown): Promise<void> {
		const item = this.toolCalls.get(toolCallId);
		if (!item) return;
		const updateOrdinal = (this.toolUpdateOrdinals.get(toolCallId) ?? 0n) + 1n;
		this.toolUpdateOrdinals.set(toolCallId, updateOrdinal);
		await this.options.emit({
			type: "item.updated",
			itemId: item.itemId,
			audience: { visibility: "internal" },
			updateOrdinal: updateOrdinal.toString(),
			content: projectToolResultContent(partialResult),
		});
	}

	private async completeToolCall(
		toolCallId: string,
		toolName: string,
		result: unknown,
		isError: boolean,
	): Promise<void> {
		const callItem = this.toolCalls.get(toolCallId);
		if (callItem) await this.options.emit({ type: "item.completed", item: callItem });

		const resultItem = {
			itemType: "tool-result" as const,
			itemId: this.options.newId(),
			toolCallId,
			toolName,
			isError,
			content: projectToolResultContent(result),
			audience: { visibility: "internal" as const },
		};
		this.toolResults.set(toolCallId, resultItem);
		await this.options.emit({ type: "item.started", item: resultItem });
		await this.options.emit({ type: "item.completed", item: resultItem });
	}
}

function visibleUserContent(message: Extract<AgentMessage, { role: "user" }>): MessageContent[] {
	if (typeof message.content === "string") return [{ type: "text", text: message.content }];
	return message.content.flatMap((part) => (part.type === "text" ? [{ type: "text" as const, text: part.text }] : []));
}

function visibleAssistantContent(message: Extract<AgentMessage, { role: "assistant" }>): AssistantMessageContent[] {
	const visible: AssistantMessageContent[] = [];
	for (const [sourceIndex, part] of message.content.entries()) {
		if (part.type === "text") {
			visible.push({ type: "text", text: part.text, sourceIndex });
			continue;
		}
		if (part.type !== "thinking") continue;
		const text = openAIResponsesReasoningSummary(message.api, part);
		if (text !== undefined) visible.push({ type: "reasoning-summary", text, sourceIndex });
	}
	return visible;
}

function openAIResponsesReasoningSummary(api: Api, part: ThinkingContent): string | undefined {
	if (
		(api !== "openai-responses" && api !== "azure-openai-responses" && api !== "openai-codex-responses") ||
		part.redacted === true ||
		part.thinkingSignature === undefined
	) {
		return undefined;
	}
	let item: unknown;
	try {
		item = JSON.parse(part.thinkingSignature);
	} catch {
		return undefined;
	}
	if (!item || typeof item !== "object" || !("type" in item) || item.type !== "reasoning" || !("summary" in item)) {
		return undefined;
	}
	if (!Array.isArray(item.summary)) return undefined;
	const summaries: string[] = [];
	for (const summary of item.summary) {
		if (
			!summary ||
			typeof summary !== "object" ||
			!("type" in summary) ||
			summary.type !== "summary_text" ||
			!("text" in summary) ||
			typeof summary.text !== "string"
		) {
			return undefined;
		}
		summaries.push(summary.text);
	}
	const text = summaries.join("\n\n");
	return text.length > 0 && part.thinking === text ? text : undefined;
}

function projectToolResultContent(result: unknown): ToolResultContent[] {
	if (!result || typeof result !== "object" || !("content" in result) || !Array.isArray(result.content)) return [];
	const projected: ToolResultContent[] = [];
	for (const part of result.content as unknown[]) {
		if (!part || typeof part !== "object" || !("type" in part)) continue;
		if (part.type === "text" && "text" in part && typeof part.text === "string") {
			projected.push({ type: "text", text: part.text });
			continue;
		}
		if (
			part.type === "image" &&
			"data" in part &&
			typeof part.data === "string" &&
			"mimeType" in part &&
			typeof part.mimeType === "string" &&
			part.mimeType.length > 0
		) {
			const bytes = decodeCanonicalBase64(part.data);
			if (bytes) {
				projected.push({
					type: "media-summary",
					mediaType: part.mimeType,
					bytes: bytes.byteLength,
					sha256: createHash("sha256").update(bytes).digest("hex"),
				});
			}
		}
	}
	return projected;
}

function mapFinishReason(reason: StopReason): FinishReason {
	switch (reason) {
		case "stop":
			return "stop";
		case "toolUse":
			return "tool-use";
		case "length":
			return "length";
		case "aborted":
			return "aborted";
		case "error":
			return "error";
		case "deferred":
			return "deferred";
		default:
			return "other";
	}
}
