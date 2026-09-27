import { randomUUID } from "node:crypto";
import {
	assertCanonicalEventData,
	assertCanonicalTimestamp,
	assertEventReadLimit,
	assertEventStreamCoordinate,
	assertOpaqueIdentifier,
	type CanonicalEvent,
	type CanonicalEventData,
	type CanonicalEventPage,
	type CanonicalEventStore,
	type CloseRunAuthorityRequest,
	canonicalAssistantText,
	canonicalizeJsonValue,
	captureCloseRunAuthorityRequest,
	captureRetainedIdentifiers,
	captureRunAdmissionAuthority,
	captureRunAuthorityKey,
	type EventReadRequest,
	type EventStreamBounds,
	type ExpectedRevision,
	eventAudience,
	PROTOCOL_VERSION,
	type RunAdmissionAuthority,
	RunAuthorityClosedError,
	type RunAuthorityClosure,
	RunAuthorityConflictError,
	type RunAuthorityGoalRetirement,
	type RunAuthorityKey,
	type RunCoordinate,
	type StoredRunRecord,
	type StreamKey,
	type TerminalEventPurgeRequest,
	type TerminalEventPurgeResult,
} from "@opengameagent/protocol";

export class WrongExpectedRevisionError extends Error {
	constructor(
		public readonly expected: ExpectedRevision,
		public readonly actual: ExpectedRevision,
	) {
		super("Canonical event stream revision did not match.");
		this.name = "WrongExpectedRevisionError";
	}
}

export interface InMemoryEventStoreOptions {
	/** Must commit synchronously and leave Goal state unchanged if it throws. */
	retireGoalAuthority?: (
		request: RunAuthorityGoalRetirement,
	) => RunAuthorityGoalRetirement & { cancelledGoalCount: number };
	newEventId?: () => string;
	now?: () => string;
}

export class InMemoryEventStore implements CanonicalEventStore {
	private readonly streams = new Map<string, CanonicalEvent[]>();
	private readonly revisions = new Map<string, bigint>();
	private readonly newEventId: () => string;
	private readonly now: () => string;
	private readonly retireGoalAuthority: InMemoryEventStoreOptions["retireGoalAuthority"];
	private readonly authorityClosures = new Map<string, RunAuthorityClosure>();
	private readonly closeOperations = new Map<string, string>();
	private readonly runAuthorities = new Map<string, RunAdmissionAuthority>();

	constructor(options: InMemoryEventStoreOptions = {}) {
		this.newEventId = options.newEventId ?? randomUUID;
		this.now = options.now ?? (() => new Date().toISOString());
		this.retireGoalAuthority = options.retireGoalAuthority;
	}

	async closeAuthority(input: CloseRunAuthorityRequest): Promise<RunAuthorityClosure> {
		const request = captureCloseRunAuthorityRequest(input);
		if (request.workRetirement !== undefined)
			throw new TypeError("Composite work retirement requires a shared SQLite authority.");
		const key = authorityMapKey(request);
		const serialized = canonicalizeJsonValue(request);
		const readExisting = () => {
			const operationKey = this.closeOperations.get(request.operationId);
			if (operationKey !== undefined && operationKey !== key) throw new RunAuthorityConflictError();
			const existing = this.authorityClosures.get(key);
			if (existing === undefined) return undefined;
			const { closedAt: _closedAt, ...previous } = existing;
			if (canonicalizeJsonValue(previous) !== serialized) throw new RunAuthorityConflictError();
			return structuredClone(existing);
		};
		const existing = readExisting();
		if (existing !== undefined) return existing;
		const closedAt = this.now();
		assertCanonicalTimestamp(closedAt);
		const reentrant = readExisting();
		if (reentrant !== undefined) return reentrant;
		const closure = structuredClone({ ...request, closedAt });
		if (request.goalRetirement !== undefined) {
			if (this.retireGoalAuthority === undefined)
				throw new TypeError("Closing Goal authority requires a synchronous Goal retirement binding.");
			this.retireGoalAuthority(request.goalRetirement);
		}
		this.authorityClosures.set(key, closure);
		this.closeOperations.set(request.operationId, key);
		return structuredClone(closure);
	}

	async readAuthorityClosure(input: RunAuthorityKey): Promise<RunAuthorityClosure | undefined> {
		const key = captureRunAuthorityKey(input);
		const closure = this.authorityClosures.get(authorityMapKey(key));
		return closure === undefined ? undefined : structuredClone(closure);
	}

	async finalizeAuthorityClosure(input: CloseRunAuthorityRequest): Promise<RunAuthorityClosure> {
		const request = captureCloseRunAuthorityRequest(input);
		const closure = this.authorityClosures.get(authorityMapKey(request));
		if (
			closure === undefined ||
			canonicalizeJsonValue(captureCloseRunAuthorityRequest(closure)) !== canonicalizeJsonValue(request)
		) {
			throw new RunAuthorityConflictError();
		}
		return structuredClone(closure);
	}

