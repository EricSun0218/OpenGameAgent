import { createHash } from "node:crypto";
import {
	SignalConflictError,
	SignalValidationError,
	SignalWaitConflictError,
	SignalWaitFanoutError,
	SignalWaitNotFoundError,
	SignalWaitRevisionConflictError,
	SignalWaitTerminalError,
	type GameSignalDraft,
	type GameSignalKey,
	type GameSignalRecord,
	type GameSignalSequenceKey,
	type SignalLimits,
	type SignalListRequest,
	type SignalPage,
	type SignalPartition,
	type SignalRecordResult,
	type SignalStore,
	type SignalWaitDraft,
	type SignalWaitKey,
	type SignalWaitListRequest,
	type SignalWaitPage,
	type SignalWaitRecord,
	normalizeGameSignal,
	normalizeSignalKindFilter,
	normalizeSignalPartition,
	normalizeSignalStatusFilter,
	normalizeSignalWait,
	laterSignalTimestamp,
	sameSignalGeneration,
	signalFingerprint,
	signalWaitFingerprint,
	signalWaitMatches,
	validateGameSignalKey,
	validateGameSignalSequenceKey,
	validateSignalCursor,
	validateSignalDecimal,
	validateSignalLimits,
	validateSignalPageLimit,
	validatePositiveSignalKey,
	validateSignalStatus,
	validateSignalTimestamp,
	validateSignalWaitKey,
} from "@opengameagent/extensions";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const SIGNAL_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "signal-store-v1",
		sql: `
			CREATE TABLE oga_game_signals (
				signal_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(signal_key > 0),
				signal_id TEXT NOT NULL,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				session_id TEXT,
				actor_id TEXT,
				kind TEXT NOT NULL,
				payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
				signal_tick TEXT NOT NULL,
				recorded_at TEXT NOT NULL,
				source_fingerprint TEXT NOT NULL CHECK(length(source_fingerprint) = 64),
				UNIQUE(owner_id, timeline_id, generation, signal_id)
			) STRICT;

			CREATE INDEX oga_game_signals_partition_key
			ON oga_game_signals(owner_id, timeline_id, generation, signal_key);

			CREATE INDEX oga_game_signals_partition_kind_key
			ON oga_game_signals(owner_id, timeline_id, generation, kind, signal_key);

			CREATE TABLE oga_game_signal_subjects (
				signal_key INTEGER NOT NULL REFERENCES oga_game_signals(signal_key) ON DELETE CASCADE,
				subject_id TEXT NOT NULL,
				PRIMARY KEY(signal_key, subject_id)
			) STRICT;

			CREATE INDEX oga_game_signal_subjects_subject
			ON oga_game_signal_subjects(subject_id, signal_key);

			CREATE TABLE oga_game_signal_causes (
				signal_key INTEGER NOT NULL REFERENCES oga_game_signals(signal_key) ON DELETE CASCADE,
				cause_id TEXT NOT NULL,
				PRIMARY KEY(signal_key, cause_id)
			) STRICT;

			CREATE TABLE oga_signal_waits (
				wait_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(wait_key > 0),
				wait_id TEXT NOT NULL,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				session_id TEXT,
				actor_id TEXT,
				after_signal_key INTEGER CHECK(after_signal_key IS NULL OR after_signal_key >= 0),
				after_tick TEXT,
				through_tick TEXT,
				target_kind TEXT NOT NULL CHECK(target_kind IN ('goal_step', 'correlation')),
				target_goal_id TEXT,
				target_step_id TEXT,
				target_correlation_id TEXT,
				status TEXT NOT NULL CHECK(status IN ('waiting', 'matched', 'cancelled', 'expired')),
				revision TEXT NOT NULL,
				matched_signal_key INTEGER REFERENCES oga_game_signals(signal_key),
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				source_fingerprint TEXT NOT NULL CHECK(length(source_fingerprint) = 64),
				CHECK(
					(target_kind = 'goal_step' AND target_goal_id IS NOT NULL AND target_step_id IS NOT NULL
					 AND target_correlation_id IS NULL)
					OR
					(target_kind = 'correlation' AND target_goal_id IS NULL AND target_step_id IS NULL
					 AND target_correlation_id IS NOT NULL)
				),
				CHECK((status = 'matched') = (matched_signal_key IS NOT NULL)),
				UNIQUE(owner_id, timeline_id, generation, wait_id)
			) STRICT;

			CREATE INDEX oga_signal_waits_partition_status_key
			ON oga_signal_waits(owner_id, timeline_id, generation, status, wait_key);

			CREATE INDEX oga_signal_waits_expiration
			ON oga_signal_waits(owner_id, timeline_id, generation, through_tick, wait_key)
			WHERE status = 'waiting' AND through_tick IS NOT NULL;

			CREATE INDEX oga_signal_waits_matched_signal
			ON oga_signal_waits(matched_signal_key)
			WHERE matched_signal_key IS NOT NULL;

			CREATE TABLE oga_signal_wait_kinds (
				wait_key INTEGER NOT NULL REFERENCES oga_signal_waits(wait_key) ON DELETE CASCADE,
				kind TEXT NOT NULL,
				PRIMARY KEY(wait_key, kind)
			) STRICT;

			CREATE INDEX oga_signal_wait_kinds_kind
			ON oga_signal_wait_kinds(kind, wait_key);

			CREATE TABLE oga_signal_wait_subjects (
				wait_key INTEGER NOT NULL REFERENCES oga_signal_waits(wait_key) ON DELETE CASCADE,
				subject_id TEXT NOT NULL,
				PRIMARY KEY(wait_key, subject_id)
			) STRICT;

			CREATE INDEX oga_signal_wait_subjects_subject
			ON oga_signal_wait_subjects(subject_id, wait_key);
		`,
	},
];

