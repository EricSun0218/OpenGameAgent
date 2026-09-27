import { randomUUID } from "node:crypto";
import { assertOpaqueIdentifier, canonicalizeJsonValue, type StreamKey } from "@opengameagent/protocol";
import { ChangeNotifier } from "./change-notifier.js";
import { ActionNotFoundError, ActionStateConflictError } from "./errors.js";
import { prepareActionIntent } from "./identity.js";
import type {
	ActionClaimRequest,
	ActionDelivery,
	ActionGoalProjectionListRequest,
	ActionGoalProjectionPage,
	ActionGoalProjectionSettlementInput,
	ActionIntentDraft,
	ActionJournal,
	ActionReceiptInput,
	ActionReconcileInput,
	ActionRecord,
	ActionRecoveryDelivery,
	ActionTerminalPurgeRequest,
	ActionWaitOptions,
	PreparedActionIntent,
} from "./types.js";
import { assertActionRecord, assertPositiveSafeInteger, validateActionWaitOptions } from "./validation.js";

export interface ActionCoordinatorOptions {
	newClaimToken?: () => string;
	now?: () => Date;
}

export interface ActionDeliveryOptions {
	sessionId: string;
	actorId: string;
	runId?: string;
	claimantId: string;
	leaseDurationMs: number;
	candidateLimit: number;
}

export class ActionCoordinator {
	private readonly newClaimToken: () => string;
	private readonly now: () => Date;
	private readonly changes = new ChangeNotifier();

	constructor(
		private readonly journal: ActionJournal,
		options: ActionCoordinatorOptions = {},
	) {
		this.newClaimToken = options.newClaimToken ?? randomUUID;
		this.now = options.now ?? (() => new Date());
	}

	prepare(draft: ActionIntentDraft): PreparedActionIntent {
		return prepareActionIntent(draft, this.journal.goalProjectionLimits);
	}

	submit(draft: ActionIntentDraft): Promise<ActionRecord> {
		return this.submitPrepared(this.prepare(draft));
	}

	async submitPrepared(intent: PreparedActionIntent): Promise<ActionRecord> {
		const record = await this.journal.ensure(intent, this.now().toISOString());
		this.changes.notify();
		return record;
	}

	get(stream: StreamKey, operationId: string): Promise<ActionRecord | undefined> {
		return this.journal.get(stream, operationId);
	}

	recoverDelivery(stream: StreamKey): Promise<ActionRecoveryDelivery | undefined> {
		return this.journal.recoverDelivery(stream);
	}

	async waitForSettlement(action: ActionRecord, options: ActionWaitOptions): Promise<ActionRecord> {
		assertActionRecord(action, this.journal.goalProjectionLimits);
		validateActionWaitOptions(options);
		let revision = this.changes.snapshot();
		let current = await this.journal.get(action, action.operationId);
		options.signal?.throwIfAborted();
		if (current === undefined) throw new ActionNotFoundError();
		assertActionRecord(current, this.journal.goalProjectionLimits);
		assertSameActionIntent(action, current);
		while (current.status === "pending" || current.status === "claimed") {
			await this.changes.wait(revision, options.pollIntervalMs, options.signal);
			revision = this.changes.snapshot();
			const stored = await this.journal.get(current, current.operationId);
			options.signal?.throwIfAborted();
			if (stored === undefined) throw new ActionNotFoundError();
			assertActionRecord(stored, this.journal.goalProjectionLimits);
			assertSameActionIntent(action, stored);
			current = stored;
		}
		return current;
	}

	async nextDelivery(options: ActionDeliveryOptions): Promise<ActionDelivery | undefined> {
		const selection = await this.journal.nextDelivery(this.prepareClaimRequest(options));
		if (selection.changed) this.changes.notify();
		return selection.delivery;
	}

	changeRevision(): bigint {
		return this.changes.snapshot();
	}

	waitForChange(afterRevision: bigint, timeoutMs: number, signal?: AbortSignal): Promise<boolean> {
		return this.changes.wait(afterRevision, timeoutMs, signal);
	}

	private prepareClaimRequest(options: ActionDeliveryOptions): ActionClaimRequest {
		assertOpaqueIdentifier(options.sessionId);
		assertOpaqueIdentifier(options.actorId);
		if (options.runId !== undefined) assertOpaqueIdentifier(options.runId);
		assertOpaqueIdentifier(options.claimantId);
		assertPositiveSafeInteger(options.leaseDurationMs, "leaseDurationMs");
		assertPositiveSafeInteger(options.candidateLimit, "candidateLimit");
		const claimedAt = this.now();
		const expiresAt = new Date(claimedAt.getTime() + options.leaseDurationMs);
		if (!Number.isFinite(expiresAt.getTime()))
			throw new RangeError("The action claim expiration is outside Date range.");
		const claimToken = this.newClaimToken();
		assertOpaqueIdentifier(claimToken);
		return {
			sessionId: options.sessionId,
			actorId: options.actorId,
			...(options.runId === undefined ? {} : { runId: options.runId }),
			claimantId: options.claimantId,
			claimToken,
			claimedAt: claimedAt.toISOString(),
			expiresAt: expiresAt.toISOString(),
			candidateLimit: options.candidateLimit,
		};
	}

	async recordReceipt(input: Omit<ActionReceiptInput, "receivedAt">): Promise<ActionRecord> {
		const record = await this.journal.recordReceipt({ ...input, receivedAt: this.now().toISOString() });
		this.changes.notify();
		return record;
	}

	async reconcile(input: Omit<ActionReconcileInput, "reconciledAt">): Promise<ActionRecord> {
		const record = await this.journal.reconcile({ ...input, reconciledAt: this.now().toISOString() });
		this.changes.notify();
		return record;
	}

	listGoalProjections(request: ActionGoalProjectionListRequest): Promise<ActionGoalProjectionPage> {
		return this.journal.listGoalProjections(request);
	}

	async settleGoalProjection(input: Omit<ActionGoalProjectionSettlementInput, "settledAt">): Promise<ActionRecord> {
		const record = await this.journal.settleGoalProjection({ ...input, settledAt: this.now().toISOString() });
		this.changes.notify();
		return record;
	}

	purgeTerminal(request: ActionTerminalPurgeRequest): Promise<readonly string[]> {
		return this.journal.purgeTerminal(request);
	}
}

function assertSameActionIntent(expected: ActionRecord, actual: ActionRecord): void {
	const identity = (record: ActionRecord) => ({
		sessionId: record.sessionId,
		actorId: record.actorId,
		operationId: record.operationId,
		intentFingerprint: record.intentFingerprint,
		payloadFingerprint: record.payloadFingerprint,
		coordinate: record.coordinate,
		actionName: record.actionName,
		operationKey: record.operationKey,
		payload: record.payload,
		expectedAuthority: record.expectedAuthority,
		conflictKeys: record.conflictKeys,
		replayPolicy: record.replayPolicy,
		goalProjection: record.goalProjection ?? null,
	});
	if (canonicalizeJsonValue(identity(expected)) !== canonicalizeJsonValue(identity(actual))) {
		throw new ActionStateConflictError();
	}
}
