import { Buffer } from "node:buffer";
import WebSocket, { type ClientOptions, type RawData } from "ws";
import Queue from "yocto-queue";
import {
	DoubaoRealtimeSpeechAdapterError,
	type DoubaoWebSocketConnection,
	type DoubaoWebSocketConnectRequest,
	type DoubaoWebSocketMessage,
} from "./doubao-realtime-speech-types.js";

export async function connectDoubaoWebSocket(
	request: Readonly<DoubaoWebSocketConnectRequest>,
	signal: AbortSignal,
): Promise<DoubaoWebSocketConnection> {
	signal.throwIfAborted();
	const options = {
		headers: { ...request.headers },
		followRedirects: false,
		handshakeTimeout: request.connectTimeoutMs,
		maxPayload: request.maxFrameBytes,
		maxFragments: request.maxWebSocketFragments,
		maxBufferedChunks: request.maxWebSocketBufferedChunks,
		perMessageDeflate: false,
	} as ClientOptions & { readonly maxFragments: number; readonly maxBufferedChunks: number };
	return new Promise<DoubaoWebSocketConnection>((resolve, reject) => {
		let settled = false;
		const socket = new WebSocket(request.endpoint, options);
		const ignoreError = () => undefined;
		const cleanup = () => {
			signal.removeEventListener("abort", onAbort);
			socket.off("open", onOpen);
			socket.off("error", onError);
			socket.off("close", onClose);
			socket.off("unexpected-response", onUnexpectedResponse);
		};
		const fail = (error: unknown) => {
			if (settled) return;
			settled = true;
			cleanup();
			socket.on("error", ignoreError);
			socket.terminate();
			reject(error);
		};
		const onAbort = () => fail(signal.reason);
		const onError = () => fail(new DoubaoRealtimeSpeechAdapterError("transport"));
		const onClose = () => fail(new DoubaoRealtimeSpeechAdapterError("transport"));
		const onUnexpectedResponse = (_request: unknown, response: { resume(): void }) => {
			response.resume();
			fail(new DoubaoRealtimeSpeechAdapterError("transport"));
		};
		const onOpen = () => {
			if (settled) return;
			settled = true;
			const connection = createOpenedDoubaoWebSocketConnection(socket, request, signal);
			cleanup();
			resolve(connection);
		};
		signal.addEventListener("abort", onAbort, { once: true });
		socket.once("open", onOpen);
		socket.once("error", onError);
		socket.once("close", onClose);
		socket.once("unexpected-response", onUnexpectedResponse);
		if (signal.aborted) onAbort();
	});
}

