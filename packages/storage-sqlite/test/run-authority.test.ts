import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { GoalAuthorityRetiredError, type GoalLimits, InMemoryGoalStore } from "@opengameagent/extensions";
import {
	type CanonicalEventData,
	type CanonicalEventStore,
	type RunAuthorityClosure,
	captureCloseRunAuthorityRequest,
	RunAuthorityClosedError,
	RunAuthorityConflictError,
} from "@opengameagent/protocol";
import { InMemoryEventStore } from "@opengameagent/runtime";
import { describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteEventStore, SqliteGoalStore } from "../src/index.js";

const limits: GoalLimits = {
	maxObjectiveBytes: 1024,
	maxStepsPerGoal: 8,
	maxStepBytes: 512,
	maxPlanBytes: 8192,
	maxRecordBytes: 32768,
	maxModelResultBytes: 16384,
	maxEvidencePerStep: 4,
	maxEvidenceKindBytes: 64,
	maxEvidenceReferenceBytes: 256,
	maxOpenGoalsPerOwnerGeneration: 2,
	maxListPageSize: 4,
	maxPurgeBatchSize: 4,
};
const stream = { sessionId: "session", actorId: "actor" };
const partition = { ...stream, ownerId: "actor", timelineId: "world", generation: "1" };
const gameTime = { timelineId: "world", generation: "1", tick: "1" };
const recordedAt = "2026-09-05T00:00:00.000Z";
const retirement = { retirementId: "close-goals", stateRevision: "1", partition, gameTime, recordedAt };
const admissionAuthority = { scope: "planning", generation: "1" };
const closeRequest = { ...stream, ...admissionAuthority, operationId: "close-1" };
const coordinate = { ...stream, runId: "run", turn: "0" };

describe.each(["memory", "sqlite"] as const)("%s Goal authority fence", (kind) => {
	it("rejects direct replacement using the latest cancelled revision", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const goals = kind === "memory" ? new InMemoryGoalStore(limits) : new SqliteGoalStore(authority, limits);
			await goals.create({ goalId: "goal", partition, objective: "Objective", plan: [], gameTime, recordedAt }, 2);
			await goals.retireAuthority(retirement);
			await expect(
				goals.replace({ ...partition, goalId: "goal" }, "2", {
					status: "active",
					plan: [],
					gameTime,
					recordedAt,
					lastHostAdvanceFingerprint: null,
				}),
			).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
			expect(await goals.get({ ...partition, goalId: "goal" })).toMatchObject({ status: "cancelled", revision: "2" });
		} finally {
			authority.close();
		}
	});
});

