import { createHash } from "node:crypto";
import { writeSync } from "node:fs";
import { SqliteAuthority } from "../../src/sqlite-authority.ts";
import { SqliteEventStore } from "../../src/sqlite-event-store.ts";

const path = process.argv[2];
if (!path) throw new Error("Expected the SQLite authority path.");

const authority = new SqliteAuthority({
	path,
	busyTimeoutMs: 0,
	verifyOnOpen: "full",
	ownership: "exclusive",
	now: () => "2026-09-01T00:00:00.000Z",
});
const events = new SqliteEventStore(authority, {
	newEventId: (() => {
		let sequence = 0;
		return () => `crash-event-${++sequence}`;
	})(),
	now: () => "2026-09-01T00:00:00.000Z",
});
const stream = { sessionId: "crash-session", actorId: "crash-actor" };
const inputFingerprint = createHash("sha256").update('{"admissionAuthority":null,"payload":null}').digest("hex");

authority.install("crash-probe", [
	{
		version: 1,
		name: "crash-probe",
		sql: "CREATE TABLE crash_probe(value TEXT PRIMARY KEY) STRICT;",
	},
]);
authority.write((database) => database.prepare("INSERT INTO crash_probe VALUES ('committed')").run());

await events.append(
	stream,
	"empty",
	{ ...stream, runId: "completed-run", turn: "0" },
	{
		type: "run.queued",
		inputId: "completed-input",
		inputFingerprint,
	},
);
await events.append(
	stream,
	"1",
	{ ...stream, runId: "completed-run", turn: "0" },
	{
		type: "run.completed",
		status: "completed",
	},
);
await events.append(
	stream,
	"2",
	{ ...stream, runId: "orphaned-run", turn: "0" },
	{
		type: "run.queued",
		inputId: "orphaned-input",
		inputFingerprint,
	},
);

authority.write((database) => {
	void events.append(
		stream,
		"3",
		{ ...stream, runId: "interrupted-run", turn: "0" },
		{
			type: "run.queued",
			inputId: "interrupted-input",
			inputFingerprint,
		},
	);
	database.prepare("INSERT INTO crash_probe VALUES ('uncommitted')").run();
	const visibleRevision = database
		.prepare("SELECT MAX(revision) FROM canonical_events")
		.pluck()
		.safeIntegers(true)
		.get();
	if (visibleRevision !== 4n) throw new Error("The interrupted event was not visible inside its transaction.");
	writeSync(1, "transaction-open\n");
	Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);
});
