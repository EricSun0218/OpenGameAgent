import type {
	RealtimeAudioChunk,
	RealtimeVoiceCoordinate,
	RealtimeVoiceInterruptResult,
	RealtimeVoiceTurnHandle,
	RealtimeVoiceTurnRequest,
} from "@opengameagent/realtime";
import { describe, expect, it, vi } from "vitest";
import {
	GameLocalSpeechPipeline,
	type LocalSpeechAudioFormat,
	LocalSpeechBargeInError,
	LocalSpeechCapacityError,
	LocalSpeechConfigurationError,
	LocalSpeechProtocolError,
	PCM_S16LE_MEDIA_TYPE,
	type PcmS16LeAudioFormat,
	type RealtimeVoiceActivityDetector,
	type RealtimeVoiceActivitySession,
	VoiceActivityUtteranceCapture,
} from "../src/index.js";

const FORMAT = { mediaType: "audio/pcm;format=s16le", sampleRateHz: 16_000, channelCount: 1 } as const;
const OUTPUT_FORMAT = { mediaType: "audio/pcm", sampleRateHz: 24_000, channelCount: 1 } as const;

const TYPE_COMPLETE_PCM: PcmS16LeAudioFormat = FORMAT;
const TYPE_COMPLETE_OUTPUT: LocalSpeechAudioFormat = OUTPUT_FORMAT;
// @ts-expect-error PCM input requires an explicit sample rate and channel count.
const TYPE_INCOMPLETE_PCM: PcmS16LeAudioFormat = { mediaType: PCM_S16LE_MEDIA_TYPE };
// @ts-expect-error Local speech output requires an explicit sample rate and channel count.
const TYPE_INCOMPLETE_OUTPUT: LocalSpeechAudioFormat = { mediaType: "audio/pcm" };
void [TYPE_COMPLETE_PCM, TYPE_COMPLETE_OUTPUT, TYPE_INCOMPLETE_PCM, TYPE_INCOMPLETE_OUTPUT];
const COORDINATE = {
	sessionId: "session",
	actorId: "actor",
	voiceSessionId: "voice",
	epoch: "2",
	utteranceId: "new",
} as const;
const PRIOR_COORDINATE = { ...COORDINATE, epoch: "1", utteranceId: "old" } as const;

const LIMITS = {
	activationThreshold: 0.7,
	deactivationThreshold: 0.3,
	minSpeechFrames: 2,
	endSilenceFrames: 2,
	prefixPaddingFrames: 1,
	maxCaptureFrames: 16,
	maxCaptureBytes: 96,
	maxUtteranceFrames: 12,
	maxFrameBytes: 8,
	maxUtteranceBytes: 64,
	maxMediaTypeBytes: 64,
} as const;

