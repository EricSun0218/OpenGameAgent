import { describe, expect, it, vi } from "vitest";
import {
	ActionCoordinator,
	ActionGoalProjector,
	type ActionGoalProjectionLimits,
	type ActionIntentDraft,
	InMemoryActionJournal,
} from "../src/index.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;
const LIMITS = {
	maxBindingBytes: 1_024,
	maxPendingPerStream: 4,
	maxListBatchSize: 2,
	maxRecordBytes: 8_192,
} as const;

describe("ActionGoalProjector", () => {
	it("advances the exact Goal only after a succeeded Action receipt is verified and then records completion", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal({ goalProjection: LIMITS }), {
			now: () => new Date("2026-08-28T00:00:02.000Z"),
			newClaimToken: () => "claim-1",
		});
		const submitted = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "run-1", turn: "2" },
			actionName: "world.write",
			operationKey: "goal-action",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "3", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
			goalProjection: {
				goalId: "goal-1",
				partition: {
					ownerId: "npc-1",
					timelineId: "world",
					generation: "3",
					sessionId: "session",
					actorId: "actor",
				},
				stepId: "step-1",
				expectedRevision: "5",
				inputId: "input-1",
			},
		});
		const delivery = await actions.nextDelivery({
			...STREAM,
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected Action delivery.");
		await actions.recordReceipt({
			...STREAM,
			operationId: submitted.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "succeeded",
			result: { receiptId: "receipt-1" },
			authorityAfter: { timelineId: "world", generation: "3", revision: "8" },
		});
		const verify = vi.fn(async () => ({
			evidence: { kind: "action_receipt", referenceId: "receipt-1" },
			gameTime: { timelineId: "world", generation: "3", tick: "9" },
			recordedAt: "2026-08-28T00:00:02.000Z",
		}));
		const advanceFromHost = vi.fn(async () => ({ status: "advanced" as const }));
		const projector = new ActionGoalProjector({
			actions,
			verifier: { verify },
			goals: { advanceFromHost },
		});

		await expect(projector.projectPending({ ...STREAM, limit: 1 })).resolves.toEqual({
			results: [{ sequence: "1", operationId: submitted.operationId, status: "completed" }],
		});
		expect(verify).toHaveBeenCalledWith(
			expect.objectContaining({
				action: expect.objectContaining({ operationId: submitted.operationId, status: "succeeded" }),
				binding: expect.objectContaining({ goalId: "goal-1", inputId: "input-1" }),
			}),
		);
		expect(advanceFromHost).toHaveBeenCalledWith({
			goalId: "goal-1",
			partition: {
				ownerId: "npc-1",
				timelineId: "world",
				generation: "3",
				sessionId: "session",
				actorId: "actor",
			},
			stepId: "step-1",
			inputId: "input-1",
			expectedRevision: "5",
			evidence: { kind: "action_receipt", referenceId: "receipt-1" },
			gameTime: { timelineId: "world", generation: "3", tick: "9" },
			recordedAt: "2026-08-28T00:00:02.000Z",
		});
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toEqual({ candidates: [] });
	});

	it("keeps the projection pending across a stop after Goal CAS and completes the exact replay", async () => {
		const { actions, submitted } = await succeededAction("goal-crash");
		const controller = new AbortController();
		const stopped = new Error("stopped after Goal CAS");
		const verify = vi.fn(async () => verifiedReceipt());
		const advance = vi.fn(async () => {
			controller.abort(stopped);
			return { status: "advanced" as const };
		});

		await expect(
			new ActionGoalProjector({ actions, verifier: { verify }, goals: { advanceFromHost: advance } }).projectPending({
				...STREAM,
				limit: 1,
				signal: controller.signal,
			}),
		).rejects.toBe(stopped);
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toMatchObject({
			candidates: [{ action: { operationId: submitted.operationId } }],
		});

		const replay = new ActionGoalProjector({
			actions,
			verifier: { verify },
			goals: { advanceFromHost: vi.fn(async () => ({ status: "already_advanced" as const })) },
		});
		await expect(replay.projectPending({ ...STREAM, limit: 1 })).resolves.toEqual({
			results: [{ sequence: "1", operationId: submitted.operationId, status: "completed" }],
		});
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toEqual({ candidates: [] });
	});

	it("converges concurrent duplicate projection deliveries through advanced and already_advanced", async () => {
		const { actions, submitted } = await succeededAction("goal-race");
		const release = deferred<void>();
		let calls = 0;
		const bothEntered = deferred<void>();
		const advanceFromHost = vi.fn(async () => {
			calls += 1;
			const current = calls;
			if (calls === 2) bothEntered.resolve(undefined);
			await release.promise;
			return { status: current === 1 ? "advanced" : "already_advanced" };
		});
		const projector = new ActionGoalProjector({
			actions,
			verifier: { verify: async () => verifiedReceipt() },
			goals: { advanceFromHost },
		});

		const concurrent = [
			projector.projectPending({ ...STREAM, limit: 1 }),
			projector.projectPending({ ...STREAM, limit: 1 }),
		];
		await bothEntered.promise;
		release.resolve(undefined);

		await expect(Promise.all(concurrent)).resolves.toEqual([
			{ results: [{ sequence: "1", operationId: submitted.operationId, status: "completed" }] },
			{ results: [{ sequence: "1", operationId: submitted.operationId, status: "completed" }] },
		]);
		expect(advanceFromHost).toHaveBeenCalledTimes(2);
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toEqual({ candidates: [] });
	});

	it("fails closed for unverifiable receipts, non-advancing Goals, and mismatched verified time", async () => {
		const { actions, submitted } = await succeededAction("goal-rejected");
		const goals = { advanceFromHost: vi.fn(async () => ({ status: "advanced" as const })) };
		const rejected = new ActionGoalProjector({
			actions,
			verifier: { verify: async () => undefined },
			goals,
		});
		await expect(rejected.projectPending({ ...STREAM, limit: 1 })).resolves.toEqual({
			results: [
				{
					sequence: "1",
					operationId: submitted.operationId,
					status: "blocked",
					reason: "source_rejected",
				},
			],
		});
		expect(goals.advanceFromHost).not.toHaveBeenCalled();

		for (const goalStatus of ["revision_conflict", "input_not_committed", "evidence_rejected"] as const) {
			const stale = new ActionGoalProjector({
				actions,
				verifier: { verify: async () => verifiedReceipt() },
				goals: { advanceFromHost: async () => ({ status: goalStatus }) },
			});
			await expect(stale.projectPending({ ...STREAM, limit: 1 })).resolves.toEqual({
				results: [
					{
						sequence: "1",
						operationId: submitted.operationId,
						status: "blocked",
						reason: "goal_not_advanced",
						goalStatus,
					},
				],
			});
		}

		const mismatched = new ActionGoalProjector({
			actions,
			verifier: {
				verify: async () => ({
					...verifiedReceipt(),
					gameTime: { timelineId: "other-world", generation: "3", tick: "9" },
				}),
			},
			goals,
		});
		await expect(mismatched.projectPending({ ...STREAM, limit: 1 })).rejects.toThrow(
			"does not match its durable partition",
		);
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toMatchObject({
			candidates: [{ action: { operationId: submitted.operationId } }],
		});
	});

	it("does not verify or advance pending, failed, or uncertain source Actions", async () => {
		for (const status of ["pending", "failed", "uncertain"] as const) {
			const limits = status === "failed" ? { ...LIMITS, maxPendingPerStream: 1 } : LIMITS;
			const { actions, submitted } = await actionInState(`goal-${status}`, status, limits);
			const verify = vi.fn(async () => verifiedReceipt());
			const advanceFromHost = vi.fn(async () => ({ status: "advanced" as const }));
			const projector = new ActionGoalProjector({ actions, verifier: { verify }, goals: { advanceFromHost } });
			await expect(projector.projectPending({ ...STREAM, limit: 1 })).resolves.toEqual({
				results: [
					status === "failed"
						? {
								sequence: "1",
								operationId: submitted.operationId,
								status: "dismissed",
								reason: "source_failed",
							}
						: {
								sequence: "1",
								operationId: submitted.operationId,
								status: "blocked",
								reason: "source_not_succeeded",
							},
				],
			});
			expect(verify).not.toHaveBeenCalled();
			expect(advanceFromHost).not.toHaveBeenCalled();
			if (status === "failed") {
				await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toEqual({ candidates: [] });
				await expect(actions.submit(goalBoundDraft("goal-after-source-failure"))).resolves.toMatchObject({
					status: "pending",
				});
			}
		}
	});

	it("captures each host collaborator once before projection starts", async () => {
		const { actions } = await succeededAction("goal-collaborators");
		const verifier = { verify: vi.fn(async () => verifiedReceipt()) };
		const goals = { advanceFromHost: vi.fn(async () => ({ status: "advanced" as const })) };
		const reads = { actions: 0, verifier: 0, goals: 0 };
		const options = Object.create(null) as Record<string, unknown>;
		Object.defineProperties(options, {
			actions: {
				enumerable: true,
				get: () => {
					reads.actions += 1;
					return actions;
				},
			},
			verifier: {
				enumerable: true,
				get: () => {
					reads.verifier += 1;
					return verifier;
				},
			},
			goals: {
				enumerable: true,
				get: () => {
					reads.goals += 1;
					return goals;
				},
			},
		});
		const projector = new ActionGoalProjector(options as never);

		await expect(projector.projectPending({ ...STREAM, limit: 1 })).resolves.toMatchObject({
			results: [{ status: "completed" }],
		});
		expect(reads).toEqual({ actions: 1, verifier: 1, goals: 1 });
	});

	it("snapshots request and callback results so stateful getters are read exactly once", async () => {
		const { actions } = await succeededAction("goal-stateful-boundaries");
		const reads = {
			sessionId: 0,
			actorId: 0,
			afterSequence: 0,
			limit: 0,
			signal: 0,
			evidence: 0,
			gameTime: 0,
			recordedAt: 0,
			kind: 0,
			referenceId: 0,
			timelineId: 0,
			generation: 0,
			tick: 0,
			goalStatus: 0,
		};
		const counted =
			<Key extends keyof typeof reads, Value>(key: Key, value: Value) =>
			() => {
				reads[key] += 1;
				return value;
			};
		const evidence = Object.create(null) as Record<string, unknown>;
		Object.defineProperties(evidence, {
			kind: { enumerable: true, get: counted("kind", "action_receipt") },
			referenceId: { enumerable: true, get: counted("referenceId", "receipt-1") },
		});
		const gameTime = Object.create(null) as Record<string, unknown>;
		Object.defineProperties(gameTime, {
			timelineId: { enumerable: true, get: counted("timelineId", "world") },
			generation: { enumerable: true, get: counted("generation", "3") },
			tick: { enumerable: true, get: counted("tick", "9") },
		});
		const verification = Object.create(null) as Record<string, unknown>;
		Object.defineProperties(verification, {
			evidence: { enumerable: true, get: counted("evidence", evidence) },
			gameTime: { enumerable: true, get: counted("gameTime", gameTime) },
			recordedAt: {
				enumerable: true,
				get: counted("recordedAt", "2026-08-28T00:00:02.000Z"),
			},
		});
		const goalResult = Object.create(null) as Record<string, unknown>;
		Object.defineProperty(goalResult, "status", {
			enumerable: true,
			get: counted("goalStatus", "advanced"),
		});
		const request = Object.create(null) as Record<string, unknown>;
		Object.defineProperties(request, {
			sessionId: { enumerable: true, get: counted("sessionId", STREAM.sessionId) },
			actorId: { enumerable: true, get: counted("actorId", STREAM.actorId) },
			afterSequence: { enumerable: true, get: counted("afterSequence", undefined) },
			limit: { enumerable: true, get: counted("limit", 1) },
			signal: { enumerable: true, get: counted("signal", undefined) },
		});
		const projector = new ActionGoalProjector({
			actions,
			verifier: { verify: async () => verification as never },
			goals: { advanceFromHost: async () => goalResult as never },
		});

		await expect(projector.projectPending(request as never)).resolves.toMatchObject({
			results: [{ status: "completed" }],
		});
		expect(reads).toEqual({
			sessionId: 1,
			actorId: 1,
			afterSequence: 1,
			limit: 1,
			signal: 1,
			evidence: 1,
			gameTime: 1,
			recordedAt: 1,
			kind: 1,
			referenceId: 1,
			timelineId: 1,
			generation: 1,
			tick: 1,
			goalStatus: 1,
		});
	});

	it("stops a cancelled caller after durable completion without reopening the projection", async () => {
		const { actions } = await succeededAction("goal-completion-cancel");
		const controller = new AbortController();
		const stopped = new Error("cancelled after projection completion");
		const complete = actions.settleGoalProjection.bind(actions);
		vi.spyOn(actions, "settleGoalProjection").mockImplementation(async (input) => {
			const completed = await complete(input);
			controller.abort(stopped);
			return completed;
		});
		const projector = new ActionGoalProjector({
			actions,
			verifier: { verify: async () => verifiedReceipt() },
			goals: { advanceFromHost: async () => ({ status: "advanced" }) },
		});

		await expect(projector.projectPending({ ...STREAM, limit: 1, signal: controller.signal })).rejects.toBe(stopped);
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toEqual({ candidates: [] });
	});
});