interface SignalRow {
	signal_key: bigint;
	signal_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	session_id: string | null;
	actor_id: string | null;
	kind: string;
	payload_json: string;
	signal_tick: string;
	recorded_at: string;
	source_fingerprint: string;
}

interface WaitRow {
	wait_key: bigint;
	wait_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	session_id: string | null;
	actor_id: string | null;
	after_signal_key: bigint | null;
	after_tick: string | null;
	through_tick: string | null;
	target_kind: string;
	target_goal_id: string | null;
	target_step_id: string | null;
	target_correlation_id: string | null;
	status: string;
	revision: string;
	matched_signal_key: bigint | null;
	created_at: string;
	updated_at: string;
	source_fingerprint: string;
}

export class SqliteSignalStore implements SignalStore {
	readonly limits: SignalLimits;

	constructor(
		private readonly authority: SqliteAuthority,
		limits: SignalLimits,
	) {
		validateSignalLimits(limits);
		this.limits = Object.freeze({ ...limits });
		this.authority.install("signals", SIGNAL_MIGRATIONS);
	}

	async record(input: GameSignalDraft, maxMatches: number): Promise<SignalRecordResult> {
		const signal = normalizeGameSignal(input, this.limits);
		validateSignalPageLimit(maxMatches, this.limits.maxWaitMatchesPerSignal, "invalid_signal_match_limit");
		const fingerprint = digest(signalFingerprint(signal));
		return this.authority.write((database) => {
			const existing = findSignalRow(database, signal);
			if (existing !== undefined) {
				if (existing.source_fingerprint !== fingerprint) throw new SignalConflictError();
				const hydrated = this.hydrateSignal(database, existing);
				const matchedWaitIds = this.hydrateMatchedWaitIds(database, hydrated, maxMatches);
				if (matchedWaitIds.length > maxMatches) throw new SignalWaitFanoutError();
				return {
					signal: hydrated,
					matchedWaitIds,
				};
			}
			const result = database
				.prepare(`
					INSERT INTO oga_game_signals(
						signal_id, owner_id, timeline_id, generation, session_id, actor_id,
						kind, payload_json, signal_tick, recorded_at, source_fingerprint
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
				`)
				.run(
					signal.signalId,
					signal.partition.ownerId,
					signal.partition.timelineId,
					signal.partition.generation,
					signal.partition.sessionId ?? null,
					signal.partition.actorId ?? null,
					signal.kind,
					JSON.stringify(signal.payload),
					signal.gameTime.tick,
					signal.recordedAt,
					fingerprint,
				);
			const signalKey = BigInt(result.lastInsertRowid);
			insertIdentifiers(database, "oga_game_signal_subjects", "subject_id", signalKey, signal.subjectIds);
			insertIdentifiers(database, "oga_game_signal_causes", "cause_id", signalKey, signal.causeIds);
			const signalRow = findSignalRowByKey(database, signalKey);
			if (signalRow === undefined) throw new Error("Signal insert did not produce a readable row.");
			const matches = findMatchingWaitRows(database, signalRow, maxMatches + 1);
			if (matches.length > maxMatches) throw new SignalWaitFanoutError();
			const update = database.prepare(`
				UPDATE oga_signal_waits
				SET status = 'matched', matched_signal_key = ?, revision = ?, updated_at = ?
				WHERE wait_key = ? AND status = 'waiting' AND revision = ?
			`);
			for (const wait of matches) {
				this.hydrateWait(database, wait);
				const nextRevision = (BigInt(wait.revision) + 1n).toString();
				const updatedAt = laterSignalTimestamp(wait.updated_at, signal.recordedAt);
				const updated = update.run(signalKey, nextRevision, updatedAt, wait.wait_key, wait.revision);
				if (updated.changes !== 1) throw new SignalWaitRevisionConflictError();
			}
			return {
				signal: this.hydrateSignal(database, signalRow),
				matchedWaitIds: matches.map((wait) => wait.wait_id),
			};
		});
	}

