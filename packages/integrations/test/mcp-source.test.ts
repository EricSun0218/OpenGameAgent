import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import { UnauthorizedError } from "@modelcontextprotocol/sdk/client/auth.js";
import { StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import {
	CallToolRequestSchema,
	type CallToolResult,
	ErrorCode,
	ListToolsRequestSchema,
	McpError,
	type Tool,
} from "@modelcontextprotocol/sdk/types.js";
import { GameAgent } from "@opengameagent/agent";
import { RuntimeHealthMonitor } from "@opengameagent/health";
import { type CanonicalEvent, eventAudience, PROTOCOL_VERSION, type RunCoordinate } from "@opengameagent/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	allowAllTestTools,
	assistant,
	messageResponse,
	TEST_TOOL_LIMITS,
	textResponse,
} from "../../devtools/src/pi-fakes.js";
import {
	createGameMcpStdioTransportFactory,
	createGameMcpStreamableHttpTransportFactory,
	GameMcpError,
	type GameMcpLimits,
	GameMcpRequestSource,
	type GameMcpServerDefinition,
	type GameMcpToolSelection,
} from "../src/index.js";

const INPUT = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { instruction: "inspect the world" },
} as const;

const COORDINATE: RunCoordinate = {
	sessionId: "session",
	actorId: "actor",
	runId: "run",
	turn: "1",
};

const LIMITS: GameMcpLimits = {
	maxServers: 4,
	maxServerIdBytes: 64,
	maxServerDescriptionBytes: 256,
	maxListPages: 4,
	maxCursorBytes: 64,
	maxToolsPerServer: 16,
	maxDiagnostics: 8,
	maxToolNameBytes: 128,
	maxToolTitleBytes: 256,
	maxToolDescriptionBytes: 1_024,
	maxSchemaDepth: 16,
	maxSchemaBytes: 16_384,
	maxCatalogBytes: 65_536,
	maxSearchQueryBytes: 256,
	maxSearchResults: 8,
	maxCallArgumentDepth: 8,
	maxCallArgumentBytes: 4_096,
	maxResultContentBlocks: 8,
	maxResultTextBytes: 8_192,
	maxResultMediaBytes: 8_192,
	maxResultUriBytes: 1_024,
	maxResultMimeTypeBytes: 128,
	maxStructuredContentDepth: 8,
	maxStructuredContentBytes: 8_192,
	maxToolResultBytes: 32_768,
	maxConcurrentCallsPerServer: 2,
	requestIdleTimeoutMs: 2_000,
	requestTotalTimeoutMs: 4_000,
};

const lazyToolNames = {
	search: "mcp_search",
	describe: "mcp_describe",
	callParallel: "mcp_call_parallel",
	callSequential: "mcp_call_sequential",
} as const;

const servers: Server[] = [];

afterEach(async () => {
	for (const server of servers.splice(0)) await server.close().catch(() => undefined);
});

