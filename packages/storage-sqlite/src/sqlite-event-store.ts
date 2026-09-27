import { randomUUID } from "node:crypto";
import {
	assertCanonicalEvent,
	assertCanonicalEventData,
	assertCanonicalTimestamp,
	assertEventReadLimit,
	assertEventStreamCoordinate,
	assertOpaqueIdentifier,
	assertRuntimeAuthorityClosure,
	type CanonicalEvent,
	type CanonicalEventData,
	type CanonicalEventPage,
	type CanonicalEventStore,
	type CloseRunAuthorityRequest,
	canonicalAssistantText,
	canonicalizeJsonValue,
	captureCloseRunAuthorityRequest,
	captureRetainedIdentifiers,
	captureRunAuthorityKey,
	type EventReadRequest,
	type EventStreamBounds,
	type ExpectedRevision,
	eventAudience,
	PROTOCOL_VERSION,
	type RunAuthorityClosure,
	RunAuthorityConflictError,
	type RunAuthorityKey,
	type RunAuthorityWorkRetirementResult,
	type RunCoordinate,
	type StoredRunRecord,
	type StreamKey,
	type TerminalEventPurgeRequest,
	type TerminalEventPurgeResult,
} from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";
import type { SqliteGoalStore } from "./sqlite-goal-store.js";
import type { SqliteScheduleStore } from "./sqlite-schedule-store.js";
import type { SqliteMailboxStore } from "./sqlite-mailbox-store.js";
import {
	assertSqliteRunAuthorityOpen,
	installSqliteRunAuthority,
	readSqliteRunAdmissionAuthority,
	recordSqliteRunAdmissionAuthority,
} from "./sqlite-run-authority.js";

const SQLITE_INTEGER_MAX = 9_223_372_036_854_775_807n;

const EVENT_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "canonical-events",
		sql: `
			CREATE TABLE canonical_events (
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				revision INTEGER NOT NULL CHECK(revision > 0),
				sequence INTEGER NOT NULL CHECK(sequence > 0),
				protocol_version TEXT NOT NULL,
				event_id TEXT NOT NULL UNIQUE,
				run_id TEXT NOT NULL,
				turn TEXT NOT NULL,
				occurred_at TEXT NOT NULL,
				event_type TEXT NOT NULL,
				input_id TEXT,
				input_fingerprint TEXT,
				audience_json TEXT NOT NULL CHECK(json_valid(audience_json)),
				data_json TEXT NOT NULL CHECK(json_valid(data_json)),
				CHECK((event_type = 'run.queued') = (input_id IS NOT NULL AND input_fingerprint IS NOT NULL)),
				CHECK(input_fingerprint IS NULL OR (
					length(input_fingerprint) = 64 AND input_fingerprint NOT GLOB '*[^0-9a-f]*'
				)),
				PRIMARY KEY(session_id, actor_id, revision),
				UNIQUE(session_id, actor_id, sequence)
			) STRICT;
			CREATE INDEX canonical_events_stream_sequence
				ON canonical_events(session_id, actor_id, sequence);
			CREATE UNIQUE INDEX canonical_events_run_input
				ON canonical_events(session_id, actor_id, input_id)
				WHERE event_type = 'run.queued';
		`,
	},
	{
		version: 2,
		name: "canonical-events-run-lookup",
		sql: `
			CREATE INDEX canonical_events_stream_run_revision
				ON canonical_events(session_id, actor_id, run_id, revision);
		`,
	},
	{
		version: 3,
		name: "canonical-event-stream-heads",
		sql: `
			CREATE TABLE canonical_event_streams (
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				revision INTEGER NOT NULL CHECK(revision > 0),
				PRIMARY KEY(session_id, actor_id)
			) STRICT;
			INSERT INTO canonical_event_streams(session_id, actor_id, revision)
			SELECT session_id, actor_id, MAX(revision)
			FROM canonical_events
			GROUP BY session_id, actor_id;
		`,
	},
];

export class SqliteEventRevisionExhaustedError extends Error {
	constructor() {
		super("The canonical event stream exhausted SQLite's signed 64-bit integer revision space.");
		this.name = "SqliteEventRevisionExhaustedError";
	}
}

