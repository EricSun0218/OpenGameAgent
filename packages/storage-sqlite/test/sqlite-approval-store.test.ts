import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { ActionApprovalConflictError, ActionApprovalCoordinator, prepareActionIntent } from "@opengameagent/actions";
import { ProtocolValidationError } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteActionApprovalStore, SqliteAuthority } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-approval-store-test-"));
	roots.push(root);
	return join(root, "approvals.sqlite3");
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

function clock(initial = "2026-08-28T00:00:00.000Z") {
	let current = new Date(initial);
	return {
		now: () => new Date(current),
		advance(milliseconds: number) {
			current = new Date(current.getTime() + milliseconds);
		},
	};
}

function approvals(authority: SqliteAuthority, time = clock()) {
	return new ActionApprovalCoordinator(new SqliteActionApprovalStore(authority), { now: time.now });
}

function intent(operationKey = "operation", runId = "run") {
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

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteActionApprovalStore", () => {
	it("purges terminal approvals durably without removing live approvals", async () => {
		const path = await databasePath();
		const time = clock();
		const coordinator = approvals(open(path), time);
		const rejectedPending = await coordinator.request("high-risk", intent("rejected"), 1_000);
		const rejected = await coordinator.decide({
			sessionId: "session",
			actorId: "actor",
			approvalId: rejectedPending.request.approvalId,
			expectedRevision: rejectedPending.revision,
			requestFingerprint: rejectedPending.request.requestFingerprint,
			decision: "reject",
			reviewerId: "player",
		});
		time.advance(1);
		const approvedPending = await coordinator.request("high-risk", intent("approved"), 1_000);
		const approved = await coordinator.decide({
			sessionId: "session",
			actorId: "actor",
			approvalId: approvedPending.request.approvalId,
			expectedRevision: approvedPending.revision,
			requestFingerprint: approvedPending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		time.advance(1);

		await expect(
			coordinator.purgeTerminal({
				sessionId: "session",
				actorId: "actor",
				beforeUpdatedAt: time.now().toISOString(),
				limit: 1,
				retainApprovalIds: [rejected.request.approvalId],
				retainBudget: { maxCount: 1, maxBytes: 67 },
			}),
		).rejects.toThrow("retainApprovalIds exceeds retainBudget.maxBytes.");
		await expect(
			coordinator.purgeTerminal({
				sessionId: "session",
				actorId: "actor",
				beforeUpdatedAt: time.now().toISOString(),
				limit: 1,
				retainApprovalIds: [rejected.request.approvalId],
				retainBudget: { maxCount: 1, maxBytes: Buffer.byteLength(rejected.request.approvalId, "utf8") },
			}),
		).resolves.toEqual([]);
		await expect(
			coordinator.purgeTerminal({
				sessionId: "session",
				actorId: "actor",
				beforeUpdatedAt: time.now().toISOString(),
				limit: 1,
				retainApprovalIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual([rejected.request.approvalId]);
		const restarted = approvals(open(path), time);
		await expect(restarted.get(rejected.request, rejected.request.approvalId)).resolves.toBeUndefined();
		await expect(restarted.get(approved.request, approved.request.approvalId)).resolves.toEqual(approved);
	});

	it("lists pending approvals only from the requested run", async () => {
		const coordinator = approvals(open(await databasePath()));
		await coordinator.request("high-risk", intent("first", "run-one"), 1_000);
		const second = await coordinator.request("high-risk", intent("second", "run-two"), 1_000);

		await expect(
			coordinator.listPending({ sessionId: "session", actorId: "actor", runId: "run-two", limit: 10 }),
		).resolves.toEqual([second]);
	});

	it("retains but does not discover legacy approvals without a bound run", async () => {
		const authority = open(await databasePath());
		const coordinator = approvals(authority);
		authority.write((database) => {
			database
				.prepare(`
					INSERT INTO action_approvals(
						session_id, actor_id, run_id, approval_id, request_fingerprint,
						status, revision, expires_at, record_json
					) VALUES (?, ?, NULL, ?, ?, 'pending', 1, ?, '{}')
				`)
				.run("session", "actor", "legacy", "0".repeat(64), "2026-08-28T00:00:01.000Z");
		});

		await expect(coordinator.listPending({ sessionId: "session", actorId: "actor", limit: 10 })).resolves.toEqual([]);
		const retained = authority.read(
			(database) =>
				database.prepare("SELECT COUNT(*) AS count FROM action_approvals WHERE approval_id = 'legacy'").get() as {
					count: number;
				},
		);
		expect(retained.count).toBe(1);
	});

	it("observes a decision written by another SQLite authority", async () => {
		const path = await databasePath();
		const time = clock();
		const first = approvals(open(path), time);
		const second = approvals(open(path), time);
		const pending = await first.request("high-risk", intent(), 1_000);
		const waiting = first.waitForDecision(pending, {
			pollIntervalMs: 1,
			signal: AbortSignal.timeout(500),
		});
		const decided = await second.decide({
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

	it("lets a concurrent decision win the expiration CAS across SQLite authorities", async () => {
		const path = await databasePath();
		const waitTime = clock();
		const decisionTime = clock();
		const firstStore = new SqliteActionApprovalStore(open(path));
		const first = new ActionApprovalCoordinator(firstStore, { now: waitTime.now });
		const second = approvals(open(path), decisionTime);
		const pending = await first.request("high-risk", intent(), 10);
		waitTime.advance(10);
		decisionTime.advance(9);
		let releaseExpiration: (() => void) | undefined;
		let markExpirationStarted: (() => void) | undefined;
		const expirationStarted = new Promise<void>((resolve) => {
			markExpirationStarted = resolve;
		});
		const durableExpire = firstStore.expire.bind(firstStore);
		firstStore.expire = async (input) => {
			markExpirationStarted?.();
			await new Promise<void>((resolve) => {
				releaseExpiration = resolve;
			});
			return durableExpire(input);
		};
		const waiting = first.waitForDecision(pending, {
			pollIntervalMs: 1,
			signal: AbortSignal.timeout(500),
		});
		await expirationStarted;
		const decided = await second.decide({
			sessionId: pending.request.sessionId,
			actorId: pending.request.actorId,
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		releaseExpiration?.();

		expect(decided.status).toBe("approved");
		await expect(waiting).resolves.toEqual(decided);
	});

	it("materializes one expiration for concurrent waiters across authorities", async () => {
		const path = await databasePath();
		const time = clock();
		const first = approvals(open(path), time);
		const second = approvals(open(path), time);
		const pending = await first.request("high-risk", intent(), 10);
		time.advance(10);

		const [firstResult, secondResult] = await Promise.all([
			first.waitForDecision(pending, { pollIntervalMs: 1, signal: AbortSignal.timeout(500) }),
			second.waitForDecision(pending, { pollIntervalMs: 1, signal: AbortSignal.timeout(500) }),
		]);

		expect(firstResult).toEqual(secondResult);
		expect(firstResult).toMatchObject({
			status: "expired",
			revision: "2",
			updatedAt: pending.request.expiresAt,
		});
		await expect(approvals(open(path), time).get(pending.request, pending.request.approvalId)).resolves.toEqual(
			firstResult,
		);
	});

	it("persists an exact one-time consumed approval across restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const prepared = intent();
		const first = approvals(firstAuthority);
		const pending = await first.request("high-risk", prepared, 1_000);
		const approved = await first.decide({
			sessionId: "session",
			actorId: "actor",
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		await first.consume({
			sessionId: "session",
			actorId: "actor",
			coordinate: prepared.coordinate,
			approvalId: pending.request.approvalId,
			expectedRevision: approved.revision,
			operationId: prepared.operationId,
			intentFingerprint: prepared.intentFingerprint,
			currentAuthority: prepared.expectedAuthority,
		});
		firstAuthority.close();

		const restarted = approvals(open(path));
		expect(await restarted.get(prepared, pending.request.approvalId)).toMatchObject({
			status: "consumed",
			revision: "3",
			decision: { decision: "approved", reviewerId: "player" },
		});
	});

	it("persists the exact content-bound presentation across restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const prepared = intent();
		const first = approvals(firstAuthority);
		const pending = await first.request("high-risk", prepared, 1_000, {
			presentation: {
				value: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
				limits: { maxBytes: 256, maxDepth: 2 },
			},
		});
		firstAuthority.close();

		const restarted = approvals(open(path));
		await expect(restarted.get(prepared, pending.request.approvalId)).resolves.toMatchObject({
			request: {
				requestFingerprint: pending.request.requestFingerprint,
				presentation: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
			},
			status: "pending",
		});
	});

	it("uses revision CAS so exactly one decision wins across connections", async () => {
		const path = await databasePath();
		const prepared = intent();
		const first = approvals(open(path));
		const second = approvals(open(path));
		const pending = await first.request("high-risk", prepared, 1_000);
		const input = {
			sessionId: "session",
			actorId: "actor",
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			reviewerId: "player",
		};

		await expect(first.decide({ ...input, decision: "approve" })).resolves.toMatchObject({ status: "approved" });
		await expect(second.decide({ ...input, decision: "reject" })).rejects.toThrow(ActionApprovalConflictError);
	});

	it("returns only unexpired pending approvals under the caller's explicit page bound", async () => {
		const path = await databasePath();
		const time = clock();
		const coordinator = approvals(open(path), time);
		await coordinator.request("high-risk", intent("first"), 10);
		await coordinator.request("high-risk", intent("second"), 1_000);
		time.advance(10);

		await expect(coordinator.listPending({ sessionId: "session", actorId: "actor", limit: 1 })).resolves.toMatchObject([
			{ request: { operationId: intent("second").operationId }, status: "pending" },
		]);
	});

	it("uses the shared protocol timestamp error and four-digit year domain", async () => {
		const coordinator = approvals(open(await databasePath()), clock("+010000-01-01T00:00:00.000Z"));
		let failure: unknown;
		try {
			await coordinator.listPending({ sessionId: "session", actorId: "actor", limit: 1 });
		} catch (error) {
			failure = error;
		}

		expect(failure).toBeInstanceOf(ProtocolValidationError);
		expect(failure).toMatchObject({ name: "ProtocolValidationError", code: "invalid_timestamp" });
	});

	it("fails closed when the approval index diverges from its canonical record", async () => {
		const authority = open(await databasePath());
		const coordinator = approvals(authority);
		const pending = await coordinator.request("high-risk", intent(), 1_000);
		authority.write((database) => {
			database
				.prepare("UPDATE action_approvals SET status = 'rejected' WHERE approval_id = ?")
				.run(pending.request.approvalId);
		});

		await expect(coordinator.get(pending.request, pending.request.approvalId)).rejects.toThrow(
			"durable approval index does not match",
		);
	});
});
