import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameAgentEvent, GameInput } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import {
	GameRuntimeTraceObserver,
	InMemoryGameTraceSink,
	JsonLinesGameTraceSink,
	readGameTraceRecording,
} from "./trace.js";

const directories: string[] = [];

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

const input: GameInput = {
	id: "input-1",
	type: "npc.chat",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 12 },
	content: [{ type: "text", text: "private-input" }],
};

type GameAgentEventBody<T extends GameAgentEvent = GameAgentEvent> = T extends GameAgentEvent
	? Omit<T, "sequence" | "eventId" | "runId" | "turn" | "timestamp">
	: never;

const event = (value: GameAgentEventBody): GameAgentEvent =>
	({
		...value,
		sequence: 1,
		eventId: "event-1",
		runId: "run-1",
		turn: 1,
		timestamp: 100,
	}) as GameAgentEvent;

describe("GameRuntimeTraceObserver", () => {
	it("omits private text, tool payloads, failure messages, and reasoning by default", () => {
		const sink = new InMemoryGameTraceSink();
		const observer = new GameRuntimeTraceObserver(sink, { includeVisibleText: true });
		observer.observeEvent(
			input,
			event({ type: "message.completed", audience: { visibility: "internal" }, text: "hidden-reasoning-token" }),
		);
		observer.observeEvent(
			input,
			event({
				type: "tool.started",
				audience: { visibility: "owner" },
				call: { id: "call-1", name: "move", arguments: { credential: "secret-tool-argument" } },
			}),
		);
		observer.observeEvent(
			input,
			event({
				type: "run.failed",
				audience: { visibility: "owner" },
				category: "provider",
				message: "secret-provider-response",
			}),
		);
		observer.observeEvent(
			input,
			event({ type: "message.completed", audience: { visibility: "owner" }, text: "visible answer" }),
		);
		observer.observeAction({
			schemaVersion: 1,
			session: input.session,
			inputId: input.id,
			runId: "run-1",
			turn: 1,
			operationId: "operation-1",
			action: "move",
			startedAt: 100,
			durationMilliseconds: 20,
			frameworkMilliseconds: 5,
			hostMilliseconds: 15,
			disposition: "executed",
			terminalStatus: "committed",
		});

		const serialized = JSON.stringify(sink.recording());
		expect(serialized).not.toContain("private-input");
		expect(serialized).not.toContain("hidden-reasoning-token");
		expect(serialized).not.toContain("secret-tool-argument");
		expect(serialized).not.toContain("secret-provider-response");
		expect(serialized).toContain("visible answer");
		expect(serialized).toContain('"errorCategory":"provider"');
		expect(serialized).toContain('"operationId":"operation-1"');
	});

	it("bounds memory and records dropped observations", () => {
		const sink = new InMemoryGameTraceSink({ maximumRecords: 2, maximumBytes: 4096 });
		const observer = new GameRuntimeTraceObserver(sink);
		for (let index = 0; index < 4; index += 1) {
			observer.observeEvent(input, {
				...event({ type: "turn.started", audience: { visibility: "owner" } }),
				eventId: `event-${index}`,
			});
		}
		expect(sink.recording().records).toHaveLength(2);
		expect(sink.recording().droppedRecords).toBe(2);
	});
});

describe("JSON Lines trace storage", () => {
	it("round-trips bounded records and rejects corrupt input", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-trace-"));
		directories.push(directory);
		const path = join(directory, "trace.jsonl");
		const sink = new JsonLinesGameTraceSink(path);
		new GameRuntimeTraceObserver(sink).observeEvent(
			input,
			event({ type: "turn.started", audience: { visibility: "owner" } }),
		);
		await sink.close();
		const recording = await readGameTraceRecording(path);
		expect(recording.records).toHaveLength(1);
		expect(recording.records[0]).toMatchObject({ kind: "event", eventType: "turn.started" });

		const corrupt = join(directory, "corrupt.jsonl");
		await writeFile(corrupt, "{not-json}\n", "utf8");
		await expect(readGameTraceRecording(corrupt)).rejects.toThrow(/corrupt/);
	});

	it("supports explicit sequence continuation when appending after restart", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-trace-append-"));
		directories.push(directory);
		const path = join(directory, "trace.jsonl");
		const first = new JsonLinesGameTraceSink(path);
		new GameRuntimeTraceObserver(first).observeEvent(
			input,
			event({ type: "turn.started", audience: { visibility: "owner" } }),
		);
		await first.close();

		const second = new JsonLinesGameTraceSink(path, { mode: "append" });
		new GameRuntimeTraceObserver(second, { initialSequence: 1 }).observeEvent(
			input,
			event({ type: "turn.completed", audience: { visibility: "owner" } }),
		);
		await second.close();

		const loaded = await readGameTraceRecording(path);
		expect(loaded.records.map((record) => record.sequence)).toEqual([1, 2]);
	});

	it("fails closed when create mode targets an existing trace", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-trace-existing-"));
		directories.push(directory);
		const path = join(directory, "trace.jsonl");
		await writeFile(path, "existing\n", "utf8");
		const sink = new JsonLinesGameTraceSink(path);
		new GameRuntimeTraceObserver(sink).observeEvent(
			input,
			event({ type: "turn.started", audience: { visibility: "owner" } }),
		);
		await expect(sink.close()).rejects.toThrow("Trace storage failed.");
		await expect(readGameTraceRecording(path)).rejects.toThrow();
	});
});
