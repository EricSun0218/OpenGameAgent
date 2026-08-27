import { createGameActionOperationId, InMemoryGameActionJournal } from "@opengameagent/actions";
import { InMemoryGameConversationStore } from "@opengameagent/kernel";
import type {
	GameActionIntent,
	GameActionReceipt,
	GameAgentEvent,
	GameAgentKernelPort,
	GameControlResult,
	GameInput,
	GameKernelRunRequest,
} from "@opengameagent/protocol";
import { GameAgentRuntime, type GameRuntimeEventStore } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { GameAgentServer, type GameServerAuthenticator, OwnerGameServerAuthorizer } from "./server.js";

const input = (ownerId: string): GameInput => ({
	id: "input",
	type: "npc.chat",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId,
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 1 },
	content: [{ type: "text", text: "hello" }],
});

class ServerTestKernel implements GameAgentKernelPort {
	readonly requests: GameKernelRunRequest[] = [];

	async *run(request: GameKernelRunRequest): AsyncIterable<GameAgentEvent> {
		this.requests.push(request);
		const common = {
			runId: request.runId,
			turn: 1,
			audience: { visibility: "owner" } as const,
			timestamp: 1,
		};
		yield { ...common, type: "run.started", sequence: 1, eventId: `${request.runId}:1`, inputId: request.input.id };
		yield {
			...common,
			type: "tool.started",
			sequence: 2,
			eventId: `${request.runId}:2`,
			call: { id: "call", name: "private_tool", arguments: { secret: "must-not-cross-wire" } },
		};
		yield { ...common, type: "run.completed", sequence: 3, eventId: `${request.runId}:3` };
	}

	steer(): GameControlResult {
		return { accepted: true };
	}

	followUp(): GameControlResult {
		return { accepted: true };
	}

	abort(): GameControlResult {
		return { accepted: true };
	}
}

const authenticator: GameServerAuthenticator = {
	async authenticate(request) {
		const { token } = request.credential ?? {};
		return typeof token === "string" ? { id: token } : undefined;
	},
};

const servers: GameAgentServer[] = [];

async function start(
	kernel: ServerTestKernel,
	stores: {
		actionJournal?: InMemoryGameActionJournal;
		conversationStore?: InMemoryGameConversationStore;
		eventStore?: GameRuntimeEventStore;
	} = {},
): Promise<string> {
	const runtime = new GameAgentRuntime({ kernel, baseSystemPrompt: "base" });
	const server = new GameAgentServer({
		runtime,
		authenticator,
		authorizer: new OwnerGameServerAuthorizer(),
		...stores,
	});
	servers.push(server);
	const address = await server.listen();
	return `http://${address.host}:${address.port}`;
}

function actionIntent(): GameActionIntent {
	const gameInput = input("owner-a");
	const identity = {
		session: gameInput.session,
		inputId: gameInput.id,
		runId: "run-action",
		turn: 1,
		toolCallIndex: 0,
		action: "move_character",
	};
	return {
		...identity,
		operationId: createGameActionOperationId(identity),
		args: { x: 1.5, y: -4.25 },
		moment: gameInput.moment,
		expectedRevision: 2,
	};
}

function actionReceipt(intent: GameActionIntent): GameActionReceipt {
	return {
		operationId: intent.operationId,
		session: intent.session,
		action: intent.action,
		expectedRevision: intent.expectedRevision,
		stateRevision: 3,
		status: "committed",
		result: { moved: true },
	};
}

afterEach(async () => {
	for (const server of servers.splice(0)) await server.close();
});

