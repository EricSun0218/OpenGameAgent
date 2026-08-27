import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import type { GameMemory, GameMemoryEmbeddingProvider } from "./memory.js";
import { SqliteGameMemoryStore } from "./memory.js";

const directories: string[] = [];

function session(ownerId = "owner-a", actorId = "actor-a", generation = 1): GameSessionKey {
	return {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation,
		ownerId,
		sessionId: `session-${actorId}`,
		actorId,
	};
}

function memory(id: string, gameSession: GameSessionKey, overrides: Partial<GameMemory> = {}): GameMemory {
	return {
		id,
		session: gameSession,
		scope: "actor",
		kind: "observation",
		content: { id },
		searchText: `memory ${id}`,
		importance: 0.5,
		moment: { tick: 10 },
		createdAt: 1,
		...overrides,
	};
}

function vectorFor(text: string): Float32Array {
	if (/apple|orchard/i.test(text)) return new Float32Array([1, -1, 1, -1, 1, -1, 1, -1]);
	if (/house|shelter/i.test(text)) return new Float32Array([-1, 1, -1, 1, -1, 1, -1, 1]);
	return new Float32Array([1, 1, -1, -1, 1, 1, -1, -1]);
}

function embedding(version = "1"): GameMemoryEmbeddingProvider {
	return {
		identity: { model: "test-embedding", version, dimensions: 8, preprocessing: "test-v1" },
		async embedQuery(text) {
			return vectorFor(text);
		},
		async embedDocuments(texts) {
			return texts.map(vectorFor);
		},
	};
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-memory-"));
	directories.push(directory);
	return join(directory, "memory.sqlite");
}

