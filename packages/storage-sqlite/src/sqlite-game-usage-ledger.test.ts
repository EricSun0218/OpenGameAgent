import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameSessionKey } from "@opengameagent/protocol";
import type { GameUsageEntry } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteGameUsageLedger } from "./sqlite-game-usage-ledger.js";

const paths: string[] = [];

const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 4,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};

function entry(id: string, cost: number | undefined, cause: GameUsageEntry["cause"] = "assistant"): GameUsageEntry {
	return {
		id,
		session,
		inputId: "input",
		runId: "run",
		turn: id === "one" ? 1 : 2,
		cause,
		provider: "provider",
		model: "model",
		usage: {
			input: 10,
			output: 5,
			cacheRead: 2,
			cacheWrite: 1,
			reasoning: 3,
			totalTokens: 21,
			...(cost === undefined ? {} : { cost: { input: cost, output: 0, cacheRead: 0, cacheWrite: 0, total: cost } }),
		},
		timestamp: 10,
	};
}

afterEach(async () => {
	for (const path of paths.splice(0)) await rm(path, { recursive: true, force: true });
});

describe("SqliteGameUsageLedger", () => {
	it("deduplicates retries and preserves usage across restart", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-usage-"));
		paths.push(directory);
		const path = join(directory, "usage.sqlite");
		using first = new SqliteGameUsageLedger(path);
		await first.append(entry("one", 0.25));
		await first.append(entry("one", 0.25));
		first.close();

		using second = new SqliteGameUsageLedger(path);
		await second.append(entry("two", undefined, "compaction"));
		const summary = await second.summarize(session);
		expect(summary.total).toEqual({
			records: 2,
			input: 20,
			output: 10,
			cacheRead: 4,
			cacheWrite: 2,
			reasoning: 6,
			totalTokens: 42,
			unknownCostRecords: 1,
			cost: null,
		});
		expect(summary.byCause.assistant?.cost).toBe(0.25);
		expect(summary.byCause.compaction?.unknownCostRecords).toBe(1);
	});

	it("isolates owner, actor, timeline, and generation and rejects conflicting record replay", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-usage-"));
		paths.push(directory);
		using ledger = new SqliteGameUsageLedger(join(directory, "usage.sqlite"));
		await ledger.append(entry("same", 0.1));
		await expect(ledger.append({ ...entry("same", 0.1), runId: "different" })).rejects.toThrow(/different content/);
		expect((await ledger.summarize({ ...session, actorId: "other" })).total.records).toBe(0);
		expect((await ledger.summarize({ ...session, generation: 5 })).total.records).toBe(0);
	});
});
