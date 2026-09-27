import {
	SignalConflictError,
	SignalWaitConflictError,
	SignalWaitFanoutError,
	SignalWaitNotFoundError,
	SignalWaitRevisionConflictError,
	SignalWaitTerminalError,
} from "./signal-errors.js";
import type {
	GameSignalDraft,
	GameSignalKey,
	GameSignalRecord,
	GameSignalSequenceKey,
	SignalListRequest,
	SignalPage,
	SignalPartition,
	SignalRecordResult,
	SignalStore,
	SignalWaitDraft,
	SignalWaitKey,
	SignalWaitListRequest,
	SignalWaitPage,
	SignalWaitRecord,
} from "./signal-types.js";
import {
	compareSignalTicks,
	laterSignalTimestamp,
	normalizeSignalStatusFilter,
	sameSignalGeneration,
	signalFingerprint,
	signalWaitFingerprint,
	signalWaitMatches,
} from "./signal-validation.js";

export class InMemorySignalStore implements SignalStore {
	private readonly signals = new Map<string, GameSignalRecord>();
	private readonly signalFingerprints = new Map<string, string>();
	private readonly waits = new Map<string, SignalWaitRecord>();
	private readonly waitFingerprints = new Map<string, string>();
	private nextSignalKey = 1n;
	private nextWaitKey = 1n;

	async record(draft: GameSignalDraft, maxMatches: number): Promise<SignalRecordResult> {
		const key = signalMapKey(draft);
		const existing = this.signals.get(key);
		if (existing !== undefined) {
			if (this.signalFingerprints.get(key) !== signalFingerprint(draft)) throw new SignalConflictError();
			const matchedWaitIds = this.matchedWaitIds(existing.signalKey);
			if (matchedWaitIds.length > maxMatches) throw new SignalWaitFanoutError();
			return {
				signal: clone(existing),
				matchedWaitIds,
			};
		}
		const signal: GameSignalRecord = { ...clone(draft), signalKey: this.nextSignalKey };
		const matches = [...this.waits.entries()]
			.filter(([, wait]) => wait.status === "waiting" && signalWaitMatches(wait, signal))
			.sort(([, left], [, right]) => (left.waitKey < right.waitKey ? -1 : 1));
		if (matches.length > maxMatches) throw new SignalWaitFanoutError();

		this.nextSignalKey += 1n;
		this.signals.set(key, signal);
		this.signalFingerprints.set(key, signalFingerprint(draft));
		for (const [waitKey, wait] of matches) {
			this.waits.set(waitKey, {
				...wait,
				status: "matched",
				matchedSignalKey: signal.signalKey,
				revision: (BigInt(wait.revision) + 1n).toString(),
				updatedAt: laterSignalTimestamp(wait.updatedAt, signal.recordedAt),
			});
		}
		return { signal: clone(signal), matchedWaitIds: matches.map(([, wait]) => wait.waitId) };
	}

	async getSignal(key: GameSignalKey): Promise<GameSignalRecord | undefined> {
		const signal = this.signals.get(signalMapKey(key));
		return signal === undefined ? undefined : clone(signal);
	}

	async getSignalByKey(key: GameSignalSequenceKey): Promise<GameSignalRecord | undefined> {
		const signal = [...this.signals.values()].find(
			(candidate) => candidate.signalKey === key.signalKey && sameSignalGeneration(candidate.partition, key),
		);
		return signal === undefined ? undefined : clone(signal);
	}

	async listSignals(request: SignalListRequest): Promise<SignalPage> {
		const matches = [...this.signals.values()]
			.filter(
				(signal) =>
					sameOwnerGeneration(signal.partition, request) &&
					(request.sessionId === undefined || signal.partition.sessionId === request.sessionId) &&
					(request.actorId === undefined || signal.partition.actorId === request.actorId) &&
					(request.kinds === undefined || request.kinds.includes(signal.kind)) &&
					(request.afterSignalKey === undefined || signal.signalKey > request.afterSignalKey),
			)
			.sort((left, right) => (left.signalKey < right.signalKey ? -1 : 1));
		const selected = matches.slice(0, request.limit);
		const next = matches.length > selected.length ? selected.at(-1)?.signalKey : undefined;
		return {
			records: selected.map(clone),
			...(next === undefined ? {} : { nextAfterSignalKey: next }),
		};
	}

	async registerWait(draft: SignalWaitDraft, maxMatches: number): Promise<SignalWaitRecord> {
		const key = waitMapKey(draft);
		const existing = this.waits.get(key);
		if (existing !== undefined) {
			if (this.waitFingerprints.get(key) !== signalWaitFingerprint(draft)) throw new SignalWaitConflictError();
			return clone(existing);
		}
		const matchedSignal = [...this.signals.values()]
			.filter((signal) => signalWaitMatches(draft, signal))
			.sort((left, right) => (left.signalKey < right.signalKey ? -1 : 1))[0];
		if (matchedSignal !== undefined && this.matchedWaitIds(matchedSignal.signalKey).length >= maxMatches) {
			throw new SignalWaitFanoutError();
		}
		const wait: SignalWaitRecord = {
			...clone(draft),
			waitKey: this.nextWaitKey,
			status: matchedSignal === undefined ? "waiting" : "matched",
			revision: "1",
			...(matchedSignal === undefined ? {} : { matchedSignalKey: matchedSignal.signalKey }),
			createdAt: draft.recordedAt,
			updatedAt:
				matchedSignal === undefined
					? draft.recordedAt
					: laterSignalTimestamp(draft.recordedAt, matchedSignal.recordedAt),
		};
		this.nextWaitKey += 1n;
		this.waits.set(key, wait);
		this.waitFingerprints.set(key, signalWaitFingerprint(draft));
		return clone(wait);
	}