describe("SqliteGameMemoryStore", () => {
	it("performs bounded hybrid recall and preserves authoritative scope isolation", async () => {
		const path = await databasePath();
		using store = new SqliteGameMemoryStore(path, { embedding: embedding(), maximumCandidates: 64 });
		await store.putMany([
			memory("actor-secret", session(), { searchText: "red apple orchard", tags: ["food"] }),
			memory("owner-shared", session(), { scope: "owner", searchText: "shared apple basket" }),
			memory("world-shared", session(), { scope: "world", searchText: "public apple market" }),
			memory("other-owner-secret", session("owner-b", "actor-b"), { searchText: "private apple note" }),
			memory("old-generation", session("owner-a", "actor-a", 0), { scope: "world", searchText: "ancient apple" }),
			memory("shelter", session(), { searchText: "stone house shelter" }),
		]);

		const sameActor = await store.search({ session: session(), text: "orchard fruit", tags: ["food"], limit: 8 });
		expect(sameActor.matches.map((item) => item.memory.id)).toEqual(["actor-secret"]);
		expect(sameActor.diagnostics.authoritativeCandidates).toBeLessThanOrEqual(64);
		expect(sameActor.diagnostics.vectorCandidates).toBeGreaterThan(0);

		const siblingActor = await store.search({ session: session("owner-a", "actor-c"), text: "apple", limit: 8 });
		expect(siblingActor.matches.map((item) => item.memory.id)).toEqual(
			expect.arrayContaining(["owner-shared", "world-shared"]),
		);
		expect(siblingActor.matches.map((item) => item.memory.id)).not.toContain("actor-secret");
		expect(siblingActor.matches.map((item) => item.memory.id)).not.toContain("other-owner-secret");

		const otherOwner = await store.search({ session: session("owner-b", "actor-b"), text: "apple", limit: 8 });
		expect(otherOwner.matches.map((item) => item.memory.id)).toEqual(
			expect.arrayContaining(["other-owner-secret", "world-shared"]),
		);
		expect(otherOwner.matches.map((item) => item.memory.id)).not.toContain("owner-shared");
		expect(otherOwner.matches.map((item) => item.memory.id)).not.toContain("old-generation");
	});

	it("keeps limited lookup bounded with ten thousand memories and survives restart", async () => {
		const path = await databasePath();
		const gameSession = session();
		using first = new SqliteGameMemoryStore(path, { maximumCandidates: 128 });
		const memories = Array.from({ length: 10_000 }, (_, index) =>
			memory(`record-${index}`, gameSession, {
				searchText: index === 9_999 ? "unique needle landmark" : `ordinary history record ${index}`,
				moment: { tick: index },
			}),
		);
		await first.putMany(memories);
		first.close();

		using second = new SqliteGameMemoryStore(path, { maximumCandidates: 128 });
		const result = await second.search({ session: gameSession, text: "unique needle", limit: 4 });
		expect(result.matches[0]?.memory.id).toBe("record-9999");
		expect(result.diagnostics.lexicalCandidates).toBeLessThanOrEqual(64);
		expect(result.diagnostics.authoritativeCandidates).toBeLessThanOrEqual(128);
	});

	it("applies metadata and game-time filters before every bounded candidate limit", async () => {
		const path = await databasePath();
		using store = new SqliteGameMemoryStore(path, { embedding: embedding(), maximumCandidates: 32 });
		const gameSession = session();
		const target = memory("filtered-target", gameSession, {
			scope: "owner",
			kind: "promise",
			tags: ["quest", "private"],
			searchText: "apple",
			importance: 0.9,
			moment: { tick: 40 },
		});
		const decoys = Array.from({ length: 256 }, (_, index) =>
			memory(`decoy-${index}`, gameSession, {
				scope: "owner",
				kind: "observation",
				tags: ["noise"],
				searchText: "apple",
				importance: 1,
				moment: { tick: 100 + index },
			}),
		);
		await store.putMany([target, ...decoys]);

		const filtered = await store.search({
			session: gameSession,
			text: "orchard",
			scopes: ["owner"],
			kinds: ["promise"],
			tags: ["quest", "private"],
			atOrBeforeTick: 50,
			minimumImportance: 0.8,
			limit: 1,
		});

		expect(filtered.matches.map((match) => match.memory.id)).toEqual(["filtered-target"]);
		expect(filtered.diagnostics.vectorCandidates).toBe(1);
		expect(filtered.diagnostics.authoritativeCandidates).toBe(1);

		const metadataOnly = await store.search({
			session: gameSession,
			scopes: ["owner"],
			kinds: ["promise"],
			tags: ["quest", "private"],
			atOrBeforeTick: 50,
			minimumImportance: 0.8,
			limit: 1,
		});
		expect(metadataOnly.matches.map((match) => match.memory.id)).toEqual(["filtered-target"]);
		expect(metadataOnly.diagnostics.authoritativeCandidates).toBe(1);
	});

	it("validates bounded query filters before preparing SQL", async () => {
		const path = await databasePath();
		using store = new SqliteGameMemoryStore(path);
		await expect(
			store.search({ session: session(), kinds: Array.from({ length: 65 }, (_, index) => `kind-${index}`), limit: 1 }),
		).rejects.toThrow(/kinds/);
		await expect(store.search({ session: session(), tags: ["invalid tag"], limit: 1 })).rejects.toThrow(/tags/);
	});

	it("uses an explicit embedding identity and rebuilds derived vectors after model changes", async () => {
		const path = await databasePath();
		using first = new SqliteGameMemoryStore(path, { embedding: embedding("1") });
		await first.put(memory("apple", session(), { searchText: "red apple orchard" }));
		first.close();

		using second = new SqliteGameMemoryStore(path, { embedding: embedding("2") });
		const before = await second.search({ session: session(), text: "orchard", limit: 4 });
		expect(before.diagnostics.vectorCandidates).toBe(0);
		expect(await second.rebuildEmbeddings(session())).toBe(1);
		const after = await second.search({ session: session(), text: "orchard", limit: 4 });
		expect(after.diagnostics.vectorCandidates).toBe(1);
		expect(after.diagnostics.embeddingIdentity).toContain('"2"');
	});

	it("fails closed on corrupt authoritative memory without recording text in diagnostics", async () => {
		const path = await databasePath();
		using store = new SqliteGameMemoryStore(path);
		await store.put(memory("valid", session(), { searchText: "corruption target" }));
		const database = (store as unknown as { database: { exec(sql: string): void } }).database;
		database.exec("UPDATE game_memories SET memory_json='not-json'");
		await expect(store.search({ session: session(), text: "corruption", limit: 4 })).rejects.toThrow(/corrupt/);
	});
});
