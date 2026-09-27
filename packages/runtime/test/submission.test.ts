import { createHash } from "node:crypto";
import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { GameControlCommand, GameInput } from "@opengameagent/agent";
import { canonicalizeJsonValue, type StreamKey } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { GameRuntime, InMemoryEventStore, RunInputConflictError } from "../src/index.js";
import { controlledResponse, TEST_MODEL, textResponse } from "./pi-fakes.js";

const STREAM: StreamKey = { sessionId: "session", actorId: "actor" };

async function runtimeWith(store: InMemoryEventStore, modelStream: StreamFn, runId: string): Promise<GameRuntime> {
	const runtime = new GameRuntime(store, {
		maxActiveActors: 1,
		maxQueuedRuns: 4,
		maxInputBytes: 4_096,
		maxInputDepth: 8,
		newRunId: () => runId,
	});
	await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream });
	return runtime;
}

function fingerprint(value: unknown): string {
	return createHash("sha256").update(canonicalizeJsonValue(value)).digest("hex");
}

describe("durable run submission", () => {
	it.each(["control", "abortRun"] as const)(
		"captures the exact %s target before its asynchronous boundary",
		async (method) => {
			const first = controlledResponse();
			const second = controlledResponse();
			let ordinal = 0;
			const runtime = new GameRuntime(new InMemoryEventStore(), {
				maxActiveActors: 2,
				maxQueuedRuns: 2,
				maxInputBytes: 4_096,
				maxInputDepth: 8,
				newRunId: () => `run-${++ordinal}`,
			});
			const peer = { ...STREAM, actorId: "peer" };
			await runtime.registerActors([
				{ ...STREAM, model: TEST_MODEL, modelStream: first.streamFn },
				{ ...peer, model: TEST_MODEL, modelStream: second.streamFn },
			]);
			const original = await runtime.submit({ ...STREAM, inputId: "original", payload: {} });
			const other = await runtime.submit({ ...peer, inputId: "other", payload: {} });
			await Promise.all([first.started, second.started]);
			const expected = runtime.readActiveCoordinate(STREAM);
			if (!expected) throw new Error("Expected an active original run.");
			const command: GameControlCommand = { type: "abort" };
			try {
				const requested = method === "control" ? runtime.control(expected, command) : runtime.abortRun(expected);
				Object.assign(expected, runtime.readActiveCoordinate(peer));
				Object.assign(command, { type: "follow-up", text: "changed" });
				expect(await requested).toMatchObject({ accepted: true, coordinate: { ...STREAM, runId: original.runId } });
				expect(runtime.readActiveCoordinate(peer)?.runId).toBe(other.runId);
			} finally {
				first.finish();
				second.finish();
				await Promise.all([original.completion, other.completion]);
			}
			await expect(original.completion).resolves.toMatchObject({ status: "aborted" });
			await expect(other.completion).resolves.toMatchObject({ status: "completed" });
		},
	);

	it("captures the submitted identity, payload and authority before waiting for admission", async () => {
		const store = new InMemoryEventStore();
		const runtime = await runtimeWith(store, () => textResponse("done"), "captured-run");
		const input = {
			...STREAM,
			inputId: "original-input",
			payload: { nested: { instruction: "original" } },
			admissionAuthority: { scope: "original-scope", generation: "1" },
		};
		const submitted = runtime.submit(input);
		input.sessionId = "changed-session";
		input.actorId = "changed-actor";
		input.inputId = "changed-input";
		input.payload.nested.instruction = "changed";
		input.admissionAuthority.scope = "changed-scope";
		input.admissionAuthority.generation = "2";

		const run = await submitted;
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });
		expect(run.initialCoordinate).toMatchObject(STREAM);
		expect(await store.readRun(STREAM, "original-input")).toMatchObject({
			admissionAuthority: { scope: "original-scope", generation: "1" },
		});
		const replay = await runtime.submit({
			...STREAM,
			inputId: "original-input",
			payload: { nested: { instruction: "original" } },
			admissionAuthority: { scope: "original-scope", generation: "1" },
		});
		expect(replay.runId).toBe(run.runId);
	});

	it.each(["start", "submit"] as const)("reads each %s input field once before choosing its actor", async (method) => {
		const store = new InMemoryEventStore();
		const runtime = await runtimeWith(store, () => textResponse("done"), "captured-run");
		const values: GameInput = {
			...STREAM,
			inputId: "captured-input",
			payload: { instruction: "original" },
			admissionAuthority: { scope: "captured-scope", generation: "1" },
		};
		const input = Object.create(null) as GameInput;
		for (const [key, value] of Object.entries(values)) {
			let read = false;
			Object.defineProperty(input, key, {
				get: () => {
					if (read) throw new Error(`The ${key} field was read again.`);
					read = true;
					return value;
				},
			});
		}

		const run = await runtime[method](input);
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });
		expect(await store.readRun(STREAM, "captured-input")).toMatchObject({
			admissionAuthority: { scope: "captured-scope", generation: "1" },
		});
	});

	it("returns the canonical cursor immediately before the admitted run", async () => {
		const store = new InMemoryEventStore();
		const history = await store.append(
			STREAM,
			"empty",
			{ ...STREAM, runId: "earlier-run", turn: "0" },
			{
				type: "turn.started",
			},
		);
		const runtime = await runtimeWith(store, () => textResponse("done"), "run-one");

		const submitted = await runtime.submit({ ...STREAM, inputId: "input", payload: {} });

		expect(submitted).toMatchObject({ runId: "run-one", eventCursor: history.revision });
		await submitted.completion;
	});

	it("coalesces concurrent requests whose canonical payload bytes match", async () => {
		const controlled = controlledResponse();
		let providerCalls = 0;
		const runtime = await runtimeWith(
			new InMemoryEventStore(),
			(...arguments_) => {
				providerCalls += 1;
				return controlled.streamFn(...arguments_);
			},
			"run-one",
		);

		const [first, second] = await Promise.all([
			runtime.submit({ ...STREAM, inputId: "input", payload: { z: 1, nested: { b: true, a: "value" } } }),
			runtime.submit({ ...STREAM, inputId: "input", payload: { nested: { a: "value", b: true }, z: 1 } }),
		]);

		expect(first).toMatchObject({ eventCursor: "0" });
		expect(second).toMatchObject({ runId: first.runId, eventCursor: "0" });
		await controlled.started;
		expect(providerCalls).toBe(1);
		controlled.finish();
		await expect(Promise.all([first.completion, second.completion])).resolves.toEqual([
			{ runId: "run-one", status: "completed", finalOutput: "done" },
			{ runId: "run-one", status: "completed", finalOutput: "done" },
		]);
	});

	it("rejects reuse of an input identifier with different content without rerunning Pi", async () => {
		const controlled = controlledResponse();
		let providerCalls = 0;
		const runtime = await runtimeWith(
			new InMemoryEventStore(),
			(...arguments_) => {
				providerCalls += 1;
				return controlled.streamFn(...arguments_);
			},
			"run-one",
		);
		const first = await runtime.submit({ ...STREAM, inputId: "input", payload: { value: 1 } });

		await expect(runtime.submit({ ...STREAM, inputId: "input", payload: { value: 2 } })).rejects.toThrow(
			RunInputConflictError,
		);
		await controlled.started;
		expect(providerCalls).toBe(1);
		controlled.finish();
		await first.completion;
	});

	it("returns a completed durable result after runtime restart without calling Pi", async () => {
		const store = new InMemoryEventStore();
		await store.append(STREAM, "empty", { ...STREAM, runId: "earlier-run", turn: "0" }, { type: "turn.started" });
		const firstRuntime = await runtimeWith(store, () => textResponse("done"), "durable-run");
		const first = await firstRuntime.submit({ ...STREAM, inputId: "input", payload: { value: true } });
		expect(first).toMatchObject({ eventCursor: "1" });
		await expect(first.completion).resolves.toEqual({ runId: "durable-run", status: "completed", finalOutput: "done" });

		let providerCalls = 0;
		const restarted = await runtimeWith(
			store,
			() => {
				providerCalls += 1;
				throw new Error("Pi must not be called for a settled submission.");
			},
			"unused-run",
		);
		const recovered = await restarted.submit({ ...STREAM, inputId: "input", payload: { value: true } });

		expect(recovered).toMatchObject({ runId: "durable-run", eventCursor: "1" });
		await expect(recovered.completion).resolves.toEqual({
			runId: "durable-run",
			status: "completed",
			finalOutput: "done",
		});
		expect(providerCalls).toBe(0);
	});

	it("fails an orphaned admitted run after restart instead of replaying its side effects", async () => {
		const store = new InMemoryEventStore();
		const payload = { operation: "game-write" };
		const coordinate = { ...STREAM, runId: "orphaned-run", turn: "0" };
		await store.append(STREAM, "empty", coordinate, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: fingerprint({ payload, admissionAuthority: null }),
		});
		let providerCalls = 0;
		const restarted = await runtimeWith(
			store,
			() => {
				providerCalls += 1;
				throw new Error("Pi must not replay an orphaned run.");
			},
			"unused-run",
		);

		const recovered = await restarted.submit({ ...STREAM, inputId: "input", payload });
		await expect(recovered.completion).resolves.toEqual({ runId: "orphaned-run", status: "failed" });
		expect(providerCalls).toBe(0);
		expect(await store.readRun(STREAM, "input")).toMatchObject({ status: "failed" });
	});
});
