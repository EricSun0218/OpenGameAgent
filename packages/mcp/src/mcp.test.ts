import type {
	GameInput,
	GameSessionKey,
	GameTool,
	GameToolExecutionContext,
	JsonObject,
} from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import type { GameMcpCallResult, GameMcpConnection, GameMcpRemoteTool } from "./client.js";
import { GameMcpToolBridge } from "./mcp.js";

const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 1,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};

function input(type = "npc.chat"): GameInput {
	return { id: crypto.randomUUID(), type, session, moment: { tick: 1 }, content: [{ type: "text", text: "hi" }] };
}

function context(value: GameInput): GameToolExecutionContext {
	return { input: value, runId: "run", turn: 1, toolCallIndex: 0, signal: new AbortController().signal };
}

async function execute(tool: GameTool, value: GameInput, argumentsValue: JsonObject) {
	return tool.execute({ id: "call", name: tool.definition.name, arguments: argumentsValue }, context(value));
}

class FakeConnection implements GameMcpConnection {
	listCount = 0;
	callCount = 0;
	closeCount = 0;
	readonly calls: Array<{ name: string; argumentsValue: JsonObject }> = [];
	private readonly changed = new Set<() => void>();
	private readonly closed = new Set<() => void>();

	constructor(
		public tools: readonly GameMcpRemoteTool[],
		public result: GameMcpCallResult = { content: [{ type: "text", text: "ok" }] },
	) {}

	async listTools(signal?: AbortSignal): Promise<readonly GameMcpRemoteTool[]> {
		signal?.throwIfAborted();
		this.listCount += 1;
		return structuredClone(this.tools);
	}

	async callTool(name: string, argumentsValue: JsonObject, signal?: AbortSignal): Promise<GameMcpCallResult> {
		signal?.throwIfAborted();
		this.callCount += 1;
		this.calls.push({ name, argumentsValue: structuredClone(argumentsValue) });
		return structuredClone(this.result);
	}

	onToolsChanged(handler: () => void): () => void {
		this.changed.add(handler);
		return () => this.changed.delete(handler);
	}

	onClose(handler: () => void): () => void {
		this.closed.add(handler);
		return () => this.closed.delete(handler);
	}

	notifyChanged(): void {
		for (const handler of [...this.changed]) handler();
	}

	notifyClosed(): void {
		for (const handler of [...this.closed]) handler();
	}

	async close(): Promise<void> {
		this.closeCount += 1;
	}
}

const readTool: GameMcpRemoteTool = {
	name: "read_state",
	description: "Read a bounded world state snapshot",
	inputSchema: {
		type: "object",
		properties: { scope: { type: "string", enum: ["nearby", "owned"] } },
		required: ["scope"],
		additionalProperties: false,
	},
};

