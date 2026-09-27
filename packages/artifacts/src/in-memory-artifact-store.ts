import { canonicalizeJsonValue } from "@opengameagent/protocol";
import { ArtifactCapacityError, ArtifactConflictError } from "./artifact-errors.js";
import type { ArtifactLimits, ArtifactRecord, ArtifactStore } from "./artifact-types.js";
import {
	cloneArtifactRecord,
	freezeArtifactLimits,
	sameArtifactBinding,
	validateArtifactFingerprint,
	validateArtifactPurge,
	validateArtifactRecord,
	validateArtifactStream,
} from "./artifact-validation.js";

export class InMemoryArtifactStore implements ArtifactStore {
	readonly limits: Readonly<ArtifactLimits>;
	private readonly records = new Map<string, ArtifactRecord>();

	constructor(limits: ArtifactLimits) {
		this.limits = freezeArtifactLimits(limits);
	}

	async put(recordValue: Readonly<ArtifactRecord>, signal?: AbortSignal) {
		signal?.throwIfAborted();
		const record = validateArtifactRecord(recordValue, this.limits);
		const key = recordKey(record);
		const existing = this.records.get(key);
		if (existing) {
			if (!sameArtifactBinding(existing, record)) throw new ArtifactConflictError();
			return { status: "existing" as const, record: cloneArtifactRecord(existing) };
		}
		const partition = [...this.records.values()].filter(
			(candidate) => candidate.sessionId === record.sessionId && candidate.actorId === record.actorId,
		);
		const storedBytes = partition.reduce((total, candidate) => total + candidate.byteLength, 0);
		if (
			partition.length >= this.limits.maxArtifactsPerStream ||
			record.byteLength > this.limits.maxStoredBytesPerStream - storedBytes
		) {
			throw new ArtifactCapacityError();
		}
		this.records.set(key, cloneArtifactRecord(record));
		return { status: "created" as const, record: cloneArtifactRecord(record) };
	}

	async read(
		streamValue: Readonly<{ sessionId: string; actorId: string }>,
		artifactIdValue: string,
		signal?: AbortSignal,
	): Promise<ArtifactRecord | undefined> {
		signal?.throwIfAborted();
		const stream = validateArtifactStream(streamValue, this.limits);
		const artifactId = validateArtifactFingerprint(artifactIdValue, "artifactId");
		const record = this.records.get(recordKey({ ...stream, artifactId }));
		return record ? cloneArtifactRecord(validateArtifactRecord(record, this.limits)) : undefined;
	}

	async purge(
		streamValue: Readonly<{ sessionId: string; actorId: string }>,
		createdBeforeValue: string,
		limitValue: number,
		signal?: AbortSignal,
	): Promise<number> {
		signal?.throwIfAborted();
		const { stream, createdBefore, limit } = validateArtifactPurge(
			streamValue,
			createdBeforeValue,
			limitValue,
			this.limits,
		);
		const selected = [...this.records.entries()]
			.filter(
				([, record]) =>
					record.sessionId === stream.sessionId &&
					record.actorId === stream.actorId &&
					record.createdAt < createdBefore,
			)
			.sort(([, left], [, right]) =>
				left.createdAt === right.createdAt
					? left.artifactId.localeCompare(right.artifactId)
					: left.createdAt.localeCompare(right.createdAt),
			)
			.slice(0, limit);
		for (const [key, record] of selected) {
			validateArtifactRecord(record, this.limits);
			this.records.delete(key);
		}
		return selected.length;
	}
}

function recordKey(value: { sessionId: string; actorId: string; artifactId: string }): string {
	return canonicalizeJsonValue([value.sessionId, value.actorId, value.artifactId]);
}
