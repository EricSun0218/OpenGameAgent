import { randomBytes } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type { GameInput, GameMoment, GameSessionKey, JsonValue } from "@opengameagent/protocol";

export interface GameSignal {
	id: string;
	session: GameSessionKey;
	kind: string;
	payload: JsonValue;
	moment: GameMoment;
	subjects?: readonly string[];
	causes?: readonly string[];
}

export interface GameSchedule {
	id: string;
	session: GameSessionKey;
	kind: string;
	payload: JsonValue;
	due: GameMoment;
	intervalTicks?: number;
	maximumOccurrences?: number;
	subjects?: readonly string[];
	causes?: readonly string[];
}

export type GameScheduleStatus = "active" | "completed" | "cancelled";

export interface GameScheduleState {
	schedule: GameSchedule;
	status: GameScheduleStatus;
	revision: number;
	occurrences: number;
	nextDue?: GameMoment;
	createdAt: number;
	updatedAt: number;
}

export interface GameScheduledOccurrence extends GameSignal {
	scheduleId: string;
	occurrence: number;
}

export interface GameScheduledOccurrenceDelivery {
	occurrence: GameScheduledOccurrence;
	leaseToken: string;
	attempt: number;
	leaseExpiresAt: number;
}

export interface GameTimeAdvance {
	id: string;
	session: GameSessionKey;
	fromExclusive: GameMoment;
	toInclusive: GameMoment;
}

export interface SqliteGameTimeSchedulerOptions {
	maximumActiveSchedules?: number;
	maximumPendingOccurrences?: number;
	maximumPayloadBytes?: number;
	terminalScheduleRetention?: number;
	completedOccurrenceRetention?: number;
	advanceRetention?: number;
}

interface ScheduleRow {
	schedule_id: string;
	revision: number;
	status: GameScheduleStatus;
	kind: string;
	payload_json: string;
	due_tick: number;
	due_calendar: string | null;
	due_phase: string | null;
	interval_ticks: number | null;
	maximum_occurrences: number | null;
	occurrences: number;
	next_due_tick: number | null;
	subjects_json: string;
	causes_json: string;
	created_at: number;
	updated_at: number;
}

interface OccurrenceRow {
	occurrence_id: string;
	schedule_id: string;
	occurrence_number: number;
	kind: string;
	payload_json: string;
	due_tick: number;
	due_calendar: string | null;
	due_phase: string | null;
	subjects_json: string;
	causes_json: string;
	attempt: number;
	lease_token: string | null;
	lease_expires_at: number | null;
}

const sessionColumns = "world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id";
const sessionWhere =
	"world_id=? AND save_id=? AND timeline_id=? AND generation=? AND owner_id=? AND session_id=? AND actor_id=?";

function clone<T>(value: T): T {
	return structuredClone(value);
}

function validateId(value: string, name: string, maximum = 192): void {
	if (!value || value.length > maximum) throw new TypeError(`${name} must be a bounded non-empty identifier.`);
	for (const character of value) {
		const code = character.codePointAt(0) ?? 0;
		if (code < 32 || code === 127) throw new TypeError(`${name} contains a control character.`);
	}
}

function validateMoment(moment: GameMoment, name: string): void {
	if (!Number.isSafeInteger(moment.tick) || moment.tick < 0)
		throw new TypeError(`${name} tick must be a non-negative safe integer.`);
	if (moment.calendar !== undefined && moment.calendar.length > 4096)
		throw new RangeError(`${name} calendar is too large.`);
	if (moment.phase !== undefined) validateId(moment.phase, `${name} phase`, 256);
}

function validateSession(session: GameSessionKey): void {
	for (const [name, value] of Object.entries(session)) {
		if (name === "generation") {
			if (!Number.isSafeInteger(value) || (value as number) < 0) throw new TypeError("Session generation is invalid.");
		} else validateId(value as string, `Session ${name}`, 1024);
	}
}

function normalizeIds(values: readonly string[] | undefined, name: string): readonly string[] {
	const unique = [...new Set(values ?? [])];
	if (unique.length > 256) throw new RangeError(`${name} has too many identifiers.`);
	for (const value of unique) validateId(value, name, 1024);
	return unique.sort((left, right) => left.localeCompare(right));
}

