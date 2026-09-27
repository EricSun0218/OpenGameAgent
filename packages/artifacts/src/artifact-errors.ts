export class ArtifactValidationError extends TypeError {
	constructor(message: string, options?: ErrorOptions) {
		super(message, options);
		this.name = "ArtifactValidationError";
	}
}

export class ArtifactConflictError extends Error {
	constructor() {
		super("The artifact identity is already bound to different content.");
		this.name = "ArtifactConflictError";
	}
}

export class ArtifactCapacityError extends Error {
	constructor() {
		super("The host-configured artifact capacity is exhausted.");
		this.name = "ArtifactCapacityError";
	}
}

export class ArtifactCorruptionError extends Error {
	constructor(options?: ErrorOptions) {
		super("Stored artifact data is corrupt.", options);
		this.name = "ArtifactCorruptionError";
	}
}

export class ArtifactReadError extends Error {
	constructor(options?: ErrorOptions) {
		super("The artifact could not be read.", options);
		this.name = "ArtifactReadError";
	}
}
