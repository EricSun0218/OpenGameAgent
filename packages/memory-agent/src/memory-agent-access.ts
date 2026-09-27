import { type GameContextReference, type NormalizedGameInput, recordRunPhaseDuration } from "@opengameagent/agent";
import {
	type MemoryRecallRequest,
	type MemoryRecallResult,
	type MemoryRecallStageMetric,
	type MemoryRecord,
	type MemorySelector,
	type MemoryViewer,
	normalizeMemoryViewer,
} from "@opengameagent/memory";
import { assertGameTime, assertOpaqueIdentifier, normalizeJsonValue } from "@opengameagent/protocol";

export type MemoryContextIdentityScope = "owner" | "actor" | "session";

export interface MemoryAgentAccess {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
	readonly identityScope: MemoryContextIdentityScope;
	readonly memoryScope?: string;
	readonly kinds?: readonly string[];
	readonly tags?: readonly string[];
	readonly atOrBeforeTick?: string;
	readonly viewer: MemoryViewer;
	readonly resultLimit: number;
	readonly vectorFailure: MemoryRecallRequest["vectorFailure"];
}

export type MemoryAgentAccessResolver = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
) => MemoryAgentAccess | Promise<MemoryAgentAccess>;

export interface MemoryAgentInputIdentity {
	readonly actorId: string;
	readonly sessionId: string;
}

export class MemoryContextIsolationError extends Error {
	constructor() {
		super("Memory recall returned a record outside the requested game identity.");
		this.name = "MemoryContextIsolationError";
	}
}

export function captureMemoryAgentInput(input: Readonly<NormalizedGameInput>): MemoryAgentInputIdentity {
	return { actorId: input.actorId, sessionId: input.sessionId };
}

export function normalizeMemoryAgentAccess(
	input: MemoryAgentInputIdentity,
	access: MemoryAgentAccess,
): MemoryAgentAccess {
	const ownerId = access.ownerId;
	const timelineId = access.timelineId;
	const generation = access.generation;
	const identityScope = access.identityScope;
	const memoryScope = access.memoryScope;
	const kinds = captureOptionalArray(access.kinds);
	const tags = captureOptionalArray(access.tags);
	const atOrBeforeTick = access.atOrBeforeTick;
	const viewer = normalizeMemoryViewer(access.viewer);
	const resultLimit = access.resultLimit;
	const vectorFailure = access.vectorFailure;
	assertOpaqueIdentifier(ownerId);
	assertGameTime({ timelineId, generation, tick: "0" });
	if (identityScope !== "owner" && identityScope !== "actor" && identityScope !== "session") {
		throw new TypeError("Invalid memory context identity scope.");
	}
	if (identityScope !== "owner") assertOpaqueIdentifier(input.actorId);
	if (identityScope === "session") assertOpaqueIdentifier(input.sessionId);
	return {
		ownerId,
		timelineId,
		generation,
		identityScope,
		...(memoryScope === undefined ? {} : { memoryScope }),
		...(kinds === undefined ? {} : { kinds }),
		...(tags === undefined ? {} : { tags }),
		...(atOrBeforeTick === undefined ? {} : { atOrBeforeTick }),
		viewer,
		resultLimit,
		vectorFailure,
	};
}

export function createMemorySelector(input: MemoryAgentInputIdentity, access: MemoryAgentAccess): MemorySelector {
	return {
		ownerId: access.ownerId,
		timelineId: access.timelineId,
		generation: access.generation,
		...(access.identityScope === "owner" ? {} : { actorId: input.actorId }),
		...(access.identityScope === "session" ? { sessionId: input.sessionId } : {}),
		...(access.memoryScope === undefined ? {} : { scope: access.memoryScope }),
		...(access.kinds === undefined ? {} : { kinds: access.kinds }),
		...(access.tags === undefined ? {} : { tags: access.tags }),
		...(access.atOrBeforeTick === undefined ? {} : { atOrBeforeTick: access.atOrBeforeTick }),
	};
}

export function projectMemoryRecall(
	input: MemoryAgentInputIdentity,
	access: MemoryAgentAccess,
	result: MemoryRecallResult,
): readonly GameContextReference[] {
	const hits = result.hits;
	if (!Array.isArray(hits)) throw new TypeError("Memory recall returned invalid hits.");
	const length = Math.min(hits.length, access.resultLimit);
	const references: GameContextReference[] = [];
	for (let index = 0; index < length; index += 1) {
		const record = hits[index]?.record;
		if (!record) throw new TypeError("Memory recall returned an invalid hit.");
		references.push(projectMemoryRecord(input, access, record));
	}
	return references;
}

