import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	type ActionClaim,
	ActionClaimConflictError,
	ActionCoordinator,
	type ActionRecord,
	ActionStateConflictError,
	prepareActionIntent,
} from "@opengameagent/actions";
import { RunAuthorityClosedError } from "@opengameagent/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SqliteActionJournal, SqliteAuthority, SqliteEventStore } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-action-journal-test-"));
	roots.push(root);
	return join(root, "actions.sqlite3");
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

function clock(initial = "2026-08-28T00:00:00.000Z") {
	let current = new Date(initial);
	return {
		now: () => new Date(current),
		advance(milliseconds: number) {
			current = new Date(current.getTime() + milliseconds);
		},
	};
}

function coordinator(authority: SqliteAuthority, time = clock(), tokens = ["claim-one", "claim-two"]) {
	let token = 0;
	return {
		actions: new ActionCoordinator(new SqliteActionJournal(authority), {
			now: time.now,
			newClaimToken: () => tokens[token++] ?? `claim-${token}`,
		}),
		time,
	};
}

async function dispatch(
	actions: ActionCoordinator,
	options: Parameters<ActionCoordinator["nextDelivery"]>[0],
): Promise<ActionRecord & { claim: ActionClaim }> {
	const delivery = await actions.nextDelivery(options);
	if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected a dispatched action.");
	return { ...delivery.action, claim: delivery.action.claim };
}

