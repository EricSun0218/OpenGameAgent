/** A lazy original-byte source whose exact length is known before it is opened. */
export interface ImmutableResourceByteReader {
	readonly byteLength: number;
	read(signal: AbortSignal): Uint8Array | Promise<Uint8Array>;
}

export type ImmutableResourceBytes = Uint8Array | AsyncIterable<Uint8Array> | ImmutableResourceByteReader;

export interface ImmutableResourceInput {
	readonly bytes: ImmutableResourceBytes;
	readonly declaredBytes?: number;
}

/** Content identity contains no path, MIME type, role, owner, or generation-slot metadata. */
export interface ImmutableResourceRef {
	readonly sha256: string;
	readonly byteLength: number;
}

export interface ImmutableResourceOperationContext {
	readonly maxBytes: number;
	readonly signal: AbortSignal;
}

export interface ImmutableResourcePublisher {
	publish(
		input: Readonly<ImmutableResourceInput>,
		context: Readonly<ImmutableResourceOperationContext>,
	): Promise<ImmutableResourceRef>;
}

export interface ImmutableResourceReader {
	read(
		reference: Readonly<ImmutableResourceRef>,
		context: Readonly<ImmutableResourceOperationContext>,
	): Promise<Uint8Array>;
}

export interface ImmutableResourceStore extends ImmutableResourcePublisher, ImmutableResourceReader {}

/** Host must freeze every publication/reference writer while taking this complete retention snapshot and collecting. */
export interface ImmutableResourceGarbageCollectionRequest {
	readonly beforeModifiedAt: string;
	/** Maximum total object and temporary-file deletions. */
	readonly limit: number;
	/** Maximum directory entries inspected; exceeding it fails before any deletion. */
	readonly maxScanEntries: number;
	/** All live content digests, including persistent archives and references outside this Store. */
	readonly retainSha256: readonly string[];
	readonly retainBudget: RetentionProtectionBudget;
	readonly signal: AbortSignal;
}

export interface ImmutableResourceGarbageCollectionResult {
	readonly scannedEntries: number;
	readonly deletedObjects: number;
	readonly deletedTemporaryFiles: number;
	readonly deletedBytes: string;
}
import type { RetentionProtectionBudget } from "@opengameagent/protocol";