describe("GameMcpToolBridge", () => {
	it("keeps large catalogs out of the model until the proxy searches, describes, and calls", async () => {
		const connection = new FakeConnection([readTool]);
		const bridge = new GameMcpToolBridge({ servers: [{ id: "world", connect: async () => connection }] });
		const value = input();
		const tools = await bridge.provide(value, context(value).signal);
		expect(tools.map((tool) => tool.definition.name)).toEqual(["use_external_game_tool"]);
		expect(connection.listCount).toBe(0);

		const searched = await execute(tools[0] as GameTool, value, { action: "search", query: "world state" });
		expect(searched.content[0]).toMatchObject({ type: "json" });
		expect(JSON.stringify(searched)).toContain("read_state");
		expect(connection.listCount).toBe(1);

		const described = await execute(tools[0] as GameTool, value, {
			action: "describe",
			server: "world",
			tool: "read_state",
		});
		expect(JSON.stringify(described)).toContain("additionalProperties");

		const called = await execute(tools[0] as GameTool, value, {
			action: "call",
			server: "world",
			tool: "read_state",
			arguments: { scope: "nearby" },
		});
		expect(called.content).toEqual([{ type: "text", text: "ok" }]);
		expect(connection.calls).toEqual([{ name: "read_state", argumentsValue: { scope: "nearby" } }]);
		await bridge[Symbol.asyncDispose]();
	});

	it("filters unsupported schemas before direct tools reach a provider", async () => {
		const diagnostics: string[] = [];
		const connection = new FakeConnection([
			readTool,
			{ name: "bad", inputSchema: { $ref: "#/hidden" } },
			{ name: "also_bad", inputSchema: { anyOf: [{ type: "object", properties: { x: { unknown: true } } }] } },
		]);
		const bridge = new GameMcpToolBridge({
			servers: [{ id: "world", connect: async () => connection }],
			exposure: "direct",
			onDiagnostic: (event) => diagnostics.push(`${event.category}:${event.message}`),
		});
		const value = input();
		const tools = await bridge.provide(value, context(value).signal);
		expect(tools.map((tool) => tool.definition.name)).toEqual(["external_world_read_state"]);
		expect(diagnostics).toHaveLength(2);
		expect(diagnostics.every((message) => message.startsWith("schema:"))).toBe(true);
	});

	it("isolates input visibility and rejects a proxy captured for another input", async () => {
		const connection = new FakeConnection([readTool]);
		const bridge = new GameMcpToolBridge({
			servers: [
				{
					id: "world",
					connect: async () => connection,
					isVisible: (current) => current.type === "npc.chat",
				},
			],
		});
		const allowed = input("npc.chat");
		const denied = input("npc.image");
		const [proxy] = await bridge.provide(allowed, context(allowed).signal);
		expect(await bridge.provide(denied, context(denied).signal)).toEqual([]);
		await expect(execute(proxy as GameTool, denied, { action: "search", query: "state" })).rejects.toThrow(
			"scope expired",
		);
	});

	it("atomically refreshes list-changed generations and reconnects only after close", async () => {
		const first = new FakeConnection([readTool]);
		const second = new FakeConnection([{ ...readTool, name: "read_inventory" }]);
		const connections = [first, second];
		const connect = vi.fn(async () => connections.shift() as FakeConnection);
		const bridge = new GameMcpToolBridge({
			servers: [{ id: "world", connect }],
			exposure: "direct",
			refreshMilliseconds: 86_400_000,
		});
		const value = input();
		expect((await bridge.provide(value, context(value).signal))[0]?.definition.name).toContain("read_state");
		first.tools = [{ ...readTool, name: "read_location" }];
		first.notifyChanged();
		expect((await bridge.provide(value, context(value).signal))[0]?.definition.name).toContain("read_location");
		expect(connect).toHaveBeenCalledTimes(1);

		first.notifyClosed();
		expect((await bridge.provide(value, context(value).signal))[0]?.definition.name).toContain("read_inventory");
		expect(connect).toHaveBeenCalledTimes(2);
	});

	it("never publishes a catalog discovered by a connection that closed in flight", async () => {
		let releaseFirst: ((tools: readonly GameMcpRemoteTool[]) => void) | undefined;
		const first = new FakeConnection([readTool]);
		first.listTools = vi.fn(
			() =>
				new Promise<readonly GameMcpRemoteTool[]>((resolve) => {
					releaseFirst = resolve;
				}),
		);
		const second = new FakeConnection([{ ...readTool, name: "read_inventory" }]);
		const connections = [first, second];
		const bridge = new GameMcpToolBridge({
			servers: [{ id: "world", connect: async () => connections.shift() as FakeConnection }],
			exposure: "direct",
		});
		const value = input();
		const staleRead = bridge.provide(value, context(value).signal);
		await vi.waitFor(() => expect(releaseFirst).toBeTypeOf("function"));
		first.notifyClosed();
		releaseFirst?.([readTool]);
		expect(await staleRead).toEqual([]);

		const fresh = await bridge.provide(value, context(value).signal);
		expect(fresh.map((tool) => tool.definition.name)).toEqual(["external_world_read_inventory"]);
	});

	it("projects bounded text, structured data, and validated images without fetching resources", async () => {
		const pixel = Buffer.from([137, 80, 78, 71]).toString("base64");
		const connection = new FakeConnection([readTool], {
			content: [
				{ type: "text", text: "ready" },
				{ type: "image", mimeType: "image/png", data: pixel },
				{ type: "resource_link", name: "report", uri: "https://example.invalid/report" },
			],
			structuredContent: { count: 3 },
		});
		const bridge = new GameMcpToolBridge({ servers: [{ id: "world", connect: async () => connection }] });
		const value = input();
		const [proxy] = await bridge.provide(value, context(value).signal);
		const result = await execute(proxy as GameTool, value, {
			action: "call",
			server: "world",
			tool: "read_state",
			arguments: { scope: "nearby" },
		});
		expect(result.content).toEqual([
			{ type: "text", text: "ready" },
			{ type: "image", mimeType: "image/png", data: pixel },
			{ type: "text", text: "External resource: report (https://example.invalid/report)" },
		]);
		expect(result.details).toEqual({ count: 3 });
	});

	it("fails closed on oversized results and never retries a possibly-writing call", async () => {
		const connection = new FakeConnection([readTool], { content: [{ type: "text", text: "x".repeat(4_000) }] });
		const bridge = new GameMcpToolBridge({
			servers: [{ id: "world", connect: async () => connection }],
			maximumResultCharacters: 1_024,
		});
		const value = input();
		const [proxy] = await bridge.provide(value, context(value).signal);
		await expect(
			execute(proxy as GameTool, value, {
				action: "call",
				server: "world",
				tool: "read_state",
				arguments: { scope: "nearby" },
			}),
		).rejects.toThrow("configured limit");
		expect(connection.callCount).toBe(1);
	});

	it("closes every owned connection exactly once", async () => {
		const left = new FakeConnection([readTool]);
		const right = new FakeConnection([readTool]);
		const bridge = new GameMcpToolBridge({
			servers: [
				{ id: "left", connect: async () => left },
				{ id: "right", connect: async () => right },
			],
			exposure: "direct",
		});
		const value = input();
		await bridge.provide(value, context(value).signal);
		await bridge[Symbol.asyncDispose]();
		await bridge[Symbol.asyncDispose]();
		expect([left.closeCount, right.closeCount]).toEqual([1, 1]);
		await expect(bridge.provide(value, context(value).signal)).rejects.toThrow("disposed");
	});
});
