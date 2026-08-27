import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type {
	GameInput,
	GameToolCall,
	GameToolDefinition,
	GameToolExecutionContext,
	GameToolResult,
} from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import {
	GameToolApprovalBroker,
	GameToolApprovalDeniedError,
	GameToolApprovalMiddleware,
	SqliteGameToolApprovalStore,
} from "./tool-approval.js";

const directories: string[] = [];
const input: GameInput = {
	id: "input-1",
	type: "npc.command",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 4,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 42.5 },
	content: [{ type: "text", text: "build" }],
};
const tool: GameToolDefinition = {
	name: "write_world",
	label: "Write world",
	description: "Mutate authoritative state.",
	parameters: { type: "object" },
	risk: "high",
};
const call: GameToolCall = { id: "call-1", name: tool.name, arguments: { amount: 1, nested: { b: 2, a: 1 } } };

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

async function approvalStore(): Promise<{ store: SqliteGameToolApprovalStore; path: string }> {
	const directory = await mkdtemp(join(tmpdir(), "oga-approval-"));
	directories.push(directory);
	const path = join(directory, "approvals.sqlite");
	return { store: new SqliteGameToolApprovalStore(path), path };
}

function context(overrides: Partial<GameToolExecutionContext> = {}): GameToolExecutionContext {
	return {
		input,
		runId: "run-1",
		turn: 2,
		toolCallIndex: 0,
		signal: new AbortController().signal,
		...overrides,
	};
}

const result: GameToolResult = { content: [{ type: "json", value: { ok: true } }] };

describe("GameToolApprovalMiddleware", () => {
	it("enforces disabled, explicit-only and allowed-in-task before execution", async () => {
		using store = (await approvalStore()).store;
		const broker = new GameToolApprovalBroker(store);
		const worldState = { read: () => ({ generationId: "world-4", revision: 10 }) };
		let executions = 0;
		const next = async () => {
			executions += 1;
			return result;
		};

		const disabled = new GameToolApprovalMiddleware({
			rules: [{ id: "disabled", mode: "disabled", toolName: tool.name }],
			broker,
			worldState,
		});
		await expect(disabled.execute(tool, call, context(), next)).rejects.toBeInstanceOf(GameToolApprovalDeniedError);

		const explicit = new GameToolApprovalMiddleware({
			rules: [{ id: "explicit", mode: "explicit-only", toolName: tool.name }],
			broker,
			worldState,
			scope: { resolve: () => ({ explicitlyRequestedTools: [tool.name] }) },
		});
		await expect(explicit.execute(tool, call, context(), next)).resolves.toEqual(result);

		const task = new GameToolApprovalMiddleware({
			rules: [{ id: "task", mode: "allowed-in-task", minimumRisk: "medium" }],
			broker,
			worldState,
			scope: { resolve: () => ({ taskId: "task-1", taskAllowedTools: [tool.name] }) },
		});
		await expect(task.execute(tool, call, context(), next)).resolves.toEqual(result);
		expect(executions).toBe(2);
	});

	it("issues a one-time world-bound approval before calling the tool", async () => {
		using store = (await approvalStore()).store;
		const broker = new GameToolApprovalBroker(store);
		let worldRevision = 10;
		let executions = 0;
		let pendingResolve: (() => void) | undefined;
		let pending = new Promise<void>((resolve) => {
			pendingResolve = resolve;
		});
		const events: Array<{ approvalId: string; status: string; waitMilliseconds: number }> = [];
		const middleware = new GameToolApprovalMiddleware({
			rules: [{ id: "confirm-write", mode: "confirm-once", minimumRisk: "high" }],
			broker,
			worldState: { read: () => ({ generationId: "world-4", revision: worldRevision }) },
			timeoutMilliseconds: 5_000,
			onEvent(event) {
				events.push(event);
				if (event.status === "pending") pendingResolve?.();
			},
		});
		const execute = () =>
			middleware.execute(tool, call, context(), async () => {
				executions += 1;
				return result;
			});
		const first = execute();
		await pending;
		expect(executions).toBe(0);
		const [request] = await broker.listPending(input.session, 8);
		expect(request?.request.canonicalArguments).toBe('{"amount":1,"nested":{"a":1,"b":2}}');
		await broker.respond({
			session: input.session,
			approvalId: request?.request.approvalId as string,
			expectedRevision: request?.revision as number,
			decision: "approve",
		});
		await expect(first).resolves.toEqual(result);
		expect(executions).toBe(1);
		expect((await store.read(input.session, request?.request.approvalId as string))?.status).toBe("consumed");
		expect(events.at(-1)).toMatchObject({ status: "consumed" });

		pending = new Promise<void>((resolve) => {
			pendingResolve = resolve;
		});
		const changed = middleware.execute(tool, { ...call, arguments: { amount: 2 } }, context(), async () => result);
		await pending;
		const [changedRequest] = await broker.listPending(input.session, 8);
		expect(changedRequest?.request.approvalId).not.toBe(request?.request.approvalId);
		worldRevision = 11;
		await broker.respond({
			session: input.session,
			approvalId: changedRequest?.request.approvalId as string,
			expectedRevision: changedRequest?.revision as number,
			decision: "approve",
		});
		await expect(changed).rejects.toMatchObject({ status: "expired" });
	});

	it("persists pending records across restart and enforces owner isolation and CAS", async () => {
		const created = await approvalStore();
		const request = {
			approvalId: "approval-v1-record",
			policyId: "confirm",
			session: input.session,
			inputId: input.id,
			runId: "run",
			turn: 1,
			toolCallIndex: 0,
			toolCallId: "call",
			toolName: tool.name,
			risk: "high" as const,
			canonicalArguments: "{}",
			argumentsDigest: "digest",
			world: { generationId: "world-4", revision: 10 },
			requestedAt: Date.now(),
			expiresAt: Date.now() + 10_000,
		};
		await created.store.create(request);
		created.store[Symbol.dispose]();
		using restarted = new SqliteGameToolApprovalStore(created.path);
		expect((await restarted.read(input.session, request.approvalId))?.status).toBe("pending");
		expect(await restarted.read({ ...input.session, actorId: "other" }, request.approvalId)).toBeUndefined();
		await expect(
			restarted.respond(
				{ session: input.session, approvalId: request.approvalId, expectedRevision: 99, decision: "deny" },
				undefined,
			),
		).rejects.toThrow("conflict");
	});

	it("times out fail-closed without invoking the tool", async () => {
		using store = (await approvalStore()).store;
		const middleware = new GameToolApprovalMiddleware({
			rules: [{ id: "confirm", mode: "confirm-once", toolName: tool.name }],
			broker: new GameToolApprovalBroker(store),
			worldState: { read: () => ({ generationId: "world-4", revision: 10 }) },
			timeoutMilliseconds: 5,
		});
		let executed = false;
		await expect(
			middleware.execute(tool, call, context(), async () => {
				executed = true;
				return result;
			}),
		).rejects.toMatchObject({ status: "timed-out" });
		expect(executed).toBe(false);
	});
});