async function succeededAction(operationKey: string) {
	return actionInState(operationKey, "succeeded");
}

async function actionInState(
	operationKey: string,
	status: "pending" | "succeeded" | "failed" | "uncertain",
	limits: Readonly<ActionGoalProjectionLimits> = LIMITS,
) {
	let now = "2026-08-28T00:00:00.000Z";
	const actions = new ActionCoordinator(new InMemoryActionJournal({ goalProjection: limits }), {
		now: () => new Date(now),
		newClaimToken: () => `claim-${operationKey}`,
	});
	const submitted = await actions.submit(goalBoundDraft(operationKey));
	if (status === "pending") return { actions, submitted };
	const delivery = await actions.nextDelivery({
		...STREAM,
		claimantId: "game",
		leaseDurationMs: 1_000,
		candidateLimit: 1,
	});
	if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected Action delivery.");
	if (status === "uncertain") {
		now = "2026-08-28T00:00:02.000Z";
		const reconciliation = await actions.nextDelivery({
			...STREAM,
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (reconciliation?.kind !== "reconcile") throw new Error("Expected Action reconciliation.");
		return { actions, submitted };
	}
	await actions.recordReceipt({
		...STREAM,
		operationId: submitted.operationId,
		claimToken: delivery.action.claim.claimToken,
		attempt: delivery.action.claim.attempt,
		outcome: status === "failed" ? "rejected" : "succeeded",
		result: { receiptId: "receipt-1" },
		...(status === "succeeded" ? { authorityAfter: { timelineId: "world", generation: "3", revision: "8" } } : {}),
	});
	return { actions, submitted };
}

function goalBoundDraft(operationKey: string): ActionIntentDraft {
	return {
		...STREAM,
		coordinate: { ...STREAM, runId: "run-1", turn: "2" },
		actionName: "world.write",
		operationKey,
		payload: { value: 1 },
		expectedAuthority: { timelineId: "world", generation: "3", revision: "7" },
		conflictKeys: ["world:tile:1"],
		replayPolicy: "recoverable",
		goalProjection: {
			goalId: "goal-1",
			partition: {
				ownerId: "npc-1",
				timelineId: "world",
				generation: "3",
				sessionId: "session",
				actorId: "actor",
			},
			stepId: "step-1",
			expectedRevision: "5",
			inputId: "input-1",
		},
	};
}

function verifiedReceipt() {
	return {
		evidence: { kind: "action_receipt", referenceId: "receipt-1" },
		gameTime: { timelineId: "world", generation: "3", tick: "9" },
		recordedAt: "2026-08-28T00:00:02.000Z",
	} as const;
}

function deferred<T>() {
	let resolve!: (value: T) => void;
	const promise = new Promise<T>((settle) => {
		resolve = settle;
	});
	return { promise, resolve };
}
