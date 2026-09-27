import { createHash, randomUUID } from "node:crypto";
import {
	addScheduleDecimals,
	compareScheduleDecimals,
	type GameScheduleDraft,
	type GameScheduleKey,
	type GameScheduleRecord,
	type GameScheduleSequenceKey,
	incrementScheduleDecimal,
	laterScheduleTimestamp,
	normalizeGameSchedule,
	normalizeScheduleAdvance,
	normalizeScheduleAuthorityLane,
	normalizeScheduleAuthorityRetirement,
	normalizeScheduleClaim,
	normalizeScheduledOccurrenceListRequest,
	normalizeScheduleListRequest,
	normalizeSchedulePartition,
	ScheduleAdvanceConflictError,
	type ScheduleAdvanceDraft,
	ScheduleAdvanceOverflowError,
	type ScheduleAdvanceRecord,
	type ScheduleAdvanceResult,
	type ScheduleAuthorityRetirementRecord,
	ScheduleCapacityError,
	ScheduleClockConflictError,
	ScheduleConflictError,
	type ScheduledOccurrenceDelivery,
	type ScheduledOccurrenceListRequest,
	type ScheduledOccurrencePage,
	type ScheduledOccurrenceRecord,
	type ScheduledOccurrenceSequenceKey,
	type ScheduleLimits,
	type ScheduleListRequest,
	ScheduleMissedOccurrenceError,
	ScheduleNotFoundError,
	type ScheduleOccurrenceClaimRequest,
	ScheduleOccurrenceLeaseConflictError,
	ScheduleOccurrenceNotFoundError,
	type SchedulePage,
	type SchedulePartition,
	ScheduleRevisionConflictError,
	type ScheduleStore,
	ScheduleTerminalError,
	ScheduleValidationError,
	sameScheduleGeneration,
	scheduleAdvanceFingerprint,
	scheduledOccurrenceFingerprint,
	scheduleFingerprint,
	validateGameScheduleKey,
	validateGameScheduleSequenceKey,
	validatePositiveScheduleKey,
	validateScheduleDecimal,
	validateScheduledOccurrenceSequenceKey,
	validateScheduledOccurrenceStatus,
	validateScheduleIdentifier,
	validateScheduleLimits,
	validateSchedulePageLimit,
	validateScheduleStatus,
	validateScheduleTimestamp,
} from "@opengameagent/extensions";
import {
	canonicalizeJsonValue,
	type WorkAuthorityLane,
	WorkAuthorityRetiredError,
	WorkAuthorityRetirementConflictError,
	type WorkAuthorityRetirementRequest,
} from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import TinyQueue from "tinyqueue";
import { requireSqliteWriteTransaction, type SqliteAuthority, type SqliteMigration } from "./sqlite-authority.js";

const SHA256 = /^[0-9a-f]{64}$/u;
const SCHEDULE_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "schedule-store-v1",
		sql: `
			CREATE TABLE oga_game_schedules (
				schedule_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(schedule_key > 0),
				schedule_id TEXT NOT NULL,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				session_id TEXT,
				actor_id TEXT,
				kind TEXT NOT NULL,
				payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
				due_tick TEXT NOT NULL,
				interval_ticks TEXT,
				maximum_occurrences TEXT,
				status TEXT NOT NULL CHECK(status IN ('active', 'completed', 'cancelled')),
				revision TEXT NOT NULL,
				occurrences TEXT NOT NULL,
				next_due_tick TEXT,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				source_fingerprint TEXT NOT NULL CHECK(length(source_fingerprint) = 64),
				CHECK((status = 'active') = (next_due_tick IS NOT NULL)),
				CHECK(interval_ticks IS NOT NULL OR maximum_occurrences = '1'),
				UNIQUE(owner_id, timeline_id, generation, schedule_id)
			) STRICT;

			CREATE INDEX oga_game_schedules_partition_status_key
			ON oga_game_schedules(owner_id, timeline_id, generation, status, schedule_key);

			CREATE INDEX oga_game_schedules_due
			ON oga_game_schedules(owner_id, timeline_id, generation, status, length(next_due_tick), next_due_tick, schedule_key)
			WHERE status = 'active';

			CREATE TABLE oga_game_schedule_subjects (
				schedule_key INTEGER NOT NULL REFERENCES oga_game_schedules(schedule_key) ON DELETE CASCADE,
				subject_id TEXT NOT NULL,
				PRIMARY KEY(schedule_key, subject_id)
			) STRICT;

			CREATE TABLE oga_game_schedule_causes (
				schedule_key INTEGER NOT NULL REFERENCES oga_game_schedules(schedule_key) ON DELETE CASCADE,
				cause_id TEXT NOT NULL,
				PRIMARY KEY(schedule_key, cause_id)
			) STRICT;

			CREATE TABLE oga_schedule_clocks (
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				current_tick TEXT NOT NULL,
				revision TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				PRIMARY KEY(owner_id, timeline_id, generation)
			) STRICT;

			CREATE TABLE oga_schedule_advances (
				advance_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(advance_key > 0),
				advance_id TEXT NOT NULL,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				from_tick TEXT NOT NULL,
				to_tick TEXT NOT NULL,
				recorded_at TEXT NOT NULL,
				source_fingerprint TEXT NOT NULL CHECK(length(source_fingerprint) = 64),
				UNIQUE(owner_id, timeline_id, generation, advance_id)
			) STRICT;

			CREATE INDEX oga_schedule_advances_partition_key
			ON oga_schedule_advances(owner_id, timeline_id, generation, advance_key);

			CREATE TABLE oga_scheduled_occurrences (
				occurrence_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(occurrence_key > 0),
				signal_id TEXT NOT NULL,
				schedule_key INTEGER NOT NULL REFERENCES oga_game_schedules(schedule_key),
				advance_key INTEGER NOT NULL REFERENCES oga_schedule_advances(advance_key),
				ordinal TEXT NOT NULL,
				due_tick TEXT NOT NULL,
				status TEXT NOT NULL CHECK(status IN ('pending', 'completed')),
				revision TEXT NOT NULL,
				attempt TEXT NOT NULL,
				lease_token TEXT,
				lease_owner_id TEXT,
				lease_expires_at TEXT,
				completed_at TEXT,
				settled_lease_hash TEXT CHECK(settled_lease_hash IS NULL OR length(settled_lease_hash) = 64),
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				source_fingerprint TEXT NOT NULL CHECK(length(source_fingerprint) = 64),
				CHECK(
					(lease_token IS NULL AND lease_owner_id IS NULL AND lease_expires_at IS NULL)
					OR (lease_token IS NOT NULL AND lease_owner_id IS NOT NULL AND lease_expires_at IS NOT NULL)
				),
				CHECK(
					(status = 'pending' AND completed_at IS NULL AND settled_lease_hash IS NULL)
					OR
					(status = 'completed' AND completed_at IS NOT NULL AND settled_lease_hash IS NOT NULL
					 AND lease_token IS NULL AND lease_owner_id IS NULL AND lease_expires_at IS NULL)
				),
				UNIQUE(schedule_key, ordinal),
				UNIQUE(signal_id)
			) STRICT;

			CREATE INDEX oga_scheduled_occurrences_status_due
			ON oga_scheduled_occurrences(status, length(due_tick), due_tick, occurrence_key);

			CREATE INDEX oga_scheduled_occurrences_advance
			ON oga_scheduled_occurrences(advance_key, occurrence_key);
		`,
	},
	{
		version: 2,
		name: "schedule-authority-retirement-v2",
		sql: `
			ALTER TABLE oga_scheduled_occurrences RENAME TO oga_scheduled_occurrences_previous;
			CREATE TABLE oga_scheduled_occurrences (
				occurrence_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(occurrence_key > 0),
				signal_id TEXT NOT NULL,
				schedule_key INTEGER NOT NULL REFERENCES oga_game_schedules(schedule_key),
				advance_key INTEGER NOT NULL REFERENCES oga_schedule_advances(advance_key),
				ordinal TEXT NOT NULL,
				due_tick TEXT NOT NULL,
				status TEXT NOT NULL CHECK(status IN ('pending', 'completed', 'cancelled')),
				revision TEXT NOT NULL,
				attempt TEXT NOT NULL,
				lease_token TEXT,
				lease_owner_id TEXT,
				lease_expires_at TEXT,
				completed_at TEXT,
				cancelled_at TEXT,
				settled_lease_hash TEXT CHECK(settled_lease_hash IS NULL OR length(settled_lease_hash) = 64),
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				source_fingerprint TEXT NOT NULL CHECK(length(source_fingerprint) = 64),
				CHECK(
					(lease_token IS NULL AND lease_owner_id IS NULL AND lease_expires_at IS NULL)
					OR (lease_token IS NOT NULL AND lease_owner_id IS NOT NULL AND lease_expires_at IS NOT NULL)
				),
				CHECK(
					(status = 'pending' AND completed_at IS NULL AND cancelled_at IS NULL AND settled_lease_hash IS NULL)
					OR
					(status = 'completed' AND completed_at IS NOT NULL AND cancelled_at IS NULL AND settled_lease_hash IS NOT NULL
					 AND lease_token IS NULL AND lease_owner_id IS NULL AND lease_expires_at IS NULL)
					OR (status = 'cancelled' AND cancelled_at IS NOT NULL AND completed_at IS NULL AND settled_lease_hash IS NULL
					 AND lease_token IS NULL AND lease_owner_id IS NULL AND lease_expires_at IS NULL)
				),
				UNIQUE(schedule_key, ordinal),
				UNIQUE(signal_id)
			) STRICT;

			INSERT INTO oga_scheduled_occurrences (occurrence_key, signal_id, schedule_key, advance_key, ordinal, due_tick, status, revision, attempt, lease_token, lease_owner_id, lease_expires_at, completed_at, settled_lease_hash, created_at, updated_at, source_fingerprint)
			SELECT occurrence_key, signal_id, schedule_key, advance_key, ordinal, due_tick, status, revision, attempt, lease_token, lease_owner_id, lease_expires_at, completed_at, settled_lease_hash, created_at, updated_at, source_fingerprint FROM oga_scheduled_occurrences_previous;
			UPDATE sqlite_sequence SET seq = MAX(seq, COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'oga_scheduled_occurrences_previous'), 0)) WHERE name = 'oga_scheduled_occurrences';
			INSERT INTO sqlite_sequence(name, seq)
			SELECT 'oga_scheduled_occurrences', seq FROM sqlite_sequence WHERE name = 'oga_scheduled_occurrences_previous'
			AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'oga_scheduled_occurrences');
			DROP TABLE oga_scheduled_occurrences_previous;
			CREATE INDEX oga_scheduled_occurrences_status_due ON oga_scheduled_occurrences(status, length(due_tick), due_tick, occurrence_key);
			CREATE INDEX oga_scheduled_occurrences_advance ON oga_scheduled_occurrences(advance_key, occurrence_key);
			CREATE TABLE oga_schedule_authority_retirements (
				owner_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation TEXT NOT NULL,
				session_id TEXT NOT NULL, actor_id TEXT NOT NULL, retirement_id TEXT NOT NULL UNIQUE,
				request_json TEXT NOT NULL CHECK(json_valid(request_json)),
				phase TEXT NOT NULL CHECK(phase IN ('fenced', 'finalized')),
				cancelled_schedule_count INTEGER NOT NULL CHECK(cancelled_schedule_count >= 0),
				cancelled_occurrence_count INTEGER NOT NULL CHECK(cancelled_occurrence_count >= 0),
				PRIMARY KEY(owner_id, timeline_id, generation, session_id, actor_id)
			) STRICT;
		`,
	},
];

