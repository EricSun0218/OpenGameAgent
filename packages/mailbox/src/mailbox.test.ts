import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import type { GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import {
	type GameMailbox,
	type GameMailboxMessage,
	type GameMailboxRecipientKey,
	InMemoryGameMailbox,
	SqliteGameMailbox,
} from "./mailbox.js";

const directories: string[] = [];
const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 2,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};
const recipient: GameMailboxRecipientKey = { session, recipientId: "npc-1" };

function message(id: string, overrides: Partial<GameMailboxMessage> = {}): GameMailboxMessage {
	return {
		id,
		session,
		recipientId: "npc-1",
		kind: "npc.arrived",
		payload: { location: "camp" },
		moment: { tick: 100.5 },
		...overrides,
	};
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

async function sqlite(): Promise<{ mailbox: SqliteGameMailbox; path: string }> {
	const directory = await mkdtemp(join(tmpdir(), "oga-mailbox-"));
	directories.push(directory);
	const path = join(directory, "mailbox.sqlite");
	return { mailbox: new SqliteGameMailbox(path), path };
}

async function assertLifecycle(mailbox: GameMailbox): Promise<void> {
	expect(await mailbox.enqueue(message("m1"))).toBe(true);
	expect(await mailbox.enqueue(message("m1"))).toBe(false);
	await expect(mailbox.enqueue(message("m1", { kind: "different" }))).rejects.toThrow("different content");

	expect(await mailbox.readPendingStatus([recipient], 1_000)).toEqual([
		{ recipient, readyCount: 1, leasedCount: 0, incompleteCount: 1 },
	]);
	const [first] = await mailbox.claim(recipient, 1, 1_000, 100);
	expect(first).toMatchObject({ attempt: 1, leaseExpiresAt: 1_100, message: { id: "m1" } });
	expect(await mailbox.readPendingStatus([recipient], 1_050)).toEqual([
		{ recipient, readyCount: 0, leasedCount: 1, incompleteCount: 1 },
	]);
	expect(await mailbox.readPendingStatus([recipient], 1_050)).toEqual([
		{ recipient, readyCount: 0, leasedCount: 1, incompleteCount: 1 },
	]);
	const [second] = await mailbox.claim(recipient, 1, 1_101, 100);
	expect(second?.attempt).toBe(2);
	await expect(mailbox.complete(recipient, "m1", first?.leaseToken as string)).rejects.toThrow("lease");
	await mailbox.abandon(recipient, "m1", second?.leaseToken as string);
	expect((await mailbox.readPendingStatus([recipient], 1_102))[0]).toMatchObject({ readyCount: 1, leasedCount: 0 });
	const [third] = await mailbox.claim(recipient, 1, 1_102, 100);
	expect(third?.attempt).toBe(3);
	await mailbox.complete(recipient, "m1", third?.leaseToken as string);
	expect((await mailbox.readPendingStatus([recipient], 1_103))[0]).toMatchObject({ incompleteCount: 0 });
}

describe("GameMailbox", () => {
	it("keeps in-memory claims, leases, attempts and status reads consistent", async () => {
		await assertLifecycle(new InMemoryGameMailbox());
	});

	it("keeps SQLite claims, leases, attempts and status reads consistent", async () => {
		using mailbox = (await sqlite()).mailbox;
		await assertLifecycle(mailbox);
	});

	it("queries many recipient keys in one indexed SQLite operation without exposing payloads", async () => {
		using mailbox = (await sqlite()).mailbox;
		const recipients = Array.from({ length: 512 }, (_, index) => ({
			session: { ...session, actorId: `actor-${index}` },
			recipientId: `npc-${index}`,
		}));
		for (let index = 0; index < recipients.length; index += 1) {
			const key = recipients[index] as GameMailboxRecipientKey;
			await mailbox.enqueue(
				message(`message-${index}`, { session: key.session, recipientId: key.recipientId, payload: { secret: index } }),
			);
		}
		const status = await mailbox.readPendingStatus(recipients, 1_000);
		expect(status).toHaveLength(512);
		expect(status.every((item) => item.readyCount === 1 && item.incompleteCount === 1)).toBe(true);
		expect(JSON.stringify(status)).not.toContain("secret");
		expect((await mailbox.readPendingStatus([{ ...recipient, recipientId: "missing" }], 1_000))[0]).toMatchObject({
			readyCount: 0,
			leasedCount: 0,
		});
	});

	it("round-trips across restart and fails closed on corrupt payload state", async () => {
		const created = await sqlite();
		await created.mailbox.enqueue(message("restart"));
		created.mailbox[Symbol.dispose]();
		using restarted = new SqliteGameMailbox(created.path);
		expect((await restarted.readPendingStatus([recipient], 1_000))[0]?.readyCount).toBe(1);
		restarted[Symbol.dispose]();
		using database = new DatabaseSync(created.path);
		database.prepare("UPDATE game_mailbox SET payload_json='{' WHERE message_id='restart'").run();
		database.close();
		using corrupt = new SqliteGameMailbox(created.path);
		await expect(corrupt.claim(recipient, 1, 1_000, 100)).rejects.toThrow("corrupt JSON");
	});

	it("isolates full session identity and rejects stale owner claims", async () => {
		using mailbox = (await sqlite()).mailbox;
		await mailbox.enqueue(message("isolated"));
		const other = { session: { ...session, ownerId: "other" }, recipientId: recipient.recipientId };
		expect(await mailbox.claim(other, 1, 1_000, 100)).toEqual([]);
		const [delivery] = await mailbox.claim(recipient, 1, 1_000, 100);
		await expect(
			mailbox.complete(other, delivery?.message.id as string, delivery?.leaseToken as string),
		).rejects.toThrow("recipient");
	});
});