	async getWait(key: SignalWaitKey): Promise<SignalWaitRecord | undefined> {
		const wait = this.waits.get(waitMapKey(key));
		return wait === undefined ? undefined : clone(wait);
	}

	async listWaits(request: SignalWaitListRequest): Promise<SignalWaitPage> {
		const statuses = normalizeSignalStatusFilter(request.statuses);
		const matches = [...this.waits.values()]
			.filter(
				(wait) =>
					sameOwnerGeneration(wait.partition, request) &&
					(request.sessionId === undefined || wait.partition.sessionId === request.sessionId) &&
					(request.actorId === undefined || wait.partition.actorId === request.actorId) &&
					(statuses === undefined || statuses.includes(wait.status)) &&
					(request.afterWaitKey === undefined || wait.waitKey > request.afterWaitKey),
			)
			.sort((left, right) => (left.waitKey < right.waitKey ? -1 : 1));
		const selected = matches.slice(0, request.limit);
		const next = matches.length > selected.length ? selected.at(-1)?.waitKey : undefined;
		return {
			records: selected.map(clone),
			...(next === undefined ? {} : { nextAfterWaitKey: next }),
		};
	}

	async cancelWait(key: SignalWaitKey, expectedRevision: string, recordedAt: string): Promise<SignalWaitRecord> {
		const storageKey = waitMapKey(key);
		const current = this.waits.get(storageKey);
		if (current === undefined) throw new SignalWaitNotFoundError();
		if (current.revision !== expectedRevision) throw new SignalWaitRevisionConflictError();
		if (current.status !== "waiting") throw new SignalWaitTerminalError();
		const updated: SignalWaitRecord = {
			...current,
			status: "cancelled",
			revision: (BigInt(current.revision) + 1n).toString(),
			updatedAt: laterSignalTimestamp(current.updatedAt, recordedAt),
		};
		this.waits.set(storageKey, updated);
		return clone(updated);
	}

	async expireWaits(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		throughTickInclusive: string,
		recordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const selected = [...this.waits.entries()]
			.filter(
				([, wait]) =>
					wait.status === "waiting" &&
					sameOwnerGeneration(wait.partition, partition) &&
					wait.selector.throughTickInclusive !== undefined &&
					compareSignalTicks(wait.selector.throughTickInclusive, throughTickInclusive) <= 0,
			)
			.sort(([, left], [, right]) => (left.waitKey < right.waitKey ? -1 : 1))
			.slice(0, limit);
		for (const [key, wait] of selected) {
			this.waits.set(key, {
				...wait,
				status: "expired",
				revision: (BigInt(wait.revision) + 1n).toString(),
				updatedAt: laterSignalTimestamp(wait.updatedAt, recordedAt),
			});
		}
		return selected.map(([, wait]) => wait.waitKey);
	}

	async purgeTerminalWaits(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const selected = [...this.waits.entries()]
			.filter(
				([, wait]) =>
					wait.status !== "waiting" &&
					sameOwnerGeneration(wait.partition, partition) &&
					wait.updatedAt < beforeRecordedAt,
			)
			.sort(([, left], [, right]) => (left.waitKey < right.waitKey ? -1 : 1))
			.slice(0, limit);
		for (const [key] of selected) {
			this.waits.delete(key);
			this.waitFingerprints.delete(key);
		}
		return selected.map(([, wait]) => wait.waitKey);
	}

	async purgeSignals(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const referenced = new Set(
			[...this.waits.values()].flatMap((wait) =>
				wait.matchedSignalKey === undefined ? [] : [wait.matchedSignalKey.toString()],
			),
		);
		const selected = [...this.signals.entries()]
			.filter(
				([, signal]) =>
					sameOwnerGeneration(signal.partition, partition) &&
					signal.recordedAt < beforeRecordedAt &&
					!referenced.has(signal.signalKey.toString()),
			)
			.sort(([, left], [, right]) => (left.signalKey < right.signalKey ? -1 : 1))
			.slice(0, limit);
		for (const [key] of selected) {
			this.signals.delete(key);
			this.signalFingerprints.delete(key);
		}
		return selected.map(([, signal]) => signal.signalKey);
	}

	private matchedWaitIds(signalKey: bigint): readonly string[] {
		return [...this.waits.values()]
			.filter((wait) => wait.matchedSignalKey === signalKey)
			.sort((left, right) => (left.waitKey < right.waitKey ? -1 : 1))
			.map((wait) => wait.waitId);
	}
}

function signalMapKey(key: GameSignalKey | GameSignalDraft): string {
	const partition = "partition" in key ? key.partition : key;
	return `${partition.ownerId}\0${partition.timelineId}\0${partition.generation}\0${key.signalId}`;
}

function waitMapKey(key: SignalWaitKey | SignalWaitDraft): string {
	const partition = "partition" in key ? key.partition : key;
	return `${partition.ownerId}\0${partition.timelineId}\0${partition.generation}\0${key.waitId}`;
}

function sameOwnerGeneration(
	left: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
	right: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
): boolean {
	return sameSignalGeneration(left, right);
}

function clone<T>(value: T): T {
	return structuredClone(value);
}
