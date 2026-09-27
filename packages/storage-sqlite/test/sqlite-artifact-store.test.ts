import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
	ArtifactCapacityError,
	ArtifactConflictError,
	ArtifactCorruptionError,
	createArtifactRecord,
	digest,
	type ArtifactLimits,
} from "@opengameagent/artifacts";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteArtifactStore, SqliteAuthority } from "../src/index.js";

const directories: string[] = [];

const limits: ArtifactLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxContentTypeBytes: 128,
	maxArtifactBytes: 4096,
	maxArtifactsPerStream: 2,
	maxStoredBytesPerStream: 8192,
	maxInlineToolResultBytes: 32,
	maxReadBytes: 128,
	maxModelResultBytes: 1024,
	maxPurgeBatch: 2,
};

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("SqliteArtifactStore", () => {
	it("provides idempotent competing writes, conflict fencing, restart durability, and exact stream isolation", async () => {
		const path = await databasePath();
		const firstAuthority = authority(path);
		const secondAuthority = authority(path);
		const first = new SqliteArtifactStore(firstAuthority, limits);
		const second = new SqliteArtifactStore(secondAuthority, limits);
		const record = artifact("call", "durable content", "2026-08-29T01:00:00.000Z");

		const outcomes = await Promise.all([first.put(record), second.put(record)]);
		expect(outcomes.map((outcome) => outcome.status).sort()).toEqual(["created", "existing"]);
		await expect(first.put(artifact("call", "different content", record.createdAt))).rejects.toBeInstanceOf(
			ArtifactConflictError,
		);
		expect(await second.read(record, record.artifactId)).toEqual(record);
		expect(await second.read({ sessionId: record.sessionId, actorId: "other" }, record.artifactId)).toBeUndefined();

		firstAuthority.close();
		secondAuthority.close();
		const reopenedAuthority = authority(path);
		const reopened = new SqliteArtifactStore(reopenedAuthority, limits);
		expect(await reopened.read(record, record.artifactId)).toEqual(record);
		reopenedAuthority.close();
	});

	it("checks count and byte capacity atomically within each stream", async () => {
		const path = await databasePath();
		const constrained = { ...limits, maxArtifactsPerStream: 1, maxStoredBytesPerStream: 4096 };
		const firstAuthority = authority(path);
		const secondAuthority = authority(path);
		const first = new SqliteArtifactStore(firstAuthority, constrained);
		const second = new SqliteArtifactStore(secondAuthority, constrained);
		await first.put(artifact("first", "one", "2026-08-29T01:00:00.000Z", constrained));
		await expect(second.put(artifact("second", "two", "2026-08-29T01:00:01.000Z", constrained))).rejects.toBeInstanceOf(
			ArtifactCapacityError,
		);
		firstAuthority.close();
		secondAuthority.close();

		const bytePath = await databasePath();
		const byteLimits = {
			...limits,
			maxArtifactBytes: 64,
			maxArtifactsPerStream: 2,
			maxStoredBytesPerStream: 64,
		};
		const byteAuthority = authority(bytePath);
		const byteStore = new SqliteArtifactStore(byteAuthority, byteLimits);
		await byteStore.put(artifact("first", "x".repeat(40), "2026-08-29T01:00:00.000Z", byteLimits));
		await expect(
			byteStore.put(artifact("second", "y".repeat(40), "2026-08-29T01:00:01.000Z", byteLimits)),
		).rejects.toBeInstanceOf(ArtifactCapacityError);
		byteAuthority.close();
	});

	it("hydrates every purged row and rolls back deletion when persisted semantics are corrupt", async () => {
		const path = await databasePath();
		const sqlite = authority(path);
		const store = new SqliteArtifactStore(sqlite, limits);
		const record = artifact("call", "content", "2026-08-29T01:00:00.000Z");
		await store.put(record);
		sqlite.write((database) => {
			database
				.prepare("UPDATE oga_artifacts SET byte_length = byte_length + 1 WHERE artifact_id = ?")
				.run(record.artifactId);
		});

		await expect(store.read(record, record.artifactId)).rejects.toBeInstanceOf(ArtifactCorruptionError);
		await expect(store.purge(record, "2026-08-29T01:00:01.000Z", 1)).rejects.toBeInstanceOf(ArtifactCorruptionError);
		expect(
			sqlite.read(
				(database) => database.prepare("SELECT COUNT(*) AS count FROM oga_artifacts").get() as { count: number },
			).count,
		).toBe(1);
		sqlite.close();
	});

	it("purges only the requested actor partition, cutoff, and batch", async () => {
		const path = await databasePath();
		const sqlite = authority(path);
		const store = new SqliteArtifactStore(sqlite, limits);
		const old = artifact("old", "old", "2026-08-29T01:00:00.000Z");
		const current = artifact("current", "current", "2026-08-29T01:00:01.000Z");
		const other = artifact("other", "other", "2026-08-29T01:00:00.000Z", limits, "other");
		await store.put(old);
		await store.put(current);
		await store.put(other);

		await expect(store.purge(old, "2026-08-29T01:00:01.000Z", 1)).resolves.toBe(1);
		expect(await store.read(old, old.artifactId)).toBeUndefined();
		expect(await store.read(current, current.artifactId)).toEqual(current);
		expect(await store.read(other, other.artifactId)).toEqual(other);
		sqlite.close();
	});
});

function artifact(
	toolCallId: string,
	content: string,
	createdAt: string,
	configured: ArtifactLimits = limits,
	actorId = "actor",
) {
	const input = { sessionId: "session", actorId, inputId: "input", payload: null } as const;
	const coordinate = { sessionId: "session", actorId, runId: "run", turn: "1" } as const;
	return createArtifactRecord(
		{
			input,
			coordinate,
			toolCallId,
			toolName: "inspect",
			purpose: "tool_result",
			requestFingerprint: digest({ toolCallId }),
			contentType: "text/plain",
			content,
		},
		createdAt,
		configured,
	);
}

function authority(path: string): SqliteAuthority {
	return new SqliteAuthority({ path, busyTimeoutMs: 1_000, verifyOnOpen: "full" });
}

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-artifact-store-"));
	directories.push(directory);
	return join(directory, "authority.db");
}
