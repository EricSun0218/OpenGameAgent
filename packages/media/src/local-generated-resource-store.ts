import { Buffer } from "node:buffer";
import {
	ImmutableResourceCapacityError,
	type ImmutableResourceGarbageCollectionRequest,
	type ImmutableResourceGarbageCollectionResult,
	ImmutableResourceStoreError,
	LocalImmutableResourceStore,
} from "@opengameagent/resources";
import { MediaJobCapacityError, MediaResourceStoreError } from "./errors.js";
import {
	type GeneratedResourceRef,
	MEDIA_KINDS,
	type MediaProviderResource,
	type MediaResourcePublishContext,
	type MediaResourcePublisher,
	type MediaResourceReadContext,
	type MediaResourceReader,
} from "./types.js";

const SHA256 = /^[0-9a-f]{64}$/u;

export interface LocalGeneratedResourceStoreOptions {
	readonly rootDirectory: string;
}

export interface LocalGeneratedResourceGarbageCollectionRequest
	extends Omit<ImmutableResourceGarbageCollectionRequest, "retainSha256"> {
	/** Complete live resource IDs across all jobs, archived bundles, and external consumers. IDs equal SHA-256. */
	readonly retainResourceIds: readonly string[];
}

/** Media-job metadata Adapter over the single neutral immutable-resource Store. */
export class LocalGeneratedResourceStore implements MediaResourcePublisher, MediaResourceReader {
	private constructor(private readonly resources: LocalImmutableResourceStore) {}

	static async open(options: Readonly<LocalGeneratedResourceStoreOptions>): Promise<LocalGeneratedResourceStore> {
		try {
			return new LocalGeneratedResourceStore(await LocalImmutableResourceStore.open(options));
		} catch (error) {
			throw mapResourceError(error);
		}
	}

	async publish(
		resource: Readonly<MediaProviderResource>,
		context: Readonly<MediaResourcePublishContext>,
	): Promise<GeneratedResourceRef> {
		const signal = operationSignal(context?.signal);
		signal.throwIfAborted();
		const maxBytes = operationByteLimit(context?.maxBytes);
		const maxMediaTypeBytes = operationPositiveLimit(context?.maxMediaTypeBytes, "maxMediaTypeBytes");
		const captured = capturePublication(resource, maxMediaTypeBytes);
		try {
			const stored = await this.resources.publish(
				{
					bytes: captured.bytes,
					...(captured.declaredBytes === undefined ? {} : { declaredBytes: captured.declaredBytes }),
				},
				{ maxBytes, signal },
			);
			signal.throwIfAborted();
			return Object.freeze({
				resourceId: stored.sha256,
				kind: captured.kind,
				mediaType: captured.mediaType,
				byteLength: stored.byteLength,
				sha256: stored.sha256,
				ordinal: captured.ordinal,
			});
		} catch (error) {
			signal.throwIfAborted();
			throw mapResourceError(error);
		}
	}

	async read(
		reference: Readonly<GeneratedResourceRef>,
		context: Readonly<MediaResourceReadContext>,
	): Promise<Uint8Array> {
		const signal = operationSignal(context?.signal);
		signal.throwIfAborted();
		const maxBytes = operationByteLimit(context?.maxBytes);
		const maxMediaTypeBytes = operationPositiveLimit(context?.maxMediaTypeBytes, "maxMediaTypeBytes");
		const captured = captureReference(reference, maxMediaTypeBytes);
		try {
			return await this.resources.read(
				{ sha256: captured.sha256, byteLength: captured.byteLength },
				{ maxBytes, signal },
			);
		} catch (error) {
			signal.throwIfAborted();
			throw mapResourceError(error);
		}
	}

