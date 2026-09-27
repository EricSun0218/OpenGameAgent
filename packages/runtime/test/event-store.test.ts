import type { CanonicalEventData, RunCoordinate, StreamKey } from "@opengameagent/protocol";
import { ProtocolValidationError } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { InMemoryEventStore, WrongExpectedRevisionError } from "../src/index.js";

const STREAM: StreamKey = { sessionId: "session", actorId: "actor" };
const COORDINATE: RunCoordinate = { ...STREAM, runId: "run", turn: "0" };

function createStore(): InMemoryEventStore {
	let event = 0;
	return new InMemoryEventStore({
		newEventId: () => {
			event += 1;
			return `event-${event}`;
		},
		now: () => "2026-08-28T00:00:00.000Z",
	});
}

describe("InMemoryEventStore", () => {
	it("rejects an over-budget retention list without purging terminal events", async () => {
		const store = createStore();
		await store.append(STREAM, "empty", COORDINATE, { type: "run.completed", status: "completed" });
		await expect(
			store.purgeTerminalRuns(STREAM, {
				beforeOccurredAt: "2026-08-29T00:00:00.000Z",
				limit: 1,
				retainRunIds: ["龙"],
				retainBudget: { maxCount: 1, maxBytes: 2 },
			}),
		).rejects.toThrow("retainRunIds exceeds retainBudget.maxBytes.");
		await expect(store.readBounds(STREAM)).resolves.toEqual({ earliestRevision: "1", latestRevision: "1" });
	});

	it("purges only complete terminal run prefixes while preserving the stream head", async () => {
		const store = createStore();
		const runOne = { ...STREAM, runId: "run-one", turn: "0" };
		const runTwo = { ...STREAM, runId: "run-two", turn: "0" };
		let revision = (
			await store.append(STREAM, "empty", runOne, {
				type: "run.queued",
				inputId: "input-one",
				inputFingerprint: "a".repeat(64),
			})
		).revision;
		revision = (await store.append(STREAM, revision, runOne, { type: "run.started", inputId: "input-one" })).revision;
		revision = (await store.append(STREAM, revision, runOne, { type: "run.completed", status: "completed" })).revision;
		await store.append(STREAM, revision, runTwo, {
			type: "run.queued",
			inputId: "input-two",
			inputFingerprint: "b".repeat(64),
		});

		await expect(
			store.purgeTerminalRuns(STREAM, {
				beforeOccurredAt: "2026-08-29T00:00:00.000Z",
				limit: 2,
				retainRunIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toMatchObject({ purgedEventCount: 0, earliestRevision: "1", latestRevision: "4" });
		await expect(
			store.purgeTerminalRuns(STREAM, {
				beforeOccurredAt: "2026-08-29T00:00:00.000Z",
				limit: 3,
				retainRunIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual({
			purgedEventCount: 3,
			purgedRunIds: ["run-one"],
			earliestRevision: "4",
			latestRevision: "4",
		});
		await expect(store.readRun(STREAM, "input-one")).resolves.toBeUndefined();
		const appended = await store.append(STREAM, "4", runTwo, { type: "run.started", inputId: "input-two" });
		expect(appended.revision).toBe("5");
	});

	it("appends with compare-and-swap revisions and cursor reads", async () => {
		const store = createStore();
		const first = await store.append(STREAM, "empty", COORDINATE, {
			type: "run.started",
			inputId: "input",
		});
		const second = await store.append(
			STREAM,
			first.revision,
			{ ...COORDINATE, turn: "1" },
			{
				type: "turn.started",
			},
		);

		expect(first.sequence).toBe("1");
		expect(second.revision).toBe("2");
		expect(await store.readRevision(STREAM)).toBe("2");
		expect(await store.read(STREAM, { afterRevision: "1", limit: 1 })).toEqual({
			events: [second],
			cursor: "2",
			hasMore: false,
		});
	});

	it("allows exactly one append for a contested expected revision", async () => {
		const store = createStore();
		const event: CanonicalEventData = { type: "turn.started" };
		const results = await Promise.allSettled([
			store.append(STREAM, "empty", COORDINATE, event),
			store.append(STREAM, "empty", COORDINATE, event),
		]);

		expect(results.filter((result) => result.status === "fulfilled")).toHaveLength(1);
		const rejected = results.find((result) => result.status === "rejected");
		expect(rejected).toMatchObject({ status: "rejected", reason: expect.any(WrongExpectedRevisionError) });
	});

	it("detaches data on append and read", async () => {
		const store = createStore();
		const coordinate = { ...COORDINATE };
		const appended = await store.append(STREAM, "empty", coordinate, { type: "turn.started" });
		appended.coordinate.turn = "999";
		coordinate.turn = "888";

		const reread = await store.read(STREAM, { limit: 1 });
		expect(reread.events[0]?.coordinate.turn).toBe("0");
	});

	it("projects the latest suspended operation until the run becomes terminal", async () => {
		const store = createStore();
		const fingerprint = "a".repeat(64);
		const queued = await store.append(STREAM, "empty", COORDINATE, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: fingerprint,
		});
		const suspended = await store.append(
			STREAM,
			queued.revision,
			{ ...COORDINATE, turn: "1" },
			{
				type: "run.suspended",
				operationId: "operation",
			},
		);

		expect(await store.readRun(STREAM, "input")).toEqual({
			inputId: "input",
			inputFingerprint: fingerprint,
			eventCursor: "0",
			initialCoordinate: COORDINATE,
			latestCoordinate: { ...COORDINATE, turn: "1" },
			status: "deferred",
			operationId: "operation",
		});

		const resumed = await store.append(
			STREAM,
			suspended.revision,
			{ ...COORDINATE, turn: "1" },
			{
				type: "run.resumed",
				operationId: "operation",
			},
		);
		expect(await store.readRun(STREAM, "input")).toMatchObject({ status: null });

		await store.append(
			STREAM,
			resumed.revision,
			{ ...COORDINATE, turn: "1" },
			{
				type: "run.completed",
				status: "completed",
			},
		);
		expect(await store.readRun(STREAM, "input")).toMatchObject({ status: "completed" });
	});

	it("validates one data snapshot and host-generated envelope before mutation", async () => {
		let typeReads = 0;
		const dynamicData = {} as Record<string, unknown>;
		Object.defineProperty(dynamicData, "type", {
			enumerable: true,
			get: () => {
				typeReads += 1;
				return "turn.started";
			},
		});
		const store = createStore();
		await expect(store.append(STREAM, "empty", COORDINATE, dynamicData as never)).resolves.toMatchObject({
			data: { type: "turn.started" },
		});
		expect(typeReads).toBe(1);

		const invalidId = new InMemoryEventStore({
			newEventId: () => "event\u0000",
			now: () => "2026-08-28T00:00:00.000Z",
		});
		await expect(invalidId.append(STREAM, "empty", COORDINATE, { type: "turn.started" })).rejects.toMatchObject({
			code: "invalid_identifier",
		});
		expect(await invalidId.readRevision(STREAM)).toBe("empty");

		const invalidTime = new InMemoryEventStore({ newEventId: () => "event", now: () => "not-a-timestamp" });
		await expect(invalidTime.append(STREAM, "empty", COORDINATE, { type: "turn.started" })).rejects.toMatchObject({
			code: "invalid_timestamp",
		});
		expect(await invalidTime.readRevision(STREAM)).toBe("empty");
	});

	it("rejects malformed cursors", async () => {
		const store = createStore();
		await expect(store.read(STREAM, { afterRevision: "01", limit: 1 })).rejects.toThrow(TypeError);
		await expect(store.read(STREAM, { limit: 0 })).rejects.toThrow(TypeError);
	});

	it("rejects coordinates owned by another stream before mutation", async () => {
		const store = createStore();
		await expect(
			store.append(STREAM, "empty", { ...COORDINATE, actorId: "other" }, { type: "turn.started" }),
		).rejects.toMatchObject({ code: "invalid_stream_coordinate" });
		expect(await store.readRevision(STREAM)).toBe("empty");
		await expect(
			store.append(STREAM, "empty", { ...COORDINATE, sessionId: "other" }, { type: "turn.started" }),
		).rejects.toBeInstanceOf(ProtocolValidationError);
		expect(await store.readRevision(STREAM)).toBe("empty");
	});
});
