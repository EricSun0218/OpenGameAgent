import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	ActionApprovalCoordinator,
	ActionCoordinator,
	ActionIntentConflictError,
	createReliableActionTool,
	prepareActionIntent,
} from "@opengameagent/actions";
import { RunAuthorityClosedError } from "@opengameagent/protocol";
import { Type } from "typebox";
import { afterEach, describe, expect, it } from "vitest";
import {
	SqliteActionApprovalStore,
	SqliteActionJournal,
	SqliteApprovedActionAdmission,
	SqliteApprovedActionAdmissionError,
	SqliteAuthority,
	SqliteEventStore,
} from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-action-admission-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string, ownership: "shared" | "exclusive" = "shared"): SqliteAuthority {
	const authority = new SqliteAuthority({
		path,
		ownership,
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		now: () => "2026-08-28T00:00:00.000Z",
	});
	authorities.push(authority);
	return authority;
}

function intent(payload: unknown = { amount: 1 }, runId = "run") {
	return prepareActionIntent({
		sessionId: "session",
		actorId: "actor",
		coordinate: { sessionId: "session", actorId: "actor", runId, turn: "2" },
		actionName: "world.write",
		operationKey: "operation",
		payload,
		expectedAuthority: { timelineId: "world", generation: "3", revision: "9" },
		conflictKeys: ["world:inventory"],
		replayPolicy: "recoverable",
	});
}

async function approved(authority: SqliteAuthority, prepared = intent(), durationMs = 1_000) {
	const coordinator = new ActionApprovalCoordinator(new SqliteActionApprovalStore(authority), {
		now: () => new Date("2026-08-28T00:00:00.000Z"),
	});
	const pending = await coordinator.request("high-risk", prepared, durationMs);
	const decision = await coordinator.decide({
		sessionId: "session",
		actorId: "actor",
		approvalId: pending.request.approvalId,
		expectedRevision: pending.revision,
		requestFingerprint: pending.request.requestFingerprint,
		decision: "approve",
		reviewerId: "player",
	});
	return { coordinator, decision, prepared };
}

