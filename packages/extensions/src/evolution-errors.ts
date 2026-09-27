export class BehaviorEvolutionValidationError extends TypeError {
	constructor(readonly code: string) {
		super(code);
		this.name = "BehaviorEvolutionValidationError";
	}
}

export class BehaviorEvolutionDisabledError extends Error {
	constructor() {
		super("behavior_evolution_disabled");
		this.name = "BehaviorEvolutionDisabledError";
	}
}

export class BehaviorProposalNotFoundError extends Error {
	constructor() {
		super("behavior_proposal_not_found");
		this.name = "BehaviorProposalNotFoundError";
	}
}

export class BehaviorProposalConflictError extends Error {
	constructor() {
		super("behavior_proposal_conflict");
		this.name = "BehaviorProposalConflictError";
	}
}

export class BehaviorProposalRevisionConflictError extends Error {
	constructor() {
		super("behavior_proposal_revision_conflict");
		this.name = "BehaviorProposalRevisionConflictError";
	}
}

export class BehaviorProposalCapacityError extends Error {
	constructor() {
		super("behavior_proposal_capacity");
		this.name = "BehaviorProposalCapacityError";
	}
}

export class BehaviorRegistryNotFoundError extends Error {
	constructor() {
		super("behavior_registry_not_found");
		this.name = "BehaviorRegistryNotFoundError";
	}
}

export class BehaviorRegistryRevisionConflictError extends Error {
	constructor() {
		super("behavior_registry_revision_conflict");
		this.name = "BehaviorRegistryRevisionConflictError";
	}
}

export class BehaviorVersionCapacityError extends Error {
	constructor() {
		super("behavior_version_capacity");
		this.name = "BehaviorVersionCapacityError";
	}
}

export class BehaviorVersionNotFoundError extends Error {
	constructor() {
		super("behavior_version_not_found");
		this.name = "BehaviorVersionNotFoundError";
	}
}

export class BehaviorToolUnavailableError extends Error {
	constructor() {
		super("behavior_tool_unavailable");
		this.name = "BehaviorToolUnavailableError";
	}
}