	async getSignal(key: GameSignalKey): Promise<GameSignalRecord | undefined> {
		validateGameSignalKey(key);
		return this.authority.snapshot((database) => {
			const row = findSignalRow(database, key);
			return row === undefined ? undefined : this.hydrateSignal(database, row);
		});
	}

	async getSignalByKey(key: GameSignalSequenceKey): Promise<GameSignalRecord | undefined> {
		validateGameSignalSequenceKey(key);
		return this.authority.snapshot((database) => {
			const row = findSignalRowBySequenceKey(database, key);
			return row === undefined ? undefined : this.hydrateSignal(database, row);
		});
	}

	async listSignals(request: SignalListRequest): Promise<SignalPage> {
		const partition = normalizeSignalPartition(request);
		const kinds = normalizeSignalKindFilter(request.kinds, this.limits.maxSignalFilterKinds);
		validateSignalCursor(request.afterSignalKey);
		validateSignalPageLimit(request.limit, this.limits.maxSignalPageSize, "invalid_signal_page_limit");
		return this.authority.snapshot((database) => {
			const selectedKinds = kinds ?? [];
			const kindSql = selectedKinds.length === 0 ? "" : `AND kind IN (${selectedKinds.map(() => "?").join(", ")})`;
			const statement = database.prepare(`
				SELECT * FROM oga_game_signals
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND (? IS NULL OR session_id = ?)
				  AND (? IS NULL OR actor_id = ?)
				  AND signal_key > ?
				  ${kindSql}
				ORDER BY signal_key
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
				request.afterSignalKey ?? 0n,
				...selectedKinds,
				BigInt(request.limit + 1),
			) as SignalRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => this.hydrateSignal(database, row));
			const next = hasMore ? records.at(-1)?.signalKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterSignalKey: next }) };
		});
	}

	async registerWait(input: SignalWaitDraft, maxMatches: number): Promise<SignalWaitRecord> {
		const wait = normalizeSignalWait(input, this.limits);
		validateSignalPageLimit(maxMatches, this.limits.maxWaitMatchesPerSignal, "invalid_signal_match_limit");
		const fingerprint = digest(signalWaitFingerprint(wait));
		return this.authority.write((database) => {
			const existing = findWaitRow(database, wait);
			if (existing !== undefined) {
				if (existing.source_fingerprint !== fingerprint) throw new SignalWaitConflictError();
				return this.hydrateWait(database, existing);
			}
			const target = wait.target;
			const result = database
				.prepare(`
					INSERT INTO oga_signal_waits(
						wait_id, owner_id, timeline_id, generation, session_id, actor_id,
						after_signal_key, after_tick, through_tick,
						target_kind, target_goal_id, target_step_id, target_correlation_id,
						status, revision, matched_signal_key, created_at, updated_at, source_fingerprint
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'waiting', '1', NULL, ?, ?, ?)
				`)
				.run(
					wait.waitId,
					wait.partition.ownerId,
					wait.partition.timelineId,
					wait.partition.generation,
					wait.partition.sessionId ?? null,
					wait.partition.actorId ?? null,
					wait.selector.afterSignalKey ?? null,
					wait.selector.afterTickExclusive ?? null,
					wait.selector.throughTickInclusive ?? null,
					target.kind,
					target.kind === "goal_step" ? target.goalId : null,
					target.kind === "goal_step" ? target.stepId : null,
					target.kind === "correlation" ? target.correlationId : null,
					wait.recordedAt,
					wait.recordedAt,
					fingerprint,
				);
			const waitKey = BigInt(result.lastInsertRowid);
			insertIdentifiers(database, "oga_signal_wait_kinds", "kind", waitKey, wait.selector.kinds);
			insertIdentifiers(database, "oga_signal_wait_subjects", "subject_id", waitKey, wait.selector.subjectIdsAny ?? []);
			const waitRow = findWaitRowByKey(database, waitKey);
			if (waitRow === undefined) throw new Error("Signal wait insert did not produce a readable row.");
			const matchedSignal = findFirstMatchingSignalRow(database, waitRow);
			if (matchedSignal !== undefined) {
				const hydratedSignal = this.hydrateSignal(database, matchedSignal);
				if (countMatchedWaits(database, matchedSignal.signal_key) >= BigInt(maxMatches)) {
					throw new SignalWaitFanoutError();
				}
				database
					.prepare(
						"UPDATE oga_signal_waits SET status = 'matched', matched_signal_key = ?, updated_at = ? WHERE wait_key = ?",
					)
					.run(matchedSignal.signal_key, laterSignalTimestamp(wait.recordedAt, hydratedSignal.recordedAt), waitKey);
			}
			const stored = findWaitRowByKey(database, waitKey);
			if (stored === undefined) throw new Error("Signal wait insert did not remain readable.");
			return this.hydrateWait(database, stored);
		});
	}

	async getWait(key: SignalWaitKey): Promise<SignalWaitRecord | undefined> {
		validateSignalWaitKey(key);
		return this.authority.snapshot((database) => {
			const row = findWaitRow(database, key);
			return row === undefined ? undefined : this.hydrateWait(database, row);
		});
	}

	async listWaits(request: SignalWaitListRequest): Promise<SignalWaitPage> {
		const partition = normalizeSignalPartition(request);
		const statuses = normalizeSignalStatusFilter(request.statuses);
		validateSignalCursor(request.afterWaitKey);
		validateSignalPageLimit(request.limit, this.limits.maxWaitPageSize, "invalid_signal_wait_page_limit");
		return this.authority.snapshot((database) => {
			const selectedStatuses = statuses ?? [];
			const statusSql =
				selectedStatuses.length === 0 ? "" : `AND status IN (${selectedStatuses.map(() => "?").join(", ")})`;
			const statement = database.prepare(`
				SELECT * FROM oga_signal_waits
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND (? IS NULL OR session_id = ?)
				  AND (? IS NULL OR actor_id = ?)
				  AND wait_key > ?
				  ${statusSql}
				ORDER BY wait_key
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
				request.afterWaitKey ?? 0n,
				...selectedStatuses,
				BigInt(request.limit + 1),
			) as WaitRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => this.hydrateWait(database, row));
			const next = hasMore ? records.at(-1)?.waitKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterWaitKey: next }) };
		});
	}

	async cancelWait(key: SignalWaitKey, expectedRevision: string, recordedAt: string): Promise<SignalWaitRecord> {
		validateSignalWaitKey(key);
		validateSignalDecimal(expectedRevision, "wait_revision");
		validateSignalTimestamp(recordedAt);
		return this.authority.write((database) => {
			const current = findWaitRow(database, key);
			if (current === undefined) throw new SignalWaitNotFoundError();
			if (current.revision !== expectedRevision) throw new SignalWaitRevisionConflictError();
			if (current.status !== "waiting") throw new SignalWaitTerminalError();
			const nextRevision = (BigInt(current.revision) + 1n).toString();
			const result = database
				.prepare(`
					UPDATE oga_signal_waits
					SET status = 'cancelled', revision = ?, updated_at = ?
					WHERE wait_key = ? AND status = 'waiting' AND revision = ?
				`)
				.run(nextRevision, laterSignalTimestamp(current.updated_at, recordedAt), current.wait_key, expectedRevision);
			if (result.changes !== 1) throw new SignalWaitRevisionConflictError();
			const updated = findWaitRowByKey(database, current.wait_key);
			if (updated === undefined) throw new SignalWaitNotFoundError();
			return this.hydrateWait(database, updated);
		});
	}

	async expireWaits(
		input: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		throughTickInclusive: string,
		recordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const partition = normalizeSignalPartition(input);
		validateSignalDecimal(throughTickInclusive, "expiration_tick");
		validateSignalTimestamp(recordedAt);
		validateSignalPageLimit(limit, this.limits.maxExpireBatchSize, "invalid_signal_expire_limit");
		return this.authority.write((database) => {
			const select = database.prepare(`
				SELECT * FROM oga_signal_waits
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND status = 'waiting' AND through_tick IS NOT NULL
				  AND (length(through_tick) < length(?) OR (length(through_tick) = length(?) AND through_tick <= ?))
				ORDER BY wait_key
				LIMIT ?
			`);
			select.safeIntegers(true);
			const rows = select.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				throughTickInclusive,
				throughTickInclusive,
				throughTickInclusive,
				BigInt(limit),
			) as WaitRow[];
			const update = database.prepare(`
				UPDATE oga_signal_waits
				SET status = 'expired', revision = ?, updated_at = ?
				WHERE wait_key = ? AND status = 'waiting' AND revision = ?
			`);
			for (const row of rows) {
				this.hydrateWait(database, row);
				const result = update.run(
					(BigInt(row.revision) + 1n).toString(),
					laterSignalTimestamp(row.updated_at, recordedAt),
					row.wait_key,
					row.revision,
				);
				if (result.changes !== 1) throw new SignalWaitRevisionConflictError();
			}
			return rows.map((row) => row.wait_key);
		});
	}

	async purgeTerminalWaits(
		input: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const partition = normalizeSignalPartition(input);
		validateSignalTimestamp(beforeRecordedAt);
		validateSignalPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_signal_wait_purge_limit");
		return this.authority.write((database) => {
			const rows = selectKeys(
				database,
				`SELECT wait_key AS item_key FROM oga_signal_waits
				 WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				   AND status IN ('matched', 'cancelled', 'expired') AND updated_at < ?
				 ORDER BY wait_key LIMIT ?`,
				[partition.ownerId, partition.timelineId, partition.generation, beforeRecordedAt, BigInt(limit)],
			);
			const remove = database.prepare("DELETE FROM oga_signal_waits WHERE wait_key = ?");
			for (const key of rows) remove.run(key);
			return rows;
		});
	}

	async purgeSignals(
		input: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const partition = normalizeSignalPartition(input);
		validateSignalTimestamp(beforeRecordedAt);
		validateSignalPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_signal_purge_limit");
		return this.authority.write((database) => {
			const rows = selectKeys(
				database,
				`SELECT signal_key AS item_key FROM oga_game_signals signal
				 WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND recorded_at < ?
				   AND NOT EXISTS (
				     SELECT 1 FROM oga_signal_waits wait WHERE wait.matched_signal_key = signal.signal_key
				   )
				 ORDER BY signal_key LIMIT ?`,
				[partition.ownerId, partition.timelineId, partition.generation, beforeRecordedAt, BigInt(limit)],
			);
			const remove = database.prepare("DELETE FROM oga_game_signals WHERE signal_key = ?");
			for (const key of rows) remove.run(key);
			return rows;
		});
	}

	private hydrateSignal(database: Database.Database, row: SignalRow): GameSignalRecord {
		validatePositiveSignalKey(row.signal_key, "record_key");
		const partition = normalizeSignalPartition({
			ownerId: row.owner_id,
			timelineId: row.timeline_id,
			generation: row.generation,
			...(row.session_id === null ? {} : { sessionId: row.session_id }),
			...(row.actor_id === null ? {} : { actorId: row.actor_id }),
		});
		let payload: unknown;
		try {
			payload = JSON.parse(row.payload_json);
		} catch {
			throw new SignalValidationError("corrupt_signal_payload");
		}
		const normalized = normalizeGameSignal(
			{
				signalId: row.signal_id,
				partition,
				kind: row.kind,
				payload: payload as never,
				gameTime: { timelineId: partition.timelineId, generation: partition.generation, tick: row.signal_tick },
				subjectIds: readIdentifiers(database, "oga_game_signal_subjects", "subject_id", "signal_key", row.signal_key),
				causeIds: readIdentifiers(database, "oga_game_signal_causes", "cause_id", "signal_key", row.signal_key),
				recordedAt: row.recorded_at,
			},
			this.limits,
		);
		if (digest(signalFingerprint(normalized)) !== row.source_fingerprint) {
			throw new SignalValidationError("corrupt_signal_index");
		}
		return { ...normalized, signalKey: row.signal_key };
	}

	private hydrateWait(database: Database.Database, row: WaitRow): SignalWaitRecord {
		validatePositiveSignalKey(row.wait_key, "wait_key");
		const partition = normalizeSignalPartition({
			ownerId: row.owner_id,
			timelineId: row.timeline_id,
			generation: row.generation,
			...(row.session_id === null ? {} : { sessionId: row.session_id }),
			...(row.actor_id === null ? {} : { actorId: row.actor_id }),
		});
		const target =
			row.target_kind === "goal_step" && row.target_goal_id !== null && row.target_step_id !== null
				? ({ kind: "goal_step", goalId: row.target_goal_id, stepId: row.target_step_id } as const)
				: row.target_kind === "correlation" && row.target_correlation_id !== null
					? ({ kind: "correlation", correlationId: row.target_correlation_id } as const)
					: undefined;
		if (target === undefined) throw new SignalValidationError("corrupt_signal_wait_target");
		const normalized = normalizeSignalWait(
			{
				waitId: row.wait_id,
				partition,
				selector: {
					kinds: readIdentifiers(database, "oga_signal_wait_kinds", "kind", "wait_key", row.wait_key),
					...(readIdentifiers(database, "oga_signal_wait_subjects", "subject_id", "wait_key", row.wait_key).length === 0
						? {}
						: {
								subjectIdsAny: readIdentifiers(
									database,
									"oga_signal_wait_subjects",
									"subject_id",
									"wait_key",
									row.wait_key,
								),
							}),
					...(row.after_signal_key === null ? {} : { afterSignalKey: row.after_signal_key }),
					...(row.after_tick === null ? {} : { afterTickExclusive: row.after_tick }),
					...(row.through_tick === null ? {} : { throughTickInclusive: row.through_tick }),
				},
				target,
				recordedAt: row.created_at,
			},
			this.limits,
		);
		validateSignalStatus(row.status);
		validateSignalDecimal(row.revision, "wait_revision");
		if (BigInt(row.revision) < 1n) throw new SignalValidationError("invalid_signal_wait_revision");
		validateSignalTimestamp(row.updated_at);
		if (row.updated_at < row.created_at) throw new SignalValidationError("corrupt_signal_wait_chronology");
		if (digest(signalWaitFingerprint(normalized)) !== row.source_fingerprint) {
			throw new SignalValidationError("corrupt_signal_wait_index");
		}
		const hydrated: SignalWaitRecord = {
			...normalized,
			waitKey: row.wait_key,
			status: row.status,
			revision: row.revision,
			...(row.matched_signal_key === null ? {} : { matchedSignalKey: row.matched_signal_key }),
			createdAt: row.created_at,
			updatedAt: row.updated_at,
		};
		if (hydrated.status === "matched") {
			if (hydrated.matchedSignalKey === undefined) throw new SignalValidationError("corrupt_signal_wait_match");
			const matchedRow = findSignalRowByKey(database, hydrated.matchedSignalKey);
			if (matchedRow === undefined) throw new SignalValidationError("corrupt_signal_wait_match");
			const matched = this.hydrateSignal(database, matchedRow);
			if (!sameSignalGeneration(hydrated.partition, matched.partition) || !signalWaitMatches(hydrated, matched)) {
				throw new SignalValidationError("corrupt_signal_wait_match");
			}
		} else if (hydrated.matchedSignalKey !== undefined) {
			throw new SignalValidationError("corrupt_signal_wait_match");
		}
		return hydrated;
	}

	private hydrateMatchedWaitIds(
		database: Database.Database,
		signal: GameSignalRecord,
		maximum: number,
	): readonly string[] {
		const rows = findMatchedWaitRowsBySignal(database, signal.signalKey, maximum + 1);
		if (rows.length > maximum) throw new SignalWaitFanoutError();
		return rows.map((row) => {
			const wait = this.hydrateWait(database, row);
			if (wait.matchedSignalKey !== signal.signalKey || !signalWaitMatches(wait, signal)) {
				throw new SignalValidationError("corrupt_signal_wait_match");
			}
			return wait.waitId;
		});
	}
}

