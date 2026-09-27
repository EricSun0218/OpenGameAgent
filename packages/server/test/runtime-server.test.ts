import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { Model } from "@earendil-works/pi-ai";
import {
	ActionApprovalCoordinator,
	ActionCoordinator,
	InMemoryActionApprovalStore,
	InMemoryActionJournal,
	prepareActionIntent,
} from "@opengameagent/actions";
import {
	DurableTranscript,
	type GameDeferredResponseAdapter,
	InMemoryTranscriptStore,
	type TranscriptStore,
} from "@opengameagent/agent";
import type {
	CanonicalEvent,
	CanonicalEventData,
	ImageReferenceContent,
	RunCoordinate,
	RuntimeHealthSnapshot,
	StreamKey,
} from "@opengameagent/protocol";
import { JsonValueLimitError, WorkAuthorityRetirementConflictError } from "@opengameagent/protocol";
import { GoalAuthorityRetirementConflictError } from "@opengameagent/extensions";
import { GameRuntime, type GameRuntimeLimits, InMemoryEventStore } from "@opengameagent/runtime";
import { describe, expect, it, vi } from "vitest";
import { assistant, controlledResponse, deferred, messageResponse } from "../../runtime/test/pi-fakes.js";
import {
	type AuthorizedRuntimeStream,
	createRuntimeServer,
	type RuntimeAccessController,
	type RuntimeAttachmentService,
	type RuntimeServerHealthService,
	type RuntimeServerLimits,
} from "../src/index.js";

const STREAM: StreamKey = { sessionId: "session", actorId: "actor" };
const COORDINATE: RunCoordinate = { ...STREAM, runId: "run", turn: "0" };
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
const NEVER_CALLED: StreamFn = () => {
	throw new Error("The provider should not be called by this test.");
};
const ATTACHMENT_BYTES = Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10]);
const ATTACHMENT_REFERENCE: ImageReferenceContent = {
	type: "image-reference",
	attachmentId: `img_${createHash("sha256").update(ATTACHMENT_BYTES).digest("hex")}`,
	mediaType: "image/png",
	bytes: ATTACHMENT_BYTES.byteLength,
	width: 1,
	height: 1,
};
const RESPONSE_TOO_LARGE = {
	error: { code: "response_too_large", message: "The response exceeds the configured byte limit." },
};
const MINIMUM_RESPONSE_BYTES = Buffer.byteLength(JSON.stringify(RESPONSE_TOO_LARGE));
const SERVER_LIMITS: RuntimeServerLimits = {
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
};

class BlockingEventStore extends InMemoryEventStore {
	blockedType: CanonicalEventData["type"] | undefined;

	override append(...parameters: Parameters<InMemoryEventStore["append"]>) {
		if (parameters[3].type === this.blockedType) throw new Error("event sink blocked");
		return super.append(...parameters);
	}
}

class ReadGatedEventStore extends InMemoryEventStore {
	readGate:
		| Readonly<{
				entered: () => void;
				release: Promise<void>;
		  }>
		| undefined;

	override async read(...parameters: Parameters<InMemoryEventStore["read"]>) {
		const gate = this.readGate;
		this.readGate = undefined;
		const page = await super.read(...parameters);
		if (gate) {
			gate.entered();
			await gate.release;
		}
		return page;
	}
}

interface FixtureOptions {
	authorizeGoalRetirement?: RuntimeAccessController["authorizeGoalRetirement"];
	authorizeWorkRetirement?: RuntimeAccessController["authorizeWorkRetirement"];
	authenticated?: boolean;
	access?: AuthorizedRuntimeStream | null;
	modelStream?: StreamFn;
	actions?: ActionCoordinator;
	approvals?: ActionApprovalCoordinator;
	attachments?: RuntimeAttachmentService;
	health?: RuntimeServerHealthService;
	limits?: RuntimeServerLimits;
	inputLimits?: Pick<GameRuntimeLimits, "maxInputBytes" | "maxInputDepth">;
	transcript?: DurableTranscript;
	deferredResponses?: GameDeferredResponseAdapter;
	eventStore?: InMemoryEventStore;
}

async function fixture(fixtureOptions: FixtureOptions = {}) {
	let nextEvent = 0;
	const eventStore =
		fixtureOptions.eventStore ??
		new InMemoryEventStore({
			newEventId: () => {
				nextEvent += 1;
				return `event-${nextEvent}`;
			},
			now: () => "2026-08-28T00:00:00.000Z",
		});
	const runtime = new GameRuntime(eventStore, {
		maxActiveActors: 1,
		maxQueuedRuns: 4,
		maxInputBytes: 4_096,
		maxInputDepth: 8,
		...fixtureOptions.inputLimits,
		...(fixtureOptions.transcript === undefined ? {} : { transcript: fixtureOptions.transcript }),
		newRunId: () => "submitted-run",
	});
	await runtime.registerActor({
		...STREAM,
		model: MODEL,
		modelStream: fixtureOptions.modelStream ?? NEVER_CALLED,
		...(fixtureOptions.deferredResponses === undefined ? {} : { deferredResponses: fixtureOptions.deferredResponses }),
	});
	let authorizeCalls = 0;
	const controller: RuntimeAccessController = {
		...(fixtureOptions.authorizeWorkRetirement === undefined
			? {}
			: { authorizeWorkRetirement: fixtureOptions.authorizeWorkRetirement }),
		...(fixtureOptions.authorizeGoalRetirement === undefined
			? {}
			: { authorizeGoalRetirement: fixtureOptions.authorizeGoalRetirement }),
		authenticate: () => (fixtureOptions.authenticated === false ? undefined : { principalId: "player" }),
		authorize: () => {
			authorizeCalls += 1;
			if (fixtureOptions.access === null) return undefined;
			return (
				fixtureOptions.access ?? {
					stream: STREAM,
					isOwner: true,
					recipientIds: ["player"],
					allowInternal: false,
				}
			);
		},
	};
	const app = createRuntimeServer({
		runtime,
		access: controller,
		...(fixtureOptions.actions === undefined ? {} : { actions: fixtureOptions.actions }),
		...(fixtureOptions.approvals === undefined ? {} : { approvals: fixtureOptions.approvals }),
		...(fixtureOptions.attachments === undefined ? {} : { attachments: fixtureOptions.attachments }),
		...(fixtureOptions.health === undefined ? {} : { health: fixtureOptions.health }),
		limits: fixtureOptions.limits ?? SERVER_LIMITS,
	});
	return { app, eventStore, runtime, authorizeCalls: () => authorizeCalls };
}

function transcript() {
	return new DurableTranscript(new InMemoryTranscriptStore(), {
		maxRecordBytes: 16_384,
		maxCommitBytes: 65_536,
		maxRestoreBytes: 131_072,
		maxJsonDepth: 16,
	});
}

const DEFERRED_HANDLE = {
	provider: MODEL.provider,
	modelId: MODEL.id,
	api: MODEL.api,
	id: "provider-operation",
} as const;

const deferredResponse = () => messageResponse({ ...assistant([], "deferred"), deferred: DEFERRED_HANDLE });

function postJson(app: ReturnType<typeof createRuntimeServer>, path: string, body: unknown) {
	return app.request(path, {
		method: "POST",
		headers: { authorization: "Bearer test", "content-type": "application/json" },
		body: JSON.stringify(body),
	});
}

