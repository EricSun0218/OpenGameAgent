import type { AgentTool } from "@earendil-works/pi-agent-core";

export const DURABLE_TOOL_EXECUTION = Symbol.for("@opengameagent/durable-tool-execution");

/** Signals that a started durable effect requires authoritative recovery instead of a synthetic Pi Tool result. */
export class DurableToolExecutionUnresolvedError extends Error {
	constructor(cause?: unknown) {
		super(
			"The prepared durable Tool execution did not produce a trustworthy result.",
			cause === undefined ? undefined : { cause },
		);
		this.name = "DurableToolExecutionUnresolvedError";
	}
}

export interface PreparedDurableToolExecution<TTool extends AgentTool = AgentTool> {
	readonly operationId?: string;
	execute(onUpdate?: Parameters<TTool["execute"]>[3]): ReturnType<TTool["execute"]>;
}

export type DurableExecutionAgentTool<TTool extends AgentTool = AgentTool> = TTool & {
	[DURABLE_TOOL_EXECUTION]?: (
		toolCallId: Parameters<TTool["execute"]>[0],
		parameters: Parameters<TTool["execute"]>[1],
		signal?: Parameters<TTool["execute"]>[2],
	) => PreparedDurableToolExecution<TTool> | Promise<PreparedDurableToolExecution<TTool>>;
};