export class SqliteWrongExpectedRevisionError extends Error {
	constructor(
		public readonly expected: ExpectedRevision,
		public readonly actual: ExpectedRevision,
	) {
		super("Canonical event stream revision did not match.");
		this.name = "SqliteWrongExpectedRevisionError";
	}
}

export interface SqliteEventStoreOptions {
	goalStore?: SqliteGoalStore;
	scheduleStore?: SqliteScheduleStore;
	mailboxStore?: SqliteMailboxStore;
	newEventId?: () => string;
	now?: () => string;
}

interface EventRow {
	protocol_version: string;
	event_id: string;
	sequence: bigint;
	revision: bigint;
	session_id: string;
	actor_id: string;
	run_id: string;
	turn: string;
	occurred_at: string;
	audience_json: string;
	data_json: string;
}

export class SqliteEventStore implements CanonicalEventStore {
	private readonly newEventId: () => string;
	private readonly now: () => string;
	private readonly goalStore: SqliteGoalStore | undefined;
	private readonly scheduleStore: SqliteScheduleStore | undefined;
	private readonly mailboxStore: SqliteMailboxStore | undefined;

	constructor(
		private readonly authority: SqliteAuthority,
		options: SqliteEventStoreOptions = {},
	) {
		this.authority.assertExclusiveOwnership();
		this.newEventId = options.newEventId ?? randomUUID;
		this.now = options.now ?? (() => new Date().toISOString());
		this.goalStore = options.goalStore;
		this.scheduleStore = options.scheduleStore;
		this.mailboxStore = options.mailboxStore;
		this.authority.install("canonical-events", EVENT_MIGRATIONS);
		installSqliteRunAuthority(this.authority);
	}

	async closeAuthority(input: CloseRunAuthorityRequest): Promise<RunAuthorityClosure> {
		const request = captureCloseRunAuthorityRequest(input);
		const serialized = canonicalizeJsonValue(request);
		const readExisting = (database: Database.Database) => {
			const existingOperation = database
				.prepare(
					"SELECT request_json, closed_at, work_result_json FROM oga_run_authority_closures WHERE operation_id = ?",
				)
				.get(request.operationId) as ClosureRow | undefined;
			const existing = existingOperation ?? readClosureRow(database, request);
			if (existing !== undefined) {
				if (existing.request_json !== serialized) throw new RunAuthorityConflictError();
				return hydrateClosure(existing);
			}
			return undefined;
		};
		const existing = this.authority.read(readExisting);
		if (existing !== undefined) return existing;
		if (request.goalRetirement !== undefined && this.goalStore === undefined) {
			throw new TypeError("Closing Goal authority requires a Goal store on the same SQLite authority.");
		}
		if (request.workRetirement !== undefined && (this.scheduleStore === undefined || this.mailboxStore === undefined)) {
			throw new TypeError("Closing work authority requires Schedule and Mailbox stores on the same SQLite authority.");
		}
		const closedAt = this.now();
		assertCanonicalTimestamp(closedAt);
		return this.authority.write((database) => {
			const reentrant = readExisting(database);
			if (reentrant !== undefined) return reentrant;
			database
				.prepare(`INSERT INTO oga_run_authority_closures(session_id, actor_id, scope, generation, operation_id, request_json, closed_at)
				VALUES (?, ?, ?, ?, ?, ?, ?)`)
				.run(
					request.sessionId,
					request.actorId,
					request.scope,
					request.generation,
					request.operationId,
					serialized,
					closedAt,
				);
			if (request.goalRetirement !== undefined) {
				this.goalStore?.retireAuthorityInTransaction(database, request.goalRetirement);
			}
			const workRetirementResult = this.retireWork(database, request, "fenced");
			return structuredClone({
				...request,
				closedAt,
				...(workRetirementResult === undefined ? {} : { workRetirementResult }),
			});
		});
	}