function serializedPayload(payload: JsonValue, maximumBytes: number): string {
	const serialized = JSON.stringify(payload);
	if (Buffer.byteLength(serialized, "utf8") > maximumBytes) throw new RangeError("Schedule payload is too large.");
	return serialized;
}

function validateSchedule(schedule: GameSchedule, maximumPayloadBytes: number): GameSchedule {
	validateId(schedule.id, "Schedule id");
	validateSession(schedule.session);
	validateId(schedule.kind, "Schedule kind", 256);
	validateMoment(schedule.due, "Schedule due");
	serializedPayload(schedule.payload, maximumPayloadBytes);
	if (
		schedule.intervalTicks !== undefined &&
		(!Number.isSafeInteger(schedule.intervalTicks) || schedule.intervalTicks < 1)
	)
		throw new RangeError("intervalTicks must be a positive safe integer.");
	if (
		schedule.maximumOccurrences !== undefined &&
		(!Number.isSafeInteger(schedule.maximumOccurrences) || schedule.maximumOccurrences < 1)
	)
		throw new RangeError("maximumOccurrences must be a positive safe integer.");
	if (
		schedule.intervalTicks === undefined &&
		schedule.maximumOccurrences !== undefined &&
		schedule.maximumOccurrences !== 1
	)
		throw new RangeError("A one-shot schedule can have at most one occurrence.");
	return {
		...clone(schedule),
		subjects: normalizeIds(schedule.subjects, "Schedule subject"),
		causes: normalizeIds(schedule.causes, "Schedule cause"),
	};
}

function sessionValues(session: GameSessionKey): readonly (string | number)[] {
	return [
		session.worldId,
		session.saveId,
		session.timelineId,
		session.generation,
		session.ownerId,
		session.sessionId,
		session.actorId,
	];
}

function moment(tick: number, calendar: string | null, phase: string | null): GameMoment {
	return {
		tick,
		...(calendar === null ? {} : { calendar }),
		...(phase === null ? {} : { phase }),
	};
}

function occurrenceId(scheduleId: string, occurrence: number): string {
	return `${scheduleId}:occurrence:${occurrence}`;
}

function sameSchedule(left: GameSchedule, right: GameSchedule): boolean {
	return JSON.stringify(left) === JSON.stringify(right);
}

export function gameSignalToInput(signal: GameSignal): GameInput {
	return {
		id: signal.id,
		type: signal.kind,
		session: clone(signal.session),
		moment: clone(signal.moment),
		content: [{ type: "json", value: clone(signal.payload) }],
		context: {
			signal: {
				subjects: [...(signal.subjects ?? [])],
				causes: [...(signal.causes ?? [])],
			},
		},
	};
}