function findSignalRow(database: Database.Database, key: GameSignalKey | GameSignalDraft): SignalRow | undefined {
	const partition = "partition" in key ? key.partition : key;
	const statement = database.prepare(`
		SELECT * FROM oga_game_signals
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND signal_id = ? LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(partition.ownerId, partition.timelineId, partition.generation, key.signalId) as
		| SignalRow
		| undefined;
}

function findSignalRowByKey(database: Database.Database, signalKey: bigint): SignalRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_game_signals WHERE signal_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(signalKey) as SignalRow | undefined;
}

function findSignalRowBySequenceKey(database: Database.Database, key: GameSignalSequenceKey): SignalRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_game_signals
		WHERE signal_key = ? AND owner_id = ? AND timeline_id = ? AND generation = ? LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(key.signalKey, key.ownerId, key.timelineId, key.generation) as SignalRow | undefined;
}

function findWaitRow(database: Database.Database, key: SignalWaitKey | SignalWaitDraft): WaitRow | undefined {
	const partition = "partition" in key ? key.partition : key;
	const statement = database.prepare(`
		SELECT * FROM oga_signal_waits
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND wait_id = ? LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(partition.ownerId, partition.timelineId, partition.generation, key.waitId) as
		| WaitRow
		| undefined;
}

function findWaitRowByKey(database: Database.Database, waitKey: bigint): WaitRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_signal_waits WHERE wait_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(waitKey) as WaitRow | undefined;
}

