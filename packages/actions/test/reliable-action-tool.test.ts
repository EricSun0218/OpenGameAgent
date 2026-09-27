import { DURABLE_TOOL_EXECUTION, DurableToolExecutionUnresolvedError } from "@opengameagent/agent";
import type { DurableTranscriptToolStartedRecord, RunCoordinate } from "@opengameagent/protocol";
import { Type } from "typebox";
import { describe, expect, it, vi } from "vitest";
import {
	ActionApprovalConflictError,
	ActionApprovalCoordinator,
	ActionApprovalDeniedError,
	type ActionApprovalRecord,
	type ActionAuthority,
	ActionCoordinator,
	type ActionInvocationScope,
	type ActionRecord,
	type ApprovedActionAdmission,
	createReliableActionTool,
	createReliableActionTranscriptRecovery,
	InMemoryActionApprovalStore,
	InMemoryActionJournal,
} from "../src/index.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;
const PARAMETERS = Type.Object({ value: Type.Number() }, { additionalProperties: false });

function clock() {
	return () => new Date("2026-08-28T00:00:00.000Z");
}

function fixture(
	mode: "explicit-only" | "confirm-once" | "allowed-in-task",
	operationKey: (toolCallId: string) => string = (toolCallId) => toolCallId,
	presentation?: {
		limits: { maxBytes: number; maxDepth: number };
		project(parameters: { value: number }, signal: AbortSignal): unknown | Promise<unknown>;
	},
) {
	const journal = new InMemoryActionJournal();
	const approvalStore = new InMemoryActionApprovalStore();
	const actions = new ActionCoordinator(journal, {
		now: clock(),
		newClaimToken: () => "claim-token",
	});
	const approvals = new ActionApprovalCoordinator(approvalStore, { now: clock() });
	const seenApprovals: ActionApprovalRecord[] = [];
	let revision = "7";
	const tool = createReliableActionTool({
		...STREAM,
		name: "world.write",
		label: "World write",
		description: "Apply one authoritative game write.",
		parameters: PARAMETERS,
		actions,
		policy: { policyId: "high-risk", mode },
		approval: {
			coordinator: approvals,
			admission: testAdmission(journal, approvalStore),
			durationMs: 1_000,
			...(presentation === undefined ? {} : { presentation }),
			waitForDecision: async (pending) => {
				seenApprovals.push(pending);
				return approvals.decide({
					...STREAM,
					approvalId: pending.request.approvalId,
					expectedRevision: pending.revision,
					requestFingerprint: pending.request.requestFingerprint,
					decision: "approve",
					reviewerId: "player",
				});
			},
		},
		readCoordinate: () => ({ ...STREAM, runId: "run", turn: "2" }),
		readAuthority: () => ({ timelineId: "world", generation: "3", revision }),
		resolveScope: () => ({}),
		operationKey,
		conflictKeys: () => ["world:inventory"],
		replayPolicy: "recoverable",
		waitForSettlement: async (submitted) => {
			const delivery = await actions.nextDelivery({
				...STREAM,
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 1,
			});
			if (
				delivery?.kind !== "dispatch" ||
				!delivery.action.claim ||
				delivery.action.operationId !== submitted.operationId
			) {
				throw new Error("Expected the submitted action claim.");
			}
			const claim = delivery.action.claim;
			return actions.recordReceipt({
				...STREAM,
				operationId: submitted.operationId,
				claimToken: claim.claimToken,
				attempt: claim.attempt,
				outcome: "succeeded",
				result: { privateGameReceipt: "secret" },
				modelResult: { message: "done" },
			});
		},
	});
	return { actions, approvals, seenApprovals, tool, setRevision: (value: string) => (revision = value) };
}