	async finalizeAuthorityClosure(input: CloseRunAuthorityRequest): Promise<RunAuthorityClosure> {
		const request = captureCloseRunAuthorityRequest(input);
		const serialized = canonicalizeJsonValue(request);
		return this.authority.write((database) => {
			const row = readClosureRow(database, request);
			if (row === undefined || row.request_json !== serialized) throw new RunAuthorityConflictError();
			const closure = hydrateClosure(row);
			if (request.workRetirement === undefined || closure.workRetirementResult?.phase === "finalized") return closure;
			const workRetirementResult = this.retireWork(database, request, "finalized");
			return structuredClone({ ...closure, ...(workRetirementResult === undefined ? {} : { workRetirementResult }) });
		});
	}

	private retireWork(
		database: Database.Database,
		request: CloseRunAuthorityRequest,
		phase: "fenced" | "finalized",
	): RunAuthorityWorkRetirementResult | undefined {
		if (request.workRetirement === undefined) return undefined;
		if (this.scheduleStore === undefined || this.mailboxStore === undefined) {
			throw new TypeError("Closing work authority requires Schedule and Mailbox stores on the same SQLite authority.");
		}
		const schedules =
			phase === "fenced"
				? this.scheduleStore.beginAuthorityRetirementInTransaction(database, request.workRetirement)
				: this.scheduleStore.finalizeAuthorityRetirementInTransaction(database, request.workRetirement);
		const mailbox =
			phase === "fenced"
				? this.mailboxStore.beginAuthorityRetirementInTransaction(database, request.workRetirement)
				: this.mailboxStore.finalizeAuthorityRetirementInTransaction(database, request.workRetirement);
		const result = {
			phase,
			cancelledScheduleCount: String(schedules.cancelledScheduleCount),
			cancelledOccurrenceCount: String(schedules.cancelledOccurrenceCount),
			cancelledMessageCount: String(mailbox.cancelledMessageCount),
		};
		database
			.prepare("UPDATE oga_run_authority_closures SET work_result_json = ? WHERE operation_id = ?")
			.run(canonicalizeJsonValue(result), request.operationId);
		return result;
	}

	async readAuthorityClosure(input: RunAuthorityKey): Promise<RunAuthorityClosure | undefined> {
		const key = captureRunAuthorityKey(input);
		return this.authority.read((database) => {
			const row = readClosureRow(database, key);
			return row === undefined ? undefined : hydrateClosure(row);
		});
	}

	async assertRunAuthorityOpen(coordinate: RunCoordinate): Promise<void> {
		this.authority.read((database) => assertSqliteRunAuthorityOpen(database, coordinate));
	}

	async read(stream: StreamKey, request: EventReadRequest): Promise<CanonicalEventPage> {
		validateStream(stream);
		assertEventReadLimit(request.limit);
		const afterRevision = request.afterRevision ?? "0";
		const after = parseSqliteRevision(afterRevision);
		const rows = this.authority.read((database) => {
			const statement = database.prepare(`
				SELECT protocol_version, event_id, sequence, revision, session_id, actor_id,
				       run_id, turn, occurred_at, audience_json, data_json
				FROM canonical_events
				WHERE session_id = ? AND actor_id = ? AND revision > ?
				ORDER BY revision
				LIMIT ?
			`);
			statement.safeIntegers(true);
			return statement.all(stream.sessionId, stream.actorId, after, BigInt(request.limit + 1)) as EventRow[];
		});
		const hasMore = rows.length > request.limit;
		const events = rows.slice(0, request.limit).map(rowToEvent);
		return { events, cursor: events.at(-1)?.revision ?? afterRevision, hasMore };
	}

	async readRevision(stream: StreamKey): Promise<ExpectedRevision> {
		validateStream(stream);
		return this.authority.read((database) => currentRevision(database, stream));
	}

	async readBounds(stream: StreamKey): Promise<EventStreamBounds> {
		validateStream(stream);
		return this.authority.read((database) => readBounds(database, stream));
	}