describe.each(["memory", "sqlite"] as const)("%s scoped run authority", (kind) => {
	it.each([
		["newEventId", "run.queued"],
		["newEventId", "run.started"],
		["newEventId", "run.resumed"],
		["now", "run.queued"],
		["now", "run.started"],
		["now", "run.resumed"],
	] as const)("fences %s callback closure before committing %s", async (callback, type) => {
		const authority = new SqliteAuthority({
			path: ":memory:",
			busyTimeoutMs: 0,
			verifyOnOpen: "full",
			ownership: "exclusive",
		});
		try {
			let events: CanonicalEventStore;
			let armed = false;
			let closure: Promise<RunAuthorityClosure> | undefined;
			let nextId = 0;
			const inputStream = { ...stream };
			const inputCoordinate = { ...coordinate };
			const trigger = () => {
				if (!armed) return;
				armed = false;
				closure = events.closeAuthority(closeRequest);
				inputStream.actorId = "other";
				inputCoordinate.actorId = "other";
			};
			const options = {
				newEventId: () => {
					if (callback === "newEventId") trigger();
					return `event-${++nextId}`;
				},
				now: () => {
					if (callback === "now") trigger();
					return recordedAt;
				},
			};
			events = kind === "memory" ? new InMemoryEventStore(options) : new SqliteEventStore(authority, options);
			const queued = {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority,
			} as const;
			if (type !== "run.queued") await events.append(stream, "empty", coordinate, queued);
			const data: CanonicalEventData =
				type === "run.queued"
					? queued
					: type === "run.started"
						? { type, inputId: "input" }
						: { type, operationId: "deferred" };
			armed = true;
			await expect(
				events.append(inputStream, type === "run.queued" ? "empty" : "1", inputCoordinate, data),
			).rejects.toBeInstanceOf(RunAuthorityClosedError);
			expect(closure).toBeDefined();
			expect(await events.readAuthorityClosure(closeRequest)).toEqual(await closure);
			expect(await events.readRevision(stream)).toBe(type === "run.queued" ? "empty" : "1");
			expect(await events.readRevision(inputStream)).toBe("empty");
			if (type === "run.queued") {
				expect(await events.readRun(stream, "input")).toBeUndefined();
				await expect(events.assertRunAuthorityOpen(coordinate)).resolves.toBeUndefined();
			}
		} finally {
			authority.close();
		}
	});

	it("preserves a close committed by its clock callback and replays without sampling the clock", async () => {
		const authority = new SqliteAuthority({
			path: ":memory:",
			busyTimeoutMs: 0,
			verifyOnOpen: "full",
			ownership: "exclusive",
		});
		try {
			let events: CanonicalEventStore;
			let armed = true;
			let clockCalls = 0;
			let nested: Promise<RunAuthorityClosure> | undefined;
			const nestedRequest = { ...closeRequest, operationId: "nested-close" };
			const options = {
				now: () => {
					clockCalls++;
					if (armed) {
						armed = false;
						nested = events.closeAuthority(nestedRequest);
					}
					return recordedAt;
				},
			};
			events = kind === "memory" ? new InMemoryEventStore(options) : new SqliteEventStore(authority, options);
			await expect(events.closeAuthority(closeRequest)).rejects.toBeInstanceOf(RunAuthorityConflictError);
			expect(nested).toBeDefined();
			const closure = await nested;
			expect(await events.readAuthorityClosure(closeRequest)).toEqual(closure);
			const sampled = clockCalls;
			expect(await events.closeAuthority(nestedRequest)).toEqual(closure);
			expect(clockCalls).toBe(sampled);
		} finally {
			authority.close();
		}
	});

	it("fences admission and activation while retaining unrelated authority and unscoped use", async () => {
		const authority = new SqliteAuthority({
			path: ":memory:",
			busyTimeoutMs: 0,
			verifyOnOpen: "full",
			ownership: "exclusive",
		});
		try {
			const events = kind === "memory" ? new InMemoryEventStore() : new SqliteEventStore(authority);
			await events.append(stream, "empty", coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority,
			});
			expect(await events.readRun(stream, "input")).toMatchObject({ admissionAuthority });
			const closed = await events.closeAuthority(closeRequest);
			expect(await events.closeAuthority(closeRequest)).toEqual(closed);
			expect(await events.readAuthorityClosure(closeRequest)).toEqual(closed);
			await expect(events.assertRunAuthorityOpen(coordinate)).rejects.toBeInstanceOf(RunAuthorityClosedError);
			for (const data of [
				{ type: "run.started", inputId: "input" },
				{ type: "run.resumed", operationId: "pending" },
			] as const) {
				await expect(events.append(stream, "1", coordinate, data)).rejects.toBeInstanceOf(RunAuthorityClosedError);
			}
			await expect(
				events.append(
					stream,
					"1",
					{ ...coordinate, runId: "late" },
					{ type: "run.queued", inputId: "late", inputFingerprint: "b".repeat(64), admissionAuthority },
				),
			).rejects.toBeInstanceOf(RunAuthorityClosedError);
			await events.append(
				stream,
				"1",
				{ ...coordinate, runId: "chat" },
				{ type: "run.queued", inputId: "chat", inputFingerprint: "c".repeat(64) },
			);
			await events.append(
				stream,
				"2",
				{ ...coordinate, runId: "next" },
				{
					type: "run.queued",
					inputId: "next",
					inputFingerprint: "d".repeat(64),
					admissionAuthority: { ...admissionAuthority, generation: "2" },
				},
			);
			await expect(events.closeAuthority({ ...closeRequest, actorId: "another" })).rejects.toBeInstanceOf(
				RunAuthorityConflictError,
			);
			await expect(events.closeAuthority({ ...closeRequest, operationId: "different" })).rejects.toBeInstanceOf(
				RunAuthorityConflictError,
			);
		} finally {
			authority.close();
		}
	});

	it("closes Goal writes in the same operation and rolls back rejected Goal retirement", async () => {
		const authority = new SqliteAuthority({
			path: ":memory:",
			busyTimeoutMs: 0,
			verifyOnOpen: "full",
			ownership: "exclusive",
		});
		try {
			const goals = kind === "memory" ? new InMemoryGoalStore(limits) : new SqliteGoalStore(authority, limits);
			const events =
				goals instanceof InMemoryGoalStore
					? new InMemoryEventStore({ retireGoalAuthority: (request) => goals.retireAuthoritySynchronously(request) })
					: new SqliteEventStore(authority, { goalStore: goals });
			await goals.create({ goalId: "goal", partition, objective: "Objective", plan: [], gameTime, recordedAt }, 2);
			const rejected = { ...closeRequest, goalRetirement: { ...retirement, gameTime: { ...gameTime, tick: "0" } } };
			await expect(events.closeAuthority(rejected)).rejects.toMatchObject({ code: "goal_time_regression" });
			expect(await events.readAuthorityClosure(closeRequest)).toBeUndefined();
			expect(await goals.get({ ...partition, goalId: "goal" })).toMatchObject({ status: "active", revision: "1" });
			const request = { ...closeRequest, goalRetirement: retirement };
			const closure = await events.closeAuthority(request);
			expect(await events.closeAuthority(request)).toEqual(closure);
			expect(await goals.get({ ...partition, goalId: "goal" })).toMatchObject({ status: "cancelled", revision: "2" });
			await expect(
				goals.create({ goalId: "late", partition, objective: "Late", plan: [], gameTime, recordedAt }, 2),
			).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
			await expect(
				events.closeAuthority({ ...request, goalRetirement: { ...retirement, stateRevision: "2" } }),
			).rejects.toBeInstanceOf(RunAuthorityConflictError);
		} finally {
			authority.close();
		}
	});

	it("requires an explicit Goal binding and preserves run authority after event purge", async () => {
		const authority = new SqliteAuthority({
			path: ":memory:",
			busyTimeoutMs: 0,
			verifyOnOpen: "full",
			ownership: "exclusive",
		});
		try {
			const events =
				kind === "memory"
					? new InMemoryEventStore({ now: () => recordedAt })
					: new SqliteEventStore(authority, { now: () => recordedAt });
			await expect(events.closeAuthority({ ...closeRequest, goalRetirement: retirement })).rejects.toBeInstanceOf(
				TypeError,
			);
			expect(await events.readAuthorityClosure(closeRequest)).toBeUndefined();
			await events.append(stream, "empty", coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority,
			});
			await events.append(stream, "1", coordinate, { type: "run.completed", status: "aborted" });
			expect(
				await events.purgeTerminalRuns(stream, {
					beforeOccurredAt: "2026-09-06T00:00:00.000Z",
					limit: 2,
					retainRunIds: [],
					retainBudget: { maxCount: 0, maxBytes: 0 },
				}),
			).toMatchObject({ purgedEventCount: 2 });
			expect(await events.readRun(stream, "input")).toBeUndefined();
			await events.closeAuthority(closeRequest);
			await expect(events.assertRunAuthorityOpen(coordinate)).rejects.toBeInstanceOf(RunAuthorityClosedError);
			await expect(
				events.append(stream, "2", coordinate, {
					type: "run.queued",
					inputId: "unscoped-reuse",
					inputFingerprint: "a".repeat(64),
				}),
			).rejects.toBeInstanceOf(RunAuthorityConflictError);
		} finally {
			authority.close();
		}
	});
});

