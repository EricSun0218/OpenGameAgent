import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	type GameScheduleDraft,
	ScheduleAdvanceConflictError,
	ScheduleAdvanceOverflowError,
	ScheduleCapacityError,
	ScheduleClockConflictError,
	ScheduledSignalDispatcher,
	type ScheduleLimits,
	ScheduleMissedOccurrenceError,
	ScheduleOccurrenceLeaseConflictError,
	ScheduleRevisionConflictError,
	ScheduleService,
	ScheduleValidationError,
	type SignalLimits,
	SignalService,
} from "@opengameagent/extensions";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteScheduleStore, SqliteSignalStore } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const scheduleLimits: ScheduleLimits = {
	maxIdentifierBytes: 128,
	maxPayloadBytes: 4_096,
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
const signalLimits: SignalLimits = {
	maxPayloadBytes: 4_096,
	maxSubjectsPerSignal: 8,
	maxCausesPerSignal: 8,
	maxSignalFilterKinds: 8,
	maxWaitKinds: 8,
	maxWaitSubjects: 8,
	maxWaitMatchesPerSignal: 8,
	maxSignalPageSize: 8,
	maxWaitPageSize: 8,
	maxExpireBatchSize: 8,
	maxPurgeBatchSize: 8,
};

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteScheduleStore", () => {
	it("restores exact large-tick schedules, ordered occurrences, and idempotent signal dispatch", async () => {
		const path = await databasePath();
		const due = 90_071_992_547_409_931_234_567_890n;
		const firstAuthority = open(path);
		const first = service(firstAuthority);
		await first.create(schedule("recurring", due.toString(), { intervalTicks: "10", maximumOccurrences: "3" }));
		await first.create(schedule("one-shot", (due + 5n).toString(), { partition: partition("actor-2") }));
		firstAuthority.close();

		const secondAuthority = open(path);
		const signalIds = ["scheduled-signal-1", "scheduled-signal-2", "scheduled-signal-3"];
		const second = service(secondAuthority, undefined, () => signalIds.shift() ?? "scheduled-signal-more");
		const request = advance("advance-large", (due - 1n).toString(), (due + 15n).toString(), timestamp(1));
		const materialized = await second.advance(request, 8);
		expect(materialized.occurrences.map((item) => [item.scheduleId, item.ordinal, item.dueTime.tick])).toEqual([
			["recurring", "1", due.toString()],
			["one-shot", "1", (due + 5n).toString()],
			["recurring", "2", (due + 10n).toString()],
		]);
		secondAuthority.close();

		const restartedAuthority = open(path);
		const restarted = service(restartedAuthority, () => "lease-dispatch");
		expect(await restarted.advance(request, 8)).toEqual(materialized);
		expect((await restarted.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual(
			materialized.occurrences,
		);
		const signals = new SignalService(new SqliteSignalStore(restartedAuthority, signalLimits), signalLimits);
		await signals.registerWait({
			waitId: "wait-scheduled",
			partition: partition(),
			selector: { kinds: ["world.month"] },
			target: { kind: "correlation", correlationId: "wake-actor" },
			recordedAt: timestamp(1),
		});
		const [delivery] = await restarted.claimOccurrences({
			...generationKey(),
			actorId: "actor-1",
			leaseOwnerId: "dispatcher-1",
			operationalNow: timestamp(2),
			leaseExpiresAt: timestamp(4),
			limit: 1,
		});
		if (delivery === undefined) throw new Error("Expected a scheduled delivery.");
		const dispatcher = new ScheduledSignalDispatcher(restarted, signals);
		const dispatched = await dispatcher.dispatch(delivery, timestamp(3));
		expect(dispatched).toMatchObject({
			occurrence: { status: "completed" },
			signal: { signalId: "scheduled-signal-1", gameTime: gameTime(due.toString()) },
			matchedWaitIds: ["wait-scheduled"],
		});
		expect(await dispatcher.dispatch(delivery, timestamp(3))).toEqual(dispatched);
	});

	it("rolls back schedule, occurrence, advance, and clock state on host-bound failures", async () => {
		const path = await databasePath();
		const current = service(open(path));
		await current.create(schedule("recurring", "10", { intervalTicks: "10", maximumOccurrences: "3" }));
		const request = advance("advance-overflow", "0", "35", timestamp(1));
		await expect(current.advance(request, 2)).rejects.toBeInstanceOf(ScheduleAdvanceOverflowError);
		expect(await current.getSchedule(scheduleKey("recurring"))).toMatchObject({
			revision: "1",
			occurrences: "0",
			nextDueTick: "10",
		});
		expect((await current.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual([]);
		expect((await current.advance(request, 3)).occurrences).toHaveLength(3);
		await expect(current.advance(request, 2)).rejects.toBeInstanceOf(ScheduleAdvanceOverflowError);

		const boundedPath = await databasePath();
		const bounded = service(open(boundedPath), undefined, undefined, {
			...scheduleLimits,
			maxPendingOccurrencesPerTimelineGeneration: 2,
		});
		await bounded.create(schedule("bounded", "10", { intervalTicks: "10" }));
		await expect(bounded.advance(advance("capacity", "0", "30", timestamp(1)), 3)).rejects.toMatchObject({
			constructor: ScheduleCapacityError,
			code: "pending_occurrences",
		});
		expect((await bounded.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual([]);
		expect(await bounded.getSchedule(scheduleKey("bounded"))).toMatchObject({ revision: "1", nextDueTick: "10" });
	});

	it("settles stale revisions and conflicting clock or advance identities across authorities", async () => {
		const path = await databasePath();
		const first = service(open(path));
		const second = service(open(path));
		const created = await first.create(schedule("cancel", "10"));
		expect(await first.cancelSchedule(scheduleKey("cancel"), created.revision, timestamp(1))).toMatchObject({
			status: "cancelled",
			revision: "2",
		});
		await expect(second.cancelSchedule(scheduleKey("cancel"), created.revision, timestamp(2))).rejects.toBeInstanceOf(
			ScheduleRevisionConflictError,
		);

		await first.advance(advance("clock-1", "0", "5", timestamp(3)), 8);
		await expect(second.advance(advance("clock-stale", "0", "6", timestamp(4)), 8)).rejects.toBeInstanceOf(
			ScheduleClockConflictError,
		);
		const stable = advance("stable-id", "5", "6", timestamp(5));
		await first.advance(stable, 8);
		await expect(second.advance({ ...stable, toInclusive: gameTime("7") }, 8)).rejects.toBeInstanceOf(
			ScheduleAdvanceConflictError,
		);
		await expect(first.create(schedule("past", "6"))).rejects.toBeInstanceOf(ScheduleMissedOccurrenceError);
		expect(
			await first.getScheduleByKey({ ...generationKey(), ownerId: "owner-2", scheduleKey: created.scheduleKey }),
		).toBeUndefined();
	});

	it("reclaims leases after restart, fences stale tokens, and replays successful completion", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority, () => "lease-old");
		await first.create(schedule("once", "10"));
		await first.advance(advance("advance", "0", "10", timestamp(1)), 1);
		const [original] = await first.claimOccurrences(claim(timestamp(2), timestamp(4)));
		if (original === undefined) throw new Error("Expected the original lease.");
		firstAuthority.close();

		const secondAuthority = open(path);
		const restarted = service(secondAuthority, () => "lease-new");
		expect(await restarted.claimOccurrences(claim(timestamp(3), timestamp(5)))).toEqual([]);
		await expect(
			restarted.completeOccurrence(occurrenceKey(original.occurrence.occurrenceKey), original.leaseToken, timestamp(4)),
		).rejects.toBeInstanceOf(ScheduleOccurrenceLeaseConflictError);
		const [reclaimed] = await restarted.claimOccurrences(claim(timestamp(4), timestamp(6)));
		if (reclaimed === undefined) throw new Error("Expected a reclaimed lease.");
		expect(reclaimed).toMatchObject({ occurrence: { attempt: "2" }, leaseToken: "lease-new" });
		const key = occurrenceKey(reclaimed.occurrence.occurrenceKey);
		await expect(restarted.completeOccurrence(key, original.leaseToken, timestamp(5))).rejects.toBeInstanceOf(
			ScheduleOccurrenceLeaseConflictError,
		);
		const completed = await restarted.completeOccurrence(key, reclaimed.leaseToken, timestamp(5));
		secondAuthority.close();

		const third = service(open(path));
		expect(await third.completeOccurrence(key, reclaimed.leaseToken, timestamp(1))).toEqual(completed);
		expect(await third.purgeTerminalOccurrences(generationKey(), timestamp(6), 1)).toEqual([
			reclaimed.occurrence.occurrenceKey,
		]);
		expect(await third.purgeTerminalSchedules(generationKey(), timestamp(6), 1)).toHaveLength(1);
		expect(await third.purgeAdvances(generationKey(), timestamp(6), 1)).toHaveLength(1);
	});

	it("isolates rewind generations and actor delivery selectors", async () => {
		const current = service(open(await databasePath()));
		await current.create(schedule("actor-1", "10"));
		await current.create(schedule("actor-2", "10", { partition: partition("actor-2") }));
		await current.create(
			schedule("generation-2", "10", {
				partition: { ...partition(), generation: "2" },
				dueTime: { ...gameTime("10"), generation: "2" },
			}),
		);
		await current.advance(advance("advance-1", "0", "10", timestamp(1)), 8);
		expect(
			await current.claimOccurrences({
				...claim(timestamp(2), timestamp(3)),
				actorId: "actor-2",
			}),
		).toHaveLength(1);
		expect((await current.listOccurrences({ ...generationKey(), limit: 8 })).records).toHaveLength(2);
		expect((await current.listOccurrences({ ...generationKey(), generation: "2", limit: 8 })).records).toEqual([]);
	});

	it("shares empty-filter rejection and fails closed on corrupted occurrence semantics", async () => {
		const authority = open(await databasePath());
		const current = service(authority);
		await expect(current.listSchedules({ ...generationKey(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
		await expect(current.listOccurrences({ ...generationKey(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
		await current.create(schedule("corrupt", "10"));
		const [occurrence] = (await current.advance(advance("advance", "0", "10", timestamp(1)), 1)).occurrences;
		if (occurrence === undefined) throw new Error("Expected an occurrence.");
		authority.write((database) =>
			database
				.prepare("UPDATE oga_scheduled_occurrences SET due_tick = '11' WHERE occurrence_key = ?")
				.run(occurrence.occurrenceKey),
		);
		await expect(current.getOccurrence(occurrenceKey(occurrence.occurrenceKey))).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
	});
});

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-schedule-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
	authorities.push(authority);
	return authority;
}

function service(
	authority: SqliteAuthority,
	leaseToken?: () => string,
	signalId?: () => string,
	limits = scheduleLimits,
): ScheduleService {
	return new ScheduleService(new SqliteScheduleStore(authority, limits, leaseToken, signalId), limits);
}

function partition(actorId = "actor-1") {
	return {
		ownerId: "owner-1",
		timelineId: "timeline-1",
		generation: "1",
		sessionId: "session-1",
		actorId,
	};
}

function generationKey() {
	return { ownerId: "owner-1", timelineId: "timeline-1", generation: "1" };
}

function schedule(scheduleId: string, dueTick: string, overrides: Partial<GameScheduleDraft> = {}): GameScheduleDraft {
	return {
		scheduleId,
		partition: partition(),
		kind: "world.month",
		payload: { reason: "month" },
		dueTime: gameTime(dueTick),
		subjectIds: ["npc-1"],
		causeIds: ["world-clock"],
		recordedAt: timestamp(0),
		...overrides,
	};
}

function advance(advanceId: string, fromTick: string, toTick: string, recordedAt: string) {
	return {
		advanceId,
		partition: generationKey(),
		fromExclusive: gameTime(fromTick),
		toInclusive: gameTime(toTick),
		recordedAt,
	};
}

function claim(operationalNow: string, leaseExpiresAt: string) {
	return {
		...generationKey(),
		leaseOwnerId: "worker",
		operationalNow,
		leaseExpiresAt,
		limit: 1,
	};
}

function gameTime(tick: string) {
	return { timelineId: "timeline-1", generation: "1", tick };
}

function scheduleKey(scheduleId: string) {
	return { ...generationKey(), scheduleId };
}

function occurrenceKey(value: bigint) {
	return { ...generationKey(), occurrenceKey: value };
}

function timestamp(second: number): string {
	return `2026-08-28T00:00:${second.toString().padStart(2, "0")}.000Z`;
}
