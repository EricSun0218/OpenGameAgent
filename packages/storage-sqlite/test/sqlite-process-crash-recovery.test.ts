import { type ChildProcess, spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { GameRuntime } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { TEST_MODEL } from "../../devtools/src/pi-fakes.js";
import { SqliteAuthority, SqliteEventStore } from "../src/index.js";

const STREAM = { sessionId: "crash-session", actorId: "crash-actor" } as const;
const READY_LINE = Buffer.from("transaction-open\n");
// One phase uses Vitest's locked non-browser default; the test allows readiness, termination, and restart recovery phases.
const PROCESS_PROGRESS_TIMEOUT_MS = 5_000;
const TEST_TIMEOUT_MS = PROCESS_PROGRESS_TIMEOUT_MS * 3;
const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const children: ChildProcess[] = [];

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-process-crash-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({
		path,
		busyTimeoutMs: 0,
		verifyOnOpen: "full",
		ownership: "exclusive",
		now: () => "2026-09-01T00:00:00.000Z",
	});
	authorities.push(authority);
	return authority;
}

async function waitForReady(child: ChildProcess): Promise<void> {
	if (!child.stdout) throw new Error("Expected the crash fixture stdout pipe.");
	const stdout = child.stdout;
	return new Promise((resolvePromise, rejectPromise) => {
		let buffered = Buffer.alloc(0);
		let settled = false;
		const timer = setTimeout(
			() => finish(new Error("Crash fixture readiness exceeded the external-process progress budget.")),
			PROCESS_PROGRESS_TIMEOUT_MS,
		);
		const onData = (chunk: Buffer) => {
			buffered = Buffer.concat([buffered, chunk]);
			if (buffered.byteLength > READY_LINE.byteLength) {
				finish(new Error("Crash fixture emitted an invalid readiness line."));
				return;
			}
			if (!buffered.includes(0x0a)) return;
			if (!buffered.equals(READY_LINE)) {
				finish(new Error("Crash fixture emitted an invalid readiness line."));
				return;
			}
			finish();
		};
		const onExit = (code: number | null, signal: NodeJS.Signals | null) =>
			finish(new Error(`Crash fixture exited before readiness: code=${String(code)} signal=${String(signal)}`));
		const onError = (error: Error) => finish(error);
		const onStdoutError = (error: Error) => finish(error);
		function finish(error?: Error): void {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			stdout.off("data", onData);
			stdout.off("error", onStdoutError);
			child.off("exit", onExit);
			child.off("error", onError);
			if (error) rejectPromise(error);
			else resolvePromise();
		}
		stdout.on("data", onData);
		stdout.once("error", onStdoutError);
		child.once("exit", onExit);
		child.once("error", onError);
	});
}

async function terminateAndWait(child: ChildProcess): Promise<{ code: number | null; signal: NodeJS.Signals | null }> {
	return new Promise((resolvePromise, rejectPromise) => {
		let settled = false;
		const timer = setTimeout(
			() => finish(new Error("Crash fixture termination exceeded the external-process progress budget.")),
			PROCESS_PROGRESS_TIMEOUT_MS,
		);
		const onClose = (code: number | null, signal: NodeJS.Signals | null) => finish(undefined, { code, signal });
		const onError = (error: Error) => finish(error);
		function finish(error?: Error, result?: { code: number | null; signal: NodeJS.Signals | null }): void {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			child.off("close", onClose);
			child.off("error", onError);
			if (error) rejectPromise(error);
			else if (result) resolvePromise(result);
			else rejectPromise(new Error("Crash fixture termination produced no result."));
		}
		child.once("close", onClose);
		child.once("error", onError);
		try {
			if (!child.kill("SIGKILL")) finish(new Error("Crash fixture could not be forcefully terminated."));
		} catch (error) {
			finish(error instanceof Error ? error : new Error("Crash fixture termination failed."));
		}
	});
}

afterEach(async () => {
	let cleanupFailure: unknown;
	for (const child of children.splice(0)) {
		if (child.exitCode !== null || child.signalCode !== null) continue;
		try {
			await terminateAndWait(child);
		} catch (error) {
			cleanupFailure ??= error;
		}
	}
	for (const authority of authorities.splice(0)) {
		try {
			authority.close();
		} catch (error) {
			cleanupFailure ??= error;
		}
	}
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) {
			cleanupFailure ??= new Error("Refusing to remove a non-temporary test directory.");
			continue;
		}
		try {
			await rm(target, { recursive: true, force: true });
		} catch (error) {
			cleanupFailure ??= error;
		}
	}
	if (cleanupFailure) throw cleanupFailure;
});

describe("SQLite process-crash recovery", () => {
	it(
		"restores only committed runtime facts and never replays an orphaned run",
		async () => {
			const path = await databasePath();
			const child = spawn(
				process.execPath,
				[
					"--experimental-transform-types",
					"--import",
					new URL("./fixtures/sqlite-source-resolver.mjs", import.meta.url).href,
					fileURLToPath(new URL("./fixtures/sqlite-runtime-crash.mjs", import.meta.url)),
					path,
				],
				{ stdio: ["ignore", "pipe", "inherit"] },
			);
			children.push(child);
			await expect(waitForReady(child)).resolves.toBeUndefined();

			await expect(terminateAndWait(child)).resolves.toEqual({ code: null, signal: "SIGKILL" });

			const authority = open(path);
			expect(
				authority.read((database) => database.prepare("SELECT value FROM crash_probe ORDER BY value").pluck().all()),
			).toEqual(["committed"]);
			const events = new SqliteEventStore(authority, {
				newEventId: () => "recovery-event",
				now: () => "2026-09-01T00:00:01.000Z",
			});
			await expect(events.readRevision(STREAM)).resolves.toBe("3");
			await expect(events.readRun(STREAM, "interrupted-input")).resolves.toBeUndefined();

			let providerCalls = 0;
			const runtime = new GameRuntime(events, {
				maxActiveActors: 1,
				maxQueuedRuns: 1,
				maxInputBytes: 4,
				maxInputDepth: 1,
				newRunId: () => "must-not-be-used",
			});
			await runtime.registerActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => {
					providerCalls += 1;
					throw new Error("The model must not replay a durable run after restart.");
				},
			});

			const completed = await runtime.submit({ ...STREAM, inputId: "completed-input", payload: null });
			expect(completed.runId).toBe("completed-run");
			await expect(completed.completion).resolves.toEqual({ runId: "completed-run", status: "completed" });
			const orphaned = await runtime.submit({ ...STREAM, inputId: "orphaned-input", payload: null });
			expect(orphaned.runId).toBe("orphaned-run");
			await expect(orphaned.completion).resolves.toEqual({ runId: "orphaned-run", status: "failed" });
			expect(providerCalls).toBe(0);

			await expect(events.readRun(STREAM, "orphaned-input")).resolves.toMatchObject({ status: "failed" });
			await expect(events.read(STREAM, { limit: 4 })).resolves.toMatchObject({
				cursor: "4",
				hasMore: false,
				events: [
					{ data: { type: "run.queued", inputId: "completed-input" } },
					{ data: { type: "run.completed", status: "completed" } },
					{ data: { type: "run.queued", inputId: "orphaned-input" } },
					{ data: { type: "run.completed", status: "failed" } },
				],
			});
		},
		TEST_TIMEOUT_MS,
	);
});
