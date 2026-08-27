import type {
	GameInput,
	GameToolCall,
	GameToolDefinition,
	GameToolExecutionContext,
	GameToolResult,
} from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import {
	createGameToolPolicyResources,
	type GameToolAuthorityScope,
	type GameToolPolicy,
	InMemoryGameToolPolicyAuditSink,
} from "./tool-policy.js";

const input: GameInput = {
	id: "input-1",
	type: "npc.chat",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 12 },
	content: [{ type: "text", text: "hello" }],
};

function tool(name: string): GameToolDefinition {
	return {
		name,
		label: name,
		description: name,
		parameters: { type: "object", properties: {}, additionalProperties: false },
	};
}

function executionContext(): GameToolExecutionContext {
	return {
		input,
		runId: "run-1",
		turn: 2,
		toolCallIndex: 0,
		signal: new AbortController().signal,
	};
}

describe("game tool policies", () => {
	it("hides tools outside the host-derived scope and rechecks scope before execution", async () => {
		let scope: GameToolAuthorityScope = { id: "ordinary-npc", allowedTools: ["look"] };
		const audit = new InMemoryGameToolPolicyAuditSink();
		const resources = createGameToolPolicyResources({ scopeProvider: { resolve: () => scope }, audit });
		const signal = new AbortController().signal;

		expect(await resources.visibility.isVisible(input, tool("look"), signal)).toBe(true);
		expect(await resources.visibility.isVisible(input, tool("write"), signal)).toBe(false);

		scope = { id: "revoked", deniedTools: ["look"] };
		const next = vi.fn<() => Promise<GameToolResult>>().mockResolvedValue({ content: [{ type: "text", text: "ran" }] });
		const denied = await resources.execution.execute(
			tool("look"),
			{ id: "call-1", name: "look", arguments: { secret: "must-not-enter-audit" } },
			executionContext(),
			next,
		);
		expect(denied).toEqual({
			isError: true,
			content: [{ type: "json", value: { error: "tool_denied" } }],
		});
		expect(next).not.toHaveBeenCalled();
		expect(JSON.stringify(audit.read())).not.toContain("must-not-enter-audit");
		expect(audit.read().at(-1)).toMatchObject({
			phase: "execute",
			inputId: "input-1",
			runId: "run-1",
			turn: 2,
			toolName: "look",
			scopeId: "revoked",
			effect: "hide",
		});
	});

	it("composes policies in order and blocks without invoking the tool", async () => {
		const seen: string[] = [];
		const policies: GameToolPolicy[] = [
			{
				evaluate(context) {
					seen.push(`first:${context.phase}`);
					return { effect: "allow", reason: "first-allowed" };
				},
			},
			{
				evaluate(context) {
					seen.push(`second:${context.phase}`);
					return context.phase === "advertise"
						? { effect: "allow", reason: "visible" }
						: { effect: "deny", reason: "world-state-denied" };
				},
			},
			{
				evaluate() {
					seen.push("unreachable");
					return { effect: "allow", reason: "wrong" };
				},
			},
		];
		const resources = createGameToolPolicyResources({
			scopeProvider: { resolve: () => ({ id: "scope" }) },
			policies,
		});
		const signal = new AbortController().signal;
		expect(await resources.visibility.isVisible(input, tool("build"), signal)).toBe(true);
		const next = vi.fn<() => Promise<GameToolResult>>().mockResolvedValue({ content: [] });
		const result = await resources.execution.execute(
			tool("build"),
			{ id: "call", name: "build", arguments: {} },
			executionContext(),
			next,
		);
		expect(result).toMatchObject({ isError: true });
		expect(next).not.toHaveBeenCalled();
		expect(seen).toEqual(["first:advertise", "second:advertise", "unreachable", "first:execute", "second:execute"]);
	});

	it("fails closed when a scope or policy is invalid", async () => {
		const invalidScope = createGameToolPolicyResources({
			scopeProvider: { resolve: () => ({ id: "", allowedTools: ["look"] }) },
		});
		await expect(invalidScope.visibility.isVisible(input, tool("look"), new AbortController().signal)).rejects.toThrow(
			"scope id",
		);

		const invalidPolicy = createGameToolPolicyResources({
			scopeProvider: { resolve: () => ({ id: "scope" }) },
			policies: [
				{
					evaluate: () => ({ effect: "other", reason: "bad" }) as never,
				},
			],
		});
		const next = vi.fn<() => Promise<GameToolResult>>();
		await expect(
			invalidPolicy.execution.execute(
				tool("look"),
				{ id: "call", name: "look", arguments: {} },
				executionContext(),
				next,
			),
		).rejects.toThrow("invalid effect");
		expect(next).not.toHaveBeenCalled();
	});

	it("propagates cancellation without running tools or policy audit", async () => {
		const controller = new AbortController();
		controller.abort(new Error("stopped"));
		const audit = new InMemoryGameToolPolicyAuditSink();
		const resources = createGameToolPolicyResources({
			scopeProvider: { resolve: () => ({ id: "scope" }) },
			audit,
		});
		const call: GameToolCall = { id: "call", name: "look", arguments: {} };
		const context = { ...executionContext(), signal: controller.signal };
		const next = vi.fn<() => Promise<GameToolResult>>();
		await expect(resources.execution.execute(tool("look"), call, context, next)).rejects.toThrow("stopped");
		expect(next).not.toHaveBeenCalled();
		expect(audit.read()).toEqual([]);
	});
});