function draft(operationKey: string, actorId = "actor-one", conflictKey = "world:tile:1") {
	return {
		sessionId: "session",
		actorId,
		coordinate: { sessionId: "session", actorId, runId: `run-${actorId}`, turn: "1" },
		actionName: "world.write",
		operationKey,
		payload: { value: operationKey },
		expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
		conflictKeys: [conflictKey],
		replayPolicy: "recoverable" as const,
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

describe("SqliteActionJournal", () => {
	it("preserves the run fence after event and Action purge across process reopen", async () => {
		const path = await databasePath();
		const authority = open(path, "exclusive");
		const journal = new SqliteActionJournal(authority);
		const events = new SqliteEventStore(authority, { now: () => "2026-08-28T00:00:00.000Z" });
		const prepared = prepareActionIntent(draft("fenced"));
		const admissionAuthority = { scope: "planning", generation: "1" };
		await events.append({ sessionId: prepared.sessionId, actorId: prepared.actorId }, "empty", prepared.coordinate, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: "a".repeat(64),
			admissionAuthority,
		});
		await journal.ensure(prepared, "2026-08-28T00:00:00.000Z");
		await events.append({ sessionId: prepared.sessionId, actorId: prepared.actorId }, "1", prepared.coordinate, {
			type: "run.completed",
			status: "aborted",
		});
		await events.purgeTerminalRuns(
			{ sessionId: prepared.sessionId, actorId: prepared.actorId },
			{
				beforeOccurredAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainRunIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			},
		);
		await events.closeAuthority({ ...prepared, ...admissionAuthority, operationId: "close" });
		await journal.nextDelivery({
			...prepared,
			claimantId: "game",
			claimToken: "claim",
			claimedAt: "2026-08-28T00:00:00.001Z",
			expiresAt: "2026-08-28T00:00:01.000Z",
			candidateLimit: 1,
		});
		expect(
			await journal.purgeTerminal({
				...prepared,
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainOperationIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).toEqual([prepared.operationId]);
		authority.close();
		const reopened = new SqliteActionJournal(open(path));
		await expect(reopened.ensure(prepared, "2026-08-28T00:00:02.000Z")).rejects.toBeInstanceOf(RunAuthorityClosedError);
		expect(await reopened.get(prepared, prepared.operationId)).toBeUndefined();
	});

	it("purges terminal Actions in bounded durable order without deleting unresolved records", async () => {
		const path = await databasePath();
		const authority = open(path);
		const time = clock();
		const { actions } = coordinator(authority, time, ["claim-one", "claim-two"]);
		const first = await actions.submit({ ...draft("purge-first"), conflictKeys: ["first"] });
		const firstClaim = await dispatch(actions, {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		await actions.recordReceipt({
			...first,
			claimToken: firstClaim.claim.claimToken,
			attempt: firstClaim.claim.attempt,
			outcome: "succeeded",
		});
		time.advance(1);
		const pending = await actions.submit({ ...draft("purge-pending"), conflictKeys: ["pending"] });

		await expect(
			actions.purgeTerminal({
				sessionId: "session",
				actorId: "actor-one",
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainOperationIds: [first.operationId],
				retainBudget: { maxCount: 0, maxBytes: 68 },
			}),
		).rejects.toThrow("retainOperationIds exceeds retainBudget.maxCount.");
		await expect(
			actions.purgeTerminal({
				sessionId: "session",
				actorId: "actor-one",
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainOperationIds: [first.operationId],
				retainBudget: { maxCount: 1, maxBytes: Buffer.byteLength(first.operationId, "utf8") },
			}),
		).resolves.toEqual([]);
		await expect(
			actions.purgeTerminal({
				sessionId: "session",
				actorId: "actor-one",
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainOperationIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual([first.operationId]);
		authority.close();
		const restarted = coordinator(open(path)).actions;
		await expect(restarted.get(first, first.operationId)).resolves.toBeUndefined();
		await expect(restarted.get(pending, pending.operationId)).resolves.toMatchObject({ status: "pending" });
	});

	it("fails a populated v2 Action journal closed before installing exact-run identity", async () => {
		const authority = open(await databasePath());
		const install = vi.spyOn(authority, "install").mockImplementation(() => undefined);
		SqliteActionJournal.install(authority);
		const migrations = install.mock.calls[0]?.[1];
		install.mockRestore();
		if (migrations === undefined) throw new Error("Expected the Action migrations.");
		authority.install("durable-actions", migrations.slice(0, 2));
		authority.write((database) => {
			database
				.prepare(`
					INSERT INTO action_records(
						action_sequence, session_id, actor_id, operation_id, intent_fingerprint,
						status, attempt, claim_expires_at, record_json
					) VALUES (1, 'session', 'actor', ?, ?, 'pending', 0, NULL, '{}')
				`)
				.run(`act_${"0".repeat(64)}`, "0".repeat(64));
		});

		expect(() => new SqliteActionJournal(authority)).toThrow("legacy_action_count = 0");
		expect(
			authority.read((database) =>
				database
					.prepare("SELECT version FROM oga_schema_migrations WHERE namespace = 'durable-actions' ORDER BY version")
					.pluck()
					.all(),
			),
		).toEqual([1, 2]);

		authority.write((database) => database.prepare("DELETE FROM action_records").run());
		expect(() => new SqliteActionJournal(authority)).not.toThrow();
		expect(
			authority.read((database) =>
				(database.pragma("table_info(action_records)") as Array<{ name: string }>).some(
					({ name }) => name === "run_id",
				),
			),
		).toBe(true);
	});

	it("keeps otherwise identical Actions from different runs as distinct durable operations", async () => {
		const { actions } = coordinator(open(await databasePath()));
		const first = await actions.submit(draft("same-operation"));
		const second = await actions.submit({
			...draft("same-operation"),
			coordinate: { sessionId: "session", actorId: "actor-one", runId: "run-two", turn: "1" },
		});

		expect(second.operationId).not.toBe(first.operationId);
		expect(second.intentFingerprint).not.toBe(first.intentFingerprint);
		await expect(actions.get(first, first.operationId)).resolves.toMatchObject({
			coordinate: { runId: "run-actor-one" },
		});
		await expect(actions.get(second, second.operationId)).resolves.toMatchObject({ coordinate: { runId: "run-two" } });
	});

	it("claims an Action only from the requested run", async () => {
		const { actions } = coordinator(open(await databasePath()));
		const first = await actions.submit({ ...draft("first-run"), conflictKeys: ["first-key"] });
		const second = await actions.submit({
			...draft("second-run"),
			coordinate: { sessionId: "session", actorId: "actor-one", runId: "run-two", turn: "1" },
			conflictKeys: ["second-key"],
		});

		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				runId: "run-two",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 1,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: second.operationId } });
		await expect(actions.get(first, first.operationId)).resolves.toMatchObject({ status: "pending", attempt: "0" });
	});

	it("does not expire or reconcile a SQLite claim from another requested run", async () => {
		const time = clock();
		const { actions } = coordinator(open(await databasePath()), time, ["first-claim", "second-claim"]);
		const first = await actions.submit({ ...draft("claimed-run"), replayPolicy: "safe", conflictKeys: ["first-key"] });
		const second = await actions.submit({
			...draft("requested-run"),
			coordinate: { sessionId: "session", actorId: "actor-one", runId: "run-two", turn: "1" },
			conflictKeys: ["second-key"],
		});
		await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			runId: "run-actor-one",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 1,
		});
		time.advance(11);

		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				runId: "run-two",
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 1,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: second.operationId } });
		await expect(actions.get(first, first.operationId)).resolves.toMatchObject({ status: "claimed", attempt: "1" });
	});

	it("recovers the oldest unsettled Action read-only across database reopen", async () => {
		const path = await databasePath();
		const time = clock();
		const firstAuthority = open(path);
		const first = coordinator(firstAuthority, time, ["claim-one"]).actions;
		const claimed = await first.submit(draft("claimed", "actor-one", "claimed-key"));
		const pending = await first.submit(draft("pending", "actor-one", "pending-key"));
		const delivery = await first.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch") throw new Error("Expected a dispatched Action.");
		firstAuthority.close();
		time.advance(11);

		const recovered = coordinator(open(path), time).actions;
		await expect(
			Promise.all([
				recovered.recoverDelivery({ sessionId: "session", actorId: "actor-one" }),
				recovered.recoverDelivery({ sessionId: "session", actorId: "actor-one" }),
			]),
		).resolves.toEqual([
			{ kind: "reconcile", action: delivery.action },
			{ kind: "reconcile", action: delivery.action },
		]);
		await expect(recovered.get(claimed, claimed.operationId)).resolves.toEqual(delivery.action);
		await expect(recovered.get(pending, pending.operationId)).resolves.toMatchObject({
			status: "pending",
			attempt: "0",
		});
		await expect(recovered.recoverDelivery({ sessionId: "session", actorId: "actor-two" })).resolves.toBeUndefined();
	});

	it("waits across SQLite authorities for the exact Action settlement", async () => {
		const path = await databasePath();
		const reader = coordinator(open(path)).actions;
		const writer = coordinator(open(path), clock(), ["writer-claim"]).actions;
		const submitted = await reader.submit(draft("cross-authority-wait"));
		const waiting = reader.waitForSettlement(submitted, {
			pollIntervalMs: 1,
			signal: AbortSignal.timeout(500),
		});
		const claimed = await dispatch(writer, {
			sessionId: submitted.sessionId,
			actorId: submitted.actorId,
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		const settled = await writer.recordReceipt({
			sessionId: submitted.sessionId,
			actorId: submitted.actorId,
			operationId: submitted.operationId,
			claimToken: claimed.claim.claimToken,
			attempt: claimed.claim.attempt,
			outcome: "succeeded",
		});

		await expect(waiting).resolves.toEqual(settled);
	});

	it("recovers and completes bounded Goal projections across database reopen", async () => {
		const path = await databasePath();
		const limits = {
			maxBindingBytes: 1_024,
			maxPendingPerStream: 2,
			maxListBatchSize: 1,
			maxRecordBytes: 8_192,
		} as const;
		const firstAuthority = open(path);
		const first = new ActionCoordinator(new SqliteActionJournal(firstAuthority, { goalProjection: limits }), {
			now: () => new Date("2026-08-28T00:00:02.000Z"),
			newClaimToken: () => "claim-goal",
		});
		const intent = await first.submit({
			...draft("goal-reopen"),
			goalProjection: {
				goalId: "goal-1",
				partition: {
					ownerId: "npc-1",
					timelineId: "world",
					generation: "2",
					sessionId: "session",
					actorId: "actor-one",
				},
				stepId: "step-1",
				expectedRevision: "4",
				inputId: "input-1",
			},
		});
		const claimed = await dispatch(first, {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		await first.recordReceipt({
			sessionId: "session",
			actorId: "actor-one",
			operationId: intent.operationId,
			claimToken: claimed.claim.claimToken,
			attempt: claimed.claim.attempt,
			outcome: "succeeded",
			authorityAfter: { timelineId: "world", generation: "2", revision: "8" },
		});
		firstAuthority.close();

		const secondAuthority = open(path);
		const second = new ActionCoordinator(new SqliteActionJournal(secondAuthority, { goalProjection: limits }), {
			now: () => new Date("2026-08-28T00:00:03.000Z"),
		});
		await expect(
			second.listGoalProjections({ sessionId: "session", actorId: "actor-one", limit: 1 }),
		).resolves.toMatchObject({
			candidates: [{ sequence: "1", action: { operationId: intent.operationId } }],
		});
		await second.settleGoalProjection({
			sessionId: "session",
			actorId: "actor-one",
			operationId: intent.operationId,
			intentFingerprint: intent.intentFingerprint,
			outcome: "advanced",
		});
		secondAuthority.close();

		const reopened = new ActionCoordinator(new SqliteActionJournal(open(path), { goalProjection: limits }));
		await expect(
			reopened.listGoalProjections({ sessionId: "session", actorId: "actor-one", limit: 1 }),
		).resolves.toEqual({ candidates: [] });
		await expect(reopened.get(intent, intent.operationId)).resolves.toMatchObject({
			goalProjectionSettlement: { outcome: "advanced", settledAt: "2026-08-28T00:00:03.000Z" },
			updatedAt: "2026-08-28T00:00:03.000Z",
		});
	});

	it("fails bounded Goal projection hydration before parsing an oversized durable Action", async () => {
		const authority = open(await databasePath());
		const limits = {
			maxBindingBytes: 1_024,
			maxPendingPerStream: 2,
			maxListBatchSize: 1,
			maxRecordBytes: 8_192,
		} as const;
		const writer = new ActionCoordinator(new SqliteActionJournal(authority, { goalProjection: limits }));
		const intent = await writer.submit({
			...draft("goal-hydration"),
			goalProjection: {
				goalId: "goal-1",
				partition: {
					ownerId: "npc-1",
					timelineId: "world",
					generation: "2",
					sessionId: "session",
					actorId: "actor-one",
				},
				stepId: "step-1",
				expectedRevision: "4",
				inputId: "input-1",
			},
		});
		const boundedReader = new SqliteActionJournal(authority, {
			goalProjection: { ...limits, maxRecordBytes: 1 },
		});

		await expect(
			boundedReader.listGoalProjections({ sessionId: "session", actorId: "actor-one", limit: 1 }),
		).rejects.toThrow("hydration byte budget");
		await expect(boundedReader.get(intent, intent.operationId)).rejects.toThrow("hydration byte budget");
	});

	it("snapshots Goal projection list requests before SQLite validation and enumeration", async () => {
		const authority = open(await databasePath());
		const actions = new ActionCoordinator(
			new SqliteActionJournal(authority, {
				goalProjection: {
					maxBindingBytes: 1_024,
					maxPendingPerStream: 1,
					maxListBatchSize: 1,
					maxRecordBytes: 8_192,
				},
			}),
		);
		await actions.submit({
			...draft("goal-list-snapshot"),
			goalProjection: {
				goalId: "goal-1",
				partition: { ownerId: "npc-1", timelineId: "world", generation: "2" },
				stepId: "step-1",
				expectedRevision: "3",
				inputId: "input-1",
			},
		});
		const reads = { sessionId: 0, actorId: 0, afterSequence: 0, limit: 0 };
		const counted =
			<Key extends keyof typeof reads, Value>(key: Key, value: Value) =>
			() => {
				reads[key] += 1;
				return value;
			};
		const request = Object.create(null) as Record<string, unknown>;
		Object.defineProperties(request, {
			sessionId: { enumerable: true, get: counted("sessionId", "session") },
			actorId: { enumerable: true, get: counted("actorId", "actor-one") },
			afterSequence: { enumerable: true, get: counted("afterSequence", "0") },
			limit: { enumerable: true, get: counted("limit", 1) },
		});

		await expect(actions.listGoalProjections(request as never)).resolves.toMatchObject({
			candidates: [{ action: { operationKey: "goal-list-snapshot" } }],
		});
		expect(reads).toEqual({ sessionId: 1, actorId: 1, afterSequence: 1, limit: 1 });
	});

	it("rejects an oversized Goal-bound receipt atomically before it can poison recovery", async () => {
		const authority = open(await databasePath());
		const actions = new ActionCoordinator(
			new SqliteActionJournal(authority, {
				goalProjection: {
					maxBindingBytes: 1_024,
					maxPendingPerStream: 1,
					maxListBatchSize: 1,
					maxRecordBytes: 2_048,
				},
			}),
			{ newClaimToken: () => "claim-goal-record-budget" },
		);
		const submitted = await actions.submit({
			...draft("goal-record-budget"),
			goalProjection: {
				goalId: "goal-1",
				partition: { ownerId: "npc-1", timelineId: "world", generation: "2" },
				stepId: "step-1",
				expectedRevision: "3",
				inputId: "input-1",
			},
		});
		const claimed = await dispatch(actions, {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});

		await expect(
			actions.recordReceipt({
				sessionId: "session",
				actorId: "actor-one",
				operationId: submitted.operationId,
				claimToken: claimed.claim.claimToken,
				attempt: claimed.claim.attempt,
				outcome: "succeeded",
				result: { receipt: "x".repeat(4_096) },
			}),
		).rejects.toThrow("record exceeds");
		const retained = await actions.get(submitted, submitted.operationId);
		expect(retained).toMatchObject({ status: "claimed" });
		expect(retained?.receipt).toBeUndefined();
	});

	it("rejects non-canonical action time before durable mutation", async () => {
		const authority = open(await databasePath());
		const journal = new SqliteActionJournal(authority);
		const prepared = prepareActionIntent(draft("timestamp"));
		await expect(journal.ensure(prepared, "2026-08-28")).rejects.toThrow();
		await expect(journal.get(prepared, prepared.operationId)).resolves.toBeUndefined();
	});

	it("persists an exact claim and authoritative receipt across restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const { actions } = coordinator(firstAuthority);
		const intent = await actions.submit(draft("operation"));
		const claimed = await dispatch(actions, {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		await actions.recordReceipt({
			sessionId: "session",
			actorId: "actor-one",
			operationId: intent.operationId,
			claimToken: claimed.claim.claimToken,
			attempt: claimed.claim.attempt,
			outcome: "succeeded",
			result: { visible: "settled" },
			authorityAfter: { timelineId: "world", generation: "2", revision: "8" },
		});
		firstAuthority.close();

		const restarted = coordinator(open(path)).actions;
		expect(await restarted.get(intent, intent.operationId)).toMatchObject({
			status: "succeeded",
			attempt: "1",
			receipt: { outcome: "succeeded", result: { visible: "settled" } },
		});
		await expect(
			restarted.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 1,
			}),
		).resolves.toBeUndefined();
	});

	it("replays a committed receipt after response loss and restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const { actions } = coordinator(firstAuthority);
		const intent = await actions.submit(draft("receipt-response-loss"));
		const claim = await dispatch(actions, {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		const input = {
			sessionId: "session",
			actorId: "actor-one",
			operationId: intent.operationId,
			claimToken: claim.claim.claimToken,
			attempt: claim.claim.attempt,
			outcome: "succeeded" as const,
			result: { applied: true },
		};
		const committed = await actions.recordReceipt(input);
		firstAuthority.close();

		const restarted = coordinator(open(path), clock("2026-08-28T00:00:00.001Z")).actions;
		await expect(restarted.recordReceipt(input)).resolves.toEqual(committed);
		await expect(restarted.recordReceipt({ ...input, result: { applied: false } })).rejects.toThrow(
			ActionStateConflictError,
		);
		await expect(restarted.recordReceipt({ ...input, claimToken: "old-token" })).rejects.toThrow(
			ActionClaimConflictError,
		);
	});

	it("replays a committed reconciliation after response loss and fences it from a new attempt", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const { actions } = coordinator(firstAuthority, clock(), ["claim-one"]);
		const intent = await actions.submit({ ...draft("reconcile-response-loss"), replayPolicy: "safe" });
		const claim = await dispatch(actions, {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		const input = {
			sessionId: "session",
			actorId: "actor-one",
			operationId: intent.operationId,
			claimToken: claim.claim.claimToken,
			attempt: claim.claim.attempt,
			resolution: "not-applied" as const,
			result: { inspected: "not-applied" },
		};
		const committed = await actions.reconcile(input);
		firstAuthority.close();

		const restarted = coordinator(open(path), clock("2026-08-28T00:00:00.001Z"), ["claim-two"]).actions;
		await expect(restarted.reconcile(input)).resolves.toEqual(committed);
		const redelivery = await restarted.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (redelivery?.kind !== "dispatch" || !redelivery.action.claim) {
			throw new Error("Expected a new fenced dispatch.");
		}
		expect(redelivery.action.attempt).toBe("2");
		await expect(restarted.reconcile(input)).rejects.toThrow(ActionClaimConflictError);
		await expect(
			restarted.reconcile({
				...input,
				claimToken: redelivery.action.claim.claimToken,
				attempt: redelivery.action.claim.attempt,
				resolution: "applied",
				result: { inspected: "applied" },
			}),
		).resolves.toMatchObject({ status: "succeeded", attempt: "2", receipt: { outcome: "succeeded" } });
	});

	it("preserves one cross-actor conflict owner across SQLite connections", async () => {
		const path = await databasePath();
		const first = coordinator(open(path), clock(), ["first-claim"]).actions;
		const second = coordinator(open(path), clock(), ["second-claim"]).actions;
		await first.submit(draft("first"));
		await second.submit(draft("second", "actor-two"));
		await expect(
			first.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				claimantId: "game-one",
				leaseDurationMs: 1_000,
				candidateLimit: 2,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { actorId: "actor-one", status: "claimed" } });
		await expect(
			second.nextDelivery({
				sessionId: "session",
				actorId: "actor-two",
				claimantId: "game-two",
				leaseDurationMs: 1_000,
				candidateLimit: 2,
			}),
		).resolves.toBeUndefined();
	});

	it("selects one dispatch or the oldest unresolved reconciliation in a single write transaction", async () => {
		const path = await databasePath();
		const producer = coordinator(open(path)).actions;
		const firstConsumer = coordinator(open(path), clock(), ["claim-one"]).actions;
		const secondConsumer = coordinator(open(path), clock(), ["claim-two"]).actions;
		const first = await producer.submit(draft("first", "actor-one", "world:tile:1"));
		const second = await producer.submit(draft("second", "actor-one", "world:tile:2"));
		const request = {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 2,
		};

		const redelivery = await Promise.all([firstConsumer.nextDelivery(request), secondConsumer.nextDelivery(request)]);

		expect(redelivery).toEqual([
			expect.objectContaining({
				kind: "dispatch",
				action: expect.objectContaining({ operationId: first.operationId, attempt: "1" }),
			}),
			expect.objectContaining({
				kind: "reconcile",
				action: expect.objectContaining({ operationId: first.operationId, attempt: "1" }),
			}),
		]);
		await expect(producer.get(second, second.operationId)).resolves.toMatchObject({ status: "pending", attempt: "0" });
	});

	it("retains an expired non-safe claim and conflict until reconciliation proves no write", async () => {
		const path = await databasePath();
		const time = clock();
		const { actions } = coordinator(open(path), time, ["first-claim", "second-claim"]);
		const first = await actions.submit(draft("first"));
		const second = await actions.submit(draft("second", "actor-two"));
		const claimed = await dispatch(actions, {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 2,
		});
		time.advance(11);
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 2,
			}),
		).resolves.toMatchObject({ kind: "reconcile", action: { operationId: first.operationId, status: "uncertain" } });
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-two",
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 2,
			}),
		).resolves.toBeUndefined();
		await expect(
			actions.reconcile({
				sessionId: "session",
				actorId: "actor-one",
				operationId: first.operationId,
				claimToken: claimed.claim.claimToken,
				attempt: claimed.claim.attempt,
				resolution: "not-applied",
			}),
		).resolves.toMatchObject({ status: "pending" });
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-two",
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 2,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: second.operationId, status: "claimed" } });
	});

	it("fails closed when indexed state and canonical record JSON diverge", async () => {
		const authority = open(await databasePath());
		const { actions } = coordinator(authority);
		const intent = await actions.submit(draft("corrupt"));
		authority.write((database) => {
			database.prepare("UPDATE action_records SET status = 'failed' WHERE operation_id = ?").run(intent.operationId);
		});

		await expect(actions.get(intent, intent.operationId)).rejects.toThrow("durable action index does not match");
	});
});
