import type { RealtimeAudioChunk } from "@opengameagent/realtime";
import { describe, expect, it, vi } from "vitest";
import {
	LocalSpeechCapacityError,
	LocalSpeechConfigurationError,
	LocalSpeechProtocolError,
	LocalSpeechProviderError,
	LocalSpeechTimeoutError,
	OpenAICompatibleSpeechRecognizer,
	OpenAICompatibleSpeechSynthesizer,
} from "../src/index.js";

const INPUT_FORMAT = { mediaType: "audio/pcm;format=s16le", sampleRateHz: 16_000, channelCount: 1 } as const;
const OUTPUT_FORMAT = { mediaType: "audio/pcm", sampleRateHz: 24_000, channelCount: 1 } as const;

const COMMON = {
	baseUrl: "http://127.0.0.1:8080/v1",
	apiKey: "local-secret",
	modelId: "local-model",
	requestTimeoutMs: 1_000,
	maxEndpointBytes: 256,
	maxCredentialBytes: 128,
	maxIdentifierBytes: 64,
	maxMediaTypeBytes: 64,
	maxResponseBytes: 128,
} as const;

const RECOGNITION_LIMITS = {
	maxInputChunks: 4,
	maxInputChunkBytes: 8,
	maxInputAudioBytes: 16,
	maxTranscriptBytes: 32,
} as const;

const SYNTHESIS_LIMITS = {
	maxTextSegments: 4,
	maxTextBytes: 32,
	maxResponseChunks: 4,
	maxResponseChunkBytes: 8,
} as const;

