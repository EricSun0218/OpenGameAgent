import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	DelegationConflictError,
	type DelegationDraft,
	DelegationLeaseError,
	type DelegationLimits,
	DelegationManager,
	DelegationService,
} from "@opengameagent/delegation";
import { GameRuntime } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { TEST_MODEL, textResponse } from "../../devtools/src/pi-fakes.js";
import { SqliteAuthority, SqliteDelegationStore, SqliteEventStore } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const limits: DelegationLimits = {
	maxIdentifierBytes: 128,
	maxControlTextBytes: 256,
	maxToolDescriptionBytes: 512,
	maxTaskBytes: 1_024,
	maxContextBytes: 1_024,
	maxResultBytes: 1_024,
	maxModelResultBytes: 1_024,
	maxJsonDepth: 8,
	maxCounterDigits: 20,
	maxDepth: 2,
	maxTargets: 2,
	maxPendingPerGeneration: 3,
	maxActiveRuns: 2,
	maxPageSize: 3,
	maxRecoveryBatchSize: 2,
	maxPurgeBatchSize: 2,
};
const part = { ownerId: "game", timelineId: "world", generation: "1" } as const;

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteDelegationStore", () => {
	it("recovers, renews, reclaims, and idempotently replays only the settling lease", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority, tokens("lease-old"));
		const created = await first.create(draft("one"));
		const claimed = await first.claim({
			...part,
			delegationId: "one",
			leaseOwnerId: "worker-a",
			operationalNow: time(0),
			leaseExpiresAt: time(2),
		});
		await first.renew({ ...part, delegationKey: created.delegationKey }, claimed.leaseToken, time(1), time(3));
		firstAuthority.close();

		const restarted = service(open(path), tokens("lease-new"));
		await expect(
			restarted.assertLease({ ...part, delegationKey: created.delegationKey }, claimed.leaseToken, time(3)),
		).rejects.toBeInstanceOf(DelegationLeaseError);
		const reclaimed = await restarted.claim({
			...part,
			delegationId: "one",
			leaseOwnerId: "worker-b",
			operationalNow: time(3),
			leaseExpiresAt: time(5),
		});
		await expect(
			restarted.complete(
				{ ...part, delegationKey: created.delegationKey },
				claimed.leaseToken,
				{ wrong: true },
				time(4),
			),
		).rejects.toBeInstanceOf(DelegationLeaseError);
		const completed = await restarted.complete(
			{ ...part, delegationKey: created.delegationKey },
			reclaimed.leaseToken,
			{ ok: true },
			time(4),
		);
		await expect(
			restarted.complete(
				{ ...part, delegationKey: created.delegationKey },
				reclaimed.leaseToken,
				{ ok: true },
				time(4),
			),
		).resolves.toEqual(completed);
		await expect(
			restarted.complete(
				{ ...part, delegationKey: created.delegationKey },
				reclaimed.leaseToken,
				{ ok: false },
				time(4),
			),
		).rejects.toBeInstanceOf(DelegationLeaseError);
	});

	it("allows exactly one competing authority to claim a durable task", async () => {
		const path = await databasePath();
		const left = service(open(path), tokens("left"));
		const right = service(open(path), tokens("right"));
		await left.create(draft("race"));
		const attempts = await Promise.allSettled([
			left.claim({
				...part,
				delegationId: "race",
				leaseOwnerId: "left",
				operationalNow: time(0),
				leaseExpiresAt: time(2),
			}),
			right.claim({
				...part,
				delegationId: "race",
				leaseOwnerId: "right",
				operationalNow: time(0),
				leaseExpiresAt: time(2),
			}),
		]);
		expect(attempts.filter((result) => result.status === "fulfilled")).toHaveLength(1);
		expect(attempts.filter((result) => result.status === "rejected")).toHaveLength(1);
	});

	it("fails closed on semantically corrupt persisted state", async () => {
		const authority = open(":memory:");
		const current = service(authority);
		await current.create(draft("corrupt"));
		await current.create(draft("extra"));
		authority.write((database) =>
			database
				.prepare(
					`UPDATE oga_delegations SET record_json=json_set(record_json,'$.status','completed') WHERE delegation_id='corrupt'`,
				)
				.run(),
		);
		authority.write((database) =>
			database
				.prepare(
					`UPDATE oga_delegations SET record_json=json_set(record_json,'$.unexpected',1) WHERE delegation_id='extra'`,
				)
				.run(),
		);
		await expect(current.require({ ...part, delegationId: "corrupt" })).rejects.toBeInstanceOf(DelegationConflictError);
		await expect(current.require({ ...part, delegationId: "extra" })).rejects.toBeInstanceOf(DelegationConflictError);
	});

	it("filters and paginates in SQLite before hydration and purges the oldest bounded terminal batch", async () => {
		const current = service(open(":memory:"));
		const first = await current.create(draft("first"));
		const second = await current.create(draft("second"));
		const third = await current.create(draft("third"));
		await current.cancel({ ...part, delegationId: second.delegationId }, second.revision, time(1));
		const page = await current.list({ ...part, statuses: ["pending"], limit: 1 });
		expect(page.records.map((record) => record.delegationId)).toEqual(["first"]);
		expect(page.nextAfterDelegationKey).toBe(first.delegationKey);
		if (page.nextAfterDelegationKey === undefined) throw new Error("Expected a second pending page.");
		await expect(
			current.list({ ...part, statuses: ["pending"], afterDelegationKey: page.nextAfterDelegationKey, limit: 1 }),
		).resolves.toMatchObject({ records: [{ delegationId: third.delegationId }] });
		await current.cancel({ ...part, delegationId: first.delegationId }, first.revision, time(2));
		await current.cancel({ ...part, delegationId: third.delegationId }, third.revision, time(3));
		await expect(current.purgeTerminal(part, time(4), 2)).resolves.toEqual([second.delegationKey, first.delegationKey]);
		await expect(current.list({ ...part, limit: 3 })).resolves.toMatchObject({
			records: [{ delegationId: third.delegationId }],
		});
	});

	it("recovers an orphaned delegation without executing an already completed Pi child run twice", async () => {
		const path = await databasePath();
		const firstAuthority = open(path, "exclusive");
		const firstService = service(firstAuthority, tokens("lease-old"));
		const created = await firstService.create(draft("orphan"));
		await firstService.claim({
			...part,
			delegationId: created.delegationId,
			leaseOwnerId: "worker-old",
			operationalNow: time(0),
			leaseExpiresAt: time(2),
		});
		let providerCalls = 0;
		const firstRuntime = new GameRuntime(new SqliteEventStore(firstAuthority), {
			maxActiveActors: 2,
			maxQueuedRuns: 2,
			maxInputBytes: 8_192,
			maxInputDepth: 12,
			newRunId: () => "child-run",
		});
		await firstRuntime.registerActor({
			sessionId: "session",
			actorId: "child",
			model: TEST_MODEL,
			modelStream: () => {
				providerCalls += 1;
				return textResponse("done");
			},
		});
		const child = await firstRuntime.submit(childInput(created));
		await expect(child.completion).resolves.toMatchObject({ status: "completed" });
		firstAuthority.close();

		const restartedAuthority = open(path, "exclusive");
		const restartedService = service(restartedAuthority, tokens("lease-new"));
		const restartedRuntime = new GameRuntime(new SqliteEventStore(restartedAuthority), {
			maxActiveActors: 3,
			maxQueuedRuns: 2,
			maxInputBytes: 8_192,
			maxInputDepth: 12,
		});
		const manager = await DelegationManager.create({
			service: restartedService,
			runtime: restartedRuntime,
			targets: [
				{
					delegateId: "worker",
					sessionId: "session",
					actorId: "child",
					maxTurns: 2,
					actor: {
						model: TEST_MODEL,
						modelStream: () => {
							providerCalls += 1;
							return textResponse("should-not-run");
						},
					},
				},
			],
			outcome: ({ childCoordinate, childFinalOutput }) => ({
				recoveredRunId: childCoordinate.runId,
				answer: childFinalOutput,
			}),
			workerId: "worker-new",
			operationalNow: () => time(2),
			leaseExpiresAt: () => time(5),
			authorizeTarget: () => true,
		});
		await expect(manager.recover(part, new AbortController().signal)).resolves.toMatchObject([
			{ status: "completed", result: { recoveredRunId: "child-run", answer: "done" } },
		]);
		expect(providerCalls).toBe(1);
		await expect(manager.recover(part, new AbortController().signal)).resolves.toEqual([]);
	});
});

