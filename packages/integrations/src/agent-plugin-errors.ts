export type DirectoryAgentPluginErrorCode = "invalid_configuration" | "catalog_limit_exceeded" | "catalog_incomplete";

export class DirectoryAgentPluginError extends Error {
	constructor(public readonly code: DirectoryAgentPluginErrorCode) {
		super(code);
		this.name = "DirectoryAgentPluginError";
	}
}
