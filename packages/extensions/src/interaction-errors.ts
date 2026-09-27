export class InteractionValidationError extends TypeError {
	constructor(message: string, options?: ErrorOptions) {
		super(message, options);
		this.name = "InteractionValidationError";
	}
}

export class InteractionCapacityError extends Error {
	constructor() {
		super("The host-configured pending interaction capacity is exhausted.");
		this.name = "InteractionCapacityError";
	}
}

export class InteractionConflictError extends Error {
	constructor() {
		super("The interaction request identity is already bound to different content.");
		this.name = "InteractionConflictError";
	}
}

export class InteractionBrokerResponseError extends Error {
	constructor(options?: ErrorOptions) {
		super("The player interaction returned an invalid response.", options);
		this.name = "InteractionBrokerResponseError";
	}
}

export class InteractionBrokerError extends Error {
	constructor(options?: ErrorOptions) {
		super("The player interaction could not be completed.", options);
		this.name = "InteractionBrokerError";
	}
}
