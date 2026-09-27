import { Buffer, constants as bufferConstants } from "node:buffer";
import type {
	MediaFailureKind,
	MediaGenerationProvider,
	MediaGenerationRequest,
	MediaProviderObservation,
	MediaProviderOperationContext,
} from "@opengameagent/media";
import type { RealtimeAudioFormat, RealtimeSpeechSynthesizer } from "@opengameagent/realtime";
import {
	collectPcmS16LeWav,
	LocalSpeechCapacityError,
	LocalSpeechProtocolError,
	PCM_S16LE_MEDIA_TYPE,
} from "@opengameagent/realtime-local";

/** Every value is mandatory host configuration; this Adapter supplies no workload defaults. */
export interface RealtimeSpeechMediaProviderLimits {
	readonly maxIdentifierBytes: number;
	readonly maxTextBytes: number;
	readonly maxOutputChunks: number;
	readonly maxOutputChunkBytes: number;
	readonly maxOutputAudioBytes: number;
}

export interface CreateRealtimeSpeechMediaProviderOptions {
	readonly synthesizer: RealtimeSpeechSynthesizer;
	readonly voiceProfileId: string;
	readonly outputFormat: Readonly<RealtimeAudioFormat>;
	readonly limits: Readonly<RealtimeSpeechMediaProviderLimits>;
}

export class RealtimeSpeechMediaAdapterError extends Error {
	constructor(
		code:
			| "invalid_realtime_speech_media_configuration"
			| "provider_outcome_unknown" = "invalid_realtime_speech_media_configuration",
	) {
		super(code);
		this.name = "RealtimeSpeechMediaAdapterError";
	}
}

interface CapturedOptions {
	readonly synthesize: RealtimeSpeechSynthesizer["synthesize"];
	readonly voiceProfileId: string;
	readonly outputFormat: Readonly<Required<RealtimeAudioFormat>>;
	readonly limits: Readonly<RealtimeSpeechMediaProviderLimits>;
}

export function createRealtimeSpeechMediaProvider(
	options: Readonly<CreateRealtimeSpeechMediaProviderOptions>,
): MediaGenerationProvider {
	const captured = captureOptions(options);
	return Object.freeze({
		async start(
			request: Readonly<MediaGenerationRequest>,
			context: Readonly<MediaProviderOperationContext>,
		): Promise<MediaProviderObservation> {
			const signal = captureSignal(context);
			const input = captureInput(request, captured.limits.maxTextBytes);
			signal.throwIfAborted();
			if (input.failure) return { status: "failed", failure: input.failure };
			let wav: Uint8Array;
			try {
				const audio = captured.synthesize({
					voiceProfileId: captured.voiceProfileId,
					format: captured.outputFormat,
					text: oneTextSegment(input.text),
					signal,
				});
				wav = await collectPcmS16LeWav(
					{ format: captured.outputFormat, audio, signal },
					{
						maxChunks: captured.limits.maxOutputChunks,
						maxChunkBytes: captured.limits.maxOutputChunkBytes,
						maxAudioBytes: captured.limits.maxOutputAudioBytes,
					},
				);
			} catch (error) {
				signal.throwIfAborted();
				const failure = mapTerminalFailure(error);
				if (failure) return { status: "failed", failure };
				throw new RealtimeSpeechMediaAdapterError("provider_outcome_unknown");
			}
			signal.throwIfAborted();
			return {
				status: "succeeded",
				resources: [{ ordinal: 0, kind: "audio", mediaType: "audio/wav", declaredBytes: wav.byteLength, bytes: wav }],
			};
		},
	});
}

function captureOptions(options: Readonly<CreateRealtimeSpeechMediaProviderOptions>): CapturedOptions {
	if (!options || typeof options !== "object") throw new RealtimeSpeechMediaAdapterError();
	let synthesizer: RealtimeSpeechSynthesizer;
	let synthesize: unknown;
	let voiceProfileId: unknown;
	let outputFormat: unknown;
	let limits: Readonly<RealtimeSpeechMediaProviderLimits>;
	try {
		synthesizer = options.synthesizer;
		synthesize = synthesizer.synthesize;
		voiceProfileId = options.voiceProfileId;
		outputFormat = options.outputFormat;
		limits = options.limits;
	} catch {
		throw new RealtimeSpeechMediaAdapterError();
	}
	if (typeof synthesize !== "function" || !limits || typeof limits !== "object") {
		throw new RealtimeSpeechMediaAdapterError();
	}
	const capturedLimits = captureLimits(limits);
	if (
		typeof voiceProfileId !== "string" ||
		voiceProfileId.length === 0 ||
		containsControl(voiceProfileId) ||
		Buffer.byteLength(voiceProfileId, "utf8") > capturedLimits.maxIdentifierBytes
	) {
		throw new RealtimeSpeechMediaAdapterError();
	}
	const capturedFormat = captureFormat(outputFormat);
	return Object.freeze({
		synthesize: (request: Parameters<RealtimeSpeechSynthesizer["synthesize"]>[0]) =>
			Reflect.apply(synthesize, synthesizer, [request]),
		voiceProfileId,
		outputFormat: capturedFormat,
		limits: capturedLimits,
	});
}

