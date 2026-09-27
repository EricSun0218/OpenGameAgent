import { describe, expect, it } from "vitest";
import {
	ActionApprovalConflictError,
	ActionApprovalCoordinator,
	ActionApprovalNotFoundError,
	InMemoryActionApprovalStore,
	approvalRequestFingerprintFromRequest,
	assertActionApprovalRecord,
	assertActionApprovalRequest,
	evaluateActionPolicy,
	prepareActionIntent,
} from "../src/index.js";

function intent(operationKey = "tool-call", runId = "run") {
	return prepareActionIntent({
		sessionId: "session",
		actorId: "actor",
		coordinate: { sessionId: "session", actorId: "actor", runId, turn: "2" },
		actionName: "world.write",
		operationKey,
		payload: { amount: 1 },
		expectedAuthority: { timelineId: "world", generation: "3", revision: "9" },
		conflictKeys: ["world:inventory"],
		replayPolicy: "recoverable",
	});
}

function clock(initial = "2026-08-28T00:00:00.000Z") {
	let current = new Date(initial);
	return {
		now: () => new Date(current),
		advance(milliseconds: number) {
			current = new Date(current.getTime() + milliseconds);
		},
	};
}

describe("action approval policies", () => {
	it("requires host attestations for explicit and task-scoped action modes", () => {
		expect(evaluateActionPolicy({ policyId: "disabled", mode: "disabled" }, "world.write", {})).toEqual({
			authorized: false,
			reason: "disabled",
		});
		expect(evaluateActionPolicy({ policyId: "explicit", mode: "explicit-only" }, "world.write", {})).toEqual({
			authorized: false,
			reason: "explicit-request-required",
		});
		expect(
			evaluateActionPolicy({ policyId: "explicit", mode: "explicit-only" }, "world.write", {
				explicitlyRequestedActionNames: ["world.write"],
			}),
		).toEqual({ authorized: true });
		expect(
			evaluateActionPolicy({ policyId: "task", mode: "allowed-in-task" }, "world.write", {
				taskId: "task",
				taskAllowedActionNames: ["world.write"],
			}),
		).toEqual({ authorized: true, taskId: "task" });
		expect(evaluateActionPolicy({ policyId: "confirm", mode: "confirm-once" }, "world.write", {})).toEqual({
			authorized: false,
			reason: "approval-required",
		});
	});
});

