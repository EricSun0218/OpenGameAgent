import { Buffer } from "node:buffer";
import { assertOpaqueIdentifier, type RunCoordinate, type StreamKey } from "@opengameagent/protocol";
import { Mutex } from "async-mutex";
import { ActionGoalProjectionCapacityError, ActionIntentConflictError, ActionNotFoundError } from "./errors.js";
import {
	assertActionGoalProjectionRecordFits,
	captureActionGoalProjectionLimits,
	captureActionGoalProjectionListRequest,
} from "./goal-projection.js";
import { isActionRunAuthorityOpen, type SynchronousActionRunAuthority } from "./run-authority.js";
import {
	claimActionRecord,
	expireActionRecord,
	receiptActionRecord,
	reclaimActionRecord,
	reconcileActionRecord,
	retirePendingActionRecord,
	settleActionGoalProjectionRecord,
} from "./transitions.js";
import type {
	ActionClaimRequest,
	ActionDeliverySelection,
	ActionGoalProjectionLimits,
	ActionGoalProjectionListRequest,
	ActionGoalProjectionPage,
	ActionGoalProjectionSettlementInput,
	ActionJournal,
	ActionJournalOptions,
	ActionReceiptInput,
	ActionReconcileInput,
	ActionRecord,
	ActionRecoveryDelivery,
	ActionTerminalPurgeRequest,
	PreparedActionIntent,
} from "./types.js";
import {
	assertActionRecord,
	captureActionTerminalPurgeRequest,
	validateActionClaimRequest,
	validateStream,
	validateTimestamp,
} from "./validation.js";

export interface InMemoryActionJournalOptions extends ActionJournalOptions {
	readonly runAuthority?: SynchronousActionRunAuthority;
}

export class InMemoryActionJournal implements ActionJournal {
	readonly goalProjectionLimits?: Readonly<ActionGoalProjectionLimits>;
	private readonly serial = new Mutex();
	private readonly records = new Map<string, ActionRecord>();
	private readonly conflicts = new Map<string, string>();
	private readonly sequences = new Map<string, bigint>();
	private nextSequence = 0n;
	private readonly assertRunAuthorityOpen: (coordinate: RunCoordinate) => void;

	constructor(options: InMemoryActionJournalOptions = {}) {
		const authority = options.runAuthority;
		const check = authority?.assertRunAuthorityOpenSynchronously.bind(authority);
		this.assertRunAuthorityOpen = (coordinate) => {
			const result: unknown = check?.(coordinate);
			if (result !== undefined) throw new TypeError("Action run authority checks must complete synchronously.");
		};
		const goalProjectionLimits = captureActionGoalProjectionLimits(options.goalProjection);
		if (goalProjectionLimits !== undefined) this.goalProjectionLimits = goalProjectionLimits;
	}

	ensure(intent: PreparedActionIntent, now: string): Promise<ActionRecord> {
		return this.serial.runExclusive(() => {
			validateTimestamp(now);
			const key = recordKey(intent, intent.operationId);
			const existing = this.records.get(key);
			if (existing) {
				if (existing.intentFingerprint !== intent.intentFingerprint) throw new ActionIntentConflictError();
				return clone(existing);
			}
			if (intent.goalProjection !== undefined) {
				const limits = this.goalProjectionLimits;
				if (limits === undefined) throw new TypeError("Goal projection limits are required for Action admission.");
				let pending = 0;
				for (const candidate of this.records.values()) {
					if (
						candidate.sessionId === intent.sessionId &&
						candidate.actorId === intent.actorId &&
						candidate.goalProjection !== undefined &&
						candidate.goalProjectionSettlement === undefined
					) {
						pending += 1;
					}
				}
				if (pending >= limits.maxPendingPerStream) throw new ActionGoalProjectionCapacityError();
			}
			const record: ActionRecord = {
				...clone(intent),
				status: "pending",
				attempt: "0",
				createdAt: now,
				updatedAt: now,
			};
			assertActionRecord(record, this.goalProjectionLimits);
			assertActionGoalProjectionRecordFits(record, this.goalProjectionLimits);
			this.assertRunAuthorityOpen(record.coordinate);
			this.records.set(key, record);
			this.nextSequence += 1n;
			this.sequences.set(key, this.nextSequence);
			return clone(record);
		});
	}

