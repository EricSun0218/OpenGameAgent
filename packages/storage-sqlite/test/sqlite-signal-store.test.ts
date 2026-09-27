import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	SignalConflictError,
	SignalService,
	SignalValidationError,
	SignalWaitFanoutError,
	SignalWaitRevisionConflictError,
	type SignalLimits,
} from "@opengameagent/extensions";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteSignalStore } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const limits: SignalLimits = {
	maxPayloadBytes: 4_096,
	maxSubjectsPerSignal: 8,
	maxCausesPerSignal: 8,
	maxSignalFilterKinds: 4,
	maxWaitKinds: 4,
	maxWaitSubjects: 4,
	maxWaitMatchesPerSignal: 2,
	maxSignalPageSize: 4,
	maxWaitPageSize: 4,
	maxExpireBatchSize: 4,
	maxPurgeBatchSize: 4,
};

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteSignalStore", () => {
	it("shares status-filter rejection with the in-memory authority", async () => {
		const store = new SqliteSignalStore(open(await databasePath()), limits);
		await expect(store.listWaits({ ...partition(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(
			SignalValidationError,
		);
	});

	it("restores waits matched on either side of registration after restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority);
		await first.registerWait(wait("wait-before", { kinds: ["npc.arrived"] }));
		const later = await first.record(signal("signal-later", "npc.arrived", "90071992547409931234567890"));
		const earlier = await first.record(signal("signal-earlier", "gate.opened", "2"));
		await first.registerWait(
			wait("wait-after", { kinds: ["gate.opened"], afterSignalKey: earlier.signal.signalKey - 1n }),
		);
		firstAuthority.close();

		const reopened = service(open(path));
		expect(await reopened.getWait(waitKey("wait-before"))).toMatchObject({
			status: "matched",
			revision: "2",
			matchedSignalKey: later.signal.signalKey,
		});
		expect(await reopened.getWait(waitKey("wait-after"))).toMatchObject({
			status: "matched",
			revision: "1",
			matchedSignalKey: earlier.signal.signalKey,
			updatedAt: earlier.signal.recordedAt,
		});
		expect(await reopened.getSignalByKey({ ...generationKey(), signalKey: later.signal.signalKey })).toMatchObject({
			gameTime: gameTime("90071992547409931234567890"),
		});
	});

	it("atomically bounds both existing-wait and late-wait fanout", async () => {
		const current = service(open(await databasePath()));
		for (const waitId of ["wait-1", "wait-2", "wait-3"]) {
			await current.registerWait(wait(waitId, { kinds: ["world.changed"] }));
		}
		await expect(current.record(signal("signal-overflow", "world.changed", "2"))).rejects.toBeInstanceOf(
			SignalWaitFanoutError,
		);
		expect(await current.getSignal(signalKey("signal-overflow"))).toBeUndefined();

		const recorded = await current.record(signal("signal-late", "late.changed", "3"));
		for (const waitId of ["late-1", "late-2"]) {
			await current.registerWait(wait(waitId, { kinds: ["late.changed"] }));
		}
		await expect(current.registerWait(wait("late-3", { kinds: ["late.changed"] }))).rejects.toBeInstanceOf(
			SignalWaitFanoutError,
		);
		expect(await current.getWait(waitKey("late-3"))).toBeUndefined();
		expect((await current.record(signal("signal-late", "late.changed", "3"))).matchedWaitIds).toEqual([
			"late-1",
			"late-2",
		]);
		expect(recorded.signal.signalKey).toBeGreaterThan(0n);
	});

	it("settles competing cancellation revisions and conflicting signal identities", async () => {
		const path = await databasePath();
		const first = service(open(path));
		const second = service(open(path));
		const waiting = await first.registerWait(wait("wait-cancel", { kinds: ["never"] }));
		const outcomes = await Promise.allSettled([
			first.cancelWait(waitKey(waiting.waitId), waiting.revision, "2026-08-28T00:00:01.000Z"),
			second.cancelWait(waitKey(waiting.waitId), waiting.revision, "2026-08-28T00:00:02.000Z"),
		]);
		expect(outcomes.filter((outcome) => outcome.status === "fulfilled")).toHaveLength(1);
		expect(outcomes.filter((outcome) => outcome.status === "rejected")).toHaveLength(1);
		expect((outcomes.find((outcome) => outcome.status === "rejected") as PromiseRejectedResult).reason).toBeInstanceOf(
			SignalWaitRevisionConflictError,
		);

		await first.record(signal("same-id", "event.one", "1"));
		await expect(second.record(signal("same-id", "event.two", "1"))).rejects.toBeInstanceOf(SignalConflictError);
	});

	it("isolates actor and rewind generation selectors and scopes sequence lookup", async () => {
		const current = service(open(await databasePath()));
		await current.registerWait(wait("wait-actor", { kinds: ["npc.arrived"] }));
		await current.record({
			...signal("wrong-actor", "npc.arrived", "2"),
			partition: { ...partition(), sessionId: "session-2", actorId: "actor-2" },
		});
		await current.record({
			...signal("wrong-generation", "npc.arrived", "2"),
			partition: { ...partition(), generation: "2" },
			gameTime: { ...gameTime("2"), generation: "2" },
		});
		expect((await current.getWait(waitKey("wait-actor")))?.status).toBe("waiting");
		const matched = await current.record(signal("right-actor", "npc.arrived", "3"));
		expect((await current.getWait(waitKey("wait-actor")))?.status).toBe("matched");
		expect(
			await current.getSignalByKey({ ...generationKey(), ownerId: "owner-2", signalKey: matched.signal.signalKey }),
		).toBeUndefined();
		expect(
			await current.getSignalByKey({ ...generationKey(), generation: "2", signalKey: matched.signal.signalKey }),
		).toBeUndefined();
	});

	it("fails closed when persisted wait state points at a non-matching signal", async () => {
		const authority = open(await databasePath());
		const current = service(authority);
		await current.registerWait(wait("wait-1", { kinds: ["event.one"] }));
		await current.record(signal("signal-1", "event.one", "1"));
		const unrelated = await current.record({
			...signal("signal-2", "event.two", "2"),
			partition: { ...partition(), sessionId: "session-2", actorId: "actor-2" },
		});
		authority.write((database) =>
			database
				.prepare("UPDATE oga_signal_waits SET matched_signal_key = ? WHERE wait_id = ?")
				.run(unrelated.signal.signalKey, "wait-1"),
		);
		await expect(current.getWait(waitKey("wait-1"))).rejects.toBeInstanceOf(SignalValidationError);
	});
});

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-signal-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
	authorities.push(authority);
	return authority;
}