describe("createReliableActionTool", () => {
	it("classifies every reliable Action as a sequential durable Tool", () => {
		const executionMode: "sequential" = fixture("allowed-in-task").tool.executionMode;
		expect(executionMode).toBe("sequential");
	});

	it.each(["explicit-only", "confirm-once"] as const)(
		"binds one host-resolved Goal projection into the exact %s Action admission",
		async (policyMode) => {
			const resolveGoalProjection = vi.fn(async ({ value }: { value: number }) => ({
				goalId: `goal-${value}`,
				partition: {
					ownerId: "npc-1",
					timelineId: "world",
					generation: "3",
					sessionId: "session",
					actorId: "actor",
				},
				stepId: "step-1",
				expectedRevision: "4",
				inputId: "input-1",
			}));
			const configured = cancellationFixture({
				policyMode,
				goalProjectionLimits: {
					maxBindingBytes: 1_024,
					maxPendingPerStream: 2,
					maxListBatchSize: 2,
					maxRecordBytes: 8_192,
				},
				resolveGoalProjection,
				waitForSettlement: async (submitted) => ({ ...submitted, status: "failed" }),
			});

			const result = await configured.tool.execute("call", { value: 7 });
			const operationId = result.details.operationId;

			expect(resolveGoalProjection).toHaveBeenCalledOnce();
			expect(resolveGoalProjection).toHaveBeenCalledWith({ value: 7 }, expect.any(AbortSignal));
			expect(JSON.stringify(result)).not.toContain("goal-7");
			await expect(configured.actions.get(STREAM, operationId)).resolves.toMatchObject({
				payload: { value: 7 },
				goalProjection: {
					goalId: "goal-7",
					partition: { timelineId: "world", generation: "3", sessionId: "session", actorId: "actor" },
					stepId: "step-1",
					expectedRevision: "4",
					inputId: "input-1",
				},
			});
		},
	);

	it("denies explicit-only execution without host attestation before creating an intent", async () => {
		const { actions, tool } = fixture("explicit-only");
		const denied = await tool.execute("call", { value: 1 }).catch((error: unknown) => error);
		expect(denied).toBeInstanceOf(ActionApprovalDeniedError);
		expect(denied).not.toBeInstanceOf(DurableToolExecutionUnresolvedError);
		await expect(actions.get(STREAM, `act_${"0".repeat(64)}`)).resolves.toBeUndefined();
	});

	it("consumes a content-bound approval, dispatches once, and projects only the model-safe receipt", async () => {
		const { tool } = fixture("confirm-once");
		const result = await tool.execute("call", { value: 1 });
		const serialized = JSON.stringify(result);

		expect(result.content).toEqual([
			expect.objectContaining({ type: "text", text: expect.stringContaining('"message":"done"') }),
		]);
		expect(serialized).not.toContain("privateGameReceipt");
		expect(serialized).not.toContain("secret");
	});

	it("projects validated parameters into the exact durable approval before admission", async () => {
		const project = vi.fn((parameters: { value: number }) => ({
			kind: "game-choice",
			optionId: `option-${parameters.value}`,
		}));
		const { seenApprovals, tool } = fixture("confirm-once", (toolCallId) => toolCallId, {
			limits: { maxBytes: 128, maxDepth: 2 },
			project,
		});

		await expect(tool.execute("call", { value: 7 })).resolves.toMatchObject({
			details: { status: "succeeded" },
		});

		expect(project).toHaveBeenCalledOnce();
		expect(project).toHaveBeenCalledWith({ value: 7 }, expect.any(AbortSignal));
		expect(seenApprovals).toHaveLength(1);
		expect(seenApprovals[0]?.request).toMatchObject({
			actionName: "world.write",
			presentation: { kind: "game-choice", optionId: "option-7" },
		});
	});

	it("captures one immutable presentation budget before invoking the host projector", async () => {
		let limitsReads = 0;
		let maxBytesReads = 0;
		let maxDepthReads = 0;
		const firstLimits = {
			get maxBytes() {
				maxBytesReads += 1;
				return 128;
			},
			get maxDepth() {
				maxDepthReads += 1;
				return 2;
			},
		};
		const presentation = {
			get limits() {
				limitsReads += 1;
				return limitsReads === 1 ? firstLimits : { maxBytes: 1, maxDepth: 0 };
			},
			project: () => {
				expect({ limitsReads, maxBytesReads, maxDepthReads }).toEqual({
					limitsReads: 1,
					maxBytesReads: 1,
					maxDepthReads: 1,
				});
				return { optionId: "opaque-option-7" };
			},
		};
		const { seenApprovals, tool } = fixture("confirm-once", (toolCallId) => toolCallId, presentation);

		await expect(tool.execute("call", { value: 7 })).resolves.toMatchObject({
			details: { status: "succeeded" },
		});
		expect(limitsReads).toBe(1);
		expect(seenApprovals[0]?.request.presentation).toEqual({ optionId: "opaque-option-7" });
	});

	it("rejects a decided approval record for a different presentation request before admission", async () => {
		let capturedIntent: Parameters<ActionApprovalCoordinator["request"]>[1] | undefined;
		let requestApproval: ActionApprovalCoordinator["request"];
		const configured = cancellationFixture({
			presentation: {
				limits: { maxBytes: 128, maxDepth: 2 },
				project: () => ({ optionId: "current-option" }),
			},
			waitForDecision: async () => {
				if (!capturedIntent) throw new Error("Expected the prepared approval intent.");
				const stale = await requestApproval("high-risk", capturedIntent, 1_000, {
					presentation: {
						value: { optionId: "stale-option" },
						limits: { maxBytes: 128, maxDepth: 2 },
					},
				});
				return configured.approvals.decide({
					...STREAM,
					approvalId: stale.request.approvalId,
					expectedRevision: stale.revision,
					requestFingerprint: stale.request.requestFingerprint,
					decision: "approve",
					reviewerId: "player",
				});
			},
		});
		requestApproval = configured.approvals.request.bind(configured.approvals);
		vi.spyOn(configured.approvals, "request").mockImplementation(async (...arguments_) => {
			capturedIntent = arguments_[1];
			return requestApproval(...arguments_);
		});
		const admitted = vi.spyOn(configured.admission, "admit");

		await expect(configured.tool.execute("call", { value: 7 })).rejects.toThrow("different approval request");
		expect(admitted).not.toHaveBeenCalled();
		await expect(noAction(configured.actions)).resolves.toBeUndefined();
	});

	it("rejects an oversized presentation before creating a durable approval or Action", async () => {
		const { actions, approvals, tool } = fixture("confirm-once", (toolCallId) => toolCallId, {
			limits: { maxBytes: 8, maxDepth: 2 },
			project: () => ({ label: "这个展示内容超过宿主预算" }),
		});

		await expect(tool.execute("call", { value: 7 })).rejects.toThrow("configured byte budget");
		await expect(approvals.listPending({ ...STREAM, limit: 1 })).resolves.toEqual([]);
		await expect(noAction(actions)).resolves.toBeUndefined();
	});

	it("rejects an over-deep presentation before creating a durable approval or Action", async () => {
		const { actions, approvals, tool } = fixture("confirm-once", (toolCallId) => toolCallId, {
			limits: { maxBytes: 128, maxDepth: 1 },
			project: () => ({ option: { id: "opaque-option-7" } }),
		});

		await expect(tool.execute("call", { value: 7 })).rejects.toThrow("configured depth");
		await expect(approvals.listPending({ ...STREAM, limit: 1 })).resolves.toEqual([]);
		await expect(noAction(actions)).resolves.toBeUndefined();
	});

	it("fails closed when world authority changes after the approval decision", async () => {
		const journal = new InMemoryActionJournal();
		const approvalStore = new InMemoryActionApprovalStore();
		const actions = new ActionCoordinator(journal);
		const approvals = new ActionApprovalCoordinator(approvalStore, { now: clock() });
		let revision = "7";
		const tool = createReliableActionTool({
			...STREAM,
			name: "world.write",
			label: "World write",
			description: "Apply one authoritative game write.",
			parameters: PARAMETERS,
			actions,
			policy: { policyId: "high-risk", mode: "confirm-once" },
			approval: {
				coordinator: approvals,
				admission: testAdmission(journal, approvalStore),
				durationMs: 1_000,
				waitForDecision: async (pending) => {
					const approved = await approvals.decide({
						...STREAM,
						approvalId: pending.request.approvalId,
						expectedRevision: pending.revision,
						requestFingerprint: pending.request.requestFingerprint,
						decision: "approve",
						reviewerId: "player",
					});
					revision = "8";
					return approved;
				},
			},
			readCoordinate: () => ({ ...STREAM, runId: "run", turn: "2" }),
			readAuthority: () => ({ timelineId: "world", generation: "3", revision }),
			resolveScope: () => ({}),
			operationKey: (toolCallId) => toolCallId,
			conflictKeys: () => ["world:inventory"],
			replayPolicy: "never",
			waitForSettlement: async () => {
				throw new Error("The action must not be submitted after authority drift.");
			},
		});

		const unresolved = await tool.execute("call", { value: 1 }).catch((error: unknown) => error);
		expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
		expect(unresolved).toHaveProperty("cause", expect.any(ActionApprovalConflictError));
	});

	it("records and executes the same prepared operation-key snapshot", async () => {
		let operationKeyReads = 0;
		const { tool } = fixture("confirm-once", () => `operation-key-${++operationKeyReads}`);
		const prepared = await tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });
		const result = await prepared.execute();

		expect(operationKeyReads).toBe(1);
		expect(result.details.operationId).toBe(prepared.operationId);
	});

	it("does not request approval after caller cancellation during scope resolution", async () => {
		const scope = deferred<ActionInvocationScope>();
		const entered = deferred<void>();
		const configured = cancellationFixture({
			resolveScope: () => {
				entered.resolve(undefined);
				return scope.promise;
			},
		});
		const controller = new AbortController();
		const reason = new Error("scope cancelled");
		const execution = configured.tool.execute("call", { value: 1 }, controller.signal);
		await entered.promise;
		controller.abort(reason);
		scope.resolve({});

		await expect(execution).rejects.toBe(reason);
		await expect(configured.approvals.listPending({ ...STREAM, limit: 1 })).resolves.toEqual([]);
		await expect(noAction(configured.actions)).resolves.toBeUndefined();
	});

	it("does not request approval after caller cancellation during presentation projection", async () => {
		const entered = deferred<void>();
		const projected = deferred<unknown>();
		const waitForDecision = vi.fn(async () => {
			throw new Error("A cancelled presentation must not reach approval waiting.");
		});
		const configured = cancellationFixture({
			presentation: {
				limits: { maxBytes: 128, maxDepth: 2 },
				project: () => {
					entered.resolve(undefined);
					return projected.promise;
				},
			},
			waitForDecision,
		});
		const controller = new AbortController();
		const reason = new Error("presentation cancelled");
		const execution = configured.tool.execute("call", { value: 1 }, controller.signal);
		await entered.promise;
		controller.abort(reason);
		projected.resolve({ optionId: "opaque-option-7" });

		await expect(execution).rejects.toBe(reason);
		expect(waitForDecision).not.toHaveBeenCalled();
		await expect(configured.approvals.listPending({ ...STREAM, limit: 1 })).resolves.toEqual([]);
		await expect(noAction(configured.actions)).resolves.toBeUndefined();
	});

	it("leaves a pending approval but does not wait after cancellation during its request", async () => {
		const persisted = deferred<ActionApprovalRecord>();
		const release = deferred<void>();
		const waitForDecision = vi.fn(async () => {
			throw new Error("The cancelled Tool must not wait for approval.");
		});
		const configured = cancellationFixture({ waitForDecision });
		const originalRequest = configured.approvals.request.bind(configured.approvals);
		vi.spyOn(configured.approvals, "request").mockImplementation(async (...arguments_) => {
			const pending = await originalRequest(...arguments_);
			persisted.resolve(pending);
			await release.promise;
			return pending;
		});
		const controller = new AbortController();
		const reason = new Error("approval request cancelled");
		const execution = configured.tool.execute("call", { value: 1 }, controller.signal);
		const pending = await persisted.promise;
		controller.abort(reason);
		release.resolve(undefined);

		await expect(execution).rejects.toBe(reason);
		expect(waitForDecision).not.toHaveBeenCalled();
		await expect(configured.approvals.get(STREAM, pending.request.approvalId)).resolves.toMatchObject({
			status: "pending",
		});
		await expect(noAction(configured.actions)).resolves.toBeUndefined();
	});

	it("does not re-read authority or admit after cancellation while awaiting approval", async () => {
		const decision = deferred<ActionApprovalRecord>();
		let pending: ActionApprovalRecord | undefined;
		const readAuthority = vi.fn(async () => authority());
		const configured = cancellationFixture({
			readAuthority,
			waitForDecision: async (approval) => {
				pending = approval;
				return decision.promise;
			},
		});
		const controller = new AbortController();
		const reason = new Error("approval wait cancelled");
		const execution = configured.tool.execute("call", { value: 1 }, controller.signal);
		await vi.waitFor(() => expect(pending).toBeDefined());
		if (!pending) throw new Error("Expected a pending approval fixture.");
		controller.abort(reason);
		decision.resolve(
			await configured.approvals.decide({
				...STREAM,
				approvalId: pending.request.approvalId,
				expectedRevision: pending.revision,
				requestFingerprint: pending.request.requestFingerprint,
				decision: "approve",
				reviewerId: "player",
			}),
		);

		await expect(execution).rejects.toBe(reason);
		expect(readAuthority).toHaveBeenCalledTimes(1);
		await expect(configured.approvals.get(STREAM, pending.request.approvalId)).resolves.toMatchObject({
			status: "approved",
		});
		await expect(noAction(configured.actions)).resolves.toBeUndefined();
	});

	it("does not consume approval or admit after cancellation during the authority recheck", async () => {
		const recheck = deferred<ActionAuthority>();
		let authorityReads = 0;
		let approved: ActionApprovalRecord | undefined;
		const configured = cancellationFixture({
			readAuthority: async () => {
				authorityReads += 1;
				return authorityReads === 1 ? authority() : recheck.promise;
			},
			waitForDecision: async (pending) => {
				approved = await configured.approvals.decide({
					...STREAM,
					approvalId: pending.request.approvalId,
					expectedRevision: pending.revision,
					requestFingerprint: pending.request.requestFingerprint,
					decision: "approve",
					reviewerId: "player",
				});
				return approved;
			},
		});
		const controller = new AbortController();
		const reason = new Error("authority recheck cancelled");
		const execution = configured.tool.execute("call", { value: 1 }, controller.signal);
		await vi.waitFor(() => expect(authorityReads).toBe(2));
		controller.abort(reason);
		recheck.resolve({
			get timelineId(): string {
				throw new Error("aborted authority must not be inspected");
			},
			generation: "3",
			revision: "7",
		});

		await expect(execution).rejects.toBe(reason);
		if (!approved) throw new Error("Expected an approved request fixture.");
		await expect(configured.approvals.get(STREAM, approved.request.approvalId)).resolves.toMatchObject({
			status: "approved",
		});
		await expect(noAction(configured.actions)).resolves.toBeUndefined();
	});

	it("captures the approval task before the first authority wait", async () => {
		const scope = { taskId: "task-original" };
		const authorityRead = deferred<ActionAuthority>();
		const entered = deferred<void>();
		const configured = cancellationFixture({
			resolveScope: () => scope,
			readAuthority: () => {
				entered.resolve(undefined);
				return authorityRead.promise;
			},
			waitForDecision: async (pending) => pending,
		});
		const requested = vi.spyOn(configured.approvals, "request");
		const execution = configured.tool.execute("call", { value: 1 });
		await entered.promise;
		scope.taskId = "task-mutated";
		authorityRead.resolve(authority());

		await expect(execution).rejects.toBeInstanceOf(ActionApprovalDeniedError);
		expect(requested).toHaveBeenCalledWith("high-risk", expect.any(Object), 1_000, { taskId: "task-original" });
	});

	it("captures the game coordinate before the first authority wait", async () => {
		const coordinate = { ...STREAM, runId: "run-original", turn: "2" };
		const authorityRead = deferred<ActionAuthority>();
		const entered = deferred<void>();
		const configured = cancellationFixture({
			policyMode: "explicit-only",
			readCoordinate: () => coordinate,
			readAuthority: () => {
				entered.resolve(undefined);
				return authorityRead.promise;
			},
			waitForSettlement: async (submitted) => ({ ...submitted, status: "failed" }),
		});
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });
		const operationId = requiredOperationId(prepared.operationId);
		const execution = prepared.execute();
		await entered.promise;
		coordinate.runId = "run-mutated";
		coordinate.turn = "9";
		authorityRead.resolve(authority());

		await expect(execution).resolves.toMatchObject({ details: { status: "failed" } });
		await expect(configured.actions.get(STREAM, operationId)).resolves.toMatchObject({
			coordinate: { ...STREAM, runId: "run-original", turn: "2" },
		});
	});

	it("captures the approval identity before the second authority wait", async () => {
		const authorityRecheck = deferred<ActionAuthority>();
		const recheckEntered = deferred<void>();
		let authorityReads = 0;
		let returnedDecision: ActionApprovalRecord | undefined;
		let configured!: ReturnType<typeof cancellationFixture>;
		configured = cancellationFixture({
			readAuthority: () => {
				authorityReads += 1;
				if (authorityReads === 1) return authority();
				recheckEntered.resolve(undefined);
				return authorityRecheck.promise;
			},
			waitForDecision: async (pending) => {
				returnedDecision = await configured.approvals.decide({
					...STREAM,
					approvalId: pending.request.approvalId,
					expectedRevision: pending.revision,
					requestFingerprint: pending.request.requestFingerprint,
					decision: "approve",
					reviewerId: "player",
				});
				return returnedDecision;
			},
			waitForSettlement: async (submitted) => ({ ...submitted, status: "failed" }),
		});
		const admitted = vi.spyOn(configured.admission, "admit");
		const execution = configured.tool.execute("call", { value: 1 });
		await recheckEntered.promise;
		if (!returnedDecision) throw new Error("Expected an approved decision.");
		const approvalId = returnedDecision.request.approvalId;
		const expectedRevision = returnedDecision.revision;
		returnedDecision.request.approvalId = "approval-mutated";
		returnedDecision.revision = "999";
		authorityRecheck.resolve(authority());

		await expect(execution).resolves.toMatchObject({ details: { status: "failed" } });
		expect(admitted).toHaveBeenCalledWith(expect.objectContaining({ approvalId, expectedRevision }));
	});

	it("captures the rechecked authority before approved admission waits", async () => {
		const currentAuthority = authority();
		let authorityReads = 0;
		const admissionEntered = deferred<void>();
		const admissionReleased = deferred<void>();
		const configured = cancellationFixture({
			readAuthority: () => {
				authorityReads += 1;
				return authorityReads === 1 ? authority() : currentAuthority;
			},
			waitForSettlement: async (submitted) => ({ ...submitted, status: "failed" }),
		});
		const originalAdmit = configured.admission.admit.bind(configured.admission);
		let admittedAuthority: ActionAuthority | undefined;
		vi.spyOn(configured.admission, "admit").mockImplementation(async (input) => {
			admittedAuthority = input.currentAuthority;
			admissionEntered.resolve(undefined);
			await admissionReleased.promise;
			return originalAdmit(input);
		});
		const execution = configured.tool.execute("call", { value: 1 });
		await admissionEntered.promise;
		currentAuthority.revision = "9";
		admissionReleased.resolve(undefined);

		await expect(execution).resolves.toMatchObject({ details: { status: "failed" } });
		expect(admittedAuthority).toEqual(authority());
	});

	it("checks approved admission against the prepared identity snapshot", async () => {
		const configured = cancellationFixture({ policyMode: "confirm-once" });
		vi.spyOn(configured.admission, "admit").mockImplementation(async (input) => {
			const approval = await configured.approvals.get(STREAM, input.approvalId);
			if (!approval) throw new Error("Expected the approval record.");
			return {
				approval: { ...approval, status: "consumed", consumedAt: "2026-08-28T00:00:00.000Z" },
				action: {
					...structuredClone(input.intent),
					operationId: `act_${"5".repeat(64)}`,
					intentFingerprint: "6".repeat(64),
					status: "pending",
					attempt: "0",
					createdAt: "2026-08-28T00:00:00.000Z",
					updatedAt: "2026-08-28T00:00:00.000Z",
				},
			};
		});
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });

		const unresolved = await prepared.execute().catch((error: unknown) => error);
		expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
		expect(unresolved).toHaveProperty(
			"cause",
			expect.objectContaining({ message: "The action admission returned a different intent." }),
		);
	});

	it("prevents approved admission from mutating the prepared identity", async () => {
		const configured = cancellationFixture({ policyMode: "confirm-once" });
		const originalAdmit = configured.admission.admit.bind(configured.admission);
		vi.spyOn(configured.admission, "admit").mockImplementation(async (input) => {
			const mutableIntent = input.intent as { operationId: string };
			mutableIntent.operationId = `act_${"7".repeat(64)}`;
			return originalAdmit(input);
		});
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });

		const unresolved = await prepared.execute().catch((error: unknown) => error);
		expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
		expect(unresolved).toHaveProperty("cause", expect.any(TypeError));
		await expect(noAction(configured.actions)).resolves.toBeUndefined();
	});

	it.each(["operationId", "intentFingerprint"] as const)(
		"rejects a terminal settlement record with a different %s",
		async (changedField) => {
			const configured = cancellationFixture({
				policyMode: "explicit-only",
				waitForSettlement: async (submitted) =>
					changedField === "operationId"
						? { ...submitted, operationId: `act_${"1".repeat(64)}`, status: "succeeded" }
						: { ...submitted, intentFingerprint: "2".repeat(64), status: "succeeded" },
			});
			const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });
			const operationId = requiredOperationId(prepared.operationId);

			const unresolved = await prepared.execute().catch((error: unknown) => error);
			expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
			expect(unresolved).toHaveProperty(
				"cause",
				expect.objectContaining({ message: expect.stringContaining("different action") }),
			);
			await expect(configured.actions.get(STREAM, operationId)).resolves.toMatchObject({ status: "pending" });
		},
	);

	it("marks a submit rejection as unresolved from the admission boundary", async () => {
		const configured = cancellationFixture({ policyMode: "explicit-only" });
		const failure = new Error("submit bridge failed");
		vi.spyOn(configured.actions, "submitPrepared").mockRejectedValue(failure);
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });

		const unresolved = await prepared.execute().catch((error: unknown) => error);
		expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
		expect(unresolved).toHaveProperty("cause", failure);
	});

	it("marks an approved admission rejection as unresolved", async () => {
		const configured = cancellationFixture({ policyMode: "confirm-once" });
		const failure = new Error("approved admission bridge failed");
		vi.spyOn(configured.admission, "admit").mockRejectedValue(failure);
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });

		const unresolved = await prepared.execute().catch((error: unknown) => error);
		expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
		expect(unresolved).toHaveProperty("cause", failure);
	});

	it("marks a settlement wait rejection as unresolved after durable submit", async () => {
		const failure = new Error("settlement bridge failed");
		const configured = cancellationFixture({
			policyMode: "explicit-only",
			waitForSettlement: async () => {
				throw failure;
			},
		});
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });
		const operationId = requiredOperationId(prepared.operationId);

		const unresolved = await prepared.execute().catch((error: unknown) => error);
		expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
		expect(unresolved).toHaveProperty("cause", failure);
		await expect(configured.actions.get(STREAM, operationId)).resolves.toMatchObject({ status: "pending" });
	});

	it("keeps an authoritative no-Action admission denial as an ordinary Tool error", async () => {
		const configured = cancellationFixture({ policyMode: "confirm-once" });
		vi.spyOn(configured.admission, "admit").mockImplementation(async (input) => {
			const approval = await configured.approvals.get(STREAM, input.approvalId);
			if (!approval) throw new Error("Expected the approval record.");
			return { approval: { ...approval, status: "rejected" } };
		});
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });

		const denied = await prepared.execute().catch((error: unknown) => error);
		expect(denied).toBeInstanceOf(ActionApprovalDeniedError);
		expect(denied).not.toBeInstanceOf(DurableToolExecutionUnresolvedError);
	});

	it.each(["consumed-without-action", "rejected-with-action"] as const)(
		"marks an inconsistent approved admission result as unresolved: %s",
		async (variant) => {
			const configured = cancellationFixture({ policyMode: "confirm-once" });
			vi.spyOn(configured.admission, "admit").mockImplementation(async (input) => {
				const approval = await configured.approvals.get(STREAM, input.approvalId);
				if (!approval) throw new Error("Expected the approval record.");
				if (variant === "consumed-without-action") {
					return { approval: { ...approval, status: "consumed", consumedAt: "2026-08-28T00:00:00.000Z" } };
				}
				return {
					approval: { ...approval, status: "rejected" },
					action: await configured.actions.submit(input.intent),
				};
			});
			const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });

			const unresolved = await prepared.execute().catch((error: unknown) => error);
			expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
			expect(unresolved).toHaveProperty(
				"cause",
				expect.objectContaining({ message: "The approved admission returned an inconsistent Action result." }),
			);
		},
	);

	it("rejects settlement identity mutation against the pre-wait Action snapshot", async () => {
		const configured = cancellationFixture({
			policyMode: "explicit-only",
			waitForSettlement: async (submitted) => {
				submitted.operationId = `act_${"3".repeat(64)}`;
				submitted.intentFingerprint = "4".repeat(64);
				return { ...submitted, status: "succeeded" };
			},
		});
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 });

		const unresolved = await prepared.execute().catch((error: unknown) => error);
		expect(unresolved).toBeInstanceOf(DurableToolExecutionUnresolvedError);
		expect(unresolved).toHaveProperty(
			"cause",
			expect.objectContaining({ message: "The reliable action settlement bridge returned a different action." }),
		);
	});

	it("preserves a durable submit but stops the cancelled Tool call", async () => {
		const configured = cancellationFixture({ policyMode: "explicit-only" });
		const persisted = deferred<void>();
		const release = deferred<void>();
		const originalSubmit = configured.actions.submitPrepared.bind(configured.actions);
		vi.spyOn(configured.actions, "submitPrepared").mockImplementation(async (intent) => {
			const action = await originalSubmit(intent);
			persisted.resolve(undefined);
			await release.promise;
			return action;
		});
		const controller = new AbortController();
		const reason = new Error("submit cancelled");
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 }, controller.signal);
		const operationId = requiredOperationId(prepared.operationId);
		const execution = prepared.execute();
		await persisted.promise;
		controller.abort(reason);
		release.resolve(undefined);

		await expect(execution).rejects.toBe(reason);
		await expect(configured.actions.get(STREAM, operationId)).resolves.toMatchObject({ status: "pending" });
	});

	it("preserves a consumed approval and durable Action but stops the cancelled Tool call", async () => {
		const configured = cancellationFixture({});
		const persisted = deferred<void>();
		const release = deferred<void>();
		const originalAdmit = configured.admission.admit.bind(configured.admission);
		vi.spyOn(configured.admission, "admit").mockImplementation(async (input) => {
			const admitted = await originalAdmit(input);
			persisted.resolve(undefined);
			await release.promise;
			return admitted;
		});
		const controller = new AbortController();
		const reason = new Error("admission cancelled");
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 }, controller.signal);
		const operationId = requiredOperationId(prepared.operationId);
		const execution = prepared.execute();
		await persisted.promise;
		controller.abort(reason);
		release.resolve(undefined);

		await expect(execution).rejects.toBe(reason);
		await expect(configured.actions.get(STREAM, operationId)).resolves.toMatchObject({ status: "pending" });
		await expect(configured.approvals.listPending({ ...STREAM, limit: 1 })).resolves.toEqual([]);
	});

	it("keeps a completed Action recoverable when cancellation wins after settlement", async () => {
		const settled = deferred<void>();
		const release = deferred<void>();
		let actions!: ActionCoordinator;
		const configured = cancellationFixture({
			policyMode: "explicit-only",
			waitForSettlement: async (submitted) => {
				const delivery = await actions.nextDelivery({
					...STREAM,
					claimantId: "game",
					leaseDurationMs: 1_000,
					candidateLimit: 1,
				});
				if (delivery?.kind !== "dispatch" || !delivery.action.claim) {
					throw new Error("Expected a claimed Action fixture.");
				}
				const completed = await actions.recordReceipt({
					...STREAM,
					operationId: submitted.operationId,
					claimToken: delivery.action.claim.claimToken,
					attempt: delivery.action.claim.attempt,
					outcome: "succeeded",
					modelResult: { message: "done" },
				});
				settled.resolve(undefined);
				await release.promise;
				return completed;
			},
		});
		actions = configured.actions;
		const controller = new AbortController();
		const reason = new Error("settlement cancelled");
		const prepared = await configured.tool[DURABLE_TOOL_EXECUTION]("call", { value: 1 }, controller.signal);
		const operationId = requiredOperationId(prepared.operationId);
		const execution = prepared.execute();
		await settled.promise;
		controller.abort(reason);
		release.resolve(undefined);

		await expect(execution).rejects.toBe(reason);
		const recover = createReliableActionTranscriptRecovery({ actions, now: () => 1 });
		await expect(
			recover({
				start: started(operationId),
				toolCall: { type: "toolCall", id: "call", name: "world.write", arguments: { value: 1 } },
			}),
		).resolves.toMatchObject({ outcome: "completed", isError: false });
	});
});