interface ScheduleRow {
	schedule_key: bigint;
	schedule_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	session_id: string | null;
	actor_id: string | null;
	kind: string;
	payload_json: string;
	due_tick: string;
	interval_ticks: string | null;
	maximum_occurrences: string | null;
	status: string;
	revision: string;
	occurrences: string;
	next_due_tick: string | null;
	created_at: string;
	updated_at: string;
	source_fingerprint: string;
}

interface ClockRow {
	owner_id: string;
	timeline_id: string;
	generation: string;
	current_tick: string;
	revision: string;
	updated_at: string;
}

interface AdvanceRow {
	advance_key: bigint;
	advance_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	from_tick: string;
	to_tick: string;
	recorded_at: string;
	source_fingerprint: string;
}

interface OccurrenceRow {
	occurrence_key: bigint;
	signal_id: string;
	schedule_key: bigint;
	advance_key: bigint;
	ordinal: string;
	due_tick: string;
	status: string;
	revision: string;
	attempt: string;
	lease_token: string | null;
	lease_owner_id: string | null;
	lease_expires_at: string | null;
	completed_at: string | null;
	cancelled_at: string | null;
	settled_lease_hash: string | null;
	created_at: string;
	updated_at: string;
	source_fingerprint: string;
}

interface AdvanceCursor {
	readonly schedule: GameScheduleRecord;
	dueTick: string;
	ordinal: string;
	completed: boolean;
}

interface PlannedOccurrence {
	readonly schedule: GameScheduleRecord;
	readonly ordinal: string;
	readonly dueTick: string;
	readonly signalId: string;
}

interface RetirementRow {
	request_json: string;
	phase: "fenced" | "finalized";
	cancelled_schedule_count: number;
	cancelled_occurrence_count: number;
}

export class SqliteScheduleStore implements ScheduleStore {
	readonly limits: ScheduleLimits;

	constructor(
		private readonly authority: SqliteAuthority,
		limits: ScheduleLimits,
		private readonly newLeaseToken: () => string = randomUUID,
		private readonly newSignalId: () => string = randomUUID,
	) {
		validateScheduleLimits(limits);
		this.limits = Object.freeze({ ...limits });
		this.authority.install("schedules", SCHEDULE_MIGRATIONS);
	}

	async beginAuthorityRetirement(input: WorkAuthorityRetirementRequest): Promise<ScheduleAuthorityRetirementRecord> {
		const request = normalizeScheduleAuthorityRetirement(input, this.limits);
		return this.authority.write((database) => this.beginAuthorityRetirementInTransaction(database, request));
	}

	beginAuthorityRetirementInTransaction(
		database: Database.Database,
		input: WorkAuthorityRetirementRequest,
	): ScheduleAuthorityRetirementRecord {
		return this.retireAuthorityInTransaction(database, input, "fenced");
	}

	async finalizeAuthorityRetirement(input: WorkAuthorityRetirementRequest): Promise<ScheduleAuthorityRetirementRecord> {
		const request = normalizeScheduleAuthorityRetirement(input, this.limits);
		return this.authority.write((database) => this.finalizeAuthorityRetirementInTransaction(database, request));
	}

	finalizeAuthorityRetirementInTransaction(
		database: Database.Database,
		input: WorkAuthorityRetirementRequest,
	): ScheduleAuthorityRetirementRecord {
		return this.retireAuthorityInTransaction(database, input, "finalized");
	}

	async readAuthorityRetirement(input: WorkAuthorityLane): Promise<ScheduleAuthorityRetirementRecord | undefined> {
		const lane = normalizeScheduleAuthorityLane(input, this.limits);
		return this.authority.read((database) => {
			const row = findRetirement(database, lane);
			return row === undefined ? undefined : this.hydrateRetirement(row);
		});
	}

