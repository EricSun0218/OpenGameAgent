import {
	InMemoryScheduleStore,
	ScheduleAdvanceOverflowError,
	ScheduleCapacityError,
	ScheduleService,
	ScheduleValidationError,
	type GameScheduleDraft,
	type ScheduleLimits,
} from "@opengameagent/extensions";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteScheduleStore } from "../src/index.js";

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

const firstDueTick = "9007199254740993";
const collisionTick = "9007199254740995";
const nextRecurringTick = "9007199254740997";

afterEach(() => {
	for (const authority of authorities.splice(0)) authority.close();
});

describe("schedule priority Store conformance", () => {
	it.each(["memory", "sqlite"] as const)(
		"orders %s occurrences by canonical tick then stable schedule identity",
		async (backend) => {
			const schedules = createService(backend, scheduleLimits);
			await createCollisionScenario(schedules);
			const request = advance("priority-advance", "9007199254740992", collisionTick, timestamp(1));

			const materialized = await schedules.advance(request, 4);
			expect(materialized.occurrences.map(occurrenceIdentity)).toEqual([
				[1n, "recurring-first", "1", firstDueTick],
				[2n, "same-tick-second", "1", firstDueTick],
				[1n, "recurring-first", "2", collisionTick],
				[3n, "collision-third", "1", collisionTick],
			]);
			expect(await schedules.advance(request, 4)).toEqual(materialized);
			expect((await schedules.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual(
				materialized.occurrences,
			);
			expect(await finalScheduleState(schedules)).toEqual([
				[1n, "recurring-first", "active", "2", "2", nextRecurringTick],
				[2n, "same-tick-second", "completed", "2", "1", undefined],
				[3n, "collision-third", "completed", "2", "1", undefined],
			]);
		},
	);

	it.each(["memory", "sqlite"] as const)(
		"rolls back %s priority planning on generated and pending occurrence bounds",
		async (backend) => {
			const limits = { ...scheduleLimits, maxPendingOccurrencesPerTimelineGeneration: 3 };
			const schedules = createService(backend, limits);
			await createCollisionScenario(schedules);
			const request = advance("bounded-priority-advance", "9007199254740992", collisionTick, timestamp(1));

			await expect(schedules.advance(request, 3)).rejects.toBeInstanceOf(ScheduleAdvanceOverflowError);
			await expect(schedules.advance(request, 4)).rejects.toMatchObject({
				constructor: ScheduleCapacityError,
				code: "pending_occurrences",
			});
			expect((await schedules.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual([]);
			expect(await finalScheduleState(schedules)).toEqual([
				[1n, "recurring-first", "active", "1", "0", firstDueTick],
				[2n, "same-tick-second", "active", "1", "0", firstDueTick],
				[3n, "collision-third", "active", "1", "0", collisionTick],
			]);

			const clockProbe = advance("rollback-clock-probe", "0", "9007199254740992", timestamp(2));
			const probed = await schedules.advance(clockProbe, 4);
			expect(probed.occurrences).toEqual([]);
			expect(await schedules.advance(clockProbe, 4)).toEqual(probed);
		},
	);

	it.each(["memory", "sqlite"] as const)(
		"fails fast and retries %s planning after a generated signal ID is invalid",
		async (backend) => {
			let signalIdCalls = 0;
			const schedules = createService(backend, scheduleLimits, () => {
				signalIdCalls += 1;
				return signalIdCalls === 1 ? "bad\nsignal" : `signal-${signalIdCalls}`;
			});
			for (const scheduleId of ["retry-first", "retry-second", "retry-third"]) {
				await schedules.create(schedule(scheduleId, "10"));
			}
			const request = advance("retry-invalid-signal-id", "0", "10", timestamp(1));

			await expect(schedules.advance(request, 3)).rejects.toBeInstanceOf(ScheduleValidationError);
			expect(signalIdCalls).toBe(1);
			expect((await schedules.listOccurrences({ ...generationKey(), limit: 8 })).records).toEqual([]);
			for (const scheduleId of ["retry-first", "retry-second", "retry-third"]) {
				expect(await schedules.getSchedule(scheduleKey(scheduleId))).toMatchObject({
					status: "active",
					revision: "1",
					occurrences: "0",
					nextDueTick: "10",
				});
			}

			const retried = await schedules.advance(request, 3);
			expect(signalIdCalls).toBe(4);
			expect(retried.occurrences.map(({ scheduleId, signalId }) => [scheduleId, signalId])).toEqual([
				["retry-first", "signal-2"],
				["retry-second", "signal-3"],
				["retry-third", "signal-4"],
			]);
		},
	);
});

function createService(
	backend: "memory" | "sqlite",
	limits: ScheduleLimits,
	providedSignalId?: () => string,
): ScheduleService {
	let signalOrdinal = 0;
	const newSignalId = providedSignalId ?? (() => `signal-${++signalOrdinal}`);
	if (backend === "memory") {
		return new ScheduleService(new InMemoryScheduleStore(limits, undefined, newSignalId), limits);
	}
	const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
	authorities.push(authority);
	return new ScheduleService(new SqliteScheduleStore(authority, limits, undefined, newSignalId), limits);
}

async function createCollisionScenario(schedules: ScheduleService): Promise<void> {
	await schedules.create(schedule("recurring-first", firstDueTick, { intervalTicks: "2", maximumOccurrences: "3" }));
	await schedules.create(schedule("same-tick-second", firstDueTick));
	await schedules.create(schedule("collision-third", collisionTick));
}

function occurrenceIdentity(occurrence: {
	readonly scheduleKey: bigint;
	readonly scheduleId: string;
	readonly ordinal: string;
	readonly dueTime: { readonly tick: string };
}) {
	return [occurrence.scheduleKey, occurrence.scheduleId, occurrence.ordinal, occurrence.dueTime.tick] as const;
}

async function finalScheduleState(schedules: ScheduleService) {
	const records = await Promise.all(
		["recurring-first", "same-tick-second", "collision-third"].map((scheduleId) =>
			schedules.getSchedule(scheduleKey(scheduleId)),
		),
	);
	return records.map((record) => {
		if (record === undefined) throw new Error("Expected the conformance schedule to remain readable.");
		return [
			record.scheduleKey,
			record.scheduleId,
			record.status,
			record.revision,
			record.occurrences,
			record.nextDueTick,
		] as const;
	});
}

function partition() {
	return {
		ownerId: "owner-1",
		timelineId: "timeline-1",
		generation: "1",
		sessionId: "session-1",
		actorId: "actor-1",
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

function timestamp(second: number): string {
	return `2026-08-31T00:00:${second.toString().padStart(2, "0")}.000Z`;
}
