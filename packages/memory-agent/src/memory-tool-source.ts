import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { GameRequestContribution, GameRequestSource, NormalizedGameInput } from "@opengameagent/agent";
import type { GameMemory, MemoryRecallResult } from "@opengameagent/memory";
import { assertOpaqueIdentifier } from "@opengameagent/protocol";
import { Type } from "typebox";
import {
	captureMemoryAgentInput,
	captureMemoryRecallObservation,
	createMemorySelector,
	type MemoryAgentAccessResolver,
	normalizeMemoryAgentAccess,
	projectMemoryRecall,
} from "./memory-agent-access.js";

const MemoryRecallParameters = Type.Object(
	{
		query: Type.String({
			minLength: 1,
			description: "Natural-language description of the game memory to recall.",
		}),
	},
	{ additionalProperties: false },
);

export const MEMORY_RECALL_TOOL_NAME = "memory_recall";

export interface MemoryToolSourceOptions {
	readonly sourceId: string;
	readonly memory: Pick<GameMemory, "recall">;
	readonly resolveAccess: MemoryAgentAccessResolver;
}

export class MemoryToolSource implements GameRequestSource {
	readonly sourceId: string;
	readonly provenanceKind = "memory" as const;
	private readonly recall: Pick<GameMemory, "recall">["recall"];
	private readonly resolveAccess: MemoryAgentAccessResolver;

	constructor(options: MemoryToolSourceOptions) {
		const sourceId = options.sourceId;
		const memory = options.memory;
		const resolveAccess = options.resolveAccess;
		assertOpaqueIdentifier(sourceId);
		if (!memory || typeof resolveAccess !== "function") {
			throw new TypeError("Invalid memory Tool source configuration.");
		}
		const recall = memory.recall;
		if (typeof recall !== "function") throw new TypeError("Invalid memory Tool source configuration.");
		this.sourceId = sourceId;
		this.recall = recall.bind(memory);
		this.resolveAccess = resolveAccess;
	}

	async collect(input: Readonly<NormalizedGameInput>, signal: AbortSignal): Promise<Readonly<GameRequestContribution>> {
		signal.throwIfAborted();
		const identity = captureMemoryAgentInput(input);
		const resolvedAccess = await this.resolveAccess(input, signal);
		signal.throwIfAborted();
		const access = normalizeMemoryAgentAccess(identity, resolvedAccess);
		const recall = this.recall;
		const tool: AgentTool<typeof MemoryRecallParameters> = {
			name: MEMORY_RECALL_TOOL_NAME,
			label: "Recall game memory",
			description:
				"Recall relevant persistent game memory within the identity, timeline, visibility, and scope selected by the game host.",
			parameters: MemoryRecallParameters,
			executionMode: "parallel",
			execute: async (_toolCallId, parameters, executionSignal) => {
				const toolSignal = executionSignal ?? signal;
				toolSignal.throwIfAborted();
				const result = await recall({
					query: parameters.query,
					selector: createMemorySelector(identity, access),
					viewer: access.viewer,
					resultLimit: access.resultLimit,
					vectorFailure: access.vectorFailure,
					signal: toolSignal,
				});
				toolSignal.throwIfAborted();
				return memoryToolResult(identity, access, result);
			},
		};
		return Object.freeze({ tools: Object.freeze([tool]) });
	}
}

function memoryToolResult(
	identity: Parameters<typeof projectMemoryRecall>[0],
	access: Parameters<typeof projectMemoryRecall>[1],
	result: MemoryRecallResult,
): AgentToolResult<undefined> {
	const memories = projectMemoryRecall(identity, access, result);
	const { vectorStatus } = captureMemoryRecallObservation(result);
	return {
		content: [
			{
				type: "text",
				text: JSON.stringify({ memories, vectorStatus }),
			},
		],
		details: undefined,
	};
}
