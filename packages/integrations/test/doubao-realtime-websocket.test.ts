import { once } from "node:events";
import type { AddressInfo } from "node:net";
import { Buffer } from "node:buffer";
import { describe, expect, it } from "vitest";
import { type WebSocket, WebSocketServer } from "ws";
import type { DoubaoWebSocketConnection, DoubaoWebSocketConnectRequest } from "../src/index.js";
import { connectDoubaoWebSocket } from "../src/doubao-realtime-websocket.js";

describe("Doubao realtime WebSocket queue", () => {
	it("drains buffered frames in FIFO order before the closed event", async () => {
		const fixture = await openSocket(4);
		try {
			await send(fixture.peer, "first");
			await send(fixture.peer, Buffer.from([2]), true);
			await send(fixture.peer, "third");
			const peerClosed = once(fixture.peer, "close");
			fixture.peer.close(1000);
			await peerClosed;

			const signal = new AbortController().signal;
			await expect(fixture.connection.receive(signal)).resolves.toEqual({ type: "text", text: "first" });
			await expect(fixture.connection.receive(signal)).resolves.toEqual({
				type: "binary",
				bytes: Uint8Array.from([2]),
			});
			await expect(fixture.connection.receive(signal)).resolves.toEqual({ type: "text", text: "third" });
			await expect(fixture.connection.receive(signal)).resolves.toEqual({ type: "closed", code: 1000 });
		} finally {
			await fixture.dispose();
		}
	});

	it("fails closed and discards buffered frames when capacity is exceeded", async () => {
		const fixture = await openSocket(2);
		try {
			const peerClosed = once(fixture.peer, "close");
			await send(fixture.peer, "first");
			await send(fixture.peer, "second");
			await send(fixture.peer, "overflow");
			await peerClosed;

			await expect(fixture.connection.receive(new AbortController().signal)).rejects.toMatchObject({
				code: "capacity",
			});
		} finally {
			await fixture.dispose();
		}
	});

	it("preserves a later frame after one waiting receiver is aborted and closes cleanly", async () => {
		const fixture = await openSocket(2);
		try {
			const receiver = new AbortController();
			const reason = Object.freeze({ kind: "test-receiver-abort" });
			const pending = fixture.connection.receive(receiver.signal);
			receiver.abort(reason);
			await expect(pending).rejects.toBe(reason);

			const next = fixture.connection.receive(new AbortController().signal);
			await send(fixture.peer, "after-abort");
			await expect(next).resolves.toEqual({ type: "text", text: "after-abort" });

			const peerClosed = once(fixture.peer, "close");
			await fixture.connection.close(new AbortController().signal);
			await peerClosed;
			await expect(fixture.connection.receive(new AbortController().signal)).resolves.toEqual({
				type: "closed",
				code: 1000,
			});
		} finally {
			await fixture.dispose();
		}
	});
});

interface SocketFixture {
	readonly connection: DoubaoWebSocketConnection;
	readonly peer: WebSocket;
	dispose(): Promise<void>;
}

async function openSocket(maxPendingFrames: number): Promise<SocketFixture> {
	const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
	await once(server, "listening");
	const accepted = Promise.withResolvers<WebSocket>();
	server.once("connection", (socket) => accepted.resolve(socket));
	const address = server.address() as AddressInfo;
	const operation = new AbortController();
	const request: DoubaoWebSocketConnectRequest = {
		endpoint: `ws://127.0.0.1:${address.port}`,
		headers: {},
		maxFrameBytes: 1024,
		maxPendingFrames,
		maxWebSocketFragments: 8,
		maxWebSocketBufferedChunks: 8,
		connectTimeoutMs: 1000,
		closeTimeoutMs: 1000,
	};
	const [connection, peer] = await Promise.all([connectDoubaoWebSocket(request, operation.signal), accepted.promise]);
	return {
		connection,
		peer,
		async dispose() {
			connection.abort(new Error("test cleanup"));
			operation.abort(new Error("test cleanup"));
			for (const client of server.clients) client.terminate();
			await new Promise<void>((resolve) => server.close(() => resolve()));
		},
	};
}

function send(peer: WebSocket, data: string | Buffer, binary = false): Promise<void> {
	return new Promise((resolve, reject) => {
		peer.send(data, { binary }, (error) => {
			if (error) reject(error);
			else resolve();
		});
	});
}