function admissionInput(
	decision: Awaited<ReturnType<typeof approved>>["decision"],
	prepared: ReturnType<typeof intent>,
) {
	return {
		sessionId: "session",
		actorId: "actor",
		coordinate: prepared.coordinate,
		approvalId: decision.request.approvalId,
		expectedRevision: decision.revision,
		operationId: prepared.operationId,
		intentFingerprint: prepared.intentFingerprint,
		currentAuthority: prepared.expectedAuthority,
		intent: prepared,
	};
}

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteApprovedActionAdmission", () => {
	it("does not consume approval when its run closes between approval and atomic admission", async () => {
		const authority = open(await databasePath(), "exclusive");
		const events = new SqliteEventStore(authority);
		const prepared = intent();
		const admissionAuthority = { scope: "planning", generation: "1" };
		await events.append({ sessionId: prepared.sessionId, actorId: prepared.actorId }, "empty", prepared.coordinate, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: "a".repeat(64),
			admissionAuthority,
		});
		const { coordinator, decision } = await approved(authority, prepared);
		let closing: ReturnType<SqliteEventStore["closeAuthority"]> | undefined;
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => {
				closing = events.closeAuthority({ ...prepared, ...admissionAuthority, operationId: "close" });
				return new Date("2026-08-28T00:00:00.001Z");
			},
		});
		await expect(admission.admit(admissionInput(decision, prepared))).rejects.toBeInstanceOf(RunAuthorityClosedError);
		await closing;
		expect(await coordinator.get(prepared, decision.request.approvalId)).toEqual(decision);
		expect(await new SqliteActionJournal(authority).get(prepared, prepared.operationId)).toBeUndefined();
	});

	it("allows a consumed exact replay after closure but never recreates its purged Action", async () => {
		const authority = open(await databasePath(), "exclusive");
		const events = new SqliteEventStore(authority);
		const prepared = intent();
		const admissionAuthority = { scope: "planning", generation: "1" };
		await events.append({ sessionId: prepared.sessionId, actorId: prepared.actorId }, "empty", prepared.coordinate, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: "a".repeat(64),
			admissionAuthority,
		});
		const { decision } = await approved(authority, prepared);
		const admission = new SqliteApprovedActionAdmission(authority, { now: () => new Date("2026-08-28T00:00:00.001Z") });
		const input = admissionInput(decision, prepared);
		const accepted = await admission.admit(input);
		await events.closeAuthority({ ...prepared, ...admissionAuthority, operationId: "close" });
		expect(await admission.admit(input)).toEqual(accepted);
		const journal = new SqliteActionJournal(authority);
		await journal.nextDelivery({
			...prepared,
			claimantId: "game",
			claimToken: "claim",
			claimedAt: "2026-08-28T00:00:00.002Z",
			expiresAt: "2026-08-28T00:00:01.000Z",
			candidateLimit: 1,
		});
		await journal.purgeTerminal({
			...prepared,
			beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
			limit: 1,
			retainOperationIds: [],
			retainBudget: { maxCount: 0, maxBytes: 0 },
		});
		await expect(admission.admit(input)).rejects.toBeInstanceOf(SqliteApprovedActionAdmissionError);
		expect(await journal.get(prepared, prepared.operationId)).toBeUndefined();
	});

	it("atomically admits a bounded Goal projection with its consumed approval", async () => {
		const authority = open(await databasePath());
		const limits = {
			maxBindingBytes: 1_024,
			maxPendingPerStream: 1,
			maxListBatchSize: 1,
			maxRecordBytes: 8_192,
		} as const;
		const prepared = prepareActionIntent(
			{
				...intent(),
				operationKey: "goal-approved",
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
			},
			limits,
		);
		const { decision } = await approved(authority, prepared);
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
			goalProjection: limits,
		});

		await expect(admission.admit(admissionInput(decision, prepared))).resolves.toMatchObject({
			approval: { status: "consumed" },
			action: { goalProjection: { goalId: "goal-1", inputId: "input-1" } },
		});
		await expect(
			new SqliteActionJournal(authority, { goalProjection: limits }).listGoalProjections({
				sessionId: "session",
				actorId: "actor",
				limit: 1,
			}),
		).resolves.toMatchObject({
			candidates: [{ action: { operationId: prepared.operationId } }],
		});
	});

	it("rolls approval consumption back when Goal projection capacity is exhausted", async () => {
		const authority = open(await databasePath());
		const limits = {
			maxBindingBytes: 1_024,
			maxPendingPerStream: 1,
			maxListBatchSize: 1,
			maxRecordBytes: 8_192,
		} as const;
		const projectedIntent = (operationKey: string, goalId: string) =>
			prepareActionIntent(
				{
					...intent(),
					operationKey,
					goalProjection: {
						goalId,
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
				},
				limits,
			);
		const journal = new SqliteActionJournal(authority, { goalProjection: limits });
		await journal.ensure(projectedIntent("goal-existing", "goal-1"), "2026-08-28T00:00:00.000Z");
		const blocked = projectedIntent("goal-blocked", "goal-2");
		const { decision } = await approved(authority, blocked);
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
			goalProjection: limits,
		});

		await expect(admission.admit(admissionInput(decision, blocked))).rejects.toThrow("Goal projection capacity");
		await expect(
			new SqliteActionApprovalStore(authority).get(blocked, decision.request.approvalId),
		).resolves.toMatchObject({ status: "approved", revision: "2" });
		await expect(journal.get(blocked, blocked.operationId)).resolves.toBeUndefined();
	});

	it("atomically consumes approval and admits the exact action across restart", async () => {
		const path = await databasePath();
		const authority = open(path);
		const { decision, prepared } = await approved(authority);
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
		});

		await expect(admission.admit(admissionInput(decision, prepared))).resolves.toMatchObject({
			approval: { status: "consumed", revision: "3" },
			action: { operationId: prepared.operationId, status: "pending", attempt: "0" },
		});
		authority.close();

		const restartedAuthority = open(path);
		const restartedApprovals = new SqliteActionApprovalStore(restartedAuthority);
		const restartedActions = new SqliteActionJournal(restartedAuthority);
		await expect(restartedApprovals.get(prepared, decision.request.approvalId)).resolves.toMatchObject({
			status: "consumed",
		});
		await expect(restartedActions.get(prepared, prepared.operationId)).resolves.toMatchObject({
			intentFingerprint: prepared.intentFingerprint,
			status: "pending",
		});
	});

	it("is idempotent only when a consumed approval already has its exact action", async () => {
		const authority = open(await databasePath());
		const { decision, prepared } = await approved(authority);
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
		});
		const input = admissionInput(decision, prepared);
		await admission.admit(input);

		await expect(admission.admit(input)).resolves.toMatchObject({
			approval: { status: "consumed" },
			action: { intentFingerprint: prepared.intentFingerprint },
		});
		expect(
			authority.read(
				(database) => database.prepare("SELECT count(*) AS count FROM action_records").get() as { count: number },
			),
		).toEqual({ count: 1 });
	});

	it("rejects admission when the approval coordinate does not match the exact Action", async () => {
		const authority = open(await databasePath());
		const { decision, prepared } = await approved(authority);
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
		});

		await expect(
			admission.admit({
				...admissionInput(decision, prepared),
				intent: intent({ amount: 1 }, "foreign-run"),
			}),
		).rejects.toThrow(SqliteApprovedActionAdmissionError);
		await expect(
			new SqliteActionApprovalStore(authority).get(prepared, decision.request.approvalId),
		).resolves.toMatchObject({ status: "approved" });
		await expect(new SqliteActionJournal(authority).get(prepared, prepared.operationId)).resolves.toBeUndefined();
	});

	it("lets the Tool resume when waiting observes an already consumed approval", async () => {
		const authority = open(await databasePath());
		const prepared = intent();
		const approvals = new ActionApprovalCoordinator(new SqliteActionApprovalStore(authority), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
		});
		const actions = new ActionCoordinator(new SqliteActionJournal(authority), {
			now: () => new Date("2026-08-28T00:00:00.002Z"),
			newClaimToken: () => "claim-token",
		});
		const tool = createReliableActionTool({
			sessionId: "session",
			actorId: "actor",
			name: "world.write",
			label: "World write",
			description: "Apply one authoritative game write.",
			parameters: Type.Object({ amount: Type.Number() }, { additionalProperties: false }),
			actions,
			policy: { policyId: "high-risk", mode: "confirm-once" },
			approval: {
				coordinator: approvals,
				admission,
				durationMs: 1_000,
				waitForDecision: async (pending) => {
					const decision = await approvals.decide({
						sessionId: "session",
						actorId: "actor",
						approvalId: pending.request.approvalId,
						expectedRevision: pending.revision,
						requestFingerprint: pending.request.requestFingerprint,
						decision: "approve",
						reviewerId: "player",
					});
					const firstAdmission = await admission.admit(admissionInput(decision, prepared));
					return firstAdmission.approval;
				},
			},
			readCoordinate: () => ({ sessionId: "session", actorId: "actor", runId: "run", turn: "2" }),
			readAuthority: () => ({ timelineId: "world", generation: "3", revision: "9" }),
			resolveScope: () => ({}),
			operationKey: () => "operation",
			conflictKeys: () => ["world:inventory"],
			replayPolicy: "recoverable",
			waitForSettlement: async (submitted) => {
				const delivery = await actions.nextDelivery({
					sessionId: "session",
					actorId: "actor",
					claimantId: "game",
					leaseDurationMs: 1_000,
					candidateLimit: 1,
				});
				if (
					delivery?.kind !== "dispatch" ||
					!delivery.action.claim ||
					delivery.action.operationId !== submitted.operationId
				) {
					throw new Error("Expected the admitted action claim.");
				}
				const claim = delivery.action.claim;
				const claimed = delivery.action;
				return actions.recordReceipt({
					sessionId: "session",
					actorId: "actor",
					operationId: claimed.operationId,
					claimToken: claim.claimToken,
					attempt: claim.attempt,
					outcome: "succeeded",
					modelResult: { message: "done" },
				});
			},
		});

		await expect(tool.execute("call", { amount: 1 })).resolves.toMatchObject({
			details: { operationId: prepared.operationId, status: "succeeded" },
		});
		expect(
			authority.read(
				(database) => database.prepare("SELECT count(*) AS count FROM action_records").get() as { count: number },
			),
		).toEqual({ count: 1 });
	});

	it("rolls the approval consumption back when action admission conflicts", async () => {
		const authority = open(await databasePath());
		const existing = intent({ amount: 1 });
		const conflicting = intent({ amount: 2 });
		await new SqliteActionJournal(authority).ensure(existing, "2026-08-28T00:00:00.000Z");
		const { decision } = await approved(authority, conflicting);
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
		});

		await expect(admission.admit(admissionInput(decision, conflicting))).rejects.toThrow(ActionIntentConflictError);
		await expect(
			new SqliteActionApprovalStore(authority).get(conflicting, decision.request.approvalId),
		).resolves.toMatchObject({ status: "approved", revision: "2" });
	});

	it("fails closed on a legacy consumed-without-action gap", async () => {
		const authority = open(await databasePath());
		const { coordinator, decision, prepared } = await approved(authority);
		await coordinator.consume({
			sessionId: "session",
			actorId: "actor",
			coordinate: prepared.coordinate,
			approvalId: decision.request.approvalId,
			expectedRevision: decision.revision,
			operationId: prepared.operationId,
			intentFingerprint: prepared.intentFingerprint,
			currentAuthority: prepared.expectedAuthority,
		});

		const admission = new SqliteApprovedActionAdmission(authority);
		await expect(admission.admit(admissionInput(decision, prepared))).rejects.toThrow(
			SqliteApprovedActionAdmissionError,
		);
		await expect(new SqliteActionJournal(authority).get(prepared, prepared.operationId)).resolves.toBeUndefined();
	});

	it("expires approval without admitting an action in the same transaction", async () => {
		const authority = open(await databasePath());
		const { decision, prepared } = await approved(authority, intent(), 1);
		const admission = new SqliteApprovedActionAdmission(authority, {
			now: () => new Date("2026-08-28T00:00:00.001Z"),
		});

		const result = await admission.admit(admissionInput(decision, prepared));
		expect(result).toMatchObject({ approval: { status: "expired" } });
		expect(result.action).toBeUndefined();
		await expect(new SqliteActionJournal(authority).get(prepared, prepared.operationId)).resolves.toBeUndefined();
	});
});