describe("VoiceActivityUtteranceCapture", () => {
	it("retains bounded prefix audio, confirms speech, and closes at the configured silence edge", async () => {
		const probabilities = [0.1, 0.8, 0.9, 0.6, 0.2, 0.1, 0.9];
		const detected: number[] = [];
		const detector = vad((request) => {
			detected.push(sample(request.chunk));
			return probabilities.shift() ?? 0;
		});
		let returned = 0;
		let pulled = 0;
		const audio = trackedAudio(
			[1, 2, 3, 4, 5, 6, 7],
			() => {
				returned += 1;
			},
			() => {
				pulled += 1;
			},
		);
		const onSpeechStart = vi.fn();
		const capture = new VoiceActivityUtteranceCapture({ detector, ...LIMITS });

		const utterance = await capture.capture({
			format: FORMAT,
			audio,
			signal: new AbortController().signal,
			onSpeechStart,
		});

		expect(utterance).toMatchObject({ frameCount: 6, byteLength: 12, format: FORMAT });
		expect(await collectSamples(utterance?.audio)).toEqual([1, 2, 3, 4, 5, 6]);
		expect(detected).toEqual([1, 2, 3, 4, 5, 6]);
		expect(onSpeechStart).toHaveBeenCalledTimes(1);
		expect({ returned, pulled }).toEqual({ returned: 1, pulled: 6 });
	});

	it("keeps one source and detector session open across consecutive utterances", async () => {
		const probabilities = [0.9, 0.1, 0.9, 0.1];
		let detectorCloses = 0;
		let sourceReturns = 0;
		const detector: RealtimeVoiceActivityDetector = {
			open: () => ({
				detect: () => probabilities.shift() ?? 0,
				close: () => {
					detectorCloses += 1;
				},
			}),
		};
		const source = trackedAudio([1, 2, 3, 4], () => {
			sourceReturns += 1;
		});
		const capture = new VoiceActivityUtteranceCapture({
			detector,
			...LIMITS,
			minSpeechFrames: 1,
			endSilenceFrames: 1,
			prefixPaddingFrames: 0,
		});
		const session = await capture.openSession({
			format: FORMAT,
			audio: source,
			signal: new AbortController().signal,
		});

		const first = await session.captureNext();
		expect(await collectSamples(first?.audio)).toEqual([1, 2]);
		expect({ detectorCloses, sourceReturns }).toEqual({ detectorCloses: 0, sourceReturns: 0 });

		const second = await session.captureNext();
		expect(await collectSamples(second?.audio)).toEqual([3, 4]);
		expect({ detectorCloses, sourceReturns }).toEqual({ detectorCloses: 0, sourceReturns: 0 });

		await expect(session.captureNext()).resolves.toBeUndefined();
		expect({ detectorCloses, sourceReturns }).toEqual({ detectorCloses: 1, sourceReturns: 1 });
	});

	it("settles a pending utterance when its owned source session is closed", async () => {
		let detectorCloses = 0;
		let sourceReturns = 0;
		const capture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open: () => ({
					detect: () => 0,
					close: () => {
						detectorCloses += 1;
					},
				}),
			},
		});
		const session = await capture.openSession({
			format: FORMAT,
			audio: {
				[Symbol.asyncIterator]() {
					return {
						next: () => new Promise<IteratorResult<RealtimeAudioChunk>>(() => undefined),
						return: async () => {
							sourceReturns += 1;
							return { done: true as const, value: undefined };
						},
					};
				},
			},
			signal: new AbortController().signal,
		});
		let outcome: IteratorResult<never> | undefined;
		const pending = session.captureNext().then((value) => {
			outcome = { done: true, value: value as never };
		});

		session.close();
		await Promise.resolve();
		await Promise.resolve();

		expect(outcome).toEqual({ done: true, value: undefined });
		expect({ detectorCloses, sourceReturns }).toEqual({ detectorCloses: 1, sourceReturns: 1 });
		await pending;
	});

	it("reports no speech without invoking the onset callback", async () => {
		const onSpeechStart = vi.fn();
		const capture = new VoiceActivityUtteranceCapture({
			detector: vad(() => 0.1),
			...LIMITS,
		});
		await expect(
			capture.capture({
				format: FORMAT,
				audio: audio(1, 2, 3),
				signal: new AbortController().signal,
				onSpeechStart,
			}),
		).resolves.toBeUndefined();
		expect(onSpeechStart).not.toHaveBeenCalled();
	});

	it("fails closed on capture, utterance, frame, byte, and PCM-alignment limits", async () => {
		const speech = vad(() => 1);
		await expect(
			new VoiceActivityUtteranceCapture({ ...LIMITS, detector: speech, maxCaptureFrames: 2 }).capture({
				format: FORMAT,
				audio: audio(1, 2, 3),
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			new VoiceActivityUtteranceCapture({ ...LIMITS, detector: speech, maxUtteranceFrames: 2 }).capture({
				format: FORMAT,
				audio: audio(1, 2, 3),
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			new VoiceActivityUtteranceCapture({ ...LIMITS, detector: speech, maxFrameBytes: 1 }).capture({
				format: FORMAT,
				audio: audio(1),
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			new VoiceActivityUtteranceCapture({ ...LIMITS, detector: speech, maxCaptureBytes: 3 }).capture({
				format: FORMAT,
				audio: audio(1, 2),
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			new VoiceActivityUtteranceCapture({ ...LIMITS, detector: speech, maxUtteranceBytes: 3 }).capture({
				format: FORMAT,
				audio: audio(1, 2),
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		await expect(
			new VoiceActivityUtteranceCapture({ ...LIMITS, detector: speech }).capture({
				format: FORMAT,
				audio: chunks(new Uint8Array([1])),
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
	});

	it("rejects negative-zero or inverted probability policy", () => {
		expect(
			() => new VoiceActivityUtteranceCapture({ ...LIMITS, detector: vad(() => 0), activationThreshold: -0 }),
		).toThrow(LocalSpeechConfigurationError);
		expect(
			() =>
				new VoiceActivityUtteranceCapture({
					...LIMITS,
					detector: vad(() => 0),
					activationThreshold: 0.2,
					deactivationThreshold: 0.3,
				}),
		).toThrow(LocalSpeechConfigurationError);
		expect(() => new VoiceActivityUtteranceCapture({ ...LIMITS, detector: vad(() => 0), minSpeechFrames: 13 })).toThrow(
			LocalSpeechConfigurationError,
		);
	});

	it("rejects a media-type bound that cannot contain the only admitted PCM identity", () => {
		const canonicalBytes = new TextEncoder().encode(PCM_S16LE_MEDIA_TYPE).byteLength;
		expect(
			() =>
				new VoiceActivityUtteranceCapture({
					...LIMITS,
					detector: vad(() => 0),
					maxMediaTypeBytes: canonicalBytes - 1,
				}),
		).toThrow(LocalSpeechConfigurationError);
		expect(
			() =>
				new VoiceActivityUtteranceCapture({
					...LIMITS,
					detector: vad(() => 0),
					maxMediaTypeBytes: canonicalBytes,
				}),
		).not.toThrow();
	});

	it("rejects audio that is not explicitly identified as signed little-endian PCM16", async () => {
		const capture = new VoiceActivityUtteranceCapture({ detector: vad(() => 0), ...LIMITS });
		await expect(
			capture.capture({
				format: { ...FORMAT, mediaType: "audio/pcm" } as never,
				audio: audio(1),
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
	});

	it("isolates and closes one detector session per capture", async () => {
		let opened = 0;
		const closed: number[] = [];
		const detections: number[] = [];
		const detector: RealtimeVoiceActivityDetector = {
			open: () => {
				const sessionId = ++opened;
				return {
					detect: () => {
						detections.push(sessionId);
						return 0;
					},
					close: () => {
						closed.push(sessionId);
					},
				};
			},
		};
		const capture = new VoiceActivityUtteranceCapture({ detector, ...LIMITS });
		await capture.capture({ format: FORMAT, audio: audio(1), signal: new AbortController().signal });
		await capture.capture({ format: FORMAT, audio: audio(2), signal: new AbortController().signal });
		expect({ opened, detections, closed }).toEqual({ opened: 2, detections: [1, 2], closed: [1, 2] });
	});

	it("closes the source when session opening fails and resets an invalid session once", async () => {
		let sourceReturns = 0;
		const openingFailure = new Error("vad-open-failed");
		const failing = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: () => Promise.reject(openingFailure) },
		});
		await expect(
			failing.capture({
				format: FORMAT,
				audio: trackedAudio([1], () => {
					sourceReturns += 1;
				}),
				signal: new AbortController().signal,
			}),
		).rejects.toBe(openingFailure);
		expect(sourceReturns).toBe(1);

		let invalidCloses = 0;
		const invalid = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open: () => ({
					detect: 1 as never,
					close: () => {
						invalidCloses += 1;
					},
				}),
			},
		});
		await expect(
			invalid.capture({ format: FORMAT, audio: audio(1), signal: new AbortController().signal }),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(invalidCloses).toBe(1);
	});

	it("does not let detector cleanup failure replace a completed capture", async () => {
		const capture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open: () => ({
					detect: () => 0,
					close: () => Promise.reject(new Error("vad-close-failed")),
				}),
			},
		});
		await expect(
			capture.capture({ format: FORMAT, audio: audio(1), signal: new AbortController().signal }),
		).resolves.toBeUndefined();
	});

	it("does not let non-cooperative source or detector cleanup block a completed capture", async () => {
		let sourceReturns = 0;
		const source = {
			[Symbol.asyncIterator]() {
				let index = 0;
				return {
					next: () =>
						Promise.resolve(
							index++ === 0 ? { done: false as const, value: frame(1) } : { done: true as const, value: undefined },
						),
					return: () => {
						sourceReturns += 1;
						return new Promise<IteratorResult<RealtimeAudioChunk>>(() => undefined);
					},
				};
			},
		};
		const sourceCapture = new VoiceActivityUtteranceCapture({ detector: vad(() => 0), ...LIMITS });
		await expect(
			sourceCapture.capture({ format: FORMAT, audio: source, signal: new AbortController().signal }),
		).resolves.toBeUndefined();
		expect(sourceReturns).toBe(1);

		let sessionCloses = 0;
		const sessionCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open: () => ({
					detect: () => 0,
					close: () => {
						sessionCloses += 1;
						return new Promise(() => undefined);
					},
				}),
			},
		});
		await expect(
			sessionCapture.capture({ format: FORMAT, audio: audio(1), signal: new AbortController().signal }),
		).resolves.toBeUndefined();
		expect(sessionCloses).toBe(1);
	});

	it("closes a detector session that resolves after caller cancellation won the open race", async () => {
		let resolveOpen: ((session: ReturnType<RealtimeVoiceActivityDetector["open"]>) => void) | undefined;
		let closes = 0;
		const opening = new Promise<ReturnType<RealtimeVoiceActivityDetector["open"]>>((resolve) => {
			resolveOpen = resolve;
		});
		const capture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: () => opening as never },
		});
		const controller = new AbortController();
		const reason = new Error("stop-opening-vad");
		const pending = capture.capture({ format: FORMAT, audio: audio(1), signal: controller.signal });
		controller.abort(reason);
		await expect(pending).rejects.toBe(reason);
		resolveOpen?.({
			detect: () => 0,
			close: () => {
				closes += 1;
			},
		});
		await Promise.resolve();
		await Promise.resolve();
		expect(closes).toBe(1);
	});

	it("preserves the caller's exact abort reason and closes a stalled source once", async () => {
		const reason = new Error("stop-capture");
		const controller = new AbortController();
		let returned = 0;
		const capture = new VoiceActivityUtteranceCapture({
			detector: vad(() => new Promise<number>(() => undefined)),
			...LIMITS,
		});
		const pending = capture.capture({
			format: FORMAT,
			audio: trackedAudio([1], () => {
				returned += 1;
			}),
			signal: controller.signal,
		});
		controller.abort(reason);
		await expect(pending).rejects.toBe(reason);
		expect(returned).toBe(1);
	});

	it("does not open a detector after format or iterator acquisition cancels the request", async () => {
		const formatController = new AbortController();
		const formatReason = new Error("abort-from-format");
		let iteratorClaims = 0;
		const openAfterFormat = vi.fn(() => ({ detect: () => 0, close: () => undefined }));
		const formatCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: openAfterFormat },
		});
		const hostileFormat = {
			get mediaType() {
				formatController.abort(formatReason);
				return FORMAT.mediaType;
			},
			sampleRateHz: FORMAT.sampleRateHz,
			channelCount: FORMAT.channelCount,
		};
		await expect(
			formatCapture.capture({
				format: hostileFormat,
				audio: {
					[Symbol.asyncIterator]() {
						iteratorClaims += 1;
						return audio(1)[Symbol.asyncIterator]();
					},
				},
				signal: formatController.signal,
			}),
		).rejects.toBe(formatReason);
		expect({ iteratorClaims, opens: openAfterFormat.mock.calls.length }).toEqual({ iteratorClaims: 0, opens: 0 });

		const invalidFormatController = new AbortController();
		const invalidFormatReason = new Error("abort-from-invalid-format");
		let invalidIteratorClaims = 0;
		const openAfterInvalidFormat = vi.fn(() => ({ detect: () => 0, close: () => undefined }));
		const invalidFormatCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: openAfterInvalidFormat },
		});
		await expect(
			invalidFormatCapture.capture({
				format: {
					get mediaType() {
						invalidFormatController.abort(invalidFormatReason);
						return "";
					},
					sampleRateHz: FORMAT.sampleRateHz,
					channelCount: FORMAT.channelCount,
				} as never,
				audio: {
					[Symbol.asyncIterator]() {
						invalidIteratorClaims += 1;
						return audio(1)[Symbol.asyncIterator]();
					},
				},
				signal: invalidFormatController.signal,
			}),
		).rejects.toBe(invalidFormatReason);
		expect({ invalidIteratorClaims, opens: openAfterInvalidFormat.mock.calls.length }).toEqual({
			invalidIteratorClaims: 0,
			opens: 0,
		});

		const iteratorController = new AbortController();
		const iteratorReason = new Error("abort-from-iterator");
		let sourceReturns = 0;
		const openAfterIterator = vi.fn(() => ({ detect: () => 0, close: () => undefined }));
		const iteratorCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: openAfterIterator },
		});
		await expect(
			iteratorCapture.capture({
				format: FORMAT,
				audio: {
					[Symbol.asyncIterator]() {
						iteratorController.abort(iteratorReason);
						return trackedAudio([1], () => {
							sourceReturns += 1;
						})[Symbol.asyncIterator]();
					},
				},
				signal: iteratorController.signal,
			}),
		).rejects.toBe(iteratorReason);
		expect({ sourceReturns, opens: openAfterIterator.mock.calls.length }).toEqual({ sourceReturns: 1, opens: 0 });
	});

	it("preserves cancellation triggered by a terminal iterator-result getter", async () => {
		const controller = new AbortController();
		const reason = new Error("abort-from-terminal-done");
		let sourceReturns = 0;
		let sessionCloses = 0;
		const capture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open: () => ({
					detect: () => 0,
					close: () => {
						sessionCloses += 1;
					},
				}),
			},
		});
		const source = {
			[Symbol.asyncIterator]() {
				return {
					next: () =>
						Promise.resolve(
							Object.defineProperty({}, "done", {
								get() {
									controller.abort(reason);
									return true;
								},
							}) as IteratorResult<RealtimeAudioChunk>,
						),
					return() {
						sourceReturns += 1;
						return Promise.resolve({ done: true as const, value: undefined });
					},
				};
			},
		};
		await expect(capture.capture({ format: FORMAT, audio: source, signal: controller.signal })).rejects.toBe(reason);
		expect({ sourceReturns, sessionCloses }).toEqual({ sourceReturns: 1, sessionCloses: 1 });
	});

	it("keeps exact cancellation and cleanup at iterator and chunk admission boundaries", async () => {
		let invalidReturns = 0;
		const invalidIterator = {
			[Symbol.asyncIterator]() {
				return {
					return() {
						invalidReturns += 1;
						return Promise.resolve({ done: true as const, value: undefined });
					},
					get next() {
						throw new Error("private-next-getter");
					},
				};
			},
		};
		const openAfterInvalid = vi.fn(() => ({ detect: () => 0, close: () => undefined }));
		const invalidCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: openAfterInvalid },
		});
		await expect(
			invalidCapture.capture({
				format: FORMAT,
				audio: invalidIterator as never,
				signal: new AbortController().signal,
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect({ invalidReturns, opens: openAfterInvalid.mock.calls.length }).toEqual({ invalidReturns: 1, opens: 0 });

		const iteratorController = new AbortController();
		const iteratorReason = new Error("abort-and-throw-from-iterator");
		const openAfterAbort = vi.fn(() => ({ detect: () => 0, close: () => undefined }));
		const iteratorCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: openAfterAbort },
		});
		await expect(
			iteratorCapture.capture({
				format: FORMAT,
				audio: {
					[Symbol.asyncIterator]() {
						iteratorController.abort(iteratorReason);
						throw new Error("private-iterator-create");
					},
				} as never,
				signal: iteratorController.signal,
			}),
		).rejects.toBe(iteratorReason);
		expect(openAfterAbort).not.toHaveBeenCalled();

		const chunkController = new AbortController();
		const chunkReason = new Error("abort-from-chunk");
		let chunkReturns = 0;
		let sessionCloses = 0;
		const chunkCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open: () => ({
					detect: () => 0,
					close: () => {
						sessionCloses += 1;
					},
				}),
			},
		});
		const chunkSource = {
			[Symbol.asyncIterator]() {
				let done = false;
				return {
					next() {
						if (done) return Promise.resolve({ done: true as const, value: undefined });
						done = true;
						return Promise.resolve({
							done: false as const,
							value: {
								get bytes() {
									chunkController.abort(chunkReason);
									throw new Error("private-bytes-getter");
								},
							},
						});
					},
					return() {
						chunkReturns += 1;
						return Promise.resolve({ done: true as const, value: undefined });
					},
				};
			},
		};
		await expect(
			chunkCapture.capture({ format: FORMAT, audio: chunkSource as never, signal: chunkController.signal }),
		).rejects.toBe(chunkReason);
		expect({ chunkReturns, sessionCloses }).toEqual({ chunkReturns: 1, sessionCloses: 1 });

		const openController = new AbortController();
		const openReason = new Error("abort-and-throw-from-open");
		let openSourceReturns = 0;
		const openCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open() {
					openController.abort(openReason);
					throw new Error("private-open-error");
				},
			},
		});
		await expect(
			openCapture.capture({
				format: FORMAT,
				audio: trackedAudio([1], () => {
					openSourceReturns += 1;
				}),
				signal: openController.signal,
			}),
		).rejects.toBe(openReason);
		expect(openSourceReturns).toBe(1);
	});

	it("owns a detector session when open wins just before cancellation", async () => {
		const controller = new AbortController();
		const reason = new Error("abort-after-open");
		let resolveOpen: ((session: RealtimeVoiceActivitySession) => void) | undefined;
		let sessionCloses = 0;
		let sourceReturns = 0;
		const opening = new Promise<RealtimeVoiceActivitySession>((resolve) => {
			resolveOpen = resolve;
		});
		const capture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: () => opening },
		});
		const result = capture.capture({
			format: FORMAT,
			audio: trackedAudio([1], () => {
				sourceReturns += 1;
			}),
			signal: controller.signal,
		});
		resolveOpen?.({
			detect: () => 0,
			close: () => {
				sessionCloses += 1;
			},
		});
		queueMicrotask(() => controller.abort(reason));
		await expect(result).rejects.toBe(reason);
		expect({ sourceReturns, sessionCloses }).toEqual({ sourceReturns: 1, sessionCloses: 1 });
	});

	it("preserves cancellation from request and session getters", async () => {
		const requestController = new AbortController();
		const requestReason = new Error("abort-from-vad-request");
		const openAfterRequest = vi.fn(() => ({ detect: () => 0, close: () => undefined }));
		const requestCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: { open: openAfterRequest },
		});
		const hostileRequest = {
			signal: requestController.signal,
			get audio() {
				requestController.abort(requestReason);
				throw new Error("private-audio-getter");
			},
			format: FORMAT,
		};
		await expect(requestCapture.capture(hostileRequest as never)).rejects.toBe(requestReason);
		expect(openAfterRequest).not.toHaveBeenCalled();

		const sessionController = new AbortController();
		const sessionReason = new Error("abort-from-vad-session");
		let sourceReturns = 0;
		let sessionCloses = 0;
		const sessionCapture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: {
				open: () =>
					({
						get close() {
							return () => {
								sessionCloses += 1;
							};
						},
						get detect() {
							sessionController.abort(sessionReason);
							throw new Error("private-detect-getter");
						},
					}) as never,
			},
		});
		await expect(
			sessionCapture.capture({
				format: FORMAT,
				audio: trackedAudio([1], () => {
					sourceReturns += 1;
				}),
				signal: sessionController.signal,
			}),
		).rejects.toBe(sessionReason);
		expect({ sourceReturns, sessionCloses }).toEqual({ sourceReturns: 1, sessionCloses: 1 });
	});

	it("preserves cancellation after detector resolution wins but before continuation", async () => {
		const controller = new AbortController();
		const reason = new Error("abort-after-detect");
		let resolveFinal: ((probability: number) => void) | undefined;
		let detections = 0;
		let sourceReturns = 0;
		const finalDetection = new Promise<number>((resolve) => {
			resolveFinal = resolve;
		});
		const capture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: vad(() => {
				detections += 1;
				return [0.8, 0.9, 0.1][detections - 1] ?? finalDetection;
			}),
		});
		const result = capture.capture({
			format: FORMAT,
			audio: trackedAudio([1, 2, 3, 4], () => {
				sourceReturns += 1;
			}),
			signal: controller.signal,
		});
		await vi.waitFor(() => expect(resolveFinal).toBeTypeOf("function"));
		resolveFinal?.(0.1);
		queueMicrotask(() => controller.abort(reason));
		await expect(result).rejects.toBe(reason);
		expect(sourceReturns).toBe(1);
	});

	it("does not expose the retained utterance snapshot to detector mutation", async () => {
		const probabilities = [0.8, 0.9, 0.1, 0.1];
		const capture = new VoiceActivityUtteranceCapture({
			...LIMITS,
			detector: vad((request) => {
				(request.chunk.bytes as Uint8Array).fill(0xff);
				return probabilities.shift() ?? 0;
			}),
		});
		const result = await capture.capture({
			format: FORMAT,
			audio: audio(1, 2, 3, 4),
			signal: new AbortController().signal,
		});
		expect(await collectSamples(result?.audio)).toEqual([1, 2, 3, 4]);
	});

	it("captures detector and iterator hooks once", async () => {
		let openReads = 0;
		let detectReads = 0;
		let closeReads = 0;
		let closeCalls = 0;
		let iteratorReads = 0;
		let nextReads = 0;
		let returnReads = 0;
		const detector = {
			get open() {
				openReads += 1;
				return () => ({
					get detect() {
						detectReads += 1;
						return () => 0;
					},
					get close() {
						closeReads += 1;
						return () => {
							closeCalls += 1;
						};
					},
				});
			},
		};
		const safe = new VoiceActivityUtteranceCapture({ detector, ...LIMITS });
		const source = {
			get [Symbol.asyncIterator]() {
				iteratorReads += 1;
				return () => {
					let done = false;
					return {
						get next() {
							nextReads += 1;
							return async () => {
								if (done) return { done: true as const, value: undefined };
								done = true;
								return { done: false as const, value: frame(1) };
							};
						},
						get return() {
							returnReads += 1;
							return async () => ({ done: true as const, value: undefined });
						},
					};
				};
			},
		};
		await safe.capture({ format: FORMAT, audio: source, signal: new AbortController().signal });
		expect({ openReads, detectReads, closeReads, closeCalls, iteratorReads, nextReads, returnReads }).toEqual({
			openReads: 1,
			detectReads: 1,
			closeReads: 1,
			closeCalls: 1,
			iteratorReads: 1,
			nextReads: 1,
			returnReads: 1,
		});
	});
});