function findMatchedWaitRowsBySignal(database: Database.Database, signalKey: bigint, limit: number): WaitRow[] {
	const statement = database.prepare(
		"SELECT * FROM oga_signal_waits WHERE matched_signal_key = ? ORDER BY wait_key LIMIT ?",
	);
	statement.safeIntegers(true);
	return statement.all(signalKey, BigInt(limit)) as WaitRow[];
}

function countMatchedWaits(database: Database.Database, signalKey: bigint): bigint {
	const statement = database.prepare("SELECT count(*) FROM oga_signal_waits WHERE matched_signal_key = ?");
	statement.safeIntegers(true);
	return statement.pluck().get(signalKey) as bigint;
}

function findMatchingWaitRows(database: Database.Database, signal: SignalRow, limit: number): WaitRow[] {
	const statement = database.prepare(`
		SELECT wait.* FROM oga_signal_waits wait
		WHERE wait.status = 'waiting'
		  AND wait.owner_id = ? AND wait.timeline_id = ? AND wait.generation = ?
		  AND (wait.session_id IS NULL OR wait.session_id = ?)
		  AND (wait.actor_id IS NULL OR wait.actor_id = ?)
		  AND (wait.after_signal_key IS NULL OR wait.after_signal_key < ?)
		  AND (wait.after_tick IS NULL OR length(wait.after_tick) < length(?)
		       OR (length(wait.after_tick) = length(?) AND wait.after_tick < ?))
		  AND (wait.through_tick IS NULL OR length(wait.through_tick) > length(?)
		       OR (length(wait.through_tick) = length(?) AND wait.through_tick >= ?))
		  AND EXISTS (
		    SELECT 1 FROM oga_signal_wait_kinds kinds WHERE kinds.wait_key = wait.wait_key AND kinds.kind = ?
		  )
		  AND (
		    NOT EXISTS (SELECT 1 FROM oga_signal_wait_subjects subjects WHERE subjects.wait_key = wait.wait_key)
		    OR EXISTS (
		      SELECT 1 FROM oga_signal_wait_subjects subjects
		      JOIN oga_game_signal_subjects signal_subjects ON signal_subjects.subject_id = subjects.subject_id
		      WHERE subjects.wait_key = wait.wait_key AND signal_subjects.signal_key = ?
		    )
		  )
		ORDER BY wait.wait_key
		LIMIT ?
	`);
	statement.safeIntegers(true);
	return statement.all(
		signal.owner_id,
		signal.timeline_id,
		signal.generation,
		signal.session_id,
		signal.actor_id,
		signal.signal_key,
		signal.signal_tick,
		signal.signal_tick,
		signal.signal_tick,
		signal.signal_tick,
		signal.signal_tick,
		signal.signal_tick,
		signal.kind,
		signal.signal_key,
		BigInt(limit),
	) as WaitRow[];
}

