export class DelegationError extends Error {
	constructor(readonly code: string) {
		super(code);
		this.name = "DelegationError";
	}
}

export class DelegationConflictError extends DelegationError {
	constructor(code = "delegation_conflict") {
		super(code);
		this.name = "DelegationConflictError";
	}
}

export class DelegationNotFoundError extends DelegationError {
	constructor() {
		super("delegation_not_found");
		this.name = "DelegationNotFoundError";
	}
}

export class DelegationLeaseError extends DelegationError {
	constructor() {
		super("delegation_lease_conflict");
		this.name = "DelegationLeaseError";
	}
}

export class DelegationCapacityError extends DelegationError {
	constructor() {
		super("delegation_capacity_exceeded");
		this.name = "DelegationCapacityError";
	}
}