describe("runtime input admission limits", () => {
	it("keeps the response budget authoritative for input rejection diagnostics", async () => {
		const modelStream = vi.fn(NEVER_CALLED);
		const { app, runtime, eventStore } = await fixture({
			modelStream,
			inputLimits: { maxInputBytes: 2, maxInputDepth: 1 },
			limits: { ...SERVER_LIMITS, maxResponseBytes: MINIMUM_RESPONSE_BYTES },
		});
		try {
			const response = await postJson(app, "/runtime/v1/runs", { ...STREAM, inputId: "input", payload: { a: true } });
			expect(response.status).toBe(500);
			expect(await response.json()).toEqual(RESPONSE_TOO_LARGE);
			expect(await eventStore.readRun(STREAM, "input")).toBeUndefined();
			expect(modelStream).not.toHaveBeenCalled();
		} finally {
			runtime.stopAdmission();
			await runtime.waitForIdle();
		}
	});

	it.each([
		{
			name: "bytes",
			inputLimits: { maxInputBytes: 262_144, maxInputDepth: 8 },
			limits: { ...SERVER_LIMITS, maxRequestBytes: 524_288 },
			payload: { text: "x".repeat(262_144) },
			acceptedPayload: { text: "x".repeat(262_144 - Buffer.byteLength(JSON.stringify({ text: "" }))) },
			status: 413,
			code: "input_too_large",
			message: "The run payload exceeds runtime.maxInputBytes (262144 UTF-8 bytes).",
		},
		{
			name: "depth",
			inputLimits: { maxInputBytes: Buffer.byteLength(JSON.stringify({ a: { b: true } })), maxInputDepth: 2 },
			limits: SERVER_LIMITS,
			payload: { a: { b: true } },
			acceptedPayload: { a: {} },
			status: 400,
			code: "input_too_deep",
			message: "The run payload exceeds runtime.maxInputDepth (2).",
		},
	])("rejects runtime payload $name before admission and accepts a corrected retry", async (testCase) => {
		const modelStream = vi.fn(() => messageResponse(assistant([])));
		const { app, runtime, eventStore } = await fixture({
			modelStream,
			limits: testCase.limits,
			inputLimits: testCase.inputLimits,
		});
		const append = vi.spyOn(eventStore, "append");
		const request = { ...STREAM, inputId: "input", payload: testCase.payload };
		expect(Buffer.byteLength(JSON.stringify(request))).toBeLessThan(testCase.limits.maxRequestBytes);
		try {
			const rejected = await postJson(app, "/runtime/v1/runs", request);
			expect(rejected.status).toBe(testCase.status);
			expect(await rejected.json()).toEqual({ error: { code: testCase.code, message: testCase.message } });
			expect(append).not.toHaveBeenCalled();
			expect(await eventStore.readRun(STREAM, request.inputId)).toBeUndefined();
			expect(runtime.readActiveCoordinate(STREAM)).toBeUndefined();
			expect(modelStream).not.toHaveBeenCalled();

			const accepted = await postJson(app, "/runtime/v1/runs", { ...request, payload: testCase.acceptedPayload });
			expect(accepted.status).toBe(202);
			expect(await accepted.json()).toMatchObject({ runId: "submitted-run", eventCursor: "0" });
		} finally {
			runtime.stopAdmission();
			await runtime.waitForIdle();
		}
		expect(modelStream).toHaveBeenCalledTimes(1);
		expect(await eventStore.readRun(STREAM, request.inputId)).toMatchObject({ status: "completed" });
	});

	it.each([
		new RangeError("private storage range failure"),
		new JsonValueLimitError("json_too_large"),
		new JsonValueLimitError("json_too_deep"),
	])("keeps internal $name failures out of the input error contract", async (failure) => {
		const modelStream = vi.fn(() => messageResponse(assistant([])));
		const { app, runtime, eventStore } = await fixture({ modelStream });
		vi.spyOn(eventStore, "readRun").mockRejectedValueOnce(failure);
		const append = vi.spyOn(eventStore, "append");
		try {
			const response = await postJson(app, "/runtime/v1/runs", { ...STREAM, inputId: "input", payload: {} });
			expect(response.status).toBe(500);
			expect(await response.json()).toEqual({
				error: { code: "internal_error", message: "The runtime server could not complete the request." },
			});
			expect(append).not.toHaveBeenCalled();
			expect(modelStream).not.toHaveBeenCalled();
		} finally {
			runtime.stopAdmission();
			await runtime.waitForIdle();
		}
	});
});

describe("scoped authority transport", () => {
	const request = { ...STREAM, scope: "planning", generation: "1", operationId: "close-1" };
	const workRetirement = {
		retirementId: "retire-work",
		recordedAt: "2026-09-05T00:00:00.000Z",
		partition: { ...STREAM, ownerId: "account", timelineId: "world", generation: "7" },
	};
	it.each([
		{ failure: new WorkAuthorityRetirementConflictError(), status: 409, code: "authority_conflict" },
		{ failure: new GoalAuthorityRetirementConflictError(), status: 409, code: "authority_conflict" },
		{ failure: new TypeError("Missing work store binding"), status: 500, code: "internal_error" },
		{ failure: new Error("Storage unavailable"), status: 500, code: "internal_error" },
		{
			failure: new AggregateError([new WorkAuthorityRetirementConflictError()], "Host drain failed"),
			status: 500,
			code: "internal_error",
		},
	])(
		"maps Work closure failure to $status/$code without treating operational failures as identity conflicts",
		async ({ failure, status, code }) => {
			const { runtime } = await fixture();
			const close = vi.spyOn(runtime, "closeAuthority").mockRejectedValue(failure);
			const app = createRuntimeServer({
				runtime,
				limits: SERVER_LIMITS,
				access: {
					authenticate: () => ({ principalId: "player" }),
					authorize: () => ({ stream: STREAM, isOwner: true, recipientIds: [], allowInternal: false }),
					authorizeWorkRetirement: () => true,
				},
			});
			const response = await postJson(app, "/runtime/v1/authority/close", { ...request, workRetirement });
			expect(response.status).toBe(status);
			expect(await response.json()).toMatchObject({ error: { code } });
			expect(close).toHaveBeenCalledExactlyOnceWith({ ...request, workRetirement });
		},
	);
	it("requires independent Work permission before any closure mutation", async () => {
		for (const authorizeWorkRetirement of [undefined, () => false, () => "true" as unknown as boolean]) {
			const { app, eventStore } = await fixture({
				...(authorizeWorkRetirement === undefined ? {} : { authorizeWorkRetirement }),
				authorizeGoalRetirement: () => true,
			});
			expect((await postJson(app, "/runtime/v1/authority/close", { ...request, workRetirement })).status).toBe(403);
			expect(await eventStore.readAuthorityClosure(request)).toBeUndefined();
		}
	});
	it("captures the bound Work grant once and freezes its exact request through asynchronous authorization", async () => {
		const { runtime } = await fixture();
		const close = vi.spyOn(runtime, "closeAuthority").mockImplementation(async (input) => ({
			...input,
			closedAt: "2026-09-05T00:00:00.000Z",
			workRetirementResult: {
				phase: "finalized",
				cancelledScheduleCount: "1",
				cancelledOccurrenceCount: "2",
				cancelledMessageCount: "3",
			},
		}));
		let enter!: () => void;
		let release!: () => void;
		const entered = new Promise<void>((resolve) => {
			enter = resolve;
		});
		const gate = new Promise<void>((resolve) => {
			release = resolve;
		});
		let reads = 0;
		const access: RuntimeAccessController = {
			authenticate: () => ({ principalId: "player" }),
			authorize: () => ({ stream: STREAM, isOwner: true, recipientIds: [], allowInternal: false }),
			get authorizeWorkRetirement() {
				reads += 1;
				return async function (
					this: RuntimeAccessController,
					...[principal, grant, input]: Parameters<NonNullable<RuntimeAccessController["authorizeWorkRetirement"]>>
				) {
					expect(this).toBe(access);
					expect(principal.principalId).toBe("player");
					expect(grant.stream).toEqual(STREAM);
					expect(Object.isFrozen(input)).toBe(true);
					expect(Object.isFrozen(input.workRetirement)).toBe(true);
					expect(Object.isFrozen(input.workRetirement?.partition)).toBe(true);
					if (!input.workRetirement) throw new Error("Expected work retirement.");
					expect(Reflect.set(input.workRetirement.partition, "ownerId", "other")).toBe(false);
					enter();
					await gate;
					return true;
				};
			},
		};
		const app = createRuntimeServer({ runtime, access, limits: SERVER_LIMITS });
		Object.defineProperty(access, "authorizeWorkRetirement", { value: () => false });
		const closing = postJson(app, "/runtime/v1/authority/close", { ...request, workRetirement });
		try {
			await entered;
			expect(close).not.toHaveBeenCalled();
		} finally {
			release();
		}
		expect((await closing).status).toBe(200);
		expect(reads).toBe(1);
		expect(close).toHaveBeenCalledExactlyOnceWith({ ...request, workRetirement });
	});
	it("requires both partition grants for a composite Goal and Work closure", async () => {
		const goalRetirement = {
			...workRetirement,
			stateRevision: "1",
			gameTime: { timelineId: "world", generation: "7", tick: "1" },
		};
		for (const permission of ["authorizeGoalRetirement", "authorizeWorkRetirement"] as const) {
			const { app, eventStore } = await fixture({ [permission]: () => true });
			expect(
				(await postJson(app, "/runtime/v1/authority/close", { ...request, goalRetirement, workRetirement })).status,
			).toBe(403);
			expect(await eventStore.readAuthorityClosure(request)).toBeUndefined();
		}
	});
	it("checks cancellation after a Work grant and validates its configuration before serving", async () => {
		const { runtime } = await fixture();
		const close = vi.spyOn(runtime, "closeAuthority");
		const controller = new AbortController();
		const access: RuntimeAccessController = {
			authenticate: () => ({ principalId: "player" }),
			authorize: () => ({ stream: STREAM, isOwner: true, recipientIds: [], allowInternal: false }),
			authorizeWorkRetirement: async (_principal, _access, _request, signal) => {
				await Promise.resolve();
				controller.abort();
				expect(signal.aborted).toBe(true);
				return true;
			},
		};
		const app = createRuntimeServer({ runtime, access, limits: SERVER_LIMITS });
		await app.fetch(
			new Request("https://runtime.invalid/runtime/v1/authority/close", {
				method: "POST",
				headers: { "content-type": "application/json" },
				body: JSON.stringify({ ...request, workRetirement }),
				signal: controller.signal,
			}),
		);
		expect(close).not.toHaveBeenCalled();
		expect(() =>
			createRuntimeServer({
				runtime,
				limits: SERVER_LIMITS,
				access: {
					...access,
					authorizeWorkRetirement: true as unknown as NonNullable<RuntimeAccessController["authorizeWorkRetirement"]>,
				},
			}),
		).toThrow(TypeError);
	});
	it("rejects a mismatched Work stream before invoking either partition grant", async () => {
		const authorizeWorkRetirement = vi.fn(() => true);
		const { app, eventStore } = await fixture({ authorizeWorkRetirement });
		expect(
			(
				await postJson(app, "/runtime/v1/authority/close", {
					...request,
					workRetirement: { ...workRetirement, partition: { ...workRetirement.partition, actorId: "other" } },
				})
			).status,
		).toBe(400);
		expect(authorizeWorkRetirement).not.toHaveBeenCalled();
		expect(await eventStore.readAuthorityClosure(request)).toBeUndefined();
	});
	it.each(["actorId", "sessionId"] as const)(
		"rejects authorization for a different %s before close, read, submit, or Goal access",
		async (field) => {
			const otherStream = { ...STREAM, [field]: "other" };
			const modelStream = vi.fn(() => messageResponse(assistant([])));
			const { runtime, eventStore } = await fixture({ modelStream });
			await runtime.registerActor({ ...otherStream, model: MODEL, modelStream });
			const authorizeGoalRetirement = vi.fn(() => true);
			const authorizeWorkRetirement = vi.fn(() => true);
			const observedSelectors: Readonly<StreamKey>[] = [];
			const app = createRuntimeServer({
				runtime,
				limits: SERVER_LIMITS,
				access: {
					authenticate: () => ({ principalId: "player" }),
					authorize: (_principal, selector) => {
						observedSelectors.push(selector);
						Reflect.set(selector, field, otherStream[field]);
						return { stream: otherStream, isOwner: true, recipientIds: [], allowInternal: false };
					},
					authorizeGoalRetirement,
					authorizeWorkRetirement,
				},
			});
			try {
				const responses = [
					await postJson(app, "/runtime/v1/authority/close", request),
					await postJson(app, "/runtime/v1/authority/close", { ...request, workRetirement }),
					await postJson(app, "/runtime/v1/authority/read", { ...STREAM, scope: "planning", generation: "1" }),
					await postJson(app, "/runtime/v1/runs", { ...STREAM, inputId: "input", payload: {} }),
					await postJson(app, "/runtime/v1/authority/close", {
						...request,
						scope: "goals",
						operationId: "close-goals",
						goalRetirement: {
							retirementId: "retirement",
							stateRevision: "1",
							recordedAt: "2026-09-05T00:00:00.000Z",
							partition: { ...STREAM, ownerId: "owner", timelineId: "world", generation: "1" },
							gameTime: { timelineId: "world", generation: "1", tick: "1" },
						},
					}),
				];
				expect(responses.map((response) => response.status)).toEqual([403, 403, 403, 403, 403]);
				for (const stream of [STREAM, otherStream]) {
					expect(await eventStore.readAuthorityClosure({ ...request, ...stream })).toBeUndefined();
					expect(await eventStore.readRun(stream, "input")).toBeUndefined();
					expect(await eventStore.readRevision(stream)).toBe("empty");
				}
				expect(authorizeGoalRetirement).not.toHaveBeenCalled();
				expect(authorizeWorkRetirement).not.toHaveBeenCalled();
				expect(modelStream).not.toHaveBeenCalled();
				for (const selector of observedSelectors) {
					expect(selector).toEqual(STREAM);
					expect(Object.isFrozen(selector)).toBe(true);
				}
			} finally {
				runtime.stopAdmission();
				await runtime.waitForIdle();
			}
		},
	);

	it("persists and replays owner closure while rejecting old submissions and admitting a new generation", async () => {
		const { app, runtime } = await fixture({ modelStream: () => messageResponse(assistant([])) });
		expect(
			await (
				await postJson(app, "/runtime/v1/authority/read", { ...STREAM, scope: "planning", generation: "1" })
			).json(),
		).toEqual({ closure: null });
		const response = await postJson(app, "/runtime/v1/authority/close", request);
		expect(response.status).toBe(200);
		const closure = await response.json();
		expect(await (await postJson(app, "/runtime/v1/authority/close", request)).json()).toEqual(closure);
		expect(
			await (
				await postJson(app, "/runtime/v1/authority/read", { ...STREAM, scope: "planning", generation: "1" })
			).json(),
		).toEqual({ closure });
		const old = await postJson(app, "/runtime/v1/runs", {
			...STREAM,
			inputId: "old",
			payload: {},
			admissionAuthority: { scope: "planning", generation: "1" },
		});
		expect(old.status).toBe(409);
		expect(await old.json()).toMatchObject({ error: { code: "authority_closed" } });
		expect(
			(
				await postJson(app, "/runtime/v1/runs", {
					...STREAM,
					inputId: "next",
					payload: {},
					admissionAuthority: { scope: "planning", generation: "2" },
				})
			).status,
		).toBe(202);
		runtime.stopAdmission();
		await runtime.waitForIdle();
	});

	it("requires ownership for closing and reading authority", async () => {
		const { app, eventStore } = await fixture({
			access: { stream: STREAM, isOwner: false, recipientIds: ["player"], allowInternal: false },
		});
		for (const path of ["close", "read"]) {
			const body = path === "close" ? request : { ...STREAM, scope: "planning", generation: "1" };
			expect((await postJson(app, `/runtime/v1/authority/${path}`, body)).status).toBe(403);
		}
		expect(await eventStore.readAuthorityClosure(request)).toBeUndefined();
	});

	it("requires explicit permission for the exact Goal partition and allows a host-authorized owner mapping", async () => {
		const goalRetirement = {
			retirementId: "goal-close",
			stateRevision: "1",
			recordedAt: "2026-09-05T00:00:00.000Z",
			partition: { ...STREAM, ownerId: "player-account", timelineId: "world", generation: "1" },
			gameTime: { timelineId: "world", generation: "1", tick: "1" },
		};
		const denied = await fixture();
		expect((await postJson(denied.app, "/runtime/v1/authority/close", { ...request, goalRetirement })).status).toBe(
			403,
		);
		const authorizeGoalRetirement = vi.fn(
			(_principal, _access, input) =>
				input.goalRetirement?.partition.ownerId === "player-account" &&
				input.goalRetirement?.partition.actorId === STREAM.actorId,
		);
		const { app, eventStore } = await fixture({
			authorizeGoalRetirement,
			eventStore: new InMemoryEventStore({ retireGoalAuthority: (draft) => ({ ...draft, cancelledGoalCount: 0 }) }),
		});
		expect(
			(
				await postJson(app, "/runtime/v1/authority/close", {
					...request,
					goalRetirement: { ...goalRetirement, partition: { ...goalRetirement.partition, actorId: "other" } },
				})
			).status,
		).toBe(403);
		expect(await eventStore.readAuthorityClosure(request)).toBeUndefined();
		expect((await postJson(app, "/runtime/v1/authority/close", { ...request, goalRetirement })).status).toBe(200);
		expect(Object.isFrozen(authorizeGoalRetirement.mock.calls[0]?.[2].goalRetirement?.partition)).toBe(true);
		const malformed = await postJson(app, "/runtime/v1/authority/close", {
			...request,
			goalRetirement: { ...goalRetirement, gameTime: { ...goalRetirement.gameTime, generation: "2" } },
		});
		expect(malformed.status).toBe(400);
	});
});