describe("GameMcpRequestSource", () => {
	it("projects real catalog readiness into the one-shot health monitor", async () => {
		const ready = await createFixture({ pages: [{ tools: [lookupTool()] }] });
		const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
		const missingCapability = new Server({ name: "missing-tools", version: "1" }, { capabilities: {} });
		await missingCapability.connect(serverTransport);
		servers.push(missingCapability);
		const source = createSource(
			[
				ready.definition,
				{
					serverId: "optional-missing",
					description: "No tools capability",
					requirement: "optional",
					createTransport: () => clientTransport,
				},
			],
			() => [],
		);
		const health = new RuntimeHealthMonitor({
			dependencies: source.healthDependencies(),
			limits: {
				maxDependencies: 2,
				maxDependencyIdBytes: 64,
				maxConcurrentInspections: 2,
				inspectionTimeoutMs: 2_000,
			},
			now: () => Date.parse("2026-08-30T00:00:00.000Z"),
		});

		await expect(health.inspect()).resolves.toEqual({
			checkedAt: "2026-08-30T00:00:00.000Z",
			status: "degraded",
			dependencies: [
				{
					id: "world",
					kind: "mcp",
					requirement: "required",
					state: "ready",
					observedAt: "2026-08-30T00:00:00.000Z",
				},
				{
					id: "optional-missing",
					kind: "mcp",
					requirement: "optional",
					state: "unavailable",
					observedAt: "2026-08-30T00:00:00.000Z",
					reason: "capability-missing",
				},
			],
		});
		await source.close();
	});

	it("maps catalog failure classes and preserves caller cancellation", async () => {
		const unavailable = createSource(
			[
				{
					serverId: "offline",
					description: "Unavailable transport",
					requirement: "required",
					createTransport: () => {
						throw new Error("private transport detail");
					},
				},
			],
			() => [],
		);
		await expect(inspectOnly(unavailable, freshSignal())).resolves.toEqual({
			state: "unavailable",
			reason: "transport",
		});
		await unavailable.close();

		for (const [serverId, error, reason] of [
			["authentication", new UnauthorizedError("private auth detail"), "authentication-required"],
			["permission", new StreamableHTTPError(403, "private permission detail"), "permission-denied"],
		] as const) {
			const denied = createSource(
				[
					{
						serverId,
						description: "Known SDK failure",
						requirement: "required",
						createTransport: () => {
							throw error;
						},
					},
				],
				() => [],
			);
			await expect(inspectOnly(denied, freshSignal())).resolves.toEqual({ state: "unavailable", reason });
			await denied.close();
		}

		const [timeoutClientTransport, timeoutServerTransport] = InMemoryTransport.createLinkedPair();
		const timeoutServer = new Server({ name: "timed-out-tools", version: "1" }, { capabilities: { tools: {} } });
		timeoutServer.setRequestHandler(ListToolsRequestSchema, () => {
			throw new McpError(ErrorCode.RequestTimeout, "private timeout detail");
		});
		await timeoutServer.connect(timeoutServerTransport);
		servers.push(timeoutServer);
		const timedOut = createSource(
			[
				{
					serverId: "timed-out",
					description: "Timed-out catalog",
					requirement: "required",
					createTransport: () => timeoutClientTransport,
				},
			],
			() => [],
		);
		await expect(inspectOnly(timedOut, freshSignal())).resolves.toEqual({ state: "unavailable", reason: "timeout" });
		await timedOut.close();

		let markListStarted: (() => void) | undefined;
		const listStarted = new Promise<void>((resolve) => {
			markListStarted = resolve;
		});
		const [closingClientTransport, closingServerTransport] = InMemoryTransport.createLinkedPair();
		const closingServer = new Server({ name: "closing-tools", version: "1" }, { capabilities: { tools: {} } });
		closingServer.setRequestHandler(ListToolsRequestSchema, async () => {
			markListStarted?.();
			return await new Promise<{ tools: Tool[] }>(() => undefined);
		});
		await closingServer.connect(closingServerTransport);
		servers.push(closingServer);
		const connectionClosed = createSource(
			[
				{
					serverId: "connection-closed",
					description: "Catalog connection closes in flight",
					requirement: "required",
					createTransport: () => closingClientTransport,
				},
			],
			() => [],
		);
		const closingCatalog = inspectOnly(connectionClosed, freshSignal());
		await listStarted;
		await closingServer.close();
		await expect(closingCatalog).resolves.toEqual({ state: "unavailable", reason: "transport" });
		await connectionClosed.close();

		const responseBound = createSource(
			[
				{
					serverId: "response-bound",
					description: "Bounded HTTP catalog",
					requirement: "required",
					createTransport: createGameMcpStreamableHttpTransportFactory({
						url: "https://resource.example/mcp",
						fetch: async () => new Response("{}", { headers: { "Content-Type": "application/json" } }),
						maxHttpResponseBytes: 1,
						maxSseEventBytes: 1,
						reconnection: { initialDelayMs: 1, maxDelayMs: 1, delayGrowFactor: 1, maxAttempts: 0 },
					}),
				},
			],
			() => [],
		);
		await expect(inspectOnly(responseBound, freshSignal())).resolves.toEqual({
			state: "unavailable",
			reason: "capacity",
		});
		await responseBound.close();

		const ambiguousRejection = createSource(
			[
				{
					serverId: "ambiguous-rejection",
					description: "Ambiguous transport policy",
					requirement: "optional",
					createTransport: () => {
						throw new GameMcpError("transport_rejected");
					},
				},
			],
			() => [],
		);
		await expect(inspectOnly(ambiguousRejection, freshSignal())).resolves.toEqual({
			state: "unavailable",
			reason: "unknown",
		});
		await ambiguousRejection.close();

		const oversizedFixture = await createFixture({
			pages: [
				{
					tools: [
						{ name: "one", inputSchema: objectSchema() },
						{ name: "two", inputSchema: objectSchema() },
					],
				},
			],
		});
		const oversized = createSource([oversizedFixture.definition], () => [], {
			limits: { ...LIMITS, maxToolsPerServer: 1 },
		});
		await expect(inspectOnly(oversized, freshSignal())).resolves.toEqual({
			state: "unavailable",
			reason: "capacity",
		});
		await oversized.close();

		const byteBoundFixture = await createFixture({ pages: [{ tools: [lookupTool()] }] });
		const byteBound = createSource([byteBoundFixture.definition], () => [], {
			limits: { ...LIMITS, maxCatalogBytes: 1 },
		});
		await expect(inspectOnly(byteBound, freshSignal())).resolves.toEqual({
			state: "unavailable",
			reason: "capacity",
		});
		await byteBound.close();

		const schemaBoundFixture = await createFixture({ pages: [{ tools: [lookupTool()] }] });
		const schemaBound = createSource([schemaBoundFixture.definition], () => [], {
			limits: { ...LIMITS, maxSchemaBytes: 1 },
		});
		await expect(inspectOnly(schemaBound, freshSignal())).resolves.toEqual({
			state: "unavailable",
			reason: "capacity",
		});
		await schemaBound.close();

		const invalidFixture = await createFixture({
			pages: [
				{
					tools: [
						{
							name: "invalid",
							inputSchema: {
								type: "object",
								properties: { value: { type: "not-a-json-schema-type" } },
							} as unknown as Tool["inputSchema"],
						},
					],
				},
			],
		});
		const invalid = createSource([invalidFixture.definition], () => []);
		await expect(inspectOnly(invalid, freshSignal())).resolves.toEqual({
			state: "unavailable",
			reason: "dependency-failure",
		});
		await invalid.close();

		let transportCalls = 0;
		let inspectionSignal: AbortSignal | undefined;
		let markStarted: (() => void) | undefined;
		const started = new Promise<void>((resolve) => {
			markStarted = resolve;
		});
		const cancelled = createSource(
			[
				{
					serverId: "cancelled",
					description: "Cancelled inspection",
					requirement: "optional",
					createTransport: async (signal) => {
						transportCalls += 1;
						inspectionSignal = signal;
						markStarted?.();
						return await new Promise<InMemoryTransport>((_resolve, reject) => {
							signal.addEventListener("abort", () => reject(signal.reason), { once: true });
						});
					},
				},
			],
			() => [],
		);
		const controller = new AbortController();
		const reason = new DOMException("Caller stopped the inspection.", "AbortError");
		const pending = inspectOnly(cancelled, controller.signal);
		await started;
		controller.abort(reason);
		await expect(pending).rejects.toBe(reason);
		expect(transportCalls).toBe(1);
		expect(inspectionSignal?.aborted).toBe(true);
		await cancelled.close();

		let closeSignal: AbortSignal | undefined;
		let markCloseProbeStarted: (() => void) | undefined;
		const closeProbeStarted = new Promise<void>((resolve) => {
			markCloseProbeStarted = resolve;
		});
		const closing = createSource(
			[
				{
					serverId: "closing",
					description: "Closing inspection",
					requirement: "required",
					createTransport: async (signal) => {
						closeSignal = signal;
						markCloseProbeStarted?.();
						return await new Promise<InMemoryTransport>((_resolve, reject) => {
							signal.addEventListener("abort", () => reject(signal.reason), { once: true });
						});
					},
				},
			],
			() => [],
		);
		const closingInspection = inspectOnly(closing, freshSignal());
		await closeProbeStarted;
		await closing.close();
		await expect(closingInspection).resolves.toEqual({ state: "unavailable", reason: "transport" });
		expect(closeSignal?.aborted).toBe(true);
	});

	it("runs paginated lazy discovery and a schema-validated call through the real Pi loop", async () => {
		const calls: unknown[] = [];
		const authorizations: unknown[] = [];
		const fixture = await createFixture({
			pages: [
				{
					tools: [lookupTool()],
					nextCursor: "page-2",
				},
				{
					tools: [
						{
							name: "background-task",
							inputSchema: { type: "object", properties: {}, additionalProperties: false },
							execution: { taskSupport: "required" },
						},
					],
				},
			],
			call: (request) => {
				calls.push(structuredClone(request.params));
				return {
					content: [{ type: "text", text: "found the village" }],
					structuredContent: { location: "village" },
				};
			},
		});
		const source = createSource([fixture.definition], () => [lazySelection("lookup")], {
			authorizeCall: (request) => {
				expect(Object.isFrozen(request.arguments)).toBe(true);
				authorizations.push(request);
			},
		});
		const contexts: Context[] = [];
		let providerRequest = 0;
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			providerRequest += 1;
			const revision = catalogRevision(context);
			switch (providerRequest) {
				case 1:
					return toolResponse("search", lazyToolNames.search, {
						query: "village lookup",
						maxResults: 1,
						catalogRevision: revision,
					});
				case 2:
					return toolResponse("describe", lazyToolNames.describe, {
						serverId: "world",
						name: "lookup",
						catalogRevision: revision,
					});
				case 3: {
					const description = context.messages.find(
						(message) => message.role === "toolResult" && message.toolCallId === "describe",
					);
					const block = description?.content[0];
					if (typeof block !== "object" || block.type !== "text") throw new Error("Missing MCP description.");
					return toolResponse("call", JSON.parse(block.text).callTool, {
						serverId: "world",
						name: "lookup",
						arguments: { place: "village" },
						catalogRevision: revision,
					});
				}
				default:
					return textResponse("done");
			}
		};
		const agent = createAgent(modelStream, source);

		await expect(agent.start(INPUT, "run").completion).resolves.toMatchObject({ status: "completed" });

		expect(providerRequest).toBe(4);
		expect(calls).toEqual([{ name: "lookup", arguments: { place: "village" } }]);
		expect(authorizations).toMatchObject([
			{ serverId: "world", name: "lookup", toolCallId: "call", arguments: { place: "village" } },
		]);
		expect(contexts[0]?.tools?.map((tool) => tool.name)).toEqual([
			lazyToolNames.search,
			lazyToolNames.describe,
			lazyToolNames.callSequential,
		]);
		expect(JSON.stringify(contexts)).not.toContain("background-task");
		expect(JSON.stringify(contexts[1]?.messages)).toContain("lookup");
		expect(JSON.stringify(contexts[2]?.messages)).toContain("inputSchema");
		expect(JSON.stringify(contexts[3]?.messages)).toContain("found the village");

		const inspection = await source.inspect(freshSignal());
		expect(inspection.tools.map((tool) => tool.name)).toEqual(["lookup", "background-task"]);
		expect(inspection.diagnostics).toContainEqual({ serverId: "world", code: "task_required_tool_ignored" });
		await source.close();
	});

	it.each([false, true])(
		"runs lazy parallel calls concurrently with an unused sequential selection: %s",
		async (includeSequential) => {
			const started: string[] = [];
			const gate = Promise.withResolvers<void>();
			const fixture = await createFixture({
				pages: [{ tools: ["first", "second", "write"].map((name) => ({ name, inputSchema: objectSchema() })) }],
				call: async (request) => {
					started.push(request.params.name);
					await gate.promise;
					return { content: [{ type: "text", text: request.params.name }] };
				},
			});
			const source = createSource([fixture.definition], () => [
				...["first", "second"].map((name) => ({ ...lazySelection(name), executionMode: "parallel" as const })),
				...(includeSequential ? [lazySelection("write")] : []),
			]);
			let requests = 0;
			const agent = createAgent((_model, context) => {
				requests += 1;
				if (requests > 1) {
					expect(context.messages.filter((message) => message.role === "toolResult")).toMatchObject([
						{ toolCallId: "first", isError: false, content: [{ type: "text", text: "first" }] },
						{ toolCallId: "second", isError: false, content: [{ type: "text", text: "second" }] },
					]);
					return textResponse("done");
				}
				return messageResponse(
					assistant(
						["first", "second"].map((name) => ({
							type: "toolCall" as const,
							id: name,
							name: lazyToolNames.callParallel,
							arguments: { serverId: "world", name, arguments: {}, catalogRevision: catalogRevision(context) },
						})),
						"toolUse",
					),
				);
			}, source);
			const run = agent.start(INPUT, "run");
			try {
				await vi.waitFor(() => expect(started).toEqual(["first", "second"]));
			} finally {
				gate.resolve();
				await expect(run.completion).resolves.toMatchObject({ status: "completed" });
				await source.close();
			}
			expect(requests).toBe(2);
		},
	);

	it.each([
		["sequential", "sequential"],
		["parallel", "sequential"],
		["sequential", "parallel"],
	] as const)("preserves Pi batch order for lazy %s then %s calls", async (firstMode, secondMode) => {
		const execution: string[] = [];
		const fixture = await createFixture({
			pages: [{ tools: ["first", "second"].map((name) => ({ name, inputSchema: objectSchema() })) }],
			call: async (request) => {
				execution.push(`start:${request.params.name}`);
				await new Promise<void>((resolve) => setImmediate(resolve));
				execution.push(`end:${request.params.name}`);
				return { content: [{ type: "text", text: request.params.name }] };
			},
		});
		const source = createSource([fixture.definition], () => [
			{ ...lazySelection("first"), executionMode: firstMode },
			{ ...lazySelection("second"), executionMode: secondMode },
		]);
		let requests = 0;
		const agent = createAgent((_model, context) => {
			if (++requests > 1) return textResponse("done");
			return messageResponse(
				assistant(
					[
						{ name: "first", mode: firstMode },
						{ name: "second", mode: secondMode },
					].map(({ name, mode }) => ({
						type: "toolCall" as const,
						id: name,
						name: mode === "parallel" ? lazyToolNames.callParallel : lazyToolNames.callSequential,
						arguments: { serverId: "world", name, arguments: {}, catalogRevision: catalogRevision(context) },
					})),
					"toolUse",
				),
			);
		}, source);
		try {
			await expect(agent.start(INPUT, "run").completion).resolves.toMatchObject({ status: "completed" });
			expect(execution).toEqual(["start:first", "end:first", "start:second", "end:second"]);
		} finally {
			await source.close();
		}
	});

	it.each([
		["parallel", "mcp_call_parallel", "mcp_call_sequential"],
		["sequential", "mcp_call_sequential", "mcp_call_parallel"],
	] as const)(
		"describes the %s endpoint and rejects the other endpoint before dispatch",
		async (mode, expectedCallTool, wrongCallTool) => {
			let authorizations = 0;
			let calls = 0;
			const fixture = await createFixture({
				pages: [{ tools: ["read", "write"].map((name) => ({ name, inputSchema: objectSchema() })) }],
				call: () => {
					calls += 1;
					return { content: [{ type: "text", text: "unexpected" }] };
				},
			});
			const source = createSource(
				[fixture.definition],
				() => [{ ...lazySelection("read"), executionMode: "parallel" }, lazySelection("write")],
				{
					authorizeCall: () => {
						authorizations += 1;
					},
				},
			);
			try {
				const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);
				const locator = {
					serverId: "world",
					name: mode === "parallel" ? "read" : "write",
					catalogRevision: contribution.context?.references[0]?.revision,
				};
				const description = await requiredTool(contribution.tools, lazyToolNames.describe).execute(
					"describe",
					locator,
					freshSignal(),
				);
				const block = description.content[0];
				if (block?.type !== "text") throw new Error("Missing MCP description.");
				expect(JSON.parse(block.text)).toMatchObject({ executionMode: mode, callTool: expectedCallTool });
				await expect(
					requiredTool(contribution.tools, wrongCallTool).execute(
						"wrong-mode",
						{
							...locator,
							arguments: {},
						},
						freshSignal(),
					),
				).rejects.toMatchObject({ code: "tool_unavailable" });
				expect(authorizations).toBe(0);
				expect(calls).toBe(0);
			} finally {
				await source.close();
			}
		},
	);

	it.each([undefined, "bad tool", "mcp_call_parallel", "x".repeat(LIMITS.maxToolNameBytes + 1)])(
		"rejects a missing, invalid, duplicate or oversized sequential endpoint name: %s",
		async (callSequential) => {
			const fixture = await createFixture({ pages: [{ tools: [lookupTool()] }] });
			expect(
				() =>
					new GameMcpRequestSource({
						sourceId: "mcp",
						servers: [fixture.definition],
						limits: LIMITS,
						lazyToolNames: { ...lazyToolNames, callSequential: callSequential as string },
						select: () => [],
						authorizeCall: () => undefined,
					}),
			).toThrowError(expect.objectContaining({ code: "invalid_configuration" }));
		},
	);

	it("changes the lazy catalog revision when host execution policy changes", async () => {
		let mode: "parallel" | "sequential" = "parallel";
		const fixture = await createFixture({ pages: [{ tools: [lookupTool()] }] });
		const source = createSource([fixture.definition], () => [{ ...lazySelection("lookup"), executionMode: mode }]);
		try {
			const previous = await source.collect(INPUT, freshSignal(), COORDINATE, []);
			expect(previous.tools?.map((tool) => tool.name)).toEqual(["mcp_search", "mcp_describe", "mcp_call_parallel"]);
			mode = "sequential";
			const current = await source.collect(INPUT, freshSignal(), COORDINATE, []);
			expect(current.tools?.map((tool) => tool.name)).toEqual(["mcp_search", "mcp_describe", "mcp_call_sequential"]);
			expect(current.context?.references[0]?.revision).not.toBe(previous.context?.references[0]?.revision);
			await expect(
				requiredTool(current.tools, lazyToolNames.callSequential).execute(
					"old-policy",
					{
						serverId: "world",
						name: "lookup",
						arguments: { place: "village" },
						catalogRevision: previous.context?.references[0]?.revision,
					},
					freshSignal(),
				),
			).rejects.toMatchObject({ code: "catalog_changed" });
		} finally {
			await source.close();
		}
	});

	it("fences a pinned Tool when the server catalog changes before dispatch", async () => {
		let callCount = 0;
		const fixture = await createFixture({
			pages: [{ tools: [lookupTool()] }],
			call: () => {
				callCount += 1;
				return { content: [{ type: "text", text: "unexpected" }] };
			},
		});
		const source = createSource([fixture.definition], () => [directSelection("lookup", "world_lookup")]);
		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		const direct = requiredTool(contribution.tools, "world_lookup");

		await fixture.server.sendToolListChanged();
		await expect(direct.execute("stale", { place: "village" }, freshSignal())).rejects.toMatchObject({
			code: "catalog_changed",
		});
		expect(callCount).toBe(0);
		expect((await source.inspect(freshSignal())).diagnostics).toContainEqual({
			serverId: "world",
			code: "tool_list_changed",
		});
		await source.close();
	});

	it("keeps the newest host-bounded diagnostics in observation order", async () => {
		const fixture = await createFixture({ pages: [{ tools: [lookupTool()] }] });
		const observed: unknown[] = [];
		const source = createSource([fixture.definition], () => [], {
			limits: { ...LIMITS, maxDiagnostics: 2 },
			onDiagnostic: (diagnostic) => observed.push(diagnostic),
		});
		await source.inspect(freshSignal());

		await fixture.server.sendToolListChanged();
		await fixture.server.sendToolListChanged();
		await fixture.server.sendToolListChanged();

		expect(observed).toHaveLength(3);
		expect((await source.inspect(freshSignal())).diagnostics).toEqual([
			{ serverId: "world", code: "tool_list_changed" },
			{ serverId: "world", code: "tool_list_changed" },
		]);
		await source.close();
	});

	it("omits optional failures, fails required failures, and rejects Tool-name collisions", async () => {
		const diagnostics: unknown[] = [];
		const unavailable = (requirement: "required" | "optional"): GameMcpServerDefinition => ({
			serverId: `${requirement}-server`,
			description: "unavailable",
			requirement,
			createTransport: () => {
				throw new Error("private transport detail");
			},
		});
		const optional = createSource([unavailable("optional")], () => [], {
			onDiagnostic: (diagnostic) => diagnostics.push(diagnostic),
		});
		await expect(optional.collect(INPUT, freshSignal(), COORDINATE, [])).resolves.toEqual({});
		expect(diagnostics).toEqual([{ serverId: "optional-server", code: "optional_server_unavailable" }]);
		await optional.close();

		const required = createSource([unavailable("required")], () => []);
		await expect(required.collect(INPUT, freshSignal(), COORDINATE, [])).rejects.toEqual(
			expect.objectContaining({ name: "GameMcpError", code: "server_unavailable", message: "server_unavailable" }),
		);
		await required.close();

		const fixture = await createFixture({ pages: [{ tools: [lookupTool()] }] });
		const colliding = createSource([fixture.definition], () => [directSelection("lookup", "inspect")]);
		await expect(colliding.collect(INPUT, freshSignal(), COORDINATE, ["inspect"])).rejects.toMatchObject({
			code: "invalid_selection",
		});
		await colliding.close();
	});

	it("authorizes every dispatch and preserves bounded server-reported failures", async () => {
		let authorized = false;
		const fixture = await createFixture({
			pages: [{ tools: [{ name: "fail", inputSchema: objectSchema() }] }],
			call: () => ({ content: [{ type: "text", text: "retry with another target" }], isError: true }),
		});
		const source = createSource([fixture.definition], () => [directSelection("fail", "world_fail")], {
			authorizeCall: () => {
				authorized = true;
			},
		});
		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		await expect(requiredTool(contribution.tools, "world_fail").execute("call", {}, freshSignal())).rejects.toEqual(
			expect.objectContaining({
				name: "GameMcpToolReportedError",
				code: "mcp_tool_reported_error",
				message: "mcp_tool_reported_error",
			}),
		);
		expect(authorized).toBe(true);
		await source.close();

		const deniedFixture = await createFixture({
			pages: [{ tools: [{ name: "fail", inputSchema: objectSchema() }] }],
		});
		const denied = createSource([deniedFixture.definition], () => [directSelection("fail", "world_fail")], {
			authorizeCall: () => {
				throw new Error("policy detail");
			},
		});
		const deniedContribution = await denied.collect(INPUT, freshSignal(), COORDINATE, []);
		await expect(
			requiredTool(deniedContribution.tools, "world_fail").execute("denied", {}, freshSignal()),
		).rejects.toMatchObject({
			code: "tool_not_authorized",
		});
		await denied.close();
	});

	it("preserves MCP error content through Pi and does not fabricate empty success content", async () => {
		const errorFixture = await createFixture({
			pages: [{ tools: [{ name: "fail", inputSchema: objectSchema() }] }],
			call: () => ({
				content: [{ type: "text", text: "retry with another target" }],
				structuredContent: { hint: "choose a visible target" },
				isError: true,
			}),
		});
		const errorSource = createSource([errorFixture.definition], () => [directSelection("fail", "world_fail")]);
		const errorContexts: Context[] = [];
		let errorRequests = 0;
		const errorAgent = createAgent((_model, context) => {
			errorContexts.push(copyContext(context));
			errorRequests += 1;
			return errorRequests === 1 ? toolResponse("reported-error", "world_fail", {}) : textResponse("recovered");
		}, errorSource);

		await expect(errorAgent.start(INPUT, "reported-error-run").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(errorContexts[1]?.messages.find((message) => message.role === "toolResult")).toMatchObject({
			isError: true,
			content: [
				{ type: "text", text: "retry with another target" },
				{ type: "text", text: '<mcp_structured_content>{"hint":"choose a visible target"}</mcp_structured_content>' },
			],
			details: {
				kind: "mcp",
				contentKinds: ["text", "structured"],
				structuredContent: { hint: "choose a visible target" },
			},
		});
		await errorSource.close();

		const emptyFixture = await createFixture({
			pages: [{ tools: [{ name: "empty", inputSchema: objectSchema() }] }],
			call: () => ({ content: [] }),
		});
		const emptySource = createSource([emptyFixture.definition], () => [directSelection("empty", "world_empty")]);
		const emptyContexts: Context[] = [];
		let emptyRequests = 0;
		const emptyAgent = createAgent((_model, context) => {
			emptyContexts.push(copyContext(context));
			emptyRequests += 1;
			return emptyRequests === 1 ? toolResponse("empty", "world_empty", {}) : textResponse("done");
		}, emptySource);

		await expect(emptyAgent.start(INPUT, "empty-run").completion).resolves.toMatchObject({ status: "completed" });
		expect(emptyContexts[1]?.messages.find((message) => message.role === "toolResult")).toMatchObject({
			isError: false,
			content: [],
			details: { kind: "mcp", contentKinds: [] },
		});
		await emptySource.close();
	});

	it("fails fast at the configured per-server concurrency boundary", async () => {
		let release: (() => void) | undefined;
		const blocked = new Promise<void>((resolve) => {
			release = resolve;
		});
		const fixture = await createFixture({
			pages: [{ tools: [{ name: "wait", inputSchema: objectSchema() }] }],
			call: async () => {
				await blocked;
				return { content: [{ type: "text", text: "released" }] };
			},
		});
		const source = createSource([fixture.definition], () => [directSelection("wait", "world_wait")], {
			limits: { ...LIMITS, maxConcurrentCallsPerServer: 1 },
		});
		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		const wait = requiredTool(contribution.tools, "world_wait");
		const first = wait.execute("first", {}, freshSignal());
		await Promise.resolve();
		await expect(wait.execute("second", {}, freshSignal())).rejects.toMatchObject({
			code: "tool_call_limit_exceeded",
		});
		release?.();
		await expect(first).resolves.toMatchObject({ content: [{ type: "text", text: "released" }] });
		await source.close();
	});

	it("keeps independent output validation for Tools from earlier catalog pages", async () => {
		const fixture = await createFixture({
			pages: [
				{ tools: [lookupTool()], nextCursor: "page-2" },
				{ tools: [{ name: "other", inputSchema: objectSchema() }] },
			],
			call: () => ({ content: [{ type: "text", text: "missing structured output" }] }),
		});
		const source = createSource([fixture.definition], () => [directSelection("lookup", "world_lookup")]);
		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);

		await expect(
			requiredTool(contribution.tools, "world_lookup").execute("call", { place: "village" }, freshSignal()),
		).rejects.toMatchObject({ code: "tool_unavailable" });
		await source.close();

		const errorFixture = await createFixture({
			pages: [
				{ tools: [lookupTool()], nextCursor: "page-2" },
				{ tools: [{ name: "other", inputSchema: objectSchema() }] },
			],
			call: () => ({
				content: [{ type: "text", text: "bad structured error" }],
				structuredContent: { location: 1 },
				isError: true,
			}),
		});
		const errorSource = createSource([errorFixture.definition], () => [directSelection("lookup", "world_lookup")]);
		const errorContribution = await errorSource.collect(INPUT, freshSignal(), COORDINATE, []);
		await expect(
			requiredTool(errorContribution.tools, "world_lookup").execute("error", { place: "village" }, freshSignal()),
		).rejects.toMatchObject({ code: "tool_unavailable" });
		await errorSource.close();

		const schemaErrorFixture = await createFixture({
			pages: [
				{ tools: [lookupTool()], nextCursor: "page-2" },
				{ tools: [{ name: "other", inputSchema: objectSchema() }] },
			],
			call: () => ({ content: [{ type: "text", text: "choose another place" }], isError: true }),
		});
		const schemaErrorSource = createSource([schemaErrorFixture.definition], () => [
			directSelection("lookup", "world_lookup"),
		]);
		const schemaErrorContribution = await schemaErrorSource.collect(INPUT, freshSignal(), COORDINATE, []);
		await expect(
			requiredTool(schemaErrorContribution.tools, "world_lookup").execute(
				"schema-error",
				{ place: "village" },
				freshSignal(),
			),
		).rejects.toMatchObject({ code: "mcp_tool_reported_error" });
		await schemaErrorSource.close();
	});

	it("rejects incomplete catalogs and invalid schemas without publishing partial Tools", async () => {
		const looping = await createFixture({ pages: [{ tools: [], nextCursor: "page-1" }] });
		const loopingSource = createSource([looping.definition], () => []);
		await expect(loopingSource.collect(INPUT, freshSignal(), COORDINATE, [])).rejects.toMatchObject({
			code: "catalog_incomplete",
		});
		await loopingSource.close();

		const oversized = await createFixture({
			pages: [
				{
					tools: [
						{ name: "one", inputSchema: objectSchema() },
						{ name: "two", inputSchema: objectSchema() },
					],
				},
			],
		});
		const oversizedSource = createSource([oversized.definition], () => [], {
			limits: { ...LIMITS, maxToolsPerServer: 1 },
		});
		await expect(oversizedSource.collect(INPUT, freshSignal(), COORDINATE, [])).rejects.toMatchObject({
			code: "catalog_limit_exceeded",
		});
		await oversizedSource.close();

		const invalid = await createFixture({
			pages: [
				{
					tools: [
						{
							name: "invalid",
							inputSchema: {
								type: "object",
								properties: { value: { type: "not-a-json-schema-type" } },
							} as unknown as Tool["inputSchema"],
						},
					],
				},
			],
		});
		const invalidSource = createSource([invalid.definition], () => []);
		await expect(invalidSource.collect(INPUT, freshSignal(), COORDINATE, [])).rejects.toMatchObject({
			code: "invalid_tool_schema",
		});
		await invalidSource.close();
	});

	it("validates arguments before authorization and projects supported MCP content within host budgets", async () => {
		let mode: "content" | "unpadded-base64" | "invalid-base64" | "invalid-mime" = "content";
		let authorizations = 0;
		const fixture = await createFixture({
			pages: [
				{
					tools: [
						{
							name: "render",
							inputSchema: {
								type: "object",
								properties: { target: { type: "string" } },
								required: ["target"],
								additionalProperties: false,
							},
						},
					],
				},
			],
			call: () => {
				if (mode === "unpadded-base64") {
					return { content: [{ type: "image", data: "aGk", mimeType: "image/png" }] };
				}
				if (mode === "invalid-base64") {
					return { content: [{ type: "image", data: "*", mimeType: "image/png" }] };
				}
				if (mode === "invalid-mime") {
					return {
						content: [{ type: "image", data: Buffer.from("image").toString("base64"), mimeType: "audio/wav" }],
					};
				}
				return {
					content: [
						{ type: "image", data: Buffer.from("image").toString("base64"), mimeType: "image/png" },
						{ type: "audio", data: Buffer.from("audio").toString("base64"), mimeType: "audio/wav" },
						{ type: "resource_link", uri: "game://village", name: "Village" },
						{ type: "resource", resource: { uri: "game://note", text: "world note", mimeType: "text/plain" } },
						{
							type: "resource",
							resource: {
								uri: "game://save",
								blob: Buffer.from("save").toString("base64"),
								mimeType: "application/octet-stream",
							},
						},
					],
				};
			},
		});
		const source = createSource([fixture.definition], () => [directSelection("render", "world_render")], {
			authorizeCall: () => {
				authorizations += 1;
			},
		});
		let invalidRequests = 0;
		const invalidAgent = createAgent(() => {
			invalidRequests += 1;
			return invalidRequests === 1 ? toolResponse("invalid", "world_render", {}) : textResponse("done");
		}, source);
		await expect(invalidAgent.start(INPUT, "invalid-direct-run").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(authorizations).toBe(0);

		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		const render = requiredTool(contribution.tools, "world_render");
		const projected = await render.execute("content", { target: "village" }, freshSignal());
		expect(projected.content.map((entry) => entry.type)).toEqual(["image", "text", "text", "text", "text"]);
		expect(projected.details).toMatchObject({
			kind: "mcp",
			contentKinds: ["image", "audio-metadata", "resource-link", "resource-text", "resource-blob-metadata"],
		});
		expect(authorizations).toBe(1);

		mode = "unpadded-base64";
		await expect(render.execute("unpadded-media", { target: "village" }, freshSignal())).resolves.toMatchObject({
			content: [{ type: "image", data: "aGk", mimeType: "image/png" }],
		});
		mode = "invalid-base64";
		await expect(render.execute("invalid-media", { target: "village" }, freshSignal())).rejects.toMatchObject({
			code: "tool_unavailable",
		});
		mode = "invalid-mime";
		await expect(render.execute("invalid-mime", { target: "village" }, freshSignal())).rejects.toMatchObject({
			code: "unsupported_result_content",
		});
		await source.close();

		const whitespaceFixture = await createFixture({
			pages: [
				{
					tools: [
						{
							name: "render",
							inputSchema: {
								type: "object",
								properties: { target: { type: "string" } },
								required: ["target"],
								additionalProperties: false,
							},
						},
					],
				},
			],
			call: () => ({ content: [{ type: "image", data: "a Gk", mimeType: "image/png" }] }),
		});
		const whitespaceSource = createSource(
			[whitespaceFixture.definition],
			() => [directSelection("render", "world_render")],
			{ limits: { ...LIMITS, maxResultMediaBytes: 2 } },
		);
		const whitespaceContribution = await whitespaceSource.collect(INPUT, freshSignal(), COORDINATE, []);
		await expect(
			requiredTool(whitespaceContribution.tools, "world_render").execute(
				"whitespace-media",
				{ target: "village" },
				freshSignal(),
			),
		).resolves.toMatchObject({ content: [{ type: "image", data: "a Gk", mimeType: "image/png" }] });
		await whitespaceSource.close();
	});

	it("validates lazy remote arguments before authorization", async () => {
		let authorizations = 0;
		const fixture = await createFixture({ pages: [{ tools: [lookupTool()] }] });
		const source = createSource([fixture.definition], () => [lazySelection("lookup")], {
			authorizeCall: () => {
				authorizations += 1;
			},
		});
		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		const call = requiredTool(contribution.tools, lazyToolNames.callSequential);
		const revision = contribution.context?.references[0]?.revision;
		expect(revision).toMatch(/^[0-9a-f]{64}$/u);
		if (revision === undefined) throw new Error("missing test catalog revision");

		await expect(
			call.execute(
				"invalid-lazy",
				{ serverId: "world", name: "lookup", arguments: {}, catalogRevision: revision },
				freshSignal(),
			),
		).rejects.toMatchObject({ code: "invalid_tool_arguments" });
		expect(authorizations).toBe(0);
		await source.close();
	});

	it("maps cancellation and timeout failures without leaking transport details", async () => {
		const fixture = await createFixture({
			pages: [{ tools: [{ name: "wait", inputSchema: objectSchema() }] }],
			call: async () => await new Promise<CallToolResult>(() => undefined),
		});
		const source = createSource([fixture.definition], () => [directSelection("wait", "world_wait")], {
			limits: { ...LIMITS, requestIdleTimeoutMs: 50, requestTotalTimeoutMs: 50 },
		});
		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		const wait = requiredTool(contribution.tools, "world_wait");

		await expect(wait.execute("timeout", {}, freshSignal())).rejects.toEqual(
			expect.objectContaining({ name: "GameMcpError", code: "server_timeout", message: "server_timeout" }),
		);
		const controller = new AbortController();
		const reason = new Error("host cancelled");
		const cancelled = wait.execute("cancel", {}, controller.signal);
		controller.abort(reason);
		await expect(cancelled).rejects.toBe(reason);
		await source.close();
	});

	it("reconnects with a new generation and never replays an interrupted Tool call", async () => {
		const instances: Server[] = [];
		let calls = 0;
		const definition: GameMcpServerDefinition = {
			serverId: "world",
			description: "Reconnectable world",
			requirement: "required",
			createTransport: async () => {
				const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
				const server = new Server(
					{ name: "reconnect-world", version: "1" },
					{ capabilities: { tools: { listChanged: true } } },
				);
				server.setRequestHandler(ListToolsRequestSchema, () => ({
					tools: [{ name: "act", inputSchema: objectSchema() }],
				}));
				server.setRequestHandler(CallToolRequestSchema, () => {
					calls += 1;
					return { content: [{ type: "text", text: "acted" }] };
				});
				await server.connect(serverTransport);
				instances.push(server);
				servers.push(server);
				return clientTransport;
			},
		};
		const source = createSource([definition], () => [directSelection("act", "world_act")]);
		const first = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		const stale = requiredTool(first.tools, "world_act");
		await instances[0]?.close();
		await expect(stale.execute("stale", {}, freshSignal())).rejects.toMatchObject({ code: "catalog_changed" });
		expect(calls).toBe(0);

		const second = await source.collect(INPUT, freshSignal(), { ...COORDINATE, turn: "2" }, []);
		expect(calls).toBe(0);
		await expect(requiredTool(second.tools, "world_act").execute("fresh", {}, freshSignal())).resolves.toMatchObject({
			content: [{ type: "text", text: "acted" }],
		});
		expect(calls).toBe(1);
		await source.close();
	});

	it("captures immutable request state and isolates diagnostic observer failures", async () => {
		const fixture = await createFixture({ pages: [{ tools: [{ name: "act", inputSchema: objectSchema() }] }] });
		let authorized: unknown;
		const source = new GameMcpRequestSource({
			sourceId: "mcp",
			servers: [fixture.definition],
			limits: LIMITS,
			lazyToolNames,
			select: (request) => {
				expect(Object.isFrozen(request.input)).toBe(true);
				expect(Object.isFrozen(request.input.payload)).toBe(true);
				expect(Object.isFrozen(request.coordinate)).toBe(true);
				expect(Object.isFrozen(request.visibleToolNames)).toBe(true);
				expect(Object.isFrozen(request.candidates)).toBe(true);
				return [directSelection("act", "world_act")];
			},
			authorizeCall: (request) => {
				authorized = request;
			},
			onDiagnostic: () => {
				throw new Error("observer failure");
			},
		});
		expect(source.provenanceKind).toBe("mcp");
		const mutableInput = {
			sessionId: INPUT.sessionId,
			actorId: INPUT.actorId,
			inputId: INPUT.inputId,
			payload: { instruction: String(INPUT.payload.instruction) },
		};
		const mutableCoordinate = { ...COORDINATE };
		const contribution = await source.collect(mutableInput, freshSignal(), mutableCoordinate, []);
		mutableInput.payload.instruction = "mutated";
		mutableCoordinate.turn = "99";
		await requiredTool(contribution.tools, "world_act").execute("call", {}, freshSignal());
		expect(authorized).toMatchObject({
			input: { payload: { instruction: "inspect the world" } },
			coordinate: { turn: "1" },
		});

		await expect(fixture.server.sendToolListChanged()).resolves.toBeUndefined();
		expect((await source.inspect(freshSignal())).diagnostics).toContainEqual({
			serverId: "world",
			code: "tool_list_changed",
		});
		await source.close();
	});

	it("reads MCP limits once and ignores unrelated future host fields", async () => {
		let idleTimeoutReads = 0;
		const limits = { ...LIMITS, futureHostLimit: 1 } as GameMcpLimits & { futureHostLimit: number };
		Object.defineProperty(limits, "requestIdleTimeoutMs", {
			enumerable: true,
			get() {
				idleTimeoutReads += 1;
				return idleTimeoutReads <= 3 ? 2_000 : 2_147_483_648;
			},
		});
		const source = new GameMcpRequestSource({
			sourceId: "mcp",
			servers: [
				{
					serverId: "world",
					description: "World",
					requirement: "required",
					createTransport: () => {
						throw new Error("Transport should not be created by configuration admission.");
					},
				},
			],
			limits,
			lazyToolNames,
			select: () => [],
			authorizeCall: () => undefined,
		});
		const prepared = source as unknown as { readonly limits: GameMcpLimits };
		expect(idleTimeoutReads).toBe(1);
		expect(prepared.limits.requestIdleTimeoutMs).toBe(2_000);
		await source.close();
	});

	it("returns from disposal and rejects waiters when a custom transport factory ignores cancellation", async () => {
		const source = createSource(
			[
				{
					serverId: "world",
					description: "Blocked custom transport",
					requirement: "required",
					createTransport: async () => await new Promise<InMemoryTransport>(() => undefined),
				},
			],
			() => [],
		);
		const pending = source.collect(INPUT, freshSignal(), COORDINATE, []);
		await expect(source.close()).resolves.toBeUndefined();
		await expect(pending).rejects.toMatchObject({ code: "server_unavailable" });
	});
});

