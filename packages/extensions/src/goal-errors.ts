export class GoalValidationError extends TypeError {
	constructor(readonly code: string) {
		super(code);
		this.name = "GoalValidationError";
	}
}

export class GoalNotFoundError extends Error {
	constructor() {
		super("goal_not_found");
		this.name = "GoalNotFoundError";
	}
}

export class GoalConflictError extends Error {
	constructor() {
		super("goal_conflict");
		this.name = "GoalConflictError";
	}
}

export class GoalRevisionConflictError extends Error {
	constructor() {
		super("goal_revision_conflict");
		this.name = "GoalRevisionConflictError";
	}
}

export class GoalCapacityError extends Error {
	constructor() {
		super("goal_capacity");
		this.name = "GoalCapacityError";
	}
}

export class GoalEvidenceRejectedError extends Error {
	constructor() {
		super("goal_evidence_rejected");
		this.name = "GoalEvidenceRejectedError";
	}
}

export class GoalAuthorityRetiredError extends Error {
	constructor() {
		super("goal_authority_retired");
		this.name = "GoalAuthorityRetiredError";
	}
}

export class GoalAuthorityRetirementConflictError extends RunAuthorityConflictError {
	constructor() {
		super();
		this.message = "goal_authority_retirement_conflict";
		this.name = "GoalAuthorityRetirementConflictError";
	}
}
import { RunAuthorityConflictError } from "@opengameagent/protocol";
