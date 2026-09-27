export type GameCharacterPersonaErrorCode =
	| "invalid_configuration"
	| "invalid_persona"
	| "definition_too_large"
	| "instructions_too_large";

export class GameCharacterPersonaError extends TypeError {
	constructor(readonly code: GameCharacterPersonaErrorCode) {
		super(code);
		this.name = "GameCharacterPersonaError";
	}
}

export type GameWorldbookErrorCode =
	| "invalid_configuration"
	| "invalid_worldbook"
	| "worldbook_capacity"
	| "invalid_scan"
	| "scan_capacity"
	| "match_capacity"
	| "activation_capacity"
	| "recursion_limit_exceeded"
	| "invalid_token_upper_bound";

export class GameWorldbookError extends TypeError {
	constructor(readonly code: GameWorldbookErrorCode) {
		super(code);
		this.name = "GameWorldbookError";
	}
}
