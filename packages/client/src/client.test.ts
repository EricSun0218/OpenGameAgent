import type { GameInput } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import { GameAgentClient, type GameAgentClientError } from "./client.js";

const session = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 1,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};

const input: GameInput = {
	id: "input",
	type: "chat",
	session,
	moment: { tick: 7 },
	content: [{ type: "text", text: "hello" }],
};

describe("GameAgentClient", () => {
	it("negotiates capabilities without credentials", async () => {
		const fetcher = vi.fn(
			async () =>
				new Response(JSON.stringify({ protocolVersion: 1, features: { runs: true }, limits: {} }), {
					status: 200,
					headers: { "content-type": "application/json" },
				}),
		);
		const client = new GameAgentClient({ baseUrl: "http://127.0.0.1:1234", fetch: fetcher as typeof fetch });
		expect((await client.capabilities()).protocolVersion).toBe(1);
		expect(fetcher).toHaveBeenCalledWith(new URL("http://127.0.0.1:1234/v1/capabilities"), { method: "GET" });
	});

	it("streams split SSE frames and sends body-scoped authentication", async () => {
		const encoder = new TextEncoder();
		const event = {
			sequence: 1,
			eventId: "event-1",
			runId: "run",
			turn: 0,
			audience: { visibility: "owner" },
			timestamp: 1,
			type: "run.completed",
		};
		const wire = `id: event-1\nevent: run.completed\ndata: ${JSON.stringify(event)}\n\n`;
		const fetcher = vi.fn(async (_url: URL | Request, init?: RequestInit) => {
			const sent = JSON.parse(String(init?.body));
			expect(sent.authentication).toEqual({ pairingToken: "bounded" });
			expect(sent.input).toEqual(input);
			return new Response(
				new ReadableStream({
					start(controller) {
						controller.enqueue(encoder.encode(wire.slice(0, 17)));
						controller.enqueue(encoder.encode(wire.slice(17)));
						controller.close();
					},
				}),
				{ status: 200, headers: { "content-type": "text/event-stream" } },
			);
		});
		const client = new GameAgentClient({
			baseUrl: "http://localhost:1234",
			authentication: { pairingToken: "bounded" },
			fetch: fetcher as typeof fetch,
		});
		const events = [];
		for await (const item of client.run(input)) events.push(item);
		expect(events).toEqual([event]);
	});

	it("returns structured errors without reflecting response bodies", async () => {
		const client = new GameAgentClient({
			baseUrl: "https://agent.example",
			fetch: (async () =>
				new Response(JSON.stringify({ error: "forbidden", secret: "never-echo" }), { status: 403 })) as typeof fetch,
		});
		await expect(client.readUsage(session)).rejects.toMatchObject({
			name: "GameAgentClientError",
			status: 403,
			category: "forbidden",
			message: "Game Agent server request failed (403, forbidden).",
		} satisfies Partial<GameAgentClientError>);
	});

	it("projects action deliveries as a typed stream", async () => {
		const claim = {
			kind: "reconcile",
			entry: { intent: { operationId: "op" }, status: "uncertain", attempt: 1 },
		};
		const client = new GameAgentClient({
			baseUrl: "https://agent.example",
			fetch: (async () =>
				new Response(`event: action.delivery\ndata: ${JSON.stringify(claim)}\n\n`, { status: 200 })) as typeof fetch,
		});
		const deliveries = [];
		for await (const delivery of client.streamActions(session)) deliveries.push(delivery);
		expect(deliveries).toEqual([claim]);
	});

	it("rejects plaintext remote endpoints and oversized events", async () => {
		expect(() => new GameAgentClient({ baseUrl: "http://agent.example" })).toThrow(/require HTTPS/);
		const client = new GameAgentClient({
			baseUrl: "https://agent.example",
			maximumEventBytes: 1024,
			fetch: (async () => new Response(`data: ${"x".repeat(2048)}\n\n`, { status: 200 })) as typeof fetch,
		});
		await expect(async () => {
			for await (const _event of client.run(input)) void _event;
		}).rejects.toThrow(/too large/);
	});
});