describe("MCP transport configuration", () => {
	it("accepts only HTTPS or exact loopback HTTP and strict stdio configuration", () => {
		expect(() =>
			createGameMcpStreamableHttpTransportFactory({
				url: "http://example.com/mcp",
				maxHttpResponseBytes: 1,
				maxSseEventBytes: 1,
				reconnection: { initialDelayMs: 1, maxDelayMs: 1, delayGrowFactor: 1, maxAttempts: 0 },
			}),
		).toThrowError(expect.objectContaining({ code: "invalid_configuration" }));
		expect(() =>
			createGameMcpStreamableHttpTransportFactory({
				url: "http://localhost/mcp",
				maxHttpResponseBytes: 1,
				maxSseEventBytes: 1,
				reconnection: { initialDelayMs: 1, maxDelayMs: 2, delayGrowFactor: 2, maxAttempts: 0 },
			}),
		).not.toThrow();
		expect(() =>
			createGameMcpStdioTransportFactory({
				command: "server",
				args: [],
				env: { INVALID: "a\u0000b" },
				stderr: "ignore",
				maxMessageBytes: 1,
			}),
		).toThrowError(expect.objectContaining({ code: "invalid_configuration" }));
	});
});

interface FixtureOptions {
	readonly pages: readonly { readonly tools: readonly Tool[]; readonly nextCursor?: string }[];
	readonly call?: (request: {
		params: { name: string; arguments?: Record<string, unknown> };
	}) => CallToolResult | Promise<CallToolResult>;
}