describe("GameLocalSpeechPipeline", () => {
	it("rechecks cancellation before capture and races a non-cooperative capturer", async () => {
		const requestController = new AbortController();
		const requestReason = new Error("abort-from-pipeline-request");
		const captureAfterRequest = vi.fn(async () => undefined);
		const requestPipeline = new GameLocalSpeechPipeline({
			capture: { capture: captureAfterRequest },
			voice: { interrupt: vi.fn(), start: vi.fn() },
		});
		const hostileRequest = {
			signal: requestController.signal,
			get coordinate() {
				requestController.abort(requestReason);
				throw new Error("private-coordinate-getter");
			},
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
		};
		await expect(requestPipeline.startWhenSpeechDetected(hostileRequest as never)).rejects.toBe(requestReason);
		expect(captureAfterRequest).not.toHaveBeenCalled();

		const synchronousController = new AbortController();
		const synchronousReason = new Error("abort-and-throw-from-capturer");
		const synchronousStart = vi.fn();
		const synchronousPipeline = new GameLocalSpeechPipeline({
			capture: {
				capture() {
					synchronousController.abort(synchronousReason);
					throw new Error("private-capturer-error");
				},
			},
			voice: { interrupt: vi.fn(), start: synchronousStart },
		});
		await expect(
			synchronousPipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: synchronousController.signal,
			}),
		).rejects.toBe(synchronousReason);
		expect(synchronousStart).not.toHaveBeenCalled();

		const getterController = new AbortController();
		const getterReason = new Error("abort-from-coordinate");
		const captureAfterGetter = vi.fn(async () => undefined);
		const coordinate = {
			...COORDINATE,
			get epoch() {
				getterController.abort(getterReason);
				return COORDINATE.epoch;
			},
		};
		const getterPipeline = new GameLocalSpeechPipeline({
			capture: { capture: captureAfterGetter },
			voice: { interrupt: vi.fn(), start: vi.fn() },
		});
		await expect(
			getterPipeline.startWhenSpeechDetected({
				coordinate,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: getterController.signal,
			}),
		).rejects.toBe(getterReason);
		expect(captureAfterGetter).not.toHaveBeenCalled();

		const formatController = new AbortController();
		const formatReason = new Error("abort-from-invalid-pipeline-format");
		const captureAfterFormat = vi.fn(async () => undefined);
		const formatPipeline = new GameLocalSpeechPipeline({
			capture: { capture: captureAfterFormat },
			voice: { interrupt: vi.fn(), start: vi.fn() },
		});
		await expect(
			formatPipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: {
					get mediaType() {
						formatController.abort(formatReason);
						return "" as typeof FORMAT.mediaType;
					},
					sampleRateHz: FORMAT.sampleRateHz,
					channelCount: FORMAT.channelCount,
				},
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: formatController.signal,
			}),
		).rejects.toBe(formatReason);
		expect(captureAfterFormat).not.toHaveBeenCalled();

		let resolveCapture: ((value: ReturnType<typeof utterance>) => void) | undefined;
		let sourceReturns = 0;
		const lateSource = trackedAudio([1], () => {
			sourceReturns += 1;
		});
		const pendingCapture = new Promise<ReturnType<typeof utterance>>((resolve) => {
			resolveCapture = resolve;
		});
		const pendingController = new AbortController();
		const pendingReason = new Error("abort-pending-capture");
		const pendingPipeline = new GameLocalSpeechPipeline({
			capture: { capture: () => pendingCapture },
			voice: { interrupt: vi.fn(), start: vi.fn() },
		});
		const result = pendingPipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			signal: pendingController.signal,
		});
		pendingController.abort(pendingReason);
		await expect(result).rejects.toBe(pendingReason);
		resolveCapture?.({ ...utterance(1), audio: lateSource });
		await vi.waitFor(() => expect(sourceReturns).toBe(1));

		let resolveSettledCapture: ((value: ReturnType<typeof utterance>) => void) | undefined;
		let settledReturns = 0;
		const settledSource = trackedAudio([2], () => {
			settledReturns += 1;
		});
		const settledCapture = new Promise<ReturnType<typeof utterance>>((resolve) => {
			resolveSettledCapture = resolve;
		});
		const settledController = new AbortController();
		const settledReason = new Error("abort-before-capture-continuation");
		const settledPipeline = new GameLocalSpeechPipeline({
			capture: { capture: () => settledCapture },
			voice: { interrupt: vi.fn(), start: vi.fn() },
		});
		const settledResult = settledPipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			signal: settledController.signal,
		});
		resolveSettledCapture?.({ ...utterance(2), audio: settledSource });
		queueMicrotask(() => settledController.abort(settledReason));
		await expect(settledResult).rejects.toBe(settledReason);
		await vi.waitFor(() => expect(settledReturns).toBe(1));

		const onsetController = new AbortController();
		const onsetReason = new Error("abort-before-onset");
		const interrupt = vi.fn(async () => ({ accepted: true, coordinate: PRIOR_COORDINATE }) as const);
		const start = vi.fn();
		const onsetPipeline = new GameLocalSpeechPipeline({
			capture: {
				capture(request) {
					request.signal.addEventListener("abort", () => request.onSpeechStart?.(), { once: true });
					return new Promise(() => undefined);
				},
			},
			voice: { interrupt, start },
		});
		const onsetResult = onsetPipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			signal: onsetController.signal,
			interrupt: { coordinate: PRIOR_COORDINATE },
		});
		onsetController.abort(onsetReason);
		await expect(onsetResult).rejects.toBe(onsetReason);
		expect({ interrupts: interrupt.mock.calls.length, starts: start.mock.calls.length }).toEqual({
			interrupts: 0,
			starts: 0,
		});

		const utteranceController = new AbortController();
		const utteranceReason = new Error("abort-from-utterance-audio");
		let utteranceReturns = 0;
		const utteranceSource = trackedAudio([3], () => {
			utteranceReturns += 1;
		});
		const utteranceStart = vi.fn();
		const utterancePipeline = new GameLocalSpeechPipeline({
			capture: {
				capture: async () => ({
					format: { ...FORMAT, sampleRateHz: 8_000 },
					get audio() {
						utteranceController.abort(utteranceReason);
						return utteranceSource;
					},
					frameCount: 1,
					byteLength: 2,
				}),
			},
			voice: { interrupt: vi.fn(), start: utteranceStart },
		});
		await expect(
			utterancePipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: utteranceController.signal,
			}),
		).rejects.toBe(utteranceReason);
		expect({ utteranceReturns, starts: utteranceStart.mock.calls.length }).toEqual({ utteranceReturns: 1, starts: 0 });

		const interruptController = new AbortController();
		const interruptReason = new Error("abort-from-interrupt-result");
		let interruptReturns = 0;
		const interruptSource = trackedAudio([4], () => {
			interruptReturns += 1;
		});
		const interruptStart = vi.fn();
		const interruptPipeline = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return { ...utterance(4), audio: interruptSource };
				},
			},
			voice: {
				interrupt: async () =>
					Object.defineProperty(
						{
							reason: "coordinate-mismatch",
							activeCoordinate: COORDINATE,
						},
						"accepted",
						{
							get() {
								interruptController.abort(interruptReason);
								return false;
							},
						},
					) as never,
				start: interruptStart,
			},
		});
		await expect(
			interruptPipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: interruptController.signal,
				interrupt: { coordinate: PRIOR_COORDINATE },
			}),
		).rejects.toBe(interruptReason);
		expect({ interruptReturns, starts: interruptStart.mock.calls.length }).toEqual({
			interruptReturns: 1,
			starts: 0,
		});
	});

	it("starts barge-in at speech onset without blocking the capturer, then waits before the new turn", async () => {
		let releaseInterrupt: ((value: Readonly<RealtimeVoiceInterruptResult>) => void) | undefined;
		const interruptPending = new Promise<Readonly<RealtimeVoiceInterruptResult>>((resolve) => {
			releaseInterrupt = resolve;
		});
		const start = vi.fn(() => handle(COORDINATE));
		const interrupt = vi.fn(() => interruptPending);
		const pipeline = new GameLocalSpeechPipeline({
			voice: { interrupt, start },
			capture: {
				async capture(request) {
					const onsetResult = request.onSpeechStart?.();
					expect(onsetResult).toBeUndefined();
					return utterance(7);
				},
			},
		});
		const pending = pipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(7),
			interrupt: { coordinate: PRIOR_COORDINATE },
		});
		await Promise.resolve();
		expect(interrupt).toHaveBeenCalledOnce();
		expect(start).not.toHaveBeenCalled();
		releaseInterrupt?.({ accepted: true, coordinate: PRIOR_COORDINATE });
		await expect(pending).resolves.toMatchObject({ status: "started" });
		expect(start).toHaveBeenCalledOnce();
	});

	it("prefers cancellation when an interrupt rejection wins just before continuation", async () => {
		let rejectInterrupt: ((reason: unknown) => void) | undefined;
		const interruptPending = new Promise<Readonly<RealtimeVoiceInterruptResult>>((_resolve, reject) => {
			rejectInterrupt = reject;
		});
		let sourceReturns = 0;
		const source = trackedAudio([1], () => {
			sourceReturns += 1;
		});
		const controller = new AbortController();
		const interruptFailure = new Error("interrupt-failed-first");
		const abortReason = new Error("abort-before-interrupt-continuation");
		const start = vi.fn();
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return { ...utterance(1), audio: source };
				},
			},
			voice: { interrupt: () => interruptPending, start },
		});
		const result = pipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			signal: controller.signal,
			interrupt: { coordinate: PRIOR_COORDINATE },
		});
		rejectInterrupt?.(interruptFailure);
		await Promise.resolve();
		queueMicrotask(() => controller.abort(abortReason));
		await expect(result).rejects.toBe(abortReason);
		expect({ sourceReturns, starts: start.mock.calls.length }).toEqual({ sourceReturns: 1, starts: 0 });
	});

	it("interrupts the exact prior turn at confirmed speech onset before starting the new turn", async () => {
		const trace: string[] = [];
		const interruptResult = { accepted: true, coordinate: PRIOR_COORDINATE } as const;
		const turn = handle(COORDINATE);
		let started: RealtimeVoiceTurnRequest | undefined;
		const voice = {
			async interrupt(expected: Readonly<RealtimeVoiceCoordinate>, reason?: unknown) {
				trace.push(`interrupt:${expected.utteranceId}:${String(reason)}`);
				return interruptResult;
			},
			start(request: Readonly<RealtimeVoiceTurnRequest>) {
				trace.push("start");
				started = request;
				return turn;
			},
		};
		const capture = {
			async capture(request: { readonly onSpeechStart?: () => PromiseLike<void> | void }) {
				trace.push("capture");
				await request.onSpeechStart?.();
				return utterance(9);
			},
		};
		const pipeline = new GameLocalSpeechPipeline({ voice, capture });

		const result = await pipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(9),
			signal: new AbortController().signal,
			interrupt: { coordinate: PRIOR_COORDINATE, reason: "barge-in" },
		});

		expect(result).toEqual({ status: "started", coordinate: COORDINATE, turn, interrupt: interruptResult });
		expect(trace).toEqual(["capture", "interrupt:old:barge-in", "start"]);
		expect(await collectSamples(started?.audio)).toEqual([9]);
	});

	it("makes no runtime or interrupt call when the finite candidate contains no speech", async () => {
		const voice = { interrupt: vi.fn(), start: vi.fn() };
		const pipeline = new GameLocalSpeechPipeline({
			voice,
			capture: { capture: async () => undefined },
		});
		await expect(
			pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: new AbortController().signal,
				interrupt: { coordinate: PRIOR_COORDINATE },
			}),
		).resolves.toEqual({ status: "no-speech" });
		expect(voice.interrupt).not.toHaveBeenCalled();
		expect(voice.start).not.toHaveBeenCalled();
	});

	it("revokes a saved speech-onset callback after capture settles", async () => {
		let lateOnset: (() => PromiseLike<void> | void) | undefined;
		const voice = { interrupt: vi.fn(), start: vi.fn() };
		const noSpeech = new GameLocalSpeechPipeline({
			voice,
			capture: {
				capture: async (request) => {
					lateOnset = request.onSpeechStart;
					return undefined;
				},
			},
		});
		await noSpeech.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			interrupt: { coordinate: PRIOR_COORDINATE },
		});
		lateOnset?.();
		await Promise.resolve();
		expect(voice.interrupt).not.toHaveBeenCalled();

		const failure = new Error("capture-failed");
		const rejected = new GameLocalSpeechPipeline({
			voice,
			capture: {
				capture: async (request) => {
					lateOnset = request.onSpeechStart;
					throw failure;
				},
			},
		});
		await expect(
			rejected.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				interrupt: { coordinate: PRIOR_COORDINATE },
			}),
		).rejects.toBe(failure);
		lateOnset?.();
		await Promise.resolve();
		expect(voice.interrupt).not.toHaveBeenCalled();
	});

	it("continues after an already-ended prior turn but rejects a coordinate mismatch", async () => {
		const turn = handle(COORDINATE);
		const baseRequest = {
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			signal: new AbortController().signal,
			interrupt: { coordinate: PRIOR_COORDINATE },
		} as const;
		const capture = {
			async capture(request: { readonly onSpeechStart?: () => PromiseLike<void> | void }) {
				await request.onSpeechStart?.();
				return utterance(1);
			},
		};
		const ended = new GameLocalSpeechPipeline({
			capture,
			voice: {
				interrupt: async () => ({ accepted: false, reason: "no-active-turn" }) as const,
				start: () => turn,
			},
		});
		await expect(ended.startWhenSpeechDetected(baseRequest)).resolves.toMatchObject({ status: "started", turn });

		const start = vi.fn();
		const mismatch = new GameLocalSpeechPipeline({
			capture,
			voice: {
				interrupt: async () =>
					({ accepted: false, reason: "coordinate-mismatch", activeCoordinate: COORDINATE }) as const,
				start,
			},
		});
		await expect(mismatch.startWhenSpeechDetected(baseRequest)).rejects.toBeInstanceOf(LocalSpeechBargeInError);
		expect(start).not.toHaveBeenCalled();
	});

	it("rejects malformed barge-in requests and results at the local pipeline boundary", async () => {
		const start = vi.fn(() => handle(COORDINATE));
		const capture = { capture: async () => utterance(1) };
		const malformedRequest = new GameLocalSpeechPipeline({
			capture,
			voice: { interrupt: vi.fn(), start },
		});
		await expect(
			malformedRequest.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				interrupt: {} as never,
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);

		const malformedResult = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return utterance(1);
				},
			},
			voice: { interrupt: async () => ({ accepted: true }) as never, start },
		});
		await expect(
			malformedResult.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				interrupt: { coordinate: PRIOR_COORDINATE },
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(start).not.toHaveBeenCalled();
	});

	it("rejects cross-channel and false-success barge-in coordinates without starting a turn", async () => {
		const start = vi.fn(() => handle(COORDINATE));
		const interrupt = vi.fn(async () => ({ accepted: true, coordinate: PRIOR_COORDINATE }) as const);
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return utterance(1);
				},
			},
			voice: { interrupt, start },
		});
		await expect(
			pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				interrupt: { coordinate: { ...PRIOR_COORDINATE, voiceSessionId: "other-channel" } },
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(interrupt).not.toHaveBeenCalled();

		const wrongCoordinate = { ...PRIOR_COORDINATE, epoch: "999" };
		const wrongResult = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return utterance(1);
				},
			},
			voice: { interrupt: async () => ({ accepted: true, coordinate: wrongCoordinate }), start },
		});
		await expect(
			wrongResult.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				interrupt: { coordinate: PRIOR_COORDINATE },
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(start).not.toHaveBeenCalled();
	});

	it("rejects an invalid voice profile before acquiring captured audio", async () => {
		const capture = vi.fn(async () => utterance(1));
		const pipeline = new GameLocalSpeechPipeline({
			capture: { capture },
			voice: { interrupt: vi.fn(), start: vi.fn() },
		});

		await expect(
			pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "invalid\u0000profile",
				audio: audio(1),
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(capture).not.toHaveBeenCalled();
	});

	it("returns a frozen barge-in snapshot and rejects hostile result getters", async () => {
		const mutable = {
			accepted: true as const,
			coordinate: { ...PRIOR_COORDINATE, epoch: String(PRIOR_COORDINATE.epoch) },
		};
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return utterance(1);
				},
			},
			voice: { interrupt: async () => mutable, start: () => handle(COORDINATE) },
		});
		const result = await pipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			interrupt: { coordinate: PRIOR_COORDINATE },
		});
		mutable.coordinate.epoch = "mutated";
		expect(result).toMatchObject({ interrupt: { accepted: true, coordinate: PRIOR_COORDINATE } });
		const returnedInterrupt = result.status === "started" ? result.interrupt : undefined;
		expect(Object.isFrozen(returnedInterrupt?.accepted ? returnedInterrupt.coordinate : undefined)).toBe(true);

		const hostile = Object.defineProperty({}, "accepted", {
			get() {
				throw new Error("private-interrupt-getter");
			},
		});
		const unsafe = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return utterance(1);
				},
			},
			voice: { interrupt: async () => hostile as never, start: vi.fn() },
		});
		await expect(
			unsafe.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				interrupt: { coordinate: PRIOR_COORDINATE },
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
	});

	it("snapshots formats before capture and rejects a capturer that changes the audio format", async () => {
		const mutableInput = { ...FORMAT, sampleRateHz: Number(FORMAT.sampleRateHz) };
		const mutableOutput = { ...OUTPUT_FORMAT, sampleRateHz: Number(OUTPUT_FORMAT.sampleRateHz) };
		let started: RealtimeVoiceTurnRequest | undefined;
		const start = vi.fn((request: Readonly<RealtimeVoiceTurnRequest>) => {
			started = request;
			return handle(COORDINATE);
		});
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				async capture() {
					mutableInput.sampleRateHz = 8_000;
					mutableOutput.sampleRateHz = 8_000;
					return utterance(1);
				},
			},
			voice: { interrupt: vi.fn(), start },
		});
		await expect(
			pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: mutableInput,
				outputFormat: mutableOutput,
				voiceProfileId: "guard",
				audio: audio(1),
			}),
		).resolves.toMatchObject({ status: "started" });
		expect(started).toMatchObject({ inputFormat: FORMAT, outputFormat: OUTPUT_FORMAT });

		const mismatchStart = vi.fn();
		const mismatch = new GameLocalSpeechPipeline({
			capture: {
				capture: async () => ({
					...utterance(1),
					format: { ...FORMAT, sampleRateHz: 8_000 },
				}),
			},
			voice: { interrupt: vi.fn(), start: mismatchStart },
		});
		await expect(
			mismatch.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(mismatchStart).not.toHaveBeenCalled();
	});

	it("claims captured audio before waiting for interruption and preserves the captured iterator", async () => {
		let releaseInterrupt: ((value: Readonly<RealtimeVoiceInterruptResult>) => void) | undefined;
		const interruptPending = new Promise<Readonly<RealtimeVoiceInterruptResult>>((resolve) => {
			releaseInterrupt = resolve;
		});
		let currentSample = 1;
		let iteratorClaims = 0;
		const mutableAudio = {
			[Symbol.asyncIterator]() {
				iteratorClaims += 1;
				const capturedSample = currentSample;
				return audio(capturedSample)[Symbol.asyncIterator]();
			},
		};
		let started: RealtimeVoiceTurnRequest | undefined;
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				async capture(request) {
					request.onSpeechStart?.();
					return { format: FORMAT, audio: mutableAudio, frameCount: 1, byteLength: 2 };
				},
			},
			voice: {
				interrupt: () => interruptPending,
				start: (request) => {
					started = request;
					return handle(COORDINATE);
				},
			},
		});
		const pending = pipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
			interrupt: { coordinate: PRIOR_COORDINATE },
		});
		await vi.waitFor(() => expect(iteratorClaims).toBe(1));
		currentSample = 9;
		releaseInterrupt?.({ accepted: true, coordinate: PRIOR_COORDINATE });
		await expect(pending).resolves.toMatchObject({ status: "started" });
		expect(await collectSamples(started?.audio)).toEqual([1]);
		expect(iteratorClaims).toBe(1);
	});

	it("does not release a late chunk after the owned audio iterator is returned", async () => {
		let resolveNext: ((result: IteratorResult<RealtimeAudioChunk>) => void) | undefined;
		let nextCalls = 0;
		let returnCalls = 0;
		const source = {
			[Symbol.asyncIterator]() {
				return {
					next() {
						nextCalls += 1;
						return new Promise<IteratorResult<RealtimeAudioChunk>>((resolve) => {
							resolveNext = resolve;
						});
					},
					return() {
						returnCalls += 1;
						return Promise.resolve({ done: true as const, value: undefined });
					},
				};
			},
		};
		let startedAudio: AsyncIterable<Readonly<RealtimeAudioChunk>> | undefined;
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				capture: async () => ({ format: FORMAT, audio: source, frameCount: 1, byteLength: 2 }),
			},
			voice: {
				interrupt: vi.fn(),
				start(request) {
					startedAudio = request.audio;
					return handle(COORDINATE);
				},
			},
		});
		await pipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(1),
		});
		const iterator = startedAudio?.[Symbol.asyncIterator]();
		if (!iterator) throw new Error("expected transferred audio iterator");
		const pending = iterator.next();
		let pendingOutcome: IteratorResult<Readonly<RealtimeAudioChunk>> | undefined;
		void pending.then((result) => {
			pendingOutcome = result;
		});
		await vi.waitFor(() => expect(resolveNext).toBeTypeOf("function"));
		await expect(iterator.return?.()).resolves.toMatchObject({ done: true });
		await Promise.resolve();
		await Promise.resolve();
		expect(pendingOutcome).toMatchObject({ done: true });
		resolveNext?.({ done: false, value: frame(7) });
		await expect(pending).resolves.toMatchObject({ done: true });
		await expect(iterator.next()).resolves.toMatchObject({ done: true });
		expect({ nextCalls, returnCalls }).toEqual({ nextCalls: 1, returnCalls: 1 });
	});

	it("does not release a chunk when iterator-result getters reenter return", async () => {
		for (const closingGetter of ["done", "value"] as const) {
			let ownedIterator: AsyncIterator<Readonly<RealtimeAudioChunk>> | undefined;
			let returnCalls = 0;
			const rawResult = Object.defineProperties(
				{},
				{
					done: {
						get() {
							if (closingGetter === "done") void ownedIterator?.return?.();
							return false;
						},
					},
					value: {
						get() {
							if (closingGetter === "value") void ownedIterator?.return?.();
							return frame(8);
						},
					},
				},
			) as IteratorYieldResult<RealtimeAudioChunk>;
			const source = {
				[Symbol.asyncIterator]() {
					return {
						next: () => Promise.resolve(rawResult),
						return() {
							returnCalls += 1;
							return Promise.resolve({ done: true as const, value: undefined });
						},
					};
				},
			};
			let startedAudio: AsyncIterable<Readonly<RealtimeAudioChunk>> | undefined;
			const pipeline = new GameLocalSpeechPipeline({
				capture: {
					capture: async () => ({ format: FORMAT, audio: source, frameCount: 1, byteLength: 2 }),
				},
				voice: {
					interrupt: vi.fn(),
					start(request) {
						startedAudio = request.audio;
						return handle(COORDINATE);
					},
				},
			});
			await pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
			});
			ownedIterator = startedAudio?.[Symbol.asyncIterator]();
			if (!ownedIterator) throw new Error("expected transferred audio iterator");
			await expect(ownedIterator.next()).resolves.toMatchObject({ done: true });
			expect(returnCalls).toBe(1);
		}
	});

	it("preserves exact cancellation when the voice start port aborts and throws", async () => {
		const controller = new AbortController();
		const reason = new Error("abort-from-voice-start");
		let sourceReturns = 0;
		const source = trackedAudio([1], () => {
			sourceReturns += 1;
		});
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				capture: async () => ({ ...utterance(1), audio: source }),
			},
			voice: {
				interrupt: vi.fn(),
				start() {
					controller.abort(reason);
					throw new Error("private-start-error");
				},
			},
		});
		await expect(
			pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: controller.signal,
			}),
		).rejects.toBe(reason);
		expect(sourceReturns).toBe(1);
	});

	it("closes captured audio once on every failure after capture ownership is acquired", async () => {
		const verifyClosed = async (
			configure: (source: AsyncIterable<RealtimeAudioChunk>, controller: AbortController) => GameLocalSpeechPipeline,
			expectedError: unknown,
		) => {
			let returns = 0;
			const source = trackedAudio([1], () => {
				returns += 1;
			});
			const controller = new AbortController();
			const pipeline = configure(source, controller);
			const result = pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
				signal: controller.signal,
				interrupt: { coordinate: PRIOR_COORDINATE },
			});
			await expect(result).rejects.toSatisfy((error: unknown) =>
				typeof expectedError === "function"
					? error instanceof (expectedError as new () => Error)
					: error === expectedError,
			);
			expect(returns).toBe(1);
		};

		for (const metadata of [
			{ format: { ...FORMAT, mediaType: "audio/pcm" }, frameCount: 1, byteLength: 2 },
			{ format: FORMAT, frameCount: 0, byteLength: 2 },
			{ format: FORMAT, frameCount: 1, byteLength: 0 },
		] as const) {
			await verifyClosed(
				(source) =>
					new GameLocalSpeechPipeline({
						capture: {
							async capture(request) {
								request.onSpeechStart?.();
								return { ...metadata, audio: source } as never;
							},
						},
						voice: {
							interrupt: async () => ({ accepted: true, coordinate: PRIOR_COORDINATE }),
							start: vi.fn(),
						},
					}),
				LocalSpeechProtocolError,
			);
		}

		await verifyClosed(
			(source) =>
				new GameLocalSpeechPipeline({
					capture: {
						async capture(request) {
							request.onSpeechStart?.();
							return {
								format: { ...FORMAT, sampleRateHz: 8_000 },
								audio: source,
								frameCount: 1,
								byteLength: 2,
							};
						},
					},
					voice: {
						interrupt: async () => ({ accepted: true, coordinate: PRIOR_COORDINATE }),
						start: vi.fn(),
					},
				}),
			LocalSpeechProtocolError,
		);

		const rejection = new Error("interrupt-failed");
		await verifyClosed(
			(source) =>
				new GameLocalSpeechPipeline({
					capture: {
						async capture(request) {
							request.onSpeechStart?.();
							return { format: FORMAT, audio: source, frameCount: 1, byteLength: 2 };
						},
					},
					voice: { interrupt: async () => Promise.reject(rejection), start: vi.fn() },
				}),
			rejection,
		);

		await verifyClosed(
			(source) =>
				new GameLocalSpeechPipeline({
					capture: {
						async capture(request) {
							request.onSpeechStart?.();
							return { format: FORMAT, audio: source, frameCount: 1, byteLength: 2 };
						},
					},
					voice: {
						interrupt: async () => ({
							accepted: false,
							reason: "coordinate-mismatch",
							activeCoordinate: COORDINATE,
						}),
						start: vi.fn(),
					},
				}),
			LocalSpeechBargeInError,
		);

		const abortReason = new Error("abort-after-capture");
		await verifyClosed(
			(source, controller) =>
				new GameLocalSpeechPipeline({
					capture: {
						async capture(request) {
							request.onSpeechStart?.();
							controller.abort(abortReason);
							return { format: FORMAT, audio: source, frameCount: 1, byteLength: 2 };
						},
					},
					voice: {
						interrupt: async () => ({ accepted: true, coordinate: PRIOR_COORDINATE }),
						start: vi.fn(),
					},
				}),
			abortReason,
		);
	});

	it("rejects a capturer audio value that is not an async iterable before calling the voice port", async () => {
		const start = vi.fn();
		const pipeline = new GameLocalSpeechPipeline({
			capture: {
				capture: async () => ({ ...utterance(1), audio: {} as never }),
			},
			voice: { interrupt: vi.fn(), start },
		});
		await expect(
			pipeline.startWhenSpeechDetected({
				coordinate: COORDINATE,
				inputFormat: FORMAT,
				outputFormat: OUTPUT_FORMAT,
				voiceProfileId: "guard",
				audio: audio(1),
			}),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		expect(start).not.toHaveBeenCalled();
	});
});

