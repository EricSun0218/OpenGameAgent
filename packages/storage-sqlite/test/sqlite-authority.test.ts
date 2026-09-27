import { type ChildProcess, spawn } from "node:child_process";
import { once } from "node:events";
import { writeFileSync } from "node:fs";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import Database from "better-sqlite3";
import { afterEach, describe, expect, it } from "vitest";
import {
	AsyncSqliteTransactionError,
	SqliteApplicationMismatchError,
	SqliteAuthority,
	SqliteAuthorityOpenError,
	SqliteMigrationMismatchError,
	SqliteSchemaTooNewError,
} from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const children: ChildProcess[] = [];

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-sqlite-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({
		path,
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		now: () => "2026-08-28T00:00:00.000Z",
	});
	authorities.push(authority);
	return authority;
}

function openExclusive(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({
		path,
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		ownership: "exclusive",
		now: () => "2026-08-28T00:00:00.000Z",
	});
	authorities.push(authority);
	return authority;
}

function expectExclusiveOwnerBusy(path: string): void {
	let thrown: unknown;
	try {
		openExclusive(path);
	} catch (error) {
		thrown = error;
	}
	expect(thrown).toBeInstanceOf(SqliteAuthorityOpenError);
	expect((thrown as SqliteAuthorityOpenError).cause).toMatchObject({ code: "SQLITE_BUSY" });
}