async function createFixture(options: FixtureOptions): Promise<{
	readonly server: Server;
	readonly definition: GameMcpServerDefinition;
}> {
	const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
	const server = new Server({ name: "test-world", version: "1" }, { capabilities: { tools: { listChanged: true } } });
	server.setRequestHandler(ListToolsRequestSchema, (request) => {
		const cursor = request.params?.cursor;
		const pageIndex = cursor === undefined ? 0 : Number(cursor.replace("page-", "")) - 1;
		const page = options.pages[pageIndex];
		if (!page) throw new Error("Unknown test cursor.");
		return { tools: [...page.tools], ...(page.nextCursor === undefined ? {} : { nextCursor: page.nextCursor }) };
	});
	server.setRequestHandler(CallToolRequestSchema, async (request) => {
		const callRequest = {
			params: {
				name: request.params.name,
				...(request.params.arguments === undefined ? {} : { arguments: request.params.arguments }),
			},
		};
		return (await options.call?.(callRequest)) ?? { content: [{ type: "text", text: "ok" }] };
	});
	await server.connect(serverTransport);
	servers.push(server);
	return {
		server,
		definition: {
			serverId: "world",
			description: "Authoritative test world",
			requirement: "required",
			createTransport: () => clientTransport,
		},
	};
}

function createSource(
	definitions: readonly GameMcpServerDefinition[],
	select: () => readonly GameMcpToolSelection[],
	overrides: {
		readonly limits?: GameMcpLimits;
		readonly authorizeCall?: ConstructorParameters<typeof GameMcpRequestSource>[0]["authorizeCall"];
		readonly onDiagnostic?: ConstructorParameters<typeof GameMcpRequestSource>[0]["onDiagnostic"];
	} = {},
): GameMcpRequestSource {
	return new GameMcpRequestSource({
		sourceId: "mcp",
		servers: definitions,
		limits: overrides.limits ?? LIMITS,
		lazyToolNames,
		select,
		authorizeCall: overrides.authorizeCall ?? (() => undefined),
		...(overrides.onDiagnostic === undefined ? {} : { onDiagnostic: overrides.onDiagnostic }),
	});
}