describe("createReliableActionTranscriptRecovery", () => {
	it("recovers the same terminal Tool result before and after durable Goal projection settlement", async () => {
		let currentTime = "2026-08-28T00:00:00.000Z";
		const actions = new ActionCoordinator(
			new InMemoryActionJournal({
				goalProjection: {
					maxBindingBytes: 1_024,
					maxPendingPerStream: 2,
					maxListBatchSize: 2,
					maxRecordBytes: 8_192,
				},
			}),
			{ now: () => new Date(currentTime), newClaimToken: () => "claim-goal-recovery" },
		);
		const submitted = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "run", turn: "2" },
			actionName: "world.write",
			operationKey: "call",
			payload: { value: 1 },
			expectedAuthority: authority(),
			conflictKeys: ["world:inventory"],
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
				expectedRevision: "4",
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
		const received = await actions.recordReceipt({
			...STREAM,
			operationId: submitted.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "succeeded",
			modelResult: { message: "done" },
		});

		const recover = createReliableActionTranscriptRecovery({ actions, now: () => 1 });
		const recoveryRequest = {
			start: started(submitted.operationId),
			toolCall: { type: "toolCall" as const, id: "call", name: "world.write", arguments: { value: 1 } },
		};
		const beforeSettlement = await recover(recoveryRequest);
		expect(beforeSettlement).toMatchObject({
			outcome: "completed",
			isError: false,
			timestamp: Date.parse(received.receipt?.receivedAt ?? ""),
		});
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toMatchObject({
			candidates: [{ action: { operationId: submitted.operationId, goalProjection: { goalId: "goal-1" } } }],
		});

		currentTime = "2026-08-28T00:00:03.000Z";
		await actions.settleGoalProjection({
			...STREAM,
			operationId: submitted.operationId,
			intentFingerprint: submitted.intentFingerprint,
			outcome: "advanced",
		});
		await expect(recover(recoveryRequest)).resolves.toEqual(beforeSettlement);
		await expect(actions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toEqual({ candidates: [] });
	});

	it("projects an authoritative terminal receipt without replaying the action", async () => {
		const { actions, tool } = fixture("confirm-once");
		const executed = await tool.execute("call", { value: 1 });
		const operationId = (executed.details as { operationId: string }).operationId;
		const recover = createReliableActionTranscriptRecovery({ actions, now: () => 1 });

		const recovered = await recover({
			start: started(operationId),
			toolCall: { type: "toolCall", id: "call", name: "world.write", arguments: { value: 1 } },
		});

		expect(recovered).toMatchObject({
			outcome: "completed",
			isError: false,
			content: [{ text: expect.stringContaining('"message":"done"') }],
		});
		expect(JSON.stringify(recovered)).not.toContain("privateGameReceipt");
	});

	it("distinguishes a missing durable action from an unresolved admitted action", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), { now: clock() });
		const recover = createReliableActionTranscriptRecovery({ actions, now: () => 1 });
		const missingId = `act_${"0".repeat(64)}`;
		await expect(
			recover({
				start: started(missingId, "missing"),
				toolCall: { type: "toolCall", id: "missing", name: "world.write", arguments: {} },
			}),
		).resolves.toMatchObject({ outcome: "not_started", isError: true });

		const pending = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "run", turn: "2" },
			actionName: "world.write",
			operationKey: "pending",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "3", revision: "7" },
			conflictKeys: ["world:inventory"],
			replayPolicy: "recoverable",
		});
		await expect(
			recover({
				start: started(pending.operationId, "pending"),
				toolCall: { type: "toolCall", id: "pending", name: "world.write", arguments: { value: 1 } },
			}),
		).resolves.toMatchObject({
			outcome: "outcome_unknown",
			isError: true,
			content: [{ text: expect.stringContaining('"status":"pending"') }],
		});
	});

	it("declines a non-action operation namespace instead of declaring it not started", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), { now: clock() });
		const recover = createReliableActionTranscriptRecovery({ actions, now: () => 1 });

		await expect(
			recover({
				start: started("service-operation-1"),
				toolCall: { type: "toolCall", id: "call", name: "world.write", arguments: { value: 1 } },
			}),
		).resolves.toBeUndefined();
	});

	it("rejects a terminal receipt for different Tool arguments", async () => {
		const { actions, tool } = fixture("confirm-once");
		const executed = await tool.execute("call", { value: 2 });
		const operationId = executed.details.operationId;
		const recover = createReliableActionTranscriptRecovery({ actions, now: () => 1 });

		await expect(
			recover({
				start: started(operationId),
				toolCall: { type: "toolCall", id: "call", name: "world.write", arguments: { value: 1 } },
			}),
		).rejects.toThrow("does not match");
	});
});