describe("content-bound one-time action approvals", () => {
	it("purges only bounded terminal approvals before the explicit cutoff", async () => {
		const time = clock();
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), { now: time.now });
		const rejectedPending = await approvals.request("high-risk", intent("rejected"), 1_000);
		const rejected = await approvals.decide({
			sessionId: "session",
			actorId: "actor",
			approvalId: rejectedPending.request.approvalId,
			expectedRevision: rejectedPending.revision,
			requestFingerprint: rejectedPending.request.requestFingerprint,
			decision: "reject",
			reviewerId: "player",
		});
		time.advance(1);
		const consumedPending = await approvals.request("high-risk", intent("consumed"), 1_000);
		const approved = await approvals.decide({
			sessionId: "session",
			actorId: "actor",
			approvalId: consumedPending.request.approvalId,
			expectedRevision: consumedPending.revision,
			requestFingerprint: consumedPending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		const consumed = await approvals.consume({
			sessionId: "session",
			actorId: "actor",
			coordinate: consumedPending.request.coordinate,
			approvalId: consumedPending.request.approvalId,
			expectedRevision: approved.revision,
			operationId: consumedPending.request.operationId,
			intentFingerprint: consumedPending.request.intentFingerprint,
			currentAuthority: consumedPending.request.expectedAuthority,
		});
		const stillPending = await approvals.request("high-risk", intent("pending"), 1_000);
		const stillApprovedPending = await approvals.request("high-risk", intent("approved"), 1_000);
		const stillApproved = await approvals.decide({
			sessionId: "session",
			actorId: "actor",
			approvalId: stillApprovedPending.request.approvalId,
			expectedRevision: stillApprovedPending.revision,
			requestFingerprint: stillApprovedPending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		time.advance(1);

		await expect(async () =>
			approvals.purgeTerminal({
				sessionId: "session",
				actorId: "actor",
				beforeUpdatedAt: time.now().toISOString(),
				limit: 1,
				retainApprovalIds: [rejected.request.approvalId],
				retainBudget: { maxCount: 0, maxBytes: 68 },
			}),
		).rejects.toThrow("retainApprovalIds exceeds retainBudget.maxCount.");
		await expect(
			approvals.purgeTerminal({
				sessionId: "session",
				actorId: "actor",
				beforeUpdatedAt: time.now().toISOString(),
				limit: 1,
				retainApprovalIds: [rejected.request.approvalId],
				retainBudget: { maxCount: 1, maxBytes: Buffer.byteLength(rejected.request.approvalId, "utf8") },
			}),
		).resolves.toEqual([consumed.request.approvalId]);
		await expect(approvals.get(rejected.request, rejected.request.approvalId)).resolves.toEqual(rejected);
		await expect(approvals.get(consumed.request, consumed.request.approvalId)).resolves.toBeUndefined();
		await expect(
			approvals.purgeTerminal({
				sessionId: "session",
				actorId: "actor",
				beforeUpdatedAt: time.now().toISOString(),
				limit: 1,
				retainApprovalIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual([rejected.request.approvalId]);
		await expect(approvals.get(rejected.request, rejected.request.approvalId)).resolves.toBeUndefined();
		await expect(approvals.get(stillPending.request, stillPending.request.approvalId)).resolves.toEqual(stillPending);
		await expect(approvals.get(stillApproved.request, stillApproved.request.approvalId)).resolves.toEqual(
			stillApproved,
		);
	});

	it("binds each approval to its exact run and lists only that run", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const firstIntent = intent("first", "run-one");
		const secondIntent = intent("second", "run-two");
		await approvals.request("high-risk", firstIntent, 1_000);
		const second = await approvals.request("high-risk", secondIntent, 1_000);

		expect(second.request.coordinate).toEqual(secondIntent.coordinate);
		await expect(
			approvals.listPending({ sessionId: "session", actorId: "actor", runId: "run-two", limit: 10 }),
		).resolves.toEqual([second]);
	});

	it("waits for the exact durable approval decision", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const pending = await approvals.request("high-risk", intent(), 1_000);
		const waiting = approvals.waitForDecision(pending, { pollIntervalMs: 1 });
		const decided = await approvals.decide({
			sessionId: pending.request.sessionId,
			actorId: pending.request.actorId,
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});

		await expect(waiting).resolves.toEqual(decided);
	});

	it("materializes the approval's exact expiration while waiting", async () => {
		const time = clock();
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), { now: time.now });
		const pending = await approvals.request("high-risk", intent(), 10);
		time.advance(10);

		const expired = await approvals.waitForDecision(pending, {
			pollIntervalMs: 1,
			signal: AbortSignal.timeout(100),
		});

		expect(expired).toMatchObject({
			status: "expired",
			revision: "2",
			updatedAt: pending.request.expiresAt,
		});
		await expect(approvals.get(pending.request, pending.request.approvalId)).resolves.toEqual(expired);
	});

	it("rejects a wait interval above Node's timer range before expiration", async () => {
		const time = clock();
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), { now: time.now });
		const pending = await approvals.request("high-risk", intent(), 10);
		time.advance(10);

		await expect(approvals.waitForDecision(pending, { pollIntervalMs: 2_147_483_648 })).rejects.toBeInstanceOf(
			RangeError,
		);
	});

	it("cancels before materializing an expired approval", async () => {
		const time = clock();
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), { now: time.now });
		const pending = await approvals.request("high-risk", intent(), 10);
		time.advance(10);
		const controller = new AbortController();
		const reason = new Error("stop-approval-wait");
		controller.abort(reason);

		await expect(approvals.waitForDecision(pending, { pollIntervalMs: 1, signal: controller.signal })).rejects.toBe(
			reason,
		);
		await expect(approvals.get(pending.request, pending.request.approvalId)).resolves.toEqual(pending);
	});

	it("fails closed when a waited approval is absent from the durable authority", async () => {
		const source = new ActionApprovalCoordinator(new InMemoryActionApprovalStore());
		const pending = await source.request("high-risk", intent(), 1_000);
		const target = new ActionApprovalCoordinator(new InMemoryActionApprovalStore());

		await expect(target.waitForDecision(pending, { pollIntervalMs: 1 })).rejects.toBeInstanceOf(
			ActionApprovalNotFoundError,
		);
	});

	it("rereads terminal approvals and returns the current durable state", async () => {
		const store = new InMemoryActionApprovalStore();
		const approvals = new ActionApprovalCoordinator(store);
		const pending = await approvals.request("high-risk", intent(), 1_000);
		const approved = await approvals.decide({
			sessionId: pending.request.sessionId,
			actorId: pending.request.actorId,
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		const consumed = await approvals.consume({
			sessionId: approved.request.sessionId,
			actorId: approved.request.actorId,
			coordinate: approved.request.coordinate,
			approvalId: approved.request.approvalId,
			expectedRevision: approved.revision,
			operationId: approved.request.operationId,
			intentFingerprint: approved.request.intentFingerprint,
			currentAuthority: approved.request.expectedAuthority,
		});

		await expect(approvals.waitForDecision(approved, { pollIntervalMs: 1 })).resolves.toEqual(consumed);
		await expect(
			new ActionApprovalCoordinator(new InMemoryActionApprovalStore()).waitForDecision(consumed, {
				pollIntervalMs: 1,
			}),
		).rejects.toBeInstanceOf(ActionApprovalNotFoundError);
	});

	it("rejects an approval returned for a different durable identity", async () => {
		const source = new ActionApprovalCoordinator(new InMemoryActionApprovalStore());
		const expected = await source.request("high-risk", intent(), 1_000);
		const foreign = await source.request(
			"high-risk",
			prepareActionIntent({
				sessionId: "foreign-session",
				actorId: "foreign-actor",
				coordinate: { sessionId: "foreign-session", actorId: "foreign-actor", runId: "run", turn: "2" },
				actionName: "world.write",
				operationKey: "foreign-tool-call",
				payload: { amount: 1 },
				expectedAuthority: { timelineId: "world", generation: "3", revision: "9" },
				conflictKeys: ["world:inventory"],
				replayPolicy: "recoverable",
			}),
			1_000,
		);
		const store = new InMemoryActionApprovalStore();
		store.get = async () => structuredClone(foreign);
		const target = new ActionApprovalCoordinator(store);

		await expect(target.waitForDecision(expected, { pollIntervalMs: 1 })).rejects.toBeInstanceOf(
			ActionApprovalConflictError,
		);
	});

	it("preserves cancellation that arrives while reading the durable approval", async () => {
		const source = new ActionApprovalCoordinator(new InMemoryActionApprovalStore());
		const pending = await source.request("high-risk", intent(), 1_000);
		let releaseRead: ((record: typeof pending) => void) | undefined;
		const store = new InMemoryActionApprovalStore();
		store.get = () =>
			new Promise((resolve) => {
				releaseRead = resolve;
			});
		const target = new ActionApprovalCoordinator(store);
		const controller = new AbortController();
		const reason = new Error("abort-during-approval-read");
		const waiting = target.waitForDecision(pending, { pollIntervalMs: 1, signal: controller.signal });
		controller.abort(reason);
		releaseRead?.(pending);

		await expect(waiting).rejects.toBe(reason);
	});

	it("does not expire an approval after cancellation wins an authority wait", async () => {
		const time = clock();
		const store = new InMemoryActionApprovalStore();
		const approvals = new ActionApprovalCoordinator(store, { now: time.now });
		const pending = await approvals.request("high-risk", intent(), 10);
		time.advance(10);
		let releaseExpiration: (() => void) | undefined;
		const expirationStarted = new Promise<void>((resolveStarted) => {
			const durableExpire = store.expire.bind(store);
			store.expire = async (input) => {
				resolveStarted();
				await new Promise<void>((resolve) => {
					releaseExpiration = resolve;
				});
				input.signal?.throwIfAborted();
				return durableExpire(input);
			};
		});
		const controller = new AbortController();
		const reason = new Error("abort-before-expiration-write");
		const waiting = approvals.waitForDecision(pending, { pollIntervalMs: 1, signal: controller.signal });
		await expirationStarted;
		controller.abort(reason);
		releaseExpiration?.();

		await expect(waiting).rejects.toBe(reason);
		await expect(approvals.get(pending.request, pending.request.approvalId)).resolves.toEqual(pending);
	});

	it("persists one bounded host presentation and binds it to the request identity", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const prepared = intent();
		const request = {
			presentation: {
				value: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
				limits: { maxBytes: 256, maxDepth: 2 },
			},
		};

		const first = await approvals.request("high-risk", prepared, 1_000, request);
		const retry = await approvals.request("high-risk", prepared, 1_000, request);

		expect(retry).toEqual(first);
		expect(first.request.presentation).toEqual({
			kind: "game-choice",
			optionId: "opaque-option-7",
			label: "赠送礼物",
		});
		const tampered = structuredClone(first.request);
		tampered.presentation = { kind: "game-choice", optionId: "opaque-option-8", label: "赠送礼物" };
		expect(() => assertActionApprovalRequest(tampered)).toThrow("fingerprint does not match");
	});

	it("captures one presentation draft and budget before reading its value", async () => {
		const reads: string[] = [];
		const limits = {
			get maxBytes() {
				reads.push("maxBytes");
				return 256;
			},
			get maxDepth() {
				reads.push("maxDepth");
				return 2;
			},
		};
		const draft = {
			get limits() {
				reads.push("limits");
				return limits;
			},
			get value() {
				reads.push("value");
				return { optionId: "opaque-option-7" };
			},
		};
		const options = {
			get presentation() {
				reads.push("presentation");
				return draft;
			},
		};
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});

		const record = await approvals.request("high-risk", intent(), 1_000, options);

		expect(reads).toEqual(["presentation", "limits", "maxBytes", "maxDepth", "value"]);
		expect(record.request.presentation).toEqual({ optionId: "opaque-option-7" });
	});

	it("reuses one deterministic request and lets exactly one concurrent decision win", async () => {
		const time = clock();
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), { now: time.now });
		const prepared = intent();
		const first = await approvals.request("high-risk", prepared, 1_000);
		time.advance(1);
		const retry = await approvals.request("high-risk", prepared, 1_000);

		expect(retry).toEqual(first);
		expect(first.request.approvalId).toMatch(/^apr_[0-9a-f]{64}$/u);
		const decision = {
			sessionId: "session",
			actorId: "actor",
			approvalId: first.request.approvalId,
			expectedRevision: first.revision,
			requestFingerprint: approvalRequestFingerprintFromRequest(first.request),
			reviewerId: "player",
		};
		const results = await Promise.allSettled([
			approvals.decide({ ...decision, decision: "approve" }),
			approvals.decide({ ...decision, decision: "reject" }),
		]);
		expect(results.filter((result) => result.status === "fulfilled")).toHaveLength(1);
		expect(results.filter((result) => result.status === "rejected")).toHaveLength(1);
	});

	it("consumes an approval once only for its exact operation and world authority", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const prepared = intent();
		const pending = await approvals.request("high-risk", prepared, 1_000);
		const approved = await approvals.decide({
			sessionId: "session",
			actorId: "actor",
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		await expect(
			approvals.consume({
				sessionId: "session",
				actorId: "actor",
				coordinate: { ...prepared.coordinate, runId: "foreign-run" },
				approvalId: pending.request.approvalId,
				expectedRevision: approved.revision,
				operationId: prepared.operationId,
				intentFingerprint: prepared.intentFingerprint,
				currentAuthority: prepared.expectedAuthority,
			}),
		).rejects.toThrow(ActionApprovalConflictError);

		await expect(
			approvals.consume({
				sessionId: "session",
				actorId: "actor",
				coordinate: prepared.coordinate,
				approvalId: pending.request.approvalId,
				expectedRevision: approved.revision,
				operationId: prepared.operationId,
				intentFingerprint: prepared.intentFingerprint,
				currentAuthority: { ...prepared.expectedAuthority, revision: "10" },
			}),
		).rejects.toThrow(ActionApprovalConflictError);
		const consumed = await approvals.consume({
			sessionId: "session",
			actorId: "actor",
			coordinate: prepared.coordinate,
			approvalId: pending.request.approvalId,
			expectedRevision: approved.revision,
			operationId: prepared.operationId,
			intentFingerprint: prepared.intentFingerprint,
			currentAuthority: prepared.expectedAuthority,
		});
		expect(consumed.status).toBe("consumed");
		await expect(
			approvals.consume({
				sessionId: "session",
				actorId: "actor",
				coordinate: prepared.coordinate,
				approvalId: pending.request.approvalId,
				expectedRevision: consumed.revision,
				operationId: prepared.operationId,
				intentFingerprint: prepared.intentFingerprint,
				currentAuthority: prepared.expectedAuthority,
			}),
		).rejects.toThrow(ActionApprovalConflictError);
	});

	it("expires instead of approving after the host-required deadline", async () => {
		const time = clock();
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), { now: time.now });
		const pending = await approvals.request("high-risk", intent(), 10);
		time.advance(10);

		await expect(
			approvals.decide({
				sessionId: "session",
				actorId: "actor",
				approvalId: pending.request.approvalId,
				expectedRevision: pending.revision,
				requestFingerprint: pending.request.requestFingerprint,
				decision: "approve",
				reviewerId: "player",
			}),
		).resolves.toMatchObject({ status: "expired" });
	});

	it("rejects decisions before their request and inconsistent durable decision chronology", async () => {
		const time = clock();
		const store = new InMemoryActionApprovalStore();
		const approvals = new ActionApprovalCoordinator(store, { now: time.now });
		const pending = await approvals.request("high-risk", intent(), 1_000);
		await expect(
			store.decide({
				sessionId: pending.request.sessionId,
				actorId: pending.request.actorId,
				approvalId: pending.request.approvalId,
				expectedRevision: pending.revision,
				requestFingerprint: pending.request.requestFingerprint,
				decision: "approve",
				reviewerId: "player",
				decidedAt: "2026-08-27T23:59:59.999Z",
			}),
		).rejects.toThrow(ActionApprovalConflictError);

		time.advance(1);
		const approved = await approvals.decide({
			sessionId: pending.request.sessionId,
			actorId: pending.request.actorId,
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		expect(() => assertActionApprovalRecord({ ...approved, updatedAt: pending.request.requestedAt })).toThrow();
		expect(() =>
			assertActionApprovalRecord({
				...approved,
				decision: { ...approved.decision, decidedAt: "2026-08-27T23:59:59.999Z" },
			}),
		).toThrow();
	});
});