it("persists close-before-admission and retired Goal writes across SQLite restart", async () => {
	const root = await mkdtemp(join(tmpdir(), "oga-run-close-"));
	const path = join(root, "authority.sqlite");
	const open = () => new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full", ownership: "exclusive" });
	let authority = open();
	try {
		const goals = new SqliteGoalStore(authority, limits);
		await goals.create({ goalId: "goal", partition, objective: "Objective", plan: [], gameTime, recordedAt }, 2);
		const events = new SqliteEventStore(authority, { goalStore: goals, now: () => recordedAt });
		const request = { ...closeRequest, goalRetirement: retirement };
		const closure = await events.closeAuthority(request);
		const purgedCoordinate = { ...coordinate, runId: "purged" };
		await events.append(stream, "empty", purgedCoordinate, {
			type: "run.queued",
			inputId: "purged",
			inputFingerprint: "b".repeat(64),
			admissionAuthority: { ...admissionAuthority, generation: "2" },
		});
		await events.append(stream, "1", purgedCoordinate, { type: "run.completed", status: "aborted" });
		await events.purgeTerminalRuns(stream, {
			beforeOccurredAt: "2026-09-06T00:00:00.000Z",
			limit: 2,
			retainRunIds: [],
			retainBudget: { maxCount: 0, maxBytes: 0 },
		});
		await events.closeAuthority({ ...closeRequest, generation: "2", operationId: "close-2" });
		authority.close();
		authority = open();
		const reopened = new SqliteEventStore(authority);
		expect(await reopened.closeAuthority(request)).toEqual(closure);
		expect(await reopened.readAuthorityClosure(closeRequest)).toEqual(closure);
		expect(await reopened.readRun(stream, "purged")).toBeUndefined();
		await expect(reopened.assertRunAuthorityOpen(purgedCoordinate)).rejects.toBeInstanceOf(RunAuthorityClosedError);
		await expect(
			reopened.append(stream, "2", coordinate, {
				type: "run.queued",
				inputId: "late",
				inputFingerprint: "a".repeat(64),
				admissionAuthority,
			}),
		).rejects.toBeInstanceOf(RunAuthorityClosedError);
		await expect(
			new SqliteGoalStore(authority, limits).replace({ ...partition, goalId: "goal" }, "2", {
				plan: [],
				status: "active",
				gameTime,
				recordedAt,
				lastHostAdvanceFingerprint: null,
			}),
		).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
	} finally {
		authority.close();
		await removeTestRoot(root);
	}
});

