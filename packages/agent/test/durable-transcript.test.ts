import { Buffer } from "node:buffer";
import { transformMessages } from "@earendil-works/pi-ai/api/transform-messages";
import type { Api, Message, Model } from "@earendil-works/pi-ai";
import { canonicalizeJsonValue, type DurableTranscriptMessage } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import {
	captureTranscriptMessage,
	DurableTranscript,
	type DurableTranscriptOptions,
	InMemoryTranscriptStore,
	type TranscriptCommitRequest,
	type TranscriptRecord,
	TranscriptRecordIdentityConflictError,
	type TranscriptRecordDraft,
	type TranscriptStore,
	TranscriptUnsupportedDeferredError,
	TranscriptUnsupportedMediaError,
} from "../src/index.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;

function createTranscript(
	store: TranscriptStore = new InMemoryTranscriptStore(),
	overrides: Partial<DurableTranscriptOptions> = {},
): DurableTranscript {
	let recordId = 0;
	return new DurableTranscript(store, {
		maxRecordBytes: 4_096,
		maxCommitBytes: 8_192,
		maxRestoreBytes: 16_384,
		maxJsonDepth: 8,
		newRecordId: () => {
			recordId += 1;
			return `record-${recordId}`;
		},
		...overrides,
	});
}

describe("DurableTranscript", () => {
	it.each([false, true])(
		"rejects a retained source without its immediate checkpoint (ordinary suffix: %s)",
		async (suffix) => {
			const coordinate = { ...STREAM, runId: "old-run", turn: "1" };
			const records: TranscriptRecord[] = [
				{
					schemaVersion: "6",
					recordId: "source",
					sequence: "42",
					type: "turn.closed",
					coordinate,
					status: "completed",
				},
			];
			if (suffix)
				records.push({
					schemaVersion: "6",
					recordId: "suffix",
					sequence: "43",
					type: "message.completed",
					coordinate: { ...STREAM, runId: "new-run", turn: "0" },
					message: { role: "user", contentFormat: "text", content: [{ type: "text", text: "next" }], timestamp: 1 },
				});
			const transcript = createTranscript({
				load: async () => ({ revision: suffix ? "43" : "42", records }),
				append: async () => {
					throw new Error("Restore must reject before repair.");
				},
			});
			await expect(transcript.restore(STREAM)).rejects.toThrow("checkpoint");
			await expect(transcript.materializeRevision(STREAM, suffix ? "43" : "42")).rejects.toThrow("checkpoint");
		},
	);
	it("stores an explicit transcript schema version", async () => {
		const transcript = createTranscript();
		const committed = await transcript.commit({
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

		expect(committed.records[0]).toMatchObject({ schemaVersion: "6" });
	});

	it("rejects a stored deferred final output that is only whitespace", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		await store.append(STREAM, "empty", [
			{
				schemaVersion: "6",
				recordId: "whitespace-output",
				sequence: "1",
				type: "deferred.opened",
				coordinate: { ...STREAM, runId: "run", turn: "0" },
				operationId: "operation",
				input: { inputId: "input", payload: {} },
				handle: { provider: "provider", modelId: "model", api: "api", id: "handle" },
				finalOutput: " \t",
			},
		]);

		await expect(transcript.restore(STREAM)).rejects.toThrow("invalid_transcript_record");
	});

	it("restores one open provider operation outside the Pi message projection", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "deferred-run", turn: "0" };
		const committed = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "start background work" }],
						timestamp: 1,
					},
				},
				{
					type: "deferred.opened",
					coordinate,
					operationId: "operation-1",
					input: { inputId: "input-1", payload: { scene: "square" } },
					handle: {
						provider: "provider",
						modelId: "model",
						api: "test-api",
						id: "provider-operation",
						pollAfterMs: 0,
						data: { cursor: "opaque" },
					},
				},
			],
		});

		const restored = await transcript.restore(STREAM);
		expect(restored).toMatchObject({
			revision: committed.revision,
			messages: [{ role: "user", content: "start background work" }],
			durableMessages: [{ role: "user", content: [{ type: "text", text: "start background work" }] }],
			pendingDeferred: {
				type: "deferred.opened",
				recordId: "record-2",
				operationId: "operation-1",
				input: { inputId: "input-1", payload: { scene: "square" } },
				handle: { id: "provider-operation", pollAfterMs: 0 },
			},
		});
		expect((await store.load(STREAM, 16_384)).records).toHaveLength(2);
	});

	it("settles an exact open provider operation with one final assistant message", async () => {
		const transcript = createTranscript();
		const coordinate = { ...STREAM, runId: "deferred-run", turn: "0" };
		const opened = await transcript.commit({
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
					input: { inputId: "input-1", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation" },
				},
			],
		});
		const deferredOpened = opened.records.find((record) => record.type === "deferred.opened");
		expect(deferredOpened).toBeDefined();
		const completed = await transcript.commit({
			stream: STREAM,
			expectedRevision: opened.revision,
			records: [
				{
					type: "deferred.completed",
					coordinate,
					operationId: "operation-1",
					openedRecordId: deferredOpened?.recordId ?? "missing-deferred-record",
					message: {
						role: "assistant",
						content: [{ type: "text", text: "ready" }],
						api: "test-api",
						provider: "provider",
						model: "model",
						stopReason: "stop",
						timestamp: 2,
					},
				},
				{ type: "turn.closed", coordinate, status: "completed" },
				{
					type: "deferred.run.closed",
					coordinate,
					operationId: "operation-1",
					inputId: "input-1",
					status: "completed",
				},
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			revision: completed.revision,
			messages: [
				{ role: "user", content: "wait" },
				{ role: "assistant", content: [{ text: "ready" }] },
			],
			durableMessages: [
				{ role: "user", content: [{ type: "text", text: "wait" }] },
				{ role: "assistant", content: [{ type: "text", text: "ready" }] },
			],
			terminalDeferredRuns: [
				{
					coordinate,
					operationId: "operation-1",
					inputId: "input-1",
					status: "completed",
				},
			],
		});
	});

	it("requires an explicit run terminal after a deferred Tool turn and keeps its final coordinate", async () => {
		const transcript = createTranscript();
		const first = { ...STREAM, runId: "deferred-tool-run", turn: "1" };
		const second = { ...STREAM, runId: "deferred-tool-run", turn: "2" };
		const opened = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: first,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "inspect" }],
						timestamp: 1,
					},
				},
				{
					type: "deferred.opened",
					coordinate: first,
					operationId: "operation-1",
					input: { inputId: "input-1", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation" },
				},
			],
		});
		const opening = opened.records.find((record) => record.type === "deferred.opened");
		if (!opening) throw new Error("Expected a deferred opening.");
		const toolTurn = await transcript.commit({
			stream: STREAM,
			expectedRevision: opened.revision,
			records: [
				{
					type: "deferred.completed",
					coordinate: first,
					operationId: opening.operationId,
					openedRecordId: opening.recordId,
					message: {
						role: "assistant",
						content: [{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} }],
						api: "test-api",
						provider: "provider",
						model: "model",
						stopReason: "toolUse",
						timestamp: 2,
					},
				},
				{
					type: "tool.completed",
					coordinate: first,
					assistantRecordId: "record-3",
					sourceIndex: 0,
					outcome: "not_started",
					message: {
						role: "toolResult",
						toolCallId: "call-1",
						toolName: "inspect",
						content: [{ type: "text", text: "ready" }],
						isError: false,
						timestamp: 3,
					},
				},
				{ type: "turn.closed", coordinate: first, status: "completed" },
			],
		});
		await expect(transcript.restore(STREAM)).resolves.toMatchObject({ terminalDeferredRuns: [] });

		await transcript.commit({
			stream: STREAM,
			expectedRevision: toolTurn.revision,
			records: [
				{
					type: "message.completed",
					coordinate: second,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "done" }],
						api: "test-api",
						provider: "provider",
						model: "model",
						stopReason: "stop",
						timestamp: 4,
					},
				},
				{ type: "turn.closed", coordinate: second, status: "completed" },
				{
					type: "deferred.run.closed",
					coordinate: second,
					operationId: opening.operationId,
					inputId: opening.input.inputId,
					status: "completed",
				},
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			terminalDeferredRuns: [
				{
					coordinate: second,
					operationId: "operation-1",
					inputId: "input-1",
					status: "completed",
				},
			],
		});
	});

	it("repairs a closed final deferred turn without a run terminal as failed", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "terminal-gap", turn: "1" };
		const opened = await transcript.commit({
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
					input: { inputId: "input-1", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation" },
				},
			],
		});
		const opening = opened.records.find((record) => record.type === "deferred.opened");
		if (!opening) throw new Error("Expected a deferred opening.");
		await transcript.commit({
			stream: STREAM,
			expectedRevision: opened.revision,
			records: [
				{
					type: "deferred.completed",
					coordinate,
					operationId: opening.operationId,
					openedRecordId: opening.recordId,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "done" }],
						api: "test-api",
						provider: "provider",
						model: "model",
						stopReason: "stop",
						timestamp: 2,
					},
				},
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			terminalDeferredRuns: [
				{
					coordinate,
					operationId: opening.operationId,
					inputId: "input-1",
					status: "failed",
				},
			],
		});
		expect((await store.load(STREAM, 16_384)).records.at(-1)).toMatchObject({
			type: "deferred.run.closed",
			status: "failed",
		});
	});

	it("repairs Tool calls from a deferred completion without requesting the provider again", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "deferred-tool-run", turn: "0" };
		const opened = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "inspect later" }],
						timestamp: 1,
					},
				},
				{
					type: "deferred.opened",
					coordinate,
					operationId: "operation-1",
					input: { inputId: "input-1", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation" },
				},
			],
		});
		const openedRecord = opened.records.find((record) => record.type === "deferred.opened");
		if (!openedRecord) throw new Error("Expected a deferred opening record.");
		const completed = await transcript.commit({
			stream: STREAM,
			expectedRevision: opened.revision,
			records: [
				{
					type: "deferred.completed",
					coordinate,
					operationId: openedRecord.operationId,
					openedRecordId: openedRecord.recordId,
					message: {
						role: "assistant",
						content: [
							{
								type: "toolCall",
								id: "call-1",
								name: "inspect",
								arguments: { target: "scene" },
							},
						],
						api: "test-api",
						provider: "provider",
						model: "model",
						stopReason: "toolUse",
						timestamp: 2,
					},
				},
			],
		});
		const completedRecord = completed.records[0];
		if (completedRecord.type !== "deferred.completed") throw new Error("Expected a deferred completion record.");

		const restored = await transcript.restore(STREAM);
		expect(restored).not.toHaveProperty("pendingDeferred");
		expect(restored.messages.at(-1)).toMatchObject({
			role: "toolResult",
			toolCallId: "call-1",
			isError: true,
			content: [{ text: expect.stringContaining("transcript_tool_not_started") }],
		});
		const stored = await store.load(STREAM, 16_384);
		expect(stored.records).toContainEqual(
			expect.objectContaining({
				type: "tool.completed",
				assistantRecordId: completedRecord.recordId,
				outcome: "not_started",
			}),
		);
		expect(stored.records.at(-2)).toMatchObject({ type: "turn.closed", status: "interrupted" });
		expect(stored.records.at(-1)).toMatchObject({ type: "deferred.run.closed", status: "failed" });
	});

	it("repairs an abandoned provider operation but rejects closing one that is still pending", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "deferred-abandon-run", turn: "0" };
		const opened = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "cancel later" }],
						timestamp: 1,
					},
				},
				{
					type: "deferred.opened",
					coordinate,
					operationId: "operation-1",
					input: { inputId: "input-1", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation" },
				},
			],
		});
		const openedRecord = opened.records.find((record) => record.type === "deferred.opened");
		if (!openedRecord) throw new Error("Expected a deferred opening record.");
		await transcript.commit({
			stream: STREAM,
			expectedRevision: opened.revision,
			records: [
				{
					type: "deferred.abandoned",
					coordinate,
					operationId: openedRecord.operationId,
					openedRecordId: openedRecord.recordId,
					reason: "cancelled",
				},
			],
		});

		const restored = await transcript.restore(STREAM);
		expect(restored.messages).toHaveLength(1);
		expect(restored).not.toHaveProperty("pendingDeferred");
		const repairedState = await store.load(STREAM, 16_384);
		const repairedRecords = repairedState.records;
		expect(repairedRecords.at(-2)).toMatchObject({
			type: "turn.closed",
			status: "interrupted",
		});
		expect(repairedRecords.at(-1)).toMatchObject({ type: "deferred.run.closed", status: "aborted" });
		for (const status of ["completed", "failed"] as const) {
			const corrupt = structuredClone(repairedState);
			const terminal = corrupt.records.at(-1);
			if (terminal?.type !== "deferred.run.closed") throw new Error("Expected a repaired deferred terminal.");
			terminal.status = status;
			const reader = createTranscript({
				load: async () => structuredClone(corrupt),
				append: async () => {
					throw new Error("Historical materialization must not append.");
				},
			});
			await expect(reader.materializeRevision(STREAM, "1")).rejects.toThrow("abandoned provider operation");
		}

		const pendingStore = new InMemoryTranscriptStore();
		const pendingTranscript = createTranscript(pendingStore);
		const pending = await pendingTranscript.commit({
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
					operationId: "operation-2",
					input: { inputId: "input-2", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation-2" },
				},
				{ type: "turn.closed", coordinate, status: "interrupted" },
			],
		});
		expect(pending.revision).toBe("3");
		await expect(pendingTranscript.restore(STREAM)).rejects.toThrow("pending provider operation");
	});

	it("rejects a second open provider operation and a mismatched terminal origin", async () => {
		const coordinate = { ...STREAM, runId: "deferred-run", turn: "0" };
		const base = [
			{
				schemaVersion: "6",
				type: "message.completed",
				coordinate,
				message: {
					role: "user",
					contentFormat: "text",
					content: [{ type: "text", text: "wait" }],
					timestamp: 1,
				},
				recordId: "user-record",
				sequence: "1",
			},
			{
				schemaVersion: "6",
				type: "deferred.opened",
				coordinate,
				operationId: "operation-1",
				input: { inputId: "input-1", payload: null },
				handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation" },
				recordId: "opened-record",
				sequence: "2",
			},
		] as unknown as TranscriptRecord[];
		const secondOpenStore: TranscriptStore = {
			load: async () => ({
				revision: "3",
				records: [
					...base,
					{ ...base[1], operationId: "operation-2", recordId: "second-open", sequence: "3" },
				] as TranscriptRecord[],
			}),
			append: async () => {
				throw new Error("unused");
			},
		};
		await expect(createTranscript(secondOpenStore).restore(STREAM)).rejects.toThrow("open provider operation");

		const wrongOriginStore: TranscriptStore = {
			load: async () => ({
				revision: "3",
				records: [
					...base,
					{
						schemaVersion: "6",
						type: "deferred.completed",
						coordinate,
						operationId: "operation-1",
						openedRecordId: "opened-record",
						message: {
							role: "assistant",
							content: [{ type: "text", text: "ready" }],
							api: "other-api",
							provider: "provider",
							model: "model",
							stopReason: "stop",
							timestamp: 2,
						},
						recordId: "completed-record",
						sequence: "3",
					},
				] as TranscriptRecord[],
			}),
			append: async () => {
				throw new Error("unused");
			},
		};
		await expect(createTranscript(wrongOriginStore).restore(STREAM)).rejects.toThrow("provider origin");

		const wrongReplayStore: TranscriptStore = {
			load: async () => ({
				revision: "3",
				records: [
					...base,
					{
						schemaVersion: "6",
						type: "deferred.completed",
						coordinate,
						operationId: "operation-1",
						openedRecordId: "opened-record",
						message: {
							role: "assistant",
							content: [{ type: "text", text: "ready" }],
							api: "test-api",
							provider: "provider",
							model: "model",
							providerReplay: {
								format: "pi",
								version: "1",
								api: "other-api",
								provider: "provider",
								model: "model",
								stopReason: "stop",
								blocks: [{ type: "text" }],
							},
							stopReason: "stop",
							timestamp: 2,
						},
						recordId: "completed-record",
						sequence: "3",
					},
				] as TranscriptRecord[],
			}),
			append: async () => {
				throw new Error("unused");
			},
		};
		await expect(createTranscript(wrongReplayStore).restore(STREAM)).rejects.toThrow("replay envelope");
	});

	it("applies the record budget to the complete stored envelope", async () => {
		const draft: TranscriptRecordDraft = {
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "hello" }],
				timestamp: 1,
			},
		};
		const draftBytes = Buffer.byteLength(canonicalizeJsonValue({ schemaVersion: "6", ...draft }));
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: draftBytes,
			maxCommitBytes: 8_192,
			maxRestoreBytes: 16_384,
			maxJsonDepth: 8,
			newRecordId: () => "record-1",
		});

		await expect(transcript.commit({ stream: STREAM, expectedRevision: "empty", records: [draft] })).rejects.toThrow(
			"byte budget",
		);
	});

	it("stops preparing a commit as soon as its byte budget is exhausted", async () => {
		let generatedIds = 0;
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 4_096,
			maxCommitBytes: 1,
			maxRestoreBytes: 16_384,
			maxJsonDepth: 8,
			newRecordId: () => {
				generatedIds += 1;
				return `record-${generatedIds}`;
			},
		});
		const draft: TranscriptRecordDraft = {
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "hello" }],
				timestamp: 1,
			},
		};
		const records = Array.from({ length: 1_000 }, () => draft) as unknown as TranscriptCommitRequest["records"];

		await expect(transcript.commit({ stream: STREAM, expectedRevision: "empty", records })).rejects.toThrow(
			"commit exceeds",
		);
		expect(generatedIds).toBe(1);
	});

	it("rejects an invalid commit record list before calling the store", async () => {
		let appendCalls = 0;
		const store: TranscriptStore = {
			load: async () => ({ revision: "empty", records: [] }),
			append: async () => {
				appendCalls += 1;
				return { revision: "empty", records: [] };
			},
		};
		const invalidRecords = new Proxy([] as TranscriptRecordDraft[], {
			get(target, property, receiver) {
				if (property === "length") return Number.NaN;
				return Reflect.get(target, property, receiver);
			},
		}) as unknown as TranscriptCommitRequest["records"];

		await expect(
			createTranscript(store).commit({ stream: STREAM, expectedRevision: "empty", records: invalidRecords }),
		).rejects.toThrow("record list");
		expect(appendCalls).toBe(0);
	});

	it("bounds an expected revision before assigning IDs or calling the store", async () => {
		let generatedIds = 0;
		let appendCalls = 0;
		const store: TranscriptStore = {
			load: async () => ({ revision: "empty", records: [] }),
			append: async () => {
				appendCalls += 1;
				return { revision: "empty", records: [] };
			},
		};
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 16,
			maxCommitBytes: 4_096,
			maxRestoreBytes: 4_096,
			maxJsonDepth: 8,
			newRecordId: () => {
				generatedIds += 1;
				return `record-${generatedIds}`;
			},
		});
		const records: TranscriptCommitRequest["records"] = [
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
		];

		await expect(transcript.commit({ stream: STREAM, expectedRevision: "1".repeat(17), records })).rejects.toThrow(
			"revision exceeds",
		);
		expect({ generatedIds, appendCalls }).toEqual({ generatedIds: 0, appendCalls: 0 });
	});

	it("rejects a committed record addressed to another stream", async () => {
		const transcript = createTranscript();

		await expect(
			transcript.commit({
				stream: STREAM,
				expectedRevision: "empty",
				records: [
					{
						type: "message.completed",
						coordinate: { sessionId: "other-session", actorId: "actor", runId: "run", turn: "0" },
						message: {
							role: "user",
							contentFormat: "text",
							content: [{ type: "text", text: "hello" }],
							timestamp: 1,
						},
					},
				],
			}),
		).rejects.toThrow("invalid_stream_coordinate");
	});

	it("rejects duplicate record identity atomically in the in-memory adapter", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 4_096,
			maxCommitBytes: 8_192,
			maxRestoreBytes: 16_384,
			maxJsonDepth: 8,
			newRecordId: () => "duplicate-record",
		});
		const draft: TranscriptRecordDraft = {
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "hello" }],
				timestamp: 1,
			},
		};

		await expect(
			transcript.commit({ stream: STREAM, expectedRevision: "empty", records: [draft, draft] }),
		).rejects.toBeInstanceOf(TranscriptRecordIdentityConflictError);
		await expect(transcript.restore(STREAM)).resolves.toMatchObject({ revision: "empty", messages: [] });
	});

	it("rejects reuse of a historical in-memory record identity without changing the head", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 4_096,
			maxCommitBytes: 8_192,
			maxRestoreBytes: 16_384,
			maxJsonDepth: 8,
			newRecordId: () => "stable-record",
		});
		const draft: TranscriptRecordDraft = {
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "hello" }],
				timestamp: 1,
			},
		};
		await transcript.commit({ stream: STREAM, expectedRevision: "empty", records: [draft] });

		await expect(transcript.commit({ stream: STREAM, expectedRevision: "1", records: [draft] })).rejects.toBeInstanceOf(
			TranscriptRecordIdentityConflictError,
		);
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({
			revision: "1",
			records: [{ message: { role: "user" } }],
		});
	});

	it("returns the same input snapshot that the in-memory adapter committed", async () => {
		const store = new InMemoryTranscriptStore();
		let messageReads = 0;
		const dynamic = {
			schemaVersion: "6",
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "0" },
			recordId: "record-1",
			sequence: "1",
		} as unknown as TranscriptRecord;
		Object.defineProperty(dynamic, "message", {
			enumerable: true,
			get() {
				messageReads += 1;
				return {
					role: "user",
					contentFormat: "text",
					content: [{ type: "text", text: messageReads === 1 ? "committed" : "changed" }],
					timestamp: 1,
				};
			},
		});

		await expect(store.append(STREAM, "empty", [dynamic])).resolves.toMatchObject({
			revision: "1",
			records: [{ message: { content: [{ text: "committed" }] } }],
		});
		expect(messageReads).toBe(1);
		await expect(store.load(STREAM, 4_096)).resolves.toMatchObject({
			records: [{ message: { content: [{ text: "committed" }] } }],
		});
	});

	it("captures stream and revision once before committing", async () => {
		let streamReads = 0;
		let revisionReads = 0;
		const request = {
			get stream() {
				streamReads += 1;
				return streamReads === 1 ? STREAM : { sessionId: "changed", actorId: "actor" };
			},
			get expectedRevision() {
				revisionReads += 1;
				return revisionReads === 1 ? ("empty" as const) : "9";
			},
			records: [
				{
					type: "message.completed" as const,
					coordinate: { ...STREAM, runId: "run", turn: "0" },
					message: {
						role: "user" as const,
						contentFormat: "text" as const,
						content: [{ type: "text" as const, text: "hello" }] as const,
						timestamp: 1,
					},
				},
			] as const,
		} as TranscriptCommitRequest;

		await expect(createTranscript().commit(request)).resolves.toMatchObject({ revision: "1" });
		expect({ streamReads, revisionReads }).toEqual({ streamReads: 1, revisionReads: 1 });
	});

	it("rejects a loaded record addressed to another stream", async () => {
		const record: TranscriptRecord = {
			schemaVersion: "6",
			type: "message.completed",
			coordinate: { sessionId: "other-session", actorId: "actor", runId: "run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "hello" }],
				timestamp: 1,
			},
			recordId: "record-1",
			sequence: "1",
		};
		const store: TranscriptStore = {
			load: async () => ({ revision: "1", records: [record] }),
			append: async () => ({ revision: "1", records: [record] }),
		};

		await expect(createTranscript(store).restore(STREAM)).rejects.toThrow("invalid_stream_coordinate");
	});

	it("stops restoring as soon as its byte budget is exhausted", async () => {
		const record: TranscriptRecord = {
			schemaVersion: "6",
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "hello" }],
				timestamp: 1,
			},
			recordId: "record-1",
			sequence: "1",
		};
		let recordReads = 0;
		const records = new Proxy(
			Array.from({ length: 1_000 }, () => record),
			{
				get(target, property, receiver) {
					if (typeof property === "string" && /^(0|[1-9][0-9]*)$/u.test(property)) recordReads += 1;
					return Reflect.get(target, property, receiver);
				},
			},
		);
		const store: TranscriptStore = {
			load: async () => ({ revision: "1000", records }),
			append: async () => ({ revision: "empty", records: [] }),
		};
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 4_096,
			maxCommitBytes: 8_192,
			maxRestoreBytes: 1,
			maxJsonDepth: 8,
		});

		await expect(transcript.restore(STREAM)).rejects.toThrow("restore budget");
		expect(recordReads).toBe(1);
	});

	it("rejects an invalid restored record list instead of treating it as empty", async () => {
		const records = new Proxy([] as TranscriptRecord[], {
			get(target, property, receiver) {
				if (property === "length") return Number.NaN;
				return Reflect.get(target, property, receiver);
			},
		});
		const store: TranscriptStore = {
			load: async () => ({ revision: "empty", records }),
			append: async () => ({ revision: "empty", records: [] }),
		};

		await expect(createTranscript(store).restore(STREAM)).rejects.toThrow("record list");
	});

	it("bounds a loaded revision before reading its record list", async () => {
		let recordListReads = 0;
		const store: TranscriptStore = {
			load: async () => ({
				revision: "1".repeat(4_097),
				get records() {
					recordListReads += 1;
					return [];
				},
			}),
			append: async () => ({ revision: "empty", records: [] }),
		};

		await expect(createTranscript(store).restore(STREAM)).rejects.toThrow("revision exceeds");
		expect(recordListReads).toBe(0);
	});

	it("does not materialize records beyond the store's declared committed head", async () => {
		const sourceStore = new InMemoryTranscriptStore();
		const source = createTranscript(sourceStore);
		await source.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: { ...STREAM, runId: "run", turn: "0" },
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "committed prefix" }],
						timestamp: 1,
					},
				},
				{
					type: "turn.closed",
					coordinate: { ...STREAM, runId: "run", turn: "0" },
					status: "interrupted",
				},
			],
		});
		const stored = await sourceStore.load(STREAM, 16_384);
		const tornStore: TranscriptStore = {
			load: async () => ({ revision: "1", records: stored.records }),
			append: async () => {
				throw new Error("Historical materialization must not append.");
			},
		};
		const transcript = createTranscript(tornStore);

		await expect(transcript.materializeRevision(STREAM, "2")).rejects.toThrow("head does not match");
		await expect(transcript.materializeRevision(STREAM, "1")).rejects.toThrow("head does not match");
	});

	it("rejects a historical revision when a later committed suffix violates stream invariants", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "0" } as const;
		const prefix = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "committed prefix" }],
						timestamp: 1,
					},
				},
				{ type: "turn.closed", coordinate, status: "interrupted" },
			],
		});
		await transcript.commit({
			stream: STREAM,
			expectedRevision: prefix.revision,
			records: [
				{
					type: "deferred.run.closed",
					coordinate,
					operationId: "missing-operation",
					inputId: "missing-input",
					status: "failed",
				},
			],
		});

		await expect(transcript.materializeRevision(STREAM, "1")).rejects.toThrow("deferred run terminal does not match");
	});

	it("rejects stale, displaced, or post-terminal records in a deferred run suffix", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const first = { ...STREAM, runId: "multi-deferred-run", turn: "1" } as const;
		const second = { ...STREAM, runId: "multi-deferred-run", turn: "2" } as const;
		const openedFirst = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: first,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "wait twice" }],
						timestamp: 1,
					},
				},
				{
					type: "deferred.opened",
					coordinate: first,
					operationId: "operation-1",
					input: { inputId: "input-1", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation-1" },
				},
			],
		});
		const firstOrigin = openedFirst.records[1];
		if (firstOrigin?.type !== "deferred.opened") throw new Error("Expected the first deferred origin.");
		const completedFirst = await transcript.commit({
			stream: STREAM,
			expectedRevision: openedFirst.revision,
			records: [
				{
					type: "deferred.completed",
					coordinate: first,
					operationId: firstOrigin.operationId,
					openedRecordId: firstOrigin.recordId,
					message: {
						role: "assistant",
						content: [{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} }],
						api: "test-api",
						provider: "provider",
						model: "model",
						stopReason: "toolUse",
						timestamp: 2,
					},
				},
			],
		});
		const firstCompletion = completedFirst.records[0];
		if (firstCompletion.type !== "deferred.completed") throw new Error("Expected the first deferred completion.");
		const openedSecond = await transcript.commit({
			stream: STREAM,
			expectedRevision: completedFirst.revision,
			records: [
				{
					type: "tool.completed",
					coordinate: first,
					assistantRecordId: firstCompletion.recordId,
					sourceIndex: 0,
					outcome: "not_started",
					message: {
						role: "toolResult",
						toolCallId: "call-1",
						toolName: "inspect",
						content: [{ type: "text", text: "ready" }],
						isError: false,
						timestamp: 3,
					},
				},
				{ type: "turn.closed", coordinate: first, status: "completed" },
				{
					type: "deferred.opened",
					coordinate: second,
					operationId: "operation-2",
					input: { inputId: "input-2", payload: null },
					handle: { provider: "provider", modelId: "model", api: "test-api", id: "provider-operation-2" },
				},
			],
		});
		const secondOrigin = openedSecond.records[2];
		if (secondOrigin?.type !== "deferred.opened") throw new Error("Expected the second deferred origin.");
		await transcript.commit({
			stream: STREAM,
			expectedRevision: openedSecond.revision,
			records: [
				{
					type: "deferred.completed",
					coordinate: second,
					operationId: secondOrigin.operationId,
					openedRecordId: secondOrigin.recordId,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "done" }],
						api: "test-api",
						provider: "provider",
						model: "model",
						stopReason: "stop",
						timestamp: 4,
					},
				},
				{ type: "turn.closed", coordinate: second, status: "completed" },
				{
					type: "deferred.run.closed",
					coordinate: second,
					operationId: secondOrigin.operationId,
					inputId: secondOrigin.input.inputId,
					status: "completed",
				},
			],
		});
		const valid = await store.load(STREAM, 16_384);
		const materializeFromState = (state: typeof valid, revision: string) =>
			createTranscript({
				load: async () => structuredClone(state),
				append: async () => {
					throw new Error("Historical materialization must not append.");
				},
			}).materializeRevision(STREAM, revision);
		await expect(transcript.materializeRevision(STREAM, "1")).resolves.toMatchObject({ revision: "1" });

		const stale = structuredClone(valid);
		const staleTerminal = stale.records.at(-1);
		if (staleTerminal?.type !== "deferred.run.closed") throw new Error("Expected a deferred run terminal.");
		staleTerminal.operationId = firstOrigin.operationId;
		staleTerminal.inputId = firstOrigin.input.inputId;
		await expect(materializeFromState(stale, "1")).rejects.toThrow("latest provider operation");

		const displaced = structuredClone(valid);
		const displacedTerminal = displaced.records.at(-1);
		if (displacedTerminal?.type !== "deferred.run.closed") throw new Error("Expected a deferred run terminal.");
		displacedTerminal.coordinate = first;
		await expect(materializeFromState(displaced, "1")).rejects.toThrow("final turn closure");

		const afterTerminal: typeof valid = {
			revision: "10",
			records: [
				...structuredClone(valid.records),
				{
					schemaVersion: "6",
					type: "message.completed",
					coordinate: { ...second, turn: "3" },
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "too late" }],
						timestamp: 5,
					},
					recordId: "post-terminal-record",
					sequence: "10",
				},
			],
		};
		await expect(materializeFromState(afterTerminal, "1")).rejects.toThrow("after its deferred run terminal");
	});

	it("rejects image content before it can enter the current text-only transcript", () => {
		expect(() =>
			captureTranscriptMessage({ role: "user", content: "ignored", timestamp: 1 }, [
				{
					type: "image-reference",
					attachmentId: `img_${"a".repeat(64)}`,
					mediaType: "image/png",
					bytes: 1,
					width: 1,
					height: 1,
				},
			]),
		).toThrow(TranscriptUnsupportedMediaError);
	});

	it("round-trips Pi text replay metadata through the durable transcript", async () => {
		const transcript = createTranscript();
		const coordinate = { ...STREAM, runId: "run-replay", turn: "1" } as const;
		const captured = captureTranscriptMessage({
			role: "assistant",
			content: [{ type: "text", text: "answer", textSignature: "private-continuation" }],
			api: "openai-responses",
			provider: "openai",
			model: "model",
			responseModel: "resolved-model",
			responseId: "response-1",
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "stop",
			timestamp: 1,
		});
		if (captured?.role !== "assistant") throw new Error("expected assistant transcript message");
		await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{ type: "message.completed", coordinate, message: captured },
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			messages: [
				{
					role: "assistant",
					content: [{ type: "text", text: "answer", textSignature: "private-continuation" }],
					responseModel: "resolved-model",
					responseId: "response-1",
				},
			],
		});
	});

	it("round-trips ordered signed and redacted Pi thinking", async () => {
		const transcript = createTranscript();
		const coordinate = { ...STREAM, runId: "run-thinking", turn: "1" } as const;
		const captured = captureTranscriptMessage({
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "private", thinkingSignature: "opaque", redacted: true },
				{ type: "text", text: "answer" },
			],
			api: "anthropic-messages",
			provider: "anthropic",
			model: "model",
			providerThinkingLevel: "high",
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "stop",
			timestamp: 1,
		});
		if (captured?.role !== "assistant") throw new Error("expected assistant transcript message");
		await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{ type: "message.completed", coordinate, message: captured },
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			messages: [
				{
					role: "assistant",
					providerThinkingLevel: "high",
					content: [
						{ type: "thinking", thinking: "private", thinkingSignature: "opaque", redacted: true },
						{ type: "text", text: "answer" },
					],
				},
			],
		});
	});

	it("keeps unsigned Pi thinking as provider-neutral durable content", () => {
		const captured = captureTranscriptMessage({
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "private" },
				{ type: "text", text: "answer" },
			],
			api: "anthropic-messages",
			provider: "anthropic",
			model: "model",
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "stop",
			timestamp: 1,
		});

		expect(captured).toMatchObject({
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "private" },
				{ type: "text", text: "answer" },
			],
		});
		expect(captured).not.toHaveProperty("providerReplay");
	});

	it.each(["high", "", "provider-specific-effort", undefined])(
		"restores optional provider thinking effort %s without other replay metadata",
		async (providerThinkingLevel) => {
			const store = new InMemoryTranscriptStore();
			const transcript = createTranscript(store);
			const coordinate = { ...STREAM, runId: "run-effort", turn: "1" };
			const source = {
				role: "assistant" as const,
				content: [{ type: "text" as const, text: "answer" }],
				api: "anthropic-messages",
				provider: "anthropic",
				model: "model",
				...(providerThinkingLevel === undefined ? {} : { providerThinkingLevel }),
				usage: {
					input: 0,
					output: 0,
					cacheRead: 0,
					cacheWrite: 0,
					totalTokens: 0,
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
				stopReason: "stop" as const,
				timestamp: 1,
			};
			const captured = captureTranscriptMessage(source);
			if (captured?.role !== "assistant") throw new Error("expected assistant transcript message");
			if (providerThinkingLevel === undefined) expect(captured).not.toHaveProperty("providerReplay");
			else expect(captured).toMatchObject({ providerReplay: { providerThinkingLevel } });
			expect(captured).not.toHaveProperty("providerThinkingLevel");
			await transcript.commit({
				stream: STREAM,
				expectedRevision: "empty",
				records: [
					{ type: "message.completed", coordinate, message: captured },
					{ type: "turn.closed", coordinate, status: "completed" },
				],
			});

			const restored = await createTranscript(store).restore(STREAM);
			if (providerThinkingLevel === undefined) expect(restored.messages[0]).not.toHaveProperty("providerThinkingLevel");
			else expect(restored.messages[0]).toMatchObject({ role: "assistant", providerThinkingLevel });
		},
	);

	it.each([
		{ providerReplay: { providerThinkingLevel: null } },
		{ providerReplay: { providerThinkingLevel: 1 } },
		{ providerReplay: { providerThinkingLevel: {} } },
		{ providerReplay: { providerThinkingLevel: "high", unknown: true } },
		{ providerThinkingLevel: "forged" },
	])("rejects malformed thinking effort at commit and restore: %j", async (invalid) => {
		const store = new InMemoryTranscriptStore();
		const message = {
			role: "assistant",
			content: [{ type: "text", text: "answer" }],
			api: "anthropic-messages",
			provider: "anthropic",
			model: "model",
			stopReason: "stop",
			timestamp: 1,
			...invalid,
			providerReplay: {
				format: "pi",
				version: "1",
				api: "anthropic-messages",
				provider: "anthropic",
				model: "model",
				stopReason: "stop",
				blocks: [{ type: "text" }],
				...invalid.providerReplay,
			},
		};
		const draft = {
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "1" },
			message,
		} as unknown as TranscriptRecordDraft;
		await expect(
			createTranscript(store).commit({ stream: STREAM, expectedRevision: "empty", records: [draft] }),
		).rejects.toThrow();
		await expect(store.load(STREAM, 16_384)).resolves.toEqual({ revision: "empty", records: [] });
		const corrupted = createTranscript({
			load: async () => ({
				revision: "1",
				records: [{ schemaVersion: "6", recordId: "record-1", sequence: "1", ...draft }],
			}),
			append: async () => {
				throw new Error("Corrupt input must not trigger recovery writes.");
			},
		});
		await expect(corrupted.restore(STREAM)).rejects.toThrow("invalid_transcript_record");
		await expect(corrupted.materialize(STREAM, [message as unknown as DurableTranscriptMessage])).rejects.toThrow();
	});

	it.each([
		{ api: "other-api" },
		{ provider: "other-provider" },
		{ model: "other-model" },
		{ stopReason: "length" },
		{ blocks: [] },
		{ blocks: [{ type: "thinking", thinkingSignature: "foreign-signature" }] },
	])("rejects mismatched replay before public materialization: %j", async (mismatch) => {
		const message = {
			role: "assistant",
			content: [{ type: "text", text: "answer" }],
			api: "anthropic-messages",
			provider: "anthropic",
			model: "model",
			stopReason: "stop",
			timestamp: 1,
			providerReplay: {
				format: "pi",
				version: "1",
				api: "anthropic-messages",
				provider: "anthropic",
				model: "model",
				providerThinkingLevel: "high",
				stopReason: "stop",
				blocks: [{ type: "text" }],
				...mismatch,
			},
		} as DurableTranscriptMessage;
		await expect(createTranscript().materialize(STREAM, [message])).rejects.toThrow("does not match");
	});

	it.each([0, -1])("counts thinking effort in the complete record byte budget at delta %i", async (delta) => {
		const store = new InMemoryTranscriptStore();
		const draft: TranscriptRecordDraft = {
			type: "message.completed",
			coordinate: { ...STREAM, runId: "run", turn: "1" },
			message: {
				role: "assistant",
				content: [],
				api: "anthropic-messages",
				provider: "anthropic",
				model: "model",
				stopReason: "stop",
				timestamp: 1,
				providerReplay: {
					format: "pi",
					version: "1",
					api: "anthropic-messages",
					provider: "anthropic",
					model: "model",
					providerThinkingLevel: "推理强度",
					stopReason: "stop",
					blocks: [],
				},
			},
		};
		const recordBytes = Buffer.byteLength(
			canonicalizeJsonValue({ schemaVersion: "6", recordId: "record-1", sequence: "1", ...draft }),
		);
		const transcript = createTranscript(store, { maxRecordBytes: recordBytes + delta });
		const commit = transcript.commit({ stream: STREAM, expectedRevision: "empty", records: [draft] });
		if (delta === 0) await expect(commit).resolves.toMatchObject({ revision: "1" });
		else {
			await expect(commit).rejects.toThrow("byte budget");
			await expect(store.load(STREAM, 16_384)).resolves.toEqual({ revision: "empty", records: [] });
		}
	});

	it("preserves defined empty Pi signatures in the replay envelope", () => {
		const captured = captureTranscriptMessage({
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "private", thinkingSignature: "" },
				{ type: "text", text: "checking", textSignature: "" },
				{ type: "toolCall", id: "call-1", name: "inspect", arguments: {}, thoughtSignature: "" },
			],
			api: "anthropic-messages",
			provider: "anthropic",
			model: "model",
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "toolUse",
			timestamp: 1,
		});

		expect(captured).toMatchObject({
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "private" },
				{ type: "text", text: "checking" },
				{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} },
			],
			providerReplay: {
				blocks: [
					{ type: "thinking", thinkingSignature: "" },
					{ type: "text", textSignature: "" },
					{ type: "toolCall", thoughtSignature: "" },
				],
			},
		});
	});

	it("round-trips Pi Tool thought signatures without changing durable Tool identity", async () => {
		const transcript = createTranscript();
		const coordinate = { ...STREAM, runId: "run-tool-replay", turn: "1" } as const;
		const captured = captureTranscriptMessage({
			role: "assistant",
			content: [
				{ type: "text", text: "checking" },
				{
					type: "toolCall",
					id: "call-1",
					name: "inspect",
					arguments: { target: "gate" },
					thoughtSignature: "private-thought",
				},
			],
			api: "openai-responses",
			provider: "openai",
			model: "model",
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "toolUse",
			timestamp: 2,
		});
		if (captured?.role !== "assistant") throw new Error("expected assistant transcript message");
		const assistant = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [{ type: "message.completed", coordinate, message: captured }],
		});
		await transcript.commit({
			stream: STREAM,
			expectedRevision: assistant.revision,
			records: [
				{
					type: "tool.completed",
					coordinate,
					assistantRecordId: assistant.records[0].recordId,
					sourceIndex: 1,
					outcome: "not_started",
					message: {
						role: "toolResult",
						toolCallId: "call-1",
						toolName: "inspect",
						content: [{ type: "text", text: "not started" }],
						isError: true,
						timestamp: 3,
					},
				},
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			messages: [
				{
					role: "assistant",
					content: [
						{ type: "text", text: "checking" },
						{
							type: "toolCall",
							id: "call-1",
							name: "inspect",
							arguments: { target: "gate" },
							thoughtSignature: "private-thought",
						},
					],
				},
				{ role: "toolResult", toolCallId: "call-1" },
			],
		});
	});

	it("rejects a misaligned replay block before committing it", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run-corrupt-replay", turn: "1" } as const;
		await expect(
			transcript.commit({
				stream: STREAM,
				expectedRevision: "empty",
				records: [
					{
						type: "message.completed",
						coordinate,
						message: {
							role: "assistant",
							content: [{ type: "text", text: "authoritative" }],
							api: "openai-responses",
							provider: "openai",
							model: "model",
							providerReplay: {
								format: "pi",
								version: "1",
								api: "openai-responses",
								provider: "openai",
								model: "model",
								stopReason: "stop",
								blocks: [{ type: "toolCall", thoughtSignature: "misaligned" }],
							},
							stopReason: "stop",
							timestamp: 1,
						},
					},
					{ type: "turn.closed", coordinate, status: "completed" },
				],
			}),
		).rejects.toThrow("replay block does not match");
		await expect(store.load(STREAM, 16_384)).resolves.toEqual({ revision: "empty", records: [] });
	});

	it("delegates every replay origin change to Pi's mature cross-model transformation", async () => {
		const transcript = createTranscript();
		const coordinate = { ...STREAM, runId: "run-cross-model", turn: "1" } as const;
		const captured = captureTranscriptMessage({
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "encrypted", thinkingSignature: "redacted-signature", redacted: true },
				{ type: "thinking", thinking: "ordinary reasoning", thinkingSignature: "thinking-signature" },
				{ type: "text", text: "answer", textSignature: "text-signature" },
				{
					type: "toolCall",
					id: "call-1",
					name: "inspect",
					arguments: {},
					thoughtSignature: "tool-signature",
				},
			],
			api: "openai-responses",
			provider: "openai",
			model: "source-model",
			providerThinkingLevel: "high",
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "toolUse",
			timestamp: 1,
		});
		if (captured?.role !== "assistant") throw new Error("Expected an assistant transcript message.");
		const assistantCommit = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [{ type: "message.completed", coordinate, message: captured }],
		});
		await transcript.commit({
			stream: STREAM,
			expectedRevision: assistantCommit.revision,
			records: [
				{
					type: "tool.completed",
					coordinate,
					assistantRecordId: assistantCommit.records[0].recordId,
					sourceIndex: 3,
					outcome: "not_started",
					message: {
						role: "toolResult",
						toolCallId: "call-1",
						toolName: "inspect",
						content: [{ type: "text", text: "not started" }],
						isError: true,
						timestamp: 2,
					},
				},
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});
		const restored = await transcript.restore(STREAM);
		const targetBase = {
			name: "Target",
			baseUrl: "https://example.invalid",
			reasoning: true,
			input: ["text"] as Array<"text" | "image">,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
			contextWindow: 1,
			maxTokens: 1,
		};
		const targets: readonly Model<Api>[] = [
			{ ...targetBase, id: "source-model", api: "openai-completions", provider: "openai" },
			{ ...targetBase, id: "source-model", api: "openai-responses", provider: "other-provider" },
			{ ...targetBase, id: "other-model", api: "openai-responses", provider: "openai" },
		];
		for (const target of targets) {
			const transformed = transformMessages([...restored.messages] as Message[], target);
			const assistant = transformed.find((message) => message.role === "assistant");
			if (assistant?.role !== "assistant") throw new Error("Expected Pi to retain the assistant message.");
			expect(assistant.providerThinkingLevel).toBe("high");
			expect(assistant.content).toEqual([
				{ type: "text", text: "ordinary reasoning" },
				{ type: "text", text: "answer" },
				{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} },
			]);
		}
	});

	it("describes deferred output as a pending-operation lifecycle gap", () => {
		const captureDeferred = () =>
			captureTranscriptMessage({
				role: "assistant",
				content: [{ type: "text", text: "queued" }],
				api: "openai-responses",
				provider: "openai",
				model: "model",
				usage: {
					input: 0,
					output: 0,
					cacheRead: 0,
					cacheWrite: 0,
					totalTokens: 0,
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
				stopReason: "deferred",
				timestamp: 1,
			});
		expect(captureDeferred).toThrow(TranscriptUnsupportedDeferredError);
		expect(captureDeferred).toThrow("pending-operation lifecycle adapter");
	});

	it("appends recovery records individually when their aggregate exceeds the commit budget", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 4_096,
			maxCommitBytes: 1_000,
			maxRestoreBytes: 16_384,
			maxJsonDepth: 8,
			newRecordId: () => `record-${++recordId}`,
		});
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: ["1", "2", "3"].map((id) => ({
							type: "toolCall" as const,
							id: `call-${id}`,
							name: "inspect",
							arguments: {},
						})),
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "toolUse",
						timestamp: 1,
					},
				},
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({ revision: "5" });
		const stored = await store.load(STREAM, 16_384);
		const repair = stored.records.slice(1);
		expect(repair.map((record) => record.type)).toEqual([
			"tool.completed",
			"tool.completed",
			"tool.completed",
			"turn.closed",
		]);
		expect(repair.reduce((sum, record) => sum + Buffer.byteLength(canonicalizeJsonValue(record)), 0)).toBeGreaterThan(
			1_000,
		);
		expect(repair.every((record) => Buffer.byteLength(canonicalizeJsonValue(record)) <= 1_000)).toBe(true);
	});

	it("does not append a repair that would make the stream exceed its restore budget", async () => {
		const store = new InMemoryTranscriptStore();
		const writer = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		await writer.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "toolUse",
						timestamp: 1,
					},
				},
			],
		});
		const prefix = await store.load(STREAM, 16_384);
		const prefixBytes = Buffer.byteLength(canonicalizeJsonValue(prefix.records[0]));
		let repairRecordId = 1;
		const bounded = createTranscript(store, {
			maxRestoreBytes: prefixBytes,
			newRecordId: () => `record-${++repairRecordId}`,
		});

		await expect(bounded.restore(STREAM)).rejects.toThrow("restore budget");
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: "1" });
	});

	it("rejects duplicate repair identities before appending any repair prefix", async () => {
		const store = new InMemoryTranscriptStore();
		const writer = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		await writer.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: ["1", "2"].map((id) => ({
							type: "toolCall" as const,
							id: `call-${id}`,
							name: "inspect",
							arguments: {},
						})),
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "toolUse",
						timestamp: 1,
					},
				},
			],
		});
		const restorer = createTranscript(store, { newRecordId: () => "repair-record" });

		await expect(restorer.restore(STREAM)).rejects.toBeInstanceOf(TranscriptRecordIdentityConflictError);
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: "1" });
	});

	it("detaches started Tool recovery input from the authoritative prefix", async () => {
		const store = new InMemoryTranscriptStore();
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const writer = createTranscript(store);
		const prefix = await writer.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "toolCall", id: "call-1", name: "write", arguments: { nested: { value: 1 } } }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "toolUse",
						timestamp: 1,
					},
				},
			],
		});
		const assistantRecordId = prefix.records[0]?.recordId;
		if (!assistantRecordId) throw new Error("Expected an assistant record.");
		await writer.commit({
			stream: STREAM,
			expectedRevision: prefix.revision,
			records: [
				{
					type: "tool.started",
					coordinate,
					assistantRecordId,
					sourceIndex: 0,
					toolCallId: "call-1",
					toolName: "write",
					arguments: { nested: { value: 1 } },
					descriptorFingerprint: "a".repeat(64),
					catalogRevision: "catalog",
					policyRevision: "policy",
					operationId: "operation-1",
				},
			],
		});
		let repairRecordId = 2;
		const transcript = createTranscript(store, {
			newRecordId: () => `record-${++repairRecordId}`,
			recoverStartedTool: ({ start, toolCall }) => {
				(start.arguments as { nested: { value: number } }).nested.value = 2;
				(toolCall.arguments as { nested: { value: number } }).nested.value = 3;
				return {
					outcome: "completed",
					content: [{ type: "text", text: "done" }],
					isError: false,
					timestamp: 2,
				};
			},
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			revision: "4",
			messages: [
				{
					role: "assistant",
					content: [{ type: "toolCall", id: "call-1", arguments: { nested: { value: 1 } } }],
				},
				{ role: "toolResult", toolCallId: "call-1" },
			],
		});
		await expect(transcript.restore(STREAM)).resolves.toMatchObject({ revision: "4" });
	});

	it("rejects a completed closure without a terminal assistant message", async () => {
		const transcript = createTranscript();
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "unanswered" }],
						timestamp: 1,
					},
				},
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});

		await expect(transcript.restore(STREAM)).rejects.toThrow("terminal assistant");
	});

	it("repairs a mixed incomplete Tool batch in assistant source order and only once", async () => {
		const store = new InMemoryTranscriptStore();
		let recoveryCalls = 0;
		const transcript = createTranscript(store, {
			recoverStartedTool: async ({ start, toolCall }) => {
				recoveryCalls += 1;
				expect(start.operationId).toBe("operation-2");
				expect(toolCall).toMatchObject({ id: "call-2", name: "write" });
				return {
					outcome: "completed",
					content: [{ type: "text", text: "authoritative receipt" }],
					isError: false,
					timestamp: 4,
				};
			},
		});
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const prefix = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "act" }],
						timestamp: 1,
					},
				},
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [
							{ type: "toolCall", id: "call-1", name: "read", arguments: { key: "gate" } },
							{ type: "toolCall", id: "call-2", name: "write", arguments: { open: true } },
						],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "toolUse",
						timestamp: 2,
					},
				},
			],
		});
		const assistantRecordId = prefix.records[1]?.recordId;
		if (!assistantRecordId) throw new Error("Expected an assistant record.");
		await transcript.commit({
			stream: STREAM,
			expectedRevision: prefix.revision,
			records: [
				{
					type: "tool.started",
					coordinate,
					assistantRecordId,
					sourceIndex: 1,
					toolCallId: "call-2",
					toolName: "write",
					arguments: { open: true },
					descriptorFingerprint: "a".repeat(64),
					catalogRevision: "catalog",
					policyRevision: "policy",
					operationId: "operation-2",
				},
			],
		});

		const restored = await transcript.restore(STREAM);
		expect(restored.revision).toBe("6");
		expect(restored.messages).toMatchObject([
			{ role: "user", content: "act" },
			{
				role: "assistant",
				content: [
					{ type: "toolCall", id: "call-1" },
					{ type: "toolCall", id: "call-2" },
				],
			},
			{
				role: "toolResult",
				toolCallId: "call-1",
				isError: true,
				content: [{ text: expect.stringContaining("transcript_tool_not_started") }],
			},
			{
				role: "toolResult",
				toolCallId: "call-2",
				isError: false,
				content: [{ text: "authoritative receipt" }],
			},
		]);
		const stored = await store.load(STREAM, 16_384);
		expect(stored.records.slice(3).map((record) => record.type)).toEqual([
			"tool.completed",
			"tool.completed",
			"turn.closed",
		]);
		expect(
			stored.records.slice(3, 5).map((record) => (record.type === "tool.completed" ? record.outcome : "")),
		).toEqual(["not_started", "completed"]);

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({ revision: "6" });
		expect(recoveryCalls).toBe(1);
	});

	it("records outcome_unknown instead of replaying a started Tool without an authoritative receipt", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const prefix = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "toolCall", id: "call-1", name: "write", arguments: {} }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "toolUse",
						timestamp: 1,
					},
				},
			],
		});
		const assistantRecordId = prefix.records[0]?.recordId;
		if (!assistantRecordId) throw new Error("Expected an assistant record.");
		await transcript.commit({
			stream: STREAM,
			expectedRevision: prefix.revision,
			records: [
				{
					type: "tool.started",
					coordinate,
					assistantRecordId,
					sourceIndex: 0,
					toolCallId: "call-1",
					toolName: "write",
					arguments: {},
					descriptorFingerprint: "b".repeat(64),
					catalogRevision: "catalog",
					policyRevision: "policy",
				},
			],
		});

		const restored = await transcript.restore(STREAM);
		expect(restored.messages.at(-1)).toMatchObject({
			role: "toolResult",
			toolCallId: "call-1",
			isError: true,
			content: [{ text: expect.stringContaining("transcript_tool_outcome_unknown") }],
		});
		const stored = await store.load(STREAM, 16_384);
		expect(stored.records).toContainEqual(
			expect.objectContaining({ type: "tool.completed", outcome: "outcome_unknown" }),
		);
	});

	it("restores the newest checkpoint replacement plus the later canonical suffix", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const firstCoordinate = { ...STREAM, runId: "run-1", turn: "1" } as const;
		const first = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: firstCoordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "old question" }],
						timestamp: 1,
					},
				},
				{
					type: "message.completed",
					coordinate: firstCoordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "old answer" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "stop",
						timestamp: 2,
					},
				},
				{ type: "turn.closed", coordinate: firstCoordinate, status: "completed" },
			],
		});
		const sourceClosure = first.records[2];
		if (sourceClosure?.type !== "turn.closed") throw new Error("Expected a closed source turn.");
		const checkpoint = await transcript.checkpoint({
			stream: STREAM,
			source: sourceClosure,
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "Earlier conversation summary." }],
					timestamp: 3,
				},
			],
		});
		expect(checkpoint.revision).toBe("4");

		const secondCoordinate = { ...STREAM, runId: "run-2", turn: "1" } as const;
		await transcript.commit({
			stream: STREAM,
			expectedRevision: checkpoint.revision,
			records: [
				{
					type: "message.completed",
					coordinate: secondCoordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "new question" }],
						timestamp: 4,
					},
				},
				{
					type: "message.completed",
					coordinate: secondCoordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "new answer" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "stop",
						timestamp: 5,
					},
				},
				{ type: "turn.closed", coordinate: secondCoordinate, status: "completed" },
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			revision: "7",
			checkpointRevision: "4",
			messages: [
				{ role: "user", content: [{ type: "text", text: "Earlier conversation summary." }] },
				{ role: "user", content: "new question" },
				{ role: "assistant", content: [{ type: "text", text: "new answer" }] },
			],
		});
		await expect(
			transcript.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 1,
				retainRevisions: ["empty"],
				retainBudget: { maxCount: 1, maxBytes: 4 },
			}),
		).rejects.toThrow("retainRevisions exceeds retainBudget.maxBytes.");
		await expect(
			transcript.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 1,
				retainRevisions: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toMatchObject({ purgedRecordCount: 0, earliestAvailableRevision: "1", latestRevision: "7" });
		await expect(
			transcript.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 2,
				retainRevisions: ["1"],
				retainBudget: { maxCount: 1, maxBytes: 1 },
			}),
		).resolves.toMatchObject({ purgedRecordCount: 0 });
		await expect(
			transcript.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 2,
				retainRevisions: [sourceClosure.sequence],
				retainBudget: { maxCount: 1, maxBytes: Buffer.byteLength(sourceClosure.sequence, "utf8") },
			}),
		).resolves.toMatchObject({ purgedRecordCount: 0 });
		await expect(
			transcript.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.revision,
				limit: 2,
				retainRevisions: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual({ purgedRecordCount: 2, earliestAvailableRevision: "3", latestRevision: "7" });
		await expect(transcript.materializeRevision(STREAM, "1")).rejects.toThrow("unavailable");
		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			revision: "7",
			checkpointRevision: "4",
			messages: [
				{ role: "user", content: [{ type: "text", text: "Earlier conversation summary." }] },
				{ role: "user", content: "new question" },
				{ role: "assistant", content: [{ type: "text", text: "new answer" }] },
			],
		});
		const raw = await store.load(STREAM, 16_384);
		expect(raw.records.map((record) => record.type)).toEqual([
			"turn.closed",
			"context.checkpoint",
			"message.completed",
			"message.completed",
			"turn.closed",
		]);
	});

	it("does not append a checkpoint whose replacement contains an orphan Tool result", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "done" }],
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
		if (closure?.type !== "turn.closed") throw new Error("Expected a closure.");

		await expect(
			transcript.checkpoint({
				stream: STREAM,
				source: closure,
				replacement: [
					{
						role: "toolResult",
						toolCallId: "orphan",
						toolName: "inspect",
						content: [{ type: "text", text: "orphan" }],
						isError: false,
						timestamp: 2,
					},
				],
			}),
		).rejects.toThrow("orphan Tool result");
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: "2" });
	});

	it("rejects an interrupted source closure before appending a checkpoint", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "interrupted" }],
						timestamp: 1,
					},
				},
				{ type: "turn.closed", coordinate, status: "interrupted" },
			],
		});
		const closure = source.records[1];
		if (closure?.type !== "turn.closed") throw new Error("Expected an interrupted closure.");

		await expect(
			transcript.checkpoint({
				stream: STREAM,
				source: closure,
				replacement: [
					{
						role: "user",
						contentFormat: "parts",
						content: [{ type: "text", text: "summary" }],
						timestamp: 2,
					},
				],
			}),
		).rejects.toThrow("completed turn closure");
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: "2" });
	});

	it.each([
		["source revision", { sourceRevision: "2" }],
		["source record", { sourceRecordId: "wrong-closure" }],
		["source coordinate", { coordinate: { ...STREAM, runId: "other-run", turn: "1" } }],
	] as const)("fails closed when a committed checkpoint references the wrong %s", async (_label, override) => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "question" }],
						timestamp: 1,
					},
				},
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "answer" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "stop",
						timestamp: 2,
					},
				},
				{ type: "turn.closed", coordinate, status: "completed" },
			],
		});
		const closure = source.records[2];
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed closure.");
		const checkpoint: TranscriptRecord = {
			schemaVersion: "6",
			recordId: "corrupt-checkpoint",
			sequence: "4",
			type: "context.checkpoint",
			coordinate,
			sourceRevision: closure.sequence,
			sourceRecordId: closure.recordId,
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "summary" }],
					timestamp: 3,
				},
			],
			...override,
		};
		await store.append(STREAM, source.revision, [checkpoint]);

		await expect(
			transcript.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: checkpoint.sequence,
				limit: 3,
				retainRevisions: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).rejects.toThrow("checkpoint source boundary is invalid");
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: "4", records: { length: 4 } });
		await expect(transcript.restore(STREAM)).rejects.toThrow("checkpoint source boundary is invalid");
	});

	it("does not permit the generic commit API to append a checkpoint", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "answer" }],
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
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed closure.");
		const records = [
			{
				type: "context.checkpoint",
				coordinate,
				sourceRevision: closure.sequence,
				sourceRecordId: closure.recordId,
				replacement: [
					{
						role: "user",
						contentFormat: "parts",
						content: [{ type: "text", text: "summary" }],
						timestamp: 2,
					},
				],
			} as unknown as TranscriptRecordDraft,
		] as [TranscriptRecordDraft];

		await expect(
			transcript.commit({
				stream: STREAM,
				expectedRevision: source.revision,
				records,
			}),
		).rejects.toThrow("checkpoint API");
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: "2" });
	});

	it("rejects a misaligned replay envelope before appending a checkpoint", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run-checkpoint-replay", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "answer" }],
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
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed closure.");

		await expect(
			transcript.checkpoint({
				stream: STREAM,
				source: closure,
				replacement: [
					{
						role: "assistant",
						content: [{ type: "text", text: "retained" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						providerReplay: {
							format: "pi",
							version: "1",
							api: "openai-responses",
							provider: "openai",
							model: "model",
							stopReason: "stop",
							blocks: [{ type: "thinking", thinkingSignature: "misaligned" }],
						},
						stopReason: "stop",
						timestamp: 2,
					},
				],
			}),
		).rejects.toThrow("replay block does not match");
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: source.revision });
	});

	it("uses one generic record snapshot for type authority and commit", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "answer" }],
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
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed closure.");
		const ordinary: TranscriptRecordDraft = {
			type: "message.completed",
			coordinate: { ...STREAM, runId: "next-run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "next" }],
				timestamp: 2,
			},
		};
		const checkpoint = {
			type: "context.checkpoint",
			coordinate,
			sourceRevision: closure.sequence,
			sourceRecordId: closure.recordId,
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "summary" }],
					timestamp: 2,
				},
			],
		} as unknown as TranscriptRecordDraft;
		let reads = 0;
		const records = [ordinary] as [TranscriptRecordDraft];
		Object.defineProperty(records, "0", {
			enumerable: true,
			get: () => {
				reads += 1;
				return reads === 1 ? ordinary : checkpoint;
			},
		});

		const committed = await transcript.commit({
			stream: STREAM,
			expectedRevision: source.revision,
			records,
		});

		expect(reads).toBe(1);
		expect(committed.records[0].type).toBe("message.completed");
		const stored = await store.load(STREAM, 16_384);
		expect(stored.records.some((record) => record.type === "context.checkpoint")).toBe(false);
	});

	it("fails closed when a stored checkpoint follows an interrupted closure", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "interrupted" }],
						timestamp: 1,
					},
				},
				{ type: "turn.closed", coordinate, status: "interrupted" },
			],
		});
		const closure = source.records[1];
		if (closure?.type !== "turn.closed") throw new Error("Expected an interrupted closure.");
		await store.append(STREAM, source.revision, [
			{
				schemaVersion: "6",
				recordId: "corrupt-checkpoint",
				sequence: "3",
				type: "context.checkpoint",
				coordinate,
				sourceRevision: closure.sequence,
				sourceRecordId: closure.recordId,
				replacement: [
					{
						role: "user",
						contentFormat: "parts",
						content: [{ type: "text", text: "summary" }],
						timestamp: 2,
					},
				],
			},
		]);

		await expect(transcript.restore(STREAM)).rejects.toThrow("checkpoint source boundary is invalid");
	});

	it("fails closed when a stored checkpoint replacement has an orphan Tool result", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "answer" }],
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
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed closure.");
		await store.append(STREAM, source.revision, [
			{
				schemaVersion: "6",
				recordId: "corrupt-checkpoint",
				sequence: "3",
				type: "context.checkpoint",
				coordinate,
				sourceRevision: closure.sequence,
				sourceRecordId: closure.recordId,
				replacement: [
					{
						role: "toolResult",
						toolCallId: "orphan",
						toolName: "inspect",
						content: [{ type: "text", text: "orphan" }],
						isError: false,
						timestamp: 2,
					},
				],
			},
		]);

		await expect(
			transcript.purgeBeforeCheckpoint({
				...STREAM,
				checkpointRevision: "3",
				limit: 2,
				retainRevisions: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).rejects.toThrow("orphan Tool result");
		await expect(store.load(STREAM, 16_384)).resolves.toMatchObject({ revision: "3", records: { length: 3 } });
		await expect(transcript.restore(STREAM)).rejects.toThrow("orphan Tool result");
	});

	it("uses the newest complete checkpoint replacement", async () => {
		const store = new InMemoryTranscriptStore();
		const transcript = createTranscript(store);
		const firstCoordinate = { ...STREAM, runId: "run-1", turn: "1" } as const;
		const first = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: firstCoordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "first" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "stop",
						timestamp: 1,
					},
				},
				{ type: "turn.closed", coordinate: firstCoordinate, status: "completed" },
			],
		});
		const firstClosure = first.records[1];
		if (firstClosure?.type !== "turn.closed") throw new Error("Expected the first closure.");
		const firstCheckpoint = await transcript.checkpoint({
			stream: STREAM,
			source: firstClosure,
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "first summary" }],
					timestamp: 2,
				},
			],
		});
		const secondCoordinate = { ...STREAM, runId: "run-2", turn: "1" } as const;
		const second = await transcript.commit({
			stream: STREAM,
			expectedRevision: firstCheckpoint.revision,
			records: [
				{
					type: "message.completed",
					coordinate: secondCoordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "second" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "stop",
						timestamp: 3,
					},
				},
				{ type: "turn.closed", coordinate: secondCoordinate, status: "completed" },
			],
		});
		const secondClosure = second.records[1];
		if (secondClosure?.type !== "turn.closed") throw new Error("Expected the second closure.");
		await transcript.checkpoint({
			stream: STREAM,
			source: secondClosure,
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "second summary" }],
					timestamp: 4,
				},
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			messages: [{ role: "user", content: [{ type: "text", text: "second summary" }] }],
		});
		const stored = await store.load(STREAM, 16_384);
		expect(stored.records.filter((record) => record.type === "context.checkpoint")).toHaveLength(2);
	});

	it("preserves provider replay metadata in a checkpoint and later committed suffix", async () => {
		const transcript = createTranscript();
		const firstCoordinate = { ...STREAM, runId: "run-before-checkpoint", turn: "1" } as const;
		const first = await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: firstCoordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "old history" }],
						timestamp: 1,
					},
				},
				{
					type: "message.completed",
					coordinate: firstCoordinate,
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
				{ type: "turn.closed", coordinate: firstCoordinate, status: "completed" },
			],
		});
		const closure = first.records[2];
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed closure.");
		const replayAssistant: DurableTranscriptMessage = {
			role: "assistant",
			content: [{ type: "text", text: "retained answer" }],
			api: "openai-responses",
			provider: "openai",
			model: "model",
			providerReplay: {
				format: "pi",
				version: "1",
				api: "openai-responses",
				provider: "openai",
				model: "model",
				responseId: "retained-response",
				stopReason: "stop",
				blocks: [{ type: "text", textSignature: "retained-signature" }],
			},
			stopReason: "stop",
			timestamp: 2,
		};
		const checkpoint = await transcript.checkpoint({
			stream: STREAM,
			source: closure,
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "summary" }],
					timestamp: 2,
				},
				replayAssistant,
			],
		});
		const laterCoordinate = { ...STREAM, runId: "run-after-checkpoint", turn: "1" } as const;
		await transcript.commit({
			stream: STREAM,
			expectedRevision: checkpoint.revision,
			records: [
				{
					type: "message.completed",
					coordinate: laterCoordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "later" }],
						timestamp: 3,
					},
				},
				{
					type: "message.completed",
					coordinate: laterCoordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "later answer" }],
						api: "openai-responses",
						provider: "openai",
						model: "model",
						stopReason: "stop",
						timestamp: 4,
					},
				},
				{ type: "turn.closed", coordinate: laterCoordinate, status: "completed" },
			],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			messages: [
				{ role: "user", content: [{ type: "text", text: "summary" }] },
				{
					role: "assistant",
					content: [{ type: "text", text: "retained answer", textSignature: "retained-signature" }],
					responseId: "retained-response",
				},
				{ role: "user", content: "later" },
				{ role: "assistant", content: [{ type: "text", text: "later answer" }] },
			],
		});
	});

	it.each([0, -1] as const)("applies the complete checkpoint envelope budget at delta %i", async (delta) => {
		const store = new InMemoryTranscriptStore();
		const writer = createTranscript(store);
		const coordinate = { ...STREAM, runId: "run", turn: "1" } as const;
		const source = await writer.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate,
					message: {
						role: "assistant",
						content: [{ type: "text", text: "answer" }],
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
		if (closure?.type !== "turn.closed") throw new Error("Expected a completed closure.");
		const replacement: readonly DurableTranscriptMessage[] = [
			{
				role: "user",
				contentFormat: "parts",
				content: [{ type: "text", text: "summary" }],
				timestamp: 2,
			},
		];
		const envelopeBytes = Buffer.byteLength(
			canonicalizeJsonValue({
				schemaVersion: "6",
				recordId: "checkpoint-record",
				sequence: "3",
				type: "context.checkpoint",
				coordinate,
				sourceRevision: closure.sequence,
				sourceRecordId: closure.recordId,
				replacement,
			}),
		);
		const budget = envelopeBytes + delta;
		const bounded = createTranscript(store, {
			maxRecordBytes: budget,
			maxCommitBytes: budget,
			newRecordId: () => "checkpoint-record",
		});
		const checkpoint = bounded.checkpoint({ stream: STREAM, source: closure, replacement });

		if (delta === 0) await expect(checkpoint).resolves.toMatchObject({ revision: "3" });
		else await expect(checkpoint).rejects.toThrow("byte budget");
	});

	it("rejects an orphan Tool result during restore", async () => {
		const transcript = createTranscript();
		await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "tool.completed",
					coordinate: { ...STREAM, runId: "run", turn: "1" },
					assistantRecordId: "missing-assistant",
					sourceIndex: 0,
					outcome: "not_started",
					message: {
						role: "toolResult",
						toolCallId: "call-1",
						toolName: "inspect",
						content: [{ type: "text", text: "not started" }],
						isError: true,
						timestamp: 1,
					},
				},
			],
		});

		await expect(transcript.restore(STREAM)).rejects.toThrow("orphan Tool result");
	});
});