describe("GameAgentServer", () => {
	it("derives ownership from authentication and rejects payload-only impersonation before runtime access", async () => {
		const kernel = new ServerTestKernel();
		const baseUrl = await start(kernel);
		const response = await fetch(`${baseUrl}/v1/runs/stream`, {
			method: "POST",
			headers: { "content-type": "application/json" },
			body: JSON.stringify({ authentication: { token: "owner-b" }, input: input("owner-a"), runId: "unauthorized" }),
		});

		expect(response.status).toBe(403);
		expect(kernel.requests).toHaveLength(0);
	});

	it("streams authorized events while stripping non-internal tool details", async () => {
		const kernel = new ServerTestKernel();
		const baseUrl = await start(kernel);
		const response = await fetch(`${baseUrl}/v1/runs/stream`, {
			method: "POST",
			headers: { "content-type": "application/json" },
			body: JSON.stringify({ authentication: { token: "owner-a" }, input: input("owner-a"), runId: "authorized" }),
		});
		const stream = await response.text();

		expect(response.status).toBe(200);
		expect(kernel.requests).toHaveLength(1);
		expect(stream).toContain("event: run.started");
		expect(stream).toContain('"name":"private_tool"');
		expect(stream).toContain('"arguments":{}');
		expect(stream).not.toContain("must-not-cross-wire");
	});

	it("keeps body credentials outside the model-visible input", async () => {
		const kernel = new ServerTestKernel();
		const baseUrl = await start(kernel);
		await fetch(`${baseUrl}/v1/runs/stream`, {
			method: "POST",
			headers: { "content-type": "application/json" },
			body: JSON.stringify({
				authentication: { token: "owner-a", pairing: "bounded-secret" },
				input: input("owner-a"),
			}),
		});

		expect(JSON.stringify(kernel.requests)).not.toContain("bounded-secret");
	});

	it("exposes durable action claim, receipt, and reconcile without creating duplicate actions", async () => {
		const kernel = new ServerTestKernel();
		const actionJournal = new InMemoryGameActionJournal();
		const intent = actionIntent();
		await actionJournal.prepare(intent);
		const baseUrl = await start(kernel, { actionJournal });
		const claim = await fetch(`${baseUrl}/v1/actions/claim`, {
			method: "POST",
			body: JSON.stringify({ authentication: { token: "owner-a" }, session: intent.session, maximum: 1 }),
		});
		const claimed = await claim.json();
		expect(claim.status).toBe(200);
		expect(claimed).toMatchObject({
			claims: [{ kind: "dispatch", entry: { attempt: 1, intent: { operationId: intent.operationId } } }],
		});

		const duplicate = await fetch(`${baseUrl}/v1/actions/claim`, {
			method: "POST",
			body: JSON.stringify({ authentication: { token: "owner-a" }, session: intent.session }),
		});
		expect(await duplicate.json()).toMatchObject({ claims: [{ kind: "reconcile", entry: { attempt: 1 } }] });

		const submitted = await fetch(`${baseUrl}/v1/actions/receipt`, {
			method: "POST",
			body: JSON.stringify({
				authentication: { token: "owner-a" },
				session: intent.session,
				receipt: actionReceipt(intent),
			}),
		});
		expect(await submitted.json()).toMatchObject({ entry: { status: "committed", attempt: 1 } });

		const reconcile = await fetch(`${baseUrl}/v1/actions/reconcile`, {
			method: "POST",
			body: JSON.stringify({
				authentication: { token: "owner-a" },
				session: intent.session,
				operationId: intent.operationId,
			}),
		});
		expect(await reconcile.json()).toMatchObject({ entry: { status: "committed", receipt: { stateRevision: 3 } } });
	});

	it("authorizes and paginates transcript reads without exposing reasoning, tool details, or inline image bytes", async () => {
		const kernel = new ServerTestKernel();
		const conversationStore = new InMemoryGameConversationStore();
		const gameInput = input("owner-a");
		await conversationStore.save(gameInput.session, 0, [
			{ role: "user", content: "hello", timestamp: 1 },
			{
				role: "assistant",
				content: [
					{ type: "reasoning", text: "hidden-chain", signature: "opaque" },
					{ type: "toolCall", id: "call", name: "private", arguments: { hidden: true } },
					{ type: "text", text: "visible" },
					{ type: "image", mimeType: "image/png", data: "inline-private-bytes" },
				],
				api: "api",
				provider: "provider",
				model: "model",
				usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0, totalTokens: 2 },
				stopReason: "stop",
				timestamp: 2,
			},
			{
				role: "toolResult",
				toolCallId: "call",
				toolName: "private",
				content: [{ type: "text", text: "tool-private" }],
				isError: false,
				timestamp: 3,
			},
		]);
		const baseUrl = await start(kernel, { conversationStore });
		const unauthorized = await fetch(`${baseUrl}/v1/sessions/transcript/read`, {
			method: "POST",
			body: JSON.stringify({ authentication: { token: "owner-b" }, session: gameInput.session }),
		});
		expect(unauthorized.status).toBe(403);

		const first = await fetch(`${baseUrl}/v1/sessions/transcript/read`, {
			method: "POST",
			body: JSON.stringify({ authentication: { token: "owner-a" }, session: gameInput.session, limit: 1 }),
		});
		const firstPage = (await first.json()) as { nextCursor: string };
		expect(firstPage.nextCursor).toBeTypeOf("string");
		const second = await fetch(`${baseUrl}/v1/sessions/transcript/read`, {
			method: "POST",
			body: JSON.stringify({
				authentication: { token: "owner-a" },
				session: gameInput.session,
				limit: 2,
				cursor: firstPage.nextCursor,
			}),
		});
		const secondPage = JSON.stringify(await second.json());
		expect(secondPage).toContain("visible");
		expect(secondPage).toContain('"inline":false');
		expect(secondPage).not.toContain("hidden-chain");
		expect(secondPage).not.toContain("tool-private");
		expect(secondPage).not.toContain("inline-private-bytes");
	});

	it("replays authorized run events after an exact sequence without touching the store on denied viewers", async () => {
		const kernel = new ServerTestKernel();
		const gameInput = input("owner-a");
		let reads = 0;
		const stored: GameAgentEvent[] = [
			{
				type: "message.delta",
				sequence: 2,
				eventId: "run:2",
				runId: "run",
				turn: 1,
				audience: { visibility: "owner" },
				timestamp: 1,
				text: "resumed",
			},
		];
		const eventStore: GameRuntimeEventStore = {
			async append() {},
			async read() {
				reads += 1;
				return stored;
			},
		};
		const baseUrl = await start(kernel, { eventStore });
		const denied = await fetch(`${baseUrl}/v1/runs/events/read`, {
			method: "POST",
			body: JSON.stringify({
				authentication: { token: "owner-b" },
				session: gameInput.session,
				runId: "run",
				afterSequence: 1,
			}),
		});
		expect(denied.status).toBe(403);
		expect(reads).toBe(0);

		const allowed = await fetch(`${baseUrl}/v1/runs/events/read`, {
			method: "POST",
			body: JSON.stringify({
				authentication: { token: "owner-a" },
				session: gameInput.session,
				runId: "run",
				afterSequence: 1,
			}),
		});
		expect(await allowed.json()).toEqual({ events: stored, gap: false, nextSequence: 2 });
		expect(reads).toBe(1);
	});
});
