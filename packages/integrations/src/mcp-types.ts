import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import type { ToolAnnotations } from "@modelcontextprotocol/sdk/types.js";
import type { NormalizedGameInput } from "@opengameagent/agent";
import type { JsonValue, RunCoordinate } from "@opengameagent/protocol";

export type GameMcpTransportFactory = (signal: AbortSignal) => Transport | Promise<Transport>;

export interface GameMcpServerDefinition {
	readonly serverId: string;
	readonly description: string;
	readonly requirement: "required" | "optional";
	readonly createTransport: GameMcpTransportFactory;
}

/** Every number is mandatory host configuration; no workload or timeout defaults are supplied. */
export interface GameMcpLimits {
	readonly maxServers: number;
	readonly maxServerIdBytes: number;
	readonly maxServerDescriptionBytes: number;
	readonly maxListPages: number;
	readonly maxCursorBytes: number;
	readonly maxToolsPerServer: number;
	readonly maxDiagnostics: number;
	readonly maxToolNameBytes: number;
	readonly maxToolTitleBytes: number;
	readonly maxToolDescriptionBytes: number;
	readonly maxSchemaDepth: number;
	readonly maxSchemaBytes: number;
	readonly maxCatalogBytes: number;
	readonly maxSearchQueryBytes: number;
	readonly maxSearchResults: number;
	readonly maxCallArgumentDepth: number;
	readonly maxCallArgumentBytes: number;
	readonly maxResultContentBlocks: number;
	readonly maxResultTextBytes: number;
	readonly maxResultMediaBytes: number;
	readonly maxResultUriBytes: number;
	readonly maxResultMimeTypeBytes: number;
	readonly maxStructuredContentDepth: number;
	readonly maxStructuredContentBytes: number;
	readonly maxToolResultBytes: number;
	readonly maxConcurrentCallsPerServer: number;
	readonly requestIdleTimeoutMs: number;
	readonly requestTotalTimeoutMs: number;
}

export interface GameMcpToolSummary {
	readonly serverId: string;
	readonly serverDescription: string;
	readonly name: string;
	readonly title?: string;
	readonly description?: string;
	/** Untrusted protocol hints; never an authorization decision. */
	readonly annotations?: Readonly<ToolAnnotations>;
}

export type GameMcpToolSelection =
	| {
			readonly serverId: string;
			readonly name: string;
			readonly exposure: "lazy";
			readonly executionMode: "parallel" | "sequential";
	  }
	| {
			readonly serverId: string;
			readonly name: string;
			readonly exposure: "direct";
			readonly modelToolName: string;
			readonly executionMode: "parallel" | "sequential";
	  };

export interface GameMcpSelectionRequest {
	readonly input: Readonly<NormalizedGameInput>;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly visibleToolNames: readonly string[];
	readonly candidates: readonly GameMcpToolSummary[];
}

export type GameMcpToolSelector = (
	request: Readonly<GameMcpSelectionRequest>,
	signal: AbortSignal,
) => readonly GameMcpToolSelection[] | Promise<readonly GameMcpToolSelection[]>;

export interface GameMcpCallAuthorizationRequest {
	readonly input: Readonly<NormalizedGameInput>;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly toolCallId: string;
	readonly serverId: string;
	readonly name: string;
	readonly arguments: Readonly<JsonValue>;
	readonly annotations?: Readonly<ToolAnnotations>;
}

export type GameMcpCallAuthorizer = (
	request: Readonly<GameMcpCallAuthorizationRequest>,
	signal: AbortSignal,
) => void | Promise<void>;

export interface GameMcpLazyToolNames {
	readonly search: string;
	readonly describe: string;
	readonly callParallel: string;
	readonly callSequential: string;
}

export interface GameMcpDiagnostic {
	readonly serverId: string;
	readonly code:
		| "optional_server_unavailable"
		| "task_required_tool_ignored"
		| "tool_list_changed"
		| "connection_closed"
		| "connection_error";
}

export interface GameMcpRequestSourceOptions {
	readonly sourceId: string;
	readonly servers: readonly GameMcpServerDefinition[];
	readonly limits: GameMcpLimits;
	readonly lazyToolNames: GameMcpLazyToolNames;
	readonly select: GameMcpToolSelector;
	readonly authorizeCall: GameMcpCallAuthorizer;
	readonly onDiagnostic?: (diagnostic: Readonly<GameMcpDiagnostic>) => void;
}

export interface GameMcpCatalogInspection {
	readonly revision: string;
	readonly tools: readonly GameMcpToolSummary[];
	readonly diagnostics: readonly GameMcpDiagnostic[];
}
