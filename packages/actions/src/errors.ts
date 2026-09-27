export class ActionIntentConflictError extends Error {
	constructor() {
		super("The operation identifier is already bound to a different action intent.");
		this.name = "ActionIntentConflictError";
	}
}

export class ActionNotFoundError extends Error {
	constructor() {
		super("The action operation was not found.");
		this.name = "ActionNotFoundError";
	}
}

export class ActionClaimConflictError extends Error {
	constructor() {
		super("The action claim token or attempt is stale.");
		this.name = "ActionClaimConflictError";
	}
}

export class ActionStateConflictError extends Error {
	constructor() {
		super("The action is not in a state that accepts this transition.");
		this.name = "ActionStateConflictError";
	}
}

export class ActionGoalProjectionCapacityError extends Error {
	constructor() {
		super("Goal projection capacity is exhausted for this Action stream.");
		this.name = "ActionGoalProjectionCapacityError";
	}
}
