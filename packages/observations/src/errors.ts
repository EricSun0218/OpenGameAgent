export type VisualObservationErrorCode =
	| "invalid_configuration"
	| "invalid_snapshot"
	| "required_budget_exceeded"
	| "transform_output_too_large"
	| "transform_failed";

export class VisualObservationError extends Error {
	constructor(public readonly code: VisualObservationErrorCode) {
		super(code);
		this.name = "VisualObservationError";
	}
}
