import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { GameRunHandle } from "@opengameagent/agent";
import type { CanonicalEvent, StreamKey } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { GameRuntime, InMemoryEventStore, RuntimeQueueFullError } from "../src/index.js";
import { FailingRunCompletionStore } from "./event-store-fakes.js";
import { controlledResponse, TEST_MODEL, textResponse } from "./pi-fakes.js";

const SESSION = "session";

function stream(actorId: string): StreamKey {
	return { sessionId: SESSION, actorId };
}

async function register(runtime: GameRuntime, actorId: string, modelStream: StreamFn): Promise<void> {
	await runtime.registerActor({
		...stream(actorId),
		model: TEST_MODEL,
		modelStream,
		now: () => 1,
	});
}

async function readAll(runtime: GameRuntime, actorId: string): Promise<CanonicalEvent[]> {
	const events: CanonicalEvent[] = [];
	let afterRevision = "0";
	while (true) {
		const page = await runtime.readEvents(stream(actorId), { afterRevision, limit: 100 });
		events.push(...page.events);
		if (!page.hasMore) return events;
		afterRevision = page.cursor;
	}
}

describe("GameRuntime scheduler", () => {
	it.each(["newRunId", "metricsNow"] as const)(
		"rechecks queue capacity after %s reenters admission",
		async (callback) => {
			let runtime: GameRuntime;
			let reenter = true;
			let ordinal = 0;
			let tick = 0;
			let nested: GameRunHandle | undefined;
			let outer: GameRunHandle | undefined;
			const admitNested = () => {
				if (!reenter) return;
				reenter = false;
				nested = runtime.start({ ...stream("actor"), inputId: "nested", payload: {} });
			};
			runtime = new GameRuntime(new InMemoryEventStore(), {
				maxActiveActors: 1,
				maxQueuedRuns: 1,
				maxInputBytes: 4_096,
				maxInputDepth: 8,
				newRunId: () => {
					const id = `run-${++ordinal}`;
					if (callback === "newRunId") admitNested();
					return id;
				},
				metricsNow: () => {
					if (callback === "metricsNow") admitNested();
					return tick++;
				},
			});
			await register(runtime, "actor", () => textResponse("done"));
			try {
				expect(() => {
					outer = runtime.start({ ...stream("actor"), inputId: "outer", payload: {} });
				}).toThrow(RuntimeQueueFullError);
			} finally {
				await Promise.all([nested?.completion, outer?.completion]);
			}
			await expect(nested?.completion).resolves.toMatchObject({ status: "completed" });
			expect((await readAll(runtime, "actor")).filter((event) => event.data.type === "run.queued")).toHaveLength(1);
		},
	);

	it("enforces the configured waiting-run capacity without counting an active run", async () => {
		const first = controlledResponse();
		let nextRun = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			newRunId: () => {
				nextRun += 1;
				return `run-${nextRun}`;
			},
		});
		await register(runtime, "actor", first.streamFn);

		const active = runtime.start({ ...stream("actor"), inputId: "one", payload: {} });
		await first.started;
		const queued = runtime.start({ ...stream("actor"), inputId: "two", payload: {} });
		expect(() => runtime.start({ ...stream("actor"), inputId: "three", payload: {} })).toThrow(RuntimeQueueFullError);

		await expect(runtime.abortRun(queued.initialCoordinate)).resolves.toMatchObject({
			accepted: true,
		});
		first.finish();
		await expect(Promise.all([active.completion, queued.completion])).resolves.toEqual([
			{ runId: active.runId, status: "completed", finalOutput: "done" },
			{ runId: queued.runId, status: "aborted" },
		]);
	});

	it("removes a queued run and releases capacity even when abort terminal persistence fails", async () => {
		const first = controlledResponse();
		let providerCalls = 0;
		const provider: StreamFn = (model, context, options) => {
			providerCalls += 1;
			return providerCalls === 1 ? first.streamFn(model, context, options) : textResponse("replacement");
		};
		const events = new FailingRunCompletionStore("terminal persistence failed");
		let nextRun = 0;
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			newRunId: () => `run-${++nextRun}`,
		});
		await register(runtime, "actor", provider);
		const active = runtime.start({ ...stream("actor"), inputId: "active", payload: {} });
		await first.started;
		const queued = runtime.start({ ...stream("actor"), inputId: "queued", payload: {} });
		events.blockedRunId = queued.runId;

		await expect(runtime.abortRun(queued.initialCoordinate)).rejects.toThrow("terminal persistence failed");
		await expect(queued.completion).resolves.toEqual({ runId: queued.runId, status: "failed" });
		const replacement = runtime.start({ ...stream("actor"), inputId: "replacement", payload: {} });
		events.blockedRunId = undefined;
		first.finish();

		await expect(Promise.all([active.completion, replacement.completion])).resolves.toEqual([
			{ runId: active.runId, status: "completed", finalOutput: "done" },
			{ runId: replacement.runId, status: "completed", finalOutput: "replacement" },
		]);
		expect(providerCalls).toBe(2);
		const queuedEvents = (await readAll(runtime, "actor")).filter((event) => event.coordinate.runId === queued.runId);
		expect(queuedEvents.map((event) => event.data.type)).toEqual(["run.queued", "control.accepted", "run.metrics"]);
	});

	it("round-robins ready actor lanes while preserving FIFO within each actor", async () => {
		const firstA = controlledResponse();
		const starts: string[] = [];
		let aCalls = 0;
		const aStream: StreamFn = (model, context, options) => {
			aCalls += 1;
			starts.push(`a${aCalls}`);
			return aCalls === 1 ? firstA.streamFn(model, context, options) : textResponse("second A");
		};
		const bStream: StreamFn = () => {
			starts.push("b1");
			return textResponse("B");
		};
		let nextRun = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 4,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			newRunId: () => {
				nextRun += 1;
				return `run-${nextRun}`;
			},
		});
		await register(runtime, "a", aStream);
		await register(runtime, "b", bStream);

		const a1 = runtime.start({ ...stream("a"), inputId: "a1", payload: {} });
		await firstA.started;
		const a2 = runtime.start({ ...stream("a"), inputId: "a2", payload: {} });
		const b1 = runtime.start({ ...stream("b"), inputId: "b1", payload: {} });
		await expect(runtime.control(a2.initialCoordinate, { type: "steer", text: "not yet" })).resolves.toMatchObject({
			accepted: false,
			reason: "run-not-active",
		});
		expect(starts).toEqual(["a1"]);

		firstA.finish("first A");
		await expect(Promise.all([a1.completion, a2.completion, b1.completion])).resolves.toEqual([
			{ runId: a1.runId, status: "completed", finalOutput: "first A" },
			{ runId: a2.runId, status: "completed", finalOutput: "second A" },
			{ runId: b1.runId, status: "completed", finalOutput: "B" },
		]);
		expect(starts).toEqual(["a1", "b1", "a2"]);
	});

	it("settles an exactly addressed queued abort without starting Pi", async () => {
		const first = controlledResponse();
		let providerCalls = 0;
		const provider: StreamFn = (model, context, options) => {
			providerCalls += 1;
			return providerCalls === 1 ? first.streamFn(model, context, options) : textResponse("unexpected");
		};
		let nextRun = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 4,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			newRunId: () => {
				nextRun += 1;
				return `run-${nextRun}`;
			},
		});
		await register(runtime, "actor", provider);
		const active = runtime.start({ ...stream("actor"), inputId: "active", payload: {} });
		await first.started;
		const queued = runtime.start({ ...stream("actor"), inputId: "queued", payload: {} });

		await expect(runtime.control({ ...queued.initialCoordinate, turn: "1" }, { type: "abort" })).resolves.toMatchObject(
			{
				accepted: false,
				reason: "coordinate-mismatch",
			},
		);
		await expect(runtime.control(queued.initialCoordinate, { type: "abort" })).resolves.toEqual({
			accepted: true,
			coordinate: queued.initialCoordinate,
		});
		await expect(queued.completion).resolves.toEqual({ runId: queued.runId, status: "aborted" });
		expect(providerCalls).toBe(1);

		const queuedEvents = (await readAll(runtime, "actor")).filter((event) => event.coordinate.runId === queued.runId);
		expect(queuedEvents.map((event) => event.data.type)).toEqual([
			"run.queued",
			"control.accepted",
			"run.metrics",
			"run.completed",
		]);
		first.finish();
		await expect(active.completion).resolves.toMatchObject({ status: "completed" });
	});

	it("preserves the surrounding actor FIFO when a middle queued run is aborted", async () => {
		const first = controlledResponse();
		let providerCalls = 0;
		const provider: StreamFn = (model, context, options) => {
			providerCalls += 1;
			return providerCalls === 1 ? first.streamFn(model, context, options) : textResponse(`run ${providerCalls}`);
		};
		let nextRun = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 3,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			newRunId: () => {
				nextRun += 1;
				return `run-${nextRun}`;
			},
		});
		await register(runtime, "actor", provider);

		const active = runtime.start({ ...stream("actor"), inputId: "active", payload: {} });
		await first.started;
		const before = runtime.start({ ...stream("actor"), inputId: "before", payload: {} });
		const aborted = runtime.start({ ...stream("actor"), inputId: "aborted", payload: {} });
		const after = runtime.start({ ...stream("actor"), inputId: "after", payload: {} });

		await expect(runtime.control(aborted.initialCoordinate, { type: "abort" })).resolves.toMatchObject({
			accepted: true,
		});
		first.finish();

		await expect(
			Promise.all([active.completion, before.completion, aborted.completion, after.completion]),
		).resolves.toEqual([
			{ runId: active.runId, status: "completed", finalOutput: "done" },
			{ runId: before.runId, status: "completed", finalOutput: "run 2" },
			{ runId: aborted.runId, status: "aborted" },
			{ runId: after.runId, status: "completed", finalOutput: "run 3" },
		]);
		expect(providerCalls).toBe(3);
	});
});
