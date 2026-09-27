import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { ActionCoordinator, ActionGoalProjector } from "@opengameagent/actions";
import { type GoalLimits, GoalService } from "@opengameagent/extensions";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SqliteActionJournal, SqliteAuthority, SqliteGoalStore } from "../src/index.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;
const PARTITION = {
	ownerId: "npc-1",
	timelineId: "world",
	generation: "3",
	...STREAM,
} as const;
const ACTION_LIMITS = {
	maxBindingBytes: 1_024,
	maxPendingPerStream: 2,
	maxListBatchSize: 2,
	maxRecordBytes: 8_192,
} as const;
const GOAL_LIMITS: GoalLimits = {
	maxObjectiveBytes: 1_024,
	maxStepsPerGoal: 4,
	maxStepBytes: 512,
	maxPlanBytes: 4_096,
	maxRecordBytes: 16_384,
	maxModelResultBytes: 8_192,
	maxEvidencePerStep: 2,
	maxEvidenceKindBytes: 64,
	maxEvidenceReferenceBytes: 256,
	maxOpenGoalsPerOwnerGeneration: 2,
	maxListPageSize: 2,
	maxPurgeBatchSize: 2,
};
const roots: string[] = [];
const authorities: SqliteAuthority[] = [];

describe("SQLite Action-to-Goal projection", () => {
	it("recovers a succeeded Action after process reopen and advances the real GoalService exactly once", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const firstGoals = goalService(firstAuthority);
		await firstGoals.create({
			goalId: "goal-1",
			partition: PARTITION,
			objective: "Apply one authoritative world change",
			plan: [
				{ stepId: "step-1", description: "Apply", status: "in_progress", evidence: [] },
				{ stepId: "step-2", description: "Observe", status: "pending", evidence: [] },
			],
			gameTime: { timelineId: "world", generation: "3", tick: "1" },
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const firstActions = new ActionCoordinator(
			new SqliteActionJournal(firstAuthority, { goalProjection: ACTION_LIMITS }),
			{
				now: () => new Date("2026-08-28T00:00:01.000Z"),
				newClaimToken: () => "claim-1",
			},
		);
		const submitted = await firstActions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "run-1", turn: "2" },
			actionName: "world.write",
			operationKey: "goal-settlement",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "3", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
			goalProjection: {
				goalId: "goal-1",
				partition: PARTITION,
				stepId: "step-1",
				expectedRevision: "1",
				inputId: "input-1",
			},
		});
		const delivery = await firstActions.nextDelivery({
			...STREAM,
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected Action delivery.");
		await firstActions.recordReceipt({
			...STREAM,
			operationId: submitted.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "succeeded",
			result: { receiptId: "receipt-1", committedInputId: "input-1" },
			authorityAfter: { timelineId: "world", generation: "3", revision: "8" },
		});
		firstAuthority.close();

		const recoveredAuthority = open(path);
		const recoveredActions = new ActionCoordinator(
			new SqliteActionJournal(recoveredAuthority, { goalProjection: ACTION_LIMITS }),
			{ now: () => new Date("2026-08-28T00:00:02.000Z") },
		);
		const recoveredGoals = goalService(recoveredAuthority);
		const verify = vi.fn(
			async ({ action }: { action: { receipt?: { result?: unknown; authorityAfter?: unknown } } }) => {
				if (
					JSON.stringify(action.receipt?.result) !==
						JSON.stringify({ receiptId: "receipt-1", committedInputId: "input-1" }) ||
					JSON.stringify(action.receipt?.authorityAfter) !==
						JSON.stringify({ timelineId: "world", generation: "3", revision: "8" })
				) {
					return undefined;
				}
				return {
					evidence: { kind: "action_receipt", referenceId: "receipt-1" },
					gameTime: { timelineId: "world", generation: "3", tick: "2" },
					recordedAt: "2026-08-28T00:00:02.000Z",
				};
			},
		);
		const projector = new ActionGoalProjector({
			actions: recoveredActions,
			verifier: { verify },
			goals: recoveredGoals,
		});

		await expect(projector.projectPending({ ...STREAM, limit: 1 })).resolves.toEqual({
			results: [{ sequence: "1", operationId: submitted.operationId, status: "completed" }],
		});
		expect(verify).toHaveBeenCalledOnce();
		await expect(recoveredGoals.get({ ...PARTITION, goalId: "goal-1" })).resolves.toMatchObject({
			revision: "2",
			plan: [
				{ stepId: "step-1", status: "completed", evidence: [{ referenceId: "receipt-1" }] },
				{ stepId: "step-2", status: "in_progress" },
			],
		});
		recoveredAuthority.close();

		const finalAuthority = open(path);
		const finalActions = new ActionCoordinator(
			new SqliteActionJournal(finalAuthority, { goalProjection: ACTION_LIMITS }),
		);
		await expect(finalActions.listGoalProjections({ ...STREAM, limit: 1 })).resolves.toEqual({ candidates: [] });
		await expect(finalActions.get(STREAM, submitted.operationId)).resolves.toMatchObject({
			goalProjectionSettlement: { outcome: "advanced", settledAt: "2026-08-28T00:00:02.000Z" },
			updatedAt: "2026-08-28T00:00:02.000Z",
		});
		await expect(goalService(finalAuthority).get({ ...PARTITION, goalId: "goal-1" })).resolves.toMatchObject({
			revision: "2",
		});
	});
});

function goalService(authority: SqliteAuthority): GoalService {
	return new GoalService(
		new SqliteGoalStore(authority, GOAL_LIMITS),
		{
			validate: async ({ inputId, evidence }) =>
				inputId === "input-1" && evidence.kind === "action_receipt" && evidence.referenceId === "receipt-1",
		},
		{ validate: async ({ inputId }) => inputId === "input-1" },
	);
}

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-action-goal-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({
		path,
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		now: () => "2026-08-28T00:00:00.000Z",
	});
	authorities.push(authority);
	return authority;
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
