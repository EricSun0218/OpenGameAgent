import type { DirectoryGameSkillRoot } from "./types.js";

export const AGENT_PLUGIN_MANIFEST_SCHEMA_V1 = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json" as const;
export const AGENT_PLUGIN_MCP_SCHEMA_V1 = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json" as const;
/** Agent Plugins 1.0 protocol bound. */
export const AGENT_PLUGIN_MAX_NAME_CHARACTERS = 64 as const;

export function isAgentPluginName(value: string): boolean {
	return (
		value.length >= 1 &&
		value.length <= AGENT_PLUGIN_MAX_NAME_CHARACTERS &&
		/^(?!.*(?:--|\.\.))[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/u.test(value)
	);
}

export interface DirectoryAgentPluginInstallation {
	/** Stable host identity for this installed instance. */
	readonly installationId: string;
	/** Plugin package directory, resolved against `cwd` when relative. */
	readonly rootPath: string;
	/** Dedicated persistent writable directory, resolved against `cwd` when relative. */
	readonly dataPath: string;
}

/** Host-required resource policy for bounds not fixed by the portable format. */
export interface DirectoryAgentPluginLimits {
	readonly maxInstallations: number;
	readonly maxInstallationIdBytes: number;
	readonly maxAddressedPathBytes: number;
	readonly maxManifestBytes: number;
	readonly maxMcpConfigurationBytes: number;
	readonly maxTotalReadBytes: number;
	readonly maxJsonDepth: number;
	readonly maxDiagnostics: number;
	readonly maxMcpServersPerPlugin: number;
	readonly maxServerNameBytes: number;
	readonly maxExpandedValueBytes: number;
}

export interface AgentPluginAuthor {
	readonly name?: string;
	readonly email?: string;
	readonly url?: string;
}

export interface AgentPluginV1Manifest {
	readonly schema: typeof AGENT_PLUGIN_MANIFEST_SCHEMA_V1;
	readonly name: string;
	readonly version?: string;
	readonly description?: string;
	readonly author?: AgentPluginAuthor;
	readonly homepage?: string;
	readonly repository?: string;
	readonly license?: string;
	readonly keywords: readonly string[];
}

export type DirectoryAgentPluginDiagnosticCode =
	| "plugin_unavailable"
	| "manifest_invalid"
	| "manifest_schema_unsupported"
	| "manifest_field_ignored"
	| "manifest_extensions_ignored"
	| "skills_invalid"
	| "mcp_configuration_invalid"
	| "mcp_server_invalid"
	| "mcp_transport_unsupported";

export interface DirectoryAgentPluginDiagnostic {
	readonly installationId: string;
	readonly code: DirectoryAgentPluginDiagnosticCode;
	/** Included only after the server name passes the host byte/control-character policy. */
	readonly serverName?: string;
}

export type AgentPluginStdioCommand =
	| { readonly kind: "bare"; readonly value: string }
	| {
			readonly kind: "plugin-relative";
			readonly relativePath: string;
			readonly canonicalPath: string;
	  };

export interface AgentPluginStdioMcpServer {
	readonly type: "stdio";
	readonly command: AgentPluginStdioCommand;
	readonly args: readonly string[];
	readonly env: Readonly<Record<string, string>>;
	readonly cwd?: string;
}

export interface AgentPluginStreamableHttpMcpServer {
	readonly type: "streamable-http";
	readonly url: string;
	readonly headers: Readonly<Record<string, string>>;
}

export interface DirectoryAgentPluginMcpServer {
	readonly installationId: string;
	readonly pluginName: string;
	readonly serverName: string;
	readonly pluginAddressedRoot: string;
	readonly pluginRootIdentity: {
		readonly canonicalPath: string;
		readonly device: string;
		readonly inode: string;
	};
	readonly pluginDataPath: string;
	readonly maxExpandedValueBytes: number;
	readonly configuration: AgentPluginStdioMcpServer | AgentPluginStreamableHttpMcpServer;
}

export interface DirectoryAgentPluginSummary {
	readonly installationId: string;
	readonly manifest: AgentPluginV1Manifest;
	readonly hasSkills: boolean;
	readonly mcpServerNames: readonly string[];
}

export interface DirectoryAgentPluginCatalogSnapshot {
	readonly revision: string;
	readonly plugins: readonly DirectoryAgentPluginSummary[];
	/** Direct input for `DirectoryGameSkillSource`; no package code is loaded. */
	readonly skillRoots: readonly DirectoryGameSkillRoot[];
	/** Validated declarations. A host must explicitly admit one before it can connect or execute. */
	readonly mcpServers: readonly DirectoryAgentPluginMcpServer[];
	readonly diagnostics: readonly DirectoryAgentPluginDiagnostic[];
}

export interface DirectoryAgentPluginCatalogOptions {
	readonly cwd: string;
	readonly installations: readonly DirectoryAgentPluginInstallation[];
	readonly limits: DirectoryAgentPluginLimits;
}
