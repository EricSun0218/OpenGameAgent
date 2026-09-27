import {
	assertEventStreamCoordinate,
	captureRunAdmissionAuthority,
	captureRunAuthorityKey,
	type RunAdmissionAuthority,
	RunAuthorityClosedError,
	RunAuthorityConflictError,
	type RunAuthorityKey,
	type RunCoordinate,
} from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const migrations: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "run-authority-closures",
		sql: `
		CREATE TABLE oga_run_authority_closures (
			session_id TEXT NOT NULL, actor_id TEXT NOT NULL, scope TEXT NOT NULL, generation TEXT NOT NULL,
			operation_id TEXT NOT NULL UNIQUE, request_json TEXT NOT NULL CHECK(json_valid(request_json)),
			closed_at TEXT NOT NULL,
			PRIMARY KEY(session_id, actor_id, scope, generation)
		) STRICT;
		CREATE TABLE oga_run_authority_admissions (
			session_id TEXT NOT NULL, actor_id TEXT NOT NULL, run_id TEXT NOT NULL,
			scope TEXT NOT NULL, generation TEXT NOT NULL,
			PRIMARY KEY(session_id, actor_id, run_id)
		) STRICT;
	`,
	},
	{
		version: 2,
		name: "run-work-authority-retirement-result",
		sql: `ALTER TABLE oga_run_authority_closures ADD COLUMN work_result_json TEXT CHECK(work_result_json IS NULL OR json_valid(work_result_json));`,
	},
];

export function installSqliteRunAuthority(authority: SqliteAuthority): void {
	authority.install("run-authority", migrations);
}

export function assertSqliteAuthorityKeyOpen(database: Database.Database, input: RunAuthorityKey): void {
	const key = captureRunAuthorityKey(input);
	if (
		database
			.prepare(`SELECT 1 FROM oga_run_authority_closures
		WHERE session_id = ? AND actor_id = ? AND scope = ? AND generation = ?`)
			.get(key.sessionId, key.actorId, key.scope, key.generation) !== undefined
	) {
		throw new RunAuthorityClosedError(key);
	}
}

/** Call inside the same write transaction as the protected effect. Unknown runs are unscoped. */
export function assertSqliteRunAuthorityOpen(database: Database.Database, coordinate: RunCoordinate): void {
	assertEventStreamCoordinate({ sessionId: coordinate.sessionId, actorId: coordinate.actorId }, coordinate);
	const admission = readSqliteRunAdmissionAuthority(database, coordinate);
	if (admission !== undefined) assertSqliteAuthorityKeyOpen(database, { ...coordinate, ...admission });
}

export function readSqliteRunAdmissionAuthority(
	database: Database.Database,
	coordinate: Pick<RunCoordinate, "sessionId" | "actorId" | "runId">,
): RunAdmissionAuthority | undefined {
	const row = database
		.prepare(`SELECT scope, generation FROM oga_run_authority_admissions
		WHERE session_id = ? AND actor_id = ? AND run_id = ?`)
		.get(coordinate.sessionId, coordinate.actorId, coordinate.runId) as RunAdmissionAuthority | undefined;
	return row === undefined ? undefined : captureRunAdmissionAuthority(row);
}

export function recordSqliteRunAdmissionAuthority(
	database: Database.Database,
	coordinate: RunCoordinate,
	authority: RunAdmissionAuthority | undefined,
): void {
	const existing = readSqliteRunAdmissionAuthority(database, coordinate);
	if (
		existing !== undefined &&
		(existing.scope !== authority?.scope || existing.generation !== authority?.generation)
	) {
		throw new RunAuthorityConflictError();
	}
	if (authority === undefined) return;
	const captured = captureRunAdmissionAuthority(authority);
	assertSqliteAuthorityKeyOpen(database, { ...coordinate, ...captured });
	if (existing === undefined) {
		database
			.prepare(`INSERT INTO oga_run_authority_admissions(session_id, actor_id, run_id, scope, generation)
			VALUES (?, ?, ?, ?, ?)`)
			.run(coordinate.sessionId, coordinate.actorId, coordinate.runId, captured.scope, captured.generation);
	}
}
