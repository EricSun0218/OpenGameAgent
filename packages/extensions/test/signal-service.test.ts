import {
	GoalEvidenceRejectedError,
	type GoalLimits,
	GoalService,
	InMemoryGoalStore,
	InMemorySignalStore,
	SIGNAL_WAIT_EVIDENCE_KIND,
	type SignalLimits,
	SignalService,
	SignalValidationError,
	SignalWaitFanoutError,
	SignalWaitGoalEvidenceVerifier,
	SignalWaitRevisionConflictError,
} from "@opengameagent/extensions";
import { describe, expect, it } from "vitest";

const limits: SignalLimits = {
	maxPayloadBytes: 4_096,
	maxSubjectsPerSignal: 8,
	maxCausesPerSignal: 8,
	maxSignalFilterKinds: 4,
	maxWaitKinds: 4,
	maxWaitSubjects: 4,
	maxWaitMatchesPerSignal: 2,
	maxSignalPageSize: 4,
	maxWaitPageSize: 4,
	maxExpireBatchSize: 4,
	maxPurgeBatchSize: 4,
};

const goalLimits: GoalLimits = {
	maxObjectiveBytes: 1_024,
	maxStepsPerGoal: 4,
	maxStepBytes: 512,
	maxPlanBytes: 8_192,
	maxRecordBytes: 32_768,
	maxModelResultBytes: 16_384,
	maxEvidencePerStep: 2,
	maxEvidenceKindBytes: 64,
	maxEvidenceReferenceBytes: 256,
	maxOpenGoalsPerOwnerGeneration: 4,
	maxListPageSize: 4,
	maxPurgeBatchSize: 4,
};

