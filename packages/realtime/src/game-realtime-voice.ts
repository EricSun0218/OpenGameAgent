import { Buffer } from "node:buffer";
import {
	assertOpaqueIdentifier,
	type CanonicalEvent,
	captureRunAdmissionAuthority,
	raceOperationWithAbort,
	type RunCoordinate,
} from "@opengameagent/protocol";
import {
	RuntimePassiveHealthDependency,
	type RuntimeDependencyDefinition,
	type RuntimeDependencyInspection,
} from "@opengameagent/health";
import { BoundedAsyncQueue, type GameRuntime } from "@opengameagent/runtime";
import { RealtimeVoiceBusyError, RealtimeVoiceConfigurationError, RealtimeVoiceFailure } from "./errors.js";
import type {
	RealtimeAudioChunk,
	RealtimeAudioFormat,
	RealtimeInputProjector,
	RealtimeSpeechRecognizer,
	RealtimeSpeechSynthesizer,
	RealtimeVoiceCoordinate,
	RealtimeVoiceEvent,
	RealtimeVoiceFailureKind,
	RealtimeVoiceHealthOptions,
	RealtimeVoiceInterruptResult,
	RealtimeVoiceLimits,
	RealtimeVoiceTurnHandle,
	RealtimeVoiceTurnRequest,
	RealtimeVoiceTurnResult,
	SpeechRecognitionEvent,
} from "./types.js";

const TYPED_ARRAY_PROTOTYPE = Object.getPrototypeOf(Uint8Array.prototype) as object;
const TYPED_ARRAY_BUFFER = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "buffer")?.get as (
	this: Uint8Array,
) => ArrayBufferLike;
const TYPED_ARRAY_BYTE_LENGTH = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteLength")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_BYTE_OFFSET = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteOffset")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_SET = Uint8Array.prototype.set;

export interface GameRealtimeVoiceServiceOptions {
	readonly runtime: GameRuntime;
	readonly recognizer: RealtimeSpeechRecognizer;
	readonly synthesizer: RealtimeSpeechSynthesizer;
	readonly projectInput: RealtimeInputProjector;
	readonly limits: Readonly<RealtimeVoiceLimits>;
	readonly health?: Readonly<RealtimeVoiceHealthOptions>;
}

interface CapturedAudioIterator {
	next(): PromiseLike<IteratorResult<Readonly<RealtimeAudioChunk>>> | IteratorResult<Readonly<RealtimeAudioChunk>>;
	close(): PromiseLike<unknown> | unknown;
}

interface ActiveTurn {
	readonly key: string;
	readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
	readonly inputFormat: Readonly<RealtimeAudioFormat>;
	readonly outputFormat: Readonly<RealtimeAudioFormat>;
	readonly voiceProfileId: string;
	readonly audio: CapturedAudioIterator;
	readonly controller: AbortController;
	readonly events: BoundedAsyncQueue<Readonly<RealtimeVoiceEvent>>;
	readonly text: BoundedAsyncQueue<string>;
	readonly assistantItems: Map<string, Map<number, string>>;
	readonly signal?: AbortSignal;
	audioClosed: boolean;
	runtimeCoordinate?: Readonly<RunCoordinate>;
	agentStatus?: "completed" | "aborted" | "failed";
	assistantTextEvents: number;
	assistantTextBytes: number;
	failure?: RealtimeVoiceFailureKind;
	interrupted: boolean;
	synthesis?: Promise<void>;
	runtimeAbort?: Promise<void>;
	synthesisFinished: boolean;
	stopRuntimeListener?: () => void;
	detachSignal?: () => void;
}

interface IntrinsicByteView {
	readonly buffer: ArrayBufferLike;
	readonly byteLength: number;
	readonly byteOffset: number;
}

const LIMIT_NAMES = [
	"maxIdentifierBytes",
	"maxMediaTypeBytes",
	"maxInputChunks",
	"maxInputChunkBytes",
	"maxInputAudioBytes",
	"maxRecognitionEvents",
	"maxRecognitionTextBytes",
	"maxTranscriptBytes",
	"maxAssistantTextEvents",
	"maxAssistantTextBytes",
	"maxPendingTextSegments",
	"maxOutputChunks",
	"maxOutputChunkBytes",
	"maxOutputAudioBytes",
	"maxEventBacklog",
] as const;

export class GameRealtimeVoiceService {
	readonly limits: Readonly<RealtimeVoiceLimits>;
	private readonly startRuntime: GameRuntime["start"];
	private readonly abortRuntimeRun: GameRuntime["abortRun"];
	private readonly subscribeRuntimeImmediate: GameRuntime["subscribeImmediate"];
	private readonly recognize: RealtimeSpeechRecognizer["recognize"];
	private readonly synthesize: RealtimeSpeechSynthesizer["synthesize"];
	private readonly projectInput: RealtimeInputProjector;
	private readonly recognizerHealth?: RuntimePassiveHealthDependency;
	private readonly synthesizerHealth?: RuntimePassiveHealthDependency;
	private readonly healthDefinitions: readonly RuntimeDependencyDefinition[];
	private readonly active = new Map<string, ActiveTurn>();

