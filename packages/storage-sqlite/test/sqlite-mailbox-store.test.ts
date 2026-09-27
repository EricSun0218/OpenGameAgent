import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	MailboxCapacityError,
	MailboxLeaseConflictError,
	type MailboxLimits,
	type MailboxMessageDraft,
	MailboxOrderConflictError,
	type MailboxRecipient,
	MailboxRunLeaseRegistry,
	MailboxService,
	MailboxValidationError,
	mailboxDeliveryToRunLeaseReceipt,
} from "@opengameagent/extensions";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteMailboxStore } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const limits: MailboxLimits = {
	maxIdentifierBytes: 128,
	maxPayloadBytes: 4096,
	maxTickDigits: 30,
	maxCounterDigits: 20,
	maxPendingMessagesPerTimelineGeneration: 3,
	maxPendingMessagesPerRecipient: 2,
	maxActiveDeliveries: 2,
	maxPageSize: 2,
	maxClaimBatchSize: 2,
	maxPurgeBatchSize: 2,
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

describe("SqliteMailboxStore", () => {
	it.each(["beginAuthorityRetirement", "finalizeAuthorityRetirement"] as const)(
		"rejects %s above a reduced recovery budget without changing the fence or lease evidence",
		async (method) => {
			const authority = open(":memory:");
			const writer = new SqliteMailboxStore(authority, limits);
			await writer.enqueue(
				message("one"),
				limits.maxPendingMessagesPerTimelineGeneration,
				limits.maxPendingMessagesPerRecipient,
			);
			await writer.enqueue(
				message("two"),
				limits.maxPendingMessagesPerTimelineGeneration,
				limits.maxPendingMessagesPerRecipient,
			);
			const request = { retirementId: "bounded", partition: recipient, recordedAt: time(2) };
			if (method === "finalizeAuthorityRetirement") {
				await writer.claim(claim(1, 5, 2));
				await writer.beginAuthorityRetirement(request);
			}
			const before = authority.read((database) =>
				database.prepare("SELECT * FROM oga_mailbox_messages ORDER BY message_key").all(),
			);
			const fence = await writer.readAuthorityRetirement(recipient);
			const reader = new SqliteMailboxStore(authority, { ...limits, maxPendingMessagesPerRecipient: 1 });
			await expect(reader[method](request)).rejects.toBeInstanceOf(MailboxCapacityError);
			expect(await writer.readAuthorityRetirement(recipient)).toEqual(fence);
			expect(
				authority.read((database) => database.prepare("SELECT * FROM oga_mailbox_messages ORDER BY message_key").all()),
			).toEqual(before);
		},
	);

	it("captures a lease key before entering the SQLite snapshot", async () => {
		const authority = open(await databasePath());
		const store = new SqliteMailboxStore(authority, limits, () => "lease");
		await store.enqueue(
			message("one"),
			limits.maxPendingMessagesPerTimelineGeneration,
			limits.maxPendingMessagesPerRecipient,
		);
		const [delivery] = await store.claim(claim(1, 5, 1));
		if (!delivery) throw new Error("Expected a lease.");
		let reads = 0;
		const key = {
			...recipient,
			get messageKey() {
				reads += 1;
				return delivery.message.messageKey;
			},
		};
		await expect(store.assertLease(key, delivery.leaseToken, time(2))).resolves.toMatchObject({ leaseToken: "lease" });
		expect(reads).toBe(1);
	});

	it("keeps a token-callback retirement committed while rejecting the obsolete claim", async () => {
		const authority = open(await databasePath());
		let closing: ReturnType<MailboxService["beginAuthorityRetirement"]> | undefined;
		const request = { retirementId: "token-reentry", partition: recipient, recordedAt: time(2) };
		const current = service(authority, () => {
			closing = current.beginAuthorityRetirement(request);
			return "stale-lease";
		});
		await current.enqueue(message("one"));
		await expect(current.claim(claim(1, 5, 1))).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		await expect(closing).resolves.toMatchObject({ phase: "fenced", cancelledMessageCount: 1 });
		expect(await current.readAuthorityRetirement(recipient)).toMatchObject({ phase: "fenced" });
		expect(await current.get(messageKey("one"))).toMatchObject({ status: "cancelled", attempt: "0" });
	});

	it("rolls back a composite retirement and keeps conflicts and counter failures atomic", async () => {
		const authority = open(await databasePath());
		const store = new SqliteMailboxStore(authority, { ...limits, maxCounterDigits: 1 });
		const current = new MailboxService(store, { ...limits, maxCounterDigits: 1 });
		const one = await current.enqueue(message("one"));
		const request = { retirementId: "atomic-retirement", partition: recipient, recordedAt: time(2) };
		expect(() =>
			authority.write((database) => {
				store.beginAuthorityRetirementInTransaction(database, request);
				throw new Error("other authority failed");
			}),
		).toThrow("other authority failed");
		expect(await current.readAuthorityRetirement(recipient)).toBeUndefined();
		expect(await current.get(messageKey("one"))).toEqual(one);
		const two = await current.enqueue(message("two"));
		authority.write((database) =>
			database.prepare("UPDATE oga_mailbox_messages SET revision = '9' WHERE message_key = ?").run(two.messageKey),
		);
		await expect(current.beginAuthorityRetirement(request)).rejects.toBeInstanceOf(MailboxValidationError);
		expect(await current.readAuthorityRetirement(recipient)).toBeUndefined();
		expect(await current.get(messageKey("one"))).toEqual(one);
		authority.write((database) =>
			database.prepare("UPDATE oga_mailbox_messages SET revision = '1' WHERE message_key = ?").run(two.messageKey),
		);
		await current.beginAuthorityRetirement(request);
		expect(() =>
			authority.write((database) => {
				store.finalizeAuthorityRetirementInTransaction(database, request);
				throw new Error("other finalization failed");
			}),
		).toThrow("other finalization failed");
		expect(await current.readAuthorityRetirement(recipient)).toMatchObject({
			phase: "fenced",
			cancelledMessageCount: 2,
		});
		const peer = { ...recipient, sessionId: "peer-session" };
		const peerMessage = await current.enqueue(message("peer", "10", peer));
		for (const changed of [
			{ ...request, retirementId: "different" },
			{ ...request, recordedAt: time(3) },
			{ ...request, partition: peer },
		]) {
			await expect(current.beginAuthorityRetirement(changed)).rejects.toMatchObject({
				name: "WorkAuthorityRetirementConflictError",
			});
		}
		expect(await current.readAuthorityRetirement(peer)).toBeUndefined();
		expect(await current.get({ ...peer, messageId: "peer" })).toEqual(peerMessage);
	});

	it("persists a two-phase exact-lane fence and lease evidence across crashes and purge", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority, tokens("retired-lease"));
		await first.enqueue(message("leased"));
		await first.enqueue(message("pending"));
		const peer = { ...recipient, actorId: "peer" };
		await first.enqueue(message("peer", "10", peer));
		const [delivery] = await first.claim(claim(1, 5, 1));
		if (!delivery) throw new Error("Expected a lease.");
		const request = { retirementId: "retire-mailbox", partition: recipient, recordedAt: time(2) };
		expect(await first.beginAuthorityRetirement(request)).toEqual({
			...request,
			phase: "fenced",
			cancelledMessageCount: 1,
		});
		const evidence = firstAuthority.read((database) =>
			database
				.prepare(
					"SELECT lease_token, lease_owner_id, lease_expires_at FROM oga_mailbox_messages WHERE message_id = 'leased'",
				)
				.get(),
		);
		expect(evidence).toEqual({
			lease_token: delivery.leaseToken,
			lease_owner_id: delivery.leaseOwnerId,
			lease_expires_at: delivery.leaseExpiresAt,
		});
		firstAuthority.close();
		const nextAuthority = open(path);
		const next = service(nextAuthority);
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const registry = new MailboxRunLeaseRegistry(next, () => time(2));
		await expect(registry.restore(receipt)).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		await expect(next.claim(claim(5, 6, 1))).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		await expect(registry.settle(receipt, "aborted", time(2))).rejects.toMatchObject({
			name: "WorkAuthorityRetiredError",
		});
		await next.purgeTerminal(recipient, time(3), 2);
		const finalized = await next.finalizeAuthorityRetirement(request);
		expect(finalized).toEqual({ ...request, phase: "finalized", cancelledMessageCount: 2 });
		expect(
			nextAuthority.read((database) =>
				database
					.prepare(
						"SELECT lease_token, lease_owner_id, lease_expires_at FROM oga_mailbox_messages WHERE message_id = 'leased'",
					)
					.get(),
			),
		).toEqual({ lease_token: null, lease_owner_id: null, lease_expires_at: null });
		await next.purgeTerminal(recipient, time(3), 2);
		nextAuthority.close();
		const last = service(open(path));
		expect(await last.beginAuthorityRetirement(request)).toEqual(finalized);
		expect(await last.finalizeAuthorityRetirement(request)).toEqual(finalized);
		await expect(last.enqueue(message("leased"))).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		expect(await last.claim({ ...claim(3, 5, 1), ...peer })).toHaveLength(1);
	});

	it("restores an exact run lease receipt through a new authority after process restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority, tokens("restart-lease"));
		await first.enqueue(message("restart"));
		const [delivery] = await first.claim(claim(1, 4, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		firstAuthority.close();

		const restarted = service(open(path));
		const registry = new MailboxRunLeaseRegistry(restarted, () => time(2));
		await expect(registry.renew(receipt, time(2), time(5))).resolves.toMatchObject({ leaseExpiresAt: time(5) });
		const input = await registry.restore(JSON.parse(JSON.stringify(receipt)));
		expect(input).toMatchObject({
			sessionId: recipient.sessionId,
			actorId: recipient.actorId,
			inputId: receipt.inputId,
			payload: { deliveryAttempt: "1", message: { messageId: "restart" } },
		});
		await expect(registry.settle(receipt, "aborted", time(2))).resolves.toMatchObject({ status: "pending" });
		await expect(
			new MailboxRunLeaseRegistry(restarted, () => time(2)).settle(receipt, "aborted", time(2)),
		).resolves.toMatchObject({ status: "pending", attempt: "1" });
		await expect(
			new MailboxRunLeaseRegistry(restarted, () => time(2)).settle(
				{ ...receipt, leaseOwnerId: "forged-owner" },
				"aborted",
				time(2),
			),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		await expect(
			new MailboxRunLeaseRegistry(restarted, () => time(2)).settle(
				{ ...receipt, leaseToken: "wrong-released-token" },
				"aborted",
				time(2),
			),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		const [reclaimed] = await restarted.claim(claim(2, 5, 1));
		expect(reclaimed?.message).toMatchObject({ messageId: "restart", attempt: "2" });
	});

	it("restores exact recipient FIFO state and status after restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority);
		const one = await first.enqueue(message("one", "90071992547409931234567890"));
		await first.enqueue(message("two", "90071992547409931234567891"));
		firstAuthority.close();

		const restarted = service(open(path), tokens("lease-1", "lease-2"));
		expect((await restarted.list({ ...recipient, limit: 2 })).records.map((record) => record.messageId)).toEqual([
			"one",
			"two",
		]);
		const claimed = await restarted.claim(claim(1, 4, 2));
		await expect(
			restarted.assertLease(sequenceKey(claimed[1]?.message.messageKey), claimed[1]?.leaseToken ?? "missing", time(2)),
		).rejects.toBeInstanceOf(MailboxOrderConflictError);
		await expect(
			restarted.complete(
				sequenceKey(claimed[1]?.message.messageKey),
				claimed[1]?.leaseToken ?? "missing",
				claimed[1]?.leaseOwnerId ?? "missing",
				time(2),
			),
		).rejects.toBeInstanceOf(MailboxOrderConflictError);
		expect(
			await restarted.complete(
				sequenceKey(claimed[0]?.message.messageKey),
				claimed[0]?.leaseToken ?? "missing",
				claimed[0]?.leaseOwnerId ?? "missing",
				time(2),
			),
		).toMatchObject({ messageKey: one.messageKey, status: "completed" });
		await expect(
			restarted.assertLease(sequenceKey(claimed[1]?.message.messageKey), claimed[1]?.leaseToken ?? "missing", time(2)),
		).resolves.toMatchObject({ message: { messageId: "two" } });
		expect(await restarted.status(recipient)).toMatchObject({ pending: 1n, completed: 1n, cancelled: 0n });
	});

	it("restores read-only generation discovery with stable global pagination", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority);
		const sessionPeer = { ...recipient, actorId: "npc-2" };
		const actorPeer = { ...recipient, sessionId: "other-session" };
		const otherGeneration = { ...recipient, generation: "1" };
		const one = await first.enqueue(message("one", "10", recipient));
		const two = await first.enqueue(message("two", "11", sessionPeer));
		const three = await first.enqueue(message("three", "12", actorPeer));
		await first.enqueue(message("other-generation", "13", otherGeneration));
		await first.cancel({ ...sessionPeer, messageId: two.messageId }, two.revision, time(1));
		firstAuthority.close();

		const restarted = service(open(path));
		const before = await Promise.all([
			restarted.get({ ...recipient, messageId: one.messageId }),
			restarted.get({ ...sessionPeer, messageId: two.messageId }),
			restarted.get({ ...actorPeer, messageId: three.messageId }),
		]);
		const scope = {
			ownerId: recipient.ownerId,
			timelineId: recipient.timelineId,
			generation: recipient.generation,
		};
		const page = await restarted.list({ ...scope, limit: 2 });
		expect(page.records.map((record) => record.messageId)).toEqual(["one", "two"]);
		expect(page.nextAfterMessageKey).toBe(two.messageKey);
		if (page.nextAfterMessageKey === undefined) throw new Error("Expected another mailbox page.");
		expect(
			(
				await restarted.list({
					...scope,
					afterMessageKey: page.nextAfterMessageKey,
					limit: 2,
				})
			).records.map((record) => record.messageId),
		).toEqual(["three"]);
		expect((await restarted.list({ ...scope, statuses: ["pending"], limit: 2 })).records).toMatchObject([
			{ messageId: "one", recipient },
			{ messageId: "three", recipient: actorPeer },
		]);
		expect(
			(await restarted.list({ ...scope, sessionId: recipient.sessionId, limit: 2 })).records.map(
				(record) => record.messageId,
			),
		).toEqual(["one", "two"]);
		expect(
			(await restarted.list({ ...scope, actorId: recipient.actorId, limit: 2 })).records.map(
				(record) => record.messageId,
			),
		).toEqual(["one", "three"]);
		expect(
			(
				await restarted.list({
					...scope,
					sessionId: recipient.sessionId,
					actorId: recipient.actorId,
					limit: 2,
				})
			).records.map((record) => record.messageId),
		).toEqual(["one"]);
		expect(
			await Promise.all([
				restarted.get({ ...recipient, messageId: one.messageId }),
				restarted.get({ ...sessionPeer, messageId: two.messageId }),
				restarted.get({ ...actorPeer, messageId: three.messageId }),
			]),
		).toEqual(before);
	});

	it("renews across restart, rejects exact expiry, reclaims, and replays only the settled token", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority, tokens("lease-old"));
		await first.enqueue(message("one"));
		const [original] = await first.claim(claim(1, 3, 1));
		if (original === undefined) throw new Error("Expected an original lease.");
		firstAuthority.close();

		const secondAuthority = open(path);
		const restarted = service(secondAuthority, tokens("lease-new"));
		const renewed = await restarted.renew({
			...sequenceKey(original.message.messageKey),
			leaseToken: original.leaseToken,
			operationalNow: time(2),
			leaseExpiresAt: time(4),
		});
		expect(renewed.message.revision).toBe("3");
		expect(await restarted.assertLease(sequenceKey(original.message.messageKey), original.leaseToken, time(3))).toEqual(
			renewed,
		);
		await expect(
			restarted.abandon(sequenceKey(original.message.messageKey), original.leaseToken, original.leaseOwnerId, time(4)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		await expect(
			restarted.assertLease(sequenceKey(original.message.messageKey), original.leaseToken, time(4)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		const [reclaimed] = await restarted.claim(claim(4, 6, 1));
		if (reclaimed === undefined) throw new Error("Expected a reclaimed message.");
		const completedReceipt = mailboxDeliveryToRunLeaseReceipt(reclaimed, limits);
		await expect(
			restarted.complete(
				sequenceKey(reclaimed.message.messageKey),
				original.leaseToken,
				original.leaseOwnerId,
				time(5),
			),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		const completed = await restarted.complete(
			sequenceKey(reclaimed.message.messageKey),
			reclaimed.leaseToken,
			reclaimed.leaseOwnerId,
			time(5),
		);
		secondAuthority.close();

		const third = service(open(path));
		expect(
			await third.complete(sequenceKey(completed.messageKey), reclaimed.leaseToken, reclaimed.leaseOwnerId, time(1)),
		).toEqual(completed);
		await expect(
			new MailboxRunLeaseRegistry(third, () => time(5)).settle(completedReceipt, "completed", time(5)),
		).resolves.toEqual(completed);
		await expect(
			new MailboxRunLeaseRegistry(third, () => time(5)).settle(
				{ ...completedReceipt, leaseOwnerId: "forged-owner" },
				"completed",
				time(5),
			),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		await expect(
			third.complete(sequenceKey(completed.messageKey), "other-token", reclaimed.leaseOwnerId, time(5)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		expect(await third.purgeTerminal(recipient, time(6), 1)).toEqual([completed.messageKey]);
	});

	it("serializes competing authorities and keeps an active head from being skipped", async () => {
		const path = await databasePath();
		const first = service(open(path), tokens("first-1", "first-2"));
		const second = service(open(path), tokens("second-1", "second-2"));
		await first.enqueue(message("one"));
		await first.enqueue(message("two"));
		const outcomes = await Promise.all([first.claim(claim(1, 4, 1)), second.claim(claim(1, 4, 1))]);
		expect(outcomes.flat()).toHaveLength(1);
		expect(await second.claim(claim(2, 5, 2))).toEqual([]);
	});

	it("enforces generation and recipient capacities transactionally", async () => {
		const current = service(open(await databasePath()));
		await current.enqueue(message("one"));
		await current.enqueue(message("two"));
		await expect(current.enqueue(message("three"))).rejects.toEqual(new MailboxCapacityError("recipient"));
		await current.enqueue(message("one", "10", { ...recipient, actorId: "npc-2" }));
		await expect(current.enqueue(message("one", "10", { ...recipient, actorId: "npc-3" }))).rejects.toEqual(
			new MailboxCapacityError("timeline_generation"),
		);
		expect((await current.list({ ...recipient, limit: 2 })).records).toHaveLength(2);
	});

	it("isolates sequence lookup and fails closed on persisted lease corruption", async () => {
		const authority = open(await databasePath());
		const current = service(authority, tokens("lease"));
		expect(await current.status(recipient)).toMatchObject({ pending: 0n, completed: 0n, cancelled: 0n });
		await expect(current.list({ ...recipient, statuses: [], limit: 1 })).rejects.toBeInstanceOf(MailboxValidationError);
		const created = await current.enqueue(message("one"));
		expect(await current.getByKey({ ...sequenceKey(created.messageKey), actorId: "npc-2" })).toBeUndefined();
		await current.claim(claim(1, 3, 1));
		authority.write((database) =>
			database
				.prepare("UPDATE oga_mailbox_messages SET lease_expires_at = updated_at WHERE message_key = ?")
				.run(created.messageKey),
		);
		await expect(current.get(messageKey("one"))).rejects.toBeInstanceOf(MailboxValidationError);
	});

	it("validates bounded terminal records before purging them", async () => {
		const authority = open(await databasePath());
		const current = service(authority);
		const created = await current.enqueue(message("terminal"));
		await current.cancel({ ...recipient, messageId: "terminal" }, created.revision, time(1));
		authority.write((database) =>
			database
				.prepare("UPDATE oga_mailbox_messages SET kind = 'corrupt.kind' WHERE message_key = ?")
				.run(created.messageKey),
		);
		await expect(current.purgeTerminal(recipient, time(2), 1)).rejects.toBeInstanceOf(MailboxValidationError);
	});
});

const recipient: MailboxRecipient = {
	ownerId: "world",
	timelineId: "main",
	generation: "0",
	sessionId: "npc-session",
	actorId: "npc-1",
};

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-mailbox-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
	authorities.push(authority);
	return authority;
}

function service(authority: SqliteAuthority, newLeaseToken?: () => string): MailboxService {
	return new MailboxService(new SqliteMailboxStore(authority, limits, newLeaseToken), limits);
}

function message(messageId: string, tick = "10", target: MailboxRecipient = recipient): MailboxMessageDraft {
	return {
		messageId,
		recipient: target,
		senderId: "game",
		kind: "world.event",
		payload: { messageId },
		gameTime: { timelineId: target.timelineId, generation: target.generation, tick },
		correlationId: `correlation-${messageId}`,
		recordedAt: time(0),
	};
}

function claim(now: number, expires: number, limit: number) {
	return {
		...recipient,
		leaseOwnerId: "runtime-1",
		operationalNow: time(now),
		leaseExpiresAt: time(expires),
		limit,
	};
}

function messageKey(messageId: string) {
	return { ...recipient, messageId };
}

function sequenceKey(messageKey: bigint | undefined) {
	if (messageKey === undefined) throw new Error("Expected a message key.");
	return { ...recipient, messageKey };
}

function tokens(...values: string[]): () => string {
	return () => values.shift() ?? "lease-extra";
}

function time(second: number): string {
	return `2026-08-28T00:00:${second.toString().padStart(2, "0")}.000Z`;
}
