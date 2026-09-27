import {
	InMemoryMailboxStore,
	MailboxCapacityError,
	MailboxConflictError,
	MailboxLeaseConflictError,
	MailboxOrderConflictError,
	MailboxRunLeaseRegistry,
	MailboxService,
	MailboxValidationError,
	mailboxDeliveryToGameInput,
	mailboxDeliveryToRunLeaseReceipt,
	type MailboxLimits,
	type MailboxMessageDraft,
	type MailboxMessageSequenceKey,
	type MailboxRecipient,
} from "@opengameagent/extensions";
import { describe, expect, it } from "vitest";

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

describe("MailboxService with the conformance store", () => {
	it("captures a message key once before a retirement fence can be reentered", async () => {
		const store = new InMemoryMailboxStore(limits, () => "lease");
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
				if (reads > 1)
					store.beginAuthorityRetirementSynchronously({
						retirementId: "key-reentry",
						partition: recipient,
						recordedAt: time(2),
					});
				return delivery.message.messageKey;
			},
		};
		await expect(store.assertLease(key, delivery.leaseToken, time(2))).resolves.toMatchObject({
			leaseToken: delivery.leaseToken,
		});
		expect(reads).toBe(1);
		expect(await store.readAuthorityRetirement(recipient)).toBeUndefined();
	});

	it("prevalidates every cancellation revision before committing an in-memory fence", async () => {
		const service = createService({ ...limits, maxCounterDigits: 1 });
		const advanced = await service.enqueue(message("advanced"));
		for (let i = 0; i < 4; i += 1) {
			const [delivery] = await service.claim(claim(i * 2 + 1, i * 2 + 3, 1));
			if (!delivery) throw new Error("Expected a lease.");
			await service.abandon(
				sequenceKey(advanced.messageKey),
				delivery.leaseToken,
				delivery.leaseOwnerId,
				time(i * 2 + 2),
			);
		}
		const pending = await service.enqueue(message("pending"));
		const request = { retirementId: "overflow", partition: recipient, recordedAt: time(9) };
		await expect(service.beginAuthorityRetirement(request)).rejects.toBeInstanceOf(MailboxValidationError);
		expect(await service.readAuthorityRetirement(recipient)).toBeUndefined();
		expect(await service.get(messageKey("pending"))).toEqual(pending);
		expect(await service.get(messageKey("advanced"))).toMatchObject({ status: "pending", revision: "9" });
	});

	it("commits a reentrant retirement before rejecting a lease-token callback's stale claim", async () => {
		let closing: ReturnType<MailboxService["beginAuthorityRetirement"]> | undefined;
		const request = { retirementId: "token-reentry", partition: recipient, recordedAt: time(2) };
		const service = createService(limits, () => {
			closing = service.beginAuthorityRetirement(request);
			return "stale-lease";
		});
		await service.enqueue(message("one"));
		await expect(service.claim(claim(1, 5, 1))).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		await expect(closing).resolves.toMatchObject({ phase: "fenced", cancelledMessageCount: 1 });
		expect(await service.get(messageKey("one"))).toMatchObject({ status: "cancelled", attempt: "0" });
	});

	it("allows only completed exact-proof replay after retirement and rejects settle after purge", async () => {
		const service = createService();
		await service.enqueue(message("done"));
		const [delivery] = await service.claim(claim(1, 5, 1));
		if (!delivery) throw new Error("Expected a lease.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const registry = new MailboxRunLeaseRegistry(service, () => time(2));
		const completed = await registry.settle(receipt, "completed", time(2));
		const request = { retirementId: "completed-retirement", partition: recipient, recordedAt: time(2) };
		await service.beginAuthorityRetirement(request);
		await service.finalizeAuthorityRetirement(request);
		expect(await registry.settle(receipt, "completed", time(2))).toEqual(completed);
		await expect(registry.settle({ ...receipt, leaseToken: "wrong" }, "completed", time(2))).rejects.toBeInstanceOf(
			MailboxLeaseConflictError,
		);
		await service.purgeTerminal(recipient, time(3), 1);
		await expect(registry.settle(receipt, "completed", time(3))).rejects.toMatchObject({
			name: "WorkAuthorityRetiredError",
		});
	});

	it("fences an exact lane before cancelling leased work and retains retirement after purge", async () => {
		const service = createService();
		const leased = await service.enqueue(message("leased"));
		await service.enqueue(message("pending"));
		const peer = { ...recipient, actorId: "peer" };
		await service.enqueue(message("peer", peer));
		const [delivery] = await service.claim(claim(1, 5, 1));
		if (!delivery) throw new Error("Expected a lease.");
		const registry = new MailboxRunLeaseRegistry(service, () => time(2));
		const input = await registry.activate(delivery);
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const request = { retirementId: "retire-mailbox", partition: recipient, recordedAt: time(2) };
		const begun = await service.beginAuthorityRetirement(request);
		expect(begun).toEqual({ ...request, phase: "fenced", cancelledMessageCount: 1 });
		expect(await service.get(messageKey("leased"))).toEqual(delivery.message);
		expect(await service.get(messageKey("pending"))).toMatchObject({ status: "cancelled" });
		for (const attempt of [
			() => service.enqueue(message("new")),
			() => service.claim(claim(2, 5, 1)),
			() => service.assertLease(sequenceKey(leased.messageKey), delivery.leaseToken, time(2)),
			() => registry.restore(receipt),
			() => registry.authorizeToolExecution(input, {}, new AbortController().signal),
			() => registry.renew(receipt, time(2), time(6)),
			() => registry.settle(receipt, "completed", time(2)),
			() => registry.settle(receipt, "aborted", time(2)),
		])
			await expect(attempt()).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		expect(await service.claim({ ...claim(2, 5, 1), ...peer })).toHaveLength(1);
		await service.purgeTerminal(recipient, time(3), 2);
		const finalized = await service.finalizeAuthorityRetirement(request);
		expect(finalized).toEqual({ ...request, phase: "finalized", cancelledMessageCount: 2 });
		expect(await service.finalizeAuthorityRetirement(request)).toEqual(finalized);
		expect(await service.beginAuthorityRetirement(request)).toEqual(finalized);
		await service.purgeTerminal(recipient, time(3), 2);
		expect(await service.readAuthorityRetirement(recipient)).toEqual(finalized);
		await expect(service.enqueue(message("leased"))).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
	});

	it("rejects conflicting retirement identities without cancelling another lane", async () => {
		const service = createService();
		const request = { retirementId: "retire-one", partition: recipient, recordedAt: time(1) };
		const peer = { ...recipient, sessionId: "peer-session" };
		await service.enqueue(message("peer", peer));
		await service.beginAuthorityRetirement(request);
		for (const changed of [
			{ ...request, recordedAt: time(2) },
			{ ...request, retirementId: "another-id" },
			{ ...request, partition: peer },
		])
			await expect(service.beginAuthorityRetirement(changed)).rejects.toMatchObject({
				name: "WorkAuthorityRetirementConflictError",
			});
		expect(await service.readAuthorityRetirement(peer)).toBeUndefined();
		expect(await service.get({ ...peer, messageId: "peer" })).toMatchObject({ status: "pending" });
		await expect(
			service.finalizeAuthorityRetirement({ ...request, partition: peer, retirementId: "not-begun" }),
		).rejects.toMatchObject({ name: "WorkAuthorityRetirementConflictError" });
	});

	it("enqueues idempotently, scopes identity to the recipient, and enforces both capacities", async () => {
		const service = createService();
		const first = await service.enqueue(message("one"));
		expect(await service.enqueue(message("one"))).toEqual(first);
		await expect(service.enqueue({ ...message("one"), payload: { changed: true } })).rejects.toBeInstanceOf(
			MailboxConflictError,
		);

		const otherRecipient = { ...recipient, actorId: "npc-2" };
		expect(await service.enqueue(message("one", otherRecipient))).toMatchObject({ messageId: "one" });
		await service.enqueue(message("two"));
		await expect(service.enqueue(message("three"))).rejects.toEqual(new MailboxCapacityError("timeline_generation"));

		const wider = createService({
			...limits,
			maxPendingMessagesPerTimelineGeneration: 4,
			maxPendingMessagesPerRecipient: 2,
		});
		await wider.enqueue(message("one"));
		await wider.enqueue(message("two"));
		await expect(wider.enqueue(message("three"))).rejects.toEqual(new MailboxCapacityError("recipient"));
	});

	it("discovers a generation read-only with optional session and actor filters", async () => {
		const service = createService({
			...limits,
			maxPendingMessagesPerTimelineGeneration: 8,
			maxPendingMessagesPerRecipient: 4,
		});
		const sessionPeer = { ...recipient, actorId: "npc-2" };
		const actorPeer = { ...recipient, sessionId: "other-session" };
		const otherGeneration = { ...recipient, generation: "1" };
		const first = await service.enqueue(message("one", recipient));
		const second = await service.enqueue(message("two", sessionPeer));
		const third = await service.enqueue(message("three", actorPeer));
		await service.enqueue(message("other-generation", otherGeneration));
		await service.cancel({ ...sessionPeer, messageId: second.messageId }, second.revision, time(1));

		const before = await Promise.all([
			service.get({ ...recipient, messageId: first.messageId }),
			service.get({ ...sessionPeer, messageId: second.messageId }),
			service.get({ ...actorPeer, messageId: third.messageId }),
		]);
		const scope = {
			ownerId: recipient.ownerId,
			timelineId: recipient.timelineId,
			generation: recipient.generation,
		};
		const page = await service.list({ ...scope, limit: 2 });
		expect(page.records.map((record) => record.messageId)).toEqual(["one", "two"]);
		expect(page.nextAfterMessageKey).toBe(second.messageKey);
		if (page.nextAfterMessageKey === undefined) throw new Error("Expected another mailbox page.");
		expect(
			(
				await service.list({
					...scope,
					afterMessageKey: page.nextAfterMessageKey,
					limit: 2,
				})
			).records.map((record) => record.messageId),
		).toEqual(["three"]);
		expect((await service.list({ ...scope, statuses: ["pending"], limit: 2 })).records).toMatchObject([
			{ messageId: "one", recipient },
			{ messageId: "three", recipient: actorPeer },
		]);
		expect(
			(await service.list({ ...scope, sessionId: recipient.sessionId, limit: 2 })).records.map(
				(record) => record.messageId,
			),
		).toEqual(["one", "two"]);
		expect(
			(await service.list({ ...scope, actorId: recipient.actorId, limit: 2 })).records.map(
				(record) => record.messageId,
			),
		).toEqual(["one", "three"]);
		expect(
			(
				await service.list({
					...scope,
					sessionId: recipient.sessionId,
					actorId: recipient.actorId,
					limit: 2,
				})
			).records.map((record) => record.messageId),
		).toEqual(["one"]);
		await expect(service.list({ ...scope, sessionId: "", limit: 1 })).rejects.toBeInstanceOf(MailboxValidationError);
		expect(
			await Promise.all([
				service.get({ ...recipient, messageId: first.messageId }),
				service.get({ ...sessionPeer, messageId: second.messageId }),
				service.get({ ...actorPeer, messageId: third.messageId }),
			]),
		).toEqual(before);
	});

	it("claims a strict FIFO prefix and refuses out-of-order completion", async () => {
		const service = createService();
		await service.enqueue(message("one"));
		await service.enqueue(message("two"));
		const claimed = await service.claim(claim(1, 4, 2));
		expect(claimed.map((delivery) => delivery.message.messageId)).toEqual(["one", "two"]);
		expect(await service.claim(claim(2, 5, 2))).toEqual([]);

		await expect(
			service.assertLease(sequenceKey(claimed[1]?.message.messageKey), claimed[1]?.leaseToken ?? "missing", time(2)),
		).rejects.toBeInstanceOf(MailboxOrderConflictError);
		await expect(
			service.complete(
				sequenceKey(claimed[1]?.message.messageKey),
				claimed[1]?.leaseToken ?? "missing",
				claimed[1]?.leaseOwnerId ?? "missing",
				time(2),
			),
		).rejects.toBeInstanceOf(MailboxOrderConflictError);
		const first = await service.complete(
			sequenceKey(claimed[0]?.message.messageKey),
			claimed[0]?.leaseToken ?? "missing",
			claimed[0]?.leaseOwnerId ?? "missing",
			time(2),
		);
		await expect(
			service.assertLease(sequenceKey(claimed[1]?.message.messageKey), claimed[1]?.leaseToken ?? "missing", time(2)),
		).resolves.toMatchObject({ message: { messageId: "two" } });
		const second = await service.complete(
			sequenceKey(claimed[1]?.message.messageKey),
			claimed[1]?.leaseToken ?? "missing",
			claimed[1]?.leaseOwnerId ?? "missing",
			time(3),
		);
		expect([first.status, second.status]).toEqual(["completed", "completed"]);
	});

	it("renews only a live lease, loses authority exactly at expiry, and fences stale tokens", async () => {
		const tokens = ["lease-old", "lease-new"];
		const service = createService(limits, () => tokens.shift() ?? "lease-extra");
		await service.enqueue(message("one"));
		const [original] = await service.claim(claim(1, 3, 1));
		if (original === undefined) throw new Error("Expected an original lease.");
		const renewed = await service.renew({
			...sequenceKey(original.message.messageKey),
			leaseToken: original.leaseToken,
			operationalNow: time(2),
			leaseExpiresAt: time(4),
		});
		expect(renewed.message).toMatchObject({ revision: "3", attempt: "1" });
		expect(await service.assertLease(sequenceKey(original.message.messageKey), original.leaseToken, time(3))).toEqual(
			renewed,
		);
		expect(
			await service.renew({
				...sequenceKey(original.message.messageKey),
				leaseToken: original.leaseToken,
				operationalNow: time(2),
				leaseExpiresAt: time(4),
			}),
		).toEqual(renewed);
		await expect(
			service.complete(sequenceKey(original.message.messageKey), original.leaseToken, original.leaseOwnerId, time(4)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		await expect(
			service.assertLease(sequenceKey(original.message.messageKey), original.leaseToken, time(4)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		const [reclaimed] = await service.claim(claim(4, 6, 1));
		if (reclaimed === undefined) throw new Error("Expected a reclaimed lease.");
		expect(reclaimed.message).toMatchObject({ attempt: "2" });
		await expect(
			service.complete(sequenceKey(original.message.messageKey), original.leaseToken, original.leaseOwnerId, time(5)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		const completed = await service.complete(
			sequenceKey(reclaimed.message.messageKey),
			reclaimed.leaseToken,
			reclaimed.leaseOwnerId,
			time(5),
		);
		expect(
			await service.complete(sequenceKey(completed.messageKey), reclaimed.leaseToken, reclaimed.leaseOwnerId, time(1)),
		).toEqual(completed);
	});

	it("cancels only an unleased or expired pending message and reports bounded status pages", async () => {
		const service = createService();
		const first = await service.enqueue(message("one"));
		const second = await service.enqueue(message("two"));
		const [delivery] = await service.claim(claim(1, 3, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		await expect(service.cancel(messageKey("one"), delivery.message.revision, time(2))).rejects.toBeInstanceOf(
			MailboxLeaseConflictError,
		);
		const cancelled = await service.cancel(messageKey("one"), delivery.message.revision, time(3));
		expect(cancelled).toMatchObject({ status: "cancelled", cancelledAt: time(3) });
		const [next] = await service.claim(claim(3, 5, 1));
		expect(next?.message.messageKey).toBe(second.messageKey);
		const status = await service.status(recipient);
		expect(status).toMatchObject({ pending: 1n, completed: 0n, cancelled: 1n });
		expect(status.oldestPendingMessageKey).toBe(second.messageKey);
		expect((await service.list({ ...recipient, statuses: ["cancelled", "pending"], limit: 1 })).records).toHaveLength(
			1,
		);
		await expect(service.list({ ...recipient, statuses: [], limit: 1 })).rejects.toBeInstanceOf(MailboxValidationError);
		expect(await service.purgeTerminal(recipient, time(4), 1)).toEqual([first.messageKey]);
	});

	it("keeps a blocked head from being skipped and rejects non-monotonic operational time", async () => {
		const service = createService();
		await service.enqueue(message("one"));
		await service.enqueue(message("two"));
		const claimed = await service.claim(claim(2, 5, 2));
		await service.abandon(
			sequenceKey(claimed[1]?.message.messageKey),
			claimed[1]?.leaseToken ?? "missing",
			claimed[1]?.leaseOwnerId ?? "missing",
			time(3),
		);
		expect(await service.claim(claim(4, 6, 2))).toEqual([]);
		await expect(service.claim(claim(1, 2, 1))).rejects.toBeInstanceOf(MailboxLeaseConflictError);
	});

	it("projects a secret-free per-attempt Pi input and fences Tool execution through the active registry", async () => {
		const tokens = ["lease-secret-one", "lease-secret-two"];
		const service = createService(limits, () => tokens.shift() ?? "lease-extra");
		await service.enqueue(message("one"));
		const [delivery] = await service.claim(claim(1, 3, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const projected = mailboxDeliveryToGameInput(delivery, limits);
		expect(projected).toMatchObject({
			sessionId: recipient.sessionId,
			actorId: recipient.actorId,
			inputId: expect.stringMatching(/^[0-9a-f]{64}$/u),
			payload: { type: "mailbox.message", deliveryAttempt: "1", message: { messageId: "one" } },
		});
		expect(JSON.stringify(projected)).not.toContain("lease-secret");

		let now = time(2);
		const registry = new MailboxRunLeaseRegistry(service, () => now);
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		expect(
			await registry.activate({
				...delivery,
				message: { ...delivery.message, payload: { forged: true } },
			}),
		).toEqual(projected);
		await expect(
			registry.authorizeToolExecution(projected, { toolName: "act" }, new AbortController().signal),
		).resolves.toBeUndefined();
		await registry.renew(receipt, time(2), time(4));
		now = time(4);
		await expect(
			registry.authorizeToolExecution(projected, { toolName: "act" }, new AbortController().signal),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		await registry.release(projected);

		const [reclaimed] = await service.claim(claim(4, 6, 1));
		if (reclaimed === undefined) throw new Error("Expected a reclaimed delivery.");
		const retryInput = mailboxDeliveryToGameInput(reclaimed, limits);
		expect(retryInput.payload.deliveryAttempt).toBe("2");
		expect(retryInput.inputId).not.toBe(projected.inputId);
	});

	it("bounds active delivery registrations and settles through the exact stored lease", async () => {
		const configured = { ...limits, maxActiveDeliveries: 1 };
		const service = createService(configured);
		const other = { ...recipient, actorId: "npc-2" };
		await service.enqueue(message("one"));
		await service.enqueue(message("two", other));
		const [first] = await service.claim(claim(1, 4, 1));
		const [second] = await service.claim({ ...claim(1, 4, 1), ...other });
		if (first === undefined || second === undefined) throw new Error("Expected both deliveries.");
		const registry = new MailboxRunLeaseRegistry(service, () => time(2));
		await registry.activate(first);
		await expect(registry.activate(second)).rejects.toEqual(new MailboxCapacityError("active_deliveries"));
		expect(
			await registry.settle(mailboxDeliveryToRunLeaseReceipt(first, configured), "completed", time(2)),
		).toMatchObject({ status: "completed" });
		expect(await registry.activate(second)).toEqual(mailboxDeliveryToGameInput(second, configured));
	});

	it("restores one exact active delivery from a portable lease receipt after registry restart", async () => {
		const service = createService(limits, () => "restart-lease-secret");
		await service.enqueue(message("restart"));
		const [delivery] = await service.claim(claim(1, 4, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const persisted = JSON.parse(JSON.stringify(receipt)) as unknown;
		const originalRegistry = new MailboxRunLeaseRegistry(service, () => time(2));
		const input = await originalRegistry.activate(delivery);

		const restartedRegistry = new MailboxRunLeaseRegistry(service, () => time(2));
		await expect(restartedRegistry.restore({ ...receipt, inputId: "0".repeat(64) })).rejects.toBeInstanceOf(
			MailboxLeaseConflictError,
		);
		await expect(restartedRegistry.restore(persisted)).resolves.toEqual(input);
		await expect(
			restartedRegistry.authorizeToolExecution(input, { toolName: "act" }, new AbortController().signal),
		).resolves.toBeUndefined();
		await expect(restartedRegistry.settle(receipt, "failed", time(2))).resolves.toMatchObject({ status: "pending" });
		const [reclaimed] = await service.claim(claim(2, 5, 1));
		expect(reclaimed?.message).toMatchObject({ messageId: "restart", attempt: "2" });
	});

	it("confirms a completed receipt after the durable write survives a registry crash", async () => {
		const service = createService(limits, () => "completed-lease-secret");
		await service.enqueue(message("completed-before-response"));
		const [delivery] = await service.claim(claim(1, 4, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const completed = await service.complete(
			sequenceKey(delivery.message.messageKey),
			delivery.leaseToken,
			delivery.leaseOwnerId,
			time(2),
		);

		const restartedRegistry = new MailboxRunLeaseRegistry(service, () => time(3));

		await expect(restartedRegistry.settle(receipt, "completed", time(3))).resolves.toEqual(completed);
		await expect(
			restartedRegistry.settle({ ...receipt, leaseOwnerId: "forged-owner" }, "completed", time(3)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
	});

	it("confirms a released receipt after a registry crash without crossing into the next attempt", async () => {
		const service = createService(limits, () => "released-lease-secret");
		await service.enqueue(message("released-before-response"));
		const [delivery] = await service.claim(claim(1, 4, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const released = await service.abandon(
			sequenceKey(delivery.message.messageKey),
			delivery.leaseToken,
			delivery.leaseOwnerId,
			time(2),
		);

		const restartedRegistry = new MailboxRunLeaseRegistry(service, () => time(3));
		await expect(restartedRegistry.settle(receipt, "failed", time(3))).resolves.toEqual(released);
		await expect(
			restartedRegistry.settle({ ...receipt, leaseOwnerId: "forged-owner" }, "failed", time(3)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);

		await service.claim(claim(3, 6, 1));
		await expect(restartedRegistry.settle(receipt, "failed", time(3))).rejects.toBeInstanceOf(
			MailboxLeaseConflictError,
		);
	});

	it("rejects a wrong receipt token while the delivery lease is still active", async () => {
		const service = createService(limits, () => "active-lease-secret");
		await service.enqueue(message("active-wrong-token"));
		const [delivery] = await service.claim(claim(1, 4, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const restartedRegistry = new MailboxRunLeaseRegistry(service, () => time(2));

		await expect(
			restartedRegistry.settle({ ...receipt, leaseToken: "wrong-lease-secret" }, "failed", time(2)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		await expect(
			service.assertLease(sequenceKey(delivery.message.messageKey), delivery.leaseToken, time(2)),
		).resolves.toMatchObject({
			leaseToken: delivery.leaseToken,
		});
	});

	it("renews an exact receipt after registry restart before restoring its Tool fence", async () => {
		const service = createService(limits, () => "renewed-lease-secret");
		await service.enqueue(message("renew-after-restart"));
		const [delivery] = await service.claim(claim(1, 3, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const restartedRegistry = new MailboxRunLeaseRegistry(service, () => time(4));

		await expect(restartedRegistry.renew(receipt, time(2), time(5))).resolves.toMatchObject({
			leaseToken: receipt.leaseToken,
			leaseOwnerId: receipt.leaseOwnerId,
			leaseExpiresAt: time(5),
		});
		await expect(restartedRegistry.restore(receipt)).resolves.toMatchObject({ inputId: receipt.inputId });
	});

	it("rejects a mismatched renewal receipt before changing the durable lease", async () => {
		const service = createService(limits, () => "renew-owner-secret");
		await service.enqueue(message("reject-renew-owner"));
		const [delivery] = await service.claim(claim(1, 4, 1));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const restartedRegistry = new MailboxRunLeaseRegistry(service, () => time(2));

		await expect(
			restartedRegistry.renew({ ...receipt, leaseOwnerId: "forged-owner" }, time(2), time(6)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		await expect(
			service.assertLease(sequenceKey(delivery.message.messageKey), delivery.leaseToken, time(5)),
		).rejects.toBeInstanceOf(MailboxLeaseConflictError);
	});

	it("serializes competing active delivery registrations at the configured capacity", async () => {
		const configured = { ...limits, maxActiveDeliveries: 1 };
		const service = createService(configured);
		const other = { ...recipient, actorId: "npc-2" };
		await service.enqueue(message("one"));
		await service.enqueue(message("two", other));
		const [first] = await service.claim(claim(1, 4, 1));
		const [second] = await service.claim({ ...claim(1, 4, 1), ...other });
		if (first === undefined || second === undefined) throw new Error("Expected both deliveries.");

		const registry = new MailboxRunLeaseRegistry(service, () => time(2));
		const results = await Promise.allSettled([registry.activate(first), registry.activate(second)]);
		expect(results.filter((result) => result.status === "fulfilled")).toHaveLength(1);
		const rejected = results.find((result) => result.status === "rejected");
		expect(rejected).toMatchObject({
			status: "rejected",
			reason: new MailboxCapacityError("active_deliveries"),
		});

		const fulfilled = results.find((result) => result.status === "fulfilled");
		if (fulfilled?.status !== "fulfilled") throw new Error("Expected one active delivery.");
		expect(await registry.release(fulfilled.value)).toBe(true);
	});
});

const recipient: MailboxRecipient = {
	ownerId: "world",
	timelineId: "main",
	generation: "0",
	sessionId: "npc-session",
	actorId: "npc-1",
};

function createService(configured: MailboxLimits = limits, newLeaseToken?: () => string): MailboxService {
	return new MailboxService(new InMemoryMailboxStore(configured, newLeaseToken), configured);
}

function message(messageId: string, target: MailboxRecipient = recipient): MailboxMessageDraft {
	return {
		messageId,
		recipient: target,
		senderId: "game",
		kind: "world.event",
		payload: { messageId },
		gameTime: { timelineId: target.timelineId, generation: target.generation, tick: "10" },
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

function sequenceKey(messageKey: bigint | undefined): MailboxMessageSequenceKey {
	if (messageKey === undefined) throw new Error("Expected a message key.");
	return { ...recipient, messageKey };
}

function time(second: number): string {
	return `2026-08-28T00:00:${second.toString().padStart(2, "0")}.000Z`;
}
