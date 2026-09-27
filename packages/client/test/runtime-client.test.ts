import { createHash } from "node:crypto";
import { createServer, type Server } from "node:http";
import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { Model } from "@earendil-works/pi-ai";
import { DurableTranscript, InMemoryTranscriptStore } from "@opengameagent/agent";
import {
	ActionApprovalCoordinator,
	ActionCoordinator,
	InMemoryActionApprovalStore,
	InMemoryActionJournal,
	prepareActionIntent,
} from "@opengameagent/actions";
import { GameInteractionService, type InteractionRequest } from "@opengameagent/extensions";
import {
	BoundedFetchRedirectError,
	createBoundedFetch,
	RuntimeAttachmentIntegrityError,
	RuntimeClient,
	type RuntimeClientOptions,
	RuntimeClientResponseLimitError,
	RuntimeRunSessionApprovalNotFoundError,
	RuntimeRunSessionGapError,
	RuntimeRunSessionHandlerTimeoutError,
	RuntimeRunSessionIdentityError,
	RuntimeRunSessionStreamEndedError,
	RuntimeSseDisconnectedError,
	type RuntimeSseRetryPolicy,
} from "@opengameagent/client";
import { RuntimeProtocolValidationError, type RuntimeHealthSnapshot } from "@opengameagent/protocol";
import { GameRuntime, InMemoryEventStore } from "@opengameagent/runtime";
import {
	createRuntimeServer,
	type RuntimeAttachmentService,
	type RuntimeServerHealthService,
} from "@opengameagent/server";
import { describe, expect, it, vi } from "vitest";
import {
	allowAllTestTools,
	assistant,
	controlledResponse,
	messageResponse,
	TEST_TOOL_LIMITS,
	textResponse,
} from "../../runtime/test/pi-fakes.js";
import { serializeQueryKeyValue } from "../src/generated/core/queryKeySerializer.gen.js";

const MODEL: Model<"openai-responses"> = {
	id: "test-model",
	name: "Test model",
	api: "openai-responses",
	provider: "openai",
	baseUrl: "https://example.invalid",
	reasoning: false,
	input: ["text"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 1,
	maxTokens: 1,
};
const STREAM = { sessionId: "session", actorId: "actor" } as const;
const NEVER_CALLED: StreamFn = () => {
	throw new Error("The provider should not be called by this test.");
};
const ATTACHMENT_BYTES = Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10]);
const ATTACHMENT_REFERENCE = {
	type: "image-reference",
	attachmentId: `img_${createHash("sha256").update(ATTACHMENT_BYTES).digest("hex")}`,
	mediaType: "image/png",
	bytes: ATTACHMENT_BYTES.byteLength,
	width: 1,
	height: 1,
} as const;

function admittedOpenEventStream(
	request: Request,
	runId: string,
	inputId: string,
	startedInputId: string | null = inputId,
	terminalStatus?: "aborted",
): Response {
	const queued = {
		type: "event",
		event: {
			protocolVersion: "1",
			eventId: `${runId}-queued`,
			sequence: "1",
			revision: "1",
			occurredAt: "2026-08-30T00:00:00.000Z",
			coordinate: { ...STREAM, runId, turn: "0" },
			audience: { visibility: "owner" },
			data: {
				type: "run.queued",
				inputId,
				inputFingerprint: "2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210",
			},
		},
	};
	return new Response(
		new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new TextEncoder().encode(`event: event\ndata: ${JSON.stringify(queued)}\nid: 1\n\n`));
				if (startedInputId !== null) {
					const activated = {
						type: "event",
						event: {
							protocolVersion: "1",
							eventId: `${runId}-started`,
							sequence: "2",
							revision: "2",
							occurredAt: "2026-08-30T00:00:01.000Z",
							coordinate: { ...STREAM, runId, turn: "1" },
							audience: { visibility: "owner" },
							data: { type: "run.started", inputId: startedInputId },
						},
					};
					controller.enqueue(new TextEncoder().encode(`event: event\ndata: ${JSON.stringify(activated)}\nid: 2\n\n`));
				}
				if (terminalStatus !== undefined) {
					const terminal = {
						type: "event",
						event: {
							protocolVersion: "1",
							eventId: `${runId}-terminal`,
							sequence: "2",
							revision: "2",
							occurredAt: "2026-08-30T00:00:01.000Z",
							coordinate: { ...STREAM, runId, turn: "0" },
							audience: { visibility: "owner" },
							data: { type: "run.completed", status: terminalStatus },
						},
					};
					controller.enqueue(new TextEncoder().encode(`event: event\ndata: ${JSON.stringify(terminal)}\nid: 2\n\n`));
				}
				request.signal.addEventListener("abort", () => controller.close(), { once: true });
			},
		}),
		{ headers: { "content-type": "text/event-stream" } },
	);
}

async function clientFixture(
	options: {
		actions?: ActionCoordinator;
		approvals?: ActionApprovalCoordinator;
		attachments?: RuntimeAttachmentService;
		health?: RuntimeServerHealthService;
		modelStream?: StreamFn;
		newRunId?: () => string;
		onResponse?: (request: Request, response: Response) => void;
		runtime?: GameRuntime;
	} = {},
): Promise<RuntimeClient> {
	const runtime =
		options.runtime ??
		new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 4,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			...(options.newRunId === undefined ? {} : { newRunId: options.newRunId }),
		});
	if (!options.runtime) {
		await runtime.registerActor({ ...STREAM, model: MODEL, modelStream: options.modelStream ?? NEVER_CALLED });
	}
	const app = createRuntimeServer({
		runtime,
		access: {
			authenticate: () => ({ principalId: "owner" }),
			authorize: () => ({
				stream: STREAM,
				isOwner: true,
				recipientIds: [],
				allowInternal: false,
				...(options.actions ? { allowActionExchange: true } : {}),
				...(options.approvals ? { allowApprovals: true } : {}),
			}),
		},
		...(options.actions ? { actions: options.actions } : {}),
		...(options.approvals ? { approvals: options.approvals } : {}),
		...(options.attachments ? { attachments: options.attachments } : {}),
		...(options.health ? { health: options.health } : {}),
		limits: {
			maxRequestBytes: 1_000,
			maxResponseBytes: 10_000,
			maxJsonDepth: 20,
			maxEventPageSize: 10,
			maxSseConnections: 2,
			maxSseEventBytes: 10_000,
			maxSseBacklog: 10,
			maxActionClaimCandidates: 10,
			actionClaimLeaseMs: 1_000,
			actionStreamWaitMs: 10,
			maxApprovalPageSize: 10,
			maxApprovalWaitMilliseconds: 1_000,
			maxApprovalWaiters: 2,
		},
	});
	const honoFetch: typeof fetch = async (input, init) => {
		const request = input instanceof Request && init === undefined ? input : new Request(input, init);
		const response = await app.fetch(request);
		options.onResponse?.(request, response);
		return response;
	};
	return new RuntimeClient({
		baseUrl: "https://runtime.invalid",
		fetch: honoFetch,
		maxHttpResponseBytes: 10_000,
		maxAttachmentResponseBytes: 10_000,
		maxSseEventBytes: 10_000,
	});
}

