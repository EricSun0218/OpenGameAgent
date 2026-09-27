import type { StreamFn } from "@earendil-works/pi-agent-core";
import {
	InMemoryMailboxStore,
	MailboxLeaseConflictError,
	MailboxRunLeaseRegistry,
	MailboxService,
	mailboxDeliveryToRunLeaseReceipt,
	type MailboxLimits,
} from "@opengameagent/extensions";
import { describe, expect, it } from "vitest";
import { GameRuntime, InMemoryEventStore } from "../src/index.js";
import {
	allowAllTestTools,
	assistant,
	messageResponse,
	scriptedStream,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "./pi-fakes.js";

const limits: MailboxLimits = {
	maxIdentifierBytes: 128,
	maxPayloadBytes: 4096,
	maxTickDigits: 20,
	maxCounterDigits: 20,
	maxPendingMessagesPerTimelineGeneration: 2,
	maxPendingMessagesPerRecipient: 2,
	maxActiveDeliveries: 1,
	maxPageSize: 2,
	maxClaimBatchSize: 1,
	maxPurgeBatchSize: 1,
};

describe("mailbox delivery through the Pi-backed runtime", () => {
	it("fences an already restored mailbox Tool run and refuses settlement after retirement", async () => {
		const mailbox = new MailboxService(new InMemoryMailboxStore(limits, () => "lease-secret"), limits);
		await mailbox.enqueue(message());
		const [delivery] = await mailbox.claim(claim(1, 5));
		if (!delivery) throw new Error("Expected a delivery.");
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		const leases = new MailboxRunLeaseRegistry(mailbox, () => time(2));
		const input = await leases.restore(receipt);
		const request = { retirementId: "mailbox-runtime-retirement", partition: recipient, recordedAt: time(2) };
		let writes = 0;
		const act = tool("act", async () => {
			writes += 1;
			return { content: [{ type: "text", text: "must not run" }], details: {} };
		});
		let calls = 0;
		const runtime = await createRuntime(
			async () => {
				if (calls++ === 0) {
					await mailbox.beginAuthorityRetirement(request);
					return messageResponse(assistant([toolCall("retired-tool", "act")], "toolUse"));
				}
				return textResponse("retired work");
			},
			leases,
			act,
		);
		const run = await runtime.submit(input);
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });
		expect(writes).toBe(0);
		await expect(leases.settle(receipt, "completed", time(2))).rejects.toMatchObject({
			name: "WorkAuthorityRetiredError",
		});
		expect(await mailbox.get({ ...recipient, messageId: delivery.message.messageId })).toMatchObject({
			status: "pending",
		});
		await mailbox.finalizeAuthorityRetirement(request);
		await expect(leases.restore(receipt)).rejects.toMatchObject({ name: "WorkAuthorityRetiredError" });
		expect(await mailbox.get({ ...recipient, messageId: delivery.message.messageId })).toMatchObject({
			status: "cancelled",
		});
	});

	it("submits a secret-free ordinary input and settles only after a guarded Tool run", async () => {
		const mailbox = new MailboxService(new InMemoryMailboxStore(limits, () => "lease-secret"), limits);
		await mailbox.enqueue(message());
		const [delivery] = await mailbox.claim(claim(1, 5));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		let now = time(2);
		const leases = new MailboxRunLeaseRegistry(mailbox, () => now);
		const input = await leases.activate(delivery);
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		let writes = 0;
		const act = tool("act", async () => {
			writes += 1;
			return { content: [{ type: "text", text: "acted" }], details: {} };
		});
		const runtime = await createRuntime(
			scriptedStream([
				() => messageResponse(assistant([toolCall("call-act", "act")], "toolUse")),
				() => textResponse("done"),
			]),
			leases,
			act,
		);

		const handle = await runtime.submit(input);
		const result = await handle.completion;
		expect(result.status).toBe("completed");
		if (result.status === "deferred") throw new Error("Expected a terminal mailbox run.");
		now = time(3);
		expect(await leases.settle(receipt, result.status, now)).toMatchObject({ status: "completed" });
		expect(writes).toBe(1);
		expect(JSON.stringify(input)).not.toContain("lease-secret");
	});

	it("fences the real Tool call and refuses mailbox completion when the lease expires first", async () => {
		const mailbox = new MailboxService(new InMemoryMailboxStore(limits, () => "lease-secret"), limits);
		await mailbox.enqueue(message());
		const [delivery] = await mailbox.claim(claim(1, 3));
		if (delivery === undefined) throw new Error("Expected a delivery.");
		let now = time(2);
		const leases = new MailboxRunLeaseRegistry(mailbox, () => now);
		const input = await leases.activate(delivery);
		const receipt = mailboxDeliveryToRunLeaseReceipt(delivery, limits);
		let writes = 0;
		const act = tool("act", async () => {
			writes += 1;
			return { content: [{ type: "text", text: "must not run" }], details: {} };
		});
		let requests = 0;
		const modelStream: StreamFn = () => {
			requests += 1;
			if (requests === 1) {
				now = time(3);
				return messageResponse(assistant([toolCall("call-act", "act")], "toolUse"));
			}
			return textResponse("authority unavailable");
		};
		const runtime = await createRuntime(modelStream, leases, act);

		const handle = await runtime.submit(input);
		const result = await handle.completion;
		expect(result.status).toBe("completed");
		if (result.status === "deferred") throw new Error("Expected a terminal mailbox run.");
		expect(writes).toBe(0);
		await expect(leases.settle(receipt, result.status, now)).rejects.toBeInstanceOf(MailboxLeaseConflictError);
		expect((await mailbox.status(recipient)).pending).toBe(1n);
	});
});

async function createRuntime(
	modelStream: StreamFn,
	leases: MailboxRunLeaseRegistry,
	act: ReturnType<typeof tool>,
): Promise<GameRuntime> {
	let eventId = 0;
	const runtime = new GameRuntime(
		new InMemoryEventStore({
			newEventId: () => `event-${++eventId}`,
			now: () => time(0),
		}),
		{ maxActiveActors: 1, maxQueuedRuns: 4, maxInputBytes: 4_096, maxInputDepth: 8 },
	);
	await runtime.registerActor({
		sessionId: recipient.sessionId,
		actorId: recipient.actorId,
		model: TEST_MODEL,
		modelStream,
		toolSource: () => [act],
		toolPolicy: allowAllTestTools,
		toolLimits: TEST_TOOL_LIMITS,
		beforeToolExecution: leases.authorizeToolExecution,
		newId: () => `agent-${++eventId}`,
		now: () => 1,
	});
	return runtime;
}

const recipient = {
	ownerId: "world",
	timelineId: "main",
	generation: "0",
	sessionId: "npc-session",
	actorId: "npc-1",
} as const;

function message() {
	return {
		messageId: "message-1",
		recipient,
		senderId: "game",
		kind: "world.event",
		payload: { instruction: "act" },
		gameTime: { timelineId: "main", generation: "0", tick: "10" },
		correlationId: "cause-1",
		recordedAt: time(0),
	} as const;
}

function claim(now: number, expires: number) {
	return {
		...recipient,
		leaseOwnerId: "runtime-1",
		operationalNow: time(now),
		leaseExpiresAt: time(expires),
		limit: 1,
	};
}

function time(second: number): string {
	return `2026-08-29T00:00:${second.toString().padStart(2, "0")}.000Z`;
}
