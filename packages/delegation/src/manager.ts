import { randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";
import type { GameControlResult, GameSessionKey, JsonValue } from "@opengameagent/protocol";
import type {
	GameDelegationClaim,
	GameDelegationLease,
	GameDelegationRecord,
	GameDelegationRequest,
	GameDelegationStore,
} from "./delegation.js";
import type { GameDelegationExecutor, GameDelegationHandle } from "./runtime-executor.js";

export interface GameDelegationManagerOptions {
	store: GameDelegationStore;
	executor: GameDelegationExecutor;
	workerId?: string;
	maximumConcurrent?: number;
	leaseMilliseconds?: number;
	renewalMilliseconds?: number;
	shutdownTimeoutMilliseconds?: number;
	now?: () => number;
	onChanged?: (record: GameDelegationRecord) => void;
}

interface ActiveDelegation {
	handle: GameDelegationHandle;
	lease: GameDelegationLease;
}

function sessionKey(session: GameSessionKey): string {
	return JSON.stringify([
		session.worldId,
		session.saveId,
		session.timelineId,
		session.generation,
		session.ownerId,
		session.sessionId,
		session.actorId,
	]);
}

function delegationKey(session: GameSessionKey, id: string): string {
	return `${sessionKey(session)}\n${id}`;
}

export class GameDelegationManager implements AsyncDisposable {
	private readonly workerId: string;
	private readonly maximumConcurrent: number;
	private readonly leaseMilliseconds: number;
	private readonly renewalMilliseconds: number;
	private readonly shutdownTimeoutMilliseconds: number;
	private readonly now: () => number;
	private readonly lifetime = new AbortController();
	private readonly running = new Map<string, Promise<GameDelegationRecord>>();
	private readonly active = new Map<string, ActiveDelegation>();
	private readonly waiters: Array<() => void> = [];
	private permits: number;
	private disposed = false;

	constructor(private readonly options: GameDelegationManagerOptions) {
		if (!options.store || !options.executor) throw new TypeError("Delegation manager requires a store and executor.");
		this.workerId = options.workerId ?? randomUUID();
		this.maximumConcurrent = options.maximumConcurrent ?? 4;
		this.leaseMilliseconds = options.leaseMilliseconds ?? 60_000;
		this.renewalMilliseconds = options.renewalMilliseconds ?? Math.floor(this.leaseMilliseconds / 3);
		this.shutdownTimeoutMilliseconds = options.shutdownTimeoutMilliseconds ?? 10_000;
		this.now = options.now ?? Date.now;
		for (const [name, value, minimum, maximum] of [
			["maximumConcurrent", this.maximumConcurrent, 1, 128],
			["leaseMilliseconds", this.leaseMilliseconds, 100, 86_400_000],
			["renewalMilliseconds", this.renewalMilliseconds, 25, this.leaseMilliseconds - 1],
			["shutdownTimeoutMilliseconds", this.shutdownTimeoutMilliseconds, 100, 300_000],
		] as const) {
			if (!Number.isInteger(value) || value < minimum || value > maximum) throw new RangeError(`${name} is invalid.`);
		}
		this.permits = this.maximumConcurrent;
	}

	async submit(
		request: GameDelegationRequest,
		options: { background?: boolean } = {},
		signal?: AbortSignal,
	): Promise<GameDelegationRecord> {
		this.ensureActive();
		signal?.throwIfAborted();
		const created = await this.options.store.create(request, signal);
		this.changed(created);
		const scheduled = this.schedule(created);
		if (options.background === true) return created;
		return await this.waitFor(scheduled, signal);
	}

	async resumePending(maximum = 128, signal?: AbortSignal): Promise<number> {
		this.ensureActive();
		const records = await this.options.store.listRecoverable(this.now(), maximum, signal);
		for (const record of records) this.schedule(record);
		return records.length;
	}

	async read(
		session: GameSessionKey,
		delegationId: string,
		signal?: AbortSignal,
	): Promise<GameDelegationRecord | undefined> {
		return await this.options.store.read(session, delegationId, signal);
	}

	async list(
		session: GameSessionKey,
		maximum = 128,
		rootDelegationId?: string,
		signal?: AbortSignal,
	): Promise<readonly GameDelegationRecord[]> {
		return await this.options.store.list(session, maximum, rootDelegationId, signal);
	}

	async cancel(
		session: GameSessionKey,
		delegationId: string,
		reason: string,
		signal?: AbortSignal,
	): Promise<GameDelegationRecord> {
		const record = await this.options.store.cancel(session, delegationId, reason, signal);
		this.changed(record);
		this.active.get(delegationKey(session, delegationId))?.handle.abort();
		return record;
	}

	async steer(
		session: GameSessionKey,
		delegationId: string,
		message: JsonValue,
		signal?: AbortSignal,
	): Promise<GameControlResult> {
		const active = this.active.get(delegationKey(session, delegationId));
		if (!active) return { accepted: false, reason: "not-active" };
		return await active.handle.steer(message, signal);
	}

	async [Symbol.asyncDispose](): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		this.lifetime.abort();
		for (const active of this.active.values()) active.handle.abort();
		const draining = Promise.allSettled(this.running.values());
		const timeoutCancellation = new AbortController();
		try {
			await Promise.race([
				draining,
				delay(this.shutdownTimeoutMilliseconds, undefined, { signal: timeoutCancellation.signal }).catch(
					() => undefined,
				),
			]);
		} finally {
			timeoutCancellation.abort();
		}
	}

	private schedule(record: GameDelegationRecord): Promise<GameDelegationRecord> {
		const key = delegationKey(record.request.session, record.request.id);
		const current = this.running.get(key);
		if (current) return current;
		const task = this.execute(record).finally(() => this.running.delete(key));
		this.running.set(key, task);
		return task;
	}

	private async execute(record: GameDelegationRecord): Promise<GameDelegationRecord> {
		const release = await this.acquire(this.lifetime.signal);
		try {
			const claim = await this.options.store.claim(
				record.request.session,
				record.request.id,
				this.workerId,
				this.now(),
				this.leaseMilliseconds,
				this.lifetime.signal,
			);
			if (claim.kind !== "leased") return claim.record;
			return await this.executeClaim(claim);
		} finally {
			release();
		}
	}

	private async executeClaim(claim: Extract<GameDelegationClaim, { kind: "leased" }>): Promise<GameDelegationRecord> {
		const request = claim.record.request;
		const key = delegationKey(request.session, request.id);
		let lease = claim.lease;
		const renewalCancellation = new AbortController();
		const renewalSignal = AbortSignal.any([renewalCancellation.signal, this.lifetime.signal]);
		const authority = {
			isAuthoritative: async (signal?: AbortSignal) =>
				await this.options.store.isLeaseAuthoritative(request.session, request.id, lease, this.now(), signal),
		};
		const handle = this.options.executor.start(request, authority, this.lifetime.signal);
		this.active.set(key, { handle, lease });
		this.changed(claim.record);
		const renewal = (async () => {
			while (!renewalSignal.aborted) {
				await delay(this.renewalMilliseconds, undefined, { signal: renewalSignal });
				lease = await this.options.store.renew(
					request.session,
					request.id,
					lease,
					this.now(),
					this.leaseMilliseconds,
					renewalSignal,
				);
				const current = this.active.get(key);
				if (current) current.lease = lease;
			}
		})().catch((error: unknown) => {
			if (!renewalCancellation.signal.aborted && !this.lifetime.signal.aborted) handle.abort();
			return error;
		});
		try {
			const outcome = await handle.completion;
			renewalCancellation.abort();
			await renewal;
			if (this.lifetime.signal.aborted) {
				return (await this.options.store.read(request.session, request.id)) ?? claim.record;
			}
			try {
				const settled = await this.options.store.settle(request.session, request.id, lease, outcome, this.now());
				this.changed(settled);
				return settled;
			} catch (error) {
				const current = await this.options.store.read(request.session, request.id);
				if (current && current.status !== "pending" && current.status !== "running") return current;
				throw error;
			}
		} finally {
			renewalCancellation.abort();
			this.active.delete(key);
			await handle[Symbol.asyncDispose]();
		}
	}

	private async acquire(signal: AbortSignal): Promise<() => void> {
		signal.throwIfAborted();
		if (this.permits > 0) {
			this.permits -= 1;
			return () => this.release();
		}
		return await new Promise<() => void>((resolve, reject) => {
			const ready = () => {
				signal.removeEventListener("abort", abort);
				this.permits -= 1;
				resolve(() => this.release());
			};
			const abort = () => {
				const index = this.waiters.indexOf(ready);
				if (index >= 0) this.waiters.splice(index, 1);
				reject(signal.reason);
			};
			this.waiters.push(ready);
			signal.addEventListener("abort", abort, { once: true });
		});
	}

	private release(): void {
		this.permits += 1;
		const next = this.waiters.shift();
		if (next) next();
	}

	private async waitFor(task: Promise<GameDelegationRecord>, signal?: AbortSignal): Promise<GameDelegationRecord> {
		if (!signal) return await task;
		signal.throwIfAborted();
		let rejectAborted: ((reason?: unknown) => void) | undefined;
		const abort = () => rejectAborted?.(signal.reason);
		const aborted = new Promise<never>((_resolve, reject) => {
			rejectAborted = reject;
			signal.addEventListener("abort", abort, { once: true });
		});
		try {
			return await Promise.race([task, aborted]);
		} finally {
			signal.removeEventListener("abort", abort);
		}
	}

	private changed(record: GameDelegationRecord): void {
		try {
			this.options.onChanged?.(structuredClone(record));
		} catch {
			// Observation cannot change delegation behavior.
		}
	}

	private ensureActive(): void {
		if (this.disposed) throw new Error("Delegation manager is disposed.");
	}
}