function createOpenedDoubaoWebSocketConnection(
	socket: WebSocket,
	request: Readonly<DoubaoWebSocketConnectRequest>,
	operationSignal: AbortSignal,
): DoubaoWebSocketConnection {
	type Receiver = {
		readonly resolve: (message: Readonly<DoubaoWebSocketMessage>) => void;
		readonly reject: (error: unknown) => void;
		readonly signal: AbortSignal;
		readonly onAbort: () => void;
	};
	const queued = new Queue<DoubaoWebSocketMessage>();
	let receiver: Receiver | undefined;
	let terminalError: unknown;
	let closed: Extract<DoubaoWebSocketMessage, { type: "closed" }> | undefined;
	const removeReceiverAbort = (current: Receiver | undefined) => {
		if (current) current.signal.removeEventListener("abort", current.onAbort);
	};
	const rejectReceiver = (error: unknown) => {
		const current = receiver;
		receiver = undefined;
		removeReceiverAbort(current);
		current?.reject(error);
	};
	const deliver = (message: DoubaoWebSocketMessage) => {
		if (receiver) {
			const current = receiver;
			receiver = undefined;
			removeReceiverAbort(current);
			current.resolve(message);
			return;
		}
		if (queued.size >= request.maxPendingFrames) {
			fail(new DoubaoRealtimeSpeechAdapterError("capacity"));
			return;
		}
		queued.enqueue(message);
	};
	const fail = (error: unknown) => {
		if (terminalError !== undefined || closed !== undefined) return;
		terminalError = error;
		queued.clear();
		rejectReceiver(error);
		socket.terminate();
	};
	const onOperationAbort = () => fail(operationSignal.reason);
	const onMessage = (data: RawData, isBinary: boolean) => {
		if (!isBinary) {
			deliver({ type: "text", text: rawDataToBuffer(data).toString("utf8") });
			return;
		}
		deliver({ type: "binary", bytes: Uint8Array.from(rawDataToBuffer(data)) });
	};
	const onError = () => fail(new DoubaoRealtimeSpeechAdapterError("transport"));
	const onClose = (code: number) => {
		operationSignal.removeEventListener("abort", onOperationAbort);
		if (terminalError !== undefined) return;
		closed = Object.freeze({ type: "closed", code });
		deliver(closed);
	};
	operationSignal.addEventListener("abort", onOperationAbort, { once: true });
	socket.on("message", onMessage);
	socket.on("error", onError);
	socket.once("close", onClose);
	if (operationSignal.aborted) onOperationAbort();
	const connection: DoubaoWebSocketConnection = {
		bufferedAmount: () => socket.bufferedAmount,
		send(frame, signal) {
			signal.throwIfAborted();
			if (terminalError !== undefined) return Promise.reject(terminalError);
			if (socket.readyState !== WebSocket.OPEN) {
				return Promise.reject(new DoubaoRealtimeSpeechAdapterError("transport"));
			}
			const copy = Buffer.from(frame);
			return new Promise<void>((resolve, reject) => {
				socket.send(copy, { binary: true, compress: false }, (error) => {
					if (error) reject(new DoubaoRealtimeSpeechAdapterError("transport"));
					else resolve();
				});
			});
		},
		receive(signal) {
			signal.throwIfAborted();
			if (terminalError !== undefined) return Promise.reject(terminalError);
			const next = queued.dequeue();
			if (next) return Promise.resolve(next);
			if (closed) return Promise.resolve(closed);
			if (receiver) return Promise.reject(new DoubaoRealtimeSpeechAdapterError("protocol"));
			return new Promise<Readonly<DoubaoWebSocketMessage>>((resolve, reject) => {
				const onAbort = () => {
					if (receiver?.onAbort !== onAbort) return;
					receiver = undefined;
					reject(signal.reason);
				};
				receiver = { resolve, reject, signal, onAbort };
				signal.addEventListener("abort", onAbort, { once: true });
				if (signal.aborted) onAbort();
			});
		},
		close(signal) {
			signal.throwIfAborted();
			if (socket.readyState === WebSocket.CLOSED) return Promise.resolve();
			return new Promise<void>((resolve, reject) => {
				let done = false;
				const finish = (error?: unknown) => {
					if (done) return;
					done = true;
					clearTimeout(timer);
					signal.removeEventListener("abort", abortClose);
					socket.off("close", closeComplete);
					if (error === undefined) resolve();
					else reject(error);
				};
				const closeComplete = () => finish();
				const abortClose = () => {
					socket.terminate();
					finish(signal.reason);
				};
				const timer = setTimeout(() => {
					socket.terminate();
					finish(new DoubaoRealtimeSpeechAdapterError("timeout"));
				}, request.closeTimeoutMs);
				signal.addEventListener("abort", abortClose, { once: true });
				socket.once("close", closeComplete);
				socket.close(1000);
				if (signal.aborted) abortClose();
			});
		},
		abort(reason) {
			fail(reason ?? new DoubaoRealtimeSpeechAdapterError("transport"));
		},
	};
	return Object.freeze(connection);
}

function rawDataToBuffer(data: RawData): Buffer {
	if (Array.isArray(data)) return Buffer.concat(data);
	if (data instanceof ArrayBuffer) return Buffer.from(data);
	return Buffer.from(data.buffer, data.byteOffset, data.byteLength);
}
