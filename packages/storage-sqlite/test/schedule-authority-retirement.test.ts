import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
	InMemoryScheduleStore,
	InMemorySignalStore,
	ScheduledSignalDispatcher,
	type ScheduleLimits,
	ScheduleService,
	type ScheduleStore,
	SignalService,
} from "@opengameagent/extensions";
import { describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteScheduleStore, SqliteSignalStore } from "../src/index.js";

const limits: ScheduleLimits = {
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
const lane = { ownerId: "owner", timelineId: "world", generation: "1", sessionId: "chat0", actorId: "actor" };
const generation = { ownerId: lane.ownerId, timelineId: lane.timelineId, generation: lane.generation };
const time = (second: number) => `2026-09-05T00:00:${String(second).padStart(2, "0")}.000Z`;
const retirement = { retirementId: "retire-lane", partition: lane, recordedAt: time(3) };
const gameTime = (tick: string) => ({ timelineId: lane.timelineId, generation: lane.generation, tick });
const draft = (scheduleId: string, tick: string, partition = lane) => ({
	scheduleId,
	partition,
	kind: "timer",
	payload: {},
	dueTime: { ...gameTime(tick), timelineId: partition.timelineId, generation: partition.generation },
	subjectIds: [],
	causeIds: [],
	recordedAt: time(0),
});
const advance = (advanceId: string, from: string, to: string) => ({
	advanceId,
	partition: generation,
	fromExclusive: gameTime(from),
	toInclusive: gameTime(to),
	recordedAt: time(1),
});
const claim = { ...lane, leaseOwnerId: "worker", operationalNow: time(2), leaseExpiresAt: time(10), limit: 1 };

describe.each(["memory", "sqlite"] as const)("%s Schedule authority retirement", (kind) => {
	it.each([
		["listSchedules", "store", "limit"],
		["listSchedules", "store", "cursor"],
		["listOccurrences", "store", "limit"],
		["listOccurrences", "store", "cursor"],
		["listSchedules", "service", "limit"],
		["listSchedules", "service", "cursor"],
		["listOccurrences", "service", "limit"],
		["listOccurrences", "service", "cursor"],
	] as const)("captures %s %s %s before reading a bounded page", async (method, surface, changing) => {
		const authority = open(":memory:");
		try {
			const bounded = { ...limits, maxSchedulePageSize: 1, maxOccurrencePageSize: 1 };
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(bounded) : new SqliteScheduleStore(authority, bounded);
			await store.create(draft("first", "1"), 8);
			await store.create(draft("second", "2"), 8);
			await store.advance(advance("first", "0", "2"), 8, 8);
			const target = surface === "store" ? store : new ScheduleService(store, bounded);
			let limitReads = 0;
			let cursorReads = 0;
			const cursor = () => (++cursorReads === 1 || changing !== "cursor" ? 0n : 99n);
			const request = {
				...lane,
				get limit() {
					return ++limitReads === 1 || changing !== "limit" ? 1 : 8;
				},
				get afterScheduleKey() {
					return cursor();
				},
				get afterOccurrenceKey() {
					return cursor();
				},
			};
			const page = await target[method](request);
			expect(page.records.map((record) => record.scheduleId)).toEqual(["first"]);
			expect(limitReads).toBe(1);
			expect(cursorReads).toBe(1);
		} finally {
			authority.close();
		}
	});

	it("captures draft, clock, claim, and purge fields without rereading caller getters", async () => {
		const authority = open(":memory:");
		try {
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
			const once = <T extends object>(value: T): T => {
				const reads = new Set<PropertyKey>();
				return new Proxy(value, {
					get(target, property, receiver) {
						if (reads.has(property)) throw new Error(`Repeated caller field read: ${String(property)}`);
						reads.add(property);
						return Reflect.get(target, property, receiver);
					},
				});
			};
			await store.create(once({ ...draft("due", "2"), partition: once(lane), dueTime: once(gameTime("2")) }), 8);
			await store.advance(
				once({
					...advance("first", "0", "2"),
					partition: once(lane),
					fromExclusive: once(gameTime("0")),
					toInclusive: once(gameTime("2")),
				}),
				8,
				8,
			);
			const [delivery] = await store.claimOccurrences(once(claim));
			if (!delivery) throw new Error("Expected delivery.");
			await store.completeOccurrence(
				{ ...generation, occurrenceKey: delivery.occurrence.occurrenceKey },
				delivery.leaseToken,
				time(3),
			);
			expect(await store.purgeTerminalOccurrences(once(lane), time(5), 8)).toEqual([delivery.occurrence.occurrenceKey]);
			expect(await store.purgeTerminalSchedules(once(lane), time(5), 8)).toHaveLength(1);
			expect(await store.purgeAdvances(once(lane), time(5), 8)).toHaveLength(1);
		} finally {
			authority.close();
		}
	});

	it.each(["cancelSchedule", "completeOccurrence", "abandonOccurrence"] as const)(
		"captures %s key getters once before entering storage",
		async (method) => {
			const authority = open(":memory:");
			try {
				const store: ScheduleStore =
					kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
				let reads = 0;
				let nested: Promise<unknown> | undefined;
				const read = () => {
					if (++reads === 2) nested = store.beginAuthorityRetirement(retirement);
				};
				if (method === "cancelSchedule") {
					const key = {
						...generation,
						get scheduleId() {
							read();
							return "missing";
						},
					};
					await expect(store.cancelSchedule(key, "1", time(2))).rejects.toMatchObject({
						name: "ScheduleNotFoundError",
					});
				} else {
					const key = {
						...generation,
						get occurrenceKey() {
							read();
							return 99n;
						},
					};
					await expect(store[method](key, "token", time(2))).rejects.toMatchObject({
						name: "ScheduleOccurrenceNotFoundError",
					});
				}
				if (nested !== undefined) {
					await nested;
					expect(await store.readAuthorityRetirement(lane)).toMatchObject({ phase: "fenced" });
				}
				expect(reads).toBe(1);
			} finally {
				authority.close();
			}
		},
	);

	it("preserves a lease claimed by a reentrant identifier callback", async () => {
		const authority = open(":memory:");
		try {
			let store: ScheduleStore;
			let armed = false;
			let nested: ReturnType<ScheduleStore["claimOccurrences"]> | undefined;
			const token = () => {
				if (!armed) return "inner-token";
				armed = false;
				nested = store.claimOccurrences(claim);
				return "outer-token";
			};
			store =
				kind === "memory"
					? new InMemoryScheduleStore(limits, token)
					: new SqliteScheduleStore(authority, limits, token);
			await store.create(draft("due", "2"), 8);
			await store.advance(advance("first", "0", "2"), 8, 8);
			armed = true;
			await expect(store.claimOccurrences(claim)).rejects.toMatchObject({
				name: "ScheduleOccurrenceLeaseConflictError",
			});
			const [delivery] = (await nested) ?? [];
			if (!delivery) throw new Error("Expected nested delivery.");
			expect(delivery).toMatchObject({ leaseToken: "inner-token", occurrence: { attempt: "1" } });
			expect(
				await store.completeOccurrence(
					{ ...generation, occurrenceKey: delivery.occurrence.occurrenceKey },
					delivery.leaseToken,
					time(3),
				),
			).toMatchObject({ status: "completed", attempt: "1" });
		} finally {
			authority.close();
		}
	});

	it("preserves cancellation committed by a reentrant signal identifier callback", async () => {
		const authority = open(":memory:");
		try {
			let store: ScheduleStore;
			let generated = 0;
			let nested: ReturnType<ScheduleStore["cancelSchedule"]> | undefined;
			const signalId = () => {
				if (++generated === 2) nested = store.cancelSchedule({ ...generation, scheduleId: "later" }, "1", time(2));
				return `signal-${generated}`;
			};
			store =
				kind === "memory"
					? new InMemoryScheduleStore(limits, undefined, signalId)
					: new SqliteScheduleStore(authority, limits, undefined, signalId);
			await store.create(draft("earlier", "1"), 8);
			await store.create(draft("later", "2"), 8);
			await expect(store.advance(advance("first", "0", "2"), 8, 8)).rejects.toMatchObject({
				name: "ScheduleRevisionConflictError",
			});
			expect(await nested).toMatchObject({ status: "cancelled", updatedAt: time(2) });
			expect(await store.getSchedule({ ...generation, scheduleId: "later" })).toMatchObject({
				status: "cancelled",
				updatedAt: time(2),
			});
			expect(await store.getSchedule({ ...generation, scheduleId: "earlier" })).toMatchObject({
				status: "active",
				revision: "1",
			});
			expect((await store.listOccurrences({ ...generation, limit: 8 })).records).toEqual([]);
		} finally {
			authority.close();
		}
	});

	it("preserves the generation clock advanced by a reentrant signal identifier callback", async () => {
		const authority = open(":memory:");
		try {
			let store: ScheduleStore;
			let armed = true;
			let nested: ReturnType<ScheduleStore["advance"]> | undefined;
			const signalId = () => {
				if (armed) {
					armed = false;
					nested = store.advance(advance("nested", "0", "1"), 8, 8);
				}
				return "signal-id";
			};
			store =
				kind === "memory"
					? new InMemoryScheduleStore(limits, undefined, signalId)
					: new SqliteScheduleStore(authority, limits, undefined, signalId);
			await store.create(draft("due", "2"), 8);
			await expect(store.advance(advance("outer", "0", "2"), 8, 8)).rejects.toMatchObject({
				name: "ScheduleClockConflictError",
			});
			expect(await nested).toMatchObject({ advance: { toInclusive: { tick: "1" } }, occurrences: [] });
			expect((await store.advance(advance("next", "1", "2"), 8, 8)).occurrences).toHaveLength(1);
		} finally {
			authority.close();
		}
	});

	it.each(["beginAuthorityRetirement", "finalizeAuthorityRetirement"] as const)(
		"captures %s input getters before mutation",
		async (method) => {
			const authority = open(":memory:");
			try {
				const store: ScheduleStore =
					kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
				await store.create(draft("future", "10"), 8);
				let nested: Promise<unknown> | undefined;
				const input = {
					...retirement,
					retirementId: "outer",
					get recordedAt() {
						nested = store.beginAuthorityRetirement(retirement);
						return "invalid";
					},
				};
				await expect(store[method](input)).rejects.toBeInstanceOf(TypeError);
				expect(await nested).toMatchObject({ phase: "fenced" });
				expect(await store.readAuthorityRetirement(lane)).toMatchObject({ phase: "fenced" });
				expect(await store.getSchedule({ ...generation, scheduleId: "future" })).toMatchObject({ status: "cancelled" });
			} finally {
				authority.close();
			}
		},
	);

	it.each(["retired", "retired changed actor", "changed actor", "changed payload"])(
		"refuses %s dispatch before recording a signal",
		async (scenario) => {
			const authority = open(":memory:");
			try {
				const store: ScheduleStore =
					kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
				const signalLimits = {
					maxPayloadBytes: 4096,
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
				const signals = new SignalService(
					kind === "memory" ? new InMemorySignalStore() : new SqliteSignalStore(authority, signalLimits),
					signalLimits,
				);
				const dispatcher = new ScheduledSignalDispatcher(new ScheduleService(store, limits), signals);
				await store.create(draft("due", "2"), 8);
				await store.advance(advance("first", "0", "2"), 8, 8);
				const [delivery] = await store.claimOccurrences(claim);
				if (!delivery) throw new Error("Expected a delivery.");
				const retired = scenario.startsWith("retired");
				if (retired) await store.beginAuthorityRetirement(retirement);
				const supplied = {
					...delivery,
					occurrence: {
						...delivery.occurrence,
						...(scenario.includes("changed actor")
							? { partition: { ...delivery.occurrence.partition, actorId: "peer" } }
							: {}),
						...(scenario.includes("changed payload") ? { payload: { altered: true } } : {}),
					},
				};
				await expect(dispatcher.dispatch(supplied, time(4))).rejects.toMatchObject({
					name: retired ? "WorkAuthorityRetiredError" : "ScheduleOccurrenceLeaseConflictError",
				});
				expect((await signals.listSignals({ ...generation, limit: 8 })).records).toEqual([]);
			} finally {
				authority.close();
			}
		},
	);

	it("keeps every differing authority-lane field isolated", async () => {
		const authority = open(":memory:");
		try {
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
			const peers = ["ownerId", "timelineId", "generation", "sessionId", "actorId"].map((field) => ({
				...lane,
				[field]: field === "generation" ? "2" : "peer",
			}));
			await store.create(draft("target", "10"), 8);
			for (const [index, peer] of peers.entries()) await store.create(draft(`peer-${index}`, "10", peer), 8);
			await store.beginAuthorityRetirement(retirement);
			await store.finalizeAuthorityRetirement(retirement);
			for (const [index, peer] of peers.entries()) {
				expect(await store.getSchedule({ ...peer, scheduleId: `peer-${index}` })).toMatchObject({ status: "active" });
				expect(await store.readAuthorityRetirement(peer)).toBeUndefined();
			}
		} finally {
			authority.close();
		}
	});

	it("keeps cancellation timestamps monotonic while finalizing leases with an earlier fixed retirement time", async () => {
		const authority = open(":memory:");
		try {
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
			await store.create(draft("late-lease", "2"), 8);
			await store.advance(advance("first", "0", "2"), 8, 8);
			const [delivery] = await store.claimOccurrences({ ...claim, operationalNow: time(8) });
			if (!delivery) throw new Error("Expected a delivery.");
			await store.beginAuthorityRetirement(retirement);
			await store.finalizeAuthorityRetirement(retirement);
			expect(
				await store.getOccurrence({ ...generation, occurrenceKey: delivery.occurrence.occurrenceKey }),
			).toMatchObject({ status: "cancelled", cancelledAt: time(8), updatedAt: time(8) });
		} finally {
			authority.close();
		}
	});

	it("captures only generation clock fields when advance receives a structurally valid exact lane", async () => {
		const authority = open(":memory:");
		try {
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
			await store.create(draft("own", "2"), 8);
			await store.create(draft("peer", "2", { ...lane, actorId: "peer" }), 8);
			const request = { ...advance("first", "0", "2"), partition: lane };
			const result = await store.advance(request, 8, 8);
			expect(result.advance.partition).toEqual(generation);
			expect(result.occurrences).toHaveLength(2);
			expect(await store.advance(request, 8, 8)).toEqual(result);
		} finally {
			authority.close();
		}
	});

	it("leaves earlier peer plans unchanged when an identifier callback fences a later lane", async () => {
		const authority = open(":memory:");
		try {
			let store: ScheduleStore;
			let generated = 0;
			let pending: Promise<unknown> | undefined;
			const signalId = () => {
				if (++generated === 2) pending = store.beginAuthorityRetirement(retirement);
				return `generated-${generated}`;
			};
			store =
				kind === "memory"
					? new InMemoryScheduleStore(limits, undefined, signalId)
					: new SqliteScheduleStore(authority, limits, undefined, signalId);
			await store.create(draft("peer", "1", { ...lane, actorId: "peer" }), 8);
			await store.create(draft("retired", "2"), 8);
			await expect(store.advance(advance("first", "0", "2"), 8, 8)).rejects.toMatchObject({
				name: "WorkAuthorityRetiredError",
			});
			await pending;
			expect(await store.getSchedule({ ...generation, scheduleId: "peer" })).toMatchObject({
				status: "active",
				revision: "1",
				occurrences: "0",
			});
			expect((await store.listOccurrences({ ...generation, limit: 8 })).records).toEqual([]);
		} finally {
			authority.close();
		}
	});

	it.each(["signal", "lease"] as const)(
		"preserves retirement reentered by a host %s identifier callback",
		async (callback) => {
			const authority = open(":memory:");
			try {
				let store: ScheduleStore;
				let armed = false;
				let pending: Promise<unknown> | undefined;
				const generated = () => {
					if (armed) {
						armed = false;
						pending = store.beginAuthorityRetirement(retirement);
					}
					return "generated-identifier";
				};
				store =
					kind === "memory"
						? new InMemoryScheduleStore(
								limits,
								callback === "lease" ? generated : undefined,
								callback === "signal" ? generated : undefined,
							)
						: new SqliteScheduleStore(
								authority,
								limits,
								callback === "lease" ? generated : undefined,
								callback === "signal" ? generated : undefined,
							);
				await store.create(draft("due", "2"), 8);
				if (callback === "lease") await store.advance(advance("first", "0", "2"), 8, 8);
				armed = true;
				await expect(
					callback === "signal" ? store.advance(advance("first", "0", "2"), 8, 8) : store.claimOccurrences(claim),
				).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
				expect(await pending).toMatchObject({ phase: "fenced" });
				expect(await store.readAuthorityRetirement(lane)).toMatchObject({ phase: "fenced" });
				expect((await store.listOccurrences({ ...lane, statuses: ["pending"], limit: 8 })).records).toEqual([]);
			} finally {
				authority.close();
			}
		},
	);

	it("precomputes all retirement revisions before committing either phase", async () => {
		const authority = open(":memory:");
		try {
			const bounded = { ...limits, maxCounterDigits: 1 };
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(bounded) : new SqliteScheduleStore(authority, bounded);
			await store.create(draft("future", "20"), 8);
			await store.create(draft("busy", "2"), 8);
			await store.advance(advance("first", "0", "2"), 8, 8);
			let delivery = (await store.claimOccurrences(claim))[0];
			if (!delivery) throw new Error("Expected a delivery.");
			const key = { ...generation, occurrenceKey: delivery.occurrence.occurrenceKey };
			for (let attempt = 0; attempt < 3; attempt++) {
				await store.abandonOccurrence(key, delivery.leaseToken, time(2));
				delivery = (await store.claimOccurrences(claim))[0];
				if (!delivery) throw new Error("Expected a delivery.");
			}
			await store.abandonOccurrence(key, delivery.leaseToken, time(2));
			expect(await store.getOccurrence(key)).toMatchObject({ revision: "9", status: "pending" });
			await expect(store.beginAuthorityRetirement(retirement)).rejects.toMatchObject({
				code: "schedule_revision_overflow",
			});
			expect(await store.readAuthorityRetirement(lane)).toBeUndefined();
			expect(await store.getSchedule({ ...generation, scheduleId: "future" })).toMatchObject({
				status: "active",
				revision: "1",
			});
			expect(await store.getOccurrence(key)).toMatchObject({ revision: "9", status: "pending" });
		} finally {
			authority.close();
		}
	});

	it("fences exactly one lane, preserves leased evidence until finalize, then permits terminal purge", async () => {
		const authority = open(":memory:");
		try {
			const store = kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
			const service = new ScheduleService(store, limits);
			await service.create(draft("future", "10"));
			await service.create(draft("leased", "2"));
			await service.create(draft("pending", "3"));
			await service.create(draft("peer", "10", { ...lane, actorId: "peer" }));
			await service.create(draft("new-generation", "10", { ...lane, generation: "2" }));
			const accepted = await service.advance(advance("first", "0", "3"), 8);
			const [leased] = await service.claimOccurrences(claim);
			if (!leased) throw new Error("Expected a leased occurrence.");
			const key = { ...generation, occurrenceKey: leased.occurrence.occurrenceKey };
			const begun = await service.beginAuthorityRetirement(retirement);
			expect(begun).toEqual({ ...retirement, phase: "fenced", cancelledScheduleCount: 1, cancelledOccurrenceCount: 1 });
			expect(await service.beginAuthorityRetirement(retirement)).toEqual(begun);
			expect(await service.getOccurrence(key)).toEqual(leased.occurrence);
			expect((await service.listOccurrences({ ...lane, statuses: ["cancelled"], limit: 8 })).records).toHaveLength(1);
			await expect(service.create(draft("late", "10"))).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
			await expect(service.claimOccurrences(claim)).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
			await expect(service.completeOccurrence(key, leased.leaseToken, time(4))).rejects.toMatchObject({
				name: "WorkAuthorityRetiredError",
			});
			await expect(service.abandonOccurrence(key, leased.leaseToken, time(4))).rejects.toMatchObject({
				name: "WorkAuthorityRetiredError",
			});
			const later = await service.advance(advance("second", "3", "10"), 8);
			expect(later.occurrences.map((entry) => entry.scheduleId)).toEqual(["peer"]);
			expect(
				await service.claimOccurrences({
					...generation,
					leaseOwnerId: "peer-worker",
					operationalNow: time(11),
					leaseExpiresAt: time(12),
					limit: 8,
				}),
			).toHaveLength(1);
			const finished = await service.finalizeAuthorityRetirement(retirement);
			expect(finished).toEqual({ ...begun, phase: "finalized", cancelledOccurrenceCount: 2 });
			expect(await service.finalizeAuthorityRetirement(retirement)).toEqual(finished);
			expect(await service.beginAuthorityRetirement(retirement)).toEqual(finished);
			expect(await service.getOccurrence(key)).toMatchObject({ status: "cancelled", cancelledAt: time(3) });
			expect(await service.purgeTerminalOccurrences(generation, time(20), 8)).toEqual(
				accepted.occurrences.map((entry) => entry.occurrenceKey),
			);
			expect(await service.purgeTerminalSchedules(generation, time(20), 8)).toHaveLength(3);
			expect(await service.purgeAdvances(generation, time(20), 8)).toHaveLength(1);
			expect(await service.readAuthorityRetirement(lane)).toEqual(finished);
			await expect(service.create(draft("future", "20"))).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
			expect(
				(await service.getSchedule({ ...generation, generation: "2", scheduleId: "new-generation" }))?.status,
			).toBe("active");
		} finally {
			authority.close();
		}
	});

	it("requires exact identity on both phases and rejects incomplete lanes before changing records", async () => {
		const authority = open(":memory:");
		try {
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
			await store.create(draft("future", "10"), 8);
			await expect(store.finalizeAuthorityRetirement(retirement)).rejects.toMatchObject({
				name: "WorkAuthorityRetirementConflictError",
			});
			await expect(
				store.beginAuthorityRetirement({ ...retirement, partition: generation as typeof lane }),
			).rejects.toBeInstanceOf(TypeError);
			expect(await store.readAuthorityRetirement(lane)).toBeUndefined();
			const begun = await store.beginAuthorityRetirement(retirement);
			for (const changed of [
				{ ...retirement, retirementId: "another" },
				{ ...retirement, recordedAt: time(4) },
				{ ...retirement, partition: { ...lane, actorId: "peer" } },
			]) {
				await expect(store.beginAuthorityRetirement(changed)).rejects.toMatchObject({
					name: "WorkAuthorityRetirementConflictError",
				});
				await expect(store.finalizeAuthorityRetirement(changed)).rejects.toMatchObject({
					name: "WorkAuthorityRetirementConflictError",
				});
			}
			expect(await store.readAuthorityRetirement(lane)).toEqual(begun);
		} finally {
			authority.close();
		}
	});

	it("preserves already completed exact-lease replay across retirement", async () => {
		const authority = open(":memory:");
		try {
			const store: ScheduleStore =
				kind === "memory" ? new InMemoryScheduleStore(limits) : new SqliteScheduleStore(authority, limits);
			await store.create(draft("done", "2"), 8);
			await store.advance(advance("first", "0", "2"), 8, 8);
			const [delivery] = await store.claimOccurrences(claim);
			if (!delivery) throw new Error("Expected a leased occurrence.");
			const key = { ...generation, occurrenceKey: delivery.occurrence.occurrenceKey };
			const completed = await store.completeOccurrence(key, delivery.leaseToken, time(2));
			await store.beginAuthorityRetirement(retirement);
			await store.finalizeAuthorityRetirement(retirement);
			expect(await store.completeOccurrence(key, delivery.leaseToken, time(4))).toEqual(completed);
			await expect(store.completeOccurrence(key, "forged", time(4))).rejects.toMatchObject({
				name: "ScheduleOccurrenceLeaseConflictError",
			});
		} finally {
			authority.close();
		}
	});
});

it("restores both retirement phases through new SQLite connections and retains the fence after purge", async () => {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-schedule-retirement-"));
	const path = join(root, "authority.sqlite3");
	let authority = open(path);
	try {
		let store = new SqliteScheduleStore(authority, limits);
		await store.create(draft("leased", "2"), 8);
		await store.advance(advance("first", "0", "2"), 8, 8);
		const [delivery] = await store.claimOccurrences(claim);
		if (!delivery) throw new Error("Expected a leased occurrence.");
		await store.beginAuthorityRetirement(retirement);
		authority.close();
		authority = open(path);
		store = new SqliteScheduleStore(authority, limits);
		expect(await store.readAuthorityRetirement(lane)).toMatchObject({ phase: "fenced", cancelledOccurrenceCount: 0 });
		await expect(
			store.claimOccurrences({ ...claim, operationalNow: time(11), leaseExpiresAt: time(12) }),
		).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		const finished = await store.finalizeAuthorityRetirement(retirement);
		await store.purgeTerminalOccurrences(generation, time(20), 8);
		await store.purgeTerminalSchedules(generation, time(20), 8);
		authority.close();
		authority = open(path);
		store = new SqliteScheduleStore(authority, limits);
		expect(await store.beginAuthorityRetirement(retirement)).toEqual(finished);
		expect(await store.finalizeAuthorityRetirement(retirement)).toEqual(finished);
		await expect(store.create(draft("leased", "20"), 8)).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
	} finally {
		authority.close();
		await rm(root, { recursive: true, force: true });
	}
});

it("requires the owning active SQLite transaction and rolls back composed retirement failures", async () => {
	const authority = open(":memory:");
	const other = open(":memory:");
	try {
		const store = new SqliteScheduleStore(authority, limits);
		await store.create(draft("future", "10"), 8);
		expect(() =>
			authority.read((database) => store.beginAuthorityRetirementInTransaction(database, retirement)),
		).toThrow(TypeError);
		expect(() => other.write((database) => store.beginAuthorityRetirementInTransaction(database, retirement))).toThrow(
			TypeError,
		);
		const failure = new Error("another participant failed");
		expect(() =>
			authority.write((database) => {
				store.beginAuthorityRetirementInTransaction(database, retirement);
				throw failure;
			}),
		).toThrow(failure);
		expect(await store.readAuthorityRetirement(lane)).toBeUndefined();
		expect(await store.getSchedule({ ...generation, scheduleId: "future" })).toMatchObject({
			status: "active",
			revision: "1",
		});
		await store.beginAuthorityRetirement(retirement);
		expect(() =>
			authority.write((database) => {
				store.finalizeAuthorityRetirementInTransaction(database, retirement);
				throw failure;
			}),
		).toThrow(failure);
		expect(await store.readAuthorityRetirement(lane)).toMatchObject({ phase: "fenced" });
	} finally {
		authority.close();
		other.close();
	}
});

function open(path: string): SqliteAuthority {
	return new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
}
