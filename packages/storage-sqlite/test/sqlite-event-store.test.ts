import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import type { RunCoordinate, StreamKey } from "@opengameagent/protocol";
import { ProtocolValidationError } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import {
	SqliteAuthority,
	SqliteAuthorityOpenError,
	SqliteEventStore,
	SqliteExclusiveOwnershipRequiredError,
} from "../src/index.js";

const STREAM: StreamKey = { sessionId: "session", actorId: "actor" };
const COORDINATE: RunCoordinate = { ...STREAM, runId: "run", turn: "0" };
const roots: string[] = [];
const authorities: SqliteAuthority[] = [];

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-event-store-test-"));
	roots.push(root);
	return join(root, "events.sqlite3");
}

function open(path: string, ownership: "shared" | "exclusive" = "exclusive"): SqliteAuthority {
	const authority = new SqliteAuthority({
		path,
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		ownership,
		now: () => "2026-08-28T00:00:00.000Z",
	});
	authorities.push(authority);
	return authority;
}

function store(authority: SqliteAuthority, prefix: string): SqliteEventStore {
	let id = 0;
	return new SqliteEventStore(authority, {
		newEventId: () => {
			id += 1;
			return `${prefix}-${id}`;
		},
		now: () => "2026-08-28T00:00:00.000Z",
	});
}

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteEventStore", () => {
	it("purges bounded terminal prefixes and preserves monotonic revisions across restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = store(firstAuthority, "event");
		const runOne = { ...STREAM, runId: "run-one", turn: "0" };
		const runTwo = { ...STREAM, runId: "run-two", turn: "0" };
		let revision = (
			await first.append(STREAM, "empty", runOne, {
				type: "run.queued",
				inputId: "input-one",
				inputFingerprint: "a".repeat(64),
			})
		).revision;
		revision = (await first.append(STREAM, revision, runOne, { type: "run.started", inputId: "input-one" })).revision;
		revision = (await first.append(STREAM, revision, runOne, { type: "run.completed", status: "completed" })).revision;
		await first.append(STREAM, revision, runTwo, {
			type: "run.queued",
			inputId: "input-two",
			inputFingerprint: "b".repeat(64),
		});

		await expect(
			first.purgeTerminalRuns(STREAM, {
				beforeOccurredAt: "2026-08-29T00:00:00.000Z",
				limit: 3,
				retainRunIds: ["run-one"],
				retainBudget: { maxCount: 1, maxBytes: 6 },
			}),
		).rejects.toThrow("retainRunIds exceeds retainBudget.maxBytes.");
		await expect(
			first.purgeTerminalRuns(STREAM, {
				beforeOccurredAt: "2026-08-29T00:00:00.000Z",
				limit: 3,
				retainRunIds: ["run-one"],
				retainBudget: { maxCount: 1, maxBytes: 7 },
			}),
		).resolves.toMatchObject({ purgedEventCount: 0 });
		await expect(
			first.purgeTerminalRuns(STREAM, {
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
		firstAuthority.close();
		const restarted = store(open(path), "restart");
		expect(await restarted.readRevision(STREAM)).toBe("4");
		const appended = await restarted.append(STREAM, "4", runTwo, { type: "run.started", inputId: "input-two" });
		expect(appended.revision).toBe("5");
	});

	it("indexes durable run admission and terminal status across restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const firstStore = store(firstAuthority, "first-event");
		const fingerprint = "a".repeat(64);
		const queued = await firstStore.append(STREAM, "empty", COORDINATE, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: fingerprint,
		});
		const started = await firstStore.append(
			STREAM,
			queued.revision,
			{ ...COORDINATE, turn: "1" },
			{
				type: "run.started",
				inputId: "input",
			},
		);
		await firstStore.append(
			STREAM,
			started.revision,
			{ ...COORDINATE, turn: "1" },
			{
				type: "run.completed",
				status: "completed",
			},
		);
		firstAuthority.close();

		const restarted = store(open(path), "restarted-event");
		expect(await restarted.readRun(STREAM, "input")).toEqual({
			inputId: "input",
			inputFingerprint: fingerprint,
			eventCursor: "0",
			initialCoordinate: COORDINATE,
			latestCoordinate: { ...COORDINATE, turn: "1" },
			status: "completed",
		});
	});

	it("rejects a second durable admission for the same stream input identifier", async () => {
		const eventStore = store(open(await databasePath()), "event");
		const first = await eventStore.append(STREAM, "empty", COORDINATE, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: "a".repeat(64),
		});
		await expect(
			eventStore.append(
				STREAM,
				first.revision,
				{ ...COORDINATE, runId: "other-run" },
				{
					type: "run.queued",
					inputId: "input",
					inputFingerprint: "b".repeat(64),
				},
			),
		).rejects.toThrow();
	});

	it("projects a suspended provider operation across restart until terminal completion", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const firstStore = store(firstAuthority, "first-event");
		const fingerprint = "a".repeat(64);
		const queued = await firstStore.append(STREAM, "empty", COORDINATE, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: fingerprint,
		});
		await firstStore.append(
			STREAM,
			queued.revision,
			{ ...COORDINATE, turn: "1" },
			{
				type: "run.suspended",
				operationId: "operation",
			},
		);
		firstAuthority.close();

		const restarted = store(open(path), "restarted-event");
		expect(await restarted.readRun(STREAM, "input")).toEqual({
			inputId: "input",
			inputFingerprint: fingerprint,
			eventCursor: "0",
			initialCoordinate: COORDINATE,
			latestCoordinate: { ...COORDINATE, turn: "1" },
			status: "deferred",
			operationId: "operation",
		});
	});

	it("persists canonical events and bounded cursors across restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const firstStore = store(firstAuthority, "first-event");
		const first = await firstStore.append(STREAM, "empty", COORDINATE, {
			type: "run.started",
			inputId: "input",
		});
		await firstStore.append(STREAM, first.revision, { ...COORDINATE, turn: "1" }, { type: "turn.started" });
		firstAuthority.close();

		const secondAuthority = open(path);
		const secondStore = store(secondAuthority, "second-event");
		expect(await secondStore.readRevision(STREAM)).toBe("2");
		const page = await secondStore.read(STREAM, { limit: 1 });
		expect(page.events).toEqual([first]);
		expect(page).toMatchObject({ cursor: "1", hasMore: true });
		expect(await secondStore.read(STREAM, { afterRevision: page.cursor, limit: 1 })).toMatchObject({
			cursor: "2",
			hasMore: false,
		});
	});

	it("validates one data snapshot and host-generated envelope before durable mutation", async () => {
		let typeReads = 0;
		const dynamicData = {} as Record<string, unknown>;
		Object.defineProperty(dynamicData, "type", {
			enumerable: true,
			get: () => {
				typeReads += 1;
				return "turn.started";
			},
		});
		const authority = open(await databasePath());
		const eventStore = store(authority, "event");
		await expect(eventStore.append(STREAM, "empty", COORDINATE, dynamicData as never)).resolves.toMatchObject({
			data: { type: "turn.started" },
		});
		expect(typeReads).toBe(1);

		const invalidTime = new SqliteEventStore(authority, {
			newEventId: () => "invalid-time-event",
			now: () => "not-a-timestamp",
		});
		await expect(invalidTime.append(STREAM, "1", COORDINATE, { type: "turn.started" })).rejects.toMatchObject({
			code: "invalid_timestamp",
		});
		expect(await invalidTime.readRevision(STREAM)).toBe("1");
	});

	it("refuses a second event owner before it can recover a nonterminal run", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const firstStore = store(firstAuthority, "first");
		await firstStore.append(STREAM, "empty", COORDINATE, {
			type: "run.queued",
			inputId: "live-input",
			inputFingerprint: "a".repeat(64),
		});

		expect(() => open(path)).toThrow(SqliteAuthorityOpenError);
		await expect(firstStore.readRun(STREAM, "live-input")).resolves.toMatchObject({ status: null });
	});

	it("requires exclusive ownership before exposing the canonical event authority", async () => {
		const authority = open(await databasePath(), "shared");
		expect(() => store(authority, "event")).toThrow(SqliteExclusiveOwnershipRequiredError);
	});

	it("fails reads when persisted event JSON no longer satisfies the canonical schema", async () => {
		const authority = open(await databasePath());
		const eventStore = store(authority, "event");
		await eventStore.append(STREAM, "empty", COORDINATE, { type: "turn.started" });
		authority.write((database) => {
			database.pragma("ignore_check_constraints = ON");
			database.prepare("UPDATE canonical_events SET data_json = ?").run('{"unknown":true}');
			database.pragma("ignore_check_constraints = OFF");
		});

		await expect(eventStore.read(STREAM, { limit: 1 })).rejects.toThrow(ProtocolValidationError);
	});

	it("rejects cursors outside SQLite's canonical signed 64-bit range", async () => {
		const eventStore = store(open(await databasePath()), "event");
		await expect(eventStore.read(STREAM, { afterRevision: "9223372036854775808", limit: 1 })).rejects.toThrow(
			RangeError,
		);
	});

	it("rejects coordinates owned by another stream before durable mutation", async () => {
		const eventStore = store(open(await databasePath()), "event");
		await expect(
			eventStore.append(STREAM, "empty", { ...COORDINATE, actorId: "other" }, { type: "turn.started" }),
		).rejects.toMatchObject({ code: "invalid_stream_coordinate" });
		expect(await eventStore.readRevision(STREAM)).toBe("empty");
		await expect(
			eventStore.append(STREAM, "empty", { ...COORDINATE, sessionId: "other" }, { type: "turn.started" }),
		).rejects.toBeInstanceOf(ProtocolValidationError);
		expect(await eventStore.readRevision(STREAM)).toBe("empty");
	});
});