	async assertRunAuthorityOpen(coordinate: RunCoordinate): Promise<void> {
		this.assertRunAuthorityOpenSynchronously(coordinate);
	}

	assertRunAuthorityOpenSynchronously(coordinate: RunCoordinate): void {
		assertEventStreamCoordinate({ sessionId: coordinate.sessionId, actorId: coordinate.actorId }, coordinate);
		const admission = this.runAuthorities.get(runMapKey(coordinate));
		if (admission !== undefined) this.assertAuthorityOpen({ ...coordinate, ...admission });
	}

	private assertAuthorityOpen(key: RunAuthorityKey): void {
		if (this.authorityClosures.has(authorityMapKey(key)))
			throw new RunAuthorityClosedError(captureRunAuthorityKey(key));
	}

	async read(stream: StreamKey, request: EventReadRequest): Promise<CanonicalEventPage> {
		validateStream(stream);
		assertEventReadLimit(request.limit);
		const afterRevision = request.afterRevision ?? "0";
		const after = parseRevision(afterRevision);
		const candidates = (this.streams.get(streamMapKey(stream)) ?? [])
			.filter((event) => BigInt(event.revision) > after)
			.slice(0, request.limit + 1);
		const hasMore = candidates.length > request.limit;
		const events = candidates.slice(0, request.limit).map((event) => structuredClone(event));
		return {
			events,
			cursor: events.at(-1)?.revision ?? afterRevision,
			hasMore,
		};
	}

	async readRevision(stream: StreamKey): Promise<ExpectedRevision> {
		validateStream(stream);
		const revision = this.revisions.get(streamMapKey(stream));
		return revision === undefined ? "empty" : revision.toString();
	}

	async readBounds(stream: StreamKey): Promise<EventStreamBounds> {
		validateStream(stream);
		const events = this.streams.get(streamMapKey(stream)) ?? [];
		return {
			earliestRevision: events[0]?.revision ?? null,
			latestRevision: this.revisions.get(streamMapKey(stream))?.toString() ?? "0",
		};
	}

	async readRun(stream: StreamKey, inputId: string): Promise<StoredRunRecord | undefined> {
		validateStream(stream);
		assertOpaqueIdentifier(inputId);
		const events = this.streams.get(streamMapKey(stream)) ?? [];
		const admission = events.find((event) => event.data.type === "run.queued" && event.data.inputId === inputId);
		if (admission?.data.type !== "run.queued") return undefined;
		const runEvents = events.filter((event) => event.coordinate.runId === admission.coordinate.runId);
		const latest = runEvents.at(-1) ?? admission;
		const terminal = [...runEvents].reverse().find((event) => event.data.type === "run.completed");
		const base = {
			...(admission.data.admissionAuthority === undefined
				? {}
				: { admissionAuthority: structuredClone(admission.data.admissionAuthority) }),
			inputId,
			inputFingerprint: admission.data.inputFingerprint,
			eventCursor: (parseRevision(admission.revision) - 1n).toString(),
			initialCoordinate: structuredClone(admission.coordinate),
			latestCoordinate: structuredClone(latest.coordinate),
		};
		if (terminal?.data.type === "run.completed") {
			if (terminal.data.status !== "completed") return { ...base, status: terminal.data.status };
			const assistantOutput = [...runEvents]
				.reverse()
				.flatMap((event) => (event.data.type === "item.completed" ? [canonicalAssistantText(event.data.item)] : []))
				.find((text) => text !== undefined);
			return {
				...base,
				status: terminal.data.status,
				...(assistantOutput === undefined ? {} : { finalOutput: assistantOutput }),
			};
		}
		const deferredLifecycle = [...runEvents]
			.reverse()
			.find((event) => event.data.type === "run.suspended" || event.data.type === "run.resumed");
		return deferredLifecycle?.data.type === "run.suspended"
			? { ...base, status: "deferred", operationId: deferredLifecycle.data.operationId }
			: { ...base, status: null };
	}