export class SqliteGameTimeScheduler implements Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumActiveSchedules: number;
	private readonly maximumPendingOccurrences: number;
	private readonly maximumPayloadBytes: number;
	private readonly terminalScheduleRetention: number;
	private readonly completedOccurrenceRetention: number;
	private readonly advanceRetention: number;
	private closed = false;

	constructor(path: string, options: SqliteGameTimeSchedulerOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumActiveSchedules = options.maximumActiveSchedules ?? 100_000;
		this.maximumPendingOccurrences = options.maximumPendingOccurrences ?? 1_000_000;
		this.maximumPayloadBytes = options.maximumPayloadBytes ?? 1024 * 1024;
		this.terminalScheduleRetention = options.terminalScheduleRetention ?? 10_000;
		this.completedOccurrenceRetention = options.completedOccurrenceRetention ?? 100_000;
		this.advanceRetention = options.advanceRetention ?? 100_000;
		for (const [name, value, minimum, maximum] of [
			["maximumActiveSchedules", this.maximumActiveSchedules, 1, 1_000_000],
			["maximumPendingOccurrences", this.maximumPendingOccurrences, 1, 10_000_000],
			["maximumPayloadBytes", this.maximumPayloadBytes, 1, 16 * 1024 * 1024],
			["terminalScheduleRetention", this.terminalScheduleRetention, 0, 1_000_000],
			["completedOccurrenceRetention", this.completedOccurrenceRetention, 0, 10_000_000],
			["advanceRetention", this.advanceRetention, 1, 10_000_000],
		] as const) {
			if (!Number.isInteger(value) || value < minimum || value > maximum) throw new RangeError(`${name} is invalid.`);
		}
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_schedules (
				world_id TEXT NOT NULL, save_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL, session_id TEXT NOT NULL, actor_id TEXT NOT NULL, schedule_id TEXT NOT NULL,
				revision INTEGER NOT NULL, status TEXT NOT NULL, kind TEXT NOT NULL, payload_json TEXT NOT NULL,
				due_tick INTEGER NOT NULL, due_calendar TEXT, due_phase TEXT, interval_ticks INTEGER,
				maximum_occurrences INTEGER, occurrences INTEGER NOT NULL, next_due_tick INTEGER,
				subjects_json TEXT NOT NULL, causes_json TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
				PRIMARY KEY (${sessionColumns}, schedule_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_schedules_due ON game_schedules(
				${sessionColumns}, status, next_due_tick, schedule_id);
			CREATE TABLE IF NOT EXISTS game_schedule_occurrences (
				world_id TEXT NOT NULL, save_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL, session_id TEXT NOT NULL, actor_id TEXT NOT NULL, occurrence_id TEXT NOT NULL,
				schedule_id TEXT NOT NULL, advance_id TEXT NOT NULL, occurrence_number INTEGER NOT NULL,
				kind TEXT NOT NULL, payload_json TEXT NOT NULL, due_tick INTEGER NOT NULL, due_calendar TEXT, due_phase TEXT,
				subjects_json TEXT NOT NULL, causes_json TEXT NOT NULL, attempt INTEGER NOT NULL DEFAULT 0,
				lease_token TEXT, lease_expires_at INTEGER, completed INTEGER NOT NULL DEFAULT 0,
				created_at INTEGER NOT NULL, completed_at INTEGER,
				PRIMARY KEY (${sessionColumns}, occurrence_id),
				UNIQUE (${sessionColumns}, schedule_id, occurrence_number)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_schedule_occurrences_pending ON game_schedule_occurrences(
				${sessionColumns}, completed, lease_expires_at, due_tick, schedule_id, occurrence_number);
			CREATE TABLE IF NOT EXISTS game_time_advances (
				world_id TEXT NOT NULL, save_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL, session_id TEXT NOT NULL, actor_id TEXT NOT NULL, advance_id TEXT NOT NULL,
				from_tick INTEGER NOT NULL, to_tick INTEGER NOT NULL, created_at INTEGER NOT NULL,
				PRIMARY KEY (${sessionColumns}, advance_id)
			) STRICT;
		`);
	}

	async schedule(scheduleValue: GameSchedule, signal?: AbortSignal): Promise<GameScheduleState> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const schedule = validateSchedule(scheduleValue, this.maximumPayloadBytes);
		const existing = this.readScheduleRow(schedule.session, schedule.id);
		if (existing) {
			const state = this.toState(schedule.session, existing);
			if (!sameSchedule(state.schedule, schedule)) throw new Error("A schedule id identifies different content.");
			return state;
		}
		const active = this.database
			.prepare(`SELECT COUNT(*) AS count FROM game_schedules WHERE ${sessionWhere} AND status='active'`)
			.get(...sessionValues(schedule.session)) as { count: number };
		if (active.count >= this.maximumActiveSchedules) throw new Error("Game-time schedule capacity is exhausted.");
		const now = Date.now();
		this.database
			.prepare(`INSERT INTO game_schedules (
				${sessionColumns},schedule_id,revision,status,kind,payload_json,due_tick,due_calendar,due_phase,
				interval_ticks,maximum_occurrences,occurrences,next_due_tick,subjects_json,causes_json,created_at,updated_at
			) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
			.run(
				...sessionValues(schedule.session),
				schedule.id,
				1,
				"active",
				schedule.kind,
				JSON.stringify(schedule.payload),
				schedule.due.tick,
				schedule.due.calendar ?? null,
				schedule.due.phase ?? null,
				schedule.intervalTicks ?? null,
				schedule.maximumOccurrences ?? null,
				0,
				schedule.due.tick,
				JSON.stringify(schedule.subjects ?? []),
				JSON.stringify(schedule.causes ?? []),
				now,
				now,
			);
		return this.toState(schedule.session, this.requireScheduleRow(schedule.session, schedule.id));
	}

	async cancel(
		session: GameSessionKey,
		scheduleId: string,
		expectedRevision: number,
		signal?: AbortSignal,
	): Promise<GameScheduleState> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		validateId(scheduleId, "Schedule id");
		if (!Number.isSafeInteger(expectedRevision) || expectedRevision < 1)
			throw new RangeError("expectedRevision is invalid.");
		const result = this.database
			.prepare(`UPDATE game_schedules SET status='cancelled',revision=revision+1,next_due_tick=NULL,updated_at=?
				WHERE ${sessionWhere} AND schedule_id=? AND revision=? AND status='active'`)
			.run(Date.now(), ...sessionValues(session), scheduleId, expectedRevision);
		if (result.changes !== 1) throw new Error("Schedule revision conflict or schedule is not active.");
		this.prune(session);
		return this.toState(session, this.requireScheduleRow(session, scheduleId));
	}

	async list(session: GameSessionKey, signal?: AbortSignal): Promise<readonly GameScheduleState[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		const rows = this.database
			.prepare(`SELECT schedule_id,revision,status,kind,payload_json,due_tick,due_calendar,due_phase,interval_ticks,
				maximum_occurrences,occurrences,next_due_tick,subjects_json,causes_json,created_at,updated_at
				FROM game_schedules WHERE ${sessionWhere} ORDER BY created_at,schedule_id`)
			.all(...sessionValues(session)) as unknown as ScheduleRow[];
		return rows.map((row) => this.toState(session, row));
	}

	async advance(
		advance: GameTimeAdvance,
		maximumOccurrences: number,
		signal?: AbortSignal,
	): Promise<readonly GameScheduledOccurrence[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateId(advance.id, "Advance id");
		validateSession(advance.session);
		validateMoment(advance.fromExclusive, "Advance start");
		validateMoment(advance.toInclusive, "Advance end");
		if (advance.toInclusive.tick < advance.fromExclusive.tick)
			throw new RangeError("Game time cannot advance backwards.");
		if (!Number.isInteger(maximumOccurrences) || maximumOccurrences < 0 || maximumOccurrences > 1_000_000)
			throw new RangeError("maximumOccurrences must be 0..1000000.");
		this.database.exec("BEGIN IMMEDIATE");
		try {
			signal?.throwIfAborted();
			const prior = this.database
				.prepare(`SELECT from_tick,to_tick FROM game_time_advances WHERE ${sessionWhere} AND advance_id=?`)
				.get(...sessionValues(advance.session), advance.id) as { from_tick: number; to_tick: number } | undefined;
			if (prior) {
				if (prior.from_tick !== advance.fromExclusive.tick || prior.to_tick !== advance.toInclusive.tick)
					throw new Error("An advance id identifies different bounds.");
				const replay = this.readOccurrencesForAdvance(advance.session, advance.id);
				this.database.exec("COMMIT");
				return replay;
			}
			if (maximumOccurrences === 0) {
				const due = this.database
					.prepare(
						`SELECT 1 FROM game_schedules WHERE ${sessionWhere} AND status='active' AND next_due_tick<=? LIMIT 1`,
					)
					.get(...sessionValues(advance.session), advance.toInclusive.tick);
				if (due) throw new Error("The time advance produced more than the configured maximum occurrences.");
			}
			const pending = this.database
				.prepare(`SELECT COUNT(*) AS count FROM game_schedule_occurrences WHERE ${sessionWhere} AND completed=0`)
				.get(...sessionValues(advance.session)) as { count: number };
			const rows = this.database
				.prepare(`SELECT schedule_id,revision,status,kind,payload_json,due_tick,due_calendar,due_phase,interval_ticks,
					maximum_occurrences,occurrences,next_due_tick,subjects_json,causes_json,created_at,updated_at
					FROM game_schedules WHERE ${sessionWhere} AND status='active' AND next_due_tick<=?
					ORDER BY next_due_tick,schedule_id`)
				.all(...sessionValues(advance.session), advance.toInclusive.tick) as unknown as ScheduleRow[];
			const generated: Array<{ row: ScheduleRow; number: number; dueTick: number }> = [];
			for (const row of rows) {
				let dueTick = row.next_due_tick as number;
				let number = row.occurrences;
				while (dueTick <= advance.toInclusive.tick) {
					signal?.throwIfAborted();
					if (generated.length >= maximumOccurrences)
						throw new Error("The time advance produced more than the configured maximum occurrences.");
					if (pending.count + generated.length >= this.maximumPendingOccurrences)
						throw new Error("Pending scheduled occurrence capacity is exhausted.");
					number += 1;
					generated.push({ row, number, dueTick });
					if (row.interval_ticks === null || (row.maximum_occurrences !== null && number >= row.maximum_occurrences))
						break;
					const next = dueTick + row.interval_ticks;
					if (!Number.isSafeInteger(next)) throw new RangeError("A recurring schedule overflowed the game tick range.");
					dueTick = next;
				}
			}
			const now = Date.now();
			for (const item of generated) {
				this.database
					.prepare(`INSERT INTO game_schedule_occurrences (
						${sessionColumns},occurrence_id,schedule_id,advance_id,occurrence_number,kind,payload_json,due_tick,
						due_calendar,due_phase,subjects_json,causes_json,created_at
					) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
					.run(
						...sessionValues(advance.session),
						occurrenceId(item.row.schedule_id, item.number),
						item.row.schedule_id,
						advance.id,
						item.number,
						item.row.kind,
						item.row.payload_json,
						item.dueTick,
						item.dueTick === advance.toInclusive.tick
							? (advance.toInclusive.calendar ?? item.row.due_calendar)
							: item.row.due_calendar,
						item.dueTick === advance.toInclusive.tick
							? (advance.toInclusive.phase ?? item.row.due_phase)
							: item.row.due_phase,
						item.row.subjects_json,
						item.row.causes_json,
						now,
					);
			}
			const generatedBySchedule = new Map<string, Array<{ number: number; dueTick: number }>>();
			for (const item of generated) {
				const own = generatedBySchedule.get(item.row.schedule_id) ?? [];
				own.push({ number: item.number, dueTick: item.dueTick });
				generatedBySchedule.set(item.row.schedule_id, own);
			}
			for (const row of rows) {
				const own = generatedBySchedule.get(row.schedule_id) ?? [];
				if (own.length === 0) continue;
				const last = own[own.length - 1] as { number: number; dueTick: number };
				const completed =
					row.interval_ticks === null || (row.maximum_occurrences !== null && last.number >= row.maximum_occurrences);
				const nextDue = completed ? null : last.dueTick + (row.interval_ticks as number);
				if (nextDue !== null && !Number.isSafeInteger(nextDue))
					throw new RangeError("A schedule overflowed game time.");
				const updated = this.database
					.prepare(`UPDATE game_schedules SET revision=revision+1,status=?,occurrences=?,next_due_tick=?,updated_at=?
						WHERE ${sessionWhere} AND schedule_id=? AND revision=? AND status='active'`)
					.run(
						completed ? "completed" : "active",
						last.number,
						nextDue,
						now,
						...sessionValues(advance.session),
						row.schedule_id,
						row.revision,
					);
				if (updated.changes !== 1) throw new Error("Schedule revision changed during time advance.");
			}
			this.database
				.prepare(`INSERT INTO game_time_advances (${sessionColumns},advance_id,from_tick,to_tick,created_at)
					VALUES (?,?,?,?,?,?,?,?,?,?,?)`)
				.run(...sessionValues(advance.session), advance.id, advance.fromExclusive.tick, advance.toInclusive.tick, now);
			this.prune(advance.session);
			const result = this.readOccurrencesForAdvance(advance.session, advance.id);
			this.database.exec("COMMIT");
			return result;
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async claim(
		session: GameSessionKey,
		maximum: number,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<readonly GameScheduledOccurrenceDelivery[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		if (!Number.isInteger(maximum) || maximum < 1 || maximum > 256) throw new RangeError("maximum must be 1..256.");
		if (
			!Number.isSafeInteger(operationalNow) ||
			operationalNow < 0 ||
			!Number.isSafeInteger(leaseMilliseconds) ||
			leaseMilliseconds < 1 ||
			leaseMilliseconds > 86_400_000
		)
			throw new RangeError("Lease time is invalid.");
		const expires = operationalNow + leaseMilliseconds;
		if (!Number.isSafeInteger(expires)) throw new RangeError("Lease expiry is invalid.");
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const rows = this.database
				.prepare(`SELECT occurrence_id,schedule_id,occurrence_number,kind,payload_json,due_tick,due_calendar,due_phase,
					subjects_json,causes_json,attempt,lease_token,lease_expires_at
					FROM game_schedule_occurrences WHERE ${sessionWhere} AND completed=0
					AND (lease_token IS NULL OR lease_expires_at<=?) ORDER BY due_tick,schedule_id,occurrence_number LIMIT ?`)
				.all(...sessionValues(session), operationalNow, maximum) as unknown as OccurrenceRow[];
			const deliveries = rows.map((row) => {
				const leaseToken = randomBytes(24).toString("base64url");
				const updated = this.database
					.prepare(`UPDATE game_schedule_occurrences SET attempt=attempt+1,lease_token=?,lease_expires_at=?
						WHERE ${sessionWhere} AND occurrence_id=? AND completed=0
						AND (lease_token IS NULL OR lease_expires_at<=?)`)
					.run(leaseToken, expires, ...sessionValues(session), row.occurrence_id, operationalNow);
				if (updated.changes !== 1) throw new Error("Scheduled occurrence lease conflict.");
				return {
					occurrence: this.toOccurrence(session, row),
					leaseToken,
					attempt: row.attempt + 1,
					leaseExpiresAt: expires,
				};
			});
			this.database.exec("COMMIT");
			return deliveries;
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	complete(
		session: GameSessionKey,
		occurrenceIdValue: string,
		leaseToken: string,
		signal?: AbortSignal,
	): Promise<void> {
		return this.settle(session, occurrenceIdValue, leaseToken, true, signal);
	}

	abandon(session: GameSessionKey, occurrenceIdValue: string, leaseToken: string, signal?: AbortSignal): Promise<void> {
		return this.settle(session, occurrenceIdValue, leaseToken, false, signal);
	}

	async readPending(
		session: GameSessionKey,
		maximum = 256,
		signal?: AbortSignal,
	): Promise<readonly GameScheduledOccurrence[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		if (!Number.isInteger(maximum) || maximum < 1 || maximum > 4096) throw new RangeError("maximum must be 1..4096.");
		const rows = this.database
			.prepare(`SELECT occurrence_id,schedule_id,occurrence_number,kind,payload_json,due_tick,due_calendar,due_phase,
				subjects_json,causes_json,attempt,lease_token,lease_expires_at
				FROM game_schedule_occurrences WHERE ${sessionWhere} AND completed=0
				ORDER BY due_tick,schedule_id,occurrence_number LIMIT ?`)
			.all(...sessionValues(session), maximum) as unknown as OccurrenceRow[];
		return rows.map((row) => this.toOccurrence(session, row));
	}

	[Symbol.dispose](): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	private async settle(
		session: GameSessionKey,
		occurrenceIdValue: string,
		leaseToken: string,
		complete: boolean,
		signal?: AbortSignal,
	): Promise<void> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		validateId(occurrenceIdValue, "Occurrence id", 512);
		validateId(leaseToken, "Lease token", 256);
		const result = this.database
			.prepare(`UPDATE game_schedule_occurrences SET completed=?,completed_at=?,lease_token=NULL,lease_expires_at=NULL
				WHERE ${sessionWhere} AND occurrence_id=? AND completed=0 AND lease_token=?`)
			.run(complete ? 1 : 0, complete ? Date.now() : null, ...sessionValues(session), occurrenceIdValue, leaseToken);
		if (result.changes !== 1) throw new Error("Scheduled occurrence lease is stale or invalid.");
		if (complete) this.prune(session);
	}

	private readScheduleRow(session: GameSessionKey, scheduleId: string): ScheduleRow | undefined {
		return this.database
			.prepare(`SELECT schedule_id,revision,status,kind,payload_json,due_tick,due_calendar,due_phase,interval_ticks,
				maximum_occurrences,occurrences,next_due_tick,subjects_json,causes_json,created_at,updated_at
				FROM game_schedules WHERE ${sessionWhere} AND schedule_id=?`)
			.get(...sessionValues(session), scheduleId) as ScheduleRow | undefined;
	}

	private requireScheduleRow(session: GameSessionKey, scheduleId: string): ScheduleRow {
		const row = this.readScheduleRow(session, scheduleId);
		if (!row) throw new Error("Schedule disappeared during mutation.");
		return row;
	}

	private toState(session: GameSessionKey, row: ScheduleRow): GameScheduleState {
		try {
			const schedule: GameSchedule = {
				id: row.schedule_id,
				session: clone(session),
				kind: row.kind,
				payload: JSON.parse(row.payload_json) as JsonValue,
				due: moment(row.due_tick, row.due_calendar, row.due_phase),
				...(row.interval_ticks === null ? {} : { intervalTicks: row.interval_ticks }),
				...(row.maximum_occurrences === null ? {} : { maximumOccurrences: row.maximum_occurrences }),
				subjects: JSON.parse(row.subjects_json) as string[],
				causes: JSON.parse(row.causes_json) as string[],
			};
			validateSchedule(schedule, this.maximumPayloadBytes);
			return {
				schedule,
				status: row.status,
				revision: row.revision,
				occurrences: row.occurrences,
				...(row.next_due_tick === null ? {} : { nextDue: moment(row.next_due_tick, row.due_calendar, row.due_phase) }),
				createdAt: row.created_at,
				updatedAt: row.updated_at,
			};
		} catch {
			throw new Error("Stored game schedule is corrupt.");
		}
	}

	private toOccurrence(session: GameSessionKey, row: OccurrenceRow): GameScheduledOccurrence {
		try {
			return {
				id: row.occurrence_id,
				scheduleId: row.schedule_id,
				occurrence: row.occurrence_number,
				session: clone(session),
				kind: row.kind,
				payload: JSON.parse(row.payload_json) as JsonValue,
				moment: moment(row.due_tick, row.due_calendar, row.due_phase),
				subjects: JSON.parse(row.subjects_json) as string[],
				causes: JSON.parse(row.causes_json) as string[],
			};
		} catch {
			throw new Error("Stored scheduled occurrence is corrupt.");
		}
	}

	private readOccurrencesForAdvance(session: GameSessionKey, advanceId: string): readonly GameScheduledOccurrence[] {
		const rows = this.database
			.prepare(`SELECT occurrence_id,schedule_id,occurrence_number,kind,payload_json,due_tick,due_calendar,due_phase,
				subjects_json,causes_json,attempt,lease_token,lease_expires_at
				FROM game_schedule_occurrences WHERE ${sessionWhere} AND advance_id=?
				ORDER BY due_tick,schedule_id,occurrence_number`)
			.all(...sessionValues(session), advanceId) as unknown as OccurrenceRow[];
		return rows.map((row) => this.toOccurrence(session, row));
	}

	private prune(session: GameSessionKey): void {
		this.database
			.prepare(`DELETE FROM game_schedules WHERE rowid IN (
				SELECT rowid FROM game_schedules WHERE ${sessionWhere} AND status IN ('completed','cancelled')
				ORDER BY updated_at DESC,schedule_id DESC LIMIT -1 OFFSET ?)`)
			.run(...sessionValues(session), this.terminalScheduleRetention);
		this.database
			.prepare(`DELETE FROM game_schedule_occurrences WHERE rowid IN (
				SELECT rowid FROM game_schedule_occurrences WHERE ${sessionWhere} AND completed=1
				ORDER BY completed_at DESC,occurrence_id DESC LIMIT -1 OFFSET ?)`)
			.run(...sessionValues(session), this.completedOccurrenceRetention);
		this.database
			.prepare(`DELETE FROM game_time_advances WHERE rowid IN (
				SELECT rowid FROM game_time_advances WHERE ${sessionWhere}
				ORDER BY created_at DESC,advance_id DESC LIMIT -1 OFFSET ?)`)
			.run(...sessionValues(session), this.advanceRetention);
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("Game-time scheduler is closed.");
	}
}
