import type { GameContextContribution, GameContextSource, NormalizedGameInput } from "@opengameagent/agent";
import type { GameMemory, MemoryRecallResult, MemoryRecallStageMetric } from "@opengameagent/memory";
import { assertOpaqueIdentifier, type RunCoordinate } from "@opengameagent/protocol";
import {
	captureMemoryAgentInput,
	createMemorySelector,
	type MemoryAgentAccess,
	normalizeMemoryAgentAccess,
	projectMemoryRecall,
	captureMemoryRecallObservation,
} from "./memory-agent-access.js";

export interface MemoryContextQuery extends MemoryAgentAccess {
	readonly query: string;
}

export type MemoryContextQueryResolver = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
) => MemoryContextQuery | Promise<MemoryContextQuery>;

export interface MemoryContextSourceOptions {
	readonly sourceId: string;
	readonly memory: Pick<GameMemory, "recall">;
	readonly resolveQuery: MemoryContextQueryResolver;
	/** Content-free host telemetry. Observer failures never affect context assembly. */
	readonly observeRecall?: (observation: MemoryContextRecallObservation) => void | PromiseLike<void>;
}

export interface MemoryContextRecallObservation {
	readonly vectorStatus: MemoryRecallResult["vectorStatus"];
	readonly stages: readonly MemoryRecallStageMetric[];
}

export class MemoryContextSource implements GameContextSource {
	readonly sourceId: string;
	readonly provenanceKind = "memory" as const;
	private readonly recall: Pick<GameMemory, "recall">["recall"];
	private readonly resolveQuery: MemoryContextQueryResolver;
	private readonly observeRecall:
		| ((observation: MemoryContextRecallObservation) => void | PromiseLike<void>)
		| undefined;

	constructor(options: MemoryContextSourceOptions) {
		const sourceId = options.sourceId;
		const memory = options.memory;
		const resolveQuery = options.resolveQuery;
		const observeRecall = options.observeRecall;
		assertOpaqueIdentifier(sourceId);
		if (!memory || typeof resolveQuery !== "function") {
			throw new TypeError("Invalid memory context source configuration.");
		}
		if (observeRecall !== undefined && typeof observeRecall !== "function") {
			throw new TypeError("Invalid memory context source configuration.");
		}
		const recall = memory.recall;
		if (typeof recall !== "function") throw new TypeError("Invalid memory context source configuration.");
		this.sourceId = sourceId;
		this.recall = recall.bind(memory);
		this.resolveQuery = resolveQuery;
		this.observeRecall = observeRecall;
	}

	async load(
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		_coordinate: Readonly<RunCoordinate>,
	): Promise<GameContextContribution> {
		signal.throwIfAborted();
		const identity = captureMemoryAgentInput(input);
		const resolvedQuery = await this.resolveQuery(input, signal);
		signal.throwIfAborted();
		const query = normalizeContextQuery(identity, resolvedQuery);
		const selector = createMemorySelector(identity, query);
		const result = await this.recall({
			query: query.query,
			selector,
			viewer: query.viewer,
			resultLimit: query.resultLimit,
			vectorFailure: query.vectorFailure,
			signal,
		});
		signal.throwIfAborted();
		const references = projectMemoryRecall(identity, query, result);
		const observation = captureMemoryRecallObservation(result);
		if (this.observeRecall) {
			try {
				void Promise.resolve(this.observeRecall(observation)).catch(() => undefined);
			} catch {
				// Host telemetry is observational and must not fail model context assembly.
			}
		}
		return { references };
	}
}

function normalizeContextQuery(
	input: Parameters<typeof normalizeMemoryAgentAccess>[0],
	query: MemoryContextQuery,
): MemoryContextQuery {
	const queryText = query.query;
	return { query: queryText, ...normalizeMemoryAgentAccess(input, query) };
}