function lookupTool(): Tool {
	return {
		name: "lookup",
		title: "Village lookup",
		description: "Find a named location in the game world.",
		inputSchema: {
			type: "object",
			properties: { place: { type: "string" } },
			required: ["place"],
			additionalProperties: false,
		},
		outputSchema: {
			type: "object",
			properties: { location: { type: "string" } },
			required: ["location"],
			additionalProperties: false,
		},
	};
}

function objectSchema(): Tool["inputSchema"] {
	return { type: "object", properties: {}, additionalProperties: false };
}

function lazySelection(name: string): GameMcpToolSelection {
	return { serverId: "world", name, exposure: "lazy", executionMode: "sequential" };
}

function directSelection(name: string, modelToolName: string): GameMcpToolSelection {
	return { serverId: "world", name, exposure: "direct", modelToolName, executionMode: "parallel" };
}

function freshSignal(): AbortSignal {
	return new AbortController().signal;
}

async function inspectOnly(source: GameMcpRequestSource, signal: AbortSignal) {
	const inspect = source.healthDependencies()[0]?.inspect;
	if (!inspect) throw new Error("Missing MCP health inspector.");
	return await inspect(signal);
}

function requiredTool(tools: readonly AgentTool[] | undefined, name: string): AgentTool {
	const tool = tools?.find((entry) => entry.name === name);
	if (!tool) throw new Error(`Missing test Tool ${name}.`);
	return tool;
}