export function captureMemoryVectorStatus(result: MemoryRecallResult): MemoryRecallResult["vectorStatus"] {
	const status = result.vectorStatus;
	if (
		status !== "not-configured" &&
		status !== "unprepared" &&
		status !== "preparing" &&
		status !== "preparation-failed" &&
		status !== "proven-empty" &&
		status !== "used" &&
		status !== "failed-open"
	) {
		throw new TypeError("Memory recall returned an invalid vector status.");
	}
	return status;
}

function projectMemoryRecord(
	input: MemoryAgentInputIdentity,
	access: MemoryAgentAccess,
	record: MemoryRecord,
): GameContextReference {
	const memoryId = record.memoryId;
	const revision = record.revision;
	const lifecycle = record.lifecycle;
	const partition = record.partition;
	const ownerId = partition.ownerId;
	const actorId = partition.actorId;
	const sessionId = partition.sessionId;
	const memoryScope = partition.scope;
	const gameTime = record.gameTime;
	const timelineId = gameTime.timelineId;
	const generation = gameTime.generation;
	const tick = gameTime.tick;
	const kind = record.kind;
	const tags = record.tags;
	const importance = record.importance;
	const content = record.content;
	if (
		lifecycle !== "active" ||
		ownerId !== access.ownerId ||
		timelineId !== access.timelineId ||
		generation !== access.generation ||
		(access.memoryScope !== undefined && memoryScope !== access.memoryScope) ||
		(access.identityScope !== "owner" && actorId !== input.actorId) ||
		(access.identityScope === "session" && sessionId !== input.sessionId)
	) {
		throw new MemoryContextIsolationError();
	}
	return {
		referenceId: memoryId,
		revision,
		content: normalizeJsonValue({
			kind,
			tags,
			gameTime: { timelineId, generation, tick },
			importance,
			memoryScope,
			content,
		}),
	};
}

function captureOptionalArray(values: readonly string[] | undefined): readonly string[] | undefined {
	if (values === undefined) return undefined;
	if (!Array.isArray(values)) throw new TypeError("Invalid memory selector values.");
	const length = values.length;
	const captured: string[] = [];
	for (let index = 0; index < length; index += 1) captured.push(values[index] as string);
	return Object.freeze(captured);
}

export function captureMemoryRecallObservation(
	result: MemoryRecallResult,
): Readonly<Pick<MemoryRecallResult, "vectorStatus" | "stages">> {
	const allowedStages = [
		"partition",
		"lexical",
		"index-restore",
		"embedding",
		"vector",
		"fusion",
		"hydrate",
		"rerank",
	] as const;
	const sourceStages = result.stages;
	if (!Array.isArray(sourceStages) || sourceStages.length > allowedStages.length)
		throw new TypeError("Memory recall returned invalid stage metrics.");
	const stages = sourceStages.map(
		({ stage, outcome, durationMs, inputCount, outputCount }): MemoryRecallStageMetric => {
			if (
				!allowedStages.includes(stage) ||
				(outcome !== "ok" && outcome !== "failed-open") ||
				!Number.isFinite(durationMs) ||
				durationMs < 0 ||
				!Number.isSafeInteger(inputCount) ||
				inputCount < 0 ||
				!Number.isSafeInteger(outputCount) ||
				outputCount < 0
			) {
				throw new TypeError("Memory recall returned invalid stage metrics.");
			}
			return Object.freeze({ stage, outcome, durationMs, inputCount, outputCount });
		},
	);
	const vectorStatus = captureMemoryVectorStatus(result);
	let memoryPrepare = 0;
	let memorySearch = 0;
	for (const metric of stages) {
		if (metric.stage === "partition" || metric.stage === "index-restore") memoryPrepare += metric.durationMs;
		else memorySearch += metric.durationMs;
	}
	recordRunPhaseDuration("memoryPrepare", memoryPrepare);
	recordRunPhaseDuration("memorySearch", memorySearch);
	return Object.freeze({ vectorStatus, stages: Object.freeze(stages) });
}