	constructor(options: Readonly<GameRealtimeVoiceServiceOptions>) {
		if (!options || typeof options !== "object") throw new RealtimeVoiceConfigurationError();
		let runtime: GameRuntime;
		let recognizer: RealtimeSpeechRecognizer;
		let synthesizer: RealtimeSpeechSynthesizer;
		let projectInput: RealtimeInputProjector;
		let limits: Readonly<RealtimeVoiceLimits>;
		let startRuntime: GameRuntime["start"];
		let abortRuntimeRun: GameRuntime["abortRun"];
		let subscribeRuntimeImmediate: GameRuntime["subscribeImmediate"];
		let recognize: RealtimeSpeechRecognizer["recognize"];
		let synthesize: RealtimeSpeechSynthesizer["synthesize"];
		let health: Readonly<RealtimeVoiceHealthOptions> | undefined;
		try {
			runtime = options.runtime;
			recognizer = options.recognizer;
			synthesizer = options.synthesizer;
			projectInput = options.projectInput;
			limits = options.limits;
			startRuntime = runtime?.start;
			abortRuntimeRun = runtime?.abortRun;
			subscribeRuntimeImmediate = runtime?.subscribeImmediate;
			recognize = recognizer?.recognize;
			synthesize = synthesizer?.synthesize;
			health = options.health;
		} catch {
			throw new RealtimeVoiceConfigurationError();
		}
		if (
			!runtime ||
			typeof startRuntime !== "function" ||
			typeof abortRuntimeRun !== "function" ||
			typeof subscribeRuntimeImmediate !== "function"
		) {
			throw new RealtimeVoiceConfigurationError();
		}
		if (!recognizer || typeof recognize !== "function") {
			throw new RealtimeVoiceConfigurationError();
		}
		if (!synthesizer || typeof synthesize !== "function") {
			throw new RealtimeVoiceConfigurationError();
		}
		if (typeof projectInput !== "function") throw new RealtimeVoiceConfigurationError();
		this.startRuntime = (...parameters) => Reflect.apply(startRuntime, runtime, parameters);
		this.abortRuntimeRun = (...parameters) => Reflect.apply(abortRuntimeRun, runtime, parameters);
		this.subscribeRuntimeImmediate = (...parameters) => Reflect.apply(subscribeRuntimeImmediate, runtime, parameters);
		this.recognize = (...parameters) => Reflect.apply(recognize, recognizer, parameters);
		this.synthesize = (...parameters) => Reflect.apply(synthesize, synthesizer, parameters);
		this.projectInput = (...parameters) => Reflect.apply(projectInput, undefined, parameters);
		this.limits = captureLimits(limits);
		try {
			if (health) {
				const { now, recognizer, synthesizer } = health;
				const {
					id: recognizerId,
					requirement: recognizerRequirement,
					maxObservationAgeMs: recognizerMaxObservationAgeMs,
				} = recognizer;
				const {
					id: synthesizerId,
					requirement: synthesizerRequirement,
					maxObservationAgeMs: synthesizerMaxObservationAgeMs,
				} = synthesizer;
				if (recognizerId === synthesizerId) throw new TypeError("duplicate health dependency");
				this.recognizerHealth = new RuntimePassiveHealthDependency({
					id: recognizerId,
					requirement: recognizerRequirement,
					maxObservationAgeMs: recognizerMaxObservationAgeMs,
					kind: "realtime",
					now,
				});
				this.synthesizerHealth = new RuntimePassiveHealthDependency({
					id: synthesizerId,
					requirement: synthesizerRequirement,
					maxObservationAgeMs: synthesizerMaxObservationAgeMs,
					kind: "realtime",
					now,
				});
			}
		} catch {
			throw new RealtimeVoiceConfigurationError();
		}
		this.healthDefinitions = Object.freeze(
			this.recognizerHealth && this.synthesizerHealth
				? [this.recognizerHealth.definition, this.synthesizerHealth.definition]
				: [],
		);
	}

	healthDependencies(): readonly RuntimeDependencyDefinition[] {
		return this.healthDefinitions;
	}

	start(request: Readonly<RealtimeVoiceTurnRequest>): RealtimeVoiceTurnHandle {
		const captured = this.captureRequest(request);
		const existing = this.active.get(captured.key);
		if (existing && (!existing.interrupted || sameVoiceCoordinate(existing.coordinate, captured.coordinate))) {
			closeAudio(captured);
			throw new RealtimeVoiceBusyError();
		}
		this.active.set(captured.key, captured);
		if (captured.signal) {
			const onAbort = () => {
				void this.stop(captured, captured.signal?.reason, true).catch(() => {
					if (!captured.failure) captured.failure = "agent";
				});
			};
			captured.signal.addEventListener("abort", onAbort, { once: true });
			captured.detachSignal = () => captured.signal?.removeEventListener("abort", onAbort);
			if (captured.signal.aborted) onAbort();
		}
		const completion = this.execute(captured);
		return Object.freeze({
			coordinate: captured.coordinate,
			events: queueEvents(captured.events),
			completion,
		});
	}