function service(authority: SqliteAuthority): SignalService {
	return new SignalService(new SqliteSignalStore(authority, limits), limits);
}

function partition() {
	return {
		ownerId: "owner-1",
		timelineId: "world",
		generation: "1",
		sessionId: "session-1",
		actorId: "actor-1",
	} as const;
}

function generationKey() {
	const { ownerId, timelineId, generation } = partition();
	return { ownerId, timelineId, generation };
}

function gameTime(tick: string) {
	return { timelineId: "world", generation: "1", tick } as const;
}

function signal(signalId: string, kind: string, tick: string) {
	return {
		signalId,
		partition: partition(),
		kind,
		payload: { tick },
		gameTime: gameTime(tick),
		subjectIds: ["gate"],
		causeIds: ["host-clock"],
		recordedAt: "2026-08-28T00:00:01.000Z",
	} as const;
}

function wait(waitId: string, selector: Parameters<SignalService["registerWait"]>[0]["selector"]) {
	return {
		waitId,
		partition: partition(),
		selector,
		target: { kind: "correlation", correlationId: waitId },
		recordedAt: "2026-08-28T00:00:00.000Z",
	} as const;
}

function signalKey(signalId: string) {
	return { ...generationKey(), signalId };
}

function waitKey(waitId: string) {
	return { ...generationKey(), waitId };
}
