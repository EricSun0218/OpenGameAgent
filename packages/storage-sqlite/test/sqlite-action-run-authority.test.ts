import {
	ActionCoordinator,
	ActionGoalProjector,
	type ActionRecord,
	InMemoryActionJournal,
} from "@opengameagent/actions";
import { RunAuthorityClosedError } from "@opengameagent/protocol";
import { InMemoryEventStore } from "@opengameagent/runtime";
import { describe, expect, it, vi } from "vitest";
import { SqliteActionJournal, SqliteAuthority, SqliteEventStore } from "../src/index.js";

const stream = { sessionId: "session", actorId: "actor" };
const authorityKey = { scope: "planning", generation: "1" };
const close = { ...stream, ...authorityKey, operationId: "close" };
const limits = { maxBindingBytes: 1024, maxPendingPerStream: 4, maxListBatchSize: 4, maxRecordBytes: 8192 };
const initialTime = "2026-09-05T00:00:00.000Z";

function draft(operationKey: string, runId = "run") {
	return {
		...stream,
		coordinate: { ...stream, runId, turn: "1" },
		actionName: "world.write",
		operationKey,
		payload: { value: operationKey },
		expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
		conflictKeys: [operationKey],
		replayPolicy: "safe" as const,
	};
}

function harness(kind: "memory" | "sqlite") {
	const authority = new SqliteAuthority({
		path: ":memory:",
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		ownership: "exclusive",
	});
	const events =
		kind === "memory"
			? new InMemoryEventStore({ now: () => initialTime })
			: new SqliteEventStore(authority, { now: () => initialTime });
	const journal =
		events instanceof InMemoryEventStore
			? new InMemoryActionJournal({ runAuthority: events, goalProjection: limits })
			: new SqliteActionJournal(authority, { goalProjection: limits });
	let now = new Date(initialTime);
	let claim = 0;
	const actions = new ActionCoordinator(journal, { now: () => now, newClaimToken: () => `claim-${++claim}` });
	return {
		authority,
		events,
		journal,
		actions,
		advance: () => {
			now = new Date(now.getTime() + 11);
		},
		next: (runId?: string) =>
			actions.nextDelivery({
				...stream,
				...(runId === undefined ? {} : { runId }),
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 1,
			}),
	};
}

function claimOf(action: ActionRecord) {
	if (!action.claim) throw new Error("Expected a preserved claim.");
	return {
		...stream,
		operationId: action.operationId,
		claimToken: action.claim.claimToken,
		attempt: action.claim.attempt,
	};
}