	async interrupt(
		expected: Readonly<RealtimeVoiceCoordinate>,
		reason?: unknown,
	): Promise<Readonly<RealtimeVoiceInterruptResult>> {
		const coordinate = captureCoordinate(expected, this.limits.maxIdentifierBytes);
		const turn = this.active.get(channelKey(coordinate));
		if (!turn) return Object.freeze({ accepted: false, reason: "no-active-turn" });
		if (!sameVoiceCoordinate(turn.coordinate, coordinate)) {
			return Object.freeze({
				accepted: false,
				reason: "coordinate-mismatch",
				activeCoordinate: turn.coordinate,
			});
		}
		await this.stop(turn, reason, true);
		return Object.freeze({ accepted: true, coordinate: turn.coordinate });
	}

	private captureRequest(request: Readonly<RealtimeVoiceTurnRequest>): ActiveTurn {
		if (!request || typeof request !== "object") throw new RealtimeVoiceConfigurationError();
		const signal = request.signal;
		if (signal !== undefined && !(signal instanceof AbortSignal)) throw new RealtimeVoiceConfigurationError();
		signal?.throwIfAborted();
		const coordinate = captureCoordinate(request.coordinate, this.limits.maxIdentifierBytes);
		const key = channelKey(coordinate);
		const existing = this.active.get(key);
		if (existing && (!existing.interrupted || sameVoiceCoordinate(existing.coordinate, coordinate))) {
			throw new RealtimeVoiceBusyError();
		}
		const inputFormat = captureFormat(request.inputFormat, this.limits.maxMediaTypeBytes);
		const outputFormat = captureFormat(request.outputFormat, this.limits.maxMediaTypeBytes);
		const voiceProfileId = boundedIdentifier(request.voiceProfileId, this.limits.maxIdentifierBytes);
		const audio = captureAudioIterator(request.audio);
		return {
			key,
			coordinate,
			inputFormat,
			outputFormat,
			voiceProfileId,
			audio,
			controller: new AbortController(),
			events: new BoundedAsyncQueue(this.limits.maxEventBacklog),
			text: new BoundedAsyncQueue(this.limits.maxPendingTextSegments),
			assistantItems: new Map(),
			...(signal ? { signal } : {}),
			audioClosed: false,
			assistantTextEvents: 0,
			assistantTextBytes: 0,
			interrupted: false,
			synthesisFinished: false,
		};
	}

	private async execute(turn: ActiveTurn): Promise<Readonly<RealtimeVoiceTurnResult>> {
		try {
			const transcript = await this.recognizeTranscript(turn);
			const projection = await raceOperationWithAbort(
				() => this.projectInput(Object.freeze({ coordinate: turn.coordinate, transcript }), turn.controller.signal),
				turn.controller.signal,
			);
			turn.controller.signal.throwIfAborted();
			if (!projection || typeof projection !== "object") throw new RealtimeVoiceFailure("agent");
			const inputId = boundedIdentifier(projection.inputId, this.limits.maxIdentifierBytes);
			const payload = projection.payload;
			const projectedAuthority = projection.admissionAuthority;
			const admissionAuthority =
				projectedAuthority === undefined ? undefined : captureRunAdmissionAuthority(projectedAuthority);
			turn.stopRuntimeListener = this.subscribeRuntimeImmediate(turn.coordinate, (event) =>
				this.onRuntimeEvent(turn, event),
			);
			turn.controller.signal.throwIfAborted();
			const run = this.startRuntime({
				sessionId: turn.coordinate.sessionId,
				actorId: turn.coordinate.actorId,
				inputId,
				payload,
				...(admissionAuthority === undefined ? {} : { admissionAuthority }),
			});
			turn.runtimeCoordinate = Object.freeze({ ...run.initialCoordinate });
			if (turn.controller.signal.aborted) {
				try {
					await this.stop(turn, turn.controller.signal.reason, turn.interrupted);
				} catch {
					turn.failure ??= "agent";
				}
			}
			const result = await run.completion;
			turn.agentStatus = result.status === "deferred" ? "failed" : result.status;
			if (turn.agentStatus !== "completed") {
				turn.interrupted ||= turn.agentStatus === "aborted";
				turn.controller.abort();
			}
			turn.text.close();
			if (turn.synthesis) await turn.synthesis;
			if (turn.failure) return this.complete(turn, "failed");
			if (turn.interrupted || turn.agentStatus === "aborted") return this.complete(turn, "interrupted");
			if (turn.agentStatus !== "completed") {
				turn.failure = "agent";
				return this.complete(turn, "failed");
			}
			return this.complete(turn, "completed");
		} catch (error) {
			if (turn.interrupted || (turn.controller.signal.aborted && !turn.failure)) {
				return this.complete(turn, "interrupted");
			}
			turn.failure = failureKind(error, turn.failure);
			if (turn.failure === "recognition") {
				this.observeHealth(this.recognizerHealth, { state: "unavailable", reason: "dependency-failure" });
			} else if (turn.failure === "recognition_capacity") {
				this.observeHealth(this.recognizerHealth, { state: "unavailable", reason: "capacity" });
			} else if (turn.failure === "invalid_transcript") {
				this.observeHealth(this.recognizerHealth, { state: "unavailable", reason: "dependency-failure" });
			}
			return this.complete(turn, "failed");
		} finally {
			turn.detachSignal?.();
			turn.stopRuntimeListener?.();
			turn.text.close();
			turn.events.close();
			closeAudio(turn);
			if (this.active.get(turn.key) === turn) this.active.delete(turn.key);
		}
	}

