import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { captureTranscriptMessage, DurableTranscript } from "@opengameagent/agent";
import {
	canonicalizeJsonValue,
	type DurableTranscriptRecord,
	type ImageReferenceContent,
	type JsonValue,
	type StreamKey,
	TranscriptRecordIdentityConflictError,
	TranscriptWriteConflictError,
} from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import { afterEach, describe, expect, it } from "vitest";
import { assistant } from "../../devtools/src/pi-fakes.js";
import { SqliteAuthority, SqliteTranscriptCorruptionError, SqliteTranscriptStore } from "../src/index.js";

const STREAM: StreamKey = { sessionId: "session", actorId: "actor" };
const OTHER_STREAM: StreamKey = { sessionId: "session", actorId: "other" };
const roots: string[] = [];
const authorities: SqliteAuthority[] = [];

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-transcript-store-test-"));
	roots.push(root);
	return join(root, "transcript.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
	authorities.push(authority);
	return authority;
}

function record(
	sequence: number,
	stream: StreamKey = STREAM,
	recordId = `record-${sequence}`,
): DurableTranscriptRecord {
	return {
		schemaVersion: "6",
		type: "message.completed",
		coordinate: { ...stream, runId: `run-${sequence}`, turn: "0" },
		message: {
			role: "user",
			contentFormat: "text",
			content: [{ type: "text", text: `message-${sequence}` }],
			timestamp: sequence,
		},
		recordId,
		sequence: sequence.toString(),
	};
}

function bytes(value: unknown): number {
	return Buffer.byteLength(canonicalizeJsonValue(value as JsonValue));
}

