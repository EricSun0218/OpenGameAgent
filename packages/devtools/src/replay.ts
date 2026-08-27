import type { GameTraceRecord, GameTraceRecording } from "./trace.js";

export interface GameTraceReplayConsumer {
	onRecord(record: GameTraceRecord, signal: AbortSignal): Promise<void> | void;
}

export interface GameTraceReplayOptions {
	afterSequence?: number;
	maximumRecords?: number;
	signal?: AbortSignal;
}

/**
 * Replays observations only. It never invokes a model, a tool, or a game action.
 */
export async function replayGameTrace(
	recording: GameTraceRecording,
	consumer: GameTraceReplayConsumer,
	options: GameTraceReplayOptions = {},
): Promise<number> {
	if (recording.schemaVersion !== 1) throw new Error("Unsupported trace recording version.");
	const afterSequence = boundedInteger(options.afterSequence ?? 0, "afterSequence", 0, Number.MAX_SAFE_INTEGER);
	const maximumRecords = boundedInteger(options.maximumRecords ?? 100_000, "maximumRecords", 1, 1_000_000);
	const signal = options.signal ?? new AbortController().signal;
	let delivered = 0;
	for (const record of [...recording.records].sort((left, right) => left.sequence - right.sequence)) {
		if (record.sequence <= afterSequence) continue;
		if (delivered >= maximumRecords) break;
		signal.throwIfAborted();
		await consumer.onRecord(structuredClone(record), signal);
		delivered += 1;
	}
	return delivered;
}

function boundedInteger(value: number, name: string, minimum: number, maximum: number): number {
	if (!Number.isInteger(value) || value < minimum || value > maximum) {
		throw new RangeError(`${name} must be an integer from ${minimum} through ${maximum}.`);
	}
	return value;
}