	private async recognizeTranscript(turn: ActiveTurn): Promise<string> {
		let stream: AsyncIterable<Readonly<SpeechRecognitionEvent>>;
		try {
			stream = this.recognize({
				format: turn.inputFormat,
				audio: this.inputAudio(turn),
				signal: turn.controller.signal,
			});
		} catch {
			turn.controller.signal.throwIfAborted();
			throw new RealtimeVoiceFailure("recognition");
		}
		const iterator = captureAsyncIterator(stream, "recognition");
		let events = 0;
		let textBytes = 0;
		try {
			while (true) {
				let result: IteratorResult<Readonly<SpeechRecognitionEvent>>;
				try {
					result = await raceOperationWithAbort(() => iterator.next(), turn.controller.signal);
				} catch (error) {
					turn.controller.signal.throwIfAborted();
					if (error instanceof RealtimeVoiceFailure) throw error;
					throw new RealtimeVoiceFailure("recognition");
				}
				const captured = captureIteratorResult<Readonly<SpeechRecognitionEvent>>(result, "recognition");
				if (captured.done) throw new RealtimeVoiceFailure("recognition");
				events += 1;
				if (events > this.limits.maxRecognitionEvents) {
					throw new RealtimeVoiceFailure("recognition_capacity");
				}
				const event = captureRecognitionEvent(captured.value);
				const bytes = Buffer.byteLength(event.text);
				if (bytes > this.limits.maxTranscriptBytes || textBytes > this.limits.maxRecognitionTextBytes - bytes) {
					throw new RealtimeVoiceFailure("recognition_capacity");
				}
				textBytes += bytes;
				if (event.type === "transcript.final" && event.text.length === 0) {
					throw new RealtimeVoiceFailure("invalid_transcript");
				}
				if (event.type === "transcript.final" && event.text.length > 0) {
					this.observeHealth(this.recognizerHealth, { state: "ready" });
				}
				if (
					!this.emit(turn, {
						type: event.type === "transcript.delta" ? "input.transcript.delta" : "input.transcript.final",
						text: event.text,
					})
				) {
					throw new RealtimeVoiceFailure("event_backpressure");
				}
				if (event.type === "transcript.final") return event.text;
			}
		} finally {
			closeIterator(iterator);
			closeAudio(turn);
		}
	}

	private async *inputAudio(turn: ActiveTurn): AsyncIterable<Readonly<RealtimeAudioChunk>> {
		let chunks = 0;
		let totalBytes = 0;
		while (true) {
			let result: IteratorResult<Readonly<RealtimeAudioChunk>>;
			try {
				result = await raceOperationWithAbort(() => turn.audio.next(), turn.controller.signal);
			} catch (error) {
				turn.controller.signal.throwIfAborted();
				if (error instanceof RealtimeVoiceFailure) throw error;
				throw new RealtimeVoiceFailure("invalid_audio");
			}
			const captured = captureIteratorResult<Readonly<RealtimeAudioChunk>>(result, "invalid_audio");
			if (captured.done) return;
			chunks += 1;
			if (chunks > this.limits.maxInputChunks) throw new RealtimeVoiceFailure("input_capacity");
			const view = inspectChunk(captured.value);
			if (
				view.byteLength > this.limits.maxInputChunkBytes ||
				totalBytes > this.limits.maxInputAudioBytes - view.byteLength
			) {
				throw new RealtimeVoiceFailure("input_capacity");
			}
			totalBytes += view.byteLength;
			yield Object.freeze({ bytes: snapshotBytes(view) });
		}
	}