describe("generated RuntimeClient", () => {
	describe.each(["submitRun", "startRunSession"] as const)("runtime input admission through %s", (method) => {
		it.each([
			{
				code: "input_too_large",
				status: 413,
				payload: { text: "界" },
				maxInputBytes: Buffer.byteLength(JSON.stringify({ text: "界" }), "utf8") - 1,
				maxInputDepth: 8,
			},
			{
				code: "input_too_deep",
				status: 400,
				payload: { nested: { value: true } },
				maxInputBytes: 4_096,
				maxInputDepth: 1,
			},
		])("preserves $code and accepts a corrected request", async (limit) => {
			const modelStream = vi.fn(() => messageResponse(assistant([])));
			const runtime = new GameRuntime(new InMemoryEventStore(), {
				maxActiveActors: 1,
				maxQueuedRuns: 4,
				maxInputBytes: limit.maxInputBytes,
				maxInputDepth: limit.maxInputDepth,
			});
			await runtime.registerActor({ ...STREAM, model: MODEL, modelStream });
			const responses: Array<{ method: string; path: string; status: number }> = [];
			const client = await clientFixture({
				runtime,
				onResponse: (request, response) => {
					responses.push({ method: request.method, path: new URL(request.url).pathname, status: response.status });
				},
			});
			const request = { ...STREAM, inputId: "input", payload: limit.payload };
			expect(Buffer.byteLength(JSON.stringify(request), "utf8")).toBeLessThan(1_000);
			const submission =
				method === "submitRun"
					? client.submitRun(request)
					: client.startRunSession(request, {
							eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 3 },
							actions: {
								retry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 3 },
								timeoutMs: 1_000,
								onDelivery: () => undefined,
							},
							approvals: { waitDurationMs: 10, pageSize: 1, timeoutMs: 1_000, onPending: () => undefined },
						});
			await expect(submission).rejects.toEqual({
				error: {
					code: limit.code,
					message:
						limit.code === "input_too_large"
							? `The run payload exceeds runtime.maxInputBytes (${limit.maxInputBytes} UTF-8 bytes).`
							: `The run payload exceeds runtime.maxInputDepth (${limit.maxInputDepth}).`,
				},
			});
			expect(responses).toEqual([{ method: "POST", path: "/runtime/v1/runs", status: limit.status }]);
			expect(modelStream).not.toHaveBeenCalled();

			await expect(client.submitRun({ ...request, payload: {} })).resolves.toMatchObject({
				data: { initialCoordinate: STREAM },
				response: { status: 202 },
			});
			runtime.stopAdmission();
			await runtime.waitForIdle();
			expect(modelStream).toHaveBeenCalledTimes(1);
			expect(responses).toEqual([
				{ method: "POST", path: "/runtime/v1/runs", status: limit.status },
				{ method: "POST", path: "/runtime/v1/runs", status: 202 },
			]);
		});
	});

	it("snapshots scoped run and close requests through the public client", async () => {
		const client = await clientFixture({ modelStream: () => messageResponse(assistant([])) });
		const request = { ...STREAM, scope: "planning", generation: "1", operationId: "close-1" };
		const closed = await client.closeAuthority(request);
		expect((await client.closeAuthority(request)).data).toEqual(closed.data);
		expect((await client.readAuthorityClosure({ ...STREAM, scope: "planning", generation: "1" })).data).toEqual({
			closure: closed.data,
		});
		await expect(
			client.submitRun({
				...STREAM,
				inputId: "old",
				payload: {},
				admissionAuthority: { scope: "planning", generation: "1" },
			}),
		).rejects.toMatchObject({ error: { code: "authority_closed" } });
		const admissionAuthority = { scope: "planning", generation: "2" };
		const starting = client.startRunSession(
			{ ...STREAM, inputId: "next", payload: {}, admissionAuthority },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
			},
		);
		admissionAuthority.generation = "1";
		const session = await starting;
		expect((await session.completion).data.status).toBe("completed");
		await expect(
			client.submitRun({
				...STREAM,
				inputId: "next",
				payload: {},
				admissionAuthority: { scope: "planning", generation: "3" },
			}),
		).rejects.toMatchObject({ error: { code: "input_conflict" } });
	});
	it("starts an exact run event stream from the server-issued admission cursor", async () => {
		const afterRevisions: Array<string | null> = [];
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const url = new URL((input as Request).url);
				if (url.pathname === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "target-run",
							initialCoordinate: { ...STREAM, runId: "target-run", turn: "0" },
							eventCursor: "41",
						},
						{ status: 202 },
					);
				}
				if (url.pathname === "/runtime/v1/events/stream") {
					afterRevisions.push(url.searchParams.get("afterRevision"));
					return new Response(
						'event: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"42","revision":"42","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"target-run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}\nid: 42\n\nevent: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"43","revision":"43","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"target-run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}\nid: 43\n\n',
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				throw new Error(`Unexpected Runtime path: ${url.pathname}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});

		const session = await client.startRunSession(
			{ ...STREAM, inputId: "input", payload: {} },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 } },
		);

		await expect(session.completion).resolves.toMatchObject({ data: { type: "run.completed" } });
		expect(afterRevisions).toEqual(["41"]);
	});

	it("rejects a cursor that skips the matching run admission", async () => {
		const paths: string[] = [];
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				paths.push(path);
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "target-run",
							initialCoordinate: { ...STREAM, runId: "target-run", turn: "0" },
							eventCursor: "41",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return new Response(
						'event: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"42","revision":"42","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"target-run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}\nid: 42\n\n',
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				if (path === "/runtime/v1/actions/stream") {
					return new Response(
						new ReadableStream<Uint8Array>({
							start(controller) {
								request.signal.addEventListener("abort", () => controller.close(), { once: true });
							},
						}),
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				if (path === "/runtime/v1/approvals/wait") {
					return Response.json({ approvals: [], cursor: "0".repeat(64) });
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				actions: {
					retry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
					timeoutMs: 1_000,
					onDelivery: () => undefined,
				},
				approvals: {
					waitDurationMs: 10,
					pageSize: 1,
					timeoutMs: 1_000,
					onPending: () => undefined,
				},
			},
		);

		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		expect(paths).toEqual(["/runtime/v1/runs", "/runtime/v1/events/stream"]);
	});

	it("keeps Action and Approval observers dormant while the exact run remains queued", async () => {
		let actionStreams = 0;
		let approvalLists = 0;
		let markQueued: (() => void) | undefined;
		let releaseQueued: (() => void) | undefined;
		const queued = new Promise<void>((resolve) => {
			markQueued = resolve;
		});
		const queuedRelease = new Promise<void>((resolve) => {
			releaseQueued = resolve;
		});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "queued-run",
							initialCoordinate: { ...STREAM, runId: "queued-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return admittedOpenEventStream(request, "queued-run", "queued-input", null, "aborted");
				}
				if (path === "/runtime/v1/actions/stream") {
					actionStreams += 1;
					return new Response(null, { headers: { "content-type": "text/event-stream" } });
				}
				if (path === "/runtime/v1/approvals/wait") {
					approvalLists += 1;
					return Response.json({ approvals: [], cursor: "0".repeat(64) });
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "queued-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				onEvent: {
					timeoutMs: 1_000,
					handle: async (event) => {
						if (event.data.type === "run.queued") {
							markQueued?.();
							await queuedRelease;
						}
					},
				},
				actions: {
					retry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
					timeoutMs: 1_000,
					onDelivery: () => undefined,
				},
				approvals: {
					waitDurationMs: 10,
					pageSize: 1,
					timeoutMs: 1_000,
					onPending: () => undefined,
				},
			},
		);

		await queued;
		await new Promise((resolve) => setTimeout(resolve, 20));
		expect(actionStreams).toBe(0);
		expect(approvalLists).toBe(0);
		releaseQueued?.();
		await expect(session.completion).resolves.toMatchObject({
			data: { type: "run.completed", status: "aborted" },
		});
	});

	it("rejects a run.started event bound to a different input", async () => {
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "activation-run",
							initialCoordinate: { ...STREAM, runId: "activation-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return admittedOpenEventStream(request, "activation-run", "activation-input", "foreign-input");
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "activation-input", payload: {} },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 } },
		);

		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
	});

	it("rejects an invalid Approval wait duration before submitting", async () => {
		let requests = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () => {
				requests += 1;
				return new Response();
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});

		await expect(
			client.startRunSession(
				{ ...STREAM, inputId: "invalid-backoff-input", payload: {} },
				{
					eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
					approvals: {
						waitDurationMs: 0,
						pageSize: 1,
						timeoutMs: 1_000,
						onPending: () => undefined,
					},
				},
			),
		).rejects.toBeInstanceOf(TypeError);
		expect(requests).toBe(0);
	});

	it("holds one Approval wait and restarts it after a local decision", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const prepared = prepareActionIntent({
			...STREAM,
			coordinate: { ...STREAM, runId: "backoff-run", turn: "1" },
			actionName: "world.write",
			operationKey: "backoff-operation",
			payload: {},
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: [],
			replayPolicy: "recoverable",
		});
		const pending = await approvals.request("high-risk", prepared, 1_000);
		let approvalLists = 0;
		let markFirstList: (() => void) | undefined;
		let markSecondList: (() => void) | undefined;
		let markThirdList: (() => void) | undefined;
		let markFourthList: (() => void) | undefined;
		const firstList = new Promise<void>((resolve) => {
			markFirstList = resolve;
		});
		const secondList = new Promise<void>((resolve) => {
			markSecondList = resolve;
		});
		const thirdList = new Promise<void>((resolve) => {
			markThirdList = resolve;
		});
		const fourthList = new Promise<void>((resolve) => {
			markFourthList = resolve;
		});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "backoff-run",
							initialCoordinate: { ...STREAM, runId: "backoff-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return admittedOpenEventStream(request, "backoff-run", "backoff-input");
				}
				if (path === "/runtime/v1/approvals/wait") {
					approvalLists += 1;
					if (approvalLists === 1) markFirstList?.();
					if (approvalLists === 2) markSecondList?.();
					if (approvalLists === 3) markThirdList?.();
					if (approvalLists === 4) markFourthList?.();
					if (approvalLists === 2 || approvalLists === 4) {
						await new Promise<void>((resolve) =>
							request.signal.addEventListener("abort", () => resolve(), { once: true }),
						);
						throw request.signal.reason;
					}
					return Response.json({
						approvals: approvalLists === 1 ? [pending] : [],
						cursor: (approvalLists === 1 ? "1" : "2").repeat(64),
					});
				}
				if (path === "/runtime/v1/approvals/decision") {
					const approval = await approvals.decide({
						...STREAM,
						approvalId: pending.request.approvalId,
						expectedRevision: pending.revision,
						requestFingerprint: pending.request.requestFingerprint,
						decision: "approve",
						reviewerId: "owner",
					});
					return Response.json({ approval });
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "backoff-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				approvals: {
					waitDurationMs: 500,
					pageSize: 1,
					timeoutMs: 1_000,
					onPending: () => undefined,
				},
			},
		);

		await firstList;
		await secondList;
		await new Promise((resolve) => setTimeout(resolve, 50));
		expect(approvalLists).toBe(2);
		await expect(
			session.decideApproval({ approvalId: pending.request.approvalId, decision: "approve" }),
		).resolves.toMatchObject({ status: "approved" });
		const mutationWokeDiscovery = await Promise.race([
			thirdList.then(() => true),
			new Promise<false>((resolve) => setTimeout(() => resolve(false), 250)),
		]);
		expect(mutationWokeDiscovery).toBe(true);
		const nextLongWaitStarted = await Promise.race([
			fourthList.then(() => true),
			new Promise<false>((resolve) => setTimeout(() => resolve(false), 250)),
		]);
		expect(nextLongWaitStarted).toBe(true);
		const reason = new Error("backoff-observation-complete");
		session.close(reason);
		await expect(session.completion).rejects.toBe(reason);
	});

	it("rejects an accepted run that is not bound to the submitted stream", async () => {
		const paths: string[] = [];
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const path = new URL((input as Request).url).pathname;
				paths.push(path);
				return Response.json(
					{
						runId: "misrouted-run",
						eventCursor: "0",
						initialCoordinate: {
							sessionId: "foreign-session",
							actorId: "foreign-actor",
							runId: "misrouted-run",
							turn: "0",
						},
					},
					{ status: 202 },
				);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});

		await expect(
			client.startRunSession(
				{ ...STREAM, inputId: "misrouted-input", payload: {} },
				{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 } },
			),
		).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		expect(paths).toEqual(["/runtime/v1/runs"]);
	});

	it("rejects a foreign stream identity before ignoring another run", async () => {
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const path = new URL((input as Request).url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "target-run",
							initialCoordinate: { ...STREAM, runId: "target-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return new Response(
						'event: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"foreign-event","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"foreign-session","actorId":"foreign-actor","runId":"foreign-run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"turn.started"}}}\nid: 1\n\n',
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "foreign-stream-input", payload: {} },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 } },
		);

		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
	});

	it("rejects an invalid terminal event before notifying the game", async () => {
		let notifications = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const path = new URL((input as Request).url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "invalid-event-run",
							initialCoordinate: { ...STREAM, runId: "invalid-event-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return new Response(
						'event: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"invalid-terminal","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"invalid-event-run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"invented"}}}\nid: 1\n\n',
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "invalid-event-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				onEvent: {
					timeoutMs: 1_000,
					handle: () => {
						notifications += 1;
					},
				},
			},
		);

		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		expect(notifications).toBe(0);
	});

	it("owns one exact run lifecycle through terminal completion", async () => {
		const client = await clientFixture({ modelStream: () => textResponse("done") });
		const observed: string[] = [];
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "session-input", payload: { instruction: "act" } },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 },
				onEvent: {
					timeoutMs: 1_000,
					handle: (event) => {
						observed.push(event.data.type);
					},
				},
			},
		);

		const terminal = await session.completion;

		expect(session.accepted.initialCoordinate.runId).toBe(session.accepted.runId);
		expect(terminal).toMatchObject({
			coordinate: { ...STREAM, runId: session.accepted.runId },
			data: { type: "run.completed", status: "completed" },
		});
		expect(observed.at(-1)).toBe("run.completed");
	});

	it("delivers and decides only Action and Approval work bound to the exact run", async () => {
		const controlled = controlledResponse();
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
			newClaimToken: () => "session-claim",
		});
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const foreign = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "foreign-run", turn: "1" },
			actionName: "world.write",
			operationKey: "foreign-operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["foreign-key"],
			replayPolicy: "recoverable",
		});
		const exact = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "session-run", turn: "1" },
			actionName: "world.write",
			operationKey: "session-operation",
			payload: { value: 2 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["session-key"],
			replayPolicy: "recoverable",
		});
		await approvals.request("high-risk", foreign, 1_000);
		const exactApproval = await approvals.request("high-risk", exact, 1_000);
		const client = await clientFixture({
			actions,
			approvals,
			modelStream: controlled.streamFn,
			newRunId: () => "session-run",
		});
		let resolveAction: ((operationId: string) => void) | undefined;
		let resolveApproval: ((approvalId: string) => void) | undefined;
		const actionSeen = new Promise<string>((resolve) => {
			resolveAction = resolve;
		});
		const approvalSeen = new Promise<string>((resolve) => {
			resolveApproval = resolve;
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "unified-input", payload: { instruction: "act" } },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 },
				actions: {
					retry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 },
					timeoutMs: 1_000,
					onDelivery: async (delivery) => {
						resolveAction?.(delivery.action.operationId);
						if (delivery.action.claim === undefined) throw new Error("Expected an exact Action claim.");
						await client.recordActionReceipt({
							...STREAM,
							operationId: delivery.action.operationId,
							claimToken: delivery.action.claim.claimToken,
							attempt: delivery.action.claim.attempt,
							outcome: "succeeded",
						});
					},
				},
				approvals: {
					waitDurationMs: 10,
					pageSize: 10,
					timeoutMs: 1_000,
					onPending: (approval) => {
						resolveApproval?.(approval.request.approvalId);
					},
				},
			},
		);

		expect(await actionSeen).toBe(exact.operationId);
		expect(await approvalSeen).toBe(exactApproval.request.approvalId);
		const decisionAbort = new AbortController();
		const decisionReason = new Error("cancel-decision-only");
		decisionAbort.abort(decisionReason);
		await expect(
			session.decideApproval({
				approvalId: exactApproval.request.approvalId,
				decision: "approve",
				signal: decisionAbort.signal,
			}),
		).rejects.toBe(decisionReason);
		await expect(
			session.decideApproval({ approvalId: exactApproval.request.approvalId, decision: "approve" }),
		).resolves.toMatchObject({ status: "approved", request: { coordinate: exact.coordinate } });
		await expect(actions.get(foreign, foreign.operationId)).resolves.toMatchObject({ status: "pending" });
		await expect(approvals.listPending({ ...STREAM, runId: "foreign-run", limit: 10 })).resolves.toHaveLength(1);
		controlled.finish();
		await expect(session.completion).resolves.toMatchObject({ data: { type: "run.completed", status: "completed" } });
	});

	it("rejects a content-tampered Action before notifying the game", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal());
		const action = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "tampered-action-run", turn: "1" },
			actionName: "world.write",
			operationKey: "tampered-action-operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: [],
			replayPolicy: "recoverable",
		});
		const tampered = structuredClone(action);
		tampered.payload = { value: 2 };
		let deliveries = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "tampered-action-run",
							initialCoordinate: { ...STREAM, runId: "tampered-action-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return admittedOpenEventStream(request, "tampered-action-run", "tampered-action-input");
				}
				if (path === "/runtime/v1/actions/stream") {
					return new Response(
						`event: delivery\ndata: ${JSON.stringify({ type: "delivery", delivery: { kind: "dispatch", action: tampered } })}\n\n`,
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 10_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 10_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "tampered-action-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				actions: {
					retry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
					timeoutMs: 1_000,
					onDelivery: () => {
						deliveries += 1;
					},
				},
			},
		);

		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		expect(deliveries).toBe(0);
	});

	it("rejects a content-tampered Approval before notifying the game", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const intent = prepareActionIntent({
			...STREAM,
			coordinate: { ...STREAM, runId: "tampered-approval-run", turn: "1" },
			actionName: "world.write",
			operationKey: "tampered-approval-operation",
			payload: {},
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: [],
			replayPolicy: "recoverable",
		});
		const approval = await approvals.request("high-risk", intent, 1_000);
		const tampered = structuredClone(approval);
		tampered.request.policyId = "other-policy";
		let notifications = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "tampered-approval-run",
							initialCoordinate: { ...STREAM, runId: "tampered-approval-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return admittedOpenEventStream(request, "tampered-approval-run", "tampered-approval-input");
				}
				if (path === "/runtime/v1/approvals/wait") {
					return Response.json({ approvals: [tampered], cursor: "1".repeat(64) });
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 10_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "tampered-approval-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				approvals: {
					waitDurationMs: 10,
					pageSize: 1,
					timeoutMs: 1_000,
					onPending: () => {
						notifications += 1;
					},
				},
			},
		);

		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		expect(notifications).toBe(0);
	});

	it("closes local observation with the caller's reason without aborting the durable run", async () => {
		const controlled = controlledResponse();
		const client = await clientFixture({ modelStream: controlled.streamFn, newRunId: () => "local-close-run" });
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "local-close-input", payload: { instruction: "wait" } },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 } },
		);
		await controlled.started;
		const reason = new Error("stop-observing");

		session.close(reason);
		await expect(session.completion).rejects.toBe(reason);
		controlled.finish();
		await vi.waitFor(async () => {
			const replay = await client.readEvents({ ...STREAM, limit: 10 });
			expect(replay.data.kind).toBe("page");
			if (replay.data.kind !== "page") return;
			expect(replay.data.events.at(-1)?.data).toEqual({ type: "run.completed", status: "completed" });
		});
	});

	it("closes promptly even when a game event handler never settles", async () => {
		let markHandlerStarted: (() => void) | undefined;
		const handlerStarted = new Promise<void>((resolve) => {
			markHandlerStarted = resolve;
		});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "pending-handler-run",
							initialCoordinate: { ...STREAM, runId: "pending-handler-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return new Response(
						new ReadableStream<Uint8Array>({
							start(controller) {
								controller.enqueue(
									new TextEncoder().encode(
										'event: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"pending-handler-queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"pending-handler-run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"pending-handler-input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}\nid: 1\n\n',
									),
								);
								request.signal.addEventListener("abort", () => controller.close(), { once: true });
							},
						}),
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "pending-handler-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				onEvent: {
					timeoutMs: 1_000,
					handle: () => {
						markHandlerStarted?.();
						return new Promise<void>(() => undefined);
					},
				},
			},
		);
		await handlerStarted;
		const reason = new Error("close-pending-handler");

		session.close(reason);

		await expect(session.completion).rejects.toBe(reason);
	});

	it("bounds terminal notification and surfaces terminal handler failure", async () => {
		let markHandlerStarted: (() => void) | undefined;
		const handlerStarted = new Promise<void>((resolve) => {
			markHandlerStarted = resolve;
		});
		let submittedInputId = "";
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					submittedInputId = ((await request.json()) as { inputId: string }).inputId;
					return Response.json(
						{
							runId: "terminal-handler-run",
							initialCoordinate: { ...STREAM, runId: "terminal-handler-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return new Response(
						`event: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal-handler-queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"terminal-handler-run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"${submittedInputId}","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}\nid: 1\n\nevent: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal-handler-event","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"terminal-handler-run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}\nid: 2\n\n`,
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "terminal-handler-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				onEvent: {
					timeoutMs: 1,
					handle: (event) => {
						if (event.data.type !== "run.completed") return;
						markHandlerStarted?.();
						return new Promise<void>(() => undefined);
					},
				},
			},
		);

		await handlerStarted;
		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionHandlerTimeoutError);

		const failure = new Error("terminal event handler failed");
		const failed = await client.startRunSession(
			{ ...STREAM, inputId: "terminal-handler-failure-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				onEvent: {
					timeoutMs: 1_000,
					handle: (event) => {
						if (event.data.type !== "run.completed") return;
						throw failure;
					},
				},
			},
		);
		await expect(failed.completion).rejects.toBe(failure);
	});

	it("enforces the configured Action delivery handler deadline", async () => {
		const controlled = controlledResponse();
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
			newClaimToken: () => "action-handler-claim",
		});
		await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "action-handler-run", turn: "1" },
			actionName: "world.write",
			operationKey: "action-handler-operation",
			payload: {},
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: [],
			replayPolicy: "recoverable",
		});
		const client = await clientFixture({
			actions,
			modelStream: controlled.streamFn,
			newRunId: () => "action-handler-run",
		});
		let markStarted: (() => void) | undefined;
		const started = new Promise<void>((resolve) => {
			markStarted = resolve;
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "action-handler-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				actions: {
					retry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
					timeoutMs: 1,
					onDelivery: () => {
						markStarted?.();
						return new Promise<void>(() => undefined);
					},
				},
			},
		);

		await started;
		await expect(session.completion).rejects.toMatchObject({
			name: "RuntimeRunSessionHandlerTimeoutError",
			handler: "action",
		});
	});

	it("enforces the configured pending Approval handler deadline", async () => {
		const controlled = controlledResponse();
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const intent = prepareActionIntent({
			...STREAM,
			coordinate: { ...STREAM, runId: "approval-handler-run", turn: "1" },
			actionName: "world.write",
			operationKey: "approval-handler-operation",
			payload: {},
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: [],
			replayPolicy: "never",
		});
		await approvals.request("high-risk", intent, 1_000);
		const client = await clientFixture({
			approvals,
			modelStream: controlled.streamFn,
			newRunId: () => "approval-handler-run",
		});
		let markStarted: (() => void) | undefined;
		const started = new Promise<void>((resolve) => {
			markStarted = resolve;
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "approval-handler-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				approvals: {
					waitDurationMs: 10,
					pageSize: 1,
					timeoutMs: 1,
					onPending: () => {
						markStarted?.();
						return new Promise<void>(() => undefined);
					},
				},
			},
		);

		await started;
		await expect(session.completion).rejects.toMatchObject({
			name: "RuntimeRunSessionHandlerTimeoutError",
			handler: "approval",
		});
	});

	it("lets an explicit approval decision finish after local observation closes", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const prepared = prepareActionIntent({
			...STREAM,
			coordinate: { ...STREAM, runId: "decision-run", turn: "1" },
			actionName: "world.write",
			operationKey: "decision-operation",
			payload: {},
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: [],
			replayPolicy: "recoverable",
		});
		const pending = await approvals.request("high-risk", prepared, 1_000);
		let markPendingSeen: (() => void) | undefined;
		const pendingSeen = new Promise<void>((resolve) => {
			markPendingSeen = resolve;
		});
		let markDecisionStarted: (() => void) | undefined;
		const decisionStarted = new Promise<void>((resolve) => {
			markDecisionStarted = resolve;
		});
		let releaseDecision: (() => void) | undefined;
		const decisionGate = new Promise<void>((resolve) => {
			releaseDecision = resolve;
		});
		let decisionCalls = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "decision-run",
							initialCoordinate: { ...STREAM, runId: "decision-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return admittedOpenEventStream(request, "decision-run", "decision-input");
				}
				if (path === "/runtime/v1/approvals/wait") {
					return Response.json({ approvals: [pending], cursor: "1".repeat(64) });
				}
				if (path === "/runtime/v1/approvals/decision") {
					decisionCalls += 1;
					markDecisionStarted?.();
					await decisionGate;
					if (decisionCalls <= 3) {
						const decidedAt = "2026-08-28T00:00:00.001Z";
						return Response.json({
							approval: {
								...pending,
								request:
									decisionCalls === 1
										? { ...pending.request, requestedAt: "2026-08-27T00:00:00.000Z" }
										: pending.request,
								revision: "2",
								status: decisionCalls <= 2 ? "approved" : "expired",
								updatedAt: decisionCalls <= 2 ? decidedAt : pending.request.expiresAt,
								decision: {
									decision: "approved",
									reviewerId: "owner",
									decidedAt,
									...(decisionCalls === 2
										? { reason: "unrequested-reason" }
										: decisionCalls === 3
											? { reason: "impossible-expired-decision" }
											: {}),
								},
							},
						});
					}
					const approval = await approvals.decide({
						...STREAM,
						approvalId: pending.request.approvalId,
						expectedRevision: pending.revision,
						requestFingerprint: pending.request.requestFingerprint,
						decision: "approve",
						reviewerId: "owner",
					});
					return Response.json({ approval });
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 10_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "decision-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				approvals: {
					waitDurationMs: 10,
					pageSize: 1,
					timeoutMs: 1_000,
					onPending: () => markPendingSeen?.(),
				},
			},
		);
		await pendingSeen;
		const closeReason = new Error("close-observation-only");
		session.close(closeReason);
		await expect(session.completion).rejects.toBe(closeReason);

		const invalidDecision = session.decideApproval({ approvalId: pending.request.approvalId, decision: "approve" });
		await decisionStarted;
		releaseDecision?.();

		await expect(invalidDecision).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		await expect(
			session.decideApproval({ approvalId: pending.request.approvalId, decision: "approve" }),
		).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		await expect(
			session.decideApproval({ approvalId: pending.request.approvalId, decision: "approve" }),
		).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		await expect(
			session.decideApproval({ approvalId: pending.request.approvalId, decision: "approve" }),
		).resolves.toMatchObject({ status: "approved", revision: "2" });
	});

	it("discards a stale approval poll that races a local decision", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const prepared = prepareActionIntent({
			...STREAM,
			coordinate: { ...STREAM, runId: "approval-race-run", turn: "1" },
			actionName: "world.write",
			operationKey: "approval-race-operation",
			payload: {},
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: [],
			replayPolicy: "recoverable",
		});
		const pending = await approvals.request("high-risk", prepared, 1_000);
		let listCalls = 0;
		let markSecondPollStarted: (() => void) | undefined;
		const secondPollStarted = new Promise<void>((resolve) => {
			markSecondPollStarted = resolve;
		});
		let releaseStalePoll: (() => void) | undefined;
		const stalePollGate = new Promise<void>((resolve) => {
			releaseStalePoll = resolve;
		});
		let markFreshPollStarted: (() => void) | undefined;
		const freshPollStarted = new Promise<void>((resolve) => {
			markFreshPollStarted = resolve;
		});
		let markReconciledPollStarted: (() => void) | undefined;
		const reconciledPollStarted = new Promise<void>((resolve) => {
			markReconciledPollStarted = resolve;
		});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "approval-race-run",
							initialCoordinate: { ...STREAM, runId: "approval-race-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return admittedOpenEventStream(request, "approval-race-run", "approval-race-input");
				}
				if (path === "/runtime/v1/approvals/wait") {
					listCalls += 1;
					if (listCalls === 1) return Response.json({ approvals: [pending], cursor: "1".repeat(64) });
					if (listCalls === 2) {
						markSecondPollStarted?.();
						await stalePollGate;
						return Response.json({ approvals: [pending], cursor: "1".repeat(64) });
					}
					if (listCalls === 3) markFreshPollStarted?.();
					else markReconciledPollStarted?.();
					return Response.json({ approvals: [], cursor: "2".repeat(64) });
				}
				if (path === "/runtime/v1/approvals/decision") {
					const approval = await approvals.decide({
						...STREAM,
						approvalId: pending.request.approvalId,
						expectedRevision: pending.revision,
						requestFingerprint: pending.request.requestFingerprint,
						decision: "approve",
						reviewerId: "owner",
					});
					return Response.json({ approval });
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 10_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		let notifications = 0;
		let markPendingSeen: (() => void) | undefined;
		const pendingSeen = new Promise<void>((resolve) => {
			markPendingSeen = resolve;
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "approval-race-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				approvals: {
					waitDurationMs: 10,
					pageSize: 1,
					timeoutMs: 1_000,
					onPending: () => {
						notifications += 1;
						markPendingSeen?.();
					},
				},
			},
		);
		await pendingSeen;
		await secondPollStarted;
		await expect(
			session.decideApproval({ approvalId: pending.request.approvalId, decision: "approve" }),
		).resolves.toMatchObject({ status: "approved" });

		releaseStalePoll?.();
		await freshPollStarted;
		await reconciledPollStarted;
		expect(notifications).toBe(1);
		await expect(
			session.decideApproval({ approvalId: pending.request.approvalId, decision: "approve" }),
		).rejects.toBeInstanceOf(RuntimeRunSessionApprovalNotFoundError);
		const closeReason = new Error("approval-race-complete");
		session.close(closeReason);
		await expect(session.completion).rejects.toBe(closeReason);
	});

	it("aborts the remote run with the latest exact coordinate", async () => {
		const controlled = controlledResponse();
		const client = await clientFixture({ modelStream: controlled.streamFn, newRunId: () => "remote-abort-run" });
		let observeActiveTurn: (() => void) | undefined;
		const activeTurn = new Promise<void>((resolve) => {
			observeActiveTurn = resolve;
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "remote-abort-input", payload: { instruction: "wait" } },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 },
				onEvent: {
					timeoutMs: 1_000,
					handle: (event) => {
						if (event.data.type === "turn.started" && event.coordinate.turn === "1") observeActiveTurn?.();
					},
				},
			},
		);
		await activeTurn;

		await expect(session.abort()).resolves.toEqual({
			accepted: true,
			coordinate: { ...STREAM, runId: "remote-abort-run", turn: "1" },
		});
		await expect(session.completion).resolves.toMatchObject({
			data: { type: "run.completed", status: "aborted" },
		});
	});

	it("starts another same-actor interaction after an aborted run reaches its terminal event", async () => {
		let firstAskedResolve: (() => void) | undefined;
		const firstAsked = new Promise<void>((resolve) => {
			firstAskedResolve = resolve;
		});
		let secondAskedResolve: (() => void) | undefined;
		const secondAsked = new Promise<void>((resolve) => {
			secondAskedResolve = resolve;
		});
		const pending = new Map<
			string,
			{ readonly request: InteractionRequest; readonly resolve: (response: unknown) => void }
		>();
		let requestCount = 0;
		const interactions = new GameInteractionService({
			limits: {
				maxIdentifierBytes: 128,
				maxCounterDigits: 20,
				maxQuestionBytes: 1_024,
				maxHeaderBytes: 128,
				maxDetailBytes: 4_096,
				maxOptionLabelBytes: 256,
				maxOptionDescriptionBytes: 1_024,
				maxCustomAnswerBytes: 2_048,
				maxQuestionsPerRequest: 4,
				maxOptionsPerQuestion: 5,
				maxRequestBytes: 8_192,
				maxResponseBytes: 4_096,
				maxPendingRequests: 2,
			},
			broker: {
				ask: (request, signal) =>
					new Promise((resolve, reject) => {
						requestCount += 1;
						pending.set(request.requestId, { request, resolve });
						signal.addEventListener(
							"abort",
							() => {
								pending.delete(request.requestId);
								reject(signal.reason);
							},
							{ once: true },
						);
						(requestCount === 1 ? firstAskedResolve : secondAskedResolve)?.();
					}),
			},
		});
		let modelCall = 0;
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		let runId = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
			newRunId: () => `same-actor-run-${++runId}`,
		});
		await runtime.registerActor({
			...STREAM,
			model: MODEL,
			modelStream: () => {
				modelCall += 1;
				return modelCall <= 2
					? messageResponse(
							assistant(
								[
									{
										type: "toolCall",
										id: `ask-${modelCall}`,
										name: "ask_player",
										arguments: {
											questions: [
												{
													questionId: "path",
													question: "Which path?",
													options: [{ optionId: "verify", label: "Verify" }],
												},
											],
										},
									},
								],
								"toolUse",
							),
						)
					: textResponse("done");
			},
			toolSource: (input, signal, coordinate) => interactions.createTools(input, signal, coordinate),
			toolPolicy: allowAllTestTools,
			toolLimits: TEST_TOOL_LIMITS,
		});
		const client = await clientFixture({ runtime });
		let firstTurnResolve: (() => void) | undefined;
		const firstTurn = new Promise<void>((resolve) => {
			firstTurnResolve = resolve;
		});
		const first = await client.startRunSession(
			{ ...STREAM, inputId: "same-actor-first", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 },
				onEvent: {
					timeoutMs: 1_000,
					handle: (event) => {
						if (event.data.type === "turn.started" && event.coordinate.turn === "1") firstTurnResolve?.();
					},
				},
			},
		);
		await Promise.all([firstAsked, firstTurn]);
		await expect(first.abort()).resolves.toMatchObject({ accepted: true });
		await expect(first.completion).resolves.toMatchObject({ data: { type: "run.completed", status: "aborted" } });

		const second = await client.startRunSession(
			{ ...STREAM, inputId: "same-actor-second", payload: {} },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 } },
		);
		await Promise.race([
			secondAsked,
			new Promise((_, reject) => setTimeout(() => reject(new Error("second interaction did not start")), 1_000)),
		]);
		const [secondPending] = [...pending.values()];
		if (!secondPending) throw new Error("Expected the second interaction request.");
		secondPending.resolve({
			status: "answered",
			answers: [{ questionId: "path", selectedOptionIds: ["verify"] }],
		});
		await expect(second.completion).resolves.toMatchObject({ data: { type: "run.completed", status: "completed" } });
	});

	it("preserves an abort reason when the control transport ignores cancellation", async () => {
		let markControlStarted: (() => void) | undefined;
		const controlStarted = new Promise<void>((resolve) => {
			markControlStarted = resolve;
		});
		let releaseControl: (() => void) | undefined;
		const controlGate = new Promise<void>((resolve) => {
			releaseControl = resolve;
		});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "ignored-abort-run",
							initialCoordinate: { ...STREAM, runId: "ignored-abort-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return new Response(
						new ReadableStream<Uint8Array>({
							start(controller) {
								request.signal.addEventListener("abort", () => controller.close(), { once: true });
							},
						}),
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				if (path === "/runtime/v1/control") {
					markControlStarted?.();
					await controlGate;
					return Response.json({
						accepted: true,
						coordinate: { ...STREAM, runId: "ignored-abort-run", turn: "0" },
					});
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "ignored-abort-input", payload: {} },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 } },
		);
		const controller = new AbortController();
		const reason = new Error("control-cancelled");
		const aborted = session.abort(controller.signal);
		await controlStarted;

		controller.abort(reason);

		await expect(aborted).rejects.toBe(reason);
		releaseControl?.();
		const closeReason = new Error("close-after-control-test");
		session.close(closeReason);
		await expect(session.completion).rejects.toBe(closeReason);
	});

	it("binds every control result to the exact coordinate sent", async () => {
		let controlCalls = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				const path = new URL(request.url).pathname;
				if (path === "/runtime/v1/runs") {
					return Response.json(
						{
							runId: "control-run",
							initialCoordinate: { ...STREAM, runId: "control-run", turn: "0" },
							eventCursor: "0",
						},
						{ status: 202 },
					);
				}
				if (path === "/runtime/v1/events/stream") {
					return new Response(
						new ReadableStream<Uint8Array>({
							start(controller) {
								request.signal.addEventListener("abort", () => controller.close(), { once: true });
							},
						}),
						{ headers: { "content-type": "text/event-stream" } },
					);
				}
				if (path === "/runtime/v1/control") {
					controlCalls += 1;
					switch (controlCalls) {
						case 1:
							return Response.json({
								accepted: false,
								reason: "run-not-active",
								activeCoordinate: { ...STREAM, runId: "control-run", turn: "0" },
							});
						case 2:
							return Response.json({
								accepted: true,
								coordinate: { ...STREAM, runId: "control-run", turn: "1" },
							});
						case 3:
							return Response.json({
								accepted: false,
								reason: "coordinate-mismatch",
								activeCoordinate: { ...STREAM, runId: "control-run", turn: "0" },
							});
						case 4:
							return Response.json({
								accepted: false,
								reason: "coordinate-mismatch",
								activeCoordinate: { ...STREAM, runId: "control-run", turn: "1" },
							});
						default:
							return Response.json({
								accepted: false,
								reason: "coordinate-mismatch",
								activeCoordinate: { ...STREAM, runId: "foreign-run", turn: "1" },
							});
					}
				}
				throw new Error(`Unexpected Runtime path: ${path}`);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "control-mismatch-input", payload: {} },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 } },
		);

		await expect(session.abort()).resolves.toEqual({
			accepted: false,
			reason: "run-not-active",
			activeCoordinate: { ...STREAM, runId: "control-run", turn: "0" },
		});
		await expect(session.abort()).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		await expect(session.abort()).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		await expect(session.abort()).resolves.toEqual({
			accepted: false,
			reason: "coordinate-mismatch",
			activeCoordinate: { ...STREAM, runId: "control-run", turn: "1" },
		});
		await expect(session.abort()).rejects.toBeInstanceOf(RuntimeRunSessionIdentityError);
		const closeReason = new Error("close-after-control-mismatch");
		session.close(closeReason);
		await expect(session.completion).rejects.toBe(closeReason);
	});

	it("surfaces a durable event gap as an explicit reconciliation failure", async () => {
		const runtimeFetch: typeof fetch = async (input) => {
			const request = input instanceof Request ? input : new Request(input);
			const path = new URL(request.url).pathname;
			if (path === "/runtime/v1/runs") {
				return Response.json(
					{
						runId: "gap-run",
						initialCoordinate: { ...STREAM, runId: "gap-run", turn: "0" },
						eventCursor: "0",
					},
					{ status: 202 },
				);
			}
			if (path === "/runtime/v1/events/stream") {
				return new Response(
					'event: gap\ndata: {"type":"gap","gap":{"kind":"gap","requestedAfterRevision":"0","earliestAvailableRevision":"4","nextAfterRevision":"3","requiresTranscriptReconciliation":true}}\nid: 3\n\n',
					{ headers: { "content-type": "text/event-stream" } },
				);
			}
			throw new Error(`Unexpected Runtime path: ${path}`);
		};
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: runtimeFetch,
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "gap-input", payload: {} },
			{ eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 } },
		);

		await expect(session.completion).rejects.toMatchObject({
			name: "RuntimeRunSessionGapError",
			gap: { earliestAvailableRevision: "4", nextAfterRevision: "3" },
		});
		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionGapError);
	});

	it("rejects invalid session limits before submitting a durable run", async () => {
		let requests = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () => {
				requests += 1;
				return new Response();
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});

		await expect(
			client.startRunSession(
				{ ...STREAM, inputId: "invalid-session-input", payload: {} },
				{ eventRetry: { defaultDelayMs: 0, maxDelayMs: 1, maxAttempts: 1 } },
			),
		).rejects.toThrow("defaultDelayMs must be a positive safe integer");
		expect(requests).toBe(0);
	});

	it("stops local observation on a handler failure without aborting the durable run", async () => {
		const controlled = controlledResponse();
		const client = await clientFixture({ modelStream: controlled.streamFn, newRunId: () => "handler-failure-run" });
		const failure = new Error("game handler failed");
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "handler-failure-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 },
				onEvent: {
					timeoutMs: 1_000,
					handle: (event) => {
						if (event.data.type === "turn.started") throw failure;
					},
				},
			},
		);

		await expect(session.completion).rejects.toBe(failure);
		controlled.finish();
		await vi.waitFor(async () => {
			const replay = await client.readEvents({ ...STREAM, limit: 10 });
			if (replay.data.kind !== "page") throw new Error("Expected a Runtime event page.");
			expect(replay.data.events.at(-1)?.data).toEqual({ type: "run.completed", status: "completed" });
		});
	});

	it("fails the whole session when an enabled Action stream ends before the run", async () => {
		const runtimeFetch: typeof fetch = async (input) => {
			const request = input instanceof Request ? input : new Request(input);
			const path = new URL(request.url).pathname;
			if (path === "/runtime/v1/runs") {
				return Response.json(
					{
						runId: "early-action-eof-run",
						initialCoordinate: { ...STREAM, runId: "early-action-eof-run", turn: "0" },
						eventCursor: "0",
					},
					{ status: 202 },
				);
			}
			if (path === "/runtime/v1/actions/stream") {
				return new Response("", { headers: { "content-type": "text/event-stream" } });
			}
			if (path === "/runtime/v1/events/stream") {
				return admittedOpenEventStream(request, "early-action-eof-run", "early-action-eof-input");
			}
			throw new Error(`Unexpected Runtime path: ${path}`);
		};
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: runtimeFetch,
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const session = await client.startRunSession(
			{ ...STREAM, inputId: "early-action-eof-input", payload: {} },
			{
				eventRetry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
				actions: {
					retry: { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 },
					timeoutMs: 1_000,
					onDelivery: () => undefined,
				},
			},
		);

		await expect(session.completion).rejects.toMatchObject({
			name: "RuntimeRunSessionStreamEndedError",
			stream: "actions",
		});
		await expect(session.completion).rejects.toBeInstanceOf(RuntimeRunSessionStreamEndedError);
	});

	it("reads the authenticated bounded Runtime health projection", async () => {
		const snapshot: RuntimeHealthSnapshot = {
			checkedAt: "2026-08-30T00:00:00.000Z",
			status: "ready",
			dependencies: [
				{
					id: "model",
					kind: "model",
					requirement: "required",
					state: "ready",
					observedAt: "2026-08-30T00:00:00.000Z",
				},
			],
		};
		const client = await clientFixture({ health: { inspect: async () => snapshot } });
		const result = await client.readHealth();
		expect(result.data).toEqual(snapshot);
	});

	it("preserves dangerous URL search keys in deterministic query keys", () => {
		const params = new URLSearchParams();
		params.append("__proto__", "first");
		params.append("__proto__", "second");
		params.append("constructor", "ctor");
		const serialized = serializeQueryKeyValue(params) as Record<string, unknown>;

		expect(Object.getPrototypeOf(serialized)).toBeNull();
		expect(Object.hasOwn(serialized, "__proto__")).toBe(true);
		expect(Reflect.get(serialized, "__proto__")).toEqual(["first", "second"]);
		expect(serialized.constructor).toBe("ctor");
	});

	it("admits HTTPS and exact loopback HTTP endpoints only", () => {
		const create = (baseUrl: string) =>
			new RuntimeClient({
				baseUrl,
				fetch: async () => new Response(),
				maxHttpResponseBytes: 100,
				maxAttachmentResponseBytes: 100,
				maxSseEventBytes: 100,
			});
		expect(() => create("https://runtime.example/sidecar")).not.toThrow();
		expect(() => create("http://localhost:4411")).not.toThrow();
		expect(() => create("http://127.8.9.10:4411")).not.toThrow();
		expect(() => create("http://[::1]:4411")).not.toThrow();
		for (const rejected of [
			"http://runtime.example",
			"https://user@runtime.example",
			"https://runtime.example?token=x",
			"https://runtime.example#fragment",
			"https:\\runtime.example",
		]) {
			expect(() => create(rejected)).toThrow(TypeError);
		}
	});

	it("overrides generated redirect settings and rejects defensive redirect evidence", async () => {
		let observedRedirect: string | undefined;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				observedRedirect = (input as Request).redirect;
				const response = new Response('{"protocolVersion":"1","capabilities":[]}', {
					status: 200,
					headers: { "content-type": "application/json" },
				});
				Object.defineProperty(response, "redirected", { value: true });
				return response;
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		await expect(client.initialize()).rejects.toBeInstanceOf(BoundedFetchRedirectError);
		expect(observedRedirect).toBe("manual");
	});

	it("does not send a Runtime request body to a redirect target", async () => {
		let targetRequests = 0;
		const target = await listen(
			createServer((_request, response) => {
				targetRequests += 1;
				response.writeHead(200, { "content-type": "application/json" });
				response.end('{"protocolVersion":"1","capabilities":[]}');
			}),
		);
		const origin = await listen(
			createServer((_request, response) => {
				response.writeHead(307, { location: `${target.url}/redirected` });
				response.end();
			}),
		);
		try {
			const client = new RuntimeClient({
				baseUrl: origin.url,
				maxHttpResponseBytes: 1_000,
				maxAttachmentResponseBytes: 1_000,
				maxSseEventBytes: 1_000,
			});
			await expect(client.initialize()).rejects.toBeInstanceOf(BoundedFetchRedirectError);
			expect(targetRequests).toBe(0);
		} finally {
			await Promise.all([close(origin.server), close(target.server)]);
		}
	});

	it("does not wait for non-cooperative redirect-response cleanup before rejecting", async () => {
		let cancellations = 0;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(Uint8Array.of(1));
			},
			cancel() {
				cancellations += 1;
				return new Promise(() => undefined);
			},
		});
		const bounded = createBoundedFetch(
			async () => new Response(body, { status: 307, headers: { location: "https://redirect.invalid" } }),
			100,
			100,
		);

		await expect(settleBeforeDeadline(bounded("https://runtime.invalid"))).rejects.toBeInstanceOf(
			BoundedFetchRedirectError,
		);
		expect(cancellations).toBe(1);
	});

	it("does not wait for non-cooperative cleanup of a declared oversized response", async () => {
		let cancellations = 0;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(Uint8Array.of(1));
			},
			cancel() {
				cancellations += 1;
				return new Promise(() => undefined);
			},
		});
		const bounded = createBoundedFetch(
			async () => new Response(body, { headers: { "content-length": "101" } }),
			100,
			100,
		);

		await expect(settleBeforeDeadline(bounded("https://runtime.invalid"))).rejects.toMatchObject({
			kind: "http-response",
		});
		expect(cancellations).toBe(1);
	});

	it("preserves a selector failure while detaching response cleanup", async () => {
		const sentinel = new Error("selector failed");
		let cancellations = 0;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(Uint8Array.of(1));
			},
			cancel() {
				cancellations += 1;
				return new Promise(() => undefined);
			},
		});
		const bounded = createBoundedFetch(
			async () => new Response(body),
			100,
			100,
			undefined,
			() => {
				throw sentinel;
			},
		);

		await expect(settleBeforeDeadline(bounded("https://runtime.invalid"))).rejects.toBe(sentinel);
		expect(cancellations).toBe(1);
	});

	it("preserves a capacity-error factory failure while detaching declared-length cleanup", async () => {
		const sentinel = new Error("capacity factory failed");
		let cancellations = 0;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(Uint8Array.of(1));
			},
			cancel() {
				cancellations += 1;
				return new Promise(() => undefined);
			},
		});
		const bounded = createBoundedFetch(
			async () => new Response(body, { headers: { "content-length": "101" } }),
			100,
			100,
			() => {
				throw sentinel;
			},
		);

		await expect(settleBeforeDeadline(bounded("https://runtime.invalid"))).rejects.toBe(sentinel);
		expect(cancellations).toBe(1);
	});

	it("can force a total-body limit when an untrusted response claims SSE", async () => {
		const bounded = createBoundedFetch(
			async () => new Response("a\n\na\n\na\n\n", { headers: { "content-type": "text/event-stream" } }),
			4,
			4,
			undefined,
			undefined,
			undefined,
			"total-body",
		);
		const response = await bounded("https://runtime.invalid");
		await expect(response.text()).rejects.toMatchObject({ kind: "http-response" });
	});

	it("does not treat an SSE token inside another media type parameter as SSE", async () => {
		const bounded = createBoundedFetch(
			async () =>
				new Response("a\n\na\n\na\n\n", {
					headers: { "content-type": "application/json; note=text/event-stream" },
				}),
			4,
			4,
		);
		const response = await bounded("https://runtime.invalid");
		await expect(response.text()).rejects.toMatchObject({ kind: "http-response" });
	});

	it("keeps parameterized event streams on the per-event limit", async () => {
		const bounded = createBoundedFetch(
			async () =>
				new Response("a\n\na\n\na\n\n", {
					headers: { "content-type": "text/event-stream; charset=utf-8" },
				}),
			4,
			4,
		);
		expect(await (await bounded("https://runtime.invalid")).text()).toBe("a\n\na\n\na\n\n");
	});

	it("diagnoses an invalid bounded-fetch response mode", () => {
		expect(() => createBoundedFetch(fetch, 4, 4, undefined, undefined, undefined, "invalid" as never)).toThrow(
			"responseMode",
		);
	});

	it("rejects an invalid HTTP limit selector before calling upstream", () => {
		let calls = 0;
		const upstream: typeof fetch = async () => {
			calls += 1;
			return new Response("body");
		};
		expect(() => createBoundedFetch(upstream, 4, 4, undefined, "invalid" as never)).toThrow("selectHttpLimit");
		expect(calls).toBe(0);
	});

	it("cancels a response body when a selected HTTP budget is invalid", async () => {
		let cancelled = false;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(Uint8Array.of(1));
			},
			cancel() {
				cancelled = true;
			},
		});
		const bounded = createBoundedFetch(
			async () => new Response(body),
			100,
			100,
			undefined,
			() => 0,
		);

		await expect(bounded("https://runtime.invalid")).rejects.toThrow(TypeError);
		expect(cancelled).toBe(true);
	});

	it("round-trips durable action exchange and exact approval decisions", async () => {
		const now = () => new Date("2026-08-28T00:00:00.000Z");
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now,
			newClaimToken: () => "claim-token",
		});
		const intent = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "run", turn: "1" },
			actionName: "world.write",
			operationKey: "operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
		});
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), { now });
		const approvalIntent = prepareActionIntent({
			...STREAM,
			coordinate: { ...STREAM, runId: "run", turn: "1" },
			actionName: "world.write",
			operationKey: "approved-operation",
			payload: { value: 2 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:2"],
			replayPolicy: "never",
		});
		const pending = await approvals.request("high-risk", approvalIntent, 1_000, {
			presentation: {
				value: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
				limits: { maxBytes: 256, maxDepth: 2 },
			},
		});
		const client = await clientFixture({ actions, approvals });

		await expect(client.recoverAction(STREAM)).resolves.toMatchObject({ data: { delivery: null } });
		const next = await client.nextAction(STREAM);
		if (next.data.delivery?.kind !== "dispatch" || !next.data.delivery.action.claim) {
			throw new Error("Expected a dispatched action.");
		}
		expect(next.data.delivery.action.operationId).toBe(intent.operationId);
		await expect(client.recoverAction(STREAM)).resolves.toMatchObject({
			data: {
				delivery: {
					kind: "reconcile",
					action: {
						operationId: intent.operationId,
						attempt: next.data.delivery.action.attempt,
						claim: next.data.delivery.action.claim,
					},
				},
			},
		});
		await expect(
			client.recordActionReceipt({
				...STREAM,
				operationId: intent.operationId,
				claimToken: next.data.delivery.action.claim.claimToken,
				attempt: next.data.delivery.action.claim.attempt,
				outcome: "succeeded",
				result: { visible: "done" },
			}),
		).resolves.toMatchObject({ data: { action: { status: "succeeded" } } });
		await expect(client.getAction({ ...STREAM, operationId: intent.operationId })).resolves.toMatchObject({
			data: { action: { receipt: { result: { visible: "done" } } } },
		});

		const listedApprovals = await client.listApprovals({ ...STREAM, limit: 10 });
		expect(listedApprovals.data.approvals[0]?.request.presentation).toEqual({
			kind: "game-choice",
			optionId: "opaque-option-7",
			label: "赠送礼物",
		});
		const decidedApproval = await client.decideApproval({
			...STREAM,
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
		});
		expect(decidedApproval.data.approval).toMatchObject({
			status: "approved",
			request: {
				presentation: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
			},
		});
	});

	it("rejects a dispatch returned by the recovery-only endpoint", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const action = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "run", turn: "1" },
			actionName: "world.write",
			operationKey: "operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
		});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () => Response.json({ delivery: { kind: "dispatch", action } }),
			maxHttpResponseBytes: 10_000,
			maxAttachmentResponseBytes: 10_000,
			maxSseEventBytes: 10_000,
		});

		await expect(client.recoverAction(STREAM)).rejects.toBeInstanceOf(RuntimeProtocolValidationError);
	});

	it("round-trips initialization and event paging through the Hono server", async () => {
		const client = await clientFixture();
		const initialized = await client.initialize();
		expect(initialized.data).toMatchObject({ protocolVersion: "1" });

		const replay = await client.readEvents({ ...STREAM, afterRevision: "0", limit: 10 });
		expect(replay.data).toEqual({
			kind: "page",
			requestedAfterRevision: "0",
			requiresTranscriptReconciliation: false,
			events: [],
			cursor: "0",
			hasMore: false,
		});
	});

	it("sends each deferred lifecycle request once through the generated transport", async () => {
		const requests: Array<{ path: string; body: unknown }> = [];
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				requests.push({ path: new URL(request.url).pathname, body: await request.json() });
				switch (new URL(request.url).pathname) {
					case "/runtime/v1/deferred/read":
						return Response.json({ deferred: null });
					case "/runtime/v1/deferred/resume":
						return Response.json(
							{
								runId: "run",
								initialCoordinate: { ...STREAM, runId: "run", turn: "0" },
								eventCursor: "0",
							},
							{ status: 202 },
						);
					case "/runtime/v1/deferred/abandon":
						return Response.json({ operationId: "operation", providerCancellation: "completed" });
					default:
						throw new Error("Unexpected Runtime request.");
				}
			},
			maxHttpResponseBytes: 10_000,
			maxAttachmentResponseBytes: 10_000,
			maxSseEventBytes: 10_000,
		});

		await expect(client.readDeferred(STREAM)).resolves.toMatchObject({ data: { deferred: null } });
		await expect(client.resumeDeferred({ ...STREAM, operationId: "operation" })).resolves.toMatchObject({
			data: { runId: "run" },
		});
		await expect(client.abandonDeferred({ ...STREAM, operationId: "operation" })).resolves.toMatchObject({
			data: { providerCancellation: "completed" },
		});
		expect(requests).toEqual([
			{ path: "/runtime/v1/deferred/read", body: STREAM },
			{ path: "/runtime/v1/deferred/resume", body: { ...STREAM, operationId: "operation" } },
			{ path: "/runtime/v1/deferred/abandon", body: { ...STREAM, operationId: "operation" } },
		]);
	});

	it("reads an authorized attachment as integrity-checked binary data", async () => {
		const client = await clientFixture({
			attachments: {
				maxResponseBytes: ATTACHMENT_BYTES.byteLength,
				authorize: () => true,
				read: async () => ATTACHMENT_BYTES,
			},
		});
		const initialized = await client.initialize();
		expect(initialized.data.capabilities).toContain("attachments.read");
		const result = await client.readAttachment({ ...STREAM, reference: ATTACHMENT_REFERENCE });
		expect([...result.data]).toEqual([...ATTACHMENT_BYTES]);
		expect(result.response.headers.get("cache-control")).toBe("private, no-store");
	});

	it("rejects attachment response metadata or digest tampering", async () => {
		const digest = createHash("sha256").update(ATTACHMENT_BYTES).digest("base64");
		const responseHeaders = {
			"cache-control": "private, no-store",
			"content-digest": `sha-256=:${digest}:`,
			"content-length": String(ATTACHMENT_BYTES.byteLength),
			"content-type": "image/png",
			etag: `"${ATTACHMENT_REFERENCE.attachmentId}"`,
			"x-content-type-options": "nosniff",
		};
		const metadataClient = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () =>
				new Response(ATTACHMENT_BYTES, { status: 200, headers: { ...responseHeaders, etag: '"wrong"' } }),
			maxHttpResponseBytes: 100,
			maxAttachmentResponseBytes: 100,
			maxSseEventBytes: 100,
		});
		await expect(metadataClient.readAttachment({ ...STREAM, reference: ATTACHMENT_REFERENCE })).rejects.toMatchObject({
			code: "invalid_response",
		});

		const digestClient = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () =>
				new Response(ATTACHMENT_BYTES, {
					status: 200,
					headers: { ...responseHeaders, "content-digest": "sha-256=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:" },
				}),
			maxHttpResponseBytes: 100,
			maxAttachmentResponseBytes: 100,
			maxSseEventBytes: 100,
		});
		await expect(digestClient.readAttachment({ ...STREAM, reference: ATTACHMENT_REFERENCE })).rejects.toBeInstanceOf(
			RuntimeAttachmentIntegrityError,
		);
	});

	it("applies the attachment byte budget to malformed successful attachment responses", async () => {
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () =>
				new Response(ATTACHMENT_BYTES, {
					status: 200,
					headers: {
						"content-length": String(ATTACHMENT_BYTES.byteLength),
						"content-type": "application/octet-stream",
					},
				}),
			maxHttpResponseBytes: 100,
			maxAttachmentResponseBytes: ATTACHMENT_BYTES.byteLength - 1,
			maxSseEventBytes: 100,
		});
		await expect(client.readAttachment({ ...STREAM, reference: ATTACHMENT_REFERENCE })).rejects.toMatchObject({
			kind: "http-response",
		});
	});

	it("consumes the generated SSE path with host-supplied retry bounds", async () => {
		const client = await clientFixture();
		const abort = new AbortController();
		const result = await client.streamEvents(
			{ ...STREAM, afterRevision: "0" },
			{ defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 1 },
			abort.signal,
		);
		const next = await result.stream.next();
		expect(next.value).toEqual({ type: "cursor", revision: "0" });
		abort.abort();
		await result.stream.return();
	});

	it("reconnects a cleanly ended event stream from the last delivered durable cursor", async () => {
		const lastEventIds: Array<string | null> = [];
		const bodies = [
			'event: cursor\ndata: {"type":"cursor","revision":"4"}\nid: 4\n\n',
			'event: cursor\ndata: {"type":"cursor","revision":"5"}\nid: 5\n\n',
		];
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				lastEventIds.push(request.headers.get("Last-Event-ID"));
				return new Response(bodies.shift(), {
					status: 200,
					headers: { "content-type": "text/event-stream" },
				});
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const result = await client.streamEvents(STREAM, { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 2 });
		const revisions: string[] = [];

		let failure: unknown;
		try {
			for await (const frame of result.stream) {
				if (frame.type === "cursor") revisions.push(frame.revision);
			}
		} catch (error) {
			failure = error;
		}

		expect(revisions).toEqual(["4", "5"]);
		expect(lastEventIds).toEqual([null, "4"]);
		expect(failure).toBeInstanceOf(RuntimeSseDisconnectedError);
		expect(failure).toMatchObject({ connectionAttempts: 2 });
	});

	it("caps a server-provided clean-EOF retry delay at the host maximum", async () => {
		vi.useFakeTimers();
		try {
			let requests = 0;
			const client = new RuntimeClient({
				baseUrl: "https://runtime.invalid",
				fetch: async () => {
					requests += 1;
					const body =
						requests === 1
							? 'retry: 9\nevent: cursor\ndata: {"type":"cursor","revision":"4"}\nid: 4\n\n'
							: 'event: gap\ndata: {"type":"gap","gap":{"kind":"gap","requestedAfterRevision":"4","earliestAvailableRevision":null,"nextAfterRevision":"4","requiresTranscriptReconciliation":true}}\nid: 4\n\n';
					return new Response(body, {
						status: 200,
						headers: { "content-type": "text/event-stream" },
					});
				},
				maxHttpResponseBytes: 1_000,
				maxAttachmentResponseBytes: 1_000,
				maxSseEventBytes: 1_000,
			});
			const result = await client.streamEvents(STREAM, { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 2 });
			await expect(result.stream.next()).resolves.toMatchObject({ value: { type: "cursor", revision: "4" } });

			const reconnect = result.stream.next();
			await vi.advanceTimersByTimeAsync(1);
			expect(requests).toBe(1);
			await vi.advanceTimersByTimeAsync(1);
			await expect(reconnect).resolves.toMatchObject({ value: { type: "gap" } });
			expect(requests).toBe(2);
			await expect(result.stream.next()).resolves.toEqual({ done: true, value: undefined });
		} finally {
			vi.useRealTimers();
		}
	});

	it("ends an event subscription when aborted during clean-EOF backoff", async () => {
		vi.useFakeTimers();
		let pending: Promise<unknown> | undefined;
		try {
			let requests = 0;
			const client = new RuntimeClient({
				baseUrl: "https://runtime.invalid",
				fetch: async () => {
					requests += 1;
					return new Response('event: cursor\ndata: {"type":"cursor","revision":"4"}\nid: 4\n\n', {
						status: 200,
						headers: { "content-type": "text/event-stream" },
					});
				},
				maxHttpResponseBytes: 1_000,
				maxAttachmentResponseBytes: 1_000,
				maxSseEventBytes: 1_000,
			});
			const abort = new AbortController();
			const result = await client.streamEvents(
				STREAM,
				{ defaultDelayMs: 100, maxDelayMs: 100, maxAttempts: 2 },
				abort.signal,
			);
			await result.stream.next();
			let settled = false;
			let outcome: unknown;
			pending = result.stream.next().then(
				(value) => {
					settled = true;
					outcome = value;
				},
				(error: unknown) => {
					settled = true;
					outcome = error;
				},
			);
			await vi.advanceTimersByTimeAsync(0);

			abort.abort();
			await vi.advanceTimersByTimeAsync(0);

			expect(settled).toBe(true);
			expect(outcome).toEqual({ done: true, value: undefined });
			expect(requests).toBe(1);
		} finally {
			await vi.runOnlyPendingTimersAsync();
			await pending;
			vi.useRealTimers();
		}
	});

	it("advances the reconnect cursor when EOF lands between an event frame and the next cursor frame", async () => {
		const lastEventIds: Array<string | null> = [];
		const bodies = [
			'event: event\ndata: {"type":"event","event":{"protocolVersion":"1","eventId":"event-7","sequence":"7","revision":"7","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}\nid: 7\n\n',
			'event: cursor\ndata: {"type":"cursor","revision":"8"}\nid: 8\n\n',
		];
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async (input) => {
				const request = input as Request;
				lastEventIds.push(request.headers.get("Last-Event-ID"));
				return new Response(bodies.shift(), {
					status: 200,
					headers: { "content-type": "text/event-stream" },
				});
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const result = await client.streamEvents(STREAM, { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 2 });
		let received = 0;

		await expect(
			(async () => {
				for await (const _frame of result.stream) received += 1;
			})(),
		).rejects.toBeInstanceOf(RuntimeSseDisconnectedError);
		expect(received).toBe(2);
		expect(lastEventIds).toEqual([null, "7"]);
	});

	it("treats a reconciliation gap as an explicit terminal frame instead of reconnecting", async () => {
		let requests = 0;
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () => {
				requests += 1;
				return new Response(
					'event: gap\ndata: {"type":"gap","gap":{"kind":"gap","requestedAfterRevision":"9","earliestAvailableRevision":null,"nextAfterRevision":"0","requiresTranscriptReconciliation":true}}\nid: 0\n\n',
					{ status: 200, headers: { "content-type": "text/event-stream" } },
				);
			},
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const result = await client.streamEvents(STREAM, { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 2 });
		const frames = [];

		for await (const frame of result.stream) frames.push(frame);

		expect(frames).toHaveLength(1);
		expect(frames[0]).toMatchObject({ type: "gap", gap: { requiresTranscriptReconciliation: true } });
		expect(requests).toBe(1);
	});

	it("does not dispatch an incomplete SSE event when CRLF is split across chunks", async () => {
		const encoder = new TextEncoder();
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: async () =>
				new Response(
					new ReadableStream<Uint8Array>({
						start(controller) {
							controller.enqueue(encoder.encode('data: {"type":"cursor","revision":"9"}\r'));
							controller.enqueue(encoder.encode("\n"));
							controller.close();
						},
					}),
					{ status: 200, headers: { "content-type": "text/event-stream" } },
				),
			maxHttpResponseBytes: 1_000,
			maxAttachmentResponseBytes: 1_000,
			maxSseEventBytes: 1_000,
		});
		const result = await client.streamEvents(STREAM, { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 });

		await expect(result.stream.next()).rejects.toBeInstanceOf(RuntimeSseDisconnectedError);
	});

	it("rejects retry delays that Node timers would collapse to one millisecond", async () => {
		const client = await clientFixture();
		await expect(
			client.streamEvents(STREAM, {
				defaultDelayMs: 1,
				maxDelayMs: 2_147_483_648,
				maxAttempts: 1,
			}),
		).rejects.toThrow("must not exceed 2147483647 milliseconds");
		const result = await client.streamEvents(STREAM, {
			defaultDelayMs: 2_147_483_647,
			maxDelayMs: 2_147_483_647,
			maxAttempts: 1,
		});
		await result.stream.return();
	});

	it("snapshots runtime limits and retry policy fields once", async () => {
		const responseHeaders = {
			"cache-control": "private, no-store",
			"content-digest": `sha-256=:${createHash("sha256").update(ATTACHMENT_BYTES).digest("base64")}:`,
			"content-length": String(ATTACHMENT_BYTES.byteLength),
			"content-type": "image/png",
			etag: `"${ATTACHMENT_REFERENCE.attachmentId}"`,
			"x-content-type-options": "nosniff",
		};
		const options: RuntimeClientOptions = {
			baseUrl: "https://runtime.invalid",
			fetch: async () => new Response(ATTACHMENT_BYTES, { status: 200, headers: responseHeaders }),
			maxHttpResponseBytes: 100,
			maxAttachmentResponseBytes: 100,
			maxSseEventBytes: 100,
		};
		const attachmentClient = new RuntimeClient(options);
		options.maxAttachmentResponseBytes = 1;
		await expect(
			attachmentClient.readAttachment({ ...STREAM, reference: ATTACHMENT_REFERENCE }),
		).resolves.toMatchObject({
			data: ATTACHMENT_BYTES,
		});

		const reads = { defaultDelayMs: 0, maxDelayMs: 0, maxAttempts: 0 };
		const values = { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 1 };
		const retry = {} as RuntimeSseRetryPolicy;
		for (const key of Object.keys(values) as (keyof RuntimeSseRetryPolicy)[]) {
			Object.defineProperty(retry, key, {
				get() {
					reads[key] += 1;
					return values[key];
				},
			});
		}
		const client = await clientFixture();
		const pending = client.streamEvents(STREAM, retry);
		values.maxDelayMs = 2_147_483_648;
		const result = await pending;
		await result.stream.return();
		expect(reads).toEqual({ defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 });
	});

	it("validates complete SSE lines before releasing them", async () => {
		const encoder = new TextEncoder();
		const lines: { readonly text: string; readonly first: boolean }[] = [];
		const upstream: typeof fetch = async () =>
			new Response(
				new ReadableStream<Uint8Array>({
					start(controller) {
						controller.enqueue(encoder.encode("retry: 2\r"));
						controller.enqueue(encoder.encode("\n\r\ndata: {}\n\n"));
						controller.close();
					},
				}),
				{ headers: { "content-type": "text/event-stream" } },
			);
		const bounded = createBoundedFetch(upstream, 100, 100, undefined, undefined, (line, first) => {
			lines.push({ text: new TextDecoder().decode(line), first });
		});

		expect(await (await bounded("https://runtime.invalid/events")).text()).toBe("retry: 2\r\n\r\ndata: {}\n\n");
		expect(lines).toEqual([
			{ text: "retry: 2\r\n", first: true },
			{ text: "\r\n", first: false },
			{ text: "data: {}\n", first: false },
			{ text: "\n", first: false },
		]);
	});

	it("aggregates validated SSE lines from one upstream chunk before enqueueing", async () => {
		const encoder = new TextEncoder();
		const body = "a\n\n".repeat(1_000);
		let validations = 0;
		const bounded = createBoundedFetch(
			async () => new Response(encoder.encode(body), { headers: { "content-type": "text/event-stream" } }),
			100,
			4,
			undefined,
			undefined,
			() => {
				validations += 1;
			},
		);
		const reader = (await bounded("https://runtime.invalid/events")).body?.getReader();
		if (!reader) throw new Error("Expected an SSE response body.");
		const first = await reader.read();
		expect(first).toMatchObject({ done: false });
		expect(first.value?.byteLength).toBe(encoder.encode(body).byteLength);
		expect(validations).toBe(2_000);
		await expect(reader.read()).resolves.toMatchObject({ done: true });
	});

	it("releases a validated SSE prefix before a later line in the same chunk fails", async () => {
		const encoder = new TextEncoder();
		const sentinel = new Error("invalid retry");
		let cancellations = 0;
		const upstream = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(encoder.encode("data: ok\n\nretry: bad\n"));
			},
			cancel() {
				cancellations += 1;
			},
		});
		const bounded = createBoundedFetch(
			async () => new Response(upstream, { headers: { "content-type": "text/event-stream" } }),
			100,
			100,
			undefined,
			undefined,
			(line) => {
				if (new TextDecoder().decode(line).startsWith("retry:")) throw sentinel;
			},
		);
		const reader = (await bounded("https://runtime.invalid/events")).body?.getReader();
		if (!reader) throw new Error("Expected an SSE response body.");
		const first = await reader.read();
		expect(new TextDecoder().decode(first.value)).toBe("data: ok\n\n");
		expect(first.value?.buffer.byteLength).toBe(first.value?.byteLength);
		await expect(reader.read()).rejects.toBe(sentinel);
		await vi.waitFor(() => expect(cancellations).toBe(1));
	});

	it("recognizes every SSE line-ending pair across chunks and resets each event budget", async () => {
		const encoder = new TextEncoder();
		const boundaries = ["\n\n", "\n\r", "\n\r\n", "\r\r", "\r\r\n", "\r\n\n", "\r\n\r", "\r\n\r\n"];
		for (const boundary of boundaries) {
			const first = `data: one${boundary}`;
			const second = "data: two\n\n";
			const bytes = encoder.encode(first + second);
			const upstream: typeof fetch = async () =>
				new Response(
					new ReadableStream<Uint8Array>({
						start(controller) {
							for (const byte of bytes) controller.enqueue(Uint8Array.of(byte));
							controller.close();
						},
					}),
					{ headers: { "content-type": "text/event-stream" } },
				);
			const eventLimit = Math.max(encoder.encode(first).byteLength, encoder.encode(second).byteLength);
			const bounded = createBoundedFetch(upstream, 100, eventLimit);
			expect(await (await bounded("https://runtime.invalid/events")).text()).toBe(first + second);
		}
	});

	it("counts the complete SSE delimiter before resetting the event budget", async () => {
		const body = "data: x\n\n";
		const upstream: typeof fetch = async () => new Response(body, { headers: { "content-type": "text/event-stream" } });
		const bounded = createBoundedFetch(upstream, 100, new TextEncoder().encode(body).byteLength - 1);
		await expect((await bounded("https://runtime.invalid/events")).text()).rejects.toMatchObject({
			kind: "sse-event",
		});
	});

	it("validates terminated SSE fields but leaves an unterminated EOF tail to the upstream parser", async () => {
		const validate = (line: Uint8Array) => {
			if (new TextDecoder().decode(line).startsWith("retry:")) throw new Error("rejected retry");
		};
		for (const tail of ["retry: 3", "retry: 3\r"]) {
			const unterminated = createBoundedFetch(
				async () => new Response(tail, { headers: { "content-type": "text/event-stream" } }),
				100,
				100,
				undefined,
				undefined,
				validate,
			);
			expect(await (await unterminated("https://runtime.invalid/events")).text()).toBe(tail);
		}

		const terminated = createBoundedFetch(
			async () => new Response("retry: 3\n", { headers: { "content-type": "text/event-stream" } }),
			100,
			100,
			undefined,
			undefined,
			validate,
		);
		const reader = (await terminated("https://runtime.invalid/events")).body?.getReader();
		if (!reader) throw new Error("Expected an SSE response body.");
		await expect(reader.read()).rejects.toThrow("rejected retry");
	});

	it("types durable action dispatch and reconnect reconciliation without a second claim", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
			newClaimToken: () => "stream-claim",
		});
		const prepared = await actions.submit({
			...STREAM,
			coordinate: { ...STREAM, runId: "run", turn: "1" },
			actionName: "world.write",
			operationKey: "stream-operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
		});
		const client = await clientFixture({ actions });
		const retry = { defaultDelayMs: 1, maxDelayMs: 2, maxAttempts: 1 };

		const firstAbort = new AbortController();
		const first = await client.streamActions(STREAM, retry, firstAbort.signal);
		await expect(first.stream.next()).resolves.toMatchObject({
			value: { type: "delivery", delivery: { kind: "dispatch", action: { operationId: prepared.operationId } } },
		});
		firstAbort.abort();
		await first.stream.return();

		const secondAbort = new AbortController();
		const second = await client.streamActions(STREAM, retry, secondAbort.signal);
		await expect(second.stream.next()).resolves.toMatchObject({
			value: { type: "delivery", delivery: { kind: "reconcile", action: { attempt: "1" } } },
		});
		secondAbort.abort();
		await second.stream.return();
		expect(await actions.get(STREAM, prepared.operationId)).toMatchObject({ attempt: "1", status: "claimed" });
	});

	it("fails closed when a non-streaming response exceeds the client byte budget", async () => {
		const oversizedFetch: typeof fetch = async () =>
			new Response(JSON.stringify({ protocolVersion: "1", capabilities: ["events.page"] }), {
				status: 200,
				headers: { "content-type": "application/json" },
			});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: oversizedFetch,
			maxHttpResponseBytes: 8,
			maxAttachmentResponseBytes: 100,
			maxSseEventBytes: 100,
		});
		await expect(client.initialize()).rejects.toMatchObject({ kind: "http-response" });
	});

	it("requires an explicit positive attachment response byte budget", () => {
		expect(
			() =>
				new RuntimeClient({
					baseUrl: "https://runtime.invalid",
					fetch: async () => new Response(),
					maxHttpResponseBytes: 100,
					maxAttachmentResponseBytes: 0,
					maxSseEventBytes: 100,
				}),
		).toThrow("positive platform-supported byte length");
	});

	it("surfaces an oversized SSE record after the explicit retry budget is exhausted", async () => {
		const oversizedSseFetch: typeof fetch = async () =>
			new Response(`data: ${"x".repeat(100)}\n\n`, {
				status: 200,
				headers: { "content-type": "text/event-stream" },
			});
		const client = new RuntimeClient({
			baseUrl: "https://runtime.invalid",
			fetch: oversizedSseFetch,
			maxHttpResponseBytes: 100,
			maxAttachmentResponseBytes: 100,
			maxSseEventBytes: 16,
		});
		const result = await client.streamEvents({ ...STREAM }, { defaultDelayMs: 1, maxDelayMs: 1, maxAttempts: 1 });
		let failure: unknown;
		try {
			await result.stream.next();
		} catch (error) {
			failure = error;
		}
		expect(failure).toBeInstanceOf(RuntimeSseDisconnectedError);
		expect(failure).toMatchObject({
			connectionAttempts: 1,
			cause: expect.any(RuntimeClientResponseLimitError),
		});
	});
});

async function settleBeforeDeadline<T>(pending: Promise<T>): Promise<T> {
	let timeout: ReturnType<typeof setTimeout> | undefined;
	try {
		return await Promise.race([
			pending,
			new Promise<T>((_resolve, reject) => {
				timeout = setTimeout(() => reject(new Error("bounded fetch did not settle before cleanup")), 100);
			}),
		]);
	} finally {
		if (timeout) clearTimeout(timeout);
	}
}

async function listen(server: Server): Promise<{ readonly server: Server; readonly url: string }> {
	await new Promise<void>((resolve, reject) => {
		server.once("error", reject);
		server.listen(0, "127.0.0.1", () => resolve());
	});
	const address = server.address();
	if (address === null || typeof address === "string") throw new Error("Loopback server did not expose a TCP port.");
	return { server, url: `http://127.0.0.1:${address.port}` };
}

async function close(server: Server): Promise<void> {
	await new Promise<void>((resolve, reject) => {
		server.close((error) => (error ? reject(error) : resolve()));
	});
}