	private retireAuthorityInTransaction(
		database: Database.Database,
		input: WorkAuthorityRetirementRequest,
		phase: "fenced" | "finalized",
	): ScheduleAuthorityRetirementRecord {
		requireSqliteWriteTransaction(this.authority, database);
		const request = normalizeScheduleAuthorityRetirement(input, this.limits);
		const serialized = canonicalizeJsonValue(request);
		const existingRow = findRetirement(database, request.partition);
		const operation = database
			.prepare("SELECT request_json FROM oga_schedule_authority_retirements WHERE retirement_id = ?")
			.get(request.retirementId) as Pick<RetirementRow, "request_json"> | undefined;
		if (
			(operation !== undefined && operation.request_json !== serialized) ||
			(existingRow !== undefined && existingRow.request_json !== serialized)
		)
			throw new WorkAuthorityRetirementConflictError();
		const existing = existingRow === undefined ? undefined : this.hydrateRetirement(existingRow);
		if (existing !== undefined && (phase === "fenced" || existing.phase === "finalized")) return existing;
		if (phase === "finalized" && existing === undefined) throw new WorkAuthorityRetirementConflictError();
		const lane = request.partition;
		const laneValues = [lane.ownerId, lane.timelineId, lane.generation, lane.sessionId, lane.actorId];
		const scheduleStatement = database.prepare(`SELECT * FROM oga_game_schedules
			WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?
			AND status = 'active' ORDER BY schedule_key LIMIT ?`);
		scheduleStatement.safeIntegers(true);
		const schedules = scheduleStatement.all(
			...laneValues,
			BigInt(this.limits.maxActiveSchedulesPerTimelineGeneration) + 1n,
		) as ScheduleRow[];
		if (schedules.length > this.limits.maxActiveSchedulesPerTimelineGeneration)
			throw new ScheduleCapacityError("active_schedules");
		const occurrenceStatement = database.prepare(`SELECT occurrence.* FROM oga_scheduled_occurrences occurrence
			JOIN oga_game_schedules schedule ON schedule.schedule_key = occurrence.schedule_key
			WHERE schedule.owner_id = ? AND schedule.timeline_id = ? AND schedule.generation = ? AND schedule.session_id = ? AND schedule.actor_id = ?
			AND occurrence.status = 'pending' AND (? = 'finalized' OR occurrence.lease_token IS NULL)
			ORDER BY occurrence.occurrence_key LIMIT ?`);
		occurrenceStatement.safeIntegers(true);
		const occurrences = occurrenceStatement.all(
			...laneValues,
			phase,
			BigInt(this.limits.maxPendingOccurrencesPerTimelineGeneration) + 1n,
		) as OccurrenceRow[];
		if (occurrences.length > this.limits.maxPendingOccurrencesPerTimelineGeneration)
			throw new ScheduleCapacityError("pending_occurrences");
		const scheduleUpdates = schedules.map((row) => {
			const record = this.hydrateSchedule(database, row);
			return {
				record,
				revision: incrementScheduleDecimal(record.revision, this.limits.maxCounterDigits, "revision"),
				updatedAt: laterScheduleTimestamp(record.updatedAt, request.recordedAt),
			};
		});
		const occurrenceUpdates = occurrences.map((row) => {
			const record = this.hydrateOccurrence(database, row);
			return {
				record,
				revision: incrementScheduleDecimal(record.revision, this.limits.maxCounterDigits, "revision"),
				updatedAt: laterScheduleTimestamp(record.updatedAt, request.recordedAt),
			};
		});
		const cancelledScheduleCount = (existing?.cancelledScheduleCount ?? 0) + schedules.length;
		const cancelledOccurrenceCount = (existing?.cancelledOccurrenceCount ?? 0) + occurrences.length;
		if (!Number.isSafeInteger(cancelledScheduleCount) || !Number.isSafeInteger(cancelledOccurrenceCount))
			throw new ScheduleValidationError("invalid_schedule_retirement_count");
		const cancelSchedule = database.prepare(
			"UPDATE oga_game_schedules SET status = 'cancelled', revision = ?, next_due_tick = NULL, updated_at = ? WHERE schedule_key = ? AND status = 'active' AND revision = ?",
		);
		for (const { record, revision, updatedAt } of scheduleUpdates) {
			if (cancelSchedule.run(revision, updatedAt, record.scheduleKey, record.revision).changes !== 1)
				throw new ScheduleRevisionConflictError();
		}
		const cancelOccurrence =
			database.prepare(`UPDATE oga_scheduled_occurrences SET status = 'cancelled', revision = ?, lease_token = NULL, lease_owner_id = NULL, lease_expires_at = NULL, cancelled_at = ?, updated_at = ?
			WHERE occurrence_key = ? AND status = 'pending' AND revision = ?`);
		for (const { record, revision, updatedAt } of occurrenceUpdates) {
			if (cancelOccurrence.run(revision, updatedAt, updatedAt, record.occurrenceKey, record.revision).changes !== 1)
				throw new ScheduleOccurrenceLeaseConflictError();
		}
		if (existing === undefined) {
			database
				.prepare(`INSERT INTO oga_schedule_authority_retirements(owner_id, timeline_id, generation, session_id, actor_id, retirement_id, request_json, phase, cancelled_schedule_count, cancelled_occurrence_count)
				VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
				.run(...laneValues, request.retirementId, serialized, phase, cancelledScheduleCount, cancelledOccurrenceCount);
		} else {
			database
				.prepare(
					"UPDATE oga_schedule_authority_retirements SET phase = ?, cancelled_schedule_count = ?, cancelled_occurrence_count = ? WHERE retirement_id = ?",
				)
				.run(phase, cancelledScheduleCount, cancelledOccurrenceCount, request.retirementId);
		}
		return { ...request, phase, cancelledScheduleCount, cancelledOccurrenceCount };
	}

	private hydrateRetirement(row: RetirementRow): ScheduleAuthorityRetirementRecord {
		const request = normalizeScheduleAuthorityRetirement(
			JSON.parse(row.request_json) as WorkAuthorityRetirementRequest,
			this.limits,
		);
		if (
			canonicalizeJsonValue(request) !== row.request_json ||
			(row.phase !== "fenced" && row.phase !== "finalized") ||
			!Number.isSafeInteger(row.cancelled_schedule_count) ||
			row.cancelled_schedule_count < 0 ||
			!Number.isSafeInteger(row.cancelled_occurrence_count) ||
			row.cancelled_occurrence_count < 0
		)
			throw new ScheduleValidationError("corrupt_schedule_retirement");
		return {
			...request,
			phase: row.phase,
			cancelledScheduleCount: row.cancelled_schedule_count,
			cancelledOccurrenceCount: row.cancelled_occurrence_count,
		};
	}

	private assertAuthorityOpen(database: Database.Database, partition: SchedulePartition): void {
		if (partition.sessionId === undefined || partition.actorId === undefined) return;
		const lane: WorkAuthorityLane = {
			ownerId: partition.ownerId,
			timelineId: partition.timelineId,
			generation: partition.generation,
			sessionId: partition.sessionId,
			actorId: partition.actorId,
		};
		if (findRetirement(database, lane) !== undefined) throw new WorkAuthorityRetiredError(lane);
	}

	async create(input: GameScheduleDraft, maxActive: number): Promise<GameScheduleRecord> {
		validateSchedulePageLimit(
			maxActive,
			this.limits.maxActiveSchedulesPerTimelineGeneration,
			"invalid_active_schedule_limit",
		);
		const schedule = normalizeGameSchedule(input, this.limits);
		const fingerprint = digest(scheduleFingerprint(schedule));
		return this.authority.write((database) => {
			this.assertAuthorityOpen(database, schedule.partition);
			const existing = findScheduleRow(database, schedule);
			if (existing !== undefined) {
				if (existing.source_fingerprint !== fingerprint) throw new ScheduleConflictError();
				return this.hydrateSchedule(database, existing);
			}
			const clock = findClockRow(database, schedule.partition);
			if (clock !== undefined) {
				this.validateClock(clock);
				if (compareScheduleDecimals(schedule.dueTime.tick, clock.current_tick) <= 0) {
					throw new ScheduleMissedOccurrenceError();
				}
			}
			const count = countActiveSchedules(database, schedule.partition);
			if (count >= BigInt(maxActive)) throw new ScheduleCapacityError("active_schedules");
			const result = database
				.prepare(`
					INSERT INTO oga_game_schedules(
						schedule_id, owner_id, timeline_id, generation, session_id, actor_id,
						kind, payload_json, due_tick, interval_ticks, maximum_occurrences,
						status, revision, occurrences, next_due_tick, created_at, updated_at, source_fingerprint
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'active', '1', '0', ?, ?, ?, ?)
				`)
				.run(
					schedule.scheduleId,
					schedule.partition.ownerId,
					schedule.partition.timelineId,
					schedule.partition.generation,
					schedule.partition.sessionId ?? null,
					schedule.partition.actorId ?? null,
					schedule.kind,
					JSON.stringify(schedule.payload),
					schedule.dueTime.tick,
					schedule.intervalTicks ?? null,
					schedule.maximumOccurrences ?? null,
					schedule.dueTime.tick,
					schedule.recordedAt,
					schedule.recordedAt,
					fingerprint,
				);
			const scheduleKey = BigInt(result.lastInsertRowid);
			insertIdentifiers(database, "oga_game_schedule_subjects", "subject_id", scheduleKey, schedule.subjectIds);
			insertIdentifiers(database, "oga_game_schedule_causes", "cause_id", scheduleKey, schedule.causeIds);
			const row = findScheduleRowByKey(database, scheduleKey);
			if (row === undefined) throw new Error("Schedule insert did not remain readable.");
			return this.hydrateSchedule(database, row);
		});
	}

	async getSchedule(key: GameScheduleKey): Promise<GameScheduleRecord | undefined> {
		key = validateGameScheduleKey(key, this.limits);
		return this.authority.snapshot((database) => {
			const row = findScheduleRow(database, key);
			return row === undefined ? undefined : this.hydrateSchedule(database, row);
		});
	}

	async getScheduleByKey(key: GameScheduleSequenceKey): Promise<GameScheduleRecord | undefined> {
		key = validateGameScheduleSequenceKey(key, this.limits);
		return this.authority.snapshot((database) => {
			const row = findScheduleRowBySequenceKey(database, key);
			return row === undefined ? undefined : this.hydrateSchedule(database, row);
		});
	}

	async listSchedules(request: ScheduleListRequest): Promise<SchedulePage> {
		request = normalizeScheduleListRequest(request, this.limits);
		const partition = request;
		const statuses = request.statuses;
		return this.authority.snapshot((database) => {
			const selectedStatuses = statuses ?? [];
			const statusSql =
				selectedStatuses.length === 0 ? "" : `AND status IN (${selectedStatuses.map(() => "?").join(", ")})`;
			const statement = database.prepare(`
				SELECT * FROM oga_game_schedules
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND (? IS NULL OR session_id = ?)
				  AND (? IS NULL OR actor_id = ?)
				  AND schedule_key > ?
				  ${statusSql}
				ORDER BY schedule_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				partition.sessionId ?? null,
				partition.sessionId ?? null,
				partition.actorId ?? null,
				partition.actorId ?? null,
				request.afterScheduleKey ?? 0n,
				...selectedStatuses,
				BigInt(request.limit + 1),
			) as ScheduleRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => this.hydrateSchedule(database, row));
			const next = hasMore ? records.at(-1)?.scheduleKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterScheduleKey: next }) };
		});
	}

	async cancelSchedule(
		key: GameScheduleKey,
		expectedRevision: string,
		recordedAt: string,
	): Promise<GameScheduleRecord> {
		key = validateGameScheduleKey(key, this.limits);
		validateScheduleDecimal(expectedRevision, "revision", this.limits.maxCounterDigits);
		validateScheduleTimestamp(recordedAt);
		return this.authority.write((database) => {
			const current = findScheduleRow(database, key);
			if (current === undefined) throw new ScheduleNotFoundError();
			const hydrated = this.hydrateSchedule(database, current);
			this.assertAuthorityOpen(database, hydrated.partition);
			if (hydrated.revision !== expectedRevision) throw new ScheduleRevisionConflictError();
			if (hydrated.status !== "active") throw new ScheduleTerminalError();
			const nextRevision = incrementScheduleDecimal(hydrated.revision, this.limits.maxCounterDigits, "revision");
			const result = database
				.prepare(`
					UPDATE oga_game_schedules
					SET status = 'cancelled', revision = ?, next_due_tick = NULL, updated_at = ?
					WHERE schedule_key = ? AND status = 'active' AND revision = ?
				`)
				.run(
					nextRevision,
					laterScheduleTimestamp(hydrated.updatedAt, recordedAt),
					hydrated.scheduleKey,
					expectedRevision,
				);
			if (result.changes !== 1) throw new ScheduleRevisionConflictError();
			const updated = findScheduleRowByKey(database, hydrated.scheduleKey);
			if (updated === undefined) throw new ScheduleNotFoundError();
			return this.hydrateSchedule(database, updated);
		});
	}

	async advance(input: ScheduleAdvanceDraft, maxGenerated: number, maxPending: number): Promise<ScheduleAdvanceResult> {
		validateSchedulePageLimit(maxGenerated, this.limits.maxOccurrencesPerAdvance, "invalid_schedule_advance_limit");
		validateSchedulePageLimit(
			maxPending,
			this.limits.maxPendingOccurrencesPerTimelineGeneration,
			"invalid_pending_occurrence_limit",
		);
		const advance = normalizeScheduleAdvance(input, this.limits);
		const fingerprint = digest(scheduleAdvanceFingerprint(advance));
		const prepared = this.authority.read((database) =>
			findAdvanceRow(database, advance) === undefined
				? this.planAdvance(database, advance, maxGenerated, maxPending)
				: undefined,
		);
		const signalIds =
			prepared?.planned.map(() => {
				const signalId = this.newSignalId();
				validateScheduleIdentifier(signalId, this.limits.maxIdentifierBytes, "signal_id");
				return signalId;
			}) ?? [];
		return this.authority.write((database) => {
			const existing = findAdvanceRow(database, advance);
			if (existing !== undefined) {
				if (existing.source_fingerprint !== fingerprint) throw new ScheduleAdvanceConflictError();
				const hydrated = this.hydrateAdvance(existing);
				const occurrences = findOccurrenceRowsByAdvance(database, existing.advance_key).map((row) =>
					this.hydrateOccurrence(database, row),
				);
				if (occurrences.length > maxGenerated) throw new ScheduleAdvanceOverflowError();
				return {
					advance: hydrated,
					occurrences,
				};
			}

			if (prepared !== undefined) {
				for (const item of prepared.planned) this.assertAuthorityOpen(database, item.schedule.partition);
			}
			const {
				clock,
				planned: currentPlan,
				finalBySchedule,
			} = this.planAdvance(database, advance, maxGenerated, maxPending);
			if (currentPlan.length !== signalIds.length) throw new ScheduleRevisionConflictError();
			const planned: PlannedOccurrence[] = currentPlan.map((item, index) => ({
				...item,
				signalId: signalIds[index] as string,
			}));

			const advanceInsert = database
				.prepare(`
					INSERT INTO oga_schedule_advances(
						advance_id, owner_id, timeline_id, generation, from_tick, to_tick, recorded_at, source_fingerprint
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
				`)
				.run(
					advance.advanceId,
					advance.partition.ownerId,
					advance.partition.timelineId,
					advance.partition.generation,
					advance.fromExclusive.tick,
					advance.toInclusive.tick,
					advance.recordedAt,
					fingerprint,
				);
			const advanceKey = BigInt(advanceInsert.lastInsertRowid);
			const occurrenceRows: OccurrenceRow[] = [];
			for (const item of planned) {
				const provisional = occurrenceFromPlan(item, advance, 1n, advanceKey);
				const occurrenceFingerprint = digest(scheduledOccurrenceFingerprint(provisional));
				const inserted = database
					.prepare(`
						INSERT INTO oga_scheduled_occurrences(
							signal_id, schedule_key, advance_key, ordinal, due_tick, status, revision, attempt,
							created_at, updated_at, source_fingerprint
						) VALUES (?, ?, ?, ?, ?, 'pending', '1', '0', ?, ?, ?)
					`)
					.run(
						item.signalId,
						item.schedule.scheduleKey,
						advanceKey,
						item.ordinal,
						item.dueTick,
						advance.recordedAt,
						advance.recordedAt,
						occurrenceFingerprint,
					);
				const row = findOccurrenceRowByKey(database, BigInt(inserted.lastInsertRowid));
				if (row === undefined) throw new Error("Occurrence insert did not remain readable.");
				occurrenceRows.push(row);
			}

			for (const cursor of finalBySchedule.values()) {
				const nextRevision = incrementScheduleDecimal(
					cursor.schedule.revision,
					this.limits.maxCounterDigits,
					"revision",
				);
				const result = database
					.prepare(`
						UPDATE oga_game_schedules
						SET status = ?, revision = ?, occurrences = ?, next_due_tick = ?, updated_at = ?
						WHERE schedule_key = ? AND status = 'active' AND revision = ?
					`)
					.run(
						cursor.completed ? "completed" : "active",
						nextRevision,
						cursor.ordinal,
						cursor.completed ? null : cursor.dueTick,
						laterScheduleTimestamp(cursor.schedule.updatedAt, advance.recordedAt),
						cursor.schedule.scheduleKey,
						cursor.schedule.revision,
					);
				if (result.changes !== 1) throw new ScheduleRevisionConflictError();
			}

			if (clock === undefined) {
				database
					.prepare(`
						INSERT INTO oga_schedule_clocks(owner_id, timeline_id, generation, current_tick, revision, updated_at)
						VALUES (?, ?, ?, ?, '1', ?)
					`)
					.run(
						advance.partition.ownerId,
						advance.partition.timelineId,
						advance.partition.generation,
						advance.toInclusive.tick,
						advance.recordedAt,
					);
			} else {
				const nextRevision = incrementScheduleDecimal(clock.revision, this.limits.maxCounterDigits, "clock_revision");
				const result = database
					.prepare(`
						UPDATE oga_schedule_clocks SET current_tick = ?, revision = ?, updated_at = ?
						WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND current_tick = ? AND revision = ?
					`)
					.run(
						advance.toInclusive.tick,
						nextRevision,
						laterScheduleTimestamp(clock.updated_at, advance.recordedAt),
						advance.partition.ownerId,
						advance.partition.timelineId,
						advance.partition.generation,
						advance.fromExclusive.tick,
						clock.revision,
					);
				if (result.changes !== 1) throw new ScheduleClockConflictError();
			}

			const advanceRow = findAdvanceRowByKey(database, advanceKey);
			if (advanceRow === undefined) throw new Error("Advance insert did not remain readable.");
			return {
				advance: this.hydrateAdvance(advanceRow),
				occurrences: occurrenceRows.map((row) => this.hydrateOccurrence(database, row)),
			};
		});
	}

	private planAdvance(
		database: Database.Database,
		advance: ScheduleAdvanceDraft,
		maxGenerated: number,
		maxPending: number,
	) {
		const clock = findClockRow(database, advance.partition);
		if (clock !== undefined) {
			this.validateClock(clock);
			if (clock.current_tick !== advance.fromExclusive.tick) throw new ScheduleClockConflictError();
		}
		const missed = findFirstDueScheduleRow(database, advance.partition, advance.fromExclusive.tick);
		if (missed !== undefined) {
			this.hydrateSchedule(database, missed);
			throw new ScheduleMissedOccurrenceError();
		}
		const pending = countPendingOccurrences(database, advance.partition);
		const dueRows = findDueScheduleRows(database, advance.partition, advance.toInclusive.tick);
		const cursors = new TinyQueue<AdvanceCursor>(
			dueRows.map((row) => {
				const schedule = this.hydrateSchedule(database, row);
				if (schedule.nextDueTick === undefined) throw new ScheduleValidationError("corrupt_schedule_state");
				return {
					schedule,
					dueTick: schedule.nextDueTick,
					ordinal: schedule.occurrences,
					completed: false,
				};
			}),
			compareCursors,
		);
		const planned: Omit<PlannedOccurrence, "signalId">[] = [];
		const finalBySchedule = new Map<bigint, AdvanceCursor>();
		while (cursors.length > 0) {
			const cursor = cursors.pop();
			if (cursor === undefined) break;
			if (planned.length >= maxGenerated) throw new ScheduleAdvanceOverflowError();
			if (pending + BigInt(planned.length) >= BigInt(maxPending)) {
				throw new ScheduleCapacityError("pending_occurrences");
			}
			cursor.ordinal = incrementScheduleDecimal(cursor.ordinal, this.limits.maxCounterDigits, "occurrences");
			planned.push({
				schedule: cursor.schedule,
				ordinal: cursor.ordinal,
				dueTick: cursor.dueTick,
			});
			cursor.completed =
				cursor.schedule.maximumOccurrences !== undefined &&
				compareScheduleDecimals(cursor.ordinal, cursor.schedule.maximumOccurrences) >= 0;
			if (!cursor.completed && cursor.schedule.intervalTicks !== undefined) {
				cursor.dueTick = addScheduleDecimals(cursor.dueTick, cursor.schedule.intervalTicks, this.limits.maxTickDigits);
				if (compareScheduleDecimals(cursor.dueTick, advance.toInclusive.tick) <= 0) cursors.push(cursor);
			} else {
				cursor.completed = true;
			}
			finalBySchedule.set(cursor.schedule.scheduleKey, cursor);
		}

		return { clock, planned, finalBySchedule };
	}

	async getOccurrence(key: ScheduledOccurrenceSequenceKey): Promise<ScheduledOccurrenceRecord | undefined> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		return this.authority.snapshot((database) => {
			const row = findOccurrenceRowBySequenceKey(database, key);
			return row === undefined ? undefined : this.hydrateOccurrence(database, row);
		});
	}

	async listOccurrences(request: ScheduledOccurrenceListRequest): Promise<ScheduledOccurrencePage> {
		request = normalizeScheduledOccurrenceListRequest(request, this.limits);
		const partition = request;
		const statuses = request.statuses;
		return this.authority.snapshot((database) => {
			const selectedStatuses = statuses ?? [];
			const statusSql =
				selectedStatuses.length === 0 ? "" : `AND occurrence.status IN (${selectedStatuses.map(() => "?").join(", ")})`;
			const statement = database.prepare(`
				SELECT occurrence.* FROM oga_scheduled_occurrences occurrence
				JOIN oga_game_schedules schedule ON schedule.schedule_key = occurrence.schedule_key
				WHERE schedule.owner_id = ? AND schedule.timeline_id = ? AND schedule.generation = ?
				  AND (? IS NULL OR schedule.session_id = ?)
				  AND (? IS NULL OR schedule.actor_id = ?)
				  AND occurrence.occurrence_key > ?
				  ${statusSql}
				ORDER BY occurrence.occurrence_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				partition.sessionId ?? null,
				partition.sessionId ?? null,
				partition.actorId ?? null,
				partition.actorId ?? null,
				request.afterOccurrenceKey ?? 0n,
				...selectedStatuses,
				BigInt(request.limit + 1),
			) as OccurrenceRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => this.hydrateOccurrence(database, row));
			const next = hasMore ? records.at(-1)?.occurrenceKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterOccurrenceKey: next }) };
		});
	}

	async claimOccurrences(request: ScheduleOccurrenceClaimRequest): Promise<readonly ScheduledOccurrenceDelivery[]> {
		const claim = normalizeScheduleClaim(request, this.limits);
		const selected = this.authority.read((database) => {
			this.assertAuthorityOpen(database, claim);
			return this.selectClaimRows(database, claim);
		});
		const partitions = this.authority.read((database) =>
			selected.map((row) => this.hydrateOccurrence(database, row).partition),
		);
		const tokens = selected.map(() => {
			const token = this.newLeaseToken();
			validateScheduleIdentifier(token, this.limits.maxIdentifierBytes, "lease_token");
			return token;
		});
		return this.authority.write((database) => {
			this.assertAuthorityOpen(database, claim);
			for (const partition of partitions) this.assertAuthorityOpen(database, partition);
			const rows = this.selectClaimRows(database, claim);
			if (
				rows.length !== selected.length ||
				rows.some(
					(row, index) =>
						row.occurrence_key !== selected[index]?.occurrence_key || row.revision !== selected[index]?.revision,
				)
			)
				throw new ScheduleOccurrenceLeaseConflictError();
			return rows.map((row, index) => {
				const current = this.hydrateOccurrence(database, row);
				const token = tokens[index] as string;
				this.assertAuthorityOpen(database, current.partition);
				const nextRevision = incrementScheduleDecimal(current.revision, this.limits.maxCounterDigits, "revision");
				const nextAttempt = incrementScheduleDecimal(current.attempt, this.limits.maxCounterDigits, "attempt");
				const result = database
					.prepare(`
						UPDATE oga_scheduled_occurrences
						SET revision = ?, attempt = ?, lease_token = ?, lease_owner_id = ?, lease_expires_at = ?, updated_at = ?
						WHERE occurrence_key = ? AND status = 'pending' AND revision = ?
						  AND (lease_token IS NULL OR lease_expires_at <= ?)
					`)
					.run(
						nextRevision,
						nextAttempt,
						token,
						claim.leaseOwnerId,
						claim.leaseExpiresAt,
						laterScheduleTimestamp(current.updatedAt, claim.operationalNow),
						current.occurrenceKey,
						current.revision,
						claim.operationalNow,
					);
				if (result.changes !== 1) throw new ScheduleOccurrenceLeaseConflictError();
				const updated = findOccurrenceRowByKey(database, current.occurrenceKey);
				if (updated === undefined) throw new ScheduleOccurrenceNotFoundError();
				return {
					occurrence: this.hydrateOccurrence(database, updated),
					leaseToken: token,
					leaseOwnerId: claim.leaseOwnerId,
					leaseExpiresAt: claim.leaseExpiresAt,
				};
			});
		});
	}

	private selectClaimRows(database: Database.Database, claim: ScheduleOccurrenceClaimRequest): OccurrenceRow[] {
		const statement = database.prepare(`
			SELECT occurrence.* FROM oga_scheduled_occurrences occurrence
			JOIN oga_game_schedules schedule ON schedule.schedule_key = occurrence.schedule_key
			WHERE schedule.owner_id = ? AND schedule.timeline_id = ? AND schedule.generation = ?
			  AND (? IS NULL OR schedule.session_id = ?)
			  AND (? IS NULL OR schedule.actor_id = ?)
			  AND occurrence.status = 'pending'
			  AND NOT EXISTS (SELECT 1 FROM oga_schedule_authority_retirements retirement
			    WHERE retirement.owner_id = schedule.owner_id AND retirement.timeline_id = schedule.timeline_id AND retirement.generation = schedule.generation
			    AND retirement.session_id = schedule.session_id AND retirement.actor_id = schedule.actor_id)
			  AND (occurrence.lease_token IS NULL OR occurrence.lease_expires_at <= ?)
			ORDER BY length(occurrence.due_tick), occurrence.due_tick, occurrence.occurrence_key
			LIMIT ?
		`);
		statement.safeIntegers(true);
		const rows = statement.all(
			claim.ownerId,
			claim.timelineId,
			claim.generation,
			claim.sessionId ?? null,
			claim.sessionId ?? null,
			claim.actorId ?? null,
			claim.actorId ?? null,
			claim.operationalNow,
			BigInt(claim.limit),
		) as OccurrenceRow[];
		return rows;
	}

	async completeOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		validateScheduleIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateScheduleTimestamp(recordedAt);
		return this.authority.write((database) => {
			const row = findOccurrenceRowBySequenceKey(database, key);
			if (row === undefined) throw new ScheduleOccurrenceNotFoundError();
			const current = this.hydrateOccurrence(database, row);
			const tokenHash = digest(leaseToken);
			if (current.status === "completed") {
				if (row.settled_lease_hash !== tokenHash) throw new ScheduleOccurrenceLeaseConflictError();
				return current;
			}
			this.assertAuthorityOpen(database, current.partition);
			if (row.lease_token !== leaseToken || row.lease_expires_at === null || row.lease_expires_at <= recordedAt) {
				throw new ScheduleOccurrenceLeaseConflictError();
			}
			const nextRevision = incrementScheduleDecimal(current.revision, this.limits.maxCounterDigits, "revision");
			const completedAt = laterScheduleTimestamp(current.updatedAt, recordedAt);
			const result = database
				.prepare(`
					UPDATE oga_scheduled_occurrences
					SET status = 'completed', revision = ?, lease_token = NULL, lease_owner_id = NULL,
					    lease_expires_at = NULL, completed_at = ?, settled_lease_hash = ?, updated_at = ?
					WHERE occurrence_key = ? AND status = 'pending' AND revision = ? AND lease_token = ?
				`)
				.run(nextRevision, completedAt, tokenHash, completedAt, current.occurrenceKey, current.revision, leaseToken);
			if (result.changes !== 1) throw new ScheduleOccurrenceLeaseConflictError();
			const updated = findOccurrenceRowByKey(database, current.occurrenceKey);
			if (updated === undefined) throw new ScheduleOccurrenceNotFoundError();
			return this.hydrateOccurrence(database, updated);
		});
	}

	async abandonOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		validateScheduleIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateScheduleTimestamp(recordedAt);
		return this.authority.write((database) => {
			const row = findOccurrenceRowBySequenceKey(database, key);
			if (row === undefined) throw new ScheduleOccurrenceNotFoundError();
			const current = this.hydrateOccurrence(database, row);
			this.assertAuthorityOpen(database, current.partition);
			if (
				current.status !== "pending" ||
				row.lease_token !== leaseToken ||
				row.lease_expires_at === null ||
				row.lease_expires_at <= recordedAt
			) {
				throw new ScheduleOccurrenceLeaseConflictError();
			}
			const nextRevision = incrementScheduleDecimal(current.revision, this.limits.maxCounterDigits, "revision");
			const result = database
				.prepare(`
					UPDATE oga_scheduled_occurrences
					SET revision = ?, lease_token = NULL, lease_owner_id = NULL, lease_expires_at = NULL, updated_at = ?
					WHERE occurrence_key = ? AND status = 'pending' AND revision = ? AND lease_token = ?
				`)
				.run(
					nextRevision,
					laterScheduleTimestamp(current.updatedAt, recordedAt),
					current.occurrenceKey,
					current.revision,
					leaseToken,
				);
			if (result.changes !== 1) throw new ScheduleOccurrenceLeaseConflictError();
			const updated = findOccurrenceRowByKey(database, current.occurrenceKey);
			if (updated === undefined) throw new ScheduleOccurrenceNotFoundError();
			return this.hydrateOccurrence(database, updated);
		});
	}

	async purgeTerminalOccurrences(
		input: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(input, this.limits);
		const partition = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_occurrence_purge_limit");
		return this.authority.write((database) => {
			const rows = selectKeys(
				database,
				`SELECT occurrence.occurrence_key AS item_key
				 FROM oga_scheduled_occurrences occurrence
				 JOIN oga_game_schedules schedule ON schedule.schedule_key = occurrence.schedule_key
				 WHERE schedule.owner_id = ? AND schedule.timeline_id = ? AND schedule.generation = ?
				   AND occurrence.status IN ('completed', 'cancelled') AND occurrence.updated_at < ?
				 ORDER BY occurrence.occurrence_key LIMIT ?`,
				[partition.ownerId, partition.timelineId, partition.generation, beforeRecordedAt, BigInt(limit)],
			);
			const remove = database.prepare("DELETE FROM oga_scheduled_occurrences WHERE occurrence_key = ?");
			for (const key of rows) remove.run(key);
			return rows;
		});
	}

	async purgeTerminalSchedules(
		input: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(input, this.limits);
		const partition = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_schedule_purge_limit");
		return this.authority.write((database) => {
			const rows = selectKeys(
				database,
				`SELECT schedule.schedule_key AS item_key FROM oga_game_schedules schedule
				 WHERE schedule.owner_id = ? AND schedule.timeline_id = ? AND schedule.generation = ?
				   AND schedule.status IN ('completed', 'cancelled') AND schedule.updated_at < ?
				   AND NOT EXISTS (
				     SELECT 1 FROM oga_scheduled_occurrences occurrence
				     WHERE occurrence.schedule_key = schedule.schedule_key
				   )
				 ORDER BY schedule.schedule_key LIMIT ?`,
				[partition.ownerId, partition.timelineId, partition.generation, beforeRecordedAt, BigInt(limit)],
			);
			const remove = database.prepare("DELETE FROM oga_game_schedules WHERE schedule_key = ?");
			for (const key of rows) remove.run(key);
			return rows;
		});
	}

	async purgeAdvances(
		input: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(input, this.limits);
		const partition = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_schedule_advance_purge_limit");
		return this.authority.write((database) => {
			const rows = selectKeys(
				database,
				`SELECT advance.advance_key AS item_key FROM oga_schedule_advances advance
				 WHERE advance.owner_id = ? AND advance.timeline_id = ? AND advance.generation = ?
				   AND advance.recorded_at < ?
				   AND NOT EXISTS (
				     SELECT 1 FROM oga_scheduled_occurrences occurrence
				     WHERE occurrence.advance_key = advance.advance_key
				   )
				 ORDER BY advance.advance_key LIMIT ?`,
				[partition.ownerId, partition.timelineId, partition.generation, beforeRecordedAt, BigInt(limit)],
			);
			const remove = database.prepare("DELETE FROM oga_schedule_advances WHERE advance_key = ?");
			for (const key of rows) remove.run(key);
			return rows;
		});
	}

	private hydrateSchedule(database: Database.Database, row: ScheduleRow): GameScheduleRecord {
		validatePositiveScheduleKey(row.schedule_key, "schedule_key");
		const partition = normalizeSchedulePartition(
			{
				ownerId: row.owner_id,
				timelineId: row.timeline_id,
				generation: row.generation,
				...(row.session_id === null ? {} : { sessionId: row.session_id }),
				...(row.actor_id === null ? {} : { actorId: row.actor_id }),
			},
			this.limits,
		);
		let payload: unknown;
		try {
			payload = JSON.parse(row.payload_json);
		} catch {
			throw new ScheduleValidationError("corrupt_schedule_payload");
		}
		const normalized = normalizeGameSchedule(
			{
				scheduleId: row.schedule_id,
				partition,
				kind: row.kind,
				payload: payload as never,
				dueTime: { timelineId: partition.timelineId, generation: partition.generation, tick: row.due_tick },
				...(row.interval_ticks === null ? {} : { intervalTicks: row.interval_ticks }),
				...(row.maximum_occurrences === null ? {} : { maximumOccurrences: row.maximum_occurrences }),
				subjectIds: readIdentifiers(database, "oga_game_schedule_subjects", "subject_id", row.schedule_key),
				causeIds: readIdentifiers(database, "oga_game_schedule_causes", "cause_id", row.schedule_key),
				recordedAt: row.created_at,
			},
			this.limits,
		);
		if (digest(scheduleFingerprint(normalized)) !== row.source_fingerprint) {
			throw new ScheduleValidationError("corrupt_schedule_index");
		}
		validateScheduleStatus(row.status);
		validateScheduleDecimal(row.revision, "revision", this.limits.maxCounterDigits);
		validateScheduleDecimal(row.occurrences, "occurrences", this.limits.maxCounterDigits);
		if (row.revision === "0") throw new ScheduleValidationError("corrupt_schedule_revision");
		validateScheduleTimestamp(row.updated_at);
		if (row.updated_at < row.created_at) throw new ScheduleValidationError("corrupt_schedule_chronology");
		if (row.next_due_tick !== null) {
			validateScheduleDecimal(row.next_due_tick, "next_due_tick", this.limits.maxTickDigits);
		}
		if ((row.status === "active") !== (row.next_due_tick !== null)) {
			throw new ScheduleValidationError("corrupt_schedule_state");
		}
		if (
			normalized.maximumOccurrences !== undefined &&
			compareScheduleDecimals(row.occurrences, normalized.maximumOccurrences) > 0
		) {
			throw new ScheduleValidationError("corrupt_schedule_occurrences");
		}
		if (row.status === "active") {
			if (
				normalized.maximumOccurrences !== undefined &&
				compareScheduleDecimals(row.occurrences, normalized.maximumOccurrences) >= 0
			) {
				throw new ScheduleValidationError("corrupt_schedule_state");
			}
			const expected = scheduleDueAtOrdinal(
				normalized,
				incrementScheduleDecimal(row.occurrences, this.limits.maxCounterDigits, "occurrences"),
				this.limits.maxTickDigits,
			);
			if (row.next_due_tick !== expected) throw new ScheduleValidationError("corrupt_schedule_next_due");
		} else if (row.status === "completed") {
			if (
				normalized.maximumOccurrences === undefined ||
				compareScheduleDecimals(row.occurrences, normalized.maximumOccurrences) !== 0
			) {
				throw new ScheduleValidationError("corrupt_schedule_state");
			}
		}
		return {
			...normalized,
			scheduleKey: row.schedule_key,
			status: row.status,
			revision: row.revision,
			occurrences: row.occurrences,
			...(row.next_due_tick === null ? {} : { nextDueTick: row.next_due_tick }),
			createdAt: row.created_at,
			updatedAt: row.updated_at,
		};
	}

	private hydrateAdvance(row: AdvanceRow): ScheduleAdvanceRecord {
		validatePositiveScheduleKey(row.advance_key, "advance_key");
		const normalized = normalizeScheduleAdvance(
			{
				advanceId: row.advance_id,
				partition: { ownerId: row.owner_id, timelineId: row.timeline_id, generation: row.generation },
				fromExclusive: { timelineId: row.timeline_id, generation: row.generation, tick: row.from_tick },
				toInclusive: { timelineId: row.timeline_id, generation: row.generation, tick: row.to_tick },
				recordedAt: row.recorded_at,
			},
			this.limits,
		);
		if (digest(scheduleAdvanceFingerprint(normalized)) !== row.source_fingerprint) {
			throw new ScheduleValidationError("corrupt_schedule_advance_index");
		}
		return { ...normalized, advanceKey: row.advance_key };
	}

	private hydrateOccurrence(database: Database.Database, row: OccurrenceRow): ScheduledOccurrenceRecord {
		validatePositiveScheduleKey(row.occurrence_key, "occurrence_key");
		validatePositiveScheduleKey(row.schedule_key, "schedule_key");
		validatePositiveScheduleKey(row.advance_key, "advance_key");
		const scheduleRow = findScheduleRowByKey(database, row.schedule_key);
		const advanceRow = findAdvanceRowByKey(database, row.advance_key);
		if (scheduleRow === undefined || advanceRow === undefined) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_reference");
		}
		const schedule = this.hydrateSchedule(database, scheduleRow);
		const advance = this.hydrateAdvance(advanceRow);
		if (!sameScheduleGeneration(schedule.partition, advance.partition)) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_partition");
		}
		validateScheduleIdentifier(row.signal_id, this.limits.maxIdentifierBytes, "signal_id");
		validateScheduleDecimal(row.ordinal, "occurrence_ordinal", this.limits.maxCounterDigits);
		if (row.ordinal === "0" || compareScheduleDecimals(row.ordinal, schedule.occurrences) > 0) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_ordinal");
		}
		validateScheduleDecimal(row.due_tick, "occurrence_due_tick", this.limits.maxTickDigits);
		if (row.due_tick !== scheduleDueAtOrdinal(schedule, row.ordinal, this.limits.maxTickDigits)) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_due");
		}
		if (
			compareScheduleDecimals(row.due_tick, advance.fromExclusive.tick) <= 0 ||
			compareScheduleDecimals(row.due_tick, advance.toInclusive.tick) > 0
		) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_advance");
		}
		validateScheduledOccurrenceStatus(row.status);
		validateScheduleDecimal(row.revision, "occurrence_revision", this.limits.maxCounterDigits);
		validateScheduleDecimal(row.attempt, "occurrence_attempt", this.limits.maxCounterDigits);
		if (row.revision === "0") throw new ScheduleValidationError("corrupt_scheduled_occurrence_revision");
		validateScheduleTimestamp(row.created_at);
		validateScheduleTimestamp(row.updated_at);
		if (row.updated_at < row.created_at) throw new ScheduleValidationError("corrupt_scheduled_occurrence_chronology");
		const leaseFields = [row.lease_token, row.lease_owner_id, row.lease_expires_at];
		if (leaseFields.some((value) => value === null) && leaseFields.some((value) => value !== null)) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_lease");
		}
		if (row.lease_token !== null && row.lease_owner_id !== null && row.lease_expires_at !== null) {
			validateScheduleIdentifier(row.lease_token, this.limits.maxIdentifierBytes, "lease_token");
			validateScheduleIdentifier(row.lease_owner_id, this.limits.maxIdentifierBytes, "lease_owner_id");
			validateScheduleTimestamp(row.lease_expires_at);
			if (row.attempt === "0") throw new ScheduleValidationError("corrupt_scheduled_occurrence_attempt");
		}
		if (row.completed_at !== null) validateScheduleTimestamp(row.completed_at);
		if (row.cancelled_at !== null) validateScheduleTimestamp(row.cancelled_at);
		if (row.status === "completed") {
			if (
				row.completed_at === null ||
				row.cancelled_at !== null ||
				row.settled_lease_hash === null ||
				row.completed_at !== row.updated_at ||
				row.lease_token !== null ||
				row.attempt === "0" ||
				!SHA256.test(row.settled_lease_hash)
			) {
				throw new ScheduleValidationError("corrupt_scheduled_occurrence_completion");
			}
		} else if (row.status === "cancelled") {
			if (
				row.cancelled_at !== row.updated_at ||
				row.completed_at !== null ||
				row.settled_lease_hash !== null ||
				row.lease_token !== null
			) {
				throw new ScheduleValidationError("corrupt_scheduled_occurrence_cancellation");
			}
		} else if (row.completed_at !== null || row.cancelled_at !== null || row.settled_lease_hash !== null) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_completion");
		}
		if (compareScheduleDecimals(row.revision, row.attempt) <= 0) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_revision");
		}
		if (row.created_at !== advance.recordedAt) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_chronology");
		}
		const occurrence: ScheduledOccurrenceRecord = {
			occurrenceKey: row.occurrence_key,
			signalId: row.signal_id,
			scheduleKey: schedule.scheduleKey,
			scheduleId: schedule.scheduleId,
			ordinal: row.ordinal,
			advanceId: advance.advanceId,
			partition: schedule.partition,
			kind: schedule.kind,
			payload: schedule.payload,
			dueTime: { ...schedule.dueTime, tick: row.due_tick },
			subjectIds: schedule.subjectIds,
			causeIds: schedule.causeIds,
			status: row.status,
			revision: row.revision,
			attempt: row.attempt,
			createdAt: row.created_at,
			updatedAt: row.updated_at,
			...(row.completed_at === null ? {} : { completedAt: row.completed_at }),
			...(row.cancelled_at === null ? {} : { cancelledAt: row.cancelled_at }),
		};
		if (digest(scheduledOccurrenceFingerprint(occurrence)) !== row.source_fingerprint) {
			throw new ScheduleValidationError("corrupt_scheduled_occurrence_index");
		}
		return occurrence;
	}

	private validateClock(row: ClockRow): void {
		normalizeSchedulePartition(
			{ ownerId: row.owner_id, timelineId: row.timeline_id, generation: row.generation },
			this.limits,
		);
		validateScheduleDecimal(row.current_tick, "clock_tick", this.limits.maxTickDigits);
		validateScheduleDecimal(row.revision, "clock_revision", this.limits.maxCounterDigits);
		if (row.revision === "0") throw new ScheduleValidationError("corrupt_schedule_clock_revision");
		validateScheduleTimestamp(row.updated_at);
	}
}

function findScheduleRow(
	database: Database.Database,
	key: GameScheduleKey | GameScheduleDraft,
): ScheduleRow | undefined {
	const partition = "partition" in key ? key.partition : key;
	const statement = database.prepare(`
		SELECT * FROM oga_game_schedules
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND schedule_id = ? LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(partition.ownerId, partition.timelineId, partition.generation, key.scheduleId) as
		| ScheduleRow
		| undefined;
}

function findScheduleRowByKey(database: Database.Database, scheduleKey: bigint): ScheduleRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_game_schedules WHERE schedule_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(scheduleKey) as ScheduleRow | undefined;
}

function findScheduleRowBySequenceKey(
	database: Database.Database,
	key: GameScheduleSequenceKey,
): ScheduleRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_game_schedules
		WHERE schedule_key = ? AND owner_id = ? AND timeline_id = ? AND generation = ? LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(key.scheduleKey, key.ownerId, key.timelineId, key.generation) as ScheduleRow | undefined;
}

function findClockRow(
	database: Database.Database,
	partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
): ClockRow | undefined {
	return database
		.prepare(`
			SELECT * FROM oga_schedule_clocks
			WHERE owner_id = ? AND timeline_id = ? AND generation = ? LIMIT 1
		`)
		.get(partition.ownerId, partition.timelineId, partition.generation) as ClockRow | undefined;
}

function findAdvanceRow(database: Database.Database, advance: ScheduleAdvanceDraft): AdvanceRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_schedule_advances
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND advance_id = ? LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(
		advance.partition.ownerId,
		advance.partition.timelineId,
		advance.partition.generation,
		advance.advanceId,
	) as AdvanceRow | undefined;
}