async function removeTestRoot(root: string): Promise<void> {
	const target = resolve(root);
	if (!target.startsWith(`${resolve(tmpdir())}${sep}`)) throw new Error("Unexpected temporary root.");
	await rm(target, { recursive: true, force: true });
}

it("rejects Goal stores belonging to another SQLite authority without partial closure", async () => {
	const options = { path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full", ownership: "exclusive" } as const;
	const authority = new SqliteAuthority(options);
	const foreign = new SqliteAuthority(options);
	try {
		const events = new SqliteEventStore(authority, { goalStore: new SqliteGoalStore(foreign, limits) });
		await expect(events.closeAuthority({ ...closeRequest, goalRetirement: retirement })).rejects.toThrow(
			"same active SQLite authority transaction",
		);
		expect(await events.readAuthorityClosure(closeRequest)).toBeUndefined();
	} finally {
		authority.close();
		foreign.close();
	}
});

it("captures authority and Goal identity fields once and rejects malformed generations", () => {
	const input = { ...closeRequest, goalRetirement: { ...retirement, partition: { ...partition } } };
	let generationReads = 0;
	Object.defineProperty(input, "generation", { get: () => (++generationReads === 1 ? "1" : "2") });
	let partitionReads = 0;
	Object.defineProperty(input.goalRetirement.partition, "generation", {
		get: () => (++partitionReads === 1 ? "1" : "2"),
	});
	const captured = captureCloseRunAuthorityRequest(input);
	expect(generationReads).toBe(1);
	expect(partitionReads).toBe(1);
	expect(captured.generation).toBe("1");
	expect(Object.isFrozen(captured.goalRetirement?.partition)).toBe(true);
	for (const generation of ["-1", "01", "1.0"])
		expect(() => captureCloseRunAuthorityRequest({ ...closeRequest, generation })).toThrow(TypeError);
});

it("rolls back both SQLite records when a Goal update fails after the closure insert", async () => {
	const authority = new SqliteAuthority({
		path: ":memory:",
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		ownership: "exclusive",
	});
	try {
		const goals = new SqliteGoalStore(authority, limits);
		await goals.create({ goalId: "goal", partition, objective: "Objective", plan: [], gameTime, recordedAt }, 2);
		const events = new SqliteEventStore(authority, { goalStore: goals });
		authority.write((database) =>
			database.exec(
				"CREATE TRIGGER reject_goal_close BEFORE UPDATE ON oga_goals BEGIN SELECT RAISE(ABORT, 'write-failure'); END",
			),
		);
		await expect(events.closeAuthority({ ...closeRequest, goalRetirement: retirement })).rejects.toThrow(
			"write-failure",
		);
		expect(await events.readAuthorityClosure(closeRequest)).toBeUndefined();
		expect(await goals.get({ ...partition, goalId: "goal" })).toMatchObject({ status: "active", revision: "1" });
		authority.write((database) => database.exec("DROP TRIGGER reject_goal_close"));
		await expect(
			goals.create({ goalId: "still-open", partition, objective: "Open", plan: [], gameTime, recordedAt }, 2),
		).resolves.toMatchObject({ status: "active" });
	} finally {
		authority.close();
	}
});