describe("OpenAICompatibleSpeechRecognizer", () => {
	it("rejects a configured input that is not explicitly signed little-endian PCM16", () => {
		expect(() => recognition({ inputMediaType: "audio/pcm" })).toThrow(LocalSpeechConfigurationError);
	});
	it("uses the official multipart API with a deterministic bounded PCM16 WAV", async () => {
		let request: Request | undefined;
		const recognizer = new OpenAICompatibleSpeechRecognizer({
			...COMMON,
			...RECOGNITION_LIMITS,
			inputMediaType: INPUT_FORMAT.mediaType,
			language: "zh",
			fetch: async (input: Parameters<typeof fetch>[0]) => {
				request = input instanceof Request ? input : new Request(input);
				return Response.json({ text: "开门" });
			},
		});

		const events = await collect(
			recognizer.recognize({
				format: INPUT_FORMAT,
				audio: chunks(new Uint8Array([1, 0]), new Uint8Array([2, 0])),
				signal: new AbortController().signal,
			}),
		);

		expect(events).toEqual([{ type: "transcript.final", text: "开门" }]);
		expect(request?.method).toBe("POST");
		expect(new URL(request?.url ?? "").pathname).toBe("/v1/audio/transcriptions");
		expect(request?.headers.get("authorization")).toBe("Bearer local-secret");
		const form = await request?.formData();
		expect(form?.get("model")).toBe("local-model");
		expect(form?.get("language")).toBe("zh");
		const file = form?.get("file");
		expect(file).toBeInstanceOf(File);
		const wav = new Uint8Array(await (file as File).arrayBuffer());
		expect(new TextDecoder().decode(wav.subarray(0, 4))).toBe("RIFF");
		expect(new TextDecoder().decode(wav.subarray(8, 12))).toBe("WAVE");
		const view = new DataView(wav.buffer, wav.byteOffset, wav.byteLength);
		expect(view.getUint32(4, true)).toBe(40);
		expect(view.getUint16(22, true)).toBe(1);
		expect(view.getUint32(24, true)).toBe(16_000);
		expect(view.getUint16(34, true)).toBe(16);
		expect(view.getUint32(40, true)).toBe(4);
		expect([...wav.subarray(44)]).toEqual([1, 0, 2, 0]);
	});

	it("disables official SDK retries for an unsafe transcription POST", async () => {
		const fetch = vi.fn(async () => new Response("private-upstream-detail", { status: 500 }));
		const recognizer = recognition({ fetch });
		const result = collect(recognizer.recognize(request(chunks(new Uint8Array([1, 0])))));
		await expect(result).rejects.toBeInstanceOf(LocalSpeechProviderError);
		await expect(result).rejects.not.toThrow(/private-upstream-detail/u);
		expect(fetch).toHaveBeenCalledTimes(1);
	});

	it("bounds source audio and the parsed final transcript before release", async () => {
		const fetch = vi.fn(async () => Response.json({ text: "12345" }));
		const transcript = recognition({ fetch, maxTranscriptBytes: 4 });
		await expect(collect(transcript.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechCapacityError,
		);
		const input = recognition({ fetch, maxInputAudioBytes: 1 });
		await expect(collect(input.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechCapacityError,
		);
		const malformed = recognition({ fetch });
		await expect(collect(malformed.recognize(request(chunks(new Uint8Array([1])))))).rejects.toBeInstanceOf(
			LocalSpeechProtocolError,
		);
		await expect(
			collect(
				recognition({ maxInputChunks: 1 }).recognize(request(chunks(new Uint8Array([1, 0]), new Uint8Array([2, 0])))),
			),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			collect(recognition({ maxInputChunkBytes: 1 }).recognize(request(chunks(new Uint8Array([1, 0]))))),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
	});

	it("makes no transcription POST for empty audio and bounds the raw provider response", async () => {
		const emptyFetch = vi.fn();
		await expect(
			collect(recognition({ fetch: emptyFetch as typeof fetch }).recognize(request(chunks()))),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(emptyFetch).not.toHaveBeenCalled();
		const oversized = recognition({
			maxResponseBytes: 4,
			fetch: async () => Response.json({ text: "long" }),
		});
		await expect(collect(oversized.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechCapacityError,
		);
		const falseSse = recognition({
			maxResponseBytes: 4,
			fetch: async () => new Response("a\n\na\n\na\n\n", { headers: { "content-type": "text/event-stream" } }),
		});
		await expect(collect(falseSse.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechCapacityError,
		);
	});

	it("preserves caller cancellation and applies the host timeout to the provider call", async () => {
		const caller = new AbortController();
		const reason = new Error("stop-stt");
		const pendingFetch: typeof fetch = (input) =>
			new Promise<Response>((_resolve, reject) => {
				const signal = (input as Request).signal;
				signal.addEventListener("abort", () => reject(signal.reason), { once: true });
			});
		const cancelled = recognition({ fetch: pendingFetch });
		const cancelledResult = collect(
			cancelled.recognize({
				...request(chunks(new Uint8Array([1, 0]))),
				signal: caller.signal,
			}),
		);
		caller.abort(reason);
		await expect(cancelledResult).rejects.toBe(reason);

		const timed = recognition({ fetch: pendingFetch, requestTimeoutMs: 20 });
		await expect(collect(timed.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechTimeoutError,
		);
	});

	it("does not claim audio after format cancellation and preserves terminal-done cancellation", async () => {
		const formatController = new AbortController();
		const formatReason = new Error("abort-from-stt-format");
		let formatClaims = 0;
		let formatReturns = 0;
		const fetch = vi.fn(async () => Response.json({ text: "must-not-run" }));
		const hostileFormat = {
			get mediaType() {
				formatController.abort(formatReason);
				return INPUT_FORMAT.mediaType;
			},
			sampleRateHz: INPUT_FORMAT.sampleRateHz,
			channelCount: INPUT_FORMAT.channelCount,
		};
		await expect(
			collect(
				recognition({ fetch }).recognize({
					format: hostileFormat,
					audio: {
						[Symbol.asyncIterator]() {
							formatClaims += 1;
							return {
								next: () => Promise.resolve({ done: true as const, value: undefined }),
								return() {
									formatReturns += 1;
									return Promise.resolve({ done: true as const, value: undefined });
								},
							};
						},
					},
					signal: formatController.signal,
				}),
			),
		).rejects.toBe(formatReason);
		expect({ formatClaims, formatReturns, fetchCalls: fetch.mock.calls.length }).toEqual({
			formatClaims: 0,
			formatReturns: 0,
			fetchCalls: 0,
		});
		const requestController = new AbortController();
		const requestReason = new Error("abort-from-stt-request");
		await expect(
			collect(
				recognition({ fetch }).recognize({
					signal: requestController.signal,
					get format() {
						requestController.abort(requestReason);
						throw new Error("private-format-getter");
					},
					audio: chunks(),
				} as never),
			),
		).rejects.toBe(requestReason);
		expect(fetch).not.toHaveBeenCalled();

		const doneController = new AbortController();
		const doneReason = new Error("abort-from-stt-done");
		let doneReturns = 0;
		const terminalAudio = {
			[Symbol.asyncIterator]() {
				return {
					next: () =>
						Promise.resolve(
							Object.defineProperty({}, "done", {
								get() {
									doneController.abort(doneReason);
									return true;
								},
							}) as IteratorResult<RealtimeAudioChunk>,
						),
					return() {
						doneReturns += 1;
						return Promise.resolve({ done: true as const, value: undefined });
					},
				};
			},
		};
		await expect(
			collect(
				recognition({ fetch }).recognize({
					format: INPUT_FORMAT,
					audio: terminalAudio,
					signal: doneController.signal,
				}),
			),
		).rejects.toBe(doneReason);
		expect({ doneReturns, fetchCalls: fetch.mock.calls.length }).toEqual({ doneReturns: 1, fetchCalls: 0 });
	});

	it("cancels a transcription response body that arrives after caller cancellation", async () => {
		let resolveFetch: ((response: Response) => void) | undefined;
		const fetch = () =>
			new Promise<Response>((resolve) => {
				resolveFetch = resolve;
			});
		const caller = new AbortController();
		const reason = new Error("stop-before-transcript-response");
		const result = collect(
			recognition({ fetch }).recognize({
				...request(chunks(new Uint8Array([1, 0]))),
				signal: caller.signal,
			}),
		);
		await vi.waitFor(() => expect(resolveFetch).toBeTypeOf("function"));
		caller.abort(reason);
		await expect(result).rejects.toBe(reason);
		let cancels = 0;
		resolveFetch?.(
			new Response(
				new ReadableStream<Uint8Array>({
					start(controller) {
						controller.enqueue(new TextEncoder().encode('{"text":"late"'));
					},
					cancel() {
						cancels += 1;
					},
				}),
				{ headers: { "content-type": "application/json" } },
			),
		);
		await vi.waitFor(() => expect(cancels).toBe(1));
	});

	it("preserves cancellation from audio iterator and chunk admission", async () => {
		const fetch = vi.fn(async () => Response.json({ text: "must-not-run" }));
		const iteratorController = new AbortController();
		const iteratorReason = new Error("abort-from-stt-iterator");
		await expect(
			collect(
				recognition({ fetch }).recognize({
					format: INPUT_FORMAT,
					audio: {
						[Symbol.asyncIterator]() {
							iteratorController.abort(iteratorReason);
							throw new Error("private-iterator-create");
						},
					} as never,
					signal: iteratorController.signal,
				}),
			),
		).rejects.toBe(iteratorReason);
		expect(fetch).not.toHaveBeenCalled();

		const chunkController = new AbortController();
		const chunkReason = new Error("abort-from-stt-chunk");
		let returns = 0;
		const source = {
			[Symbol.asyncIterator]() {
				return {
					next: () =>
						Promise.resolve({
							done: false as const,
							value: {
								get bytes() {
									chunkController.abort(chunkReason);
									throw new Error("private-bytes-getter");
								},
							},
						}),
					return() {
						returns += 1;
						return Promise.resolve({ done: true as const, value: undefined });
					},
				};
			},
		};
		await expect(
			collect(
				recognition({ fetch }).recognize({
					format: INPUT_FORMAT,
					audio: source as never,
					signal: chunkController.signal,
				}),
			),
		).rejects.toBe(chunkReason);
		expect({ returns, fetchCalls: fetch.mock.calls.length }).toEqual({ returns: 1, fetchCalls: 0 });
	});

	it("rejects remote plaintext, redirects, malformed response shapes, and repeated configuration reads", async () => {
		expect(() => recognition({ baseUrl: "http://example.com/v1" })).toThrow();
		const redirected = recognition({
			fetch: async () => new Response(null, { status: 302, headers: { location: "/x" } }),
		});
		await expect(collect(redirected.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toThrow();
		const malformed = recognition({ fetch: async () => Response.json({ text: 7 }) });
		await expect(collect(malformed.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechProtocolError,
		);

		let modelReads = 0;
		const options = {
			...COMMON,
			...RECOGNITION_LIMITS,
			inputMediaType: INPUT_FORMAT.mediaType,
			get modelId() {
				modelReads += 1;
				return "local-model";
			},
			fetch: async () => Response.json({ text: "ok" }),
		};
		const snapshotted = new OpenAICompatibleSpeechRecognizer(options);
		await collect(snapshotted.recognize(request(chunks(new Uint8Array([1, 0])))));
		expect(modelReads).toBe(1);
	});

	it("rejects followed redirect evidence before accepting a successful response", async () => {
		const followed = Response.json({ text: "must-not-pass" });
		Object.defineProperties(followed, {
			redirected: { value: true },
			url: { value: "http://127.0.0.1:8080/redirect-target" },
		});
		await expect(
			collect(recognition({ fetch: async () => followed }).recognize(request(chunks(new Uint8Array([1, 0]))))),
		).rejects.toBeInstanceOf(LocalSpeechProviderError);
	});

	it("preserves header-level rejection when response cleanup never settles", async () => {
		const nonCooperativeBody = (onCancel: () => void) =>
			new ReadableStream<Uint8Array>({
				start(controller) {
					controller.enqueue(new Uint8Array([1]));
				},
				cancel() {
					onCancel();
					return new Promise(() => undefined);
				},
			});
		let redirectCancellations = 0;
		const redirected = recognition({
			requestTimeoutMs: 20,
			fetch: async () =>
				new Response(
					nonCooperativeBody(() => {
						redirectCancellations += 1;
					}),
					{ status: 302, headers: { location: "/redirected" } },
				),
		});
		await expect(collect(redirected.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechProviderError,
		);
		expect(redirectCancellations).toBe(1);

		let capacityCancellations = 0;
		const oversized = recognition({
			requestTimeoutMs: 20,
			maxResponseBytes: 4,
			fetch: async () =>
				new Response(
					nonCooperativeBody(() => {
						capacityCancellations += 1;
					}),
					{ headers: { "content-length": "5", "content-type": "application/json" } },
				),
		});
		await expect(collect(oversized.recognize(request(chunks(new Uint8Array([1, 0])))))).rejects.toBeInstanceOf(
			LocalSpeechCapacityError,
		);
		expect(capacityCancellations).toBe(1);
	});

	it("does not invoke the provider when its request signal is already aborted", async () => {
		const caller = new AbortController();
		const reason = new Error("abort-before-provider");
		const fetch = vi.fn(async () => Response.json({ text: "must-not-run" }));
		const audio = {
			[Symbol.asyncIterator]() {
				let read = false;
				return {
					next() {
						if (read) {
							caller.abort(reason);
							return Promise.resolve({ done: true as const, value: undefined });
						}
						read = true;
						return Promise.resolve({ done: false as const, value: { bytes: new Uint8Array([1, 0]) } });
					},
					return: () => Promise.resolve({ done: true as const, value: undefined }),
				};
			},
		};
		await expect(
			collect(
				recognition({ fetch }).recognize({
					...request(audio),
					signal: caller.signal,
				}),
			),
		).rejects.toBe(reason);
		expect(fetch).not.toHaveBeenCalled();
	});

	it("maps hostile recognition request getters to the stable protocol boundary", async () => {
		const recognizer = recognition();
		const hostile = Object.defineProperty({}, "signal", {
			get() {
				throw new Error("private-request-getter");
			},
		});
		const result = collect(recognizer.recognize(hostile as never));
		await expect(result).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		await expect(result).rejects.not.toThrow(/private-request-getter/u);
	});

	it("sanitizes a hostile provider cause object", async () => {
		const cause = new Proxy(
			{},
			{
				getPrototypeOf() {
					throw new Error("private-provider-cause");
				},
			},
		);
		const recognizer = recognition({ fetch: async () => Promise.reject(cause) });
		const result = collect(recognizer.recognize(request(chunks(new Uint8Array([1, 0])))));
		await expect(result).rejects.toBeInstanceOf(LocalSpeechProviderError);
		await expect(result).rejects.not.toThrow(/private-provider-cause/u);
	});
});

describe("OpenAICompatibleSpeechSynthesizer", () => {
	it("consumes complete visible text before one POST and streams the bounded binary body", async () => {
		const trace: string[] = [];
		let request: Request | undefined;
		const synthesizer = synthesis({
			fetch: async (input: Parameters<typeof fetch>[0]) => {
				trace.push("fetch");
				request = input as Request;
				return audioResponse(stream(new Uint8Array([1, 2]), new Uint8Array([3])));
			},
		});
		const output: number[][] = [];
		for await (const chunk of synthesizer.synthesize({
			voiceProfileId: "guard",
			format: OUTPUT_FORMAT,
			text: text(trace, "hello ", "world"),
			signal: new AbortController().signal,
		})) {
			output.push([...chunk.bytes]);
		}

		expect(trace).toEqual(["text:hello ", "text:world", "fetch"]);
		expect(output).toEqual([[1, 2], [3]]);
		expect(new URL(request?.url ?? "").pathname).toBe("/v1/audio/speech");
		expect(await request?.json()).toEqual({
			input: "hello world",
			model: "local-model",
			response_format: "pcm",
			stream_format: "audio",
			voice: "guard",
		});
	});

	it("accepts response media-type case and parameters but rejects configured output parameters", async () => {
		await expect(collect(synthesis().synthesize(synthesisRequest("hello")))).resolves.toEqual([
			{ bytes: new Uint8Array([1]) },
		]);
		expect(() => synthesis({ outputFormat: { ...OUTPUT_FORMAT, mediaType: "audio/pcm; rate=24000" } })).toThrow(
			LocalSpeechConfigurationError,
		);
		expect(() => synthesis({ outputFormat: { mediaType: "audio/pcm", sampleRateHz: 0, channelCount: 1 } })).toThrow(
			LocalSpeechConfigurationError,
		);
		expect(() => synthesis({ outputFormat: { mediaType: "audio/pcm" } })).toThrow(LocalSpeechConfigurationError);
		expect(() => synthesis({ outputFormat: { ...OUTPUT_FORMAT, mediaType: "audio/x" }, maxMediaTypeBytes: 6 })).toThrow(
			LocalSpeechConfigurationError,
		);
	});

	it("does not call a provider for empty text", async () => {
		const fetch = vi.fn();
		const synthesizer = synthesis({ fetch: fetch as typeof fetch });
		await expect(
			collect(
				synthesizer.synthesize({
					voiceProfileId: "guard",
					format: OUTPUT_FORMAT,
					text: text([], "", ""),
					signal: new AbortController().signal,
				}),
			),
		).resolves.toEqual([]);
		expect(fetch).not.toHaveBeenCalled();
	});

	it("disables official SDK retries and sanitizes synthesis provider failures", async () => {
		const fetch = vi.fn(async () => new Response("private-tts-detail", { status: 500 }));
		const synthesizer = synthesis({ fetch });
		const result = collect(synthesizer.synthesize(synthesisRequest("hello")));
		await expect(result).rejects.toBeInstanceOf(LocalSpeechProviderError);
		await expect(result).rejects.not.toThrow(/private-tts-detail/u);
		expect(fetch).toHaveBeenCalledTimes(1);
	});

	it("bounds text segments, text bytes, response chunks, response chunk bytes, and total response bytes", async () => {
		const response = async () => audioResponse(stream(new Uint8Array([1, 2]), new Uint8Array([3, 4])));
		await expect(
			collect(synthesis({ fetch: response, maxTextSegments: 1 }).synthesize(synthesisRequest("a", "b"))),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			collect(synthesis({ fetch: response, maxTextBytes: 1 }).synthesize(synthesisRequest("ab"))),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			collect(synthesis({ fetch: response, maxResponseChunks: 1 }).synthesize(synthesisRequest("a"))),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			collect(synthesis({ fetch: response, maxResponseChunkBytes: 1 }).synthesize(synthesisRequest("a"))),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			collect(synthesis({ fetch: response, maxResponseBytes: 3 }).synthesize(synthesisRequest("a"))),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
	});

	it("keeps the host timeout alive through body streaming and preserves caller abort", async () => {
		const body = () =>
			new ReadableStream<Uint8Array>({
				start(controller) {
					controller.enqueue(new Uint8Array([1]));
				},
			});
		const timed = synthesis({ fetch: async () => audioResponse(body()), requestTimeoutMs: 100 });
		const timedIterator = timed.synthesize(synthesisRequest("a"))[Symbol.asyncIterator]();
		await expect(timedIterator.next()).resolves.toMatchObject({ done: false, value: { bytes: new Uint8Array([1]) } });
		await expect(timedIterator.next()).rejects.toBeInstanceOf(LocalSpeechTimeoutError);

		const caller = new AbortController();
		const reason = new Error("stop-tts");
		let cancels = 0;
		const cancellableBody = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new Uint8Array([1]));
			},
			cancel() {
				cancels += 1;
			},
		});
		const cancelled = synthesis({ fetch: async () => audioResponse(cancellableBody) });
		const iterator = cancelled.synthesize({ ...synthesisRequest("a"), signal: caller.signal })[Symbol.asyncIterator]();
		await iterator.next();
		const pending = iterator.next();
		caller.abort(reason);
		await expect(pending).rejects.toBe(reason);
		await Promise.resolve();
		expect(cancels).toBe(1);
	});

	it("aborts the provider signal on early consumer return but not after natural EOF", async () => {
		let earlySignal: AbortSignal | undefined;
		let earlyCancels = 0;
		const earlyBody = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new Uint8Array([1]));
			},
			cancel() {
				earlyCancels += 1;
				return new Promise(() => undefined);
			},
		});
		const early = synthesis({
			fetch: async (input: Parameters<typeof fetch>[0], init?: Parameters<typeof fetch>[1]) => {
				const candidate = input instanceof Request ? input.signal : init?.signal;
				earlySignal = candidate instanceof AbortSignal ? candidate : undefined;
				return audioResponse(earlyBody);
			},
		})
			.synthesize(synthesisRequest("a"))
			[Symbol.asyncIterator]();
		await expect(early.next()).resolves.toMatchObject({ done: false });
		const earlyReturn = early.return;
		if (!earlyReturn) throw new Error("Expected the synthesis iterator to support return().");
		await expect(Reflect.apply(earlyReturn, early, [])).resolves.toMatchObject({ done: true });
		expect(earlySignal?.aborted).toBe(true);
		expect(earlyCancels).toBe(1);

		let completeSignal: AbortSignal | undefined;
		await expect(
			collect(
				synthesis({
					fetch: async (input: Parameters<typeof fetch>[0], init?: Parameters<typeof fetch>[1]) => {
						const candidate = input instanceof Request ? input.signal : init?.signal;
						completeSignal = candidate instanceof AbortSignal ? candidate : undefined;
						return audioResponse(stream(new Uint8Array([1])));
					},
				}).synthesize(synthesisRequest("a")),
			),
		).resolves.toEqual([{ bytes: new Uint8Array([1]) }]);
		expect(completeSignal?.aborted).toBe(false);
	});

	it("does not claim text after format cancellation and preserves terminal-done cancellation", async () => {
		const formatController = new AbortController();
		const formatReason = new Error("abort-from-tts-format");
		let formatClaims = 0;
		let formatReturns = 0;
		const fetch = vi.fn(async () => audioResponse(stream(new Uint8Array([1]))));
		const hostileFormat = {
			get mediaType() {
				formatController.abort(formatReason);
				return OUTPUT_FORMAT.mediaType;
			},
			sampleRateHz: OUTPUT_FORMAT.sampleRateHz,
			channelCount: OUTPUT_FORMAT.channelCount,
		};
		await expect(
			collect(
				synthesis({ fetch }).synthesize({
					...synthesisRequest("unused"),
					format: hostileFormat,
					text: {
						[Symbol.asyncIterator]() {
							formatClaims += 1;
							return {
								next: () => Promise.resolve({ done: true as const, value: undefined }),
								return() {
									formatReturns += 1;
									return Promise.resolve({ done: true as const, value: undefined });
								},
							};
						},
					},
					signal: formatController.signal,
				}),
			),
		).rejects.toBe(formatReason);
		expect({ formatClaims, formatReturns, fetchCalls: fetch.mock.calls.length }).toEqual({
			formatClaims: 0,
			formatReturns: 0,
			fetchCalls: 0,
		});
		const requestController = new AbortController();
		const requestReason = new Error("abort-from-tts-request");
		await expect(
			collect(
				synthesis({ fetch }).synthesize({
					signal: requestController.signal,
					get voiceProfileId() {
						requestController.abort(requestReason);
						throw new Error("private-voice-getter");
					},
					format: OUTPUT_FORMAT,
					text: text([], "unused"),
				} as never),
			),
		).rejects.toBe(requestReason);
		expect(fetch).not.toHaveBeenCalled();

		const doneController = new AbortController();
		const doneReason = new Error("abort-from-tts-done");
		let doneReturns = 0;
		const terminalText = {
			[Symbol.asyncIterator]() {
				return {
					next: () =>
						Promise.resolve(
							Object.defineProperty({}, "done", {
								get() {
									doneController.abort(doneReason);
									return true;
								},
							}) as IteratorResult<string>,
						),
					return() {
						doneReturns += 1;
						return Promise.resolve({ done: true as const, value: undefined });
					},
				};
			},
		};
		await expect(
			collect(
				synthesis({ fetch }).synthesize({
					...synthesisRequest("unused"),
					text: terminalText,
					signal: doneController.signal,
				}),
			),
		).rejects.toBe(doneReason);
		expect({ doneReturns, fetchCalls: fetch.mock.calls.length }).toEqual({ doneReturns: 1, fetchCalls: 0 });
	});

	it("cancels the upstream response when the deadline expires during consumer backpressure", async () => {
		let cancels = 0;
		const body = new ReadableStream<Uint8Array>(
			{
				start(controller) {
					controller.enqueue(new Uint8Array([1]));
				},
				cancel() {
					cancels += 1;
				},
			},
			{ highWaterMark: 0 },
		);
		const iterator = synthesis({ fetch: async () => audioResponse(body), requestTimeoutMs: 20 })
			.synthesize(synthesisRequest("a"))
			[Symbol.asyncIterator]();
		await expect(iterator.next()).resolves.toMatchObject({ done: false, value: { bytes: new Uint8Array([1]) } });
		await vi.waitFor(() => expect(cancels).toBe(1));
		await expect(iterator.next()).rejects.toBeInstanceOf(LocalSpeechTimeoutError);
	});

	it("releases the recognition deadline before yielding its sole final transcript", async () => {
		vi.useFakeTimers();
		try {
			const iterator = recognition({
				requestTimeoutMs: 1_000,
				fetch: async () => Response.json({ text: "ready" }),
			})
				.recognize(request(chunks(new Uint8Array([1, 0]))))
				[Symbol.asyncIterator]();
			await expect(iterator.next()).resolves.toEqual({
				done: false,
				value: { type: "transcript.final", text: "ready" },
			});
			expect(vi.getTimerCount()).toBe(0);
		} finally {
			vi.useRealTimers();
		}
	});

	it("preserves cancellation from text iterator acquisition", async () => {
		const controller = new AbortController();
		const reason = new Error("abort-from-tts-iterator");
		const fetch = vi.fn(async () => audioResponse(stream(new Uint8Array([1]))));
		await expect(
			collect(
				synthesis({ fetch }).synthesize({
					...synthesisRequest("unused"),
					text: {
						[Symbol.asyncIterator]() {
							controller.abort(reason);
							throw new Error("private-text-iterator");
						},
					} as never,
					signal: controller.signal,
				}),
			),
		).rejects.toBe(reason);
		expect(fetch).not.toHaveBeenCalled();
	});

	it("cancels a response body that arrives after caller cancellation won the request race", async () => {
		let resolveFetch: ((response: Response) => void) | undefined;
		const fetch = () =>
			new Promise<Response>((resolve) => {
				resolveFetch = resolve;
			});
		const caller = new AbortController();
		const reason = new Error("stop-before-response");
		const iterator = synthesis({ fetch })
			.synthesize({ ...synthesisRequest("a"), signal: caller.signal })
			[Symbol.asyncIterator]();
		const pending = iterator.next();
		await vi.waitFor(() => expect(resolveFetch).toBeTypeOf("function"));
		caller.abort(reason);
		await expect(pending).rejects.toBe(reason);
		let cancels = 0;
		resolveFetch?.(
			audioResponse(
				new ReadableStream<Uint8Array>({
					cancel() {
						cancels += 1;
					},
				}),
			),
		);
		await vi.waitFor(() => expect(cancels).toBe(1));
	});

	it("does not let non-cooperative response cleanup block the primary capacity failure", async () => {
		let cancels = 0;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new Uint8Array([1]));
				controller.enqueue(new Uint8Array([2]));
			},
			cancel() {
				cancels += 1;
				return new Promise(() => undefined);
			},
		});
		await expect(
			collect(
				synthesis({ fetch: async () => audioResponse(body), maxResponseChunks: 1 }).synthesize(synthesisRequest("a")),
			),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		expect(cancels).toBe(1);
	});

	it("rejects an output-format mismatch before consuming text or calling the provider", async () => {
		const fetch = vi.fn();
		let touched = false;
		const synthesizer = synthesis({ fetch: fetch as typeof fetch });
		await expect(
			collect(
				synthesizer.synthesize({
					...synthesisRequest("hello"),
					format: { ...OUTPUT_FORMAT, sampleRateHz: 16_000 },
					text: {
						async *[Symbol.asyncIterator]() {
							touched = true;
							yield "hello";
						},
					},
				}),
			),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect({ touched, fetchCalls: fetch.mock.calls.length }).toEqual({ touched: false, fetchCalls: 0 });
	});

	it("rejects a provider media type that does not match the configured game output", async () => {
		let cancels = 0;
		const body = new ReadableStream<Uint8Array>({
			cancel() {
				cancels += 1;
			},
		});
		const synthesizer = synthesis({
			fetch: async () => new Response(body, { headers: { "content-type": "audio/mpeg" } }),
		});
		await expect(collect(synthesizer.synthesize(synthesisRequest("hello")))).rejects.toBeInstanceOf(
			LocalSpeechProtocolError,
		);
		await Promise.resolve();
		expect(cancels).toBe(1);

		const missing = synthesis({ fetch: async () => new Response(stream(new Uint8Array([1]))) });
		await expect(collect(missing.synthesize(synthesisRequest("hello")))).rejects.toBeInstanceOf(
			LocalSpeechProtocolError,
		);
	});

	it("maps hostile synthesis request getters to the stable protocol boundary", async () => {
		const synthesizer = synthesis();
		const hostile = Object.defineProperty({}, "voiceProfileId", {
			get() {
				throw new Error("private-request-getter");
			},
		});
		const result = collect(synthesizer.synthesize(hostile as never));
		await expect(result).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		await expect(result).rejects.not.toThrow(/private-request-getter/u);
	});
});

function recognition(overrides: Record<string, unknown> = {}) {
	return new OpenAICompatibleSpeechRecognizer({
		...COMMON,
		...RECOGNITION_LIMITS,
		inputMediaType: INPUT_FORMAT.mediaType,
		fetch: async () => Response.json({ text: "ok" }),
		...overrides,
	} as never);
}

function synthesis(overrides: Record<string, unknown> = {}) {
	return new OpenAICompatibleSpeechSynthesizer({
		...COMMON,
		...SYNTHESIS_LIMITS,
		outputFormat: OUTPUT_FORMAT,
		responseFormat: "pcm",
		fetch: async () => audioResponse(stream(new Uint8Array([1]))),
		...overrides,
	} as never);
}

function request(audio: AsyncIterable<Readonly<RealtimeAudioChunk>>) {
	return { format: INPUT_FORMAT, audio, signal: new AbortController().signal } as const;
}

function synthesisRequest(...segments: string[]) {
	return {
		voiceProfileId: "guard",
		format: OUTPUT_FORMAT,
		text: text([], ...segments),
		signal: new AbortController().signal,
	} as const;
}

async function* chunks(...values: Uint8Array[]): AsyncIterable<RealtimeAudioChunk> {
	for (const bytes of values) yield { bytes };
}

async function* text(trace: string[], ...segments: string[]): AsyncIterable<string> {
	for (const segment of segments) {
		trace.push(`text:${segment}`);
		yield segment;
	}
}

function stream(...values: Uint8Array[]): ReadableStream<Uint8Array> {
	return new ReadableStream({
		start(controller) {
			for (const value of values) controller.enqueue(value);
			controller.close();
		},
	});
}

function audioResponse(body: ReadableStream<Uint8Array>): Response {
	return new Response(body, { headers: { "content-type": "Audio/PCM; rate=24000" } });
}

async function collect<T>(source: AsyncIterable<T>): Promise<T[]> {
	const values: T[] = [];
	for await (const value of source) values.push(value);
	return values;
}
