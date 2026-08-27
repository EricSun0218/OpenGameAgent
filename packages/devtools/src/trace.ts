import { appendFile, mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import type { GameActionDispatchObservation, GameActionObserver } from "@opengameagent/actions";
import type {
	GameAgentEvent,
	GameAudience,
	GameInput,
	GameSessionKey,
	GameUsage,
	JsonObject,
} from "@opengameagent/protocol";
import type { GameRuntimeObserver, GameRuntimeStage, GameRuntimeStageObservation } from "@opengameagent/runtime";

export interface GameTraceRecordBase {
	schemaVersion: 1;
	sequence: number;
	observedAt: number;
	session: GameSessionKey;
	inputId: string;
	runId: string;
	turn: number;
}

export interface GameTraceStageRecord extends GameTraceRecordBase {
	kind: "stage";
	stage: GameRuntimeStage;
	name?: string;
	startedAt: number;
	durationMilliseconds: number;
	outcome: "ok" | "error" | "cancelled";
	errorCategory?: string;
}

export interface GameTraceEventRecord extends GameTraceRecordBase {
	kind: "event";
	eventType: GameAgentEvent["type"];
	eventSequence: number;
	eventId: string;
	audience: GameAudience;
	timestamp: number;
	attributes: JsonObject;
}

export interface GameTraceActionRecord extends GameTraceRecordBase {
	kind: "action";
	operationId: string;
	action: string;
	startedAt: number;
	durationMilliseconds: number;
	frameworkMilliseconds: number;
	hostMilliseconds: number;
	disposition: GameActionDispatchObservation["disposition"];
	terminalStatus?: GameActionDispatchObservation["terminalStatus"];
	blockingOperationId?: string;
	errorCategory?: string;
}

export type GameTraceRecord = GameTraceStageRecord | GameTraceEventRecord | GameTraceActionRecord;

export interface GameTraceRecording {
	schemaVersion: 1;
	createdAt: number;
	records: readonly GameTraceRecord[];
	droppedRecords: number;
}

export interface GameTraceSink {
	write(record: GameTraceRecord): void;
	flush(): Promise<void>;
	close(): Promise<void>;
}

export interface InMemoryGameTraceSinkOptions {
	maximumRecords?: number;
	maximumBytes?: number;
	overflow?: "drop-oldest" | "drop-newest";
}

export class InMemoryGameTraceSink implements GameTraceSink {
	private readonly maximumRecords: number;
	private readonly maximumBytes: number;
	private readonly overflow: "drop-oldest" | "drop-newest";
	private readonly entries: { record: GameTraceRecord; bytes: number }[] = [];
	private bytes = 0;
	private dropped = 0;
	private closed = false;
	private readonly createdAt = Date.now();

	constructor(options: InMemoryGameTraceSinkOptions = {}) {
		this.maximumRecords = boundedInteger(options.maximumRecords ?? 100_000, "maximumRecords", 1, 1_000_000);
		this.maximumBytes = boundedInteger(options.maximumBytes ?? 64 * 1024 * 1024, "maximumBytes", 1024, 1024 ** 3);
		this.overflow = options.overflow ?? "drop-oldest";
	}

	write(record: GameTraceRecord): void {
		if (this.closed) throw new Error("Trace sink is closed.");
		const copy = structuredClone(record);
		const bytes = Buffer.byteLength(JSON.stringify(copy), "utf8");
		if (bytes > this.maximumBytes) {
			this.dropped += 1;
			return;
		}
		if (
			this.overflow === "drop-newest" &&
			(this.entries.length >= this.maximumRecords || this.bytes + bytes > this.maximumBytes)
		) {
			this.dropped += 1;
			return;
		}
		while (this.entries.length >= this.maximumRecords || this.bytes + bytes > this.maximumBytes) {
			const removed = this.entries.shift();
			if (!removed) break;
			this.bytes -= removed.bytes;
			this.dropped += 1;
		}
		this.entries.push({ record: copy, bytes });
		this.bytes += bytes;
	}

	recording(): GameTraceRecording {
		return {
			schemaVersion: 1,
			createdAt: this.createdAt,
			records: this.entries.map((entry) => structuredClone(entry.record)),
			droppedRecords: this.dropped,
		};
	}

	async flush(): Promise<void> {}

	async close(): Promise<void> {
		this.closed = true;
	}
}

export interface JsonLinesGameTraceSinkOptions {
	mode?: "create" | "append";
	maximumQueuedBytes?: number;
	maximumRecordBytes?: number;
}

export class JsonLinesGameTraceSink implements GameTraceSink {
	private readonly maximumQueuedBytes: number;
	private readonly maximumRecordBytes: number;
	private queuedBytes = 0;
	private dropped = 0;
	private closed = false;
	private failure = false;
	private pending: Promise<void>;

	constructor(
		private readonly path: string,
		options: JsonLinesGameTraceSinkOptions = {},
	) {
		if (path.trim().length === 0) throw new TypeError("path is required.");
		this.maximumQueuedBytes = boundedInteger(
			options.maximumQueuedBytes ?? 8 * 1024 * 1024,
			"maximumQueuedBytes",
			1024,
			256 * 1024 * 1024,
		);
		this.maximumRecordBytes = boundedInteger(
			options.maximumRecordBytes ?? 256 * 1024,
			"maximumRecordBytes",
			1024,
			4 * 1024 * 1024,
		);
		this.pending = mkdir(dirname(path), { recursive: true })
			.then(async () => {
				if ((options.mode ?? "create") === "create") await writeFile(path, "", { encoding: "utf8", flag: "wx" });
			})
			.catch(() => {
				this.failure = true;
			});
	}

	get droppedRecords(): number {
		return this.dropped;
	}

	write(record: GameTraceRecord): void {
		if (this.closed) throw new Error("Trace sink is closed.");
		if (this.failure) {
			this.dropped += 1;
			return;
		}
		const line = `${JSON.stringify(record)}\n`;
		const bytes = Buffer.byteLength(line, "utf8");
		if (bytes > this.maximumRecordBytes || this.queuedBytes + bytes > this.maximumQueuedBytes) {
			this.dropped += 1;
			return;
		}
		this.queuedBytes += bytes;
		this.pending = this.pending
			.then(async () => {
				if (this.failure) {
					this.dropped += 1;
					return;
				}
				await appendFile(this.path, line, { encoding: "utf8", flag: "a" });
			})
			.catch(() => {
				this.failure = true;
			})
			.finally(() => {
				this.queuedBytes -= bytes;
			});
	}

	async flush(): Promise<void> {
		await this.pending;
		if (this.failure) throw new Error("Trace storage failed.");
	}

	async close(): Promise<void> {
		if (this.closed) return;
		this.closed = true;
		await this.flush();
	}
}

export interface GameRuntimeTraceObserverOptions {
	includeVisibleText?: boolean;
	maximumTextCharacters?: number;
	initialSequence?: number;
	clock?: () => number;
}

export class GameRuntimeTraceObserver implements GameRuntimeObserver, GameActionObserver {
	private sequence: number;
	private readonly includeVisibleText: boolean;
	private readonly maximumTextCharacters: number;
	private readonly clock: () => number;

	constructor(
		private readonly sink: GameTraceSink,
		options: GameRuntimeTraceObserverOptions = {},
	) {
		this.includeVisibleText = options.includeVisibleText ?? false;
		this.maximumTextCharacters = boundedInteger(
			options.maximumTextCharacters ?? 16_384,
			"maximumTextCharacters",
			0,
			1_000_000,
		);
		this.sequence = boundedInteger(options.initialSequence ?? 0, "initialSequence", 0, Number.MAX_SAFE_INTEGER - 1);
		this.clock = options.clock ?? Date.now;
	}

	observeStage(observation: GameRuntimeStageObservation): void {
		this.sink.write({
			kind: "stage",
			...structuredClone(observation),
			sequence: ++this.sequence,
			observedAt: this.clock(),
		});
	}

	observeEvent(input: GameInput, event: GameAgentEvent): void {
		this.sink.write({
			schemaVersion: 1,
			kind: "event",
			sequence: ++this.sequence,
			observedAt: this.clock(),
			session: structuredClone(input.session),
			inputId: input.id,
			runId: event.runId,
			turn: event.turn,
			eventType: event.type,
			eventSequence: event.sequence,
			eventId: event.eventId,
			audience: structuredClone(event.audience),
			timestamp: event.timestamp,
			attributes: projectEvent(event, this.includeVisibleText, this.maximumTextCharacters),
		});
	}

	observeAction(observation: GameActionDispatchObservation): void {
		this.sink.write({
			kind: "action",
			...structuredClone(observation),
			sequence: ++this.sequence,
			observedAt: this.clock(),
		});
	}
}

export interface GameTraceReadOptions {
	maximumBytes?: number;
	maximumRecords?: number;
	maximumRecordBytes?: number;
}

export async function readGameTraceRecording(
	path: string,
	options: GameTraceReadOptions = {},
): Promise<GameTraceRecording> {
	const maximumBytes = boundedInteger(options.maximumBytes ?? 64 * 1024 * 1024, "maximumBytes", 1, 1024 ** 3);
	const maximumRecords = boundedInteger(options.maximumRecords ?? 100_000, "maximumRecords", 1, 1_000_000);
	const maximumRecordBytes = boundedInteger(
		options.maximumRecordBytes ?? 256 * 1024,
		"maximumRecordBytes",
		128,
		4 * 1024 * 1024,
	);
	const info = await stat(path);
	if (!info.isFile() || info.size > maximumBytes)
		throw new Error("Trace recording is invalid or exceeds the configured limit.");
	const text = await readFile(path, "utf8");
	const lines = text.split("\n").filter((line) => line.length > 0);
	if (lines.length > maximumRecords) throw new Error("Trace recording exceeds the configured record limit.");
	const records = lines.map((line) => {
		if (Buffer.byteLength(line, "utf8") > maximumRecordBytes)
			throw new Error("Trace record exceeds the configured limit.");
		let parsed: unknown;
		try {
			parsed = JSON.parse(line);
		} catch {
			throw new Error("Trace recording is corrupt.");
		}
		if (!isTraceRecord(parsed)) throw new Error("Trace recording contains an unsupported record.");
		return parsed;
	});
	for (let index = 1; index < records.length; index += 1) {
		if ((records[index]?.sequence ?? 0) <= (records[index - 1]?.sequence ?? 0)) {
			throw new Error("Trace recording sequence is not strictly increasing.");
		}
	}
	return { schemaVersion: 1, createdAt: info.birthtimeMs || info.mtimeMs, records, droppedRecords: 0 };
}

function projectEvent(event: GameAgentEvent, includeVisibleText: boolean, maximumTextCharacters: number): JsonObject {
	switch (event.type) {
		case "run.started":
			return {
				inputId: event.inputId,
				profileId: event.model.profileId,
				provider: event.model.provider,
				model: event.model.model,
				api: event.model.api,
				reasoning: event.model.reasoning,
				inputKinds: [...event.model.input],
				contextWindow: event.model.contextWindow,
				maximumOutputTokens: event.model.maximumOutputTokens,
			};
		case "run.completed":
			return event.usage ? { usage: usageJson(event.usage) } : {};
		case "run.failed":
			return { errorCategory: event.category };
		case "run.aborted":
		case "turn.started":
		case "turn.completed":
			return {};
		case "message.delta":
			return messageAttributes(event.text, event.audience, includeVisibleText, maximumTextCharacters);
		case "message.completed":
			return {
				...messageAttributes(event.text, event.audience, includeVisibleText, maximumTextCharacters),
				...(event.usage ? { usage: usageJson(event.usage) } : {}),
				...(event.provider === undefined ? {} : { provider: event.provider }),
				...(event.model === undefined ? {} : { model: event.model }),
				...(event.responseId === undefined ? {} : { responseId: event.responseId }),
			};
		case "tool.started":
			return { callId: event.call.id, tool: event.call.name };
		case "tool.progress":
			return { callId: event.callId };
		case "tool.completed":
			return {
				callId: event.callId,
				isError: event.result.isError ?? false,
				contentKinds: event.result.content.map((content) => content.type),
			};
	}
}

function messageAttributes(
	text: string,
	audience: GameAudience,
	includeVisibleText: boolean,
	maximumTextCharacters: number,
): JsonObject {
	const attributes: JsonObject = { characters: text.length };
	if (includeVisibleText && audience.visibility !== "internal" && text.length <= maximumTextCharacters) {
		attributes["text"] = text;
	}
	return attributes;
}

function usageJson(usage: GameUsage): JsonObject {
	return {
		input: usage.input,
		output: usage.output,
		cacheRead: usage.cacheRead,
		cacheWrite: usage.cacheWrite,
		...(usage.reasoning === undefined ? {} : { reasoning: usage.reasoning }),
		totalTokens: usage.totalTokens,
		...(usage.cost === undefined ? {} : { cost: { ...usage.cost } }),
	};
}

function isTraceRecord(value: unknown): value is GameTraceRecord {
	if (
		!isObject(value) ||
		value["schemaVersion"] !== 1 ||
		(value["kind"] !== "stage" && value["kind"] !== "event" && value["kind"] !== "action")
	)
		return false;
	if (!integer(value["sequence"]) || !finite(value["observedAt"]) || !isSession(value["session"])) return false;
	if (!text(value["inputId"]) || !text(value["runId"]) || !integer(value["turn"]) || value["turn"] < 0) return false;
	if (value["kind"] === "stage") {
		return (
			stageNames.has(value["stage"] as GameRuntimeStage) &&
			finite(value["startedAt"]) &&
			finite(value["durationMilliseconds"]) &&
			value["durationMilliseconds"] >= 0 &&
			(value["outcome"] === "ok" || value["outcome"] === "error" || value["outcome"] === "cancelled") &&
			(value["name"] === undefined || text(value["name"])) &&
			(value["errorCategory"] === undefined || text(value["errorCategory"]))
		);
	}
	if (value["kind"] === "action") {
		return (
			text(value["operationId"]) &&
			text(value["action"]) &&
			finite(value["startedAt"]) &&
			finite(value["durationMilliseconds"]) &&
			value["durationMilliseconds"] >= 0 &&
			finite(value["frameworkMilliseconds"]) &&
			value["frameworkMilliseconds"] >= 0 &&
			finite(value["hostMilliseconds"]) &&
			value["hostMilliseconds"] >= 0 &&
			actionDispositions.has(value["disposition"] as GameActionDispatchObservation["disposition"]) &&
			(value["terminalStatus"] === undefined ||
				actionTerminalStatuses.has(
					value["terminalStatus"] as NonNullable<GameActionDispatchObservation["terminalStatus"]>,
				)) &&
			(value["blockingOperationId"] === undefined || text(value["blockingOperationId"])) &&
			(value["errorCategory"] === undefined || text(value["errorCategory"]))
		);
	}
	return (
		eventNames.has(value["eventType"] as GameAgentEvent["type"]) &&
		integer(value["eventSequence"]) &&
		value["eventSequence"] >= 0 &&
		text(value["eventId"]) &&
		isAudience(value["audience"]) &&
		finite(value["timestamp"]) &&
		isObject(value["attributes"])
	);
}

const stageNames = new Set<GameRuntimeStage>([
	"run",
	"queue",
	"prepare-turn",
	"context",
	"tool-provider",
	"tool-catalog",
	"post-tool-context",
	"model-profile",
	"event-store",
	"usage-ledger",
	"tool-execution",
]);

const eventNames = new Set<GameAgentEvent["type"]>([
	"run.started",
	"run.completed",
	"run.failed",
	"run.aborted",
	"turn.started",
	"turn.completed",
	"message.delta",
	"message.completed",
	"tool.started",
	"tool.progress",
	"tool.completed",
]);

const actionDispositions = new Set<GameActionDispatchObservation["disposition"]>([
	"executed",
	"duplicate-prevented",
	"reconcile-required",
	"conflict-blocked",
	"uncertain",
	"reconciled",
	"failed-before-dispatch",
]);

const actionTerminalStatuses = new Set<NonNullable<GameActionDispatchObservation["terminalStatus"]>>([
	"committed",
	"rejected",
	"failed",
]);

function isSession(value: unknown): value is GameSessionKey {
	return (
		isObject(value) &&
		text(value["worldId"]) &&
		text(value["saveId"]) &&
		text(value["timelineId"]) &&
		integer(value["generation"]) &&
		text(value["ownerId"]) &&
		text(value["sessionId"]) &&
		text(value["actorId"])
	);
}

function isAudience(value: unknown): value is GameAudience {
	if (!isObject(value) || !text(value["visibility"])) return false;
	return (
		value["visibility"] === "internal" ||
		value["visibility"] === "owner" ||
		value["visibility"] === "public" ||
		(value["visibility"] === "recipient" && text(value["recipientId"]))
	);
}

function isObject(value: unknown): value is Record<string, unknown> {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

function finite(value: unknown): value is number {
	return typeof value === "number" && Number.isFinite(value);
}

function integer(value: unknown): value is number {
	return finite(value) && Number.isInteger(value);
}

function text(value: unknown): value is string {
	return typeof value === "string" && value.length > 0 && value.length <= 1024;
}

function boundedInteger(value: number, name: string, minimum: number, maximum: number): number {
	if (!Number.isInteger(value) || value < minimum || value > maximum) {
		throw new RangeError(`${name} must be an integer from ${minimum} through ${maximum}.`);
	}
	return value;
}