function started(operationId: string, toolCallId = "call"): DurableTranscriptToolStartedRecord {
	return {
		schemaVersion: "6",
		type: "tool.started",
		coordinate: { ...STREAM, runId: "run", turn: "2" },
		assistantRecordId: "assistant-record",
		sourceIndex: 0,
		toolCallId,
		toolName: "world.write",
		arguments: { value: 1 },
		descriptorFingerprint: "a".repeat(64),
		catalogRevision: "catalog",
		policyRevision: "policy",
		operationId,
		recordId: "start-record",
		sequence: "1",
	};
}

function testAdmission(
	journal: InMemoryActionJournal,
	approvals: InMemoryActionApprovalStore,
): ApprovedActionAdmission {
	return {
		admit: async (input) => {
			const approval = await approvals.consume({
				sessionId: input.sessionId,
				actorId: input.actorId,
				coordinate: input.coordinate,
				approvalId: input.approvalId,
				expectedRevision: input.expectedRevision,
				operationId: input.operationId,
				intentFingerprint: input.intentFingerprint,
				currentAuthority: input.currentAuthority,
				consumedAt: "2026-08-28T00:00:00.000Z",
			});
			return {
				approval,
				...(approval.status === "consumed"
					? { action: await journal.ensure(input.intent, "2026-08-28T00:00:00.000Z") }
					: {}),
			};
		},
	};
}