function catalogRevision(context: Context): string {
	const match = JSON.stringify(context.messages).match(/catalogRevision\\?"\s*:\s*\\?"([0-9a-f]{64})/u);
	if (!match?.[1]) throw new Error("Missing MCP catalog revision in model context.");
	return match[1];
}

function toolResponse(id: string, name: string, args: Record<string, unknown>) {
	return messageResponse(assistant([{ type: "toolCall", id, name, arguments: args }], "toolUse"));
}

function copyContext(context: Context): Context {
	return {
		...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
		messages: structuredClone(context.messages),
		...(context.tools === undefined ? {} : { tools: [...context.tools] }),
	};
}

function createAgent(modelStream: StreamFn, source: GameMcpRequestSource): GameAgent {
	let ordinal = 0;
	return new GameAgent({
		sessionId: INPUT.sessionId,
		actorId: INPUT.actorId,
		model: {
			id: "test-model",
			name: "Test model",
			api: "openai-responses",
			provider: "openai",
			baseUrl: "https://example.invalid",
			reasoning: true,
			input: ["text"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
			contextWindow: 1,
			maxTokens: 1,
		},
		modelStream,
		requestSources: [source],
		toolPolicy: allowAllTestTools,
		toolLimits: TEST_TOOL_LIMITS,
		contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 131_072 },
		emitEvent: async (coordinate, data) => {
			ordinal += 1;
			const value = String(ordinal);
			return {
				protocolVersion: PROTOCOL_VERSION,
				eventId: `event-${value}`,
				sequence: value,
				revision: value,
				occurredAt: "2026-08-29T00:00:00.000Z",
				coordinate: structuredClone(coordinate),
				audience: eventAudience(data),
				data: structuredClone(data),
			} satisfies CanonicalEvent;
		},
		newId: () => `item-${++ordinal}`,
		now: () => 1,
	});
}