function draft(id: string): DelegationDraft {
	return {
		delegationId: id,
		partition: part,
		parent: {
			sessionId: "session",
			actorId: "parent",
			inputId: "input",
			runId: "run",
			turn: "1",
			toolCallId: `call-${id}`,
		},
		target: { delegateId: "worker", sessionId: "session", actorId: "child", maxTurns: 2 },
		mode: "background",
		task: { id },
		rootDelegationId: id,
		depth: "1",
		childInputId: `child-${id}`,
		createdAt: time(0),
	};
}
function childInput(record: Awaited<ReturnType<DelegationService["create"]>>) {
	return {
		sessionId: record.target.sessionId,
		actorId: record.target.actorId,
		inputId: record.childInputId,
		payload: {
			type: "delegation.task" as const,
			delegation: {
				delegationId: record.delegationId,
				rootDelegationId: record.rootDelegationId,
				depth: record.depth,
				parent: {
					sessionId: record.parent.sessionId,
					actorId: record.parent.actorId,
					inputId: record.parent.inputId,
					runId: record.parent.runId,
					turn: record.parent.turn,
				},
			},
			task: record.task,
		},
	};
}
async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-delegation-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}
function open(path: string, ownership: "shared" | "exclusive" = "shared"): SqliteAuthority {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full", ownership });
	authorities.push(authority);
	return authority;
}
function service(authority: SqliteAuthority, newLeaseToken?: () => string): DelegationService {
	return new DelegationService(new SqliteDelegationStore(authority, limits, newLeaseToken), limits);
}
function tokens(...values: string[]): () => string {
	return () => values.shift() ?? "lease-extra";
}
function time(second: number): string {
	return `2026-08-29T04:00:${second.toString().padStart(2, "0")}.000Z`;
}