	get(stream: StreamKey, operationId: string): Promise<ActionRecord | undefined> {
		return this.serial.runExclusive(() => {
			validateStream(stream);
			assertOpaqueIdentifier(operationId);
			const record = this.records.get(recordKey(stream, operationId));
			return record ? clone(record) : undefined;
		});
	}

	recoverDelivery(stream: StreamKey): Promise<ActionRecoveryDelivery | undefined> {
		return this.serial.runExclusive(() => {
			validateStream(stream);
			const unsettled = this.oldestUnsettled(stream);
			return unsettled ? { kind: "reconcile", action: clone(unsettled) } : undefined;
		});
	}

	nextDelivery(request: ActionClaimRequest): Promise<ActionDeliverySelection> {
		return this.serial.runExclusive(() => {
			validateActionClaimRequest(request);
			const retired = this.retireClosedPending(request);
			const { replayable, changed: expired } = this.expireDueClaims(request);
			const changed = retired || expired;
			const unsettled = this.oldestUnsettled(request, replayable);
			if (unsettled) return { changed, delivery: { kind: "reconcile", action: clone(unsettled) } };
			const claimed = this.claimPendingRecord(request, replayable);
			if (claimed) return { changed: true, delivery: { kind: "dispatch", action: clone(claimed) } };
			const waitingReplay = this.oldestUnsettled(request);
			return waitingReplay ? { changed, delivery: { kind: "reconcile", action: clone(waitingReplay) } } : { changed };
		});
	}

	recordReceipt(input: ActionReceiptInput): Promise<ActionRecord> {
		return this.serial.runExclusive(() => {
			const record = this.require(input, input.operationId);
			const transition = receiptActionRecord(record, input);
			if (transition.record !== record) {
				assertActionGoalProjectionRecordFits(transition.record, this.goalProjectionLimits);
				if (transition.releaseConflicts) this.release(record);
				this.records.set(recordKey(transition.record, transition.record.operationId), transition.record);
			}
			return clone(transition.record);
		});
	}

	reconcile(input: ActionReconcileInput): Promise<ActionRecord> {
		return this.serial.runExclusive(() => {
			const record = this.require(input, input.operationId);
			const transition = reconcileActionRecord(record, input);
			if (transition.record.status === "pending" && !this.isRunOpen(record)) {
				transition.record = retirePendingActionRecord(transition.record, input.reconciledAt).record;
			}
			if (transition.record !== record) {
				assertActionGoalProjectionRecordFits(transition.record, this.goalProjectionLimits);
				if (transition.releaseConflicts) this.release(record);
				this.records.set(recordKey(transition.record, transition.record.operationId), transition.record);
			}
			return clone(transition.record);
		});
	}

	listGoalProjections(request: ActionGoalProjectionListRequest): Promise<ActionGoalProjectionPage> {
		return this.serial.runExclusive(() => {
			const limits = this.requireGoalProjectionLimits();
			const captured = captureActionGoalProjectionListRequest(request, limits);
			const after = BigInt(captured.afterSequence ?? "0");
			const candidates = [];
			for (const [key, record] of this.records) {
				const sequence = this.sequences.get(key);
				if (
					sequence === undefined ||
					sequence <= after ||
					record.sessionId !== captured.sessionId ||
					record.actorId !== captured.actorId ||
					record.goalProjection === undefined ||
					record.goalProjectionSettlement !== undefined
				) {
					continue;
				}
				if (Buffer.byteLength(JSON.stringify(record), "utf8") > limits.maxRecordBytes) {
					throw new RangeError("A Goal projection Action exceeds its configured hydration byte budget.");
				}
				candidates.push({ sequence: sequence.toString(), action: clone(record) });
				if (candidates.length > captured.limit) break;
			}
			const hasMore = candidates.length > captured.limit;
			const page = candidates.slice(0, captured.limit);
			const next = hasMore ? page.at(-1)?.sequence : undefined;
			return { candidates: page, ...(next === undefined ? {} : { nextAfterSequence: next }) };
		});
	}

