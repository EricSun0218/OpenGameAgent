import { describe, expect, it, vi } from "vitest";
import type { OpenAIRealtimeWire } from "./openai-realtime.js";
import { OpenAIRealtimeTransport } from "./openai-realtime.js";

class FakeWire implements OpenAIRealtimeWire {
	readonly sent: Record<string, unknown>[] = [];
	private eventHandler: (event: Record<string, unknown>) => void = () => undefined;
	private errorHandler: (error: unknown) => void = () => undefined;
	private closeHandler: () => void = () => undefined;
	closed = 0;

	send(event: Record<string, unknown>): void {
		this.sent.push(structuredClone(event));
	}
	close(): void {
		this.closed += 1;
		this.closeHandler();
	}
	onEvent(handler: (event: Record<string, unknown>) => void): void {
		this.eventHandler = handler;
	}
	onError(handler: (error: unknown) => void): void {
		this.errorHandler = handler;
	}
	onClose(handler: () => void): void {
		this.closeHandler = handler;
	}
	emit(event: Record<string, unknown>): void {
		this.eventHandler(event);
	}
	fail(error: unknown): void {
		this.errorHandler(error);
	}
}

async function take(session: Awaited<ReturnType<OpenAIRealtimeTransport["connect"]>>, count: number) {
	const values = [];
	for await (const event of session.events()) {
		values.push(event);
		if (values.length === count) break;
	}
	return values;
}

describe("OpenAIRealtimeTransport", () => {
	it("maps the current realtime protocol without exposing provider authority to the agent loop", async () => {
		const wire = new FakeWire();
		const factory = vi.fn(async (options: { endpoint: string; model: string; apiKey: string }) => {
			expect(options).toEqual({ endpoint: "https://api.example.test/v1", model: "gpt-realtime", apiKey: "secret" });
			return wire;
		});
		const transport = new OpenAIRealtimeTransport({
			endpoint: "https://api.example.test/v1",
			authentication: { resolve: async () => ({ apiKey: "secret" }) },
			wireFactory: factory,
		});
		const session = await transport.connect({
			model: "gpt-realtime",
			voice: "coral",
			instructions: "stay in character",
		});
		expect(wire.sent[0]).toMatchObject({
			type: "session.update",
			session: { model: "gpt-realtime", instructions: "stay in character", output_modalities: ["audio"] },
		});
		await session.sendAudio({ pcm16: new Uint8Array([0, 0, 1, 0]), sampleRate: 24_000, channels: 1 });
		await session.sendText("hello", "user");
		await session.cancelResponse();
		await session.truncateAudio("item-1", 120);
		expect(wire.sent.map((event) => event["type"])).toEqual([
			"session.update",
			"input_audio_buffer.append",
			"conversation.item.create",
			"response.create",
			"response.cancel",
			"conversation.item.truncate",
		]);

		wire.emit({
			type: "conversation.item.input_audio_transcription.completed",
			item_id: "input-1",
			transcript: "build a house",
		});
		wire.emit({
			type: "response.output_audio.delta",
			item_id: "audio-1",
			response_id: "response-1",
			delta: Buffer.from([0, 0, 1, 0]).toString("base64"),
		});
		wire.emit({
			type: "response.function_call_arguments.done",
			name: "handoff",
			call_id: "call-1",
			arguments: JSON.stringify({ transcript: "build a house", context: { biome: "forest" } }),
		});
		wire.emit({ type: "response.done", response: { id: "response-1", status: "completed" } });
		const events = await take(session, 4);
		expect(events.map((event) => event.type)).toEqual([
			"input.transcript.completed",
			"output.audio",
			"handoff.requested",
			"response.completed",
		]);
		expect(events[2]).toMatchObject({ handoff: { handoffId: "call-1", transcript: "build a house" } });
		await session.close();
		expect(wire.closed).toBe(1);
	});

	it("fails closed on missing credentials and redacts provider errors", async () => {
		await expect(
			new OpenAIRealtimeTransport({ wireFactory: async () => new FakeWire() }).connect({
				model: "gpt-realtime",
				voice: "voice",
			}),
		).rejects.toThrow("authentication");
		const wire = new FakeWire();
		const transport = new OpenAIRealtimeTransport({
			requiresCredential: false,
			endpoint: "http://127.0.0.1:8080/v1",
			wireFactory: async () => wire,
		});
		const session = await transport.connect({ model: "local-realtime", voice: "voice" });
		wire.fail(new Error("secret response body"));
		const events = await take(session, 1);
		expect(events[0]).toMatchObject({ type: "error", category: "provider", message: "Realtime provider error." });
		expect(JSON.stringify(events)).not.toContain("secret response body");
		await session.close();
	});

	it("rejects insecure remote endpoints before opening a socket", () => {
		expect(() => new OpenAIRealtimeTransport({ endpoint: "http://provider.example.test/v1" })).toThrow("HTTPS");
	});
});