describe("SignalService", () => {
	it("matches a durable wait whether the signal arrives before or after registration", async () => {
		const service = createService();
		const early = await service.record(signal("signal-early", "npc.arrived", "2"));
		const immediatelyMatched = await service.registerWait(
			wait("wait-late", { kinds: ["npc.arrived"], afterSignalKey: early.signal.signalKey - 1n }),
		);
		expect(immediatelyMatched).toMatchObject({
			status: "matched",
			revision: "1",
			matchedSignalKey: early.signal.signalKey,
			updatedAt: early.signal.recordedAt,
		});

		const waiting = await service.registerWait(
			wait("wait-first", { kinds: ["chest.opened"], subjectIdsAny: ["chest-1"] }),
		);
		expect(waiting.status).toBe("waiting");
		const recorded = await service.record(signal("signal-late", "chest.opened", "3", ["chest-1"]));
		expect(recorded.matchedWaitIds).toEqual(["wait-first"]);
		expect(await service.getWait(waitKey("wait-first"))).toMatchObject({
			status: "matched",
			revision: "2",
			matchedSignalKey: recorded.signal.signalKey,
		});
	});

	it("fails atomically instead of partially matching excessive wait fanout", async () => {
		const service = createService();
		for (const waitId of ["wait-1", "wait-2", "wait-3"]) {
			await service.registerWait(wait(waitId, { kinds: ["world.changed"] }));
		}
		await expect(service.record(signal("signal-1", "world.changed", "2"))).rejects.toBeInstanceOf(
			SignalWaitFanoutError,
		);
		expect(await service.getSignal(signalKey("signal-1"))).toBeUndefined();
		expect((await service.listWaits({ ...partition(), statuses: ["waiting"], limit: 4 })).records).toHaveLength(3);
	});

	it("bounds late wait fanout and keeps a rejected registration invisible", async () => {
		const service = createService();
		const recorded = await service.record(signal("signal-1", "world.changed", "2"));
		for (const waitId of ["wait-1", "wait-2"]) {
			expect((await service.registerWait(wait(waitId, { kinds: ["world.changed"] }))).status).toBe("matched");
		}
		await expect(service.registerWait(wait("wait-3", { kinds: ["world.changed"] }))).rejects.toBeInstanceOf(
			SignalWaitFanoutError,
		);
		expect(await service.getWait(waitKey("wait-3"))).toBeUndefined();
		expect((await service.record(signal("signal-1", "world.changed", "2"))).matchedWaitIds).toEqual([
			"wait-1",
			"wait-2",
		]);
		expect(recorded.signal.signalKey).toBe(1n);
	});

	it("turns a matched goal-step wait into host-verifiable plan evidence", async () => {
		const signals = createService();
		const goals = new GoalService(new InMemoryGoalStore(goalLimits), new SignalWaitGoalEvidenceVerifier(signals));
		const created = await goals.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "Wait for the gate",
			plan: [{ stepId: "step-1", description: "Reach the gate", status: "in_progress", evidence: [] }],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		await signals.registerWait({
			...wait("wait-goal", { kinds: ["npc.arrived"], subjectIdsAny: ["gate"] }),
			target: { kind: "goal_step", goalId: "goal-1", stepId: "step-1" },
		});
		await signals.record(signal("signal-1", "npc.arrived", "2"));
		const achieved = await goals.update({
			...goalKey("goal-1"),
			expectedRevision: created.revision,
			plan: [
				{
					stepId: "step-1",
					description: "Reach the gate",
					status: "completed",
					evidence: [{ kind: SIGNAL_WAIT_EVIDENCE_KIND, referenceId: "wait-goal" }],
				},
			],
			status: "achieved",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		expect(achieved.status).toBe("achieved");

		const futureGoals = new GoalService(new InMemoryGoalStore(goalLimits), new SignalWaitGoalEvidenceVerifier(signals));
		await futureGoals.create({
			goalId: "goal-future",
			partition: partition(),
			objective: "Reject future proof",
			plan: [{ stepId: "step-future", description: "Future", status: "in_progress", evidence: [] }],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		await signals.registerWait({
			...wait("wait-future", { kinds: ["future.event"] }),
			target: { kind: "goal_step", goalId: "goal-future", stepId: "step-future" },
		});
		await signals.record(signal("signal-future", "future.event", "10"));
		await expect(
			futureGoals.update({
				...goalKey("goal-future"),
				expectedRevision: "1",
				plan: [
					{
						stepId: "step-future",
						description: "Future",
						status: "completed",
						evidence: [{ kind: SIGNAL_WAIT_EVIDENCE_KIND, referenceId: "wait-future" }],
					},
				],
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toBeInstanceOf(GoalEvidenceRejectedError);
	});

	it("isolates selectors, expires bounded horizons, and enforces cancellation CAS", async () => {
		const service = createService();
		await service.registerWait(
			wait("wait-expire", {
				kinds: ["npc.arrived"],
				subjectIdsAny: ["gate"],
				afterTickExclusive: "2",
				throughTickInclusive: "5",
			}),
		);
		await service.record(signal("wrong-subject", "npc.arrived", "3", ["village"]));
		await service.record({
			...signal("wrong-generation", "npc.arrived", "3"),
			partition: { ...partition(), generation: "2" },
			gameTime: { ...gameTime("3"), generation: "2" },
		});
		expect((await service.getWait(waitKey("wait-expire")))?.status).toBe("waiting");
		expect(await service.expireWaits(partition(), "5", "2026-08-28T00:00:02.000Z", 1)).toHaveLength(1);
		expect((await service.getWait(waitKey("wait-expire")))?.status).toBe("expired");

		const cancellable = await service.registerWait(wait("wait-cancel", { kinds: ["never"] }));
		await expect(service.cancelWait(waitKey("wait-cancel"), "0", "2026-08-28T00:00:02.000Z")).rejects.toBeInstanceOf(
			SignalWaitRevisionConflictError,
		);
		expect(
			await service.cancelWait(waitKey("wait-cancel"), cancellable.revision, "2026-08-28T00:00:02.000Z"),
		).toMatchObject({ status: "cancelled", revision: "2" });
	});

	it("binds sequence lookup to its authority partition and keeps update timestamps monotonic", async () => {
		const service = createService();
		const waiting = await service.registerWait({
			...wait("wait-time", { kinds: ["npc.arrived"] }),
			recordedAt: "2026-08-28T00:00:02.000Z",
		});
		const recorded = await service.record({
			...signal("signal-time", "npc.arrived", "2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		expect((await service.getWait(waitKey(waiting.waitId)))?.updatedAt).toBe("2026-08-28T00:00:02.000Z");
		expect(
			await service.getSignalByKey({ ...generationKey(), ownerId: "owner-2", signalKey: recorded.signal.signalKey }),
		).toBeUndefined();
		expect(
			await service.getSignalByKey({ ...generationKey(), generation: "2", signalKey: recorded.signal.signalKey }),
		).toBeUndefined();

		const cancellable = await service.registerWait({
			...wait("wait-cancel-time", { kinds: ["never"] }),
			recordedAt: "2026-08-28T00:00:03.000Z",
		});
		expect(
			await service.cancelWait(waitKey(cancellable.waitId), cancellable.revision, "2026-08-28T00:00:01.000Z"),
		).toMatchObject({ updatedAt: "2026-08-28T00:00:03.000Z" });
	});

	it("freezes independent host limits and rejects malformed signal input before storage", async () => {
		const configured = { ...limits, maxSignalFilterKinds: 1 };
		const service = new SignalService(new InMemorySignalStore(), configured);
		configured.maxSignalFilterKinds = 4;
		expect(Object.isFrozen(service.limits)).toBe(true);
		expect(service.limits.maxSignalFilterKinds).toBe(1);
		await expect(
			service.listSignals({ ...partition(), kinds: ["event.one", "event.two"], limit: 1 }),
		).rejects.toBeInstanceOf(SignalValidationError);
		await expect(service.listWaits({ ...partition(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(
			SignalValidationError,
		);
		await expect(
			service.record({
				...signal("invalid-time", "event.one", "1"),
				recordedAt: "2026-08-28T00:00:00Z",
			}),
		).rejects.toBeInstanceOf(SignalValidationError);
		await expect(
			service.record({
				...signal("wrong-time", "event.one", "1"),
				gameTime: { ...gameTime("1"), generation: "2" },
			}),
		).rejects.toBeInstanceOf(SignalValidationError);
		expect((await service.listSignals({ ...partition(), limit: 1 })).records).toEqual([]);
	});

	it("paginates immutable signals and preserves referenced evidence until terminal waits are purged", async () => {
		const service = createService();
		await service.registerWait(wait("wait-1", { kinds: ["event.one"] }));
		const firstSignal = await service.record(signal("signal-1", "event.one", "1"));
		await service.record(signal("signal-2", "event.two", "2"));
		const first = await service.listSignals({ ...partition(), limit: 1 });
		if (first.nextAfterSignalKey === undefined) throw new Error("Expected a second signal page.");
		const second = await service.listSignals({ ...partition(), afterSignalKey: first.nextAfterSignalKey, limit: 1 });
		expect(first.records.map((record) => record.signalId)).toEqual(["signal-1"]);
		expect(second.records.map((record) => record.signalId)).toEqual(["signal-2"]);

		expect(await service.purgeSignals(partition(), "2026-08-29T00:00:00.000Z", 4)).toHaveLength(1);
		expect(await service.getSignalByKey({ ...generationKey(), signalKey: firstSignal.signal.signalKey })).toBeDefined();
		expect(await service.purgeTerminalWaits(partition(), "2026-08-29T00:00:00.000Z", 4)).toHaveLength(1);
		expect(await service.purgeSignals(partition(), "2026-08-29T00:00:00.000Z", 4)).toEqual([
			firstSignal.signal.signalKey,
		]);
	});
});

function createService(): SignalService {
	return new SignalService(new InMemorySignalStore(), limits);
}

function partition() {
	return {
		ownerId: "owner-1",
		timelineId: "world",
		generation: "1",
		sessionId: "session-1",
		actorId: "actor-1",
	} as const;
}

function gameTime(tick: string) {
	return { timelineId: "world", generation: "1", tick } as const;
}

function signal(signalId: string, kind: string, tick: string, subjectIds: readonly string[] = ["gate"]) {
	return {
		signalId,
		partition: partition(),
		kind,
		payload: { tick },
		gameTime: gameTime(tick),
		subjectIds,
		causeIds: ["host-clock"],
		recordedAt: "2026-08-28T00:00:01.000Z",
	} as const;
}

function wait(waitId: string, selector: Parameters<SignalService["registerWait"]>[0]["selector"]) {
	return {
		waitId,
		partition: partition(),
		selector,
		target: { kind: "correlation", correlationId: waitId },
		recordedAt: "2026-08-28T00:00:00.000Z",
	} as const;
}

function signalKey(signalId: string) {
	return { ...generationKey(), signalId };
}

function waitKey(waitId: string) {
	return { ...generationKey(), waitId };
}

function goalKey(goalId: string) {
	return { ...partition(), goalId };
}

function generationKey() {
	const { ownerId, timelineId, generation } = partition();
	return { ownerId, timelineId, generation };
}