async function appendAudienceEvents(eventStore: InMemoryEventStore): Promise<void> {
	let revision: "empty" | string = "empty";
	const dataItems: CanonicalEventData[] = [
		{ type: "run.queued", inputId: "input", inputFingerprint: "0".repeat(64) },
		{
			type: "item.started",
			item: {
				itemType: "message",
				itemId: "public-item",
				role: "assistant",
				content: [{ type: "text", text: "public", sourceIndex: 0 }],
				audience: { visibility: "public" },
			},
		},
		{
			type: "item.started",
			item: {
				itemType: "message",
				itemId: "recipient-item",
				role: "assistant",
				content: [{ type: "text", text: "recipient", sourceIndex: 0 }],
				audience: { visibility: "recipient", recipientIds: ["player", "other"] },
			},
		},
		{
			type: "item.updated",
			itemId: "internal-progress",
			audience: { visibility: "internal" },
			updateOrdinal: "1",
			content: [{ type: "text", text: "private" }],
		},
	];
	for (const data of dataItems) {
		const event = await eventStore.append(STREAM, revision, COORDINATE, data);
		revision = event.revision;
	}
}

function readAttachment(app: ReturnType<typeof createRuntimeServer>, reference = ATTACHMENT_REFERENCE) {
	return app.request("/runtime/v1/attachments/read", {
		method: "POST",
		headers: { authorization: "Bearer test", "content-type": "application/json" },
		body: JSON.stringify({ ...STREAM, reference }),
	});
}