	private onRuntimeEvent(turn: ActiveTurn, event: Readonly<CanonicalEvent>): void {
		if (event.coordinate.runId !== turn.runtimeCoordinate?.runId || turn.controller.signal.aborted) return;
		turn.runtimeCoordinate = Object.freeze({ ...event.coordinate });
		const data = event.data;
		if (data.type === "run.started") {
			if (!this.emit(turn, { type: "agent.started", coordinate: turn.runtimeCoordinate })) {
				this.fail(turn, "event_backpressure");
			}
			return;
		}
		if (data.type === "item.started" && data.item.itemType === "message" && data.item.role === "assistant") {
			if (turn.assistantItems.size >= this.limits.maxAssistantTextEvents) {
				this.fail(turn, "assistant_capacity");
				return;
			}
			turn.assistantItems.set(data.item.itemId, new Map());
			return;
		}
		if (data.type === "item.delta") {
			const item = turn.assistantItems.get(data.itemId);
			if (!item || data.delta.type !== "text" || data.delta.text.length === 0) return;
			const observed = item.get(data.delta.contentIndex) ?? "";
			if (this.acceptAssistantText(turn, data.itemId, data.delta.contentIndex, data.delta.text)) {
				item.set(data.delta.contentIndex, observed + data.delta.text);
			}
			return;
		}
		if (data.type === "item.completed" && data.item.itemType === "message" && data.item.role === "assistant") {
			const observed = turn.assistantItems.get(data.item.itemId);
			if (!observed) return;
			turn.assistantItems.delete(data.item.itemId);
			const seen = new Set<number>();
			const matched = new Set<number>();
			const suffixes: Array<{ sourceIndex: number; text: string; prefixLength: number }> = [];
			let suffixEvents = 0;
			let suffixBytes = 0;
			for (const part of data.item.content) {
				if (part.type !== "text") continue;
				if (seen.size >= this.limits.maxAssistantTextEvents) {
					this.fail(turn, "assistant_capacity");
					return;
				}
				if (seen.has(part.sourceIndex)) {
					this.fail(turn, "agent");
					return;
				}
				seen.add(part.sourceIndex);
				const prefix = observed.get(part.sourceIndex) ?? "";
				if (observed.has(part.sourceIndex)) matched.add(part.sourceIndex);
				if (!part.text.startsWith(prefix)) {
					this.fail(turn, "agent");
					return;
				}
				if (part.text.length === prefix.length) continue;
				const bytes = Buffer.byteLength(part.text) - Buffer.byteLength(prefix);
				if (
					suffixEvents >= this.limits.maxAssistantTextEvents - turn.assistantTextEvents ||
					bytes > this.limits.maxAssistantTextBytes - turn.assistantTextBytes - suffixBytes
				) {
					this.fail(turn, "assistant_capacity");
					return;
				}
				suffixEvents += 1;
				suffixBytes += bytes;
				suffixes.push({ sourceIndex: part.sourceIndex, text: part.text, prefixLength: prefix.length });
			}
			for (const sourceIndex of observed.keys()) {
				if (matched.has(sourceIndex)) continue;
				this.fail(turn, "agent");
				return;
			}
			for (const suffix of suffixes) {
				if (
					!this.acceptAssistantText(turn, data.item.itemId, suffix.sourceIndex, suffix.text.slice(suffix.prefixLength))
				) {
					return;
				}
			}
		}
	}

	private acceptAssistantText(turn: ActiveTurn, itemId: string, contentIndex: number, text: string): boolean {
		if (turn.synthesisFinished) {
			this.fail(turn, "synthesis");
			return false;
		}
		const currentEvents = turn.assistantTextEvents;
		const currentBytes = turn.assistantTextBytes;
		const bytes = Buffer.byteLength(text);
		if (
			currentEvents >= this.limits.maxAssistantTextEvents ||
			bytes > this.limits.maxAssistantTextBytes - currentBytes
		) {
			this.fail(turn, "assistant_capacity");
			return false;
		}
		turn.assistantTextEvents = currentEvents + 1;
		turn.assistantTextBytes = currentBytes + bytes;
		if (!this.emit(turn, { type: "assistant.text.delta", itemId, contentIndex, text })) {
			this.fail(turn, "event_backpressure");
			return false;
		}
		if (!turn.text.push(text)) {
			this.fail(turn, "text_backpressure");
			return false;
		}
		if (!turn.synthesis) turn.synthesis = this.runSynthesis(turn);
		return true;
	}

