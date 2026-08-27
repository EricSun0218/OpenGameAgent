import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameControlResult, GameSessionKey, JsonValue } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { type GameDelegationOutcome, type GameDelegationRequest, SqliteGameDelegationStore } from "./delegation.js";
import { GameDelegationManager } from "./manager.js";
import type {
	GameDelegationExecutionAuthority,
	GameDelegationExecutor,
	GameDelegationHandle,
} from "./runtime-executor.js";

const directories: string[] = [];

function session(actorId = "actor-a", ownerId = "owner-a", generation = 1): GameSessionKey {
	return {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation,
		ownerId,
		sessionId: `session-${actorId}`,
		actorId,
	};
}

function request(id = "delegation-a", gameSession = session()): GameDelegationRequest {
	return {
		id,
		session: gameSession,
		parentInputId: "input-a",
		parentRunId: "run-a",
		parentTurn: 1,
		parentMoment: { tick: 42 },
		delegateId: "scout",
		task: { objective: "inspect" },
		depth: 1,
		maximumTurns: 8,
		inheritContext: false,
		rootDelegationId: id,
	};
}

async function path(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-delegation-"));
	directories.push(directory);
	return join(directory, "delegation.sqlite");
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

class DeferredHandle implements GameDelegationHandle {
	readonly completion: Promise<GameDelegationOutcome>;
	private resolve!: (outcome: GameDelegationOutcome) => void;
	abortCount = 0;
	steers: JsonValue[] = [];

	constructor() {
		this.completion = new Promise((resolve) => {
			this.resolve = resolve;
		});
	}

	complete(outcome: GameDelegationOutcome): void {
		this.resolve(outcome);
	}

	async steer(message: JsonValue): Promise<GameControlResult> {
		this.steers.push(structuredClone(message));
		return { accepted: true };
	}

	abort(): GameControlResult {
		this.abortCount += 1;
		return { accepted: true };
	}

	async [Symbol.asyncDispose](): Promise<void> {}
}

class ControlledExecutor implements GameDelegationExecutor {
	readonly starts: Array<{
		request: GameDelegationRequest;
		authority: GameDelegationExecutionAuthority;
		handle: DeferredHandle;
	}> = [];

	start(
		delegation: GameDelegationRequest,
		authority: GameDelegationExecutionAuthority,
		_signal: AbortSignal,
	): GameDelegationHandle {
		const handle = new DeferredHandle();
		this.starts.push({ request: structuredClone(delegation), authority, handle });
		return handle;
	}
}

describe("SqliteGameDelegationStore", () => {
	it("uses session-scoped idempotent creation and durable fencing leases", async () => {
		const database = await path();
		const store = new SqliteGameDelegationStore(database);
		const created = await store.create(request());
		expect(await store.create(request())).toEqual(created);
		await expect(store.create({ ...request(), task: { objective: "different" } })).rejects.toThrow(/different content/);
		expect(await store.read(session("actor-b"), created.request.id)).toBeUndefined();

		const first = await store.claim(session(), created.request.id, "worker-a", 1_000, 1_000);
		expect(first.kind).toBe("leased");
		if (first.kind !== "leased") throw new Error("Expected a lease.");
		expect(first.lease.fencingToken).toBe(1);
		expect(await store.claim(session(), created.request.id, "worker-b", 1_500, 1_000)).toMatchObject({ kind: "busy" });
		expect(await store.isLeaseAuthoritative(session(), created.request.id, first.lease, 1_500)).toBe(true);

		const renewed = await store.renew(session(), created.request.id, first.lease, 1_500, 1_000);
		expect(renewed.expiresAt).toBe(2_500);
		expect(await store.isLeaseAuthoritative(session(), created.request.id, first.lease, 1_500)).toBe(false);
		await expect(
			store.settle(session(), created.request.id, first.lease, { status: "completed", result: { ok: true } }, 1_500),
		).rejects.toThrow(/stale/);
		const terminal = await store.settle(
			session(),
			created.request.id,
			renewed,
			{ status: "completed", result: { ok: true } },
			1_500,
		);
		expect(terminal).toMatchObject({ status: "completed", result: { ok: true }, attempt: 1 });
		store.close();

		using reopened = new SqliteGameDelegationStore(database);
		expect(await reopened.read(session(), created.request.id)).toEqual(terminal);
	});

	it("reclaims only expired work with a higher fence and rejects stale settlement", async () => {
		using store = new SqliteGameDelegationStore(await path());
		await store.create(request());
		const first = await store.claim(session(), "delegation-a", "worker-a", 1_000, 100);
		if (first.kind !== "leased") throw new Error("Expected a lease.");
		expect(await store.listRecoverable(1_099, 8)).toHaveLength(0);
		expect(await store.listRecoverable(1_100, 8)).toHaveLength(1);
		const second = await store.claim(session(), "delegation-a", "worker-b", 1_100, 100);
		if (second.kind !== "leased") throw new Error("Expected a replacement lease.");
		expect(second.lease.fencingToken).toBe(2);
		expect(await store.isLeaseAuthoritative(session(), "delegation-a", first.lease, 1_100)).toBe(false);
		await expect(
			store.settle(session(), "delegation-a", first.lease, { status: "completed", result: null }, 1_100),
		).rejects.toThrow(/stale/);
		await expect(
			store.settle(session(), "delegation-a", second.lease, { status: "completed", result: null }, 1_200),
		).rejects.toThrow(/expired/);
	});

	it("keeps lineage exact, cancellation idempotent, and terminal retention bounded", async () => {
		using store = new SqliteGameDelegationStore(await path(), { terminalRetentionPerSession: 2 });
		const root = request("root");
		await store.create(root);
		await expect(
			store.create({
				...request("child"),
				parentDelegationId: "root",
				rootDelegationId: "wrong-root",
				depth: 2,
			}),
		).rejects.toThrow(/root/);
		const child = await store.create({
			...request("child"),
			parentDelegationId: "root",
			rootDelegationId: "root",
			depth: 2,
		});
		expect(child.request.parentDelegationId).toBe("root");

		for (const id of ["root", "child", "third"]) {
			if (id === "third") await store.create(request(id));
			const cancelled = await store.cancel(session(), id, "host cancelled");
			expect(await store.cancel(session(), id, "host cancelled again")).toEqual(cancelled);
		}
		const records = await store.list(session(), 16);
		expect(records).toHaveLength(2);
		expect(records.every((record) => record.status === "cancelled")).toBe(true);
	});

	it("fails closed when persisted state is corrupt", async () => {
		const database = await path();
		using store = new SqliteGameDelegationStore(database);
		await store.create(request());
		const databaseHandle = new (await import("node:sqlite")).DatabaseSync(database);
		databaseHandle.prepare("UPDATE game_delegations SET record_json=?").run("{broken");
		databaseHandle.close();
		await expect(store.read(session(), "delegation-a")).rejects.toThrow(/corrupt/);
	});
});

describe("GameDelegationManager", () => {
	it("bounds concurrent delegated agents and persists their terminal results", async () => {
		using store = new SqliteGameDelegationStore(await path());
		const executor = new ControlledExecutor();
		await using manager = new GameDelegationManager({
			store,
			executor,
			workerId: "worker",
			maximumConcurrent: 1,
			leaseMilliseconds: 1_000,
			renewalMilliseconds: 250,
		});
		await manager.submit(request("first"), { background: true });
		await manager.submit(request("second"), { background: true });
		await viWait(() => executor.starts.length === 1);
		executor.starts[0]?.handle.complete({ status: "completed", result: { value: 1 } });
		await viWait(() => executor.starts.length === 2);
		executor.starts[1]?.handle.complete({ status: "completed", result: { value: 2 } });
		await viWait(async () => (await manager.read(session(), "second"))?.status === "completed");
		expect((await manager.read(session(), "first"))?.result).toEqual({ value: 1 });
	});

	it("cancels a running task durably and routes steering only to its active handle", async () => {
		using store = new SqliteGameDelegationStore(await path());
		const executor = new ControlledExecutor();
		await using manager = new GameDelegationManager({
			store,
			executor,
			workerId: "worker",
			leaseMilliseconds: 1_000,
			renewalMilliseconds: 250,
		});
		await manager.submit(request(), { background: true });
		await viWait(() => executor.starts.length === 1);
		expect(await manager.steer(session(), "delegation-a", { update: 1 })).toEqual({ accepted: true });
		expect(executor.starts[0]?.handle.steers).toEqual([{ update: 1 }]);
		const cancelled = await manager.cancel(session(), "delegation-a", "no longer needed");
		expect(cancelled.status).toBe("cancelled");
		expect(executor.starts[0]?.handle.abortCount).toBe(1);
		executor.starts[0]?.handle.complete({ status: "cancelled", error: "Delegated run was cancelled." });
		await viWait(async () => !(await manager.steer(session(), "delegation-a", null)).accepted);
		expect((await manager.read(session(), "delegation-a"))?.error).toBe("no longer needed");
		expect(await manager.steer(session(), "delegation-a", null)).toEqual({ accepted: false, reason: "not-active" });
	});

	it("leaves uncooperative shutdown work recoverable and resumes it after lease expiry", async () => {
		using store = new SqliteGameDelegationStore(await path());
		let now = 1_000;
		const firstExecutor = new ControlledExecutor();
		const first = new GameDelegationManager({
			store,
			executor: firstExecutor,
			workerId: "worker-a",
			leaseMilliseconds: 100,
			renewalMilliseconds: 25,
			shutdownTimeoutMilliseconds: 100,
			now: () => now,
		});
		await first.submit(request(), { background: true });
		await viWait(() => firstExecutor.starts.length === 1);
		await first[Symbol.asyncDispose]();
		now = 1_101;

		const secondExecutor = new ControlledExecutor();
		await using second = new GameDelegationManager({
			store,
			executor: secondExecutor,
			workerId: "worker-b",
			leaseMilliseconds: 100,
			renewalMilliseconds: 25,
			now: () => now,
		});
		expect(await second.resumePending()).toBe(1);
		await viWait(() => secondExecutor.starts.length === 1);
		expect(await firstExecutor.starts[0]?.authority.isAuthoritative()).toBe(false);
		secondExecutor.starts[0]?.handle.complete({ status: "completed", result: { recovered: true } });
		await viWait(async () => (await second.read(session(), "delegation-a"))?.status === "completed");
		expect((await second.read(session(), "delegation-a"))?.attempt).toBe(2);
	});
});

async function viWait(predicate: () => boolean | Promise<boolean>, timeoutMilliseconds = 2_000): Promise<void> {
	const deadline = Date.now() + timeoutMilliseconds;
	while (!(await predicate())) {
		if (Date.now() >= deadline) throw new Error("Timed out waiting for condition.");
		await new Promise((resolve) => setTimeout(resolve, 5));
	}
}
