import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import type { GoalLimits, MailboxLimits, ScheduleLimits } from "@opengameagent/extensions";
import {
	RunAuthorityConflictError,
	WorkAuthorityRetiredError,
	WorkAuthorityRetirementConflictError,
} from "@opengameagent/protocol";
import { GameRuntime } from "@opengameagent/runtime";
import { afterEach, describe, expect, it, vi } from "vitest";
import { controlledResponse, TEST_MODEL } from "../../runtime/test/pi-fakes.js";
import {
	SqliteAuthority,
	SqliteEventStore,
	SqliteGoalStore,
	SqliteMailboxStore,
	SqliteScheduleStore,
} from "../src/index.js";

const scheduleLimits: ScheduleLimits = {
	maxIdentifierBytes: 128,
	maxPayloadBytes: 4096,
	maxSubjectsPerSchedule: 8,
	maxCausesPerSchedule: 8,
	maxTickDigits: 32,
	maxCounterDigits: 16,
	maxActiveSchedulesPerTimelineGeneration: 8,
	maxOccurrencesPerAdvance: 8,
	maxPendingOccurrencesPerTimelineGeneration: 8,
	maxSchedulePageSize: 8,
	maxOccurrencePageSize: 8,
	maxClaimBatchSize: 8,
	maxPurgeBatchSize: 8,
};
const mailboxLimits: MailboxLimits = {
	maxIdentifierBytes: 128,
	maxPayloadBytes: 4096,
	maxTickDigits: 30,
	maxCounterDigits: 20,
	maxPendingMessagesPerTimelineGeneration: 8,
	maxPendingMessagesPerRecipient: 8,
	maxActiveDeliveries: 8,
	maxPageSize: 8,
	maxClaimBatchSize: 8,
	maxPurgeBatchSize: 8,
};
const goalLimits: GoalLimits = {
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
const partition = { ...stream, ownerId: "owner", timelineId: "world", generation: "1" };
const time = (tick: string) => ({ timelineId: partition.timelineId, generation: partition.generation, tick });
const recordedAt = "2026-09-05T00:00:00.000Z";
const after = "2026-09-05T00:00:01.000Z";
const retirement = { retirementId: "retire-work", partition, recordedAt };
const goalRetirement = { retirementId: "retire-goals", partition, recordedAt, stateRevision: "1", gameTime: time("3") };
const request = {
	...stream,
	scope: "host-work",
	generation: "1",
	operationId: "close",
	workRetirement: retirement,
	goalRetirement,
};
const runtimeLimits = { maxActiveActors: 1, maxQueuedRuns: 1, maxInputBytes: 2, maxInputDepth: 1 };
const claim = { ...partition, leaseOwnerId: "host", operationalNow: recordedAt, leaseExpiresAt: after, limit: 1 };
const authorities: SqliteAuthority[] = [];
const roots: string[] = [];
function open(path = ":memory:") {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full", ownership: "exclusive" });
	authorities.push(authority);
	return authority;
}
function fixture(authority = open()) {
	const schedules = new SqliteScheduleStore(authority, scheduleLimits);
	const mailbox = new SqliteMailboxStore(authority, mailboxLimits);
	const goals = new SqliteGoalStore(authority, goalLimits);
	const events = new SqliteEventStore(authority, {
		scheduleStore: schedules,
		mailboxStore: mailbox,
		goalStore: goals,
		now: () => recordedAt,
	});
	return { authority, schedules, mailbox, goals, events };
}
async function seed(f: ReturnType<typeof fixture>) {
	await f.goals.create({ goalId: "goal", partition, objective: "Work", plan: [], gameTime: time("0"), recordedAt }, 2);
	for (const [scheduleId, tick] of [
		["handed", "1"],
		["leased", "2"],
		["pending", "3"],
		["future", "5"],
	] as const) {
		await f.schedules.create(
			{
				scheduleId,
				partition,
				kind: "wake",
				payload: {},
				dueTime: time(tick),
				subjectIds: [],
				causeIds: [],
				recordedAt,
			},
			8,
		);
	}
	await f.schedules.advance(
		{ advanceId: "advance", partition, fromExclusive: time("0"), toInclusive: time("3"), recordedAt },
		8,
		8,
	);
	const [handed] = await f.schedules.claimOccurrences(claim);
	if (!handed) throw new Error("Expected handoff occurrence.");
	await f.schedules.completeOccurrence(
		{ ...partition, occurrenceKey: handed.occurrence.occurrenceKey },
		handed.leaseToken,
		recordedAt,
	);
	for (const messageId of ["handed", "pending"]) {
		await f.mailbox.enqueue(
			{ messageId, recipient: partition, kind: "wake", payload: {}, gameTime: time("3"), recordedAt },
			8,
			8,
		);
	}
	const [occurrence] = await f.schedules.claimOccurrences(claim);
	const [message] = await f.mailbox.claim(claim);
	if (!occurrence || !message) throw new Error("Expected leased work.");
	return { occurrence, message };
}
afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(`${resolve(tmpdir())}${sep}`)) throw new Error("Unexpected test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("composite SQLite work retirement", () => {
	it("drains scoped runs and detached host work before clearing leases and reporting finalized", async () => {
		const f = fixture();
		const { occurrence, message } = await seed(f);
		const entered = Promise.withResolvers<void>();
		const release = Promise.withResolvers<void>();
		let runFinished = false;
		const drainWorkAuthority = vi.fn(async () => {
			expect(runFinished).toBe(true);
			entered.resolve();
			await release.promise;
		});
		const runtime = new GameRuntime(f.events, { ...runtimeLimits, drainWorkAuthority });
		const response = controlledResponse();
		await runtime.registerActor({ ...stream, model: TEST_MODEL, modelStream: response.streamFn });
		const run = await runtime.submit({
			...stream,
			inputId: "run",
			payload: {},
			admissionAuthority: { scope: request.scope, generation: request.generation },
		});
		void run.completion.then(() => {
			runFinished = true;
		});
		await response.started;
		const closing = runtime.closeAuthority(request);
		await entered.promise;
		const concurrent = runtime.closeAuthority(structuredClone(request));
		expect(await f.events.readAuthorityClosure(request)).toMatchObject({
			workRetirementResult: {
				phase: "fenced",
				cancelledScheduleCount: "1",
				cancelledOccurrenceCount: "1",
				cancelledMessageCount: "1",
			},
		});
		expect(await f.goals.get({ ...partition, goalId: "goal" })).toMatchObject({ status: "cancelled" });
		expect(
			await f.schedules.getOccurrence({ ...partition, occurrenceKey: occurrence.occurrence.occurrenceKey }),
		).toMatchObject({ status: "pending" });
		expect(await f.mailbox.getByKey({ ...partition, messageKey: message.message.messageKey })).toMatchObject({
			status: "pending",
		});
		await expect(
			f.mailbox.assertLease({ ...partition, messageKey: message.message.messageKey }, message.leaseToken, recordedAt),
		).rejects.toBeInstanceOf(WorkAuthorityRetiredError);
		release.resolve();
		const finalized = await closing;
		expect(finalized.workRetirementResult).toEqual({
			phase: "finalized",
			cancelledScheduleCount: "1",
			cancelledOccurrenceCount: "2",
			cancelledMessageCount: "2",
		});
		expect(await concurrent).toEqual(finalized);
		expect(await runtime.closeAuthority(request)).toEqual(finalized);
		expect(drainWorkAuthority).toHaveBeenCalledTimes(1);
		expect(
			await f.schedules.getOccurrence({ ...partition, occurrenceKey: occurrence.occurrence.occurrenceKey }),
		).toMatchObject({ status: "cancelled" });
		expect(await f.mailbox.getByKey({ ...partition, messageKey: message.message.messageKey })).toMatchObject({
			status: "cancelled",
		});
	});

	it("rolls back run, Goal and Schedule changes when Mailbox begin conflicts", async () => {
		const f = fixture();
		await seed(f);
		await f.mailbox.beginAuthorityRetirement({ ...retirement, retirementId: "different" });
		await expect(f.events.closeAuthority(request)).rejects.toBeInstanceOf(WorkAuthorityRetirementConflictError);
		expect(await f.events.readAuthorityClosure(request)).toBeUndefined();
		expect(await f.schedules.readAuthorityRetirement(partition)).toBeUndefined();
		expect(await f.goals.get({ ...partition, goalId: "goal" })).toMatchObject({ status: "active" });
		expect(await f.schedules.getSchedule({ ...partition, scheduleId: "future" })).toMatchObject({ status: "active" });
	});

	it("requires both work stores on the same authority without partial Goal or lane closure", async () => {
		const f = fixture();
		await seed(f);
		for (const mailboxStore of [undefined, new SqliteMailboxStore(open(), mailboxLimits)]) {
			const events = new SqliteEventStore(f.authority, {
				goalStore: f.goals,
				scheduleStore: f.schedules,
				...(mailboxStore ? { mailboxStore } : {}),
			});
			await expect(events.closeAuthority(request)).rejects.toThrow(/same (?:active )?SQLite authority/);
			expect(await events.readAuthorityClosure(request)).toBeUndefined();
			expect(await f.schedules.readAuthorityRetirement(partition)).toBeUndefined();
			expect(await f.goals.get({ ...partition, goalId: "goal" })).toMatchObject({ status: "active" });
		}
	});

	it("restarts a fenced retirement after failed host drain and preserves final replay after purge", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-work-close-"));
		roots.push(root);
		const path = join(root, "authority.sqlite");
		const first = fixture(open(path));
		await seed(first);
		const fenced = await first.events.closeAuthority(request);
		first.authority.close();
		const second = fixture(open(path));
		expect(await second.events.readAuthorityClosure(request)).toEqual(fenced);
		const failed = new GameRuntime(second.events, {
			...runtimeLimits,
			drainWorkAuthority: async () => {
				throw new Error("handoff still running");
			},
		});
		await expect(failed.closeAuthority(request)).rejects.toThrow("handoff still running");
		expect(await second.events.readAuthorityClosure(request)).toEqual(fenced);
		second.authority.close();
		const third = fixture(open(path));
		const runtime = new GameRuntime(third.events, { ...runtimeLimits, drainWorkAuthority: async () => undefined });
		const finalized = await runtime.closeAuthority(request);
		expect(finalized.workRetirementResult?.phase).toBe("finalized");
		await third.schedules.purgeTerminalOccurrences(partition, after, 8);
		await third.schedules.purgeTerminalSchedules(partition, after, 8);
		await third.mailbox.purgeTerminal(partition, after, 8);
		third.authority.close();
		const last = fixture(open(path));
		expect(await last.events.closeAuthority(request)).toEqual(finalized);
		expect(await last.events.finalizeAuthorityClosure(request)).toEqual(finalized);
		await expect(last.mailbox.claim(claim)).rejects.toBeInstanceOf(WorkAuthorityRetiredError);
		await expect(last.events.finalizeAuthorityClosure({ ...request, operationId: "wrong" })).rejects.toBeInstanceOf(
			RunAuthorityConflictError,
		);
	});

	it("rolls back both finalizations if one store fails, and retries the identical fenced operation", async () => {
		const f = fixture();
		await seed(f);
		const fenced = await f.events.closeAuthority(request);
		const original = f.mailbox.finalizeAuthorityRetirementInTransaction.bind(f.mailbox);
		const failure = vi.spyOn(f.mailbox, "finalizeAuthorityRetirementInTransaction").mockImplementation((...args) => {
			original(...args);
			throw new Error("final commit failed");
		});
		await expect(f.events.finalizeAuthorityClosure(request)).rejects.toThrow("final commit failed");
		expect(await f.events.readAuthorityClosure(request)).toEqual(fenced);
		expect(await f.schedules.readAuthorityRetirement(partition)).toMatchObject({
			phase: "fenced",
			cancelledOccurrenceCount: 1,
		});
		expect(await f.mailbox.readAuthorityRetirement(partition)).toMatchObject({
			phase: "fenced",
			cancelledMessageCount: 1,
		});
		failure.mockRestore();
		expect((await f.events.finalizeAuthorityClosure(request)).workRetirementResult?.phase).toBe("finalized");
	});
});