describe("Hono runtime server", () => {
	it("returns service unavailable when Runtime admission has stopped", async () => {
		const base = await fixture();
		base.runtime.stopAdmission();

		const response = await postJson(base.app, "/runtime/v1/runs", {
			...STREAM,
			inputId: "stopping-input",
			payload: {},
		});

		expect(response.status).toBe(503);
		await expect(response.json()).resolves.toEqual({
			error: { code: "runtime_stopping", message: "The runtime is not accepting new runs." },
		});
	});

	it("rejects a runtime limit object with a missing required field", async () => {
		const base = await fixture();
		const incomplete: Partial<RuntimeServerLimits> = { ...SERVER_LIMITS };
		delete incomplete.maxRequestBytes;
		expect(() =>
			createRuntimeServer({
				runtime: base.runtime,
				access: { authenticate: () => undefined, authorize: () => undefined },
				limits: incomplete as RuntimeServerLimits,
			}),
		).toThrow("maxRequestBytes must be a positive safe integer");
	});

	it("requires a JSON response budget that can represent its bounded failure envelope", async () => {
		const base = await fixture();
		expect(() =>
			createRuntimeServer({
				runtime: base.runtime,
				access: { authenticate: () => undefined, authorize: () => undefined },
				limits: { ...SERVER_LIMITS, maxResponseBytes: MINIMUM_RESPONSE_BYTES - 1 },
			}),
		).toThrow("maxResponseBytes cannot represent the bounded response error");
	});

	it("bounds the timer-backed action poll without constraining the action lease", async () => {
		const base = await fixture();
		const create = (limits: RuntimeServerLimits) =>
			createRuntimeServer({
				runtime: base.runtime,
				access: { authenticate: () => undefined, authorize: () => undefined },
				limits,
			});

		expect(() => create({ ...SERVER_LIMITS, actionStreamWaitMs: 2_147_483_648 })).toThrow(
			"actionStreamWaitMs must not exceed 2147483647",
		);
		expect(() =>
			create({ ...SERVER_LIMITS, actionClaimLeaseMs: Number.MAX_SAFE_INTEGER, actionStreamWaitMs: 2_147_483_647 }),
		).not.toThrow();
	});

	it("requires explicit valid attachment callbacks and response bounds", async () => {
		const base = await fixture();
		const create = (attachments: RuntimeAttachmentService) =>
			createRuntimeServer({
				runtime: base.runtime,
				access: { authenticate: () => undefined, authorize: () => undefined },
				limits: SERVER_LIMITS,
				attachments,
			});
		expect(() => create({ maxResponseBytes: 0, authorize: () => true, read: async () => ATTACHMENT_BYTES })).toThrow(
			"platform-supported response byte limit",
		);
		expect(() =>
			create({
				maxResponseBytes: ATTACHMENT_BYTES.byteLength,
				authorize: undefined,
				read: async () => ATTACHMENT_BYTES,
			} as unknown as RuntimeAttachmentService),
		).toThrow("platform-supported response byte limit");
	});

	it("negotiates only the optional capabilities that are configured", async () => {
		const base = await fixture();
		const baseResponse = (await (await base.app.request("/runtime/v1/initialize", { method: "POST" })).json()) as {
			capabilities: string[];
		};
		expect(baseResponse.capabilities).not.toContain("actions.exchange");
		expect(baseResponse.capabilities).not.toContain("actions.sse");
		expect(baseResponse.capabilities).not.toContain("approvals.exact");
		expect(baseResponse.capabilities).not.toContain("attachments.read");
		expect(baseResponse.capabilities).not.toContain("health.snapshot");
		expect(baseResponse.capabilities).toContain("deferred.lifecycle");

		const configured = await fixture({
			actions: new ActionCoordinator(new InMemoryActionJournal()),
			approvals: new ActionApprovalCoordinator(new InMemoryActionApprovalStore()),
			attachments: {
				maxResponseBytes: ATTACHMENT_BYTES.byteLength,
				authorize: () => true,
				read: async () => ATTACHMENT_BYTES,
			},
			health: {
				inspect: async () => ({
					checkedAt: "2026-08-30T00:00:00.000Z",
					status: "ready",
					dependencies: [],
				}),
			},
		});
		const configuredResponse = (await (
			await configured.app.request("/runtime/v1/initialize", { method: "POST" })
		).json()) as { capabilities: string[] };
		expect(configuredResponse.capabilities).toContain("actions.exchange");
		expect(configuredResponse.capabilities).toContain("actions.sse");
		expect(configuredResponse.capabilities).toContain("approvals.exact");
		expect(configuredResponse.capabilities).toContain("attachments.read");
		expect(configuredResponse.capabilities).toContain("health.snapshot");
	});

	it("serves the detailed health snapshot only to an authenticated principal", async () => {
		const snapshot: RuntimeHealthSnapshot = {
			checkedAt: "2026-08-30T00:00:00.000Z",
			status: "degraded",
			dependencies: [
				{
					id: "optional-mcp",
					kind: "mcp",
					requirement: "optional",
					state: "unavailable",
					observedAt: "2026-08-30T00:00:00.000Z",
					reason: "transport",
				},
			],
		};
		const inspect = vi.fn(async (signal: AbortSignal) => {
			expect(signal).toBeInstanceOf(AbortSignal);
			return snapshot;
		});
		const configured = await fixture({ health: { inspect } });
		const response = await configured.app.request("/runtime/v1/health", {
			headers: { authorization: "Bearer test" },
		});
		expect(response.status).toBe(200);
		expect(response.headers.get("cache-control")).toBe("private, no-store");
		await expect(response.json()).resolves.toEqual(snapshot);
		expect(inspect).toHaveBeenCalledOnce();
		expect(configured.authorizeCalls()).toBe(0);

		const deniedInspect = vi.fn(async () => snapshot);
		const denied = await fixture({ authenticated: false, health: { inspect: deniedInspect } });
		expect((await denied.app.request("/runtime/v1/health")).status).toBe(401);
		expect(deniedInspect).not.toHaveBeenCalled();
	});

	it("keeps missing, invalid, and oversized health projections fail-closed", async () => {
		const missing = await fixture();
		expect(
			(
				await missing.app.request("/runtime/v1/health", {
					headers: { authorization: "Bearer test" },
				})
			).status,
		).toBe(501);

		const invalid = await fixture({
			health: {
				inspect: async () => ({ checkedAt: "now", status: "ready", dependencies: [] }) as never,
			},
		});
		const invalidResponse = await invalid.app.request("/runtime/v1/health", {
			headers: { authorization: "Bearer test" },
		});
		expect(invalidResponse.status).toBe(500);
		await expect(invalidResponse.json()).resolves.toEqual({
			error: { code: "internal_error", message: "The runtime server could not complete the request." },
		});

		const oversized = await fixture({
			limits: { ...SERVER_LIMITS, maxResponseBytes: MINIMUM_RESPONSE_BYTES },
			health: {
				inspect: async () => ({
					checkedAt: "2026-08-30T00:00:00.000Z",
					status: "not-ready",
					dependencies: [{ id: "x".repeat(1_000), kind: "mcp", requirement: "required", state: "declared" }],
				}),
			},
		});
		const oversizedResponse = await oversized.app.request("/runtime/v1/health", {
			headers: { authorization: "Bearer test" },
		});
		expect(oversizedResponse.status).toBe(500);
		await expect(oversizedResponse.json()).resolves.toEqual(RESPONSE_TOO_LARGE);
	});

	it("requires a callable health inspector before exposing the route", async () => {
		const base = await fixture();
		expect(() =>
			createRuntimeServer({
				runtime: base.runtime,
				access: { authenticate: () => undefined, authorize: () => undefined },
				limits: SERVER_LIMITS,
				health: { inspect: undefined } as unknown as RuntimeServerHealthService,
			}),
		).toThrow("health inspect callback");
	});

	it("serves authorized PNG bytes with exact integrity and privacy metadata", async () => {
		let authorizeCalls = 0;
		let readCalls = 0;
		const { app } = await fixture({
			attachments: {
				maxResponseBytes: ATTACHMENT_BYTES.byteLength,
				authorize: (_principal, access, reference) => {
					authorizeCalls += 1;
					expect(access.stream).toEqual(STREAM);
					expect(Object.isFrozen(reference)).toBe(true);
					return true;
				},
				read: async () => {
					readCalls += 1;
					return ATTACHMENT_BYTES;
				},
			},
		});
		const response = await readAttachment(app);
		const digest = createHash("sha256").update(ATTACHMENT_BYTES).digest("base64");
		expect(response.status).toBe(200);
		expect(new Uint8Array(await response.arrayBuffer())).toEqual(ATTACHMENT_BYTES);
		expect(Object.fromEntries(response.headers)).toMatchObject({
			"cache-control": "private, no-store",
			"content-digest": `sha-256=:${digest}:`,
			"content-length": String(ATTACHMENT_BYTES.byteLength),
			"content-type": "image/png",
			etag: `"${ATTACHMENT_REFERENCE.attachmentId}"`,
			"x-content-type-options": "nosniff",
		});
		expect({ authorizeCalls, readCalls }).toEqual({ authorizeCalls: 1, readCalls: 1 });
	});

	it("fails closed before attachment reads when authentication, actor access, media, size, or attachment access fails", async () => {
		const cases = [
			{ fixture: { authenticated: false }, expectedStatus: 401, expectedActorAuthorizations: 0 },
			{ fixture: { access: null }, expectedStatus: 403, expectedActorAuthorizations: 1 },
			{
				fixture: {},
				reference: { ...ATTACHMENT_REFERENCE, mediaType: "image/jpeg" as const },
				expectedStatus: 400,
				expectedActorAuthorizations: 1,
			},
			{
				fixture: {},
				reference: { ...ATTACHMENT_REFERENCE, bytes: ATTACHMENT_BYTES.byteLength + 1 },
				expectedStatus: 413,
				expectedActorAuthorizations: 1,
			},
		] as const;
		for (const testCase of cases) {
			let attachmentAuthorizeCalls = 0;
			let attachmentReadCalls = 0;
			const configured = await fixture({
				...testCase.fixture,
				attachments: {
					maxResponseBytes: ATTACHMENT_BYTES.byteLength,
					authorize: () => {
						attachmentAuthorizeCalls += 1;
						return true;
					},
					read: async () => {
						attachmentReadCalls += 1;
						return ATTACHMENT_BYTES;
					},
				},
			});
			const reference = "reference" in testCase ? testCase.reference : ATTACHMENT_REFERENCE;
			const response = await readAttachment(configured.app, reference);
			expect(response.status).toBe(testCase.expectedStatus);
			expect(configured.authorizeCalls()).toBe(testCase.expectedActorAuthorizations);
			expect({ attachmentAuthorizeCalls, attachmentReadCalls }).toEqual({
				attachmentAuthorizeCalls: 0,
				attachmentReadCalls: 0,
			});
		}

		let deniedReads = 0;
		const denied = await fixture({
			attachments: {
				maxResponseBytes: ATTACHMENT_BYTES.byteLength,
				authorize: () => false,
				read: async () => {
					deniedReads += 1;
					return ATTACHMENT_BYTES;
				},
			},
		});
		expect((await readAttachment(denied.app)).status).toBe(403);
		expect(deniedReads).toBe(0);

		const nonBoolean = await fixture({
			attachments: {
				maxResponseBytes: ATTACHMENT_BYTES.byteLength,
				authorize: () => 1 as unknown as boolean,
				read: async () => {
					deniedReads += 1;
					return ATTACHMENT_BYTES;
				},
			},
		});
		expect((await readAttachment(nonBoolean.app)).status).toBe(403);
		expect(deniedReads).toBe(0);
	});

	it("snapshots attachment callbacks and rejects reader integrity mismatches", async () => {
		let originalReads = 0;
		let mutatedReads = 0;
		const service = {
			maxResponseBytes: ATTACHMENT_BYTES.byteLength,
			authorize: () => true,
			read: async () => {
				originalReads += 1;
				return ATTACHMENT_BYTES;
			},
		};
		const configured = await fixture({ attachments: service });
		service.maxResponseBytes = 1;
		service.authorize = () => false;
		service.read = async () => {
			mutatedReads += 1;
			return Uint8Array.of(0);
		};
		expect((await readAttachment(configured.app)).status).toBe(200);
		expect({ originalReads, mutatedReads }).toEqual({ originalReads: 1, mutatedReads: 0 });

		const corrupted = await fixture({
			attachments: {
				maxResponseBytes: ATTACHMENT_BYTES.byteLength,
				authorize: () => true,
				read: async () => Uint8Array.from(ATTACHMENT_BYTES, (byte, index) => (index === 0 ? byte ^ 1 : byte)),
			},
		});
		const response = await readAttachment(corrupted.app);
		expect(response.status).toBe(500);
		expect(await response.json()).toEqual({
			error: { code: "internal_error", message: "The runtime server could not complete the request." },
		});
	});

	it("exchanges atomic action deliveries and receipts only with an authorized game adapter", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
			newClaimToken: () => "claim-token",
		});
		const intent = await actions.submit({
			...STREAM,
			coordinate: { ...COORDINATE, turn: "1" },
			actionName: "world.write",
			operationKey: "operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
		});
		const { app } = await fixture({
			actions,
			access: {
				stream: STREAM,
				isOwner: false,
				recipientIds: [],
				allowInternal: false,
				allowActionExchange: true,
			},
		});
		const post = (path: string, body: unknown) =>
			app.request(path, {
				method: "POST",
				headers: { "content-type": "application/json" },
				body: JSON.stringify(body),
			});

		const emptyRecovery = await post("/runtime/v1/actions/recovery", STREAM);
		expect(emptyRecovery.status).toBe(200);
		await expect(emptyRecovery.json()).resolves.toEqual({ delivery: null });

		const nextResponse = await post("/runtime/v1/actions/next", STREAM);
		const next = (await nextResponse.json()) as {
			delivery: { kind: "dispatch"; action: { claim: { claimToken: string; attempt: string } } };
		};
		expect(nextResponse.status).toBe(200);
		expect(next.delivery).toMatchObject({
			kind: "dispatch",
			action: { claim: { claimToken: "claim-token", attempt: "1" } },
		});
		const reconciliationResponse = await post("/runtime/v1/actions/recovery", STREAM);
		await expect(reconciliationResponse.json()).resolves.toMatchObject({
			delivery: {
				kind: "reconcile",
				action: { operationId: intent.operationId, claim: next.delivery.action.claim },
			},
		});
		expect((await post("/runtime/v1/actions/recovery", { ...STREAM, runId: "run" })).status).toBe(400);
		const receipt = await post("/runtime/v1/actions/receipt", {
			...STREAM,
			operationId: intent.operationId,
			claimToken: next.delivery.action.claim.claimToken,
			attempt: next.delivery.action.claim.attempt,
			outcome: "succeeded",
			result: { visible: "done" },
		});
		expect(await receipt.json()).toMatchObject({
			action: { status: "succeeded", receipt: { result: { visible: "done" } } },
		});
		const stale = await post("/runtime/v1/actions/receipt", {
			...STREAM,
			operationId: intent.operationId,
			claimToken: next.delivery.action.claim.claimToken,
			attempt: next.delivery.action.claim.attempt,
			outcome: "succeeded",
		});
		expect(stale.status).toBe(409);
	});

	it("claims an Action only from the requested run over the Runtime transport", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
			newClaimToken: () => "claim-token",
		});
		const first = await actions.submit({
			...STREAM,
			coordinate: { ...COORDINATE, runId: "run-one" },
			actionName: "world.write",
			operationKey: "first-run",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["first-key"],
			replayPolicy: "recoverable",
		});
		const second = await actions.submit({
			...STREAM,
			coordinate: { ...COORDINATE, runId: "run-two" },
			actionName: "world.write",
			operationKey: "second-run",
			payload: { value: 2 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["second-key"],
			replayPolicy: "recoverable",
		});
		const { app } = await fixture({
			actions,
			access: {
				stream: STREAM,
				isOwner: false,
				recipientIds: [],
				allowInternal: false,
				allowActionExchange: true,
			},
		});

		const response = await postJson(app, "/runtime/v1/actions/next", { ...STREAM, runId: "run-two" });
		expect(await response.json()).toMatchObject({
			delivery: { kind: "dispatch", action: { operationId: second.operationId } },
		});
		await expect(actions.get(first, first.operationId)).resolves.toMatchObject({ status: "pending" });
	});

	it("requires an exact claim fence and replays an identical reconciliation", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
			newClaimToken: () => "reconcile-token",
		});
		const intent = await actions.submit({
			...STREAM,
			coordinate: COORDINATE,
			actionName: "world.write",
			operationKey: "reconcile-operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "never",
		});
		const { app } = await fixture({
			actions,
			access: {
				stream: STREAM,
				isOwner: false,
				recipientIds: [],
				allowInternal: false,
				allowActionExchange: true,
			},
		});
		const post = (path: string, body: unknown) =>
			app.request(path, {
				method: "POST",
				headers: { "content-type": "application/json" },
				body: JSON.stringify(body),
			});
		const next = (await (await post("/runtime/v1/actions/next", STREAM)).json()) as {
			delivery: { action: { claim: { claimToken: string; attempt: string } } };
		};
		expect(
			(
				await post("/runtime/v1/actions/receipt", {
					...STREAM,
					operationId: intent.operationId,
					claimToken: next.delivery.action.claim.claimToken,
					attempt: "0",
					outcome: "succeeded",
				})
			).status,
		).toBe(400);
		expect(
			(
				await post("/runtime/v1/actions/reconcile", {
					...STREAM,
					operationId: intent.operationId,
					claimToken: next.delivery.action.claim.claimToken,
					attempt: "0",
					resolution: "applied",
				})
			).status,
		).toBe(400);
		const reconciliation = {
			...STREAM,
			operationId: intent.operationId,
			claimToken: next.delivery.action.claim.claimToken,
			attempt: next.delivery.action.claim.attempt,
			resolution: "applied",
			result: { visible: "applied" },
		};

		expect((await post("/runtime/v1/actions/reconcile", { ...reconciliation, claimToken: "stale" })).status).toBe(409);
		const first = await post("/runtime/v1/actions/reconcile", reconciliation);
		const firstBody = await first.json();
		expect(first.status).toBe(200);
		expect(firstBody).toMatchObject({ action: { status: "succeeded", receipt: { result: { visible: "applied" } } } });
		const replay = await post("/runtime/v1/actions/reconcile", reconciliation);
		expect(replay.status).toBe(200);
		expect(await replay.json()).toEqual(firstBody);
		expect(
			(await post("/runtime/v1/actions/reconcile", { ...reconciliation, result: { visible: "different" } })).status,
		).toBe(409);
	});

	it("streams a durable action once and makes reconnect reconciliation-only", async () => {
		const actions = new ActionCoordinator(new InMemoryActionJournal(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
			newClaimToken: () => "stream-claim",
		});
		const prepared = await actions.submit({
			...STREAM,
			coordinate: COORDINATE,
			actionName: "world.write",
			operationKey: "stream-operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
		});
		const { app } = await fixture({
			actions,
			access: {
				stream: STREAM,
				isOwner: true,
				recipientIds: ["player"],
				allowInternal: false,
				allowActionExchange: true,
			},
		});

		const readDelivery = async () => {
			const abort = new AbortController();
			const response = await app.request("/runtime/v1/actions/stream", {
				method: "POST",
				headers: { authorization: "Bearer test", "content-type": "application/json" },
				body: JSON.stringify(STREAM),
				signal: abort.signal,
			});
			expect(response.headers.get("content-type")).toContain("text/event-stream");
			const reader = response.body?.getReader();
			if (!reader) throw new Error("Expected an action SSE body.");
			const decoder = new TextDecoder();
			let text = "";
			while (!text.includes("\n\n")) {
				const chunk = await reader.read();
				if (chunk.done) break;
				text += decoder.decode(chunk.value, { stream: true });
			}
			abort.abort();
			await reader.cancel();
			return text;
		};

		expect(await readDelivery()).toContain('"kind":"dispatch"');
		expect(await readDelivery()).toContain('"kind":"reconcile"');
		expect(await actions.get(STREAM, prepared.operationId)).toMatchObject({ attempt: "1", status: "claimed" });
	});

	it("lists and atomically decides exact approvals using authenticated reviewer identity", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const prepared = prepareActionIntent({
			...STREAM,
			coordinate: COORDINATE,
			actionName: "world.write",
			operationKey: "operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "never",
		});
		const pending = await approvals.request("high-risk", prepared, 1_000, {
			presentation: {
				value: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
				limits: { maxBytes: 256, maxDepth: 2 },
			},
		});
		await approvals.request(
			"high-risk",
			prepareActionIntent({
				...STREAM,
				coordinate: { ...COORDINATE, runId: "other-run" },
				actionName: "world.write",
				operationKey: "other-operation",
				payload: { value: 2 },
				expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
				conflictKeys: ["world:tile:2"],
				replayPolicy: "never",
			}),
			1_000,
		);
		const { app } = await fixture({
			approvals,
			access: {
				stream: STREAM,
				isOwner: true,
				recipientIds: ["player"],
				allowInternal: false,
				allowApprovals: true,
			},
		});
		const post = (path: string, body: unknown) =>
			app.request(path, {
				method: "POST",
				headers: { "content-type": "application/json" },
				body: JSON.stringify(body),
			});

		const listed = await post("/runtime/v1/approvals", { ...STREAM, runId: COORDINATE.runId, limit: 10 });
		expect(await listed.json()).toMatchObject({
			approvals: [
				{
					status: "pending",
					request: {
						coordinate: COORDINATE,
						presentation: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
					},
				},
			],
		});
		const decisionBody = {
			...STREAM,
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
		};
		const decided = await post("/runtime/v1/approvals/decision", decisionBody);
		expect(await decided.json()).toMatchObject({
			approval: {
				status: "approved",
				decision: { reviewerId: "player" },
				request: {
					presentation: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
				},
			},
		});
		expect((await post("/runtime/v1/approvals/decision", decisionBody)).status).toBe(409);
	});

	it("long-waits on an Approval cursor, wakes on change, and bounds concurrent waiters", async () => {
		const approvals = new ActionApprovalCoordinator(new InMemoryActionApprovalStore(), {
			now: () => new Date("2026-08-28T00:00:00.000Z"),
		});
		const pending = await approvals.request(
			"high-risk",
			prepareActionIntent({
				...STREAM,
				coordinate: COORDINATE,
				actionName: "world.write",
				operationKey: "wait-operation",
				payload: {},
				expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
				conflictKeys: [],
				replayPolicy: "never",
			}),
			1_000,
		);
		const secondRead = deferred();
		let reads = 0;
		const listPending = approvals.listPending.bind(approvals);
		vi.spyOn(approvals, "listPending").mockImplementation(async (request) => {
			reads += 1;
			if (reads === 2) secondRead.resolve();
			return listPending(request);
		});
		const { app } = await fixture({
			approvals,
			access: {
				stream: STREAM,
				isOwner: true,
				recipientIds: ["player"],
				allowInternal: false,
				allowApprovals: true,
			},
			limits: { ...SERVER_LIMITS, maxApprovalWaiters: 1 },
		});
		const body = { ...STREAM, runId: COORDINATE.runId, limit: 10, waitMilliseconds: 1_000 };
		const initial = await postJson(app, "/runtime/v1/approvals/wait", body);
		const initialBody = (await initial.json()) as { approvals: unknown[]; cursor: string };
		expect(initialBody.approvals).toHaveLength(1);
		expect(initialBody.cursor).toMatch(/^[0-9a-f]{64}$/);

		const waiting = postJson(app, "/runtime/v1/approvals/wait", {
			...body,
			afterCursor: initialBody.cursor,
		});
		await secondRead.promise;
		const capacity = await postJson(app, "/runtime/v1/approvals/wait", {
			...body,
			afterCursor: initialBody.cursor,
		});
		expect(capacity.status).toBe(503);
		expect(await capacity.json()).toMatchObject({ error: { code: "approval_wait_capacity" } });

		await approvals.decide({
			...STREAM,
			approvalId: pending.request.approvalId,
			expectedRevision: pending.revision,
			requestFingerprint: pending.request.requestFingerprint,
			decision: "approve",
			reviewerId: "player",
		});
		const changed = await waiting;
		expect(changed.status).toBe(200);
		expect(await changed.json()).toMatchObject({ approvals: [] });
	});

	it("coalesces concurrent canonically equivalent run submissions", async () => {
		const controlled = controlledResponse();
		let providerCalls = 0;
		const eventStore = new InMemoryEventStore();
		await eventStore.append(STREAM, "empty", { ...STREAM, runId: "earlier-run", turn: "0" }, { type: "turn.started" });
		const { app } = await fixture({
			eventStore,
			modelStream: (...arguments_) => {
				providerCalls += 1;
				return controlled.streamFn(...arguments_);
			},
		});
		const request = (payload: unknown) =>
			app.request("/runtime/v1/runs", {
				method: "POST",
				headers: { "content-type": "application/json" },
				body: JSON.stringify({ ...STREAM, inputId: "input", payload }),
			});

		const [first, second] = await Promise.all([
			request({ z: 1, nested: { b: true, a: "value" } }),
			request({ nested: { a: "value", b: true }, z: 1 }),
		]);
		await controlled.started;
		expect(first.status).toBe(202);
		expect(second.status).toBe(202);
		const firstBody = await first.json();
		expect(firstBody).toMatchObject({ eventCursor: "1" });
		expect(firstBody).toEqual(await second.json());
		expect(providerCalls).toBe(1);
		controlled.finish();
	});

	it("returns conflict for changed content under an existing input identifier", async () => {
		const controlled = controlledResponse();
		const { app } = await fixture({ modelStream: controlled.streamFn });
		const request = (payload: unknown) =>
			app.request("/runtime/v1/runs", {
				method: "POST",
				headers: { "content-type": "application/json" },
				body: JSON.stringify({ ...STREAM, inputId: "input", payload }),
			});

		expect((await request({ value: 1 })).status).toBe(202);
		const conflict = await request({ value: 2 });
		expect(conflict.status).toBe(409);
		expect(await conflict.json()).toMatchObject({ error: { code: "input_conflict" } });
		controlled.finish();
	});

	it("authenticates before parsing or authorizing an invalid request", async () => {
		const { app, authorizeCalls } = await fixture({ authenticated: false });
		const response = await app.request("/runtime/v1/events", { method: "POST", body: "not-json" });
		expect(response.status).toBe(401);
		expect(authorizeCalls()).toBe(0);
	});

	it("reads only the safe deferred projection through the authorized stream", async () => {
		const empty = await fixture();
		const emptyResponse = await postJson(empty.app, "/runtime/v1/deferred/read", STREAM);
		expect(emptyResponse.status).toBe(200);
		expect(await emptyResponse.json()).toEqual({ deferred: null });

		const configured = await fixture({ transcript: transcript(), modelStream: deferredResponse });
		await expect(
			configured.runtime.start({ ...STREAM, inputId: "private-input", payload: { secret: "game-state" } }).completion,
		).resolves.toMatchObject({ status: "deferred" });
		const pending = configured.runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		expect(
			(
				await postJson(configured.app, "/runtime/v1/deferred/read", {
					sessionId: "untrusted-session",
					actorId: "untrusted-actor",
				})
			).status,
		).toBe(403);
		const response = await postJson(configured.app, "/runtime/v1/deferred/read", STREAM);
		const body = await response.json();
		expect(response.status).toBe(200);
		expect(body).toEqual({
			deferred: {
				operationId: pending.operationId,
				inputId: "private-input",
				coordinate: pending.coordinate,
			},
		});
		expect(JSON.stringify(body)).not.toContain("game-state");
		expect(JSON.stringify(body)).not.toContain("provider-operation");
		expect(JSON.stringify(body)).not.toContain("handle");
	});

	it("blocks deferred resume after admission stops while preserving explicit abandonment", async () => {
		const configured = await fixture({ transcript: transcript(), modelStream: deferredResponse });
		await configured.runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = configured.runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		configured.runtime.stopAdmission();

		const resume = await postJson(configured.app, "/runtime/v1/deferred/resume", {
			...STREAM,
			operationId: pending.operationId,
		});
		expect(resume.status).toBe(503);
		expect(await resume.json()).toEqual({
			error: { code: "runtime_stopping", message: "The runtime is not accepting new runs." },
		});

		const abandon = await postJson(configured.app, "/runtime/v1/deferred/abandon", {
			...STREAM,
			operationId: pending.operationId,
		});
		expect(abandon.status).toBe(200);
		expect(await abandon.json()).toEqual({
			operationId: pending.operationId,
			providerCancellation: "unavailable",
		});
	});

	it("fails deferred requests before Runtime access when authentication or actor authorization fails", async () => {
		const unauthenticated = await fixture({ authenticated: false });
		const invalid = await unauthenticated.app.request("/runtime/v1/deferred/read", {
			method: "POST",
			body: "not-json",
		});
		expect(invalid.status).toBe(401);
		expect(unauthenticated.authorizeCalls()).toBe(0);

		const denied = await fixture({ access: null });
		const read = vi.spyOn(denied.runtime, "readDeferred");
		const response = await postJson(denied.app, "/runtime/v1/deferred/read", STREAM);
		expect(response.status).toBe(403);
		expect(read).not.toHaveBeenCalled();
	});

	it("snapshots every deferred Runtime method at server construction", async () => {
		const configured = await fixture({ transcript: transcript(), modelStream: deferredResponse });
		await configured.runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = configured.runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		configured.runtime.readDeferred = () => {
			throw new Error("mutated read");
		};
		configured.runtime.resumeDeferred = async () => {
			throw new Error("mutated resume");
		};
		configured.runtime.abandonDeferred = async () => {
			throw new Error("mutated abandon");
		};

		expect((await postJson(configured.app, "/runtime/v1/deferred/read", STREAM)).status).toBe(200);
		expect(
			(
				await postJson(configured.app, "/runtime/v1/deferred/resume", {
					...STREAM,
					operationId: "stale-operation",
				})
			).status,
		).toBe(404);
		expect(
			(
				await postJson(configured.app, "/runtime/v1/deferred/abandon", {
					...STREAM,
					operationId: pending.operationId,
				})
			).status,
		).toBe(200);
	});

	it("returns stable conflicts for pending input, stale operations, and an unavailable resume Adapter", async () => {
		const configured = await fixture({ transcript: transcript(), modelStream: deferredResponse });
		await configured.runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = configured.runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		const sameInput = await postJson(configured.app, "/runtime/v1/runs", {
			...STREAM,
			inputId: "input",
			payload: {},
		});
		expect(sameInput.status).toBe(202);
		expect(await sameInput.json()).toMatchObject({ runId: pending.coordinate.runId });

		const changedInput = await postJson(configured.app, "/runtime/v1/runs", {
			...STREAM,
			inputId: "input",
			payload: { changed: true },
		});
		expect(changedInput.status).toBe(409);
		expect(await changedInput.json()).toMatchObject({ error: { code: "input_conflict" } });

		const pendingInput = await postJson(configured.app, "/runtime/v1/runs", {
			...STREAM,
			inputId: "other-input",
			payload: {},
		});
		expect(pendingInput.status).toBe(409);
		expect(await pendingInput.json()).toMatchObject({ error: { code: "deferred_pending" } });

		const stale = await postJson(configured.app, "/runtime/v1/deferred/resume", {
			...STREAM,
			operationId: "stale-operation",
		});
		expect(stale.status).toBe(404);
		expect(await stale.json()).toMatchObject({ error: { code: "deferred_not_found" } });

		const unavailable = await postJson(configured.app, "/runtime/v1/deferred/resume", {
			...STREAM,
			operationId: pending.operationId,
		});
		expect(unavailable.status).toBe(409);
		expect(await unavailable.json()).toMatchObject({ error: { code: "deferred_resume_unavailable" } });
		expect(configured.runtime.readDeferred(STREAM)).toMatchObject({ operationId: pending.operationId });
		const replay = await configured.runtime.replayEvents(STREAM, { afterRevision: "0", limit: 10 });
		if (replay.kind !== "page") throw new Error("Expected a deferred lifecycle event page.");
		expect(replay.events).not.toContainEqual(
			expect.objectContaining({ data: expect.objectContaining({ type: "run.resumed" }) }),
		);
	});

	it("maps an in-progress deferred transition to a stable busy conflict", async () => {
		const backing = new InMemoryTranscriptStore();
		const appendEntered = deferred();
		const appendRelease = deferred();
		const store: TranscriptStore = {
			load: backing.load.bind(backing),
			append: async (stream, revision, records) => {
				if (records.some((record) => record.type === "deferred.abandoned")) {
					appendEntered.resolve();
					await appendRelease.promise;
				}
				return backing.append(stream, revision, records);
			},
		};
		const configured = await fixture({
			transcript: new DurableTranscript(store, {
				maxRecordBytes: 16_384,
				maxCommitBytes: 65_536,
				maxRestoreBytes: 131_072,
				maxJsonDepth: 16,
			}),
			modelStream: deferredResponse,
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "unused" }]),
				cancel: async () => undefined,
			},
		});
		await configured.runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = configured.runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		const command = { ...STREAM, operationId: pending.operationId };

		const abandoning = postJson(configured.app, "/runtime/v1/deferred/abandon", command);
		await appendEntered.promise;
		const busy = await postJson(configured.app, "/runtime/v1/deferred/resume", command);
		expect(busy.status).toBe(409);
		expect(await busy.json()).toMatchObject({ error: { code: "deferred_busy" } });
		appendRelease.resolve();
		expect((await abandoning).status).toBe(200);
	});

	it("coalesces only an in-flight resume and permits the next explicit fetch for the same operation", async () => {
		const firstFetchStarted = deferred();
		const releaseFirstFetch = deferred();
		let fetches = 0;
		const configured = await fixture({
			transcript: transcript(),
			modelStream: deferredResponse,
			deferredResponses: {
				fetch: async () => {
					fetches += 1;
					if (fetches === 1) {
						firstFetchStarted.resolve();
						await releaseFirstFetch.promise;
						return { ...assistant([], "deferred"), deferred: DEFERRED_HANDLE };
					}
					return assistant([{ type: "text", text: "ready" }]);
				},
				cancel: async () => undefined,
			},
		});
		await configured.runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = configured.runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		const command = { ...STREAM, operationId: pending.operationId };

		const [first, duplicate] = await Promise.all([
			postJson(configured.app, "/runtime/v1/deferred/resume", command),
			postJson(configured.app, "/runtime/v1/deferred/resume", command),
		]);
		expect(first.status).toBe(202);
		expect(duplicate.status).toBe(202);
		expect(await first.json()).toEqual(await duplicate.json());
		await firstFetchStarted.promise;
		expect(fetches).toBe(1);
		const suspendedAgain = new Promise<void>((resolve) => {
			const unsubscribe = configured.runtime.subscribe(STREAM, (event) => {
				if (event.data.type !== "run.suspended" || event.data.operationId !== pending.operationId) return;
				unsubscribe();
				resolve();
			});
		});
		releaseFirstFetch.resolve();
		await suspendedAgain;
		await (await configured.runtime.submit({ ...STREAM, inputId: "input", payload: {} })).completion;
		expect(configured.runtime.readDeferred(STREAM)).toMatchObject({ operationId: pending.operationId });

		const completedEvent = new Promise<void>((resolve) => {
			const unsubscribe = configured.runtime.subscribe(STREAM, (event) => {
				if (event.data.type !== "run.completed") return;
				unsubscribe();
				resolve();
			});
		});
		const explicitNextFetch = await postJson(configured.app, "/runtime/v1/deferred/resume", command);
		expect(explicitNextFetch.status).toBe(202);
		await completedEvent;
		expect(fetches).toBe(2);
		expect(configured.runtime.readDeferred(STREAM)).toBeUndefined();
	});

	it("does not acknowledge resume before durable suspension reconciliation succeeds", async () => {
		const eventStore = new BlockingEventStore();
		eventStore.blockedType = "run.suspended";
		let fetches = 0;
		const configured = await fixture({
			eventStore,
			transcript: transcript(),
			modelStream: deferredResponse,
			deferredResponses: {
				fetch: async () => {
					fetches += 1;
					return assistant([{ type: "text", text: "should-not-run" }]);
				},
				cancel: async () => undefined,
			},
		});
		await configured.runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = configured.runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		const response = await postJson(configured.app, "/runtime/v1/deferred/resume", {
			...STREAM,
			operationId: pending.operationId,
		});
		expect(response.status).toBe(500);
		expect(await response.json()).toEqual({
			error: { code: "internal_error", message: "The runtime server could not complete the request." },
		});
		expect(fetches).toBe(0);
		expect(configured.runtime.readDeferred(STREAM)).toMatchObject({ operationId: pending.operationId });
	});

	it("reports provider cancellation without weakening durable local abandonment", async () => {
		const cases = [
			{ expected: "completed", adapter: { fetch: async () => assistant([]), cancel: async () => undefined } },
			{
				expected: "failed",
				adapter: {
					fetch: async () => assistant([]),
					cancel: async () => {
						throw new Error("provider unavailable");
					},
				},
			},
			{ expected: "unavailable", adapter: undefined },
		] as const;
		for (const testCase of cases) {
			const configured = await fixture({
				transcript: transcript(),
				modelStream: deferredResponse,
				...(testCase.adapter === undefined ? {} : { deferredResponses: testCase.adapter }),
			});
			await configured.runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
			const pending = configured.runtime.readDeferred(STREAM);
			if (!pending) throw new Error("Expected a pending deferred operation.");
			const response = await postJson(configured.app, "/runtime/v1/deferred/abandon", {
				...STREAM,
				operationId: pending.operationId,
			});
			expect(response.status).toBe(200);
			expect(await response.json()).toEqual({
				operationId: pending.operationId,
				providerCancellation: testCase.expected,
			});
			expect(configured.runtime.readDeferred(STREAM)).toBeUndefined();
			let cursor = "0";
			let terminal: CanonicalEventData | undefined;
			while (true) {
				const events = await configured.runtime.replayEvents(STREAM, { afterRevision: cursor, limit: 10 });
				if (events.kind !== "page") throw new Error("Expected a deferred lifecycle event page.");
				if (events.events.length === 0) break;
				terminal = events.events.at(-1)?.data;
				cursor = events.cursor;
			}
			expect(terminal).toEqual({ type: "run.completed", status: "aborted" });
		}
	});

	it("projects audience before returning events while advancing the scanned cursor", async () => {
		const access: AuthorizedRuntimeStream = {
			stream: STREAM,
			isOwner: false,
			recipientIds: ["player"],
			allowInternal: false,
		};
		const { app, eventStore } = await fixture({ access });
		await appendAudienceEvents(eventStore);
		const response = await app.request("/runtime/v1/events", {
			method: "POST",
			headers: { "content-type": "application/json" },
			body: JSON.stringify({ ...STREAM, afterRevision: "0", limit: 10 }),
		});
		const body = (await response.json()) as { cursor: string; events: CanonicalEvent[] };

		expect(response.status).toBe(200);
		expect(body.cursor).toBe("4");
		expect(body.events.map((event) => event.data.type)).toEqual(["item.started", "item.started"]);
		expect(body.events[1]?.audience).toEqual({ visibility: "recipient", recipientIds: ["player"] });
		expect(JSON.stringify(body)).not.toContain("private");
		expect(JSON.stringify(body)).not.toContain("other");
	});

	it("replaces an oversized JSON result with an in-budget error response", async () => {
		const { app, eventStore } = await fixture({
			limits: { ...SERVER_LIMITS, maxResponseBytes: MINIMUM_RESPONSE_BYTES },
		});
		await eventStore.append(STREAM, "empty", COORDINATE, {
			type: "item.started",
			item: {
				itemType: "message",
				itemId: "large-public-item",
				role: "assistant",
				content: [{ type: "text", text: "x".repeat(MINIMUM_RESPONSE_BYTES), sourceIndex: 0 }],
				audience: { visibility: "public" },
			},
		});
		const response = await app.request("/runtime/v1/events", {
			method: "POST",
			headers: { "content-type": "application/json" },
			body: JSON.stringify({ ...STREAM, afterRevision: "0", limit: 10 }),
		});

		expect(response.status).toBe(500);
		expect(Number(response.headers.get("content-length"))).toBe(MINIMUM_RESPONSE_BYTES);
		expect(await response.json()).toEqual(RESPONSE_TOO_LARGE);
	});

	it("returns an explicit reconciliation gap for a cursor beyond the authority head", async () => {
		const { app } = await fixture();
		const response = await app.request("/runtime/v1/events", {
			method: "POST",
			headers: { "content-type": "application/json" },
			body: JSON.stringify({ ...STREAM, afterRevision: "9", limit: 10 }),
		});
		expect(await response.json()).toEqual({
			kind: "gap",
			requestedAfterRevision: "9",
			earliestAvailableRevision: null,
			nextAfterRevision: "0",
			requiresTranscriptReconciliation: true,
		});
	});

	it("replays SSE with revision IDs and a cursor frame without exposing filtered events", async () => {
		const access: AuthorizedRuntimeStream = {
			stream: STREAM,
			isOwner: false,
			recipientIds: ["player"],
			allowInternal: false,
		};
		const { app, eventStore } = await fixture({ access });
		await appendAudienceEvents(eventStore);
		const abort = new AbortController();
		const response = await app.request(`/runtime/v1/events/stream?sessionId=session&actorId=actor`, {
			headers: { authorization: "Bearer test" },
			signal: abort.signal,
		});
		expect(response.headers.get("content-type")).toContain("text/event-stream");
		const reader = response.body?.getReader();
		if (!reader) throw new Error("Expected an SSE body.");
		const decoder = new TextDecoder();
		let text = "";
		while (!text.includes("event: cursor")) {
			const chunk = await reader.read();
			if (chunk.done) break;
			text += decoder.decode(chunk.value, { stream: true });
		}
		abort.abort();
		await reader.cancel();

		expect(text).toContain("id: 2");
		expect(text).toContain("id: 3");
		expect(text).toContain("id: 4");
		expect(text).toContain("event: cursor");
		expect(text).not.toContain("private");
		expect(text).not.toContain("other");
	});

	it("closes before an oversized projected frame and recovers it from the unchanged cursor", async () => {
		const cursorFrame = 'event: cursor\ndata: {"type":"cursor","revision":"0"}\nid: 0\n\n';
		const { app, runtime } = await fixture({
			limits: { ...SERVER_LIMITS, maxSseEventBytes: Buffer.byteLength(cursorFrame) },
		});
		const response = await app.request("/runtime/v1/events/stream?sessionId=session&actorId=actor", {
			headers: { authorization: "Bearer test" },
		});
		const reader = response.body?.getReader();
		if (!reader) throw new Error("Expected an SSE body.");
		const first = await reader.read();
		expect(new TextDecoder().decode(first.value)).toBe(cursorFrame);

		await runtime.submit({ ...STREAM, inputId: "oversized-live-input", payload: {} });
		const closed = await reader.read();

		expect(closed.done).toBe(true);
		expect(closed.value).toBeUndefined();

		const resumedApp = createRuntimeServer({
			runtime,
			access: {
				authenticate: () => ({ principalId: "player" }),
				authorize: () => ({
					stream: STREAM,
					isOwner: true,
					recipientIds: ["player"],
					allowInternal: false,
				}),
			},
			limits: SERVER_LIMITS,
		});
		const abort = new AbortController();
		const resumed = await resumedApp.request("/runtime/v1/events/stream?sessionId=session&actorId=actor", {
			headers: { authorization: "Bearer test", "Last-Event-ID": "0" },
			signal: abort.signal,
		});
		const resumedReader = resumed.body?.getReader();
		if (!resumedReader) throw new Error("Expected a resumed SSE body.");
		let resumedText = "";
		while (!resumedText.includes("event: event")) {
			const next = await resumedReader.read();
			if (next.done) break;
			resumedText += new TextDecoder().decode(next.value, { stream: true });
		}
		abort.abort();
		await resumedReader.cancel();

		expect(resumedText).toContain("event: event");
		expect(resumedText).toContain("id: 1");
	});

	it("recovers unqueued events from the last delivered cursor after the live SSE backlog overflows", async () => {
		const eventStore = new ReadGatedEventStore({
			newEventId: (() => {
				let next = 0;
				return () => {
					next += 1;
					return `event-${next}`;
				};
			})(),
			now: () => "2026-08-30T00:00:00.000Z",
		});
		const controlled = controlledResponse();
		const { app, runtime } = await fixture({
			eventStore,
			modelStream: controlled.streamFn,
			limits: { ...SERVER_LIMITS, maxSseBacklog: 1 },
		});
		const readEntered = deferred();
		const releaseRead = deferred();
		eventStore.readGate = { entered: readEntered.resolve, release: releaseRead.promise };

		const first = await app.request("/runtime/v1/events/stream?sessionId=session&actorId=actor", {
			headers: { authorization: "Bearer test" },
		});
		const firstBody = first.text();
		await readEntered.promise;
		await runtime.submit({ ...STREAM, inputId: "first", payload: {} });
		await controlled.started;
		const active = runtime.readActiveCoordinate(STREAM);
		if (!active) throw new Error("Expected an active run.");
		await runtime.control(active, { type: "steer", text: "overflow" });
		await new Promise<void>((resolve) => setImmediate(resolve));
		releaseRead.resolve();
		const text = await firstBody;

		expect(text).toContain("event: event");
		const deliveredIds = [...text.matchAll(/^id: ([0-9]+)$/gmu)].map((match) => match[1]);
		const cursor = deliveredIds.at(-1);
		if (!cursor) throw new Error("Expected the overflowed stream to deliver a durable event identifier.");

		const abort = new AbortController();
		const resumed = await app.request("/runtime/v1/events/stream?sessionId=session&actorId=actor", {
			headers: { authorization: "Bearer test", "Last-Event-ID": cursor },
			signal: abort.signal,
		});
		const reader = resumed.body?.getReader();
		if (!reader) throw new Error("Expected a resumed SSE body.");
		let resumedText = "";
		while (!resumedText.includes("event: cursor")) {
			const next = await reader.read();
			if (next.done) break;
			resumedText += new TextDecoder().decode(next.value, { stream: true });
		}
		abort.abort();
		await reader.cancel();

		expect(resumedText).toContain("event: event");
		expect(resumedText).not.toMatch(new RegExp(`^id: ${cursor}$`, "mu"));
		const recoveredCursor = /event: cursor\ndata: \{"type":"cursor","revision":"([0-9]+)"\}/u.exec(resumedText)?.[1];
		expect(BigInt(recoveredCursor ?? "0")).toBeGreaterThan(BigInt(cursor));
		controlled.finish();
	});
});
