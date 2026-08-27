import type {
	GameActionIntent,
	GameActionJournalEntry,
	GameActionReceipt,
	GameTool,
	GameToolCall,
	GameToolDefinition,
	GameToolExecutionContext,
	GameToolResult,
	JsonObject,
} from "@opengameagent/protocol";
import type { DurableGameActionDispatcher, GameActionDispatchResult } from "./dispatcher.js";
import { createGameActionOperationId } from "./operation-id.js";

export const MAXIMUM_MODEL_ACTION_RECEIPT_CHARACTERS = 64_000;
export const MAXIMUM_ACTION_CONFLICT_KEY_CHARACTERS = 1_024;

export interface GameActionModelReceiptProjectionContext {
	intent: GameActionIntent;
	receipt: GameActionReceipt;
}

export type GameActionModelReceiptProjector = (context: GameActionModelReceiptProjectionContext) => JsonObject;

export interface GameActionToolOptions {
	definition: GameToolDefinition;
	dispatcher: DurableGameActionDispatcher;
	expectedRevision: number | ((call: GameToolCall, context: GameToolExecutionContext) => number | Promise<number>);
	conflictKey?: (
		call: GameToolCall,
		context: GameToolExecutionContext,
	) => string | undefined | Promise<string | undefined>;
	operationId?: (call: GameToolCall, context: GameToolExecutionContext) => string;
	projectReceipt?: GameActionModelReceiptProjector;
	maximumModelReceiptCharacters?: number;
}

export interface GameActionToolDetails {
	kind: "terminal" | "reconcile";
	entry: GameActionJournalEntry;
	blockingOperationId?: string;
}

const PROJECTION_FAILURE: JsonObject = { status: "projection_failed" };

function requireRevision(value: number): number {
	if (!Number.isSafeInteger(value) || value < 0) {
		throw new RangeError("expectedRevision must be a non-negative safe integer.");
	}
	return value;
}

function requireConflictKey(value: string | undefined): string | undefined {
	if (value === undefined || value.length === 0) return undefined;
	if (value.length > MAXIMUM_ACTION_CONFLICT_KEY_CHARACTERS) {
		throw new RangeError(`conflictKey cannot exceed ${MAXIMUM_ACTION_CONFLICT_KEY_CHARACTERS} characters.`);
	}
	if ([...value].some((character) => character.charCodeAt(0) < 0x20 || character.charCodeAt(0) === 0x7f)) {
		throw new TypeError("conflictKey cannot contain control characters.");
	}
	return value;
}

function containsControlCharacter(value: string): boolean {
	return [...value].some((character) => character.charCodeAt(0) < 0x20 || character.charCodeAt(0) === 0x7f);
}

function requireOperationId(value: string): string {
	if (value.length < 1 || value.length > 256 || containsControlCharacter(value)) {
		throw new TypeError("operationId must be a bounded non-empty identifier without control characters.");
	}
	return value;
}

function normalizeProjection(value: unknown, maximumCharacters: number): JsonObject | undefined {
	try {
		const serialized = JSON.stringify(value);
		if (serialized.length > maximumCharacters) return undefined;
		const parsed = JSON.parse(serialized) as unknown;
		if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) return undefined;
		return parsed as JsonObject;
	} catch {
		return undefined;
	}
}

function projectModelReceipt(
	projector: GameActionModelReceiptProjector | undefined,
	context: GameActionModelReceiptProjectionContext,
	maximumCharacters: number,
): JsonObject | undefined {
	try {
		return normalizeProjection(projector?.(context) ?? defaultProjection(context.receipt), maximumCharacters);
	} catch {
		return undefined;
	}
}

function defaultProjection(receipt: GameActionReceipt): JsonObject {
	return {
		action: receipt.action,
		status: receipt.status,
		result: receipt.result,
	};
}

function canonicalDetails(dispatch: GameActionDispatchResult): JsonObject {
	const details: GameActionToolDetails = {
		kind: dispatch.kind,
		entry: dispatch.entry,
		...(dispatch.kind === "reconcile" && dispatch.blockingOperationId !== undefined
			? { blockingOperationId: dispatch.blockingOperationId }
			: {}),
	};
	return JSON.parse(JSON.stringify(details)) as JsonObject;
}

function pendingModelResult(dispatch: Extract<GameActionDispatchResult, { kind: "reconcile" }>): JsonObject {
	return {
		status: dispatch.blockingOperationId === undefined ? "reconcile_required" : "conflict_blocked",
	};
}

/**
 * Creates a model-callable tool backed by the durable authoritative action dispatcher.
 * Canonical intent and receipt coordinates are retained in `details`; model-visible
 * `content` receives only the bounded semantic projection.
 */
export function createGameActionTool(options: GameActionToolOptions): GameTool {
	const maximumCharacters = options.maximumModelReceiptCharacters ?? MAXIMUM_MODEL_ACTION_RECEIPT_CHARACTERS;
	if (!Number.isSafeInteger(maximumCharacters) || maximumCharacters < 64 || maximumCharacters > 1_000_000) {
		throw new RangeError("maximumModelReceiptCharacters must be between 64 and 1,000,000.");
	}

	return {
		definition: options.definition,
		async execute(call, context): Promise<GameToolResult> {
			if (call.name !== options.definition.name) {
				throw new Error("Tool call name does not match the authoritative action definition.");
			}
			context.signal.throwIfAborted();
			const expectedRevision = requireRevision(
				typeof options.expectedRevision === "number"
					? options.expectedRevision
					: await options.expectedRevision(call, context),
			);
			const conflictKey = requireConflictKey(await options.conflictKey?.(call, context));
			const identity = {
				session: context.input.session,
				inputId: context.input.id,
				runId: context.runId,
				turn: context.turn,
				toolCallIndex: context.toolCallIndex,
				action: options.definition.name,
			};
			const intent: GameActionIntent = {
				...identity,
				operationId: requireOperationId(options.operationId?.(call, context) ?? createGameActionOperationId(identity)),
				args: structuredClone(call.arguments),
				moment: structuredClone(context.input.moment),
				expectedRevision,
				...(conflictKey === undefined ? {} : { conflictKey }),
			};

			const dispatch = await options.dispatcher.dispatch(intent, context.signal);
			const details = canonicalDetails(dispatch);
			if (dispatch.kind === "reconcile") {
				return {
					content: [{ type: "json", value: pendingModelResult(dispatch) }],
					details,
					isError: true,
				};
			}

			const receipt = dispatch.entry.receipt;
			if (receipt === undefined) {
				throw new Error("A terminal durable action journal entry must contain a receipt.");
			}
			const projected = projectModelReceipt(options.projectReceipt, { intent, receipt }, maximumCharacters);
			if (projected === undefined) {
				return {
					content: [{ type: "json", value: PROJECTION_FAILURE }],
					details,
					isError: true,
				};
			}
			return {
				content: [{ type: "json", value: projected }],
				details,
				isError: receipt.status !== "committed",
			};
		},
	};
}
