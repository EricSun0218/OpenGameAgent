export type AgentPluginPackageErrorCode =
	| "invalid_configuration"
	| "source_invalid"
	| "package_invalid"
	| "package_limit_exceeded"
	| "signature_invalid"
	| "rollback_rejected"
	| "installation_conflict"
	| "operation_incomplete";

export class AgentPluginPackageError extends Error {
	constructor(public readonly code: AgentPluginPackageErrorCode) {
		super(code);
		this.name = "AgentPluginPackageError";
	}
}