	async readRun(stream: StreamKey, inputId: string): Promise<StoredRunRecord | undefined> {
		validateStream(stream);
		assertOpaqueIdentifier(inputId);
		return this.authority.read((database) => {
			const statement = database.prepare(`
					WITH admission AS (
						SELECT revision, run_id, turn, input_fingerprint
						FROM canonical_events
						WHERE session_id = ? AND actor_id = ?
						  AND event_type = 'run.queued' AND input_id = ?
						LIMIT 1
					)
					SELECT admission.revision AS admission_revision, admission.run_id,
					       admission.turn AS initial_turn, admission.input_fingerprint,
					       (SELECT turn FROM canonical_events latest
					        WHERE latest.session_id = ? AND latest.actor_id = ?
					          AND latest.run_id = admission.run_id
					        ORDER BY latest.revision DESC LIMIT 1) AS latest_turn,
					       (SELECT json_extract(terminal.data_json, '$.status')
					        FROM canonical_events terminal
					        WHERE terminal.session_id = ? AND terminal.actor_id = ?
					          AND terminal.run_id = admission.run_id
					          AND terminal.event_type = 'run.completed'
					        ORDER BY terminal.revision DESC LIMIT 1) AS status,
					       (SELECT lifecycle.event_type
					        FROM canonical_events lifecycle
					        WHERE lifecycle.session_id = ? AND lifecycle.actor_id = ?
					          AND lifecycle.run_id = admission.run_id
					          AND lifecycle.event_type IN ('run.suspended', 'run.resumed')
					        ORDER BY lifecycle.revision DESC LIMIT 1) AS deferred_event_type,
					       (SELECT json_extract(lifecycle.data_json, '$.operationId')
					        FROM canonical_events lifecycle
					        WHERE lifecycle.session_id = ? AND lifecycle.actor_id = ?
					          AND lifecycle.run_id = admission.run_id
					          AND lifecycle.event_type IN ('run.suspended', 'run.resumed')
					        ORDER BY lifecycle.revision DESC LIMIT 1) AS operation_id
					FROM admission
				`);
			statement.safeIntegers(true);
			const row = statement.get(
				stream.sessionId,
				stream.actorId,
				inputId,
				stream.sessionId,
				stream.actorId,
				stream.sessionId,
				stream.actorId,
				stream.sessionId,
				stream.actorId,
				stream.sessionId,
				stream.actorId,
			) as
				| {
						admission_revision: bigint;
						run_id: string;
						initial_turn: string;
						input_fingerprint: string;
						latest_turn: string;
						status: string | null;
						deferred_event_type: string | null;
						operation_id: string | null;
				  }
				| undefined;
			if (!row) return undefined;
			const admissionAuthority = readSqliteRunAdmissionAuthority(database, { ...stream, runId: row.run_id });
			if (row.status !== null && !isRunStatus(row.status)) {
				throw new TypeError("The persisted run status is invalid.");
			}
			const base = {
				...(admissionAuthority === undefined ? {} : { admissionAuthority }),
				inputId,
				inputFingerprint: row.input_fingerprint,
				eventCursor: (row.admission_revision - 1n).toString(),
				initialCoordinate: { ...stream, runId: row.run_id, turn: row.initial_turn },
				latestCoordinate: { ...stream, runId: row.run_id, turn: row.latest_turn },
			};
			if (row.status !== null) {
				if (row.status !== "completed") return { ...base, status: row.status };
				const finalOutput = readFinalOutput(database, stream, row.run_id);
				return { ...base, status: row.status, ...(finalOutput === undefined ? {} : { finalOutput }) };
			}
			if (row.deferred_event_type === "run.suspended" && row.operation_id !== null) {
				assertOpaqueIdentifier(row.operation_id);
				return { ...base, status: "deferred", operationId: row.operation_id };
			}
			return { ...base, status: null };
		});
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
		return this.authority.write((database) => {
			const actual = currentRevision(database, stream);
			if (expectedRevision !== actual) {
				throw new SqliteWrongExpectedRevisionError(expectedRevision, actual);
			}
			if (eventData.type === "run.queued")
				recordSqliteRunAdmissionAuthority(database, coordinate, eventData.admissionAuthority);
			if (eventData.type === "run.started" || eventData.type === "run.resumed")
				assertSqliteRunAuthorityOpen(database, coordinate);
			const next = actual === "empty" ? 1n : BigInt(actual) + 1n;
			if (next > SQLITE_INTEGER_MAX) throw new SqliteEventRevisionExhaustedError();
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
			database
				.prepare(`
					INSERT INTO canonical_events(
						session_id, actor_id, revision, sequence, protocol_version, event_id,
						run_id, turn, occurred_at, event_type, input_id, input_fingerprint,
						audience_json, data_json
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
				`)
				.run(
					stream.sessionId,
					stream.actorId,
					next,
					next,
					event.protocolVersion,
					event.eventId,
					event.coordinate.runId,
					event.coordinate.turn,
					event.occurredAt,
					event.data.type,
					event.data.type === "run.queued" ? event.data.inputId : null,
					event.data.type === "run.queued" ? event.data.inputFingerprint : null,
					JSON.stringify(event.audience),
					JSON.stringify(event.data),
				);
			database
				.prepare(`
					INSERT INTO canonical_event_streams(session_id, actor_id, revision)
					VALUES (?, ?, ?)
					ON CONFLICT(session_id, actor_id) DO UPDATE SET revision = excluded.revision
				`)
				.run(stream.sessionId, stream.actorId, next);
			return structuredClone(event);
		});
	}

