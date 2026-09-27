export class ImmutableResourceCapacityError extends RangeError {
	constructor(readonly code: "resource" | "scan") {
		super(code);
		this.name = "ImmutableResourceCapacityError";
	}
}

export type ImmutableResourceStoreErrorCode =
	| "invalid_configuration"
	| "invalid_resource"
	| "invalid_reference"
	| "storage_missing"
	| "storage_corrupt"
	| "storage_changed"
	| "storage_busy"
	| "storage_unavailable";

export class ImmutableResourceStoreError extends Error {
	constructor(readonly code: ImmutableResourceStoreErrorCode) {
		super(code);
		this.name = "ImmutableResourceStoreError";
	}
}
