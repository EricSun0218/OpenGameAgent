import type {
	CloseRunAuthorityRequest,
	RunAdmissionAuthority,
	RunAuthorityClosure,
	RunAuthorityKey,
} from "./run-authority.js";
import type { CanonicalEvent, CanonicalEventData, RunCoordinate, StreamKey } from "./schema.js";

export type ExpectedRevision = "empty" | string;

export interface EventReadRequest {
	afterRevision?: string;
	limit: number;
}

export interface CanonicalEventPage {
	events: CanonicalEvent[];
	cursor: string;
	hasMore: boolean;
}

export interface EventStreamBounds {
	earliestRevision: string | null;
	latestRevision: string;
}

export interface TerminalEventPurgeRequest {
	readonly beforeOccurredAt: string;
	readonly limit: number;
	readonly retainRunIds: readonly string[];
	readonly retainBudget: RetentionProtectionBudget;
}

export interface TerminalEventPurgeResult extends EventStreamBounds {
	readonly purgedEventCount: number;
	readonly purgedRunIds: readonly string[];
}

interface StoredRunRecordBase {
	admissionAuthority?: RunAdmissionAuthority;
	inputId: string;
	inputFingerprint: string;
	eventCursor: string;
	initialCoordinate: RunCoordinate;
	latestCoordinate: RunCoordinate;
}

export type StoredRunRecord = StoredRunRecordBase &
	(
		| { status: "completed"; finalOutput?: string }
		| { status: "aborted" | "failed" }
		| { status: "deferred"; operationId: string }
		| { status: null }
	);

export interface CanonicalEventReplayPage extends CanonicalEventPage {
	kind: "page";
	requestedAfterRevision: string;
	requiresTranscriptReconciliation: false;
}

export interface CanonicalEventReplayGap {
	kind: "gap";
	requestedAfterRevision: string;
	earliestAvailableRevision: string | null;
	nextAfterRevision: string;
	requiresTranscriptReconciliation: true;
}

export type CanonicalEventReplay = CanonicalEventReplayPage | CanonicalEventReplayGap;

export interface CanonicalEventStore {
	/** Permanently fence the exact scope generation. Work retirement remains fenced until its holders drain. */
	closeAuthority(request: CloseRunAuthorityRequest): Promise<RunAuthorityClosure>;
	/** Host-only: commit work retirement after all matching runtime and detached host work has drained. */
	finalizeAuthorityClosure(request: CloseRunAuthorityRequest): Promise<RunAuthorityClosure>;
	readAuthorityClosure(key: RunAuthorityKey): Promise<RunAuthorityClosure | undefined>;
	/** Unknown runs are unscoped. Effect adapters must repeat the check inside their write transaction. */
	assertRunAuthorityOpen(coordinate: RunCoordinate): Promise<void>;
	read(stream: StreamKey, request: EventReadRequest): Promise<CanonicalEventPage>;
	readBounds(stream: StreamKey): Promise<EventStreamBounds>;
	readRun(stream: StreamKey, inputId: string): Promise<StoredRunRecord | undefined>;
	readRevision(stream: StreamKey): Promise<ExpectedRevision>;
	/** Queued, started and resumed admissions must check the scope fence atomically with the append. */
	append(
		stream: StreamKey,
		expectedRevision: ExpectedRevision,
		coordinate: RunCoordinate,
		data: CanonicalEventData,
	): Promise<CanonicalEvent>;
	purgeTerminalRuns(stream: StreamKey, request: TerminalEventPurgeRequest): Promise<TerminalEventPurgeResult>;
}

export function assertEventReadLimit(limit: number): void {
	if (!Number.isSafeInteger(limit) || limit <= 0 || limit >= Number.MAX_SAFE_INTEGER) {
		throw new TypeError("Event page limit must be a positive safe integer with room for lookahead.");
	}
}

export function parseEventRevision(revision: string): bigint {
	if (!/^(0|[1-9][0-9]*)$/u.test(revision)) {
		throw new TypeError("Event revision must be a non-negative canonical decimal string.");
	}
	return BigInt(revision);
}

import type { RetentionProtectionBudget } from "./retention.js";
