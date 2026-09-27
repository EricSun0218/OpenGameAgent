import { createHash } from "node:crypto";
import { mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { assertOpaqueIdentifier } from "@opengameagent/protocol";
import Database from "better-sqlite3";

const APPLICATION_ID = 0x4f474131;
const AUTHORITY_SCHEMA_VERSION = 1;
const SQLITE_SIGNED_INT_MAX = 2_147_483_647;

export interface SqliteAuthorityOptions {
	path: string;
	busyTimeoutMs: number;
	verifyOnOpen: "full" | "quick";
	ownership?: "shared" | "exclusive";
	now?: () => string;
}

export interface SqliteMigration {
	version: number;
	name: string;
	sql: string;
}

export class SqliteAuthorityOpenError extends Error {
	constructor(cause: unknown) {
		super("The SQLite authority could not be opened safely.", { cause });
		this.name = "SqliteAuthorityOpenError";
	}
}

export class SqliteApplicationMismatchError extends Error {
	constructor() {
		super("The SQLite file belongs to a different application or is an unclaimed non-empty database.");
		this.name = "SqliteApplicationMismatchError";
	}
}

export class SqliteIntegrityError extends Error {
	constructor(public readonly findings: readonly string[]) {
		super("The SQLite authority failed its integrity check.");
		this.name = "SqliteIntegrityError";
	}
}

export class SqliteSchemaTooNewError extends Error {
	constructor(public readonly namespace: string) {
		super("The SQLite schema is newer than this runtime.");
		this.name = "SqliteSchemaTooNewError";
	}
}

export class SqliteMigrationMismatchError extends Error {
	constructor(
		public readonly namespace: string,
		public readonly version: number,
	) {
		super("An applied SQLite migration no longer matches its immutable definition.");
		this.name = "SqliteMigrationMismatchError";
	}
}

export class AsyncSqliteTransactionError extends TypeError {
	constructor() {
		super("SQLite authority transactions must settle synchronously.");
		this.name = "AsyncSqliteTransactionError";
	}
}

export class SqliteExclusiveOwnershipRequiredError extends Error {
	constructor() {
		super("The canonical event authority requires exclusive SQLite ownership.");
		this.name = "SqliteExclusiveOwnershipRequiredError";
	}
}

interface AppliedMigrationRow {
	version: number;
	name: string;
	checksum: string;
}

export class SqliteAuthority {
	private readonly database: Database.Database;
	private readonly now: () => string;
	private readonly ownership: "shared" | "exclusive";
	private closed = false;

	constructor(options: SqliteAuthorityOptions) {
		const path = options.path;
		const busyTimeoutMs = options.busyTimeoutMs;
		const verifyOnOpen = options.verifyOnOpen;
		const ownership = options.ownership ?? "shared";
		const now = options.now;
		validateBusyTimeout(busyTimeoutMs);
		validateOwnership(ownership);
		this.now = now ?? (() => new Date().toISOString());
		this.ownership = ownership;
		let database: Database.Database | undefined;
		try {
			if (path !== ":memory:") {
				mkdirSync(dirname(resolve(path)), { recursive: true });
			}
			database = new Database(path, { timeout: busyTimeoutMs });
			this.database = database;
			this.configure();
			this.claimOrVerifyApplication();
			this.bootstrapAuthoritySchema();
			this.verify(verifyOnOpen);
		} catch (error) {
			database?.close();
			if (
				error instanceof SqliteApplicationMismatchError ||
				error instanceof SqliteIntegrityError ||
				error instanceof SqliteSchemaTooNewError
			) {
				throw error;
			}
			throw new SqliteAuthorityOpenError(error);
		}
	}

	read<T>(operation: (database: Database.Database) => T): T {
		this.assertOpen();
		const result = operation(this.database);
		if (isPromiseLike(result)) {
			throw new AsyncSqliteTransactionError();
		}
		return result;
	}

	snapshot<T>(operation: (database: Database.Database) => T): T {
		this.assertOpen();
		const transaction = this.database.transaction(() => {
			const result = operation(this.database);
			if (isPromiseLike(result)) {
				throw new AsyncSqliteTransactionError();
			}
			return result;
		});
		return transaction.deferred();
	}

	write<T>(operation: (database: Database.Database) => T): T {
		this.assertOpen();
		const transaction = this.database.transaction(() => {
			const result = operation(this.database);
			if (isPromiseLike(result)) {
				throw new AsyncSqliteTransactionError();
			}
			return result;
		});
		return transaction.immediate();
	}

	install(namespace: string, migrations: readonly SqliteMigration[]): void {
		assertOpaqueIdentifier(namespace);
		validateMigrations(migrations);
		const applied = this.read(
			(database) =>
				database
					.prepare("SELECT version, name, checksum FROM oga_schema_migrations WHERE namespace = ? ORDER BY version")
					.all(namespace) as AppliedMigrationRow[],
		);
		const newestKnown = migrations.at(-1)?.version ?? 0;
		if ((applied.at(-1)?.version ?? 0) > newestKnown) {
			throw new SqliteSchemaTooNewError(namespace);
		}
		for (const row of applied) {
			const migration = migrations.find((candidate) => candidate.version === row.version);
			if (!migration || migration.name !== row.name || migrationChecksum(migration) !== row.checksum) {
				throw new SqliteMigrationMismatchError(namespace, row.version);
			}
		}

		const appliedVersions = new Set(applied.map((row) => row.version));
		const pending = migrations.filter((migration) => !appliedVersions.has(migration.version));
		if (pending.length === 0) return;
		this.write((database) => {
			const insert = database.prepare(
				"INSERT INTO oga_schema_migrations(namespace, version, name, checksum, applied_at) VALUES (?, ?, ?, ?, ?)",
			);
			for (const migration of pending) {
				database.exec(migration.sql);
				insert.run(namespace, migration.version, migration.name, migrationChecksum(migration), this.now());
			}
		});
	}

	verify(mode: "full" | "quick" = "full"): void {
		this.assertOpen();
		const pragma = mode === "full" ? "integrity_check" : "quick_check";
		const integrityRows = this.database.pragma(pragma) as Record<string, unknown>[];
		const findings = integrityRows.map((row) => String(Object.values(row)[0]));
		const foreignKeyRows = this.database.pragma("foreign_key_check") as Record<string, unknown>[];
		for (const row of foreignKeyRows) findings.push(JSON.stringify(row));
		if (findings.length !== 1 || findings[0] !== "ok") {
			throw new SqliteIntegrityError(findings);
		}
	}

	close(): void {
		if (this.closed) return;
		this.database.close();
		this.closed = true;
	}

	assertExclusiveOwnership(): void {
		this.assertOpen();
		if (this.ownership !== "exclusive") throw new SqliteExclusiveOwnershipRequiredError();
	}

	private configure(): void {
		const ownsFileExclusively = this.database.name !== ":memory:" && this.ownership === "exclusive";
		this.database.pragma("foreign_keys = ON");
		this.database.pragma("trusted_schema = OFF");
		if (ownsFileExclusively) {
			const lockingMode = this.database.pragma("locking_mode = EXCLUSIVE", { simple: true });
			if (lockingMode !== "exclusive") throw new Error("SQLite exclusive ownership could not be configured.");
		}
		const journalMode = this.database.pragma("journal_mode = WAL", { simple: true });
		if (this.database.name !== ":memory:" && journalMode !== "wal") {
			throw new Error("SQLite WAL mode is required for a file-backed authority.");
		}
		this.database.pragma("synchronous = FULL");
		if (ownsFileExclusively) this.database.exec("BEGIN EXCLUSIVE; COMMIT");
	}

	private claimOrVerifyApplication(): void {
		const applicationId = this.database.pragma("application_id", { simple: true });
		if (applicationId === APPLICATION_ID) return;
		if (applicationId !== 0) throw new SqliteApplicationMismatchError();
		const existing = this.database
			.prepare("SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' LIMIT 1")
			.get();
		if (existing) throw new SqliteApplicationMismatchError();
		this.database.pragma(`application_id = ${APPLICATION_ID}`);
	}

	private bootstrapAuthoritySchema(): void {
		const userVersion = this.database.pragma("user_version", { simple: true });
		if (typeof userVersion !== "number" || userVersion > AUTHORITY_SCHEMA_VERSION) {
			throw new SqliteSchemaTooNewError("authority");
		}
		if (userVersion === AUTHORITY_SCHEMA_VERSION) return;
		this.write((database) => {
			database.exec(`
				CREATE TABLE oga_schema_migrations (
					namespace TEXT NOT NULL,
					version INTEGER NOT NULL CHECK(version > 0),
					name TEXT NOT NULL,
					checksum TEXT NOT NULL,
					applied_at TEXT NOT NULL,
					PRIMARY KEY(namespace, version)
				) STRICT;
			`);
			database.pragma(`user_version = ${AUTHORITY_SCHEMA_VERSION}`);
		});
	}

	private assertOpen(): void {
		if (this.closed) throw new Error("The SQLite authority is closed.");
	}
}

function validateBusyTimeout(value: number): void {
	if (!Number.isInteger(value) || value < 0 || value > SQLITE_SIGNED_INT_MAX) {
		throw new RangeError("SQLite busy timeout must be an explicit non-negative signed 32-bit millisecond value.");
	}
}

/** Protect composable synchronous store operations from partial commits on another connection. */
export function requireSqliteWriteTransaction(authority: SqliteAuthority, database: Database.Database): void {
	if (!database.inTransaction || authority.read((owned) => owned !== database)) {
		throw new TypeError("The operation requires a transaction on the same SQLite authority.");
	}
}

function validateOwnership(value: string): void {
	if (value !== "shared" && value !== "exclusive") {
		throw new TypeError("SQLite ownership must be shared or exclusive.");
	}
}

function validateMigrations(migrations: readonly SqliteMigration[]): void {
	for (const [index, migration] of migrations.entries()) {
		if (migration.version !== index + 1 || migration.name.length === 0 || migration.sql.length === 0) {
			throw new TypeError("SQLite migrations must be non-empty, immutable, contiguous, and start at version one.");
		}
	}
}

function migrationChecksum(migration: SqliteMigration): string {
	return createHash("sha256").update(migration.name).update("\0").update(migration.sql).digest("hex");
}

function isPromiseLike(value: unknown): value is PromiseLike<unknown> {
	return typeof value === "object" && value !== null && "then" in value && typeof value.then === "function";
}