	async append(
		inputStream: StreamKey,
		expectedRevision: ExpectedRevision,
		inputCoordinate: RunCoordinate,
		data: CanonicalEventData,
	): Promise<CanonicalEvent> {
		const stream = structuredClone(inputStream);
		const coordinate = structuredClone(inputCoordinate);
		assertEventStreamCoordinate(stream, coordinate);
		const eventData = structuredClone(data);
		assertCanonicalEventData(eventData);
		const eventId = this.newEventId();
		assertOpaqueIdentifier(eventId);
		const occurredAt = this.now();
		assertCanonicalTimestamp(occurredAt);
		const key = streamMapKey(stream);
		const current = this.streams.get(key) ?? [];
		const head = this.revisions.get(key);
		const actual = head === undefined ? "empty" : head.toString();
		if (expectedRevision !== actual) {
			throw new WrongExpectedRevisionError(expectedRevision, actual);
		}
		if (eventData.type === "run.queued") {
			const existing = this.runAuthorities.get(runMapKey(coordinate));
			if (
				existing !== undefined &&
				(existing.scope !== eventData.admissionAuthority?.scope ||
					existing.generation !== eventData.admissionAuthority?.generation)
			) {
				throw new RunAuthorityConflictError();
			}
			if (eventData.admissionAuthority !== undefined)
				this.assertAuthorityOpen({ ...coordinate, ...eventData.admissionAuthority });
		}
		if (eventData.type === "run.started" || eventData.type === "run.resumed")
			this.assertRunAuthorityOpenSynchronously(coordinate);

		const next = (head ?? 0n) + 1n;
		const event: CanonicalEvent = {
			protocolVersion: PROTOCOL_VERSION,
			eventId,
			sequence: next.toString(),
			revision: next.toString(),
			occurredAt,
			coordinate: structuredClone(coordinate),
			audience: eventAudience(eventData),
			data: eventData,
		};
		this.streams.set(key, [...current, structuredClone(event)]);
		this.revisions.set(key, next);
		if (eventData.type === "run.queued" && eventData.admissionAuthority !== undefined) {
			this.runAuthorities.set(runMapKey(coordinate), captureRunAdmissionAuthority(eventData.admissionAuthority));
		}
		return structuredClone(event);
	}

	async purgeTerminalRuns(stream: StreamKey, request: TerminalEventPurgeRequest): Promise<TerminalEventPurgeResult> {
		validateStream(stream);
		const captured = capturePurgeRequest(request);
		const key = streamMapKey(stream);
		const events = this.streams.get(key) ?? [];
		const candidate = terminalPrefix(events.slice(0, captured.limit), captured);
		if (candidate.eventCount > 0) {
			for (const runId of candidate.runIds) {
				if (events.slice(candidate.eventCount).some((event) => event.coordinate.runId === runId)) {
					throw new TypeError("A canonical event run continues after its terminal event.");
				}
			}
			this.streams.set(key, events.slice(candidate.eventCount));
		}
		const remaining = this.streams.get(key) ?? [];
		return {
			purgedEventCount: candidate.eventCount,
			purgedRunIds: candidate.runIds,
			earliestRevision: remaining[0]?.revision ?? null,
			latestRevision: this.revisions.get(key)?.toString() ?? "0",
		};
	}
}

function authorityMapKey(key: RunAuthorityKey): string {
	return JSON.stringify([key.sessionId, key.actorId, key.scope, key.generation]);
}

function runMapKey(key: RunCoordinate): string {
	return JSON.stringify([key.sessionId, key.actorId, key.runId]);
}

function capturePurgeRequest(request: TerminalEventPurgeRequest): Readonly<TerminalEventPurgeRequest> {
	assertCanonicalTimestamp(request.beforeOccurredAt);
	assertEventReadLimit(request.limit);
	const retainBudget = request.retainBudget;
	const retainRunIds = captureRetainedIdentifiers(request.retainRunIds, retainBudget, "retainRunIds");
	for (const runId of retainRunIds) assertOpaqueIdentifier(runId);
	return Object.freeze({
		beforeOccurredAt: request.beforeOccurredAt,
		limit: request.limit,
		retainRunIds,
		retainBudget: Object.freeze({ maxCount: retainBudget.maxCount, maxBytes: retainBudget.maxBytes }),
	});
}

function terminalPrefix(
	events: readonly CanonicalEvent[],
	request: Readonly<TerminalEventPurgeRequest>,
): { readonly eventCount: number; readonly runIds: readonly string[] } {
	const retained = new Set(request.retainRunIds);
	const seen = new Set<string>();
	const terminal = new Set<string>();
	let eventCount = 0;
	let runIds: readonly string[] = [];
	for (let index = 0; index < events.length; index += 1) {
		const event = events[index];
		if (!event) break;
		const runId = event.coordinate.runId;
		if (retained.has(runId)) break;
		seen.add(runId);
		if (event.data.type === "run.completed") {
			if (event.occurredAt >= request.beforeOccurredAt) break;
			terminal.add(runId);
		}
		if (seen.size === terminal.size) {
			eventCount = index + 1;
			runIds = [...seen];
		}
	}
	return { eventCount, runIds };
}

function validateStream(stream: StreamKey): void {
	assertOpaqueIdentifier(stream.sessionId);
	assertOpaqueIdentifier(stream.actorId);
}

function streamMapKey(stream: StreamKey): string {
	return JSON.stringify([stream.sessionId, stream.actorId]);
}

function parseRevision(revision: string): bigint {
	if (!/^(0|[1-9][0-9]*)$/u.test(revision)) {
		throw new TypeError("Revision must be a non-negative decimal string.");
	}
	return BigInt(revision);
}
