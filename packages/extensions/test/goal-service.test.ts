import {
	createGoalTools,
	GoalAuthorityRetiredError,
	GoalAuthorityRetirementConflictError,
	GoalCapacityError,
	type GoalDraft,
	type GoalAuthorityRetirementDraft,
	GoalEvidenceRejectedError,
	type GoalEvidenceValidationRequest,
	type GoalLimits,
	GoalNotFoundError,
	type GoalPlanStep,
	GoalRevisionConflictError,
	type GoalRecord,
	GoalService,
	GoalValidationError,
	InMemoryGoalStore,
} from "@opengameagent/extensions";
import { canonicalizeJsonValue } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";

const limits: GoalLimits = {
	maxObjectiveBytes: 1_024,
	maxStepsPerGoal: 8,
	maxStepBytes: 512,
	maxPlanBytes: 8_192,
	maxRecordBytes: 32_768,
	maxModelResultBytes: 16_384,
	maxEvidencePerStep: 4,
	maxEvidenceKindBytes: 64,
	maxEvidenceReferenceBytes: 256,
	maxOpenGoalsPerOwnerGeneration: 2,
	maxListPageSize: 4,
	maxPurgeBatchSize: 4,
};

describe("GoalService", () => {
	it("captures every host-supplied goal limit once before validating and freezing it", () => {
		const configured = { ...limits };
		let reads = 0;
		Object.defineProperty(configured, "maxPlanBytes", {
			enumerable: true,
			get: () => {
				reads += 1;
				return reads === 1 ? limits.maxPlanBytes : 0;
			},
		});

		const store = new InMemoryGoalStore(configured);
		expect(store.limits.maxPlanBytes).toBe(limits.maxPlanBytes);
		expect(reads).toBe(1);
	});

	it("keeps the in-memory store from retaining a record over its aggregate budget", async () => {
		const draft = {
			goalId: "goal-memory-record-budget",
			partition: partition(),
			objective: "Bound the conformance store",
			plan: [step("step-1", "Inspect", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		} as const;
		const recordBytes = initialGoalRecordBytes(draft);
		const boundedLimits = { ...limits, maxRecordBytes: recordBytes - 1 };
		const store = new InMemoryGoalStore(boundedLimits);
		const service = new GoalService(store, { validate: async () => true });

		await expect(service.create(draft)).rejects.toMatchObject({ code: "goal_record_too_large" });
		expect(await store.get(goalKey(draft.goalId))).toBeUndefined();
	});

	it("rolls back an in-memory replacement when the complete updated record crosses its budget", async () => {
		const draft = {
			goalId: "goal-memory-replace-budget",
			partition: partition(),
			objective: "Keep the original in-memory record",
			plan: [step("step-1", "Inspect", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		} as const;
		const initialRecordBytes = initialGoalRecordBytes(draft);
		const boundedLimits = { ...limits, maxRecordBytes: initialRecordBytes };
		const service = new GoalService(new InMemoryGoalStore(boundedLimits), { validate: async () => true });
		await service.create(draft);

		await expect(
			service.update({
				...goalKey(draft.goalId),
				expectedRevision: "1",
				plan: [step("step-1", "Inspect", "completed", [{ kind: "action_receipt", referenceId: "receipt-1" }])],
				status: "achieved",
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toMatchObject({ code: "goal_record_too_large" });
		expect(await service.get(goalKey(draft.goalId))).toMatchObject({
			revision: "1",
			status: "active",
			plan: draft.plan,
		});
	});

	it("admits a complete plan at its canonical byte budget and rejects one byte less before persistence", async () => {
		const plan = [
			step("step-1", "Inspect the sealed gate", "in_progress"),
			step("step-2", "Report the mechanism", "pending"),
		];
		const planBytes = canonicalBytes(plan);
		const acceptedLimits = { ...limits, maxPlanBytes: planBytes };
		const store = new InMemoryGoalStore(acceptedLimits);
		const service = new GoalService(store, { validate: async () => true });
		await expect(
			service.create({
				goalId: "goal-at-plan-budget",
				partition: partition(),
				objective: "Fit exactly",
				plan,
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			}),
		).resolves.toMatchObject({ goalId: "goal-at-plan-budget" });

		const rejectedLimits = { ...limits, maxPlanBytes: planBytes - 1 };
		const rejectedStore = new InMemoryGoalStore(rejectedLimits);
		const rejected = new GoalService(rejectedStore, { validate: async () => true });
		await expect(
			rejected.create({
				goalId: "goal-over-plan-budget",
				partition: partition(),
				objective: "Reject cumulatively",
				plan,
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			}),
		).rejects.toMatchObject({ code: "goal_plan_too_large" });
		expect(await rejectedStore.get(goalKey("goal-over-plan-budget"))).toBeUndefined();
	});

	it("rejects a replan that crosses the aggregate plan budget without changing its revision", async () => {
		const initialPlan = [step("step-1", "Inspect", "in_progress")];
		const planBytes = canonicalBytes(initialPlan);
		const boundedLimits = { ...limits, maxPlanBytes: planBytes };
		const service = new GoalService(new InMemoryGoalStore(boundedLimits), { validate: async () => true });
		await service.create({
			goalId: "goal-replan-budget",
			partition: partition(),
			objective: "Keep the admitted plan",
			plan: initialPlan,
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		await expect(
			service.update({
				...goalKey("goal-replan-budget"),
				expectedRevision: "1",
				plan: [...initialPlan, step("step-2", "Report", "pending")],
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toMatchObject({ code: "goal_plan_too_large" });
		expect(await service.get(goalKey("goal-replan-budget"))).toMatchObject({ revision: "1", plan: initialPlan });
	});

	it("freezes limits and rejects an ambiguous empty status filter", async () => {
		const configured = { ...limits };
		const service = new GoalService(new InMemoryGoalStore(configured), { validate: async () => true });
		configured.maxListPageSize = 1;
		expect(Object.isFrozen(service.limits)).toBe(true);
		expect(service.limits.maxListPageSize).toBe(4);
		const minimumModelResultBytes = canonicalBytes({ found: false });
		expect(() => new InMemoryGoalStore({ ...limits, maxModelResultBytes: minimumModelResultBytes - 1 })).toThrow(
			"invalid_limit:maxModelResultBytes",
		);
		await expect(service.list({ ...partition(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(GoalValidationError);
	});

	it("captures create and update scalar fields once before validation or persistence", async () => {
		const service = acceptingService();
		let createTimeReads = 0;
		const createRequest: Record<string, unknown> = {
			goalId: "goal-snapshot",
			partition: partition(),
			objective: "Capture once",
			plan: [step("step-1", "Advance", "in_progress")],
			gameTime: gameTime("1"),
		};
		Object.defineProperty(createRequest, "recordedAt", {
			enumerable: true,
			get: () => {
				createTimeReads += 1;
				return createTimeReads === 1 ? "2026-08-28T00:00:00.000Z" : "not-a-time";
			},
		});
		const created = await service.create(createRequest as never);
		expect(created.createdAt).toBe("2026-08-28T00:00:00.000Z");
		expect(createTimeReads).toBe(1);

		let updateStatusReads = 0;
		const updateRequest: Record<string, unknown> = {
			...goalKey("goal-snapshot"),
			expectedRevision: "1",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		};
		Object.defineProperty(updateRequest, "status", {
			enumerable: true,
			get: () => {
				updateStatusReads += 1;
				return updateStatusReads === 1 ? "paused" : "cancelled";
			},
		});
		const updated = await service.update(updateRequest as never);
		expect(updated.status).toBe("paused");
		expect(updateStatusReads).toBe(1);
	});

	it("preserves an exact caller cancellation before creating a goal", async () => {
		const service = acceptingService();
		const controller = new AbortController();
		const cancellation = new Error("goal authority revoked");
		controller.abort(cancellation);

		await expect(
			service.create({
				goalId: "goal-cancelled-before-create",
				partition: partition(),
				objective: "Must not survive revocation",
				plan: [step("step-1", "Start", "in_progress")],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
				signal: controller.signal,
			}),
		).rejects.toBe(cancellation);
		expect(await service.get(goalKey("goal-cancelled-before-create"))).toBeUndefined();
	});

	it("carries caller cancellation through a delayed Goal Store write", async () => {
		let startedCreate!: () => void;
		let continueCreate!: () => void;
		const started = new Promise<void>((resolve) => {
			startedCreate = resolve;
		});
		const gate = new Promise<void>((resolve) => {
			continueCreate = resolve;
		});
		class DelayedGoalStore extends InMemoryGoalStore {
			override async create(draft: GoalDraft, maxOpenGoals: number, signal?: AbortSignal): Promise<GoalRecord> {
				startedCreate();
				await gate;
				signal?.throwIfAborted();
				return await super.create(draft, maxOpenGoals);
			}
		}
		const service = new GoalService(new DelayedGoalStore(limits), { validate: async () => true });
		const controller = new AbortController();
		const cancellation = new Error("goal authority revoked while waiting");
		const create = service.create({
			goalId: "goal-cancelled-during-create",
			partition: partition(),
			objective: "Must not outlive its authority",
			plan: [step("step-1", "Start", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
			signal: controller.signal,
		});
		await started;
		controller.abort(cancellation);
		continueCreate();

		await expect(create).rejects.toBe(cancellation);
		expect(await service.get(goalKey("goal-cancelled-during-create"))).toBeUndefined();
	});

	it("atomically retires one exact authority partition and fences late Goal creation", async () => {
		const service = acceptingService();
		for (const goalId of ["goal-retire-active", "goal-retire-paused"]) {
			await service.create({
				goalId,
				partition: partition(),
				objective: goalId,
				plan: [],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			});
		}
		await service.update({
			...goalKey("goal-retire-paused"),
			expectedRevision: "1",
			status: "paused",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		const request = {
			retirementId: "retirement-1",
			stateRevision: "7",
			partition: partition(),
			gameTime: gameTime("3"),
			recordedAt: "2026-08-28T00:00:02.000Z",
		} as const;

		const retired = await service.retireAuthority(request);
		const replayed = await service.retireAuthority(request);

		expect(retired).toEqual({ ...request, cancelledGoalCount: 2 });
		expect(replayed).toEqual(retired);
		expect(
			(await service.list({ ...partition(), statuses: ["cancelled"], limit: 4 })).records.map((goal) => ({
				goalId: goal.goalId,
				revision: goal.revision,
				gameTime: goal.gameTime,
			})),
		).toEqual([
			{ goalId: "goal-retire-active", revision: "2", gameTime: gameTime("3") },
			{ goalId: "goal-retire-paused", revision: "3", gameTime: gameTime("3") },
		]);
		await expect(
			service.create({
				goalId: "goal-late",
				partition: partition(),
				objective: "must remain fenced",
				plan: [],
				gameTime: gameTime("4"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
		await expect(service.retireAuthority({ ...request, stateRevision: "8" })).rejects.toBeInstanceOf(
			GoalAuthorityRetirementConflictError,
		);
		await expect(
			service.create({
				goalId: "goal-other-actor",
				partition: { ...partition(), actorId: "actor-2" },
				objective: "independent authority",
				plan: [],
				gameTime: gameTime("4"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).resolves.toMatchObject({ status: "active" });
	});

	it("carries exact cancellation through a delayed authority retirement without installing the fence", async () => {
		let startedRetirement!: () => void;
		let continueRetirement!: () => void;
		const started = new Promise<void>((resolve) => {
			startedRetirement = resolve;
		});
		const gate = new Promise<void>((resolve) => {
			continueRetirement = resolve;
		});
		class DelayedGoalStore extends InMemoryGoalStore {
			override async retireAuthority(draft: GoalAuthorityRetirementDraft, signal?: AbortSignal) {
				startedRetirement();
				await gate;
				signal?.throwIfAborted();
				return await super.retireAuthority(draft);
			}
		}
		const service = new GoalService(new DelayedGoalStore(limits), { validate: async () => true });
		const controller = new AbortController();
		const cancellation = new Error("authority retirement cancelled while waiting");
		const retirement = service.retireAuthority({
			retirementId: "retirement-cancelled",
			stateRevision: "8",
			partition: partition(),
			gameTime: gameTime("3"),
			recordedAt: "2026-08-28T00:00:02.000Z",
			signal: controller.signal,
		});
		await started;
		controller.abort(cancellation);
		continueRetirement();

		await expect(retirement).rejects.toBe(cancellation);
		await expect(
			service.create({
				goalId: "goal-after-cancelled-retirement",
				partition: partition(),
				objective: "Authority remains live",
				plan: [],
				gameTime: gameTime("4"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).resolves.toMatchObject({ status: "active" });
	});

	it("advances an ordered plan only through verified evidence and terminal completion", async () => {
		const verify = vi.fn(async (request: GoalEvidenceValidationRequest) => request.evidence.kind === "action_receipt");
		const service = new GoalService(new InMemoryGoalStore(limits), { validate: verify });
		const created = await service.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "Open the sealed gate",
			plan: [step("step-1", "Find the key", "in_progress"), step("step-2", "Open the gate", "pending")],
			gameTime: gameTime("90071992547409931234567890"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		const advanced = await service.update({
			...goalKey("goal-1"),
			expectedRevision: "1",
			plan: [
				step("step-1", "Find the key", "completed", [{ kind: "action_receipt", referenceId: "receipt-1" }]),
				step("step-2", "Open the gate", "in_progress"),
			],
			gameTime: gameTime("90071992547409931234567891"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		const achieved = await service.update({
			...goalKey("goal-1"),
			expectedRevision: "2",
			plan: [
				...advanced.plan.slice(0, 1),
				step("step-2", "Open the gate", "completed", [{ kind: "action_receipt", referenceId: "receipt-2" }]),
			],
			status: "achieved",
			gameTime: gameTime("90071992547409931234567892"),
			recordedAt: "2026-08-28T00:00:02.000Z",
		});

		expect(created).toMatchObject({ revision: "1", status: "active" });
		expect(created).not.toHaveProperty("recordedAt");
		expect(advanced).toMatchObject({ revision: "2", status: "active" });
		expect(achieved).toMatchObject({
			revision: "3",
			status: "achieved",
			createdGameTime: gameTime("90071992547409931234567890"),
			gameTime: gameTime("90071992547409931234567892"),
		});
		expect(verify).toHaveBeenCalledTimes(2);
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: "3",
				status: "active",
				gameTime: gameTime("90071992547409931234567893"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).rejects.toThrow("terminal_goal_update");
	});

	it("lets the host advance the current step from a committed input and verified receipt", async () => {
		const committedInput = vi.fn(async ({ inputId }: { inputId: string }) => inputId === "input-1");
		const verify = vi.fn(
			async (request: GoalEvidenceValidationRequest) =>
				request.inputId === "input-1" &&
				request.evidence.kind === "action_receipt" &&
				request.evidence.referenceId === "receipt-1",
		);
		const service = new GoalService(new InMemoryGoalStore(limits), { validate: verify }, { validate: committedInput });
		await service.create({
			goalId: "goal-host",
			partition: partition(),
			objective: "Advance without model bookkeeping",
			plan: [step("step-1", "Inspect", "in_progress"), step("step-2", "Report", "pending")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		const result = await service.advanceFromHost({
			goalId: "goal-host",
			partition: partition(),
			stepId: "step-1",
			inputId: "input-1",
			expectedRevision: "1",
			evidence: { kind: "action_receipt", referenceId: "receipt-1" },
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});

		expect(result).toMatchObject({
			status: "advanced",
			goal: {
				revision: "2",
				status: "active",
				plan: [
					step("step-1", "Inspect", "completed", [{ kind: "action_receipt", referenceId: "receipt-1" }]),
					step("step-2", "Report", "in_progress"),
				],
			},
		});
		expect(committedInput).toHaveBeenCalledOnce();
		expect(verify).toHaveBeenCalledOnce();
	});

	it("captures the exact host-advance identity once before fingerprinting and CAS", async () => {
		const service = new GoalService(
			new InMemoryGoalStore(limits),
			{ validate: async () => true },
			{ validate: async () => true },
		);
		for (const goalId of ["goal-snapshot-a", "goal-snapshot-b"]) {
			await service.create({
				goalId,
				partition: partition(),
				objective: goalId,
				plan: [step("step-1", "Advance", "in_progress")],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			});
		}
		let goalIdReads = 0;
		const request: Record<string, unknown> = {
			partition: partition(),
			stepId: "step-1",
			inputId: "input-1",
			expectedRevision: "1",
			evidence: { kind: "receipt", referenceId: "receipt-1" },
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		};
		Object.defineProperty(request, "goalId", {
			enumerable: true,
			get: () => {
				goalIdReads += 1;
				return goalIdReads === 1 ? "goal-snapshot-a" : "goal-snapshot-b";
			},
		});

		expect((await service.advanceFromHost(request as never)).status).toBe("advanced");
		expect(goalIdReads).toBe(1);
		expect((await service.get(goalKey("goal-snapshot-a")))?.revision).toBe("2");
		expect((await service.get(goalKey("goal-snapshot-b")))?.revision).toBe("1");
	});

	it("makes the same host receipt idempotent while preserving compare-and-swap conflicts", async () => {
		const verifyEvidence = vi.fn(async () => true);
		const verifyInput = vi.fn(async () => true);
		const service = new GoalService(
			new InMemoryGoalStore(limits),
			{ validate: verifyEvidence },
			{ validate: verifyInput },
		);
		await service.create({
			goalId: "goal-race",
			partition: partition(),
			objective: "Settle once",
			plan: [step("step-1", "Inspect", "in_progress"), step("step-2", "Report", "pending")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const request = {
			goalId: "goal-race",
			partition: partition(),
			stepId: "step-1",
			inputId: "input-race",
			expectedRevision: "1",
			evidence: { kind: "action_receipt", referenceId: "receipt-race" },
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		} as const;

		const concurrent = await Promise.all([service.advanceFromHost(request), service.advanceFromHost(request)]);
		expect(concurrent.map((result) => result.status).sort()).toEqual(["advanced", "already_advanced"]);
		expect((await service.advanceFromHost(request)).status).toBe("already_advanced");
		expect(verifyEvidence).toHaveBeenCalledTimes(2);
		expect(verifyInput).toHaveBeenCalledTimes(2);
		for (const mismatch of [
			{ inputId: "input-other" },
			{ expectedRevision: "999" },
			{ recordedAt: "2026-08-28T00:00:02.000Z" },
		] as const) {
			const replay = await service.advanceFromHost({ ...request, ...mismatch });
			expect(replay.status).toBe("revision_conflict");
		}
		await expect(service.advanceFromHost({ ...request, gameTime: gameTime("0") })).rejects.toThrow(
			"goal_time_regression",
		);
		expect(verifyEvidence).toHaveBeenCalledTimes(2);
		expect(verifyInput).toHaveBeenCalledTimes(2);

		const staleDifferentReceipt = await service.advanceFromHost({
			...request,
			evidence: { kind: "action_receipt", referenceId: "receipt-other" },
		});
		expect(staleDifferentReceipt.status).toBe("revision_conflict");
		const repeatedCompletion = await service.advanceFromHost({
			...request,
			expectedRevision: "2",
			evidence: { kind: "action_receipt", referenceId: "receipt-other" },
		});
		expect(repeatedCompletion.status).toBe("step_already_completed");
	});

	it("rejects uncommitted inputs, unsuccessful receipts, and mismatched authority partitions", async () => {
		const committedInput = vi.fn(async ({ inputId }: { inputId: string }) => inputId !== "input-uncommitted");
		const verify = vi.fn(
			async ({ evidence }: GoalEvidenceValidationRequest) => evidence.referenceId === "receipt-succeeded",
		);
		const service = new GoalService(new InMemoryGoalStore(limits), { validate: verify }, { validate: committedInput });
		await service.create({
			goalId: "goal-authority",
			partition: partition(),
			objective: "Accept only authoritative proof",
			plan: [step("step-1", "Inspect", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const request = {
			goalId: "goal-authority",
			partition: partition(),
			stepId: "step-1",
			inputId: "input-1",
			expectedRevision: "1",
			evidence: { kind: "action_receipt", referenceId: "receipt-succeeded" },
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		} as const;

		expect((await service.advanceFromHost({ ...request, inputId: "input-uncommitted" })).status).toBe(
			"input_not_committed",
		);
		for (const referenceId of ["receipt-failed", "receipt-unknown"]) {
			expect(
				(
					await service.advanceFromHost({
						...request,
						evidence: { kind: "action_receipt", referenceId },
					})
				).status,
			).toBe("evidence_rejected");
		}

		const mismatches = [
			{ partition: { ...partition(), ownerId: "owner-2" }, gameTime: gameTime("2") },
			{
				partition: { ...partition(), timelineId: "other-world" },
				gameTime: { timelineId: "other-world", generation: "1", tick: "2" } as const,
			},
			{
				partition: { ...partition(), generation: "2" },
				gameTime: { timelineId: "world", generation: "2", tick: "2" } as const,
			},
			{ partition: { ...partition(), sessionId: "session-2" }, gameTime: gameTime("2") },
			{ partition: { ...partition(), actorId: "actor-2" }, gameTime: gameTime("2") },
		];
		for (const mismatch of mismatches) {
			expect((await service.advanceFromHost({ ...request, ...mismatch })).status).toBe("goal_not_found");
		}
		expect((await service.get(goalKey("goal-authority")))?.revision).toBe("1");
	});

	it("never skips an unfinished predecessor or advances a paused or terminal goal", async () => {
		const service = new GoalService(
			new InMemoryGoalStore(limits),
			{ validate: async () => true },
			{ validate: async () => true },
		);
		await service.create({
			goalId: "goal-ordered",
			partition: partition(),
			objective: "Keep order",
			plan: [step("step-1", "First", "in_progress"), step("step-2", "Second", "pending")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const base = {
			goalId: "goal-ordered",
			partition: partition(),
			inputId: "input-1",
			expectedRevision: "1",
			evidence: { kind: "receipt", referenceId: "receipt-1" },
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		} as const;
		expect((await service.advanceFromHost({ ...base, stepId: "step-2" })).status).toBe("step_not_current");

		const paused = await service.update({
			...goalKey("goal-ordered"),
			expectedRevision: "1",
			status: "paused",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		expect(
			(
				await service.advanceFromHost({
					...base,
					stepId: "step-1",
					expectedRevision: paused.revision,
					gameTime: gameTime("3"),
					recordedAt: "2026-08-28T00:00:02.000Z",
				})
			).status,
		).toBe("plan_not_active");

		await service.create({
			goalId: "goal-terminal",
			partition: partition(),
			objective: "Finish once",
			plan: [step("step-only", "Finish", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const finished = await service.advanceFromHost({
			...base,
			goalId: "goal-terminal",
			stepId: "step-only",
		});
		expect(finished).toMatchObject({ status: "advanced", goal: { status: "achieved", revision: "2" } });
		expect(
			(
				await service.advanceFromHost({
					...base,
					goalId: "goal-terminal",
					stepId: "step-only",
					expectedRevision: "2",
					evidence: { kind: "receipt", referenceId: "receipt-other" },
				})
			).status,
		).toBe("plan_not_active");
	});

	it("keeps the existing exact game-time monotonicity guard on host advancement", async () => {
		const service = new GoalService(
			new InMemoryGoalStore(limits),
			{ validate: async () => true },
			{ validate: async () => true },
		);
		await service.create({
			goalId: "goal-time",
			partition: partition(),
			objective: "Do not rewind",
			plan: [step("step-1", "Advance", "in_progress")],
			gameTime: gameTime("5"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		await expect(
			service.advanceFromHost({
				goalId: "goal-time",
				partition: partition(),
				stepId: "step-1",
				inputId: "input-1",
				expectedRevision: "1",
				evidence: { kind: "receipt", referenceId: "receipt-1" },
				gameTime: gameTime("4"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toThrow("goal_time_regression");
		expect((await service.get(goalKey("goal-time")))?.revision).toBe("1");
	});

	it("stops before evidence validation when committed-input verification aborts", async () => {
		const controller = new AbortController();
		const cancellation = new Error("host advancement cancelled");
		const verify = vi.fn(async () => true);
		const service = new GoalService(
			new InMemoryGoalStore(limits),
			{ validate: verify },
			{
				validate: async () => {
					controller.abort(cancellation);
					return true;
				},
			},
		);
		await service.create({
			goalId: "goal-cancel",
			partition: partition(),
			objective: "Stop cleanly",
			plan: [step("step-1", "Advance", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		await expect(
			service.advanceFromHost({
				goalId: "goal-cancel",
				partition: partition(),
				stepId: "step-1",
				inputId: "input-1",
				expectedRevision: "1",
				evidence: { kind: "receipt", referenceId: "receipt-1" },
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
				signal: controller.signal,
			}),
		).rejects.toBe(cancellation);
		expect(verify).not.toHaveBeenCalled();
		expect((await service.get(goalKey("goal-cancel")))?.revision).toBe("1");
	});

	it("honors cancellation after reading authority to reconcile a compare-and-swap conflict", async () => {
		const controller = new AbortController();
		const cancellation = new Error("cancelled during conflict reconciliation");
		const store = new InMemoryGoalStore(limits);
		const service = new GoalService(store, { validate: async () => true }, { validate: async () => true });
		await service.create({
			goalId: "goal-cas-cancel",
			partition: partition(),
			objective: "Cancel at the awaited boundary",
			plan: [step("step-1", "Advance", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const originalGet = store.get.bind(store);
		let reads = 0;
		vi.spyOn(store, "get").mockImplementation(async (key) => {
			const record = await originalGet(key);
			reads += 1;
			if (reads === 2) controller.abort(cancellation);
			return record;
		});
		vi.spyOn(store, "replace").mockRejectedValueOnce(new GoalRevisionConflictError());

		await expect(
			service.advanceFromHost({
				goalId: "goal-cas-cancel",
				partition: partition(),
				stepId: "step-1",
				inputId: "input-1",
				expectedRevision: "1",
				evidence: { kind: "receipt", referenceId: "receipt-1" },
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
				signal: controller.signal,
			}),
		).rejects.toBe(cancellation);
	});

	it("does not commit a model-authored completion after evidence verification aborts", async () => {
		const controller = new AbortController();
		const cancellation = new Error("cancelled during evidence verification");
		const service = new GoalService(new InMemoryGoalStore(limits), {
			validate: async () => {
				controller.abort(cancellation);
				return true;
			},
		});
		await service.create({
			goalId: "goal-update-cancel",
			partition: partition(),
			objective: "Do not commit after cancellation",
			plan: [step("step-1", "Advance", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		await expect(
			service.update({
				...goalKey("goal-update-cancel"),
				expectedRevision: "1",
				plan: [step("step-1", "Advance", "completed", [{ kind: "receipt", referenceId: "receipt-1" }])],
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
				signal: controller.signal,
			}),
		).rejects.toBe(cancellation);
		expect((await service.get(goalKey("goal-update-cancel")))?.revision).toBe("1");
	});

	it("does not commit after cancellation resumes an unchanged-plan transition", async () => {
		const controller = new AbortController();
		const cancellation = new Error("cancelled after plan transition validation");
		const service = acceptingService();
		const unchangedPlan = [step("step-1", "Advance", "in_progress")];
		await service.create({
			goalId: "goal-transition-cancel",
			partition: partition(),
			objective: "Cancel at every awaited boundary",
			plan: unchangedPlan,
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		const pending = service.update({
			...goalKey("goal-transition-cancel"),
			expectedRevision: "1",
			plan: unchangedPlan,
			status: "paused",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
			signal: controller.signal,
		});
		queueMicrotask(() => controller.abort(cancellation));

		await expect(pending).rejects.toBe(cancellation);
		expect(await service.get(goalKey("goal-transition-cancel"))).toMatchObject({
			revision: "1",
			status: "active",
		});
	});

	it("rejects skipped, unverified, or mutated completion evidence", async () => {
		const service = new GoalService(new InMemoryGoalStore(limits), {
			validate: async ({ evidence }) => evidence.referenceId !== "rejected",
		});
		await service.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "Collect evidence",
			plan: [step("step-1", "Inspect", "pending")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: "1",
				plan: [step("step-1", "Inspect", "completed", [{ kind: "receipt", referenceId: "accepted" }])],
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toThrow("goal_step_skipped_in_progress");

		const started = await service.update({
			...goalKey("goal-1"),
			expectedRevision: "1",
			plan: [step("step-1", "Inspect", "in_progress")],
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: started.revision,
				plan: [step("step-1", "Inspect", "completed")],
				gameTime: gameTime("3"),
				recordedAt: "2026-08-28T00:00:02.000Z",
			}),
		).rejects.toThrow("missing_goal_evidence");
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: started.revision,
				plan: [step("step-1", "Inspect", "completed", [{ kind: "receipt", referenceId: "rejected" }])],
				gameTime: gameTime("3"),
				recordedAt: "2026-08-28T00:00:02.000Z",
			}),
		).rejects.toBeInstanceOf(GoalEvidenceRejectedError);

		const completed = await service.update({
			...goalKey("goal-1"),
			expectedRevision: started.revision,
			plan: [step("step-1", "Inspect", "completed", [{ kind: "receipt", referenceId: "accepted" }])],
			gameTime: gameTime("3"),
			recordedAt: "2026-08-28T00:00:02.000Z",
		});
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: completed.revision,
				plan: [step("step-1", "Changed", "completed", [{ kind: "receipt", referenceId: "accepted" }])],
				gameTime: gameTime("4"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).rejects.toThrow("completed_goal_step_changed");
	});

	it("enforces optimistic revisions, exact time, plan shape, and pause semantics", async () => {
		const service = acceptingService();
		const created = await service.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "Wait safely",
			plan: [step("step-1", "Wait", "in_progress"), step("step-2", "Report", "pending")],
			gameTime: gameTime("10"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: "0",
				status: "paused",
				gameTime: gameTime("11"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toBeInstanceOf(GoalRevisionConflictError);
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: created.revision,
				status: "paused",
				gameTime: gameTime("9"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toThrow("goal_time_regression");

		const paused = await service.update({
			...goalKey("goal-1"),
			expectedRevision: created.revision,
			status: "paused",
			gameTime: gameTime("11"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: paused.revision,
				plan: [step("replacement", "Replanned", "in_progress")],
				gameTime: gameTime("12"),
				recordedAt: "2026-08-28T00:00:02.000Z",
			}),
		).rejects.toThrow("paused_goal_plan_update");
		const resumed = await service.update({
			...goalKey("goal-1"),
			expectedRevision: paused.revision,
			status: "active",
			plan: [step("replacement", "Replanned", "in_progress")],
			gameTime: gameTime("12"),
			recordedAt: "2026-08-28T00:00:02.000Z",
		});
		expect(resumed.plan.map((item) => item.stepId)).toEqual(["replacement"]);
		await expect(
			service.update({
				...goalKey("goal-1"),
				expectedRevision: resumed.revision,
				plan: [step("a", "A", "in_progress"), step("b", "B", "in_progress")],
				gameTime: gameTime("13"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).rejects.toBeInstanceOf(GoalValidationError);
	});

	it("bounds open goals, paginates host reads, isolates generations, and purges terminal records", async () => {
		const service = acceptingService();
		for (const goalId of ["goal-1", "goal-2"]) {
			await service.create({
				goalId,
				partition: partition(),
				objective: goalId,
				plan: [],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			});
		}
		await expect(
			service.create({
				goalId: "goal-3",
				partition: partition(),
				objective: "capacity",
				plan: [],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			}),
		).rejects.toBeInstanceOf(GoalCapacityError);
		await service.create({
			goalId: "goal-3",
			partition: { ...partition(), generation: "2" },
			objective: "isolated rewind",
			plan: [],
			gameTime: { ...gameTime("1"), generation: "2" },
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		const first = await service.list({ ...partition(), limit: 1 });
		if (first.nextAfterGoalKey === undefined) throw new Error("Expected a second page.");
		const second = await service.list({ ...partition(), afterGoalKey: first.nextAfterGoalKey, limit: 1 });
		expect(first.records.map((record) => record.goalId)).toEqual(["goal-1"]);
		expect(second.records.map((record) => record.goalId)).toEqual(["goal-2"]);

		const cancelled = await service.update({
			...goalKey("goal-1"),
			expectedRevision: "1",
			status: "cancelled",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		expect((await service.list({ ...partition(), statuses: ["cancelled"], limit: 4 })).records).toHaveLength(1);
		expect(await service.purgeTerminal(partition(), "2026-08-28T00:00:02.000Z", 1)).toEqual([cancelled.goalKey]);
		expect(await service.get(goalKey("goal-1"))).toBeUndefined();
	});

	it("exposes optional Pi tools without creating another execution loop", async () => {
		const service = acceptingService();
		const tools = createGoalTools({
			service,
			readPartition: partition,
			readGameTime: () => gameTime("1"),
			recordedAt: () => "2026-08-28T00:00:00.000Z",
			goalId: (toolCallId) => `goal-${toolCallId}`,
			stepId: (toolCallId, index) => `step-${toolCallId}-${index}`,
		});
		expect(tools.map((tool) => [tool.name, tool.executionMode])).toEqual([
			["goal_create", "sequential"],
			["goal_update", "sequential"],
			["goal_read", "parallel"],
		]);
		const create = tools[0];
		const read = tools[2];
		if (create === undefined || read === undefined) throw new Error("Goal tools are missing.");
		const created = await create.execute("call-1", {
			objective: "Tool-owned optional goal",
			plan: [{ description: "Inspect", status: "in_progress" }],
		} as never);
		expect(created.details).toMatchObject({ goalId: "goal-call-1", revision: "1", status: "active" });
		const fetched = await read.execute("read", { goalId: "goal-call-1" } as never);
		expect(JSON.parse(fetched.content[0]?.type === "text" ? fetched.content[0].text : "{}")).toMatchObject({
			goalId: "goal-call-1",
			objective: "Tool-owned optional goal",
		});
	});

	it("passes the exact Tool cancellation signal into goal creation", async () => {
		const service = acceptingService();
		const create = createGoalTools({
			service,
			readPartition: partition,
			readGameTime: () => gameTime("1"),
			recordedAt: () => "2026-08-28T00:00:00.000Z",
			goalId: (toolCallId) => `goal-${toolCallId}`,
			stepId: (toolCallId, index) => `step-${toolCallId}-${index}`,
		})[0];
		if (create === undefined) throw new Error("Goal create Tool is missing.");
		const controller = new AbortController();
		const cancellation = new Error("run authority revoked");
		controller.abort(cancellation);

		await expect(
			create.execute(
				"cancelled",
				{ objective: "Must not survive revocation", plan: [{ description: "Start", status: "in_progress" }] } as never,
				controller.signal,
			),
		).rejects.toBe(cancellation);
		expect(await service.get(goalKey("goal-cancelled"))).toBeUndefined();
	});

	it("admits a complete model goal result at its byte budget and rejects one byte less before create", async () => {
		const objective = "Bound the complete model projection";
		const projectedPlan = [step("step-call-1-0", "Inspect", "in_progress")];
		const modelResultBytes = initialGoalModelResultBytes({
			goalId: "goal-call-1",
			objective,
			plan: projectedPlan,
		});
		const makeTools = (service: GoalService) =>
			createGoalTools({
				service,
				readPartition: partition,
				readGameTime: () => gameTime("1"),
				recordedAt: () => "2026-08-28T00:00:00.000Z",
				goalId: (toolCallId) => `goal-${toolCallId}`,
				stepId: (toolCallId, index) => `step-${toolCallId}-${index}`,
			});

		const acceptedLimits = { ...limits, maxModelResultBytes: modelResultBytes };
		const accepted = new GoalService(new InMemoryGoalStore(acceptedLimits), { validate: async () => true });
		const acceptedResult = await makeTools(accepted)[0]?.execute("call-1", {
			objective,
			plan: [{ description: "Inspect", status: "in_progress" }],
		} as never);
		if (acceptedResult === undefined) throw new Error("Goal create Tool is missing.");
		const acceptedText = acceptedResult.content[0]?.type === "text" ? acceptedResult.content[0].text : "";
		expect(new TextEncoder().encode(acceptedText).byteLength).toBe(modelResultBytes);

		const rejectedLimits = { ...limits, maxModelResultBytes: modelResultBytes - 1 };
		const rejectedStore = new InMemoryGoalStore(rejectedLimits);
		const rejected = new GoalService(rejectedStore, { validate: async () => true });
		await expect(
			makeTools(rejected)[0]?.execute("call-1", {
				objective,
				plan: [{ description: "Inspect", status: "in_progress" }],
			} as never),
		).rejects.toMatchObject({ code: "goal_model_result_too_large" });
		expect(await rejectedStore.get(goalKey("goal-call-1"))).toBeUndefined();
	});

	it("rejects an oversized model result before an update can commit a larger replan", async () => {
		const initialPlan = [step("step-1", "Inspect", "in_progress")];
		const modelResultBytes = initialGoalModelResultBytes({
			goalId: "goal-update-budget",
			objective: "Keep the original revision",
			plan: initialPlan,
		});
		const boundedLimits = { ...limits, maxModelResultBytes: modelResultBytes };
		const service = new GoalService(new InMemoryGoalStore(boundedLimits), { validate: async () => true });
		await service.create({
			goalId: "goal-update-budget",
			partition: partition(),
			objective: "Keep the original revision",
			plan: initialPlan,
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const update = createGoalTools({
			service,
			readPartition: partition,
			readGameTime: () => gameTime("2"),
			recordedAt: () => "2026-08-28T00:00:01.000Z",
			goalId: (toolCallId) => `goal-${toolCallId}`,
			stepId: (toolCallId, index) => `step-${toolCallId}-${index}`,
		}).find((tool) => tool.name === "goal_update");
		if (update === undefined) throw new Error("Goal update Tool is missing.");

		await expect(
			update.execute("update", {
				goalId: "goal-update-budget",
				expectedRevision: "1",
				plan: [initialPlan[0], step("step-2", "Report", "pending")],
			} as never),
		).rejects.toMatchObject({ code: "goal_model_result_too_large" });
		expect(await service.get(goalKey("goal-update-budget"))).toMatchObject({
			revision: "1",
			plan: initialPlan,
		});
	});

	it("rejects host advancement before evidence can make the model result exceed its budget", async () => {
		const initialPlan = [step("step-1", "Inspect", "in_progress")];
		const modelResultBytes = initialGoalModelResultBytes({
			goalId: "goal-host-result-budget",
			objective: "Keep host advancement readable",
			plan: initialPlan,
		});
		const boundedLimits = { ...limits, maxModelResultBytes: modelResultBytes };
		const service = new GoalService(
			new InMemoryGoalStore(boundedLimits),
			{ validate: async () => true },
			{ validate: async () => true },
		);
		await service.create({
			goalId: "goal-host-result-budget",
			partition: partition(),
			objective: "Keep host advancement readable",
			plan: initialPlan,
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});

		await expect(
			service.advanceFromHost({
				goalId: "goal-host-result-budget",
				partition: partition(),
				stepId: "step-1",
				inputId: "input-1",
				expectedRevision: "1",
				evidence: { kind: "action_receipt", referenceId: "receipt-1" },
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toMatchObject({ code: "goal_model_result_too_large" });
		expect(await service.get(goalKey("goal-host-result-budget"))).toMatchObject({
			revision: "1",
			status: "active",
			plan: initialPlan,
		});
	});

	it("keeps goal read and update tools inside the exact session and actor partition", async () => {
		const service = acceptingService();
		await service.create({
			goalId: "goal-private",
			partition: partition(),
			objective: "Stay private",
			plan: [step("step-private", "Inspect", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const tools = createGoalTools({
			service,
			readPartition: () => ({ ...partition(), sessionId: "session-other", actorId: "actor-other" }),
			readGameTime: () => gameTime("2"),
			recordedAt: () => "2026-08-28T00:00:01.000Z",
			goalId: (toolCallId) => `goal-${toolCallId}`,
			stepId: (toolCallId, index) => `step-${toolCallId}-${index}`,
		});
		const update = tools.find((tool) => tool.name === "goal_update");
		const read = tools.find((tool) => tool.name === "goal_read");
		if (update === undefined || read === undefined) throw new Error("Goal tools are missing.");

		const fetched = await read.execute("read", { goalId: "goal-private" } as never);
		expect(JSON.parse(fetched.content[0]?.type === "text" ? fetched.content[0].text : "{}")).toEqual({
			found: false,
		});
		await expect(
			update.execute("update", {
				goalId: "goal-private",
				expectedRevision: "1",
				status: "cancelled",
			} as never),
		).rejects.toBeInstanceOf(GoalNotFoundError);
		expect((await service.get(goalKey("goal-private")))?.status).toBe("active");
	});

	it("can keep host-owned progress out of the model-visible tool set", () => {
		const tools = createGoalTools({
			service: acceptingService(),
			readPartition: partition,
			readGameTime: () => gameTime("1"),
			recordedAt: () => "2026-08-28T00:00:00.000Z",
			goalId: (toolCallId) => `goal-${toolCallId}`,
			stepId: (toolCallId, index) => `step-${toolCallId}-${index}`,
			includeModelUpdateTool: false,
		});
		expect(tools.map((tool) => tool.name)).toEqual(["goal_create", "goal_read"]);
	});
});

function acceptingService(): GoalService {
	return new GoalService(new InMemoryGoalStore(limits), { validate: async () => true });
}

function initialGoalRecordBytes(draft: {
	readonly goalId: string;
	readonly partition: ReturnType<typeof partition>;
	readonly objective: string;
	readonly plan: readonly GoalPlanStep[];
	readonly gameTime: ReturnType<typeof gameTime>;
	readonly recordedAt: string;
}): number {
	return canonicalBytes({
		goalId: draft.goalId,
		partition: draft.partition,
		objective: draft.objective,
		plan: draft.plan,
		gameTime: draft.gameTime,
		goalKey: "1",
		revision: "1",
		status: "active",
		createdGameTime: draft.gameTime,
		createdAt: draft.recordedAt,
		updatedAt: draft.recordedAt,
		lastHostAdvanceFingerprint: null,
	});
}

function initialGoalModelResultBytes(input: {
	readonly goalId: string;
	readonly objective: string;
	readonly plan: readonly GoalPlanStep[];
}): number {
	return canonicalBytes({
		goalId: input.goalId,
		revision: "1",
		status: "active",
		objective: input.objective,
		plan: input.plan,
	});
}

function canonicalBytes(value: unknown): number {
	return new TextEncoder().encode(canonicalizeJsonValue(value)).byteLength;
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

function goalKey(goalId: string) {
	return { ...partition(), goalId };
}

function gameTime(tick: string) {
	return { timelineId: "world", generation: "1", tick } as const;
}

function step(
	stepId: string,
	description: string,
	status: GoalPlanStep["status"],
	evidence: GoalPlanStep["evidence"] = [],
): GoalPlanStep {
	return { stepId, description, status, evidence };
}
