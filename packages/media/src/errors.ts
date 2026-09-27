export class MediaJobValidationError extends TypeError {
	constructor(readonly code: "invalid_job" | "invalid_provider_result" | "invalid_resource") {
		super(code);
		this.name = "MediaJobValidationError";
	}
}

export class MediaJobCapacityError extends RangeError {
	constructor(readonly code: "identifier" | "adapters" | "attempts" | "outputs" | "resource" | "bundle" | "manifest") {
		super(code);
		this.name = "MediaJobCapacityError";
	}
}

export class MediaJobAdapterUnavailableError extends Error {
	constructor() {
		super("The media adapter is unavailable.");
		this.name = "MediaJobAdapterUnavailableError";
	}
}

export class MediaJobAdmissionFullError extends Error {
	constructor() {
		super("The media provider call admission group is full.");
		this.name = "MediaJobAdmissionFullError";
	}
}

export class MediaJobConflictError extends Error {
	constructor() {
		super("The media job changed or already exists.");
		this.name = "MediaJobConflictError";
	}
}

export class MediaJobNotFoundError extends Error {
	constructor() {
		super("The media job does not exist.");
		this.name = "MediaJobNotFoundError";
	}
}

export class MediaJobControlUnsupportedError extends Error {
	constructor() {
		super("The media provider cannot perform this explicit control operation.");
		this.name = "MediaJobControlUnsupportedError";
	}
}

export type MediaResourceStoreErrorCode =
	| "invalid_configuration"
	| "invalid_resource"
	| "invalid_reference"
	| "storage_missing"
	| "storage_corrupt"
	| "storage_changed"
	| "storage_busy"
	| "storage_unavailable";

export class MediaResourceStoreError extends Error {
	constructor(readonly code: MediaResourceStoreErrorCode) {
		super(code);
		this.name = "MediaResourceStoreError";
	}
}