	settleGoalProjection(input: ActionGoalProjectionSettlementInput): Promise<ActionRecord> {
		return this.serial.runExclusive(() => {
			const limits = this.requireGoalProjectionLimits();
			const record = this.require(input, input.operationId);
			const settled = settleActionGoalProjectionRecord(record, input);
			if (settled === record) return clone(record);
			assertActionRecord(settled, limits);
			assertActionGoalProjectionRecordFits(settled, limits);
			this.records.set(recordKey(settled, settled.operationId), settled);
			return clone(settled);
		});
	}

	purgeTerminal(request: ActionTerminalPurgeRequest): Promise<readonly string[]> {
		const captured = captureActionTerminalPurgeRequest(request);
		return this.serial.runExclusive(() => {
			const retained = new Set(captured.retainOperationIds);
			const eligible = [...this.records.entries()]
				.flatMap(([key, record]) => {
					const sequence = this.sequences.get(key);
					return sequence === undefined ? [] : [{ key, record, sequence }];
				})
				.filter(
					({ record }) =>
						record.sessionId === captured.sessionId &&
						record.actorId === captured.actorId &&
						!retained.has(record.operationId) &&
						(record.status === "succeeded" || record.status === "failed") &&
						record.updatedAt < captured.beforeUpdatedAt &&
						(record.goalProjection === undefined || record.goalProjectionSettlement !== undefined),
				)
				.sort((left, right) =>
					left.record.updatedAt === right.record.updatedAt
						? left.sequence < right.sequence
							? -1
							: left.sequence > right.sequence
								? 1
								: 0
						: left.record.updatedAt < right.record.updatedAt
							? -1
							: 1,
				)
				.slice(0, captured.limit);
			for (const { key, record } of eligible) {
				this.release(record);
				this.records.delete(key);
				this.sequences.delete(key);
			}
			return eligible.map(({ record }) => record.operationId);
		});
	}

	private requireGoalProjectionLimits() {
		const limits = this.goalProjectionLimits;
		if (limits === undefined) throw new TypeError("Goal projection limits are not configured.");
		return limits;
	}

	private isRunOpen(record: ActionRecord): boolean {
		return isActionRunAuthorityOpen(() => this.assertRunAuthorityOpen(record.coordinate));
	}

	private retireClosedPending(request: ActionClaimRequest): boolean {
		let retired = 0;
		for (const record of this.records.values()) {
			if (
				record.sessionId !== request.sessionId ||
				record.actorId !== request.actorId ||
				record.status !== "pending" ||
				this.isRunOpen(record)
			)
				continue;
			const transition = retirePendingActionRecord(record, request.claimedAt);
			assertActionRecord(transition.record, this.goalProjectionLimits);
			assertActionGoalProjectionRecordFits(transition.record, this.goalProjectionLimits);
			this.release(record);
			this.records.set(recordKey(record, record.operationId), transition.record);
			if (++retired >= request.candidateLimit) break;
		}
		return retired !== 0;
	}

