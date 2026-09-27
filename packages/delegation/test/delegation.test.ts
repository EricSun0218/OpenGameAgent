import type { StreamFn } from "@earendil-works/pi-agent-core";
import { DurableTranscript, InMemoryTranscriptStore } from "@opengameagent/agent";
import type { StreamKey } from "@opengameagent/protocol";
import { GameRuntime, InMemoryEventStore } from "@opengameagent/runtime";
import { describe, expect, it, vi } from "vitest";
import {
	assistant,
	allowAllTestTools,
	controlledResponse,
	messageResponse,
	scriptedStream,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "../../devtools/src/pi-fakes.js";
import {
	DelegationCapacityError,
	DelegationLeaseError,
	DelegationManager,
	DelegationService,
	InMemoryDelegationStore,
	type DelegationDraft,
	type DelegationLimits,
} from "../src/index.js";

const LIMITS: DelegationLimits = {
	maxIdentifierBytes: 128,
	maxControlTextBytes: 256,
	maxToolDescriptionBytes: 512,
	maxTaskBytes: 1_024,
	maxContextBytes: 1_024,
	maxResultBytes: 1_024,
	maxModelResultBytes: 1_024,
	maxJsonDepth: 8,
	maxCounterDigits: 20,
	maxDepth: 1,
	maxTargets: 2,
	maxPendingPerGeneration: 2,
	maxActiveRuns: 1,
	maxPageSize: 4,
	maxRecoveryBatchSize: 2,
	maxPurgeBatchSize: 2,
};
const PARTITION = { ownerId: "game", timelineId: "world", generation: "1" } as const;
const PARENT: StreamKey = { sessionId: "session", actorId: "parent" };
const CHILD: StreamKey = { sessionId: "session", actorId: "child" };
const NOW = "2026-08-29T03:00:00.000Z";
const EXPIRY = "2026-08-29T03:01:00.000Z";

function draft(id: string): DelegationDraft {
	return {
		delegationId: id,
		partition: PARTITION,
		parent: { ...PARENT, inputId: "parent-input", runId: "parent-run", turn: "1", toolCallId: `call-${id}` },
		target: { delegateId: "worker", ...CHILD, maxTurns: 1 },
		mode: "background",
		task: { instruction: id },
		rootDelegationId: id,
		depth: "1",
		childInputId: `child-input-${id}`,
		createdAt: NOW,
	};
}

describe("delegation authority", () => {
	it("rejects missing and internally inconsistent host limits", () => {
		const incomplete: { -readonly [Key in keyof DelegationLimits]?: DelegationLimits[Key] } = { ...LIMITS };
		delete incomplete.maxTaskBytes;
		expect(() => new InMemoryDelegationStore(incomplete as DelegationLimits)).toThrow("invalid_maxTaskBytes");
		expect(
			() =>
				new InMemoryDelegationStore({
					...LIMITS,
					maxPageSize: 1,
					maxRecoveryBatchSize: 2,
				}),
		).toThrow("invalid_delegation_limits");
	});

	it("is idempotent, bounded, reclaimable, and fences stale leases", async () => {
		let lease = 0;
		const store = new InMemoryDelegationStore(LIMITS, () => `lease-${++lease}`);
		const service = new DelegationService(store, LIMITS);
		const first = await service.create(draft("one"));
		await expect(service.create(draft("one"))).resolves.toEqual(first);
		await service.create(draft("two"));
		await expect(service.create(draft("three"))).rejects.toBeInstanceOf(DelegationCapacityError);

		const claimed = await service.claim({
			...PARTITION,
			delegationId: "one",
			leaseOwnerId: "worker-a",
			operationalNow: NOW,
			leaseExpiresAt: EXPIRY,
		});
		await expect(
			service.assertLease({ ...PARTITION, delegationKey: claimed.record.delegationKey }, claimed.leaseToken, EXPIRY),
		).rejects.toBeInstanceOf(DelegationLeaseError);
		const reclaimed = await service.claim({
			...PARTITION,
			delegationId: "one",
			leaseOwnerId: "worker-b",
			operationalNow: EXPIRY,
			leaseExpiresAt: "2026-08-29T03:02:00.000Z",
		});
		expect(reclaimed.record.attempt).toBe("2");
		await expect(
			service.complete(
				{ ...PARTITION, delegationKey: first.delegationKey },
				claimed.leaseToken,
				{ stale: true },
				EXPIRY,
			),
		).rejects.toBeInstanceOf(DelegationLeaseError);
		const completed = await service.complete(
			{ ...PARTITION, delegationKey: first.delegationKey },
			reclaimed.leaseToken,
			{ accepted: true },
			EXPIRY,
		);
		await expect(
			service.complete(
				{ ...PARTITION, delegationKey: first.delegationKey },
				reclaimed.leaseToken,
				{ accepted: true },
				EXPIRY,
			),
		).resolves.toEqual(completed);
		await expect(
			service.complete(
				{ ...PARTITION, delegationKey: first.delegationKey },
				reclaimed.leaseToken,
				{ accepted: false },
				EXPIRY,
			),
		).rejects.toBeInstanceOf(DelegationLeaseError);

		const second = await service.claim({
			...PARTITION,
			delegationId: "two",
			leaseOwnerId: "worker-a",
			operationalNow: NOW,
			leaseExpiresAt: EXPIRY,
		});
		const renewed = await service.renew(
			{ ...PARTITION, delegationKey: second.record.delegationKey },
			second.leaseToken,
			NOW,
			"2026-08-29T03:02:00.000Z",
		);
		expect(renewed.leaseExpiresAt).toBe("2026-08-29T03:02:00.000Z");
		await expect(
			service.claim({
				...PARTITION,
				delegationId: "two",
				leaseOwnerId: "worker-b",
				operationalNow: EXPIRY,
				leaseExpiresAt: "2026-08-29T03:03:00.000Z",
			}),
		).rejects.toBeInstanceOf(DelegationLeaseError);
	});
});

describe("delegation over one Pi runtime", () => {
	it("exposes no model-side delegation controls when the host authorizes no target", async () => {
		const service = new DelegationService(new InMemoryDelegationStore(LIMITS), LIMITS);
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 2,
			maxQueuedRuns: 1,
			maxInputBytes: 1_024,
			maxInputDepth: 8,
		});
		const manager = await DelegationManager.create({
			service,
			runtime,
			targets: [
				{
					delegateId: "worker",
					...CHILD,
					maxTurns: 1,
					actor: { model: TEST_MODEL, modelStream: () => textResponse("unused") },
				},
			],
			outcome: () => null,
			workerId: "worker",
			operationalNow: () => NOW,
			leaseExpiresAt: () => EXPIRY,
			authorizeTarget: () => false,
		});
		expect(
			manager.createTools({
				input: { ...PARENT, inputId: "input", payload: {} },
				coordinate: { ...PARENT, runId: "run", turn: "1" },
				signal: new AbortController().signal,
				partition: PARTITION,
			}),
		).toEqual([]);
	});

	it("stops a foreground child at its Pi turn ceiling and rejects a missing final output", async () => {
		const service = new DelegationService(new InMemoryDelegationStore(LIMITS, () => "lease"), LIMITS);
		let runId = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 2,
			maxQueuedRuns: 4,
			maxInputBytes: 8_192,
			maxInputDepth: 12,
			newRunId: () => `run-${++runId}`,
		});
		let childRequests = 0;
		const childTool = tool("inspect", async () => ({ content: [{ type: "text", text: "inspected" }], details: {} }));
		const childStream: StreamFn = () => {
			childRequests += 1;
			if (childRequests > 1) throw new Error("turn limit did not stop Pi");
			return messageResponse(assistant([toolCall("child-call", "inspect")], "toolUse"));
		};
		const outcome = vi.fn(() => ({ accepted: true }));
		const manager = await DelegationManager.create({
			service,
			runtime,
			targets: [
				{
					delegateId: "worker",
					...CHILD,
					maxTurns: 1,
					actor: {
						model: TEST_MODEL,
						modelStream: childStream,
						toolSource: () => [childTool],
						toolPolicy: allowAllTestTools,
						toolLimits: TEST_TOOL_LIMITS,
						now: () => 1,
					},
				},
			],
			outcome,
			workerId: "local-worker",
			operationalNow: () => NOW,
			leaseExpiresAt: () => EXPIRY,
			authorizeTarget: () => true,
		});

		const parentStream = scriptedStream([
			() =>
				messageResponse(
					assistant(
						[
							{
								type: "toolCall",
								id: "delegate-call",
								name: "delegate_game_task",
								arguments: {
									delegateId: "worker",
									mode: "foreground",
									task: { instruction: "inspect" },
								},
							},
						],
						"toolUse",
					),
				),
			() => textResponse("parent done"),
		]);
		await runtime.registerActor({
			...PARENT,
			model: TEST_MODEL,
			modelStream: parentStream,
			toolSource: (input, signal, coordinate) =>
				manager.createTools({ input, signal, coordinate, partition: PARTITION }),
			toolPolicy: allowAllTestTools,
			toolLimits: TEST_TOOL_LIMITS,
			now: () => 1,
		});
		const parent = runtime.start({ ...PARENT, inputId: "parent-input", payload: { instruction: "delegate" } });
		await expect(parent.completion).resolves.toMatchObject({ status: "completed" });
		expect(childRequests).toBe(1);
		const page = await service.list({ ...PARTITION, limit: 4 });
		expect(page.records).toHaveLength(1);
		expect(page.records[0]).toMatchObject({ status: "failed", failureCode: "result_unavailable", depth: "1" });
		expect(outcome).not.toHaveBeenCalled();
	});

	it("projects only each completed child run's bounded final output on a reused stream", async () => {
		const service = new DelegationService(new InMemoryDelegationStore(LIMITS, () => "lease"), LIMITS);
		const outputs = [...Array.from({ length: 20 }, (_, index) => `history-${index}`), "first", "second"];
		let runId = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 2,
			maxQueuedRuns: 2,
			maxInputBytes: 8_192,
			maxInputDepth: 12,
			newRunId: () => `run-${++runId}`,
		});
		const outcome = vi.fn(({ childFinalOutput }: { childFinalOutput: string }) => ({ answer: childFinalOutput }));
		const manager = await DelegationManager.create({
			service,
			runtime,
			targets: [
				{
					delegateId: "worker",
					...CHILD,
					maxTurns: 1,
					actor: {
						model: TEST_MODEL,
						modelStream: () => textResponse(outputs.shift() ?? "unexpected"),
					},
				},
			],
			outcome,
			workerId: "local-worker",
			operationalNow: () => NOW,
			leaseExpiresAt: () => EXPIRY,
			authorizeTarget: () => true,
		});
		for (let index = 0; index < 20; index += 1) {
			const historic = await runtime.submit({ ...CHILD, inputId: `historic-${index}`, payload: { index } });
			await expect(historic.completion).resolves.toMatchObject({ status: "completed" });
		}
		const readEvents = vi.spyOn(runtime, "readEvents");

		await service.create(draft("first"));
		await expect(
			manager.run({ ...PARTITION, delegationId: "first" }, new AbortController().signal),
		).resolves.toMatchObject({ status: "completed", result: { answer: "first" } });
		await service.create(draft("second"));
		await expect(
			manager.run({ ...PARTITION, delegationId: "second" }, new AbortController().signal),
		).resolves.toMatchObject({ status: "completed", result: { answer: "second" } });
		outputs.push("x".repeat(LIMITS.maxResultBytes));
		await service.create(draft("oversized"));
		await expect(
			manager.run({ ...PARTITION, delegationId: "oversized" }, new AbortController().signal),
		).resolves.toMatchObject({ status: "failed", failureCode: "result_unavailable" });
		expect(outcome).toHaveBeenCalledTimes(2);
		expect(readEvents).not.toHaveBeenCalled();
	});

	it("keeps a deferred child nonterminal and resumes it on the next explicit worker run", async () => {
		const service = new DelegationService(new InMemoryDelegationStore(LIMITS, () => "lease"), LIMITS);
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 2,
			maxQueuedRuns: 2,
			maxInputBytes: 8_192,
			maxInputDepth: 12,
			transcript,
		});
		const deferredHandle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		let providerCalls = 0;
		let fetchCalls = 0;
		const manager = await DelegationManager.create({
			service,
			runtime,
			targets: [
				{
					delegateId: "worker",
					...CHILD,
					maxTurns: 1,
					actor: {
						model: TEST_MODEL,
						modelStream: () => {
							providerCalls += 1;
							return messageResponse({ ...assistant([], "deferred"), deferred: deferredHandle });
						},
						deferredResponses: {
							fetch: async () => {
								fetchCalls += 1;
								return assistant([{ type: "text", text: "done" }]);
							},
							cancel: async () => undefined,
						},
					},
				},
			],
			outcome: () => ({ accepted: true }),
			workerId: "local-worker",
			operationalNow: () => NOW,
			leaseExpiresAt: () => EXPIRY,
			authorizeTarget: () => true,
		});
		await service.create(draft("deferred"));
		const signal = new AbortController().signal;

		await expect(manager.run({ ...PARTITION, delegationId: "deferred" }, signal)).resolves.toMatchObject({
			status: "pending",
		});
		await expect(service.require({ ...PARTITION, delegationId: "deferred" })).resolves.not.toHaveProperty(
			"failureCode",
		);
		await expect(manager.run({ ...PARTITION, delegationId: "deferred" }, signal)).resolves.toMatchObject({
			status: "completed",
			result: { accepted: true },
		});
		expect(providerCalls).toBe(1);
		expect(fetchCalls).toBe(1);
	});

	it("rejects a runtime whose actor capacity cannot satisfy foreground nesting", async () => {
		const service = new DelegationService(new InMemoryDelegationStore(LIMITS), LIMITS);
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 1_024,
			maxInputDepth: 8,
		});
		await expect(
			DelegationManager.create({
				service,
				runtime,
				targets: [
					{
						delegateId: "worker",
						...CHILD,
						maxTurns: 1,
						actor: { model: TEST_MODEL, modelStream: () => textResponse("done") },
					},
				],
				outcome: () => null,
				workerId: "worker",
				operationalNow: () => NOW,
				leaseExpiresAt: () => EXPIRY,
				authorizeTarget: () => true,
			}),
		).rejects.toThrow("runtime_actor_capacity_below_delegation_depth");
	});

	it("cancels the exact active child when its foreground owner aborts", async () => {
		const service = new DelegationService(new InMemoryDelegationStore(LIMITS, () => "lease"), LIMITS);
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 2,
			maxQueuedRuns: 2,
			maxInputBytes: 8_192,
			maxInputDepth: 12,
		});
		const child = controlledResponse();
		const manager = await DelegationManager.create({
			service,
			runtime,
			targets: [
				{
					delegateId: "worker",
					...CHILD,
					maxTurns: 1,
					actor: { model: TEST_MODEL, modelStream: child.streamFn },
				},
			],
			outcome: () => ({ done: true }),
			workerId: "local-worker",
			operationalNow: () => NOW,
			leaseExpiresAt: () => EXPIRY,
			authorizeTarget: () => true,
		});
		await service.create(draft("cancel"));
		const controller = new AbortController();
		const running = manager.run({ ...PARTITION, delegationId: "cancel" }, controller.signal);
		await child.started;
		controller.abort(new Error("parent aborted"));
		await expect(running).rejects.toThrow("parent aborted");
		await expect(service.require({ ...PARTITION, delegationId: "cancel" })).resolves.toMatchObject({
			status: "cancelled",
		});
	});

	it("settles an unavailable host outcome as a stable failure during recovery", async () => {
		const service = new DelegationService(new InMemoryDelegationStore(LIMITS, () => "lease"), LIMITS);
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 2,
			maxQueuedRuns: 2,
			maxInputBytes: 8_192,
			maxInputDepth: 12,
		});
		const manager = await DelegationManager.create({
			service,
			runtime,
			targets: [
				{
					delegateId: "worker",
					...CHILD,
					maxTurns: 1,
					actor: { model: TEST_MODEL, modelStream: () => textResponse("done") },
				},
			],
			outcome: () => {
				throw new Error("host result missing");
			},
			workerId: "local-worker",
			operationalNow: () => NOW,
			leaseExpiresAt: () => EXPIRY,
			authorizeTarget: () => true,
		});
		await service.create(draft("recover"));
		await expect(manager.recover(PARTITION, new AbortController().signal)).resolves.toMatchObject([
			{ status: "failed", failureCode: "result_unavailable" },
		]);
	});
});