function cancellationFixture(options: {
	policyMode?: "explicit-only" | "confirm-once";
	goalProjectionLimits?: {
		maxBindingBytes: number;
		maxPendingPerStream: number;
		maxListBatchSize: number;
		maxRecordBytes: number;
	};
	resolveGoalProjection?: (
		parameters: { value: number },
		signal: AbortSignal,
	) =>
		| {
				goalId: string;
				partition: {
					ownerId: string;
					timelineId: string;
					generation: string;
					sessionId?: string;
					actorId?: string;
				};
				stepId: string;
				expectedRevision: string;
				inputId: string;
		  }
		| Promise<{
				goalId: string;
				partition: {
					ownerId: string;
					timelineId: string;
					generation: string;
					sessionId?: string;
					actorId?: string;
				};
				stepId: string;
				expectedRevision: string;
				inputId: string;
		  }>;
	readCoordinate?: () => RunCoordinate;
	resolveScope?: (
		parameters: { value: number },
		signal: AbortSignal,
	) => ActionInvocationScope | Promise<ActionInvocationScope>;
	readAuthority?: (parameters: { value: number }, signal: AbortSignal) => ActionAuthority | Promise<ActionAuthority>;
	presentation?: {
		limits: { maxBytes: number; maxDepth: number };
		project(parameters: { value: number }, signal: AbortSignal): unknown | Promise<unknown>;
	};
	waitForDecision?: (approval: ActionApprovalRecord, signal: AbortSignal) => Promise<ActionApprovalRecord>;
	waitForSettlement?: (action: ActionRecord, signal: AbortSignal) => Promise<ActionRecord>;
}) {
	const journal = new InMemoryActionJournal(
		options.goalProjectionLimits === undefined ? {} : { goalProjection: options.goalProjectionLimits },
	);
	const approvalStore = new InMemoryActionApprovalStore();
	const actions = new ActionCoordinator(journal, { now: clock() });
	const approvals = new ActionApprovalCoordinator(approvalStore, { now: clock() });
	const admission = testAdmission(journal, approvalStore);
	const policyMode = options.policyMode ?? "confirm-once";
	const tool = createReliableActionTool({
		...STREAM,
		name: "world.write",
		label: "World write",
		description: "Apply one authoritative game write.",
		parameters: PARAMETERS,
		actions,
		policy: { policyId: "high-risk", mode: policyMode },
		approval: {
			coordinator: approvals,
			admission,
			durationMs: 1_000,
			...(options.presentation === undefined ? {} : { presentation: options.presentation }),
			waitForDecision:
				options.waitForDecision ??
				(async (pending) =>
					approvals.decide({
						...STREAM,
						approvalId: pending.request.approvalId,
						expectedRevision: pending.revision,
						requestFingerprint: pending.request.requestFingerprint,
						decision: "approve",
						reviewerId: "player",
					})),
		},
		readCoordinate: options.readCoordinate ?? (() => ({ ...STREAM, runId: "run", turn: "2" })),
		readAuthority: options.readAuthority ?? (async () => authority()),
		...(options.resolveGoalProjection === undefined ? {} : { resolveGoalProjection: options.resolveGoalProjection }),
		resolveScope:
			options.resolveScope ??
			(async () => (policyMode === "explicit-only" ? { explicitlyRequestedActionNames: ["world.write"] } : {})),
		operationKey: (toolCallId) => toolCallId,
		conflictKeys: () => ["world:inventory"],
		replayPolicy: "recoverable",
		waitForSettlement:
			options.waitForSettlement ??
			(async () => {
				throw new Error("An action must not be admitted after cancellation.");
			}),
	});
	return { actions, approvals, admission, tool };
}

function authority(): ActionAuthority {
	return { timelineId: "world", generation: "3", revision: "7" };
}

function requiredOperationId(value: string | undefined): string {
	if (!value) throw new Error("Expected a prepared durable Action operation ID.");
	return value;
}

async function noAction(actions: ActionCoordinator) {
	return actions.nextDelivery({
		...STREAM,
		claimantId: "game",
		leaseDurationMs: 1_000,
		candidateLimit: 1,
	});
}

function deferred<T>() {
	let resolve!: (value: T) => void;
	const promise = new Promise<T>((settle) => {
		resolve = settle;
	});
	return { promise, resolve };
}