function frame(value: number): RealtimeAudioChunk {
	const bytes = new Uint8Array(2);
	new DataView(bytes.buffer).setInt16(0, value, true);
	return { bytes };
}

function sample(chunk: Readonly<RealtimeAudioChunk>): number {
	return new DataView(chunk.bytes.buffer, chunk.bytes.byteOffset, chunk.bytes.byteLength).getInt16(0, true);
}

async function* audio(...values: number[]): AsyncIterable<RealtimeAudioChunk> {
	for (const value of values) yield frame(value);
}

async function* chunks(...values: Uint8Array[]): AsyncIterable<RealtimeAudioChunk> {
	for (const bytes of values) yield { bytes };
}

function trackedAudio(values: readonly number[], onReturn: () => void, onPull: () => void = () => undefined) {
	return {
		[Symbol.asyncIterator]() {
			let index = 0;
			return {
				next() {
					onPull();
					const value = values[index++];
					return Promise.resolve(
						value === undefined
							? { done: true as const, value: undefined }
							: { done: false as const, value: frame(value) },
					);
				},
				return() {
					onReturn();
					return Promise.resolve({ done: true as const, value: undefined });
				},
			};
		},
	};
}

async function collectSamples(source: AsyncIterable<Readonly<RealtimeAudioChunk>> | undefined): Promise<number[]> {
	const values: number[] = [];
	if (!source) return values;
	for await (const chunk of source) values.push(sample(chunk));
	return values;
}

function utterance(value: number) {
	return Object.freeze({ format: FORMAT, audio: audio(value), frameCount: 1, byteLength: 2 });
}

function handle(coordinate: Readonly<RealtimeVoiceCoordinate>): RealtimeVoiceTurnHandle {
	return Object.freeze({
		coordinate,
		events: (async function* () {})(),
		completion: Promise.resolve({ status: "completed" as const, coordinate, agentStatus: "completed" as const }),
	});
}

function vad(
	detect: (request: {
		readonly chunk: Readonly<RealtimeAudioChunk>;
		readonly signal: AbortSignal;
	}) => number | PromiseLike<number>,
): RealtimeVoiceActivityDetector {
	return {
		open: () => ({ detect, close: () => undefined }),
	};
}