function findAdvanceRowByKey(database: Database.Database, advanceKey: bigint): AdvanceRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_schedule_advances WHERE advance_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(advanceKey) as AdvanceRow | undefined;
}

function findOccurrenceRowByKey(database: Database.Database, occurrenceKey: bigint): OccurrenceRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_scheduled_occurrences WHERE occurrence_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(occurrenceKey) as OccurrenceRow | undefined;
}

function findOccurrenceRowBySequenceKey(
	database: Database.Database,
	key: ScheduledOccurrenceSequenceKey,
): OccurrenceRow | undefined {
	const statement = database.prepare(`
		SELECT occurrence.* FROM oga_scheduled_occurrences occurrence
		JOIN oga_game_schedules schedule ON schedule.schedule_key = occurrence.schedule_key
		WHERE occurrence.occurrence_key = ?
		  AND schedule.owner_id = ? AND schedule.timeline_id = ? AND schedule.generation = ?
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(key.occurrenceKey, key.ownerId, key.timelineId, key.generation) as OccurrenceRow | undefined;
}

function findOccurrenceRowsByAdvance(database: Database.Database, advanceKey: bigint): OccurrenceRow[] {
	const statement = database.prepare(`
		SELECT * FROM oga_scheduled_occurrences
		WHERE advance_key = ?
		ORDER BY length(due_tick), due_tick, occurrence_key
	`);
	statement.safeIntegers(true);
	return statement.all(advanceKey) as OccurrenceRow[];
}

function findFirstDueScheduleRow(
	database: Database.Database,
	partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
	throughTick: string,
): ScheduleRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_game_schedules
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND status = 'active'
		  AND (length(next_due_tick) < length(?) OR (length(next_due_tick) = length(?) AND next_due_tick <= ?))
		ORDER BY length(next_due_tick), next_due_tick, schedule_key LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(
		partition.ownerId,
		partition.timelineId,
		partition.generation,
		throughTick,
		throughTick,
		throughTick,
	) as ScheduleRow | undefined;
}

function findDueScheduleRows(
	database: Database.Database,
	partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
	throughTick: string,
): ScheduleRow[] {
	const statement = database.prepare(`
		SELECT * FROM oga_game_schedules
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND status = 'active'
		  AND (length(next_due_tick) < length(?) OR (length(next_due_tick) = length(?) AND next_due_tick <= ?))
		ORDER BY length(next_due_tick), next_due_tick, schedule_key
	`);
	statement.safeIntegers(true);
	return statement.all(
		partition.ownerId,
		partition.timelineId,
		partition.generation,
		throughTick,
		throughTick,
		throughTick,
	) as ScheduleRow[];
}

function countActiveSchedules(
	database: Database.Database,
	partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
): bigint {
	const statement = database.prepare(`
		SELECT count(*) FROM oga_game_schedules
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND status = 'active'
	`);
	statement.safeIntegers(true);
	return statement.pluck().get(partition.ownerId, partition.timelineId, partition.generation) as bigint;
}

function countPendingOccurrences(
	database: Database.Database,
	partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
): bigint {
	const statement = database.prepare(`
		SELECT count(*) FROM oga_scheduled_occurrences occurrence
		JOIN oga_game_schedules schedule ON schedule.schedule_key = occurrence.schedule_key
		WHERE schedule.owner_id = ? AND schedule.timeline_id = ? AND schedule.generation = ?
		  AND occurrence.status = 'pending'
	`);
	statement.safeIntegers(true);
	return statement.pluck().get(partition.ownerId, partition.timelineId, partition.generation) as bigint;
}

function insertIdentifiers(
	database: Database.Database,
	table: string,
	column: string,
	scheduleKey: bigint,
	values: readonly string[],
): void {
	const statement = database.prepare(`INSERT INTO ${table}(schedule_key, ${column}) VALUES (?, ?)`);
	for (const value of values) statement.run(scheduleKey, value);
}

function readIdentifiers(database: Database.Database, table: string, column: string, scheduleKey: bigint): string[] {
	return database
		.prepare(`SELECT ${column} FROM ${table} WHERE schedule_key = ? ORDER BY ${column}`)
		.pluck()
		.all(scheduleKey) as string[];
}

function occurrenceFromPlan(
	item: PlannedOccurrence,
	advance: ScheduleAdvanceDraft,
	occurrenceKey: bigint,
	_advanceKey: bigint,
): ScheduledOccurrenceRecord {
	return {
		occurrenceKey,
		signalId: item.signalId,
		scheduleKey: item.schedule.scheduleKey,
		scheduleId: item.schedule.scheduleId,
		ordinal: item.ordinal,
		advanceId: advance.advanceId,
		partition: item.schedule.partition,
		kind: item.schedule.kind,
		payload: item.schedule.payload,
		dueTime: { ...item.schedule.dueTime, tick: item.dueTick },
		subjectIds: item.schedule.subjectIds,
		causeIds: item.schedule.causeIds,
		status: "pending",
		revision: "1",
		attempt: "0",
		createdAt: advance.recordedAt,
		updatedAt: advance.recordedAt,
	};
}

function scheduleDueAtOrdinal(schedule: GameScheduleDraft, ordinal: string, maximumDigits: number): string {
	if (ordinal === "0") throw new ScheduleValidationError("invalid_schedule_occurrence_ordinal");
	if (ordinal === "1") return schedule.dueTime.tick;
	if (schedule.intervalTicks === undefined) throw new ScheduleValidationError("corrupt_schedule_occurrence_ordinal");
	const due = BigInt(schedule.dueTime.tick) + BigInt(schedule.intervalTicks) * (BigInt(ordinal) - 1n);
	const result = due.toString();
	if (result.length > maximumDigits) throw new ScheduleValidationError("schedule_tick_overflow");
	return result;
}

function compareCursors(left: AdvanceCursor, right: AdvanceCursor): number {
	const tick = compareScheduleDecimals(left.dueTick, right.dueTick);
	return tick === 0
		? left.schedule.scheduleKey < right.schedule.scheduleKey
			? -1
			: left.schedule.scheduleKey > right.schedule.scheduleKey
				? 1
				: 0
		: tick;
}

function selectKeys(database: Database.Database, sql: string, values: readonly unknown[]): bigint[] {
	const statement = database.prepare(sql);
	statement.safeIntegers(true);
	return statement.pluck().all(...values) as bigint[];
}

function digest(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

function findRetirement(database: Database.Database, partition: WorkAuthorityLane): RetirementRow | undefined {
	return database
		.prepare(`SELECT request_json, phase, cancelled_schedule_count, cancelled_occurrence_count FROM oga_schedule_authority_retirements
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?`)
		.get(partition.ownerId, partition.timelineId, partition.generation, partition.sessionId, partition.actorId) as
		| RetirementRow
		| undefined;
}