afterEach(async () => {
	for (const child of children.splice(0)) {
		if (child.exitCode !== null || child.signalCode !== null) continue;
		const exited = once(child, "exit");
		child.kill();
		await exited;
	}
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteAuthority", () => {
	it("persists one shared migration history and data across restart", async () => {
		const path = await databasePath();
		const first = open(path);
		first.install("feature", [
			{
				version: 1,
				name: "feature-records",
				sql: "CREATE TABLE feature_records(id TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;",
			},
		]);
		first.write((database) => database.prepare("INSERT INTO feature_records VALUES (?, ?)").run("id", "value"));
		first.close();

		const reopened = open(path);
		reopened.install("feature", [
			{
				version: 1,
				name: "feature-records",
				sql: "CREATE TABLE feature_records(id TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;",
			},
		]);
		expect(reopened.read((database) => database.prepare("SELECT value FROM feature_records").pluck().get())).toBe(
			"value",
		);
		expect(
			reopened.read((database) =>
				database.prepare("SELECT COUNT(*) FROM oga_schema_migrations WHERE namespace = ?").pluck().get("feature"),
			),
		).toBe(1);
	});

	it("rolls back an entire migration batch when one statement fails", async () => {
		const authority = open(await databasePath());
		expect(() =>
			authority.install("broken", [
				{
					version: 1,
					name: "broken",
					sql: "CREATE TABLE should_rollback(id INTEGER) STRICT; INSERT INTO missing_table VALUES (1);",
				},
			]),
		).toThrow();
		expect(
			authority.read((database) =>
				database.prepare("SELECT name FROM sqlite_schema WHERE name = 'should_rollback'").get(),
			),
		).toBeUndefined();
	});

	it("rejects asynchronous transaction callbacks and rolls back their synchronous prefix", async () => {
		const authority = open(await databasePath());
		authority.install("records", [
			{ version: 1, name: "records", sql: "CREATE TABLE records(value TEXT NOT NULL) STRICT;" },
		]);

		expect(() =>
			authority.write(async (database) => {
				database.prepare("INSERT INTO records VALUES (?)").run("must roll back");
			}),
		).toThrow(AsyncSqliteTransactionError);
		expect(authority.read((database) => database.prepare("SELECT COUNT(*) FROM records").pluck().get())).toBe(0);
	});

	it("keeps one read snapshot while another WAL connection commits", async () => {
		const path = await databasePath();
		const reader = open(path);
		const writer = open(path);
		reader.install("snapshot-records", [
			{
				version: 1,
				name: "snapshot-records",
				sql: "CREATE TABLE snapshot_records(value TEXT NOT NULL) STRICT;",
			},
		]);
		reader.write((database) => database.prepare("INSERT INTO snapshot_records VALUES ('before')").run());

		const observed = reader.snapshot((database) => {
			const before = database.prepare("SELECT value FROM snapshot_records").pluck().get();
			writer.write((other) => other.prepare("UPDATE snapshot_records SET value = 'after'").run());
			const afterConcurrentCommit = database.prepare("SELECT value FROM snapshot_records").pluck().get();
			return { before, afterConcurrentCommit };
		});

		expect(observed).toEqual({ before: "before", afterConcurrentCommit: "before" });
		expect(reader.read((database) => database.prepare("SELECT value FROM snapshot_records").pluck().get())).toBe(
			"after",
		);
	});

	it("holds one exclusive owner across real SQLite connections until close", async () => {
		const path = await databasePath();
		const owner = openExclusive(path);
		owner.install("exclusive-records", [
			{ version: 1, name: "exclusive-records", sql: "CREATE TABLE exclusive_records(value TEXT NOT NULL) STRICT;" },
		]);
		owner.write((database) => database.prepare("INSERT INTO exclusive_records VALUES ('owned')").run());

		const otherConnection = new Database(path, { timeout: 0 });
		try {
			expect(() => otherConnection.prepare("SELECT value FROM exclusive_records").pluck().get()).toThrow(/locked/u);
			expectExclusiveOwnerBusy(path);

			owner.close();
			expect(otherConnection.prepare("SELECT value FROM exclusive_records").pluck().get()).toBe("owned");
		} finally {
			otherConnection.close();
		}
	});

	it("reopens after the owning process crashes", async () => {
		const path = await databasePath();
		const child = spawn(
			process.execPath,
			[
				"--experimental-transform-types",
				fileURLToPath(new URL("./fixtures/sqlite-exclusive-owner.mjs", import.meta.url)),
				path,
			],
			{ stdio: ["pipe", "pipe", "inherit"] },
		);
		children.push(child);
		if (!child.stdout) throw new Error("Expected the owner process stdout pipe.");
		const [ready] = await once(child.stdout, "data");
		expect(String(ready)).toBe("ready\n");

		expectExclusiveOwnerBusy(path);
		const exited = once(child, "exit");
		child.kill();
		await exited;

		expect(() => openExclusive(path)).not.toThrow();
	});

	it("rejects asynchronous read-snapshot callbacks", async () => {
		const authority = open(await databasePath());

		expect(() => authority.snapshot(async () => "not synchronous")).toThrow(AsyncSqliteTransactionError);
	});

	it("detects immutable migration drift and newer installed schemas", async () => {
		const authority = open(await databasePath());
		const first = { version: 1, name: "one", sql: "CREATE TABLE one(id INTEGER) STRICT;" } as const;
		const second = { version: 2, name: "two", sql: "CREATE TABLE two(id INTEGER) STRICT;" } as const;
		authority.install("versioned", [first, second]);

		expect(() => authority.install("versioned", [first])).toThrow(SqliteSchemaTooNewError);
		expect(() =>
			authority.install("versioned", [{ ...first, sql: "CREATE TABLE changed(id INTEGER) STRICT;" }, second]),
		).toThrow(SqliteMigrationMismatchError);
	});

	it("refuses an unclaimed non-empty SQLite database", async () => {
		const path = await databasePath();
		const unrelated = new Database(path);
		unrelated.exec("CREATE TABLE unrelated(value TEXT);");
		unrelated.close();

		expect(() => open(path)).toThrow(SqliteApplicationMismatchError);
	});

	it("rejects a malformed database file instead of recreating it", async () => {
		const path = await databasePath();
		writeFileSync(path, "not a SQLite database", "utf8");

		expect(() => open(path)).toThrow(SqliteAuthorityOpenError);
	});

	it("requires an explicit signed 32-bit lock wait", async () => {
		const path = await databasePath();
		expect(() => new SqliteAuthority({ path, busyTimeoutMs: -1, verifyOnOpen: "quick" })).toThrow(RangeError);
		expect(() => new SqliteAuthority({ path, busyTimeoutMs: 2_147_483_648, verifyOnOpen: "quick" })).toThrow(
			RangeError,
		);
	});

	it("reads the lock wait configuration once before opening SQLite", async () => {
		let reads = 0;
		const options = {
			path: await databasePath(),
			get busyTimeoutMs() {
				reads += 1;
				return reads === 1 ? 0 : 2_147_483_648;
			},
			verifyOnOpen: "quick" as const,
		};
		const authority = new SqliteAuthority(options);
		authorities.push(authority);
		expect(reads).toBe(1);
	});
});