	private async runSynthesis(turn: ActiveTurn): Promise<void> {
		try {
			let consumedAllText = false;
			const output = this.synthesize({
				voiceProfileId: turn.voiceProfileId,
				format: turn.outputFormat,
				text: queueText(turn.text, () => {
					consumedAllText = true;
				}),
				signal: turn.controller.signal,
			});
			const iterator = captureAsyncIterator(output, "synthesis");
			let observedChunks = 0;
			let emittedChunks = 0;
			let totalBytes = 0;
			try {
				while (true) {
					const result = await raceOperationWithAbort(() => iterator.next(), turn.controller.signal);
					const captured = captureIteratorResult<Readonly<RealtimeAudioChunk>>(result, "synthesis");
					if (captured.done) break;
					observedChunks += 1;
					if (observedChunks > this.limits.maxOutputChunks) {
						throw new RealtimeVoiceFailure("output_capacity");
					}
					const view = inspectChunk(captured.value);
					if (view.byteLength === 0) continue;
					if (
						view.byteLength > this.limits.maxOutputChunkBytes ||
						totalBytes > this.limits.maxOutputAudioBytes - view.byteLength
					) {
						throw new RealtimeVoiceFailure("output_capacity");
					}
					totalBytes += view.byteLength;
					emittedChunks += 1;
					if (
						!this.emit(turn, {
							type: "assistant.audio.chunk",
							sequence: String(emittedChunks),
							chunk: Object.freeze({ bytes: snapshotBytes(view) }),
						})
					) {
						throw new RealtimeVoiceFailure("event_backpressure");
					}
				}
				if (emittedChunks === 0) throw new RealtimeVoiceFailure("synthesis");
				if (!consumedAllText) throw new RealtimeVoiceFailure("synthesis");
				this.observeHealth(this.synthesizerHealth, { state: "ready" });
				if (!turn.controller.signal.aborted) {
					if (
						!this.emit(turn, {
							type: "assistant.audio.completed",
							chunks: String(emittedChunks),
							bytes: String(totalBytes),
						})
					) {
						throw new RealtimeVoiceFailure("event_backpressure");
					}
				}
			} finally {
				turn.synthesisFinished = true;
				closeIterator(iterator);
			}
		} catch (error) {
			if (turn.controller.signal.aborted) return;
			const failure =
				error instanceof RealtimeVoiceFailure && error.code !== "invalid_audio" ? error.code : "synthesis";
			if (failure === "synthesis") {
				this.observeHealth(this.synthesizerHealth, { state: "unavailable", reason: "dependency-failure" });
			} else if (failure === "output_capacity") {
				this.observeHealth(this.synthesizerHealth, { state: "unavailable", reason: "capacity" });
			}
			this.fail(turn, failure);
		}
	}

	private observeHealth(
		health: RuntimePassiveHealthDependency | undefined,
		inspection: RuntimeDependencyInspection,
	): void {
		try {
			health?.observe(inspection);
		} catch {
			// A host health clock must never change realtime turn authority.
		}
	}

	private emit(turn: ActiveTurn, event: Readonly<RealtimeVoiceEvent>): boolean {
		if (turn.controller.signal.aborted && event.type !== "turn.completed") return false;
		const accepted = turn.events.push(freezeEvent(event));
		if (!accepted && !turn.failure) turn.failure = "event_backpressure";
		return accepted;
	}

	private complete(turn: ActiveTurn, status: RealtimeVoiceTurnResult["status"]): Readonly<RealtimeVoiceTurnResult> {
		const event: RealtimeVoiceEvent = {
			type: "turn.completed",
			status,
			...(turn.agentStatus ? { agentStatus: turn.agentStatus } : {}),
			...(turn.failure ? { failure: turn.failure } : {}),
		};
		const terminalDelivered = this.emit(turn, event);
		const finalStatus = terminalDelivered ? status : "failed";
		if (finalStatus === "completed") {
			return Object.freeze({ status: "completed", coordinate: turn.coordinate, agentStatus: "completed" });
		}
		if (finalStatus === "interrupted") {
			return Object.freeze({
				status: finalStatus,
				coordinate: turn.coordinate,
				...(turn.agentStatus ? { agentStatus: turn.agentStatus } : {}),
			});
		}
		return Object.freeze({
			status: finalStatus,
			coordinate: turn.coordinate,
			failure: turn.failure ?? "agent",
			...(turn.agentStatus ? { agentStatus: turn.agentStatus } : {}),
		});
	}

	private async stop(turn: ActiveTurn, reason: unknown, interrupted: boolean): Promise<void> {
		if (interrupted) turn.interrupted = true;
		if (!turn.controller.signal.aborted) turn.controller.abort(reason);
		turn.text.close();
		const expected = turn.runtimeCoordinate;
		if (!expected) return;
		turn.runtimeAbort ??= this.abortRuntimeRun(expected, turn.controller.signal.reason).then(() => undefined);
		await turn.runtimeAbort;
	}

	private fail(turn: ActiveTurn, failure: RealtimeVoiceFailureKind): void {
		if (!turn.failure) turn.failure = failure;
		void this.stop(turn, new RealtimeVoiceFailure(failure), false).catch(() => undefined);
	}
}

function captureLimits(value: Readonly<RealtimeVoiceLimits>): Readonly<RealtimeVoiceLimits> {
	if (!value || typeof value !== "object") throw new RealtimeVoiceConfigurationError();
	const captured = {} as Record<(typeof LIMIT_NAMES)[number], number>;
	for (const name of LIMIT_NAMES) {
		let current: unknown;
		try {
			current = value[name];
		} catch {
			throw new RealtimeVoiceConfigurationError();
		}
		if (!Number.isSafeInteger(current) || Object.is(current, -0) || (current as number) <= 0) {
			throw new RealtimeVoiceConfigurationError();
		}
		captured[name] = current as number;
	}
	return Object.freeze(captured) as Readonly<RealtimeVoiceLimits>;
}