	private claimPendingRecord(request: ActionClaimRequest, replayable: ReadonlySet<string>): ActionRecord | undefined {
		let scanned = 0;
		for (const record of this.records.values()) {
			if (
				record.sessionId !== request.sessionId ||
				record.actorId !== request.actorId ||
				(request.runId !== undefined && record.coordinate.runId !== request.runId) ||
				(record.status !== "pending" && !replayable.has(record.operationId))
			) {
				continue;
			}
			scanned += 1;
			if (scanned > request.candidateLimit) return undefined;
			if (!this.isRunOpen(record)) continue;
			if (!this.canAcquire(record)) continue;

			const replay = replayable.has(record.operationId);
			const transition = replay ? reclaimActionRecord(record, request) : claimActionRecord(record, request);
			assertActionRecord(transition.record, this.goalProjectionLimits);
			assertActionGoalProjectionRecordFits(transition.record, this.goalProjectionLimits);
			if (replay) this.release(record);
			this.acquire(transition.record);
			this.records.set(recordKey(transition.record, transition.record.operationId), transition.record);
			return transition.record;
		}
		return undefined;
	}

	private oldestUnsettled(
		stream: StreamKey & { runId?: string },
		replayable?: ReadonlySet<string>,
	): ActionRecord | undefined {
		// Map insertion order is this journal's action admission sequence; updates do not move an existing key.
		for (const record of this.records.values()) {
			if (
				record.sessionId === stream.sessionId &&
				record.actorId === stream.actorId &&
				(stream.runId === undefined || record.coordinate.runId === stream.runId) &&
				!replayable?.has(record.operationId) &&
				(record.status === "claimed" || record.status === "uncertain")
			) {
				return record;
			}
		}
		return undefined;
	}

	private expireDueClaims(request: ActionClaimRequest): {
		readonly replayable: ReadonlySet<string>;
		readonly changed: boolean;
	} {
		const expired: { record: ActionRecord; expiresAt: string }[] = [];
		for (const record of this.records.values()) {
			if (
				record.sessionId !== request.sessionId ||
				record.actorId !== request.actorId ||
				(request.runId !== undefined && record.coordinate.runId !== request.runId) ||
				record.status !== "claimed" ||
				!record.claim ||
				record.claim.expiresAt > request.claimedAt
			) {
				continue;
			}
			expired.push({ record, expiresAt: record.claim.expiresAt });
			expired.sort((left, right) => (left.expiresAt < right.expiresAt ? -1 : left.expiresAt > right.expiresAt ? 1 : 0));
			if (expired.length > request.candidateLimit) expired.pop();
		}
		const replayable = new Set<string>();
		let changed = false;
		for (const { record } of expired) {
			if (record.replayPolicy === "safe" && this.isRunOpen(record)) {
				replayable.add(record.operationId);
				continue;
			}
			const transition = expireActionRecord(record, request.claimedAt);
			assertActionGoalProjectionRecordFits(transition.record, this.goalProjectionLimits);
			if (transition.releaseConflicts) this.release(record);
			this.records.set(recordKey(transition.record, transition.record.operationId), transition.record);
			changed = true;
		}
		return { replayable, changed };
	}

	private require(stream: StreamKey, operationId: string): ActionRecord {
		validateStream(stream);
		assertOpaqueIdentifier(operationId);
		const record = this.records.get(recordKey(stream, operationId));
		if (!record) throw new ActionNotFoundError();
		return record;
	}

	private canAcquire(record: ActionRecord): boolean {
		return record.conflictKeys.every((key) => {
			const owner = this.conflicts.get(conflictKey(record.sessionId, key));
			return owner === undefined || owner === record.operationId;
		});
	}

	private acquire(record: ActionRecord): void {
		for (const key of record.conflictKeys) this.conflicts.set(conflictKey(record.sessionId, key), record.operationId);
	}

	private release(record: ActionRecord): void {
		for (const key of record.conflictKeys) {
			const keyId = conflictKey(record.sessionId, key);
			if (this.conflicts.get(keyId) === record.operationId) this.conflicts.delete(keyId);
		}
	}
}

function recordKey(stream: StreamKey, operationId: string): string {
	return JSON.stringify([stream.sessionId, stream.actorId, operationId]);
}

function conflictKey(sessionId: string, key: string): string {
	return JSON.stringify([sessionId, key]);
}

function clone<T>(value: T): T {
	return structuredClone(value);
}