	async purgeTerminalRuns(stream: StreamKey, request: TerminalEventPurgeRequest): Promise<TerminalEventPurgeResult> {
		validateStream(stream);
		const captured = capturePurgeRequest(request);
		return this.authority.write((database) => {
			const statement = database.prepare(`
				SELECT revision, run_id, occurred_at, event_type
				FROM canonical_events
				WHERE session_id = ? AND actor_id = ?
				ORDER BY revision
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(stream.sessionId, stream.actorId, BigInt(captured.limit)) as Array<{
				revision: bigint;
				run_id: string;
				occurred_at: string;
				event_type: string;
			}>;
			const candidate = terminalRowPrefix(rows, captured);
			if (candidate.revision !== undefined) {
				const later = database.prepare(`
					SELECT 1 FROM canonical_events
					WHERE session_id = ? AND actor_id = ? AND run_id = ? AND revision > ?
					LIMIT 1
				`);
				for (const runId of candidate.runIds) {
					if (later.get(stream.sessionId, stream.actorId, runId, candidate.revision) !== undefined) {
						throw new TypeError("A canonical event run continues after its terminal event.");
					}
				}
				database
					.prepare(`
						DELETE FROM canonical_events
						WHERE session_id = ? AND actor_id = ? AND revision <= ?
					`)
					.run(stream.sessionId, stream.actorId, candidate.revision);
			}
			const bounds = readBounds(database, stream);
			return {
				purgedEventCount: candidate.eventCount,
				purgedRunIds: candidate.runIds,
				...bounds,
			};
		});
	}
}

interface ClosureRow {
	request_json: string;
	closed_at: string;
	work_result_json: string | null;
}

function readClosureRow(database: Database.Database, key: RunAuthorityKey): ClosureRow | undefined {
	return database
		.prepare(`SELECT request_json, closed_at, work_result_json FROM oga_run_authority_closures
		WHERE session_id = ? AND actor_id = ? AND scope = ? AND generation = ?`)
		.get(key.sessionId, key.actorId, key.scope, key.generation) as ClosureRow | undefined;
}

function hydrateClosure(row: ClosureRow): RunAuthorityClosure {
	const request = captureCloseRunAuthorityRequest(JSON.parse(row.request_json) as CloseRunAuthorityRequest);
	assertCanonicalTimestamp(row.closed_at);
	const closure = {
		...request,
		closedAt: row.closed_at,
		...(row.work_result_json === null ? {} : { workRetirementResult: JSON.parse(row.work_result_json) as unknown }),
	};
	assertRuntimeAuthorityClosure(closure);
	return closure;
}

function isRunStatus(value: string): value is "completed" | "aborted" | "failed" {
	return value === "completed" || value === "aborted" || value === "failed";
}

function readFinalOutput(database: Database.Database, stream: StreamKey, runId: string): string | undefined {
	const statement = database.prepare(`
		SELECT protocol_version, event_id, sequence, revision, session_id, actor_id,
		       run_id, turn, occurred_at, audience_json, data_json
		FROM canonical_events
		WHERE session_id = ? AND actor_id = ? AND run_id = ? AND event_type = 'item.completed'
		ORDER BY revision DESC
	`);
	statement.safeIntegers(true);
	for (const row of statement.iterate(stream.sessionId, stream.actorId, runId) as Iterable<EventRow>) {
		const event = rowToEvent(row);
		if (event.data.type !== "item.completed") throw new TypeError("The persisted run output event is invalid.");
		const text = canonicalAssistantText(event.data.item);
		if (text !== undefined) return text;
	}
	return undefined;
}

function currentRevision(database: Database.Database, stream: StreamKey): ExpectedRevision {
	const statement = database.prepare(`
		SELECT revision FROM canonical_event_streams
		WHERE session_id = ? AND actor_id = ?
	`);
	statement.safeIntegers(true);
	const row = statement.get(stream.sessionId, stream.actorId) as { revision: bigint } | undefined;
	return row ? row.revision.toString() : "empty";
}

function readBounds(database: Database.Database, stream: StreamKey): EventStreamBounds {
	const statement = database.prepare(`
		SELECT
			(SELECT MIN(revision) FROM canonical_events
			 WHERE session_id = ? AND actor_id = ?) AS earliest_revision,
			(SELECT revision FROM canonical_event_streams
			 WHERE session_id = ? AND actor_id = ?) AS latest_revision
	`);
	statement.safeIntegers(true);
	const row = statement.get(stream.sessionId, stream.actorId, stream.sessionId, stream.actorId) as {
		earliest_revision: bigint | null;
		latest_revision: bigint | null;
	};
	return {
		earliestRevision: row.earliest_revision?.toString() ?? null,
		latestRevision: row.latest_revision?.toString() ?? "0",
	};
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

function terminalRowPrefix(
	rows: readonly { revision: bigint; run_id: string; occurred_at: string; event_type: string }[],
	request: Readonly<TerminalEventPurgeRequest>,
): { readonly eventCount: number; readonly revision?: bigint; readonly runIds: readonly string[] } {
	const retained = new Set(request.retainRunIds);
	const seen = new Set<string>();
	const terminal = new Set<string>();
	let eventCount = 0;
	let revision: bigint | undefined;
	let runIds: readonly string[] = [];
	for (let index = 0; index < rows.length; index += 1) {
		const row = rows[index];
		if (!row) break;
		if (retained.has(row.run_id)) break;
		seen.add(row.run_id);
		if (row.event_type === "run.completed") {
			if (row.occurred_at >= request.beforeOccurredAt) break;
			terminal.add(row.run_id);
		}
		if (seen.size === terminal.size) {
			eventCount = index + 1;
			revision = row.revision;
			runIds = [...seen];
		}
	}
	return { eventCount, ...(revision === undefined ? {} : { revision }), runIds };
}

function rowToEvent(row: EventRow): CanonicalEvent {
	const event = {
		protocolVersion: row.protocol_version,
		eventId: row.event_id,
		sequence: row.sequence.toString(),
		revision: row.revision.toString(),
		occurredAt: row.occurred_at,
		coordinate: {
			sessionId: row.session_id,
			actorId: row.actor_id,
			runId: row.run_id,
			turn: row.turn,
		},
		audience: JSON.parse(row.audience_json) as unknown,
		data: JSON.parse(row.data_json) as unknown,
	};
	assertCanonicalEvent(event);
	return event;
}

function validateStream(stream: StreamKey): void {
	assertOpaqueIdentifier(stream.sessionId);
	assertOpaqueIdentifier(stream.actorId);
}

function parseSqliteRevision(revision: string): bigint {
	if (!/^(0|[1-9][0-9]*)$/u.test(revision)) {
		throw new TypeError("Revision must be a non-negative canonical decimal string.");
	}
	const parsed = BigInt(revision);
	if (parsed > SQLITE_INTEGER_MAX) throw new RangeError("Revision exceeds SQLite's signed 64-bit integer range.");
	return parsed;
}