function findFirstMatchingSignalRow(database: Database.Database, wait: WaitRow): SignalRow | undefined {
	const statement = database.prepare(`
		SELECT signal.* FROM oga_game_signals signal
		WHERE signal.owner_id = ? AND signal.timeline_id = ? AND signal.generation = ?
		  AND (? IS NULL OR signal.session_id = ?)
		  AND (? IS NULL OR signal.actor_id = ?)
		  AND (? IS NULL OR signal.signal_key > ?)
		  AND (? IS NULL OR length(signal.signal_tick) > length(?)
		       OR (length(signal.signal_tick) = length(?) AND signal.signal_tick > ?))
		  AND (? IS NULL OR length(signal.signal_tick) < length(?)
		       OR (length(signal.signal_tick) = length(?) AND signal.signal_tick <= ?))
		  AND EXISTS (
		    SELECT 1 FROM oga_signal_wait_kinds kinds WHERE kinds.wait_key = ? AND kinds.kind = signal.kind
		  )
		  AND (
		    NOT EXISTS (SELECT 1 FROM oga_signal_wait_subjects subjects WHERE subjects.wait_key = ?)
		    OR EXISTS (
		      SELECT 1 FROM oga_signal_wait_subjects subjects
		      JOIN oga_game_signal_subjects signal_subjects ON signal_subjects.subject_id = subjects.subject_id
		      WHERE subjects.wait_key = ? AND signal_subjects.signal_key = signal.signal_key
		    )
		  )
		ORDER BY signal.signal_key
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(
		wait.owner_id,
		wait.timeline_id,
		wait.generation,
		wait.session_id,
		wait.session_id,
		wait.actor_id,
		wait.actor_id,
		wait.after_signal_key,
		wait.after_signal_key,
		wait.after_tick,
		wait.after_tick,
		wait.after_tick,
		wait.after_tick,
		wait.through_tick,
		wait.through_tick,
		wait.through_tick,
		wait.through_tick,
		wait.wait_key,
		wait.wait_key,
		wait.wait_key,
	) as SignalRow | undefined;
}

function insertIdentifiers(
	database: Database.Database,
	table: string,
	column: string,
	key: bigint,
	values: readonly string[],
): void {
	const statement = database.prepare(
		`INSERT INTO ${table}(signal_key, ${column}) VALUES (?, ?)`.replace("signal_key", keyColumn(table)),
	);
	for (const value of values) statement.run(key, value);
}

function readIdentifiers(
	database: Database.Database,
	table: string,
	column: string,
	keyColumnName: string,
	key: bigint,
): string[] {
	return database
		.prepare(`SELECT ${column} FROM ${table} WHERE ${keyColumnName} = ? ORDER BY ${column}`)
		.pluck()
		.all(key) as string[];
}

function keyColumn(table: string): "signal_key" | "wait_key" {
	return table.includes("wait_") ? "wait_key" : "signal_key";
}

function selectKeys(database: Database.Database, sql: string, values: readonly unknown[]): bigint[] {
	const statement = database.prepare(sql);
	statement.safeIntegers(true);
	return statement.pluck().all(...values) as bigint[];
}

function digest(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}