	async collectGarbage(
		request: Readonly<LocalGeneratedResourceGarbageCollectionRequest>,
	): Promise<ImmutableResourceGarbageCollectionResult> {
		try {
			return await this.resources.collectGarbage({
				beforeModifiedAt: request.beforeModifiedAt,
				limit: request.limit,
				maxScanEntries: request.maxScanEntries,
				retainSha256: request.retainResourceIds,
				retainBudget: request.retainBudget,
				signal: request.signal,
			});
		} catch (error) {
			throw mapResourceError(error);
		}
	}
}

function capturePublication(
	resource: Readonly<MediaProviderResource>,
	maxMediaTypeBytes: number,
): MediaProviderResource {
	try {
		if (!resource || typeof resource !== "object") throw new TypeError();
		const ordinal = resource.ordinal;
		const kind = resource.kind;
		const mediaType = resource.mediaType;
		const bytes = resource.bytes;
		const declaredBytes = resource.declaredBytes;
		if (
			!Number.isSafeInteger(ordinal) ||
			ordinal < 0 ||
			!MEDIA_KINDS.includes(kind) ||
			typeof mediaType !== "string" ||
			mediaType.length === 0 ||
			(declaredBytes !== undefined && (!Number.isSafeInteger(declaredBytes) || declaredBytes < 0))
		) {
			throw new TypeError();
		}
		if (Buffer.byteLength(mediaType) > maxMediaTypeBytes) throw new MediaJobCapacityError("resource");
		return Object.freeze({
			ordinal,
			kind,
			mediaType,
			bytes,
			...(declaredBytes === undefined ? {} : { declaredBytes }),
		});
	} catch (error) {
		if (error instanceof MediaJobCapacityError) throw error;
		throw new MediaResourceStoreError("invalid_resource");
	}
}

function captureReference(reference: Readonly<GeneratedResourceRef>, maxMediaTypeBytes: number): GeneratedResourceRef {
	try {
		if (!reference || typeof reference !== "object") throw new TypeError();
		const resourceId = reference.resourceId;
		const kind = reference.kind;
		const mediaType = reference.mediaType;
		const byteLength = reference.byteLength;
		const sha256 = reference.sha256;
		const ordinal = reference.ordinal;
		if (
			typeof resourceId !== "string" ||
			typeof sha256 !== "string" ||
			!SHA256.test(sha256) ||
			resourceId !== sha256 ||
			!MEDIA_KINDS.includes(kind) ||
			typeof mediaType !== "string" ||
			mediaType.length === 0 ||
			!Number.isSafeInteger(byteLength) ||
			byteLength < 0 ||
			!Number.isSafeInteger(ordinal) ||
			ordinal < 0
		) {
			throw new TypeError();
		}
		if (Buffer.byteLength(mediaType) > maxMediaTypeBytes) throw new MediaJobCapacityError("resource");
		return Object.freeze({ resourceId, kind, mediaType, byteLength, sha256, ordinal });
	} catch (error) {
		if (error instanceof MediaJobCapacityError) throw error;
		throw new MediaResourceStoreError("invalid_reference");
	}
}

function mapResourceError(error: unknown): unknown {
	if (error instanceof ImmutableResourceCapacityError) return new MediaJobCapacityError("resource");
	if (error instanceof ImmutableResourceStoreError) return new MediaResourceStoreError(error.code);
	return error;
}

function operationByteLimit(value: unknown): number {
	if (!Number.isSafeInteger(value) || (value as number) < 0) {
		throw new RangeError("maxBytes must be a nonnegative safe integer.");
	}
	return value as number;
}

function operationPositiveLimit(value: unknown, name: string): number {
	if (!Number.isSafeInteger(value) || (value as number) < 1) {
		throw new RangeError(`${name} must be a positive safe integer.`);
	}
	return value as number;
}

function operationSignal(value: unknown): AbortSignal {
	if (!value || typeof value !== "object" || typeof (value as AbortSignal).throwIfAborted !== "function") {
		throw new TypeError("signal must be an AbortSignal.");
	}
	return value as AbortSignal;
}