describe.each(["memory", "sqlite"] as const)("%s Action run authority", (kind) => {
	it("rejects new intents after closure while exact persisted replays remain readable", async () => {
		const h = harness(kind);
		try {
			await h.events.append(stream, "empty", draft("first").coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority: authorityKey,
			});
			const first = await h.actions.submit(draft("first"));
			await h.events.closeAuthority(close);
			await expect(h.actions.submit(draft("late"))).rejects.toBeInstanceOf(RunAuthorityClosedError);
			expect(await h.actions.submit(draft("first"))).toEqual(first);
			await expect(h.actions.submit(draft("unscoped", "chat"))).resolves.toMatchObject({ status: "pending" });
		} finally {
			h.authority.close();
		}
	});

	it("retires bounded closed pending work across run selectors, dismisses its Goal binding, and permits purge", async () => {
		const h = harness(kind);
		try {
			await h.events.append(stream, "empty", draft("first").coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority: authorityKey,
			});
			const first = await h.actions.submit({
				...draft("first"),
				goalProjection: {
					goalId: "goal",
					partition: { ownerId: "actor", timelineId: "world", generation: "1" },
					stepId: "step",
					expectedRevision: "1",
					inputId: "goal-input",
				},
			});
			const second = await h.actions.submit(draft("second"));
			await h.events.closeAuthority(close);
			await h.events.append(stream, "1", draft("new", "new-run").coordinate, {
				type: "run.queued",
				inputId: "new-input",
				inputFingerprint: "b".repeat(64),
				admissionAuthority: { ...authorityKey, generation: "2" },
			});
			await h.actions.submit(draft("new", "new-run"));
			expect(await h.next("new-run")).toMatchObject({ kind: "dispatch", action: { coordinate: { runId: "new-run" } } });
			expect(await h.actions.get(first, first.operationId)).toMatchObject({ status: "failed", attempt: "0" });
			expect((await h.actions.get(first, first.operationId))?.receipt).toBeUndefined();
			expect(await h.actions.get(second, second.operationId)).toMatchObject({ status: "pending" });
			await h.next("new-run");
			expect(await h.actions.get(second, second.operationId)).toMatchObject({ status: "failed", attempt: "0" });
			const verify = vi.fn();
			const advanceFromHost = vi.fn();
			const projector = new ActionGoalProjector({
				actions: h.actions,
				verifier: { verify },
				goals: { advanceFromHost },
			});
			expect(await projector.projectPending({ ...stream, limit: 1 })).toMatchObject({
				results: [{ status: "dismissed", reason: "source_failed" }],
			});
			expect(verify).not.toHaveBeenCalled();
			expect(advanceFromHost).not.toHaveBeenCalled();
			expect(
				await h.actions.purgeTerminal({
					...stream,
					beforeUpdatedAt: "2026-09-05T00:00:01.000Z",
					limit: 2,
					retainOperationIds: [],
					retainBudget: { maxCount: 0, maxBytes: 0 },
				}),
			).toEqual([first.operationId, second.operationId]);
			await expect(h.actions.submit(draft("first"))).rejects.toBeInstanceOf(RunAuthorityClosedError);
		} finally {
			h.authority.close();
		}
	});

	it("retains expired safe claims and their conflict fence after closure, then accepts a truthful receipt", async () => {
		const h = harness(kind);
		try {
			await h.events.append(stream, "empty", draft("effect").coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority: authorityKey,
			});
			const effect = await h.actions.submit(draft("effect"));
			const delivery = await h.next();
			if (delivery?.kind !== "dispatch") throw new Error("Expected dispatch.");
			await h.events.closeAuthority(close);
			h.advance();
			expect(await h.next()).toMatchObject({
				kind: "reconcile",
				action: { status: "uncertain", claim: delivery.action.claim },
			});
			expect(await h.actions.recoverDelivery(stream)).toMatchObject({
				kind: "reconcile",
				action: { operationId: effect.operationId, claim: delivery.action.claim },
			});
			expect(await h.actions.recordReceipt({ ...claimOf(delivery.action), outcome: "succeeded" })).toMatchObject({
				status: "succeeded",
			});
			expect(await h.actions.recoverDelivery(stream)).toBeUndefined();
		} finally {
			h.authority.close();
		}
	});

	it("does not retry a closed action after authoritative not-applied reconciliation", async () => {
		const h = harness(kind);
		try {
			await h.events.append(stream, "empty", draft("effect").coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority: authorityKey,
			});
			await h.actions.submit(draft("effect"));
			const delivery = await h.next();
			if (!delivery) throw new Error("Expected delivery.");
			await h.events.closeAuthority(close);
			expect(await h.actions.reconcile({ ...claimOf(delivery.action), resolution: "not-applied" })).toMatchObject({
				status: "failed",
				receipt: { outcome: "rejected" },
			});
			expect(await h.next()).toBeUndefined();
		} finally {
			h.authority.close();
		}
	});

	it("preserves a safe expired claim when an earlier blocked candidate prevents atomic redelivery", async () => {
		const h = harness(kind);
		try {
			await h.events.append(stream, "empty", draft("effect").coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority: authorityKey,
			});
			await h.actions.submit({
				...draft("blocker", "blocker"),
				actorId: "other",
				coordinate: { ...stream, actorId: "other", runId: "blocker", turn: "1" },
				conflictKeys: ["blocked"],
			});
			await h.actions.nextDelivery({
				...stream,
				actorId: "other",
				claimantId: "game",
				leaseDurationMs: 100,
				candidateLimit: 1,
			});
			await h.actions.submit({ ...draft("earlier"), conflictKeys: ["blocked"] });
			const effect = await h.actions.submit(draft("effect"));
			const delivery = await h.actions.nextDelivery({
				...stream,
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 2,
			});
			if (delivery?.kind !== "dispatch") throw new Error("Expected safe dispatch.");
			h.advance();
			expect(await h.next()).toMatchObject({
				kind: "reconcile",
				action: { operationId: effect.operationId, claim: delivery.action.claim },
			});
			expect(await h.actions.get(effect, effect.operationId)).toMatchObject({
				status: "claimed",
				claim: delivery.action.claim,
			});
			await h.events.closeAuthority(close);
			expect(await h.next()).toMatchObject({
				kind: "reconcile",
				action: { operationId: effect.operationId, status: "uncertain", claim: delivery.action.claim },
			});
			expect(await h.actions.reconcile({ ...claimOf(delivery.action), resolution: "applied" })).toMatchObject({
				status: "succeeded",
			});
		} finally {
			h.authority.close();
		}
	});

	it("notifies actual delivery mutations without waking idle or repeated recovery reads", async () => {
		vi.useFakeTimers();
		const h = harness(kind);
		try {
			const empty = h.actions.changeRevision();
			await h.next();
			expect(h.actions.changeRevision()).toBe(empty);
			await h.events.append(stream, "empty", draft("effect").coordinate, {
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "a".repeat(64),
				admissionAuthority: authorityKey,
			});
			await h.actions.submit(draft("effect"));
			const beforeClaim = h.actions.changeRevision();
			const changed = h.actions.waitForChange(beforeClaim, 1000);
			await h.next();
			expect(h.actions.changeRevision()).toBeGreaterThan(beforeClaim);
			await expect(changed).resolves.toBe(true);
			const afterClaim = h.actions.changeRevision();
			await h.next();
			await h.actions.recoverDelivery(stream);
			expect(h.actions.changeRevision()).toBe(afterClaim);
			await h.events.closeAuthority(close);
			h.advance();
			const expired = h.actions.waitForChange(afterClaim, 1000);
			await h.next();
			await expect(expired).resolves.toBe(true);
			const afterExpiry = h.actions.changeRevision();
			await h.next();
			expect(h.actions.changeRevision()).toBe(afterExpiry);
			expect(vi.getTimerCount()).toBe(0);
		} finally {
			h.authority.close();
			vi.useRealTimers();
		}
	});

	it("does not lose a settlement notification arriving while its initial durable read is pending", async () => {
		vi.useFakeTimers();
		const h = harness(kind);
		try {
			await h.actions.submit(draft("effect"));
			const delivery = await h.next();
			if (!delivery) throw new Error("Expected delivery.");
			let releaseRead!: () => void;
			const readGate = new Promise<void>((resolve) => {
				releaseRead = resolve;
			});
			vi.spyOn(h.journal, "get").mockImplementationOnce(async () => {
				await readGate;
				return delivery.action;
			});
			const waiting = h.actions.waitForSettlement(delivery.action, { pollIntervalMs: 1000 });
			await h.actions.recordReceipt({ ...claimOf(delivery.action), outcome: "succeeded" });
			releaseRead();
			await expect(waiting).resolves.toMatchObject({ status: "succeeded" });
			expect(vi.getTimerCount()).toBe(0);
		} finally {
			h.authority.close();
			vi.useRealTimers();
		}
	});
});

it("checks actual in-memory authority after queued journal admission acquires its mutex", async () => {
	const h = harness("memory");
	try {
		await h.events.append(stream, "empty", draft("first").coordinate, {
			type: "run.queued",
			inputId: "input",
			inputFingerprint: "a".repeat(64),
			admissionAuthority: authorityKey,
		});
		const admission = h.journal.ensure(h.actions.prepare(draft("first")), initialTime);
		const checked = expect(admission).rejects.toBeInstanceOf(RunAuthorityClosedError);
		await h.events.closeAuthority(close);
		await checked;
		expect(await h.actions.get(stream, h.actions.prepare(draft("first")).operationId)).toBeUndefined();
	} finally {
		h.authority.close();
	}
});