function captureCoordinate(value: unknown, maxIdentifierBytes: number): Readonly<RealtimeVoiceCoordinate> {
	if (!value || typeof value !== "object") throw new RealtimeVoiceConfigurationError();
	const current = value as RealtimeVoiceCoordinate;
	const coordinate = {
		sessionId: boundedIdentifier(current.sessionId, maxIdentifierBytes),
		actorId: boundedIdentifier(current.actorId, maxIdentifierBytes),
		voiceSessionId: boundedIdentifier(current.voiceSessionId, maxIdentifierBytes),
		epoch: boundedIdentifier(current.epoch, maxIdentifierBytes),
		utteranceId: boundedIdentifier(current.utteranceId, maxIdentifierBytes),
	};
	if (!/^[1-9][0-9]*$/u.test(coordinate.epoch)) throw new RealtimeVoiceConfigurationError();
	return Object.freeze(coordinate);
}

function captureFormat(value: unknown, maxMediaTypeBytes: number): Readonly<RealtimeAudioFormat> {
	if (!value || typeof value !== "object") throw new RealtimeVoiceConfigurationError();
	const current = value as RealtimeAudioFormat;
	const mediaType = current.mediaType;
	if (typeof mediaType !== "string" || mediaType.length === 0 || Buffer.byteLength(mediaType) > maxMediaTypeBytes) {
		throw new RealtimeVoiceConfigurationError();
	}
	const sampleRateHz = optionalPositiveInteger(current.sampleRateHz);
	const channelCount = optionalPositiveInteger(current.channelCount);
	return Object.freeze({
		mediaType,
		...(sampleRateHz === undefined ? {} : { sampleRateHz }),
		...(channelCount === undefined ? {} : { channelCount }),
	});
}

function optionalPositiveInteger(value: unknown): number | undefined {
	if (value === undefined) return undefined;
	if (!Number.isSafeInteger(value) || Object.is(value, -0) || (value as number) <= 0) {
		throw new RealtimeVoiceConfigurationError();
	}
	return value as number;
}

function boundedIdentifier(value: unknown, maxBytes: number): string {
	if (typeof value !== "string" || Buffer.byteLength(value) > maxBytes) {
		throw new RealtimeVoiceConfigurationError();
	}
	try {
		assertOpaqueIdentifier(value);
	} catch {
		throw new RealtimeVoiceConfigurationError();
	}
	return value;
}

function captureAudioIterator(value: unknown): CapturedAudioIterator {
	if (!value || (typeof value !== "object" && typeof value !== "function")) {
		throw new RealtimeVoiceConfigurationError();
	}
	let iterator: AsyncIterator<Readonly<RealtimeAudioChunk>> | undefined;
	let closeMethod: unknown;
	try {
		const create = (value as AsyncIterable<Readonly<RealtimeAudioChunk>>)[Symbol.asyncIterator];
		if (typeof create !== "function") throw new RealtimeVoiceConfigurationError();
		iterator = Reflect.apply(create, value, []);
		if (!iterator || typeof iterator !== "object") throw new RealtimeVoiceConfigurationError();
		closeMethod = iterator.return;
		const nextMethod = iterator.next;
		if (typeof nextMethod !== "function" || (closeMethod !== undefined && typeof closeMethod !== "function")) {
			throw new RealtimeVoiceConfigurationError();
		}
		return Object.freeze({
			next: () => Reflect.apply(nextMethod, iterator as AsyncIterator<Readonly<RealtimeAudioChunk>>, []),
			close: () =>
				typeof closeMethod === "function"
					? Reflect.apply(closeMethod, iterator as AsyncIterator<Readonly<RealtimeAudioChunk>>, [])
					: undefined,
		});
	} catch (error) {
		if (iterator) closeCapturedIterator(iterator, closeMethod);
		if (error instanceof RealtimeVoiceConfigurationError) throw error;
		throw new RealtimeVoiceConfigurationError();
	}
}

function captureAsyncIterator<T>(value: AsyncIterable<T>, failure: "recognition" | "synthesis"): CapturedIterator<T> {
	let iterator: AsyncIterator<T> | undefined;
	let closeMethod: unknown;
	try {
		if (!value || (typeof value !== "object" && typeof value !== "function")) throw new Error();
		const create = value[Symbol.asyncIterator];
		if (typeof create !== "function") throw new Error();
		iterator = Reflect.apply(create, value, []);
		if (!iterator || typeof iterator !== "object") throw new Error();
		closeMethod = iterator.return;
		const nextMethod = iterator.next;
		if (typeof nextMethod !== "function" || (closeMethod !== undefined && typeof closeMethod !== "function")) {
			throw new Error();
		}
		return Object.freeze({
			next: () => Reflect.apply(nextMethod, iterator as AsyncIterator<T>, []),
			close: () =>
				typeof closeMethod === "function" ? Reflect.apply(closeMethod, iterator as AsyncIterator<T>, []) : undefined,
		});
	} catch {
		if (iterator) closeCapturedIterator(iterator, closeMethod);
		throw new RealtimeVoiceFailure(failure);
	}
}

