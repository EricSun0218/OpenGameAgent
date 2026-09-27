import {
	type GameScheduleDraft,
	InMemoryScheduleStore,
	InMemorySignalStore,
	ScheduleAdvanceConflictError,
	ScheduleAdvanceOverflowError,
	ScheduleCapacityError,
	ScheduleClockConflictError,
	ScheduleConflictError,
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
import { describe, expect, it } from "vitest";

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

describe("ScheduleService", () => {
	it("materializes ordered recurring occurrences and dispatches each as an idempotent durable signal", async () => {
		const tokens = ["lease-1", "lease-2"];
		const schedules = createService(scheduleLimits, () => tokens.shift() ?? "lease-more");
		const signals = new SignalService(new InMemorySignalStore(), signalLimits);
		const dispatcher = new ScheduledSignalDispatcher(schedules, signals);
		const recurring = await schedules.create(
			schedule("recurring", "10", { intervalTicks: "10", maximumOccurrences: "3" }),
		);
		await schedules.create(schedule("one-shot", "15", { partition: partition("actor-2") }));
		await signals.registerWait({
			waitId: "wait-scheduled",
			partition: partition(),
			selector: { kinds: ["world.month"] },
			target: { kind: "correlation", correlationId: "wake-actor" },
			recordedAt: timestamp(1),
		});

		const request = advance("advance-1", "0", "25", timestamp(2));
		const first = await schedules.advance(request, 8);
		expect(first.occurrences.map((item) => [item.scheduleId, item.ordinal, item.dueTime.tick])).toEqual([
			["recurring", "1", "10"],
			["one-shot", "1", "15"],
			["recurring", "2", "20"],
		]);
		expect(await schedules.advance(request, 8)).toEqual(first);
		expect(await schedules.getSchedule(scheduleKey("recurring"))).toMatchObject({
			status: "active",
			revision: "2",
			occurrences: "2",
			nextDueTick: "30",
		});
		expect(await schedules.getSchedule(scheduleKey("one-shot"))).toMatchObject({
			status: "completed",
			revision: "2",
			occurrences: "1",
		});

		const [delivery] = await schedules.claimOccurrences({
			...generationKey(),
			actorId: "actor-1",
			leaseOwnerId: "dispatcher-1",
			operationalNow: timestamp(3),
			leaseExpiresAt: timestamp(5),
			limit: 1,
		});
		expect(delivery).toMatchObject({ occurrence: { scheduleId: "recurring", attempt: "1" }, leaseToken: "lease-1" });
		if (delivery === undefined) throw new Error("Expected a scheduled delivery.");
		const dispatched = await dispatcher.dispatch(delivery, timestamp(4));
		expect(dispatched).toMatchObject({ occurrence: { status: "completed" }, matchedWaitIds: ["wait-scheduled"] });
		expect(dispatched.signal).toMatchObject({
			signalId: delivery.occurrence.signalId,
			kind: "world.month",
			gameTime: gameTime("10"),
		});
		expect(await dispatcher.dispatch(delivery, timestamp(4))).toEqual(dispatched);
		expect(recurring.scheduleKey).toBeGreaterThan(0n);
	});

	it("orders huge tied ticks by schedule key when a recurring schedule is reinserted", async () => {
		const signalIds = ["signal-recurring-1", "signal-recurring-2", "signal-once", "signal-recurring-3"];
		const schedules = createService(scheduleLimits, undefined, () => signalIds.shift() ?? "signal-extra");
		await schedules.create(
			schedule("recurring", "99999999999999999999999999999997", {
				intervalTicks: "1",
				maximumOccurrences: "3",
			}),
		);
		await schedules.create(schedule("one-shot", "99999999999999999999999999999998"));

		const result = await schedules.advance(
			advance("advance-huge-tie", "99999999999999999999999999999996", "99999999999999999999999999999999", timestamp(1)),
			8,
		);

		expect(result.occurrences.map(({ scheduleId, dueTime, signalId }) => [scheduleId, dueTime.tick, signalId])).toEqual(
			[
				["recurring", "99999999999999999999999999999997", "signal-recurring-1"],
				["recurring", "99999999999999999999999999999998", "signal-recurring-2"],
				["one-shot", "99999999999999999999999999999998", "signal-once"],
				["recurring", "99999999999999999999999999999999", "signal-recurring-3"],
			],
		);
	});

	it("rolls back occurrence, schedule, and clock state when an advance exceeds a host bound", async () => {
		const schedules = createService();
		await schedules.create(schedule("recurring", "10", { intervalTicks: "10", maximumOccurrences: "3" }));
		const request = advance("advance-overflow", "0", "35", timestamp(1));
		await expect(schedules.advance(request, 2)).rejects.toBeInstanceOf(ScheduleAdvanceOverflowError);
		expect(await schedules.getSchedule(scheduleKey("recurring"))).toMatchObject({
			revision: "1",
			occurrences: "0",
			nextDueTick: "10",
		});
		expect((await schedules.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual([]);

		const generated = await schedules.advance(request, 3);
		expect(generated.occurrences).toHaveLength(3);
		await expect(schedules.advance(request, 2)).rejects.toBeInstanceOf(ScheduleAdvanceOverflowError);
		expect(await schedules.getSchedule(scheduleKey("recurring"))).toMatchObject({
			status: "completed",
			revision: "2",
			occurrences: "3",
		});
		await expect(schedules.advance({ ...request, toInclusive: gameTime("36") }, 3)).rejects.toBeInstanceOf(
			ScheduleAdvanceConflictError,
		);
		await expect(schedules.advance(advance("advance-2", "34", "40", timestamp(2)), 3)).rejects.toBeInstanceOf(
			ScheduleClockConflictError,
		);
	});

	it("fails closed on missed due time and pending-outbox capacity without consuming the clock", async () => {
		const missed = createService();
		await missed.create(schedule("past", "5"));
		await expect(missed.advance(advance("missed", "10", "20", timestamp(1)), 8)).rejects.toBeInstanceOf(
			ScheduleMissedOccurrenceError,
		);
		expect((await missed.advance(advance("valid", "0", "5", timestamp(2)), 8)).occurrences).toHaveLength(1);

		const boundedLimits = { ...scheduleLimits, maxPendingOccurrencesPerTimelineGeneration: 2 };
		const bounded = createService(boundedLimits);
		await bounded.create(schedule("bounded", "10", { intervalTicks: "10" }));
		await expect(bounded.advance(advance("capacity", "0", "30", timestamp(1)), 3)).rejects.toMatchObject({
			constructor: ScheduleCapacityError,
			code: "pending_occurrences",
		});
		expect((await bounded.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual([]);
		expect(await bounded.getSchedule(scheduleKey("bounded"))).toMatchObject({ revision: "1", nextDueTick: "10" });
	});

	it("reclaims expired leases, fences stale settlers, and makes successful completion replay-safe", async () => {
		const tokens = ["lease-old", "lease-new", "lease-final"];
		const schedules = createService(scheduleLimits, () => tokens.shift() ?? "lease-extra");
		await schedules.create(schedule("once", "10"));
		await schedules.advance(advance("advance", "0", "10", timestamp(1)), 1);
		const claim = (now: number, expires: number) =>
			schedules.claimOccurrences({
				...generationKey(),
				leaseOwnerId: "worker",
				operationalNow: timestamp(now),
				leaseExpiresAt: timestamp(expires),
				limit: 1,
			});
		const [first] = await claim(2, 4);
		expect(await claim(3, 5)).toEqual([]);
		if (first === undefined) throw new Error("Expected the first leased occurrence.");
		await expect(
			schedules.abandonOccurrence(occurrenceKey(first.occurrence.occurrenceKey), first.leaseToken, timestamp(4)),
		).rejects.toBeInstanceOf(ScheduleOccurrenceLeaseConflictError);
		const [reclaimed] = await claim(4, 6);
		expect(reclaimed).toMatchObject({ occurrence: { attempt: "2" }, leaseToken: "lease-new" });
		if (reclaimed === undefined) throw new Error("Expected a reclaimed occurrence.");
		const key = occurrenceKey(reclaimed.occurrence.occurrenceKey);
		await expect(schedules.completeOccurrence(key, first.leaseToken, timestamp(5))).rejects.toBeInstanceOf(
			ScheduleOccurrenceLeaseConflictError,
		);
		const completed = await schedules.completeOccurrence(key, reclaimed.leaseToken, timestamp(5));
		expect(completed).toMatchObject({ status: "completed", revision: "4", completedAt: timestamp(5) });
		expect(await schedules.completeOccurrence(key, reclaimed.leaseToken, timestamp(1))).toEqual(completed);

		expect(await schedules.purgeTerminalOccurrences(generationKey(), timestamp(6), 1)).toEqual([
			reclaimed.occurrence.occurrenceKey,
		]);
		expect(await schedules.purgeTerminalSchedules(generationKey(), timestamp(6), 1)).toHaveLength(1);
		expect(await schedules.purgeAdvances(generationKey(), timestamp(6), 1)).toHaveLength(1);
	});

	it("freezes host limits and rejects identity, filter, capacity, and cancellation ambiguity", async () => {
		const configured = {
			...scheduleLimits,
			maxIdentifierBytes: 16,
			maxTickDigits: 3,
			maxActiveSchedulesPerTimelineGeneration: 1,
		};
		const store = new InMemoryScheduleStore(configured);
		const schedules = new ScheduleService(store, configured);
		configured.maxActiveSchedulesPerTimelineGeneration = 8;
		expect(Object.isFrozen(schedules.limits)).toBe(true);
		expect(Object.isFrozen(store.limits)).toBe(true);
		expect(schedules.limits.maxActiveSchedulesPerTimelineGeneration).toBe(1);
		const created = await schedules.create(schedule("first", "10"));
		await expect(schedules.create(schedule("second", "20"))).rejects.toMatchObject({
			constructor: ScheduleCapacityError,
			code: "active_schedules",
		});
		await expect(schedules.create({ ...schedule("first", "10"), kind: "changed" })).rejects.toBeInstanceOf(
			ScheduleConflictError,
		);
		await expect(schedules.listSchedules({ ...generationKey(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
		await expect(schedules.listOccurrences({ ...generationKey(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
		await expect(schedules.create(schedule("this-id-is-far-too-long", "10"))).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
		await expect(schedules.create(schedule("tick", "1000"))).rejects.toBeInstanceOf(ScheduleValidationError);
		await expect(schedules.create(schedule("one", "10", { maximumOccurrences: "2" }))).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
		await expect(schedules.cancelSchedule(scheduleKey("first"), "2", timestamp(1))).rejects.toBeInstanceOf(
			ScheduleRevisionConflictError,
		);
		expect(await schedules.cancelSchedule(scheduleKey("first"), created.revision, timestamp(1))).toMatchObject({
			status: "cancelled",
			revision: "2",
		});
		expect(
			await schedules.getScheduleByKey({ ...generationKey(), ownerId: "owner-2", scheduleKey: created.scheduleKey }),
		).toBeUndefined();
	});

	it("keeps schedule occurrences isolated by rewind generation and actor claim filters", async () => {
		const schedules = createService();
		await schedules.create(schedule("actor-1", "10"));
		await schedules.create(schedule("actor-2", "10", { partition: partition("actor-2") }));
		await schedules.create(
			schedule("generation-2", "10", {
				partition: { ...partition(), generation: "2" },
				dueTime: { ...gameTime("10"), generation: "2" },
			}),
		);
		await schedules.advance(advance("advance-1", "0", "10", timestamp(1)), 8);
		expect(
			await schedules.claimOccurrences({
				...generationKey(),
				actorId: "actor-2",
				leaseOwnerId: "worker",
				operationalNow: timestamp(2),
				leaseExpiresAt: timestamp(3),
				limit: 8,
			}),
		).toHaveLength(1);
		expect((await schedules.listOccurrences({ ...generationKey(), limit: 8 })).records).toHaveLength(2);
		expect((await schedules.listOccurrences({ ...generationKey(), generation: "2", limit: 8 })).records).toEqual([]);
	});

	it("rejects invalid generated identifiers before mutating fixture state", async () => {
		const invalidSignal = new ScheduleService(
			new InMemoryScheduleStore(scheduleLimits, undefined, () => "bad\nsignal"),
			scheduleLimits,
		);
		await invalidSignal.create(schedule("invalid-signal", "10"));
		await expect(invalidSignal.advance(advance("advance", "0", "10", timestamp(1)), 1)).rejects.toBeInstanceOf(
			ScheduleValidationError,
		);
		expect((await invalidSignal.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual([]);

		const invalidLease = new ScheduleService(
			new InMemoryScheduleStore(
				scheduleLimits,
				() => "bad\nlease",
				() => "valid-signal",
			),
			scheduleLimits,
		);
		await invalidLease.create(schedule("invalid-lease", "10"));
		await invalidLease.advance(advance("advance", "0", "10", timestamp(1)), 1);
		await expect(
			invalidLease.claimOccurrences({
				...generationKey(),
				leaseOwnerId: "worker",
				operationalNow: timestamp(2),
				leaseExpiresAt: timestamp(3),
				limit: 1,
			}),
		).rejects.toBeInstanceOf(ScheduleValidationError);
		expect((await invalidLease.listOccurrences({ ...generationKey(), limit: 8 })).records[0]).toMatchObject({
			revision: "1",
			attempt: "0",
		});
	});
});

function createService(limits = scheduleLimits, token?: () => string, signalId?: () => string): ScheduleService {
	return new ScheduleService(new InMemoryScheduleStore(limits, token, signalId), limits);
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

function gameTime(tick: string) {
	return { timelineId: "timeline-1", generation: "1", tick };
}

function scheduleKey(scheduleId: string) {
	return { ...generationKey(), scheduleId };
}

function occurrenceKey(occurrenceKeyValue: bigint) {
	return { ...generationKey(), occurrenceKey: occurrenceKeyValue };
}

function timestamp(second: number): string {
	return `2026-08-28T00:00:${second.toString().padStart(2, "0")}.000Z`;
}
