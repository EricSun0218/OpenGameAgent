export type DirectoryGameSkillErrorCode =
	| "invalid_configuration"
	| "catalog_incomplete"
	| "catalog_limit_exceeded"
	| "root_escape"
	| "invalid_selection"
	| "skill_unavailable"
	| "invalid_resource_path"
	| "resource_unavailable"
	| "resource_not_file"
	| "resource_limit_exceeded"
	| "resource_not_text"
	| "tool_result_limit_exceeded"
	| "unsupported_discovery_operation";

export class DirectoryGameSkillError extends Error {
	constructor(public readonly code: DirectoryGameSkillErrorCode) {
		super(code);
		this.name = "DirectoryGameSkillError";
	}
}