interface CapturedIterator<T> {
	next(): PromiseLike<IteratorResult<T>> | IteratorResult<T>;
	close(): PromiseLike<unknown> | unknown;
}

type CapturedIteratorResult<T> = { readonly done: true } | { readonly done: false; readonly value: T };

function captureIteratorResult<T>(result: unknown, failure: RealtimeVoiceFailureKind): CapturedIteratorResult<T> {
	if (!result || typeof result !== "object") throw new RealtimeVoiceFailure(failure);
	let done: unknown;
	let value: unknown;
	try {
		done = (result as IteratorResult<T>).done;
		if (!done) value = (result as IteratorYieldResult<T>).value;
	} catch {
		throw new RealtimeVoiceFailure(failure);
	}
	if (done) return Object.freeze({ done: true });
	return Object.freeze({ done: false, value: value as T });
}

function captureRecognitionEvent(value: unknown): Readonly<SpeechRecognitionEvent> {
	if (!value || typeof value !== "object") throw new RealtimeVoiceFailure("recognition");
	let type: unknown;
	let text: unknown;
	try {
		const current = value as SpeechRecognitionEvent;
		type = current.type;
		text = current.text;
	} catch {
		throw new RealtimeVoiceFailure("recognition");
	}
	if ((type !== "transcript.delta" && type !== "transcript.final") || typeof text !== "string") {
		throw new RealtimeVoiceFailure("recognition");
	}
	return Object.freeze({ type, text });
}

function inspectChunk(value: unknown): IntrinsicByteView {
	if (!value || typeof value !== "object") throw new RealtimeVoiceFailure("invalid_audio");
	let bytes: unknown;
	try {
		bytes = (value as RealtimeAudioChunk).bytes;
	} catch {
		throw new RealtimeVoiceFailure("invalid_audio");
	}
	try {
		return {
			buffer: TYPED_ARRAY_BUFFER.call(bytes as Uint8Array),
			byteLength: TYPED_ARRAY_BYTE_LENGTH.call(bytes as Uint8Array),
			byteOffset: TYPED_ARRAY_BYTE_OFFSET.call(bytes as Uint8Array),
		};
	} catch {
		throw new RealtimeVoiceFailure("invalid_audio");
	}
}

function snapshotBytes(view: Readonly<IntrinsicByteView>): Uint8Array {
	const snapshot = new Uint8Array(view.byteLength);
	TYPED_ARRAY_SET.call(snapshot, new Uint8Array(view.buffer, view.byteOffset, view.byteLength));
	return snapshot;
}

function closeIterator(iterator: { close(): PromiseLike<unknown> | unknown }): void {
	try {
		void Promise.resolve(iterator.close()).catch(() => undefined);
	} catch {
		// Best effort; cleanup cannot replace the primary outcome.
	}
}

function closeCapturedIterator(iterator: object, closeMethod: unknown): void {
	if (typeof closeMethod !== "function") return;
	try {
		void Promise.resolve(Reflect.apply(closeMethod, iterator, [])).catch(() => undefined);
	} catch {
		// Best effort while rejecting a malformed iterator.
	}
}

function closeAudio(turn: ActiveTurn): void {
	if (turn.audioClosed) return;
	turn.audioClosed = true;
	closeIterator(turn.audio);
}

async function* queueEvents(
	queue: BoundedAsyncQueue<Readonly<RealtimeVoiceEvent>>,
): AsyncIterable<Readonly<RealtimeVoiceEvent>> {
	while (true) {
		const value = await queue.next();
		if (value === undefined) return;
		yield value;
	}
}

async function* queueText(queue: BoundedAsyncQueue<string>, complete: () => void): AsyncIterable<string> {
	while (true) {
		const value = await queue.next();
		if (value === undefined) {
			complete();
			return;
		}
		yield value;
	}
}

function freezeEvent(event: Readonly<RealtimeVoiceEvent>): Readonly<RealtimeVoiceEvent> {
	if (event.type === "assistant.audio.chunk") {
		return Object.freeze({ ...event, chunk: Object.freeze({ bytes: event.chunk.bytes }) });
	}
	if (event.type === "agent.started") {
		return Object.freeze({ ...event, coordinate: Object.freeze({ ...event.coordinate }) });
	}
	return Object.freeze({ ...event });
}

function channelKey(coordinate: Readonly<RealtimeVoiceCoordinate>): string {
	return JSON.stringify([coordinate.sessionId, coordinate.actorId, coordinate.voiceSessionId]);
}

function sameVoiceCoordinate(
	left: Readonly<RealtimeVoiceCoordinate>,
	right: Readonly<RealtimeVoiceCoordinate>,
): boolean {
	return (
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId &&
		left.voiceSessionId === right.voiceSessionId &&
		left.epoch === right.epoch &&
		left.utteranceId === right.utteranceId
	);
}

function failureKind(error: unknown, existing?: RealtimeVoiceFailureKind): RealtimeVoiceFailureKind {
	if (existing) return existing;
	return error instanceof RealtimeVoiceFailure ? error.code : "agent";
}