function boundMember(target: object, property: PropertyKey): unknown {
	const value = Reflect.get(target, property, target) as unknown;
	return typeof value === "function" ? value.bind(target) : value;
}

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteTranscriptStore", () => {
	it("retains provider thinking effort through restart, checkpoint, and revision materialization", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		let recordId = 0;
		const options = {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `replay-${++recordId}`,
		};
		const first = new DurableTranscript(new SqliteTranscriptStore(firstAuthority), options);
		const coordinate = { ...STREAM, runId: "replay-run", turn: "1" };
		const captured = captureTranscriptMessage({
			...assistant([{ type: "text", text: "retained answer", textSignature: "signed-text" }]),
			providerThinkingLevel: "high",
		});
		if (captured?.role !== "assistant") throw new Error("Expected an assistant message.");
		const committed = await first.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{ type: "message.completed", coordinate, message: captured },
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});
		firstAuthority.close();

		const secondAuthority = open(path);
		const second = new DurableTranscript(new SqliteTranscriptStore(secondAuthority), options);
		const restored = await second.restore(STREAM);
		expect(restored.messages).toMatchObject([
			{ providerThinkingLevel: "high", content: [{ textSignature: "signed-text" }] },
		]);
		await expect(second.materializeRevision(STREAM, committed.revision)).resolves.toMatchObject({
			messages: restored.messages,
		});
		const closure = committed.records[1];
		if (closure?.type !== "turn.closed") throw new Error("Expected a closed turn.");
		await second.checkpoint({
			stream: STREAM,
			source: closure,
			replacement: [
				{ role: "user", contentFormat: "parts", content: [{ type: "text", text: "summary" }], timestamp: 2 },
				...restored.durableMessages,
			],
		});
		secondAuthority.close();

		const third = new DurableTranscript(new SqliteTranscriptStore(open(path)), options);
		await expect(third.restore(STREAM)).resolves.toMatchObject({
			messages: [
				{ role: "user", content: [{ text: "summary" }] },
				{
					role: "assistant",
					providerThinkingLevel: "high",
					content: [{ text: "retained answer", textSignature: "signed-text" }],
				},
			],
		});
	});

	it("persists a complete ordered transcript across authority restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const firstStore = new SqliteTranscriptStore(firstAuthority);
		const records = [record(1), record(2)];

		await expect(firstStore.append(STREAM, "empty", records)).resolves.toMatchObject({ revision: "2" });
		firstAuthority.close();

		const restarted = new SqliteTranscriptStore(open(path));
		await expect(restarted.load(STREAM, bytes(records[0]) + bytes(records[1]))).resolves.toEqual({
			revision: "2",
			records,
		});
	});

	it("restores durable image references through the media Adapter after an authority restart", async () => {
		const path = await databasePath();
		const imageBytes = Buffer.from("sqlite-media");
		const reference: ImageReferenceContent = {
			type: "image-reference",
			attachmentId: `img_${createHash("sha256").update(imageBytes).digest("hex")}`,
			mediaType: "image/png",
			bytes: imageBytes.byteLength,
			width: 1,
			height: 1,
		};
		const media = {
			captureToolResult: async () => {
				throw new Error("unused");
			},
			read: async () => [{ type: "image" as const, data: imageBytes.toString("base64"), mimeType: "image/png" }],
		};
		let recordId = 0;
		const firstAuthority = open(path);
		const first = new DurableTranscript(new SqliteTranscriptStore(firstAuthority), {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `media-record-${++recordId}`,
			media,
		});
		await first.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: { ...STREAM, runId: "media-run", turn: "0" },
					message: { role: "user", contentFormat: "parts", content: [reference], timestamp: 1 },
				},
			],
		});
		firstAuthority.close();

		const restarted = new DurableTranscript(new SqliteTranscriptStore(open(path)), {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `repair-record-${++recordId}`,
			media,
		});
		await expect(restarted.restore(STREAM)).resolves.toMatchObject({
			durableMessages: [{ role: "user", content: [reference] }],
			messages: [
				{
					role: "user",
					content: [{ type: "image", data: imageBytes.toString("base64"), mimeType: "image/png" }],
				},
			],
		});
	});

	it("restores one open provider operation outside Pi history after an authority restart", async () => {
		const path = await databasePath();
		let recordId = 0;
		const firstAuthority = open(path);
		const first = new DurableTranscript(new SqliteTranscriptStore(firstAuthority), {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `deferred-record-${++recordId}`,
		});
		const coordinate = { ...STREAM, runId: "deferred-run", turn: "0" };
		await first.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "wait" }],
						timestamp: 1,
					},
				},
				{
					type: "deferred.opened",
					coordinate,
					operationId: "operation-1",
					input: { inputId: "input-1", payload: { scene: "square" } },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation" },
				},
			],
		});
		firstAuthority.close();

		const restarted = new DurableTranscript(new SqliteTranscriptStore(open(path)), {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `restart-record-${++recordId}`,
		});
		await expect(restarted.restore(STREAM)).resolves.toMatchObject({
			messages: [{ role: "user", content: "wait" }],
			pendingDeferred: {
				operationId: "operation-1",
				input: { inputId: "input-1", payload: { scene: "square" } },
				handle: { id: "provider-operation" },
			},
		});
	});

	it("uses one immediate transaction for revision CAS across connections", async () => {
		const path = await databasePath();
		const first = new SqliteTranscriptStore(open(path));
		const second = new SqliteTranscriptStore(open(path));

		await first.append(STREAM, "empty", [record(1)]);
		await expect(second.append(STREAM, "empty", [record(1, STREAM, "other-record")])).rejects.toEqual(
			new TranscriptWriteConflictError("empty", "1"),
		);
		await expect(second.load(STREAM, 4_096)).resolves.toMatchObject({ revision: "1" });
	});

	it("loads one WAL snapshot while another authority appends", async () => {
		const path = await databasePath();
		const readerAuthority = open(path);
		const writer = new SqliteTranscriptStore(open(path));
		await writer.append(STREAM, "empty", [record(1)]);
		const originalSnapshot = readerAuthority.snapshot.bind(readerAuthority);
		let writerAppend: Promise<unknown> | undefined;
		let interleaved = false;
		readerAuthority.snapshot = <T>(operation: (database: Database.Database) => T): T =>
			originalSnapshot((database) => operation(interleavingDatabase(database)));
		const reader = new SqliteTranscriptStore(readerAuthority);

		const restored = await reader.load(STREAM, 4_096);
		await writerAppend;

		expect(interleaved).toBe(true);
		expect(restored).toEqual({ revision: "1", records: [record(1)] });
		await expect(writer.load(STREAM, 4_096)).resolves.toEqual({ revision: "2", records: [record(1), record(2)] });

		function interleavingDatabase(database: Database.Database): Database.Database {
			return new Proxy(database, {
				get(target, property) {
					if (property !== "prepare") return boundMember(target, property);
					return (source: string) => {
						const statement = target.prepare(source);
						if (!source.includes("FROM oga_transcript_streams")) return statement;
						return new Proxy(statement, {
							get(statementTarget, statementProperty) {
								if (statementProperty !== "get") return boundMember(statementTarget, statementProperty);
								return (...parameters: unknown[]) => {
									const get = statementTarget.get.bind(statementTarget) as (...values: unknown[]) => unknown;
									const result = get(...parameters);
									if (!interleaved) {
										interleaved = true;
										writerAppend = writer.append(STREAM, "1", [record(2)]);
									}
									return result;
								};
							},
						});
					};
				},
			}) as Database.Database;
		}
	});

	it("rolls back the whole append when a later record conflicts", async () => {
		const store = new SqliteTranscriptStore(open(await databasePath()));

		await expect(
			store.append(STREAM, "empty", [record(1, STREAM, "duplicate"), record(2, STREAM, "duplicate")]),
		).rejects.toBeInstanceOf(TranscriptRecordIdentityConflictError);
		await expect(store.load(STREAM, 4_096)).resolves.toEqual({ revision: "empty", records: [] });
	});

	it("rejects a historical record identity without changing the SQLite head", async () => {
		const store = new SqliteTranscriptStore(open(await databasePath()));
		await store.append(STREAM, "empty", [record(1, STREAM, "stable-record")]);

		await expect(store.append(STREAM, "1", [record(2, STREAM, "stable-record")])).rejects.toBeInstanceOf(
			TranscriptRecordIdentityConflictError,
		);
		await expect(store.load(STREAM, 4_096)).resolves.toMatchObject({
			revision: "1",
			records: [record(1, STREAM, "stable-record")],
		});
	});

	it("scopes record identity to its transcript stream", async () => {
		const store = new SqliteTranscriptStore(open(await databasePath()));

		await store.append(STREAM, "empty", [record(1, STREAM, "shared-record")]);
		await store.append(OTHER_STREAM, "empty", [record(1, OTHER_STREAM, "shared-record")]);

		await expect(store.load(STREAM, 4_096)).resolves.toMatchObject({ revision: "1" });
		await expect(store.load(OTHER_STREAM, 4_096)).resolves.toMatchObject({ revision: "1" });
	});

	it("rejects cross-stream records before mutation", async () => {
		const store = new SqliteTranscriptStore(open(await databasePath()));

		await expect(store.append(STREAM, "empty", [record(1, OTHER_STREAM)])).rejects.toThrow("invalid_stream_coordinate");
		await expect(store.load(STREAM, 4_096)).resolves.toEqual({ revision: "empty", records: [] });
		await expect(store.load(OTHER_STREAM, 4_096)).resolves.toEqual({ revision: "empty", records: [] });
	});

	it("fails closed when persisted record content no longer matches its fingerprint", async () => {
		const authority = open(await databasePath());
		const store = new SqliteTranscriptStore(authority);
		await store.append(STREAM, "empty", [record(1)]);
		authority.write((database) => {
			const changed = record(1);
			if (changed.type !== "message.completed") throw new Error("Expected a message record.");
			const content = changed.message.content[0];
			if (content?.type !== "text") throw new Error("Expected text content.");
			content.text = "changed-1";
			database.prepare("UPDATE oga_transcript_records SET record_json = ?").run(canonicalizeJsonValue(changed));
		});

		await expect(store.load(STREAM, 4_096)).rejects.toBeInstanceOf(SqliteTranscriptCorruptionError);
	});

	it("fails closed when persisted row mirrors disagree with the record", async () => {
		const authority = open(await databasePath());
		const store = new SqliteTranscriptStore(authority);
		await store.append(STREAM, "empty", [record(1)]);
		authority.write((database) => {
			database.prepare("UPDATE oga_transcript_records SET run_id = 'other-run'").run();
		});

		await expect(store.load(STREAM, 4_096)).rejects.toBeInstanceOf(SqliteTranscriptCorruptionError);
	});

	it("checks the persisted aggregate before materializing records", async () => {
		const store = new SqliteTranscriptStore(open(await databasePath()));
		const records = [record(1), record(2)];
		const total = bytes(records[0]) + bytes(records[1]);
		await store.append(STREAM, "empty", records);

		await expect(store.load(STREAM, total - 1)).rejects.toThrow("restore budget");
		await expect(store.load(STREAM, total)).resolves.toMatchObject({ revision: "2" });
	});

	it("rejects understated stream bytes before hydrating the persisted rows", async () => {
		const authority = open(await databasePath());
		const store = new SqliteTranscriptStore(authority);
		await store.append(STREAM, "empty", [record(1), record(2)]);
		authority.write((database) => {
			database.prepare("UPDATE oga_transcript_streams SET stored_bytes = 1").run();
		});

		await expect(store.load(STREAM, 1)).rejects.toEqual(new SqliteTranscriptCorruptionError());
	});

	it("rejects revisions beyond SQLite's derived integer range before reading records", async () => {
		let recordReads = 0;
		const records = new Proxy([record(1)], {
			get(target, property, receiver) {
				if (property === "length" || property === "0") recordReads += 1;
				return Reflect.get(target, property, receiver);
			},
		});
		const store = new SqliteTranscriptStore(open(await databasePath()));

		await expect(store.append(STREAM, "1".repeat(20), records)).rejects.toThrow("signed 64-bit range");
		expect(recordReads).toBe(0);
	});

	it("captures the append record count once before mutation", async () => {
		let lengthReads = 0;
		const records = new Proxy([record(1)], {
			get(target, property, receiver) {
				if (property === "length") {
					lengthReads += 1;
					return lengthReads === 1 ? 1 : 2;
				}
				return Reflect.get(target, property, receiver);
			},
		});
		const store = new SqliteTranscriptStore(open(await databasePath()));

		await expect(store.append(STREAM, "empty", records)).resolves.toMatchObject({ revision: "1" });
		expect(lengthReads).toBe(1);
		await expect(store.load(STREAM, 4_096)).resolves.toMatchObject({ revision: "1", records: [record(1)] });
	});

	it("conforms to DurableTranscript commit and fresh restore", async () => {
		const path = await databasePath();
		let recordId = 0;
		const options = {
			maxRecordBytes: 4_096,
			maxCommitBytes: 8_192,
			maxRestoreBytes: 16_384,
			maxJsonDepth: 8,
			newRecordId: () => `record-${++recordId}`,
		};
		const firstAuthority = open(path);
		const first = new DurableTranscript(new SqliteTranscriptStore(firstAuthority), options);
		await first.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: { ...STREAM, runId: "run", turn: "0" },
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "hello" }],
						timestamp: 1,
					},
				},
			],
		});
		firstAuthority.close();

		const restored = await new DurableTranscript(new SqliteTranscriptStore(open(path)), options).restore(STREAM);
		expect(restored).toMatchObject({ revision: "2", messages: [{ role: "user", content: "hello" }] });
	});

	it("persists checkpoint replacement history without deleting its canonical source", async () => {
		const path = await databasePath();
		let recordId = 0;
		const options = {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		};
		const firstAuthority = open(path);
		const firstStore = new SqliteTranscriptStore(firstAuthority);
		const first = new DurableTranscript(firstStore, options);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await first.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "old answer" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "stop",
						timestamp: 1,
					},
				},
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});
		const closure = source.records[1];
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed source closure.");
		const checkpoint = await first.checkpoint({
			stream: STREAM,
			source: closure,
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "Earlier conversation summary." }],
					timestamp: 2,
				},
			],
		});
		await expect(
			first.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 1,
				retainRevisions: ["empty"],
				retainBudget: { maxCount: 1, maxBytes: 4 },
			}),
		).rejects.toThrow("retainRevisions exceeds retainBudget.maxBytes.");
		await expect(
			first.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 1,
				retainRevisions: ["1"],
				retainBudget: { maxCount: 1, maxBytes: 1 },
			}),
		).resolves.toMatchObject({ purgedRecordCount: 0 });
		await expect(
			first.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 1,
				retainRevisions: [closure.sequence],
				retainBudget: { maxCount: 1, maxBytes: Buffer.byteLength(closure.sequence, "utf8") },
			}),
		).resolves.toMatchObject({ purgedRecordCount: 0 });
		await expect(
			first.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 1,
				retainRevisions: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual({ purgedRecordCount: 1, earliestAvailableRevision: "2", latestRevision: "3" });
		firstAuthority.close();

		const restartedStore = new SqliteTranscriptStore(open(path));
		await expect(new DurableTranscript(restartedStore, options).restore(STREAM)).resolves.toMatchObject({
			revision: "3",
			messages: [{ role: "user", content: [{ type: "text", text: "Earlier conversation summary." }] }],
		});
		await expect(restartedStore.load(STREAM, 32_768)).resolves.toMatchObject({
			records: [{ type: "turn.closed" }, { type: "context.checkpoint" }],
		});
	});

	it.each(["coordinate", "replacement"] as const)(
		"rejects a checkpoint with an invalid %s before deleting SQLite history",
		async (corruption) => {
			const store = new SqliteTranscriptStore(open(await databasePath()));
			const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
			const records: DurableTranscriptRecord[] = [
				{
					schemaVersion: "6",
					recordId: "assistant",
					sequence: "1",
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "answer" }],
						api: "api",
						provider: "provider",
						model: "model",
						stopReason: "stop",
						timestamp: 1,
					},
				},
				{
					schemaVersion: "6",
					recordId: "closure",
					sequence: "2",
					type: "turn.closed",
					coordinate,
					status: "completed",
				},
				{
					schemaVersion: "6",
					recordId: "checkpoint",
					sequence: "3",
					type: "context.checkpoint",
					coordinate: corruption === "coordinate" ? { ...coordinate, runId: "other" } : coordinate,
					sourceRevision: "2",
					sourceRecordId: "closure",
					replacement:
						corruption === "replacement"
							? [
									{
										role: "toolResult",
										toolCallId: "orphan",
										toolName: "inspect",
										content: [{ type: "text", text: "orphan" }],
										isError: false,
										timestamp: 2,
									},
								]
							: [
									{
										role: "user",
										contentFormat: "parts",
										content: [{ type: "text", text: "summary" }],
										timestamp: 2,
									},
								],
				},
			];
			await store.append(STREAM, "empty", records);

			await expect(
				store.purgeBeforeCheckpoint({
					...STREAM,
					checkpointRevision: "3",
					limit: 1,
					retainRevisions: [],
					retainBudget: { maxCount: 0, maxBytes: 0 },
				}),
			).rejects.toThrow();
			await expect(store.load(STREAM, 32_768)).resolves.toMatchObject({ revision: "3", records: { length: 3 } });
		},
	);
});
