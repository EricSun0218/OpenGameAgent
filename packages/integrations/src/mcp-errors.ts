import type { AgentToolResult } from "@earendil-works/pi-agent-core";
import { GameToolReportedError } from "@opengameagent/agent";

export type GameMcpErrorCode =
	| "invalid_configuration"
	| "server_unavailable"
	| "server_timeout"
	| "authentication_required"
	| "permission_denied"
	| "server_capability_missing"
	| "catalog_incomplete"
	| "catalog_changed"
	| "catalog_limit_exceeded"
	| "invalid_tool_schema"
	| "invalid_selection"
	| "tool_unavailable"
	| "tool_not_authorized"
	| "invalid_tool_arguments"
	| "tool_call_limit_exceeded"
	| "result_limit_exceeded"
	| "unsupported_result_content"
	| "transport_limit_exceeded"
	| "transport_rejected";

export class GameMcpError extends Error {
	constructor(public readonly code: GameMcpErrorCode) {
		super(code);
		this.name = "GameMcpError";
	}
}

/** A bounded server-reported Tool failure that Pi must record as an error result for self-correction. */
export class GameMcpToolReportedError<T = unknown> extends GameToolReportedError<T> {
	readonly code = "mcp_tool_reported_error" as const;

	constructor(result: AgentToolResult<T>) {
		super(result, "mcp_tool_reported_error");
		this.name = "GameMcpToolReportedError";
	}
}
