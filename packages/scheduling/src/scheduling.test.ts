import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { type GameSchedule, gameSignalToInput, SqliteGameTimeScheduler } from "./scheduling.js";

const directories: string[] = [];

function session(actorId = "actor-a", generation = 1): GameSessionKey {
	return {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation,
		ownerId: "owner",
		sessionId: "session",
		actorId,
	};
}

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-scheduling-"));
	directories.push(directory);
	return join(directory, "scheduling.db");
}

function schedule(overrides: Partial<GameSchedule> = {}): GameSchedule {
	return {
		id: "monthly-life",
		session: session(),
		kind: "game.monthly-life",
		payload: { reason: "month" },
		due: { tick: 10, calendar: "month-1" },
		intervalTicks: 10,
		maximumOccurrences: 3,
		subjects: ["npc:1"],
		causes: ["world-clock"],
		...overrides,
	};
}

function oneShotSchedule(): GameSchedule {
	const recurring = schedule();
	const { intervalTicks: _intervalTicks, maximumOccurrences: _maximumOccurrences, ...oneShot } = recurring;
	return oneShot;
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("SQLite game-time scheduler", () => {
	it("materializes stable recurring occurrences and leases them durably", async () => {
		const path = await databasePath();
		using scheduler = new SqliteGameTimeScheduler(path);
		const created = await scheduler.schedule(schedule());
		expect(created).toMatchObject({ status: "active", revision: 1, occurrences: 0, nextDue: { tick: 10 } });
		expect(await scheduler.schedule(schedule())).toEqual(created);

		const advance = {
			id: "advance-1",
			session: session(),
			fromExclusive: { tick: 0 },
			toInclusive: { tick: 25, calendar: "month-2" },
		};
		const first = await scheduler.advance(advance, 8);
		expect(first.map((item) => [item.id, item.occurrence, item.moment])).toEqual([
			["monthly-life:occurrence:1", 1, { tick: 10, calendar: "month-1" }],
			["monthly-life:occurrence:2", 2, { tick: 20, calendar: "month-1" }],
		]);
		expect(await scheduler.advance(advance, 8)).toEqual(first);
		expect((await scheduler.list(session()))[0]).toMatchObject({ revision: 2, occurrences: 2, nextDue: { tick: 30 } });

		const [delivery] = await scheduler.claim(session(), 1, 1000, 100);
		expect(delivery).toMatchObject({ attempt: 1, occurrence: { id: "monthly-life:occurrence:1" } });
		expect(await scheduler.claim(session(), 8, 1050, 100)).toHaveLength(1);
		await scheduler.abandon(session(), delivery?.occurrence.id ?? "", delivery?.leaseToken ?? "");
		const [retried] = await scheduler.claim(session(), 1, 1050, 100);
		expect(retried).toMatchObject({ attempt: 2, occurrence: { id: "monthly-life:occurrence:1" } });
		await scheduler.complete(session(), retried?.occurrence.id ?? "", retried?.leaseToken ?? "");
		expect((await scheduler.readPending(session())).map((item) => item.id)).toEqual(["monthly-life:occurrence:2"]);
	});

	it("rolls back an overflowing time advance without consuming schedules", async () => {
		using scheduler = new SqliteGameTimeScheduler(await databasePath());
		await scheduler.schedule(schedule());
		const advance = {
			id: "advance-overflow",
			session: session(),
			fromExclusive: { tick: 0 },
			toInclusive: { tick: 35 },
		};
		await expect(scheduler.advance(advance, 2)).rejects.toThrow("maximum");
		expect((await scheduler.list(session()))[0]).toMatchObject({ revision: 1, occurrences: 0, nextDue: { tick: 10 } });
		expect(await scheduler.readPending(session())).toEqual([]);
		const generated = await scheduler.advance(advance, 3);
		expect(generated).toHaveLength(3);
		expect((await scheduler.list(session()))[0]).toMatchObject({ status: "completed", revision: 2, occurrences: 3 });
	});

	it("recovers pending delivery after restart and keeps sessions and generations isolated", async () => {
		const path = await databasePath();
		let leaseToken = "";
		{
			using scheduler = new SqliteGameTimeScheduler(path);
			await scheduler.schedule(schedule());
			await scheduler.schedule(schedule({ id: "other-actor", session: session("actor-b") }));
			await scheduler.schedule(schedule({ id: "new-generation", session: session("actor-a", 2) }));
			await scheduler.advance(
				{ id: "advance", session: session(), fromExclusive: { tick: 0 }, toInclusive: { tick: 10 } },
				1,
			);
			const [delivery] = await scheduler.claim(session(), 1, 100, 10);
			leaseToken = delivery?.leaseToken ?? "";
			expect(await scheduler.readPending(session("actor-b"))).toEqual([]);
			expect(await scheduler.readPending(session("actor-a", 2))).toEqual([]);
		}
		using reopened = new SqliteGameTimeScheduler(path);
		expect(await reopened.claim(session(), 1, 105, 10)).toEqual([]);
		const [recovered] = await reopened.claim(session(), 1, 111, 10);
		expect(recovered).toMatchObject({ attempt: 2, occurrence: { id: "monthly-life:occurrence:1" } });
		expect(recovered?.leaseToken).not.toBe(leaseToken);
	});

	it("uses CAS cancellation and rejects conflicting identities and corrupt state", async () => {
		const path = await databasePath();
		using scheduler = new SqliteGameTimeScheduler(path);
		const created = await scheduler.schedule(oneShotSchedule());
		await expect(scheduler.schedule(schedule({ kind: "different" }))).rejects.toThrow("different content");
		await expect(scheduler.cancel(session(), created.schedule.id, 2)).rejects.toThrow("revision conflict");
		const cancelled = await scheduler.cancel(session(), created.schedule.id, 1);
		expect(cancelled).toMatchObject({ status: "cancelled", revision: 2 });
		await expect(scheduler.cancel(session(), created.schedule.id, 2)).rejects.toThrow("not active");
	});

	it("projects a typed game signal into an ordinary runtime input", () => {
		const input = gameSignalToInput({
			id: "signal-1",
			session: session(),
			kind: "npc.arrived",
			payload: { location: "gate" },
			moment: { tick: 42, phase: "day" },
			subjects: ["npc:1"],
			causes: ["path:7"],
		});
		expect(input).toMatchObject({
			id: "signal-1",
			type: "npc.arrived",
			moment: { tick: 42, phase: "day" },
			content: [{ type: "json", value: { location: "gate" } }],
			context: { signal: { subjects: ["npc:1"], causes: ["path:7"] } },
		});
	});
});