function captureLimits(limits: Readonly<RealtimeSpeechMediaProviderLimits>) {
	let maxIdentifierBytes: number;
	let maxTextBytes: number;
	let maxOutputChunks: number;
	let maxOutputChunkBytes: number;
	let maxOutputAudioBytes: number;
	try {
		maxIdentifierBytes = limits.maxIdentifierBytes;
		maxTextBytes = limits.maxTextBytes;
		maxOutputChunks = limits.maxOutputChunks;
		maxOutputChunkBytes = limits.maxOutputChunkBytes;
		maxOutputAudioBytes = limits.maxOutputAudioBytes;
	} catch {
		throw new RealtimeSpeechMediaAdapterError();
	}
	for (const value of [maxIdentifierBytes, maxTextBytes, maxOutputChunks, maxOutputChunkBytes, maxOutputAudioBytes]) {
		if (!Number.isSafeInteger(value) || Object.is(value, -0) || value <= 0) {
			throw new RealtimeSpeechMediaAdapterError();
		}
	}
	if (maxOutputAudioBytes > 0xffff_ffff - 36 || maxOutputAudioBytes > bufferConstants.MAX_LENGTH - 44) {
		throw new RealtimeSpeechMediaAdapterError();
	}
	return Object.freeze({
		maxIdentifierBytes,
		maxTextBytes,
		maxOutputChunks,
		maxOutputChunkBytes,
		maxOutputAudioBytes,
	});
}

function captureFormat(value: unknown): Readonly<Required<RealtimeAudioFormat>> {
	if (!value || typeof value !== "object") throw new RealtimeSpeechMediaAdapterError();
	let mediaType: unknown;
	let sampleRateHz: unknown;
	let channelCount: unknown;
	try {
		const format = value as RealtimeAudioFormat;
		mediaType = format.mediaType;
		sampleRateHz = format.sampleRateHz;
		channelCount = format.channelCount;
	} catch {
		throw new RealtimeSpeechMediaAdapterError();
	}
	if (
		mediaType !== PCM_S16LE_MEDIA_TYPE ||
		!Number.isSafeInteger(sampleRateHz) ||
		(sampleRateHz as number) <= 0 ||
		(sampleRateHz as number) > 0xffff_ffff ||
		!Number.isSafeInteger(channelCount) ||
		(channelCount as number) <= 0 ||
		(channelCount as number) * 2 > 0xffff ||
		(sampleRateHz as number) * (channelCount as number) * 2 > 0xffff_ffff
	) {
		throw new RealtimeSpeechMediaAdapterError();
	}
	return Object.freeze({ mediaType, sampleRateHz: sampleRateHz as number, channelCount: channelCount as number });
}

function captureSignal(context: Readonly<MediaProviderOperationContext>): AbortSignal {
	let signal: AbortSignal;
	try {
		signal = context.signal;
	} catch {
		throw new RealtimeSpeechMediaAdapterError();
	}
	if (!(signal instanceof AbortSignal)) throw new RealtimeSpeechMediaAdapterError();
	signal.throwIfAborted();
	return signal;
}

type CapturedInput =
	| { readonly text: string; readonly failure?: never }
	| { readonly failure: "capacity" | "malformed" };

function captureInput(request: Readonly<MediaGenerationRequest>, maxTextBytes: number): CapturedInput {
	try {
		const kind = request.kind;
		const input = request.input;
		if (kind !== "audio" || !isRecord(input)) return { failure: "malformed" };
		const keys = Object.keys(input);
		if (keys.length !== 1 || keys[0] !== "text") return { failure: "malformed" };
		const text = input.text;
		if (typeof text !== "string" || text.length === 0) return { failure: "malformed" };
		if (Buffer.byteLength(text, "utf8") > maxTextBytes) return { failure: "capacity" };
		return Object.freeze({ text });
	} catch {
		return { failure: "malformed" };
	}
}

function mapTerminalFailure(error: unknown): Extract<MediaFailureKind, "capacity" | "malformed"> | undefined {
	try {
		if (error instanceof LocalSpeechCapacityError) return "capacity";
		if (error instanceof LocalSpeechProtocolError) return "malformed";
	} catch {
		return undefined;
	}
	return undefined;
}

async function* oneTextSegment(text: string): AsyncIterable<string> {
	yield text;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function containsControl(value: string): boolean {
	for (let index = 0; index < value.length; index += 1) {
		const code = value.charCodeAt(index);
		if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) return true;
	}
	return false;
}
