export class KnowledgeValidationError extends TypeError {
	constructor(message: string, options?: ErrorOptions) {
		super(message, options);
		this.name = "KnowledgeValidationError";
	}
}

export class KnowledgeSourceResultError extends TypeError {
	constructor(options?: ErrorOptions) {
		super("External knowledge source returned an invalid result.", options);
		this.name = "KnowledgeSourceResultError";
	}
}

export type KnowledgeHttpErrorCode =
	| "invalid_limits"
	| "invalid_endpoint"
	| "invalid_headers"
	| "request_too_large"
	| "request_aborted"
	| "request_timeout"
	| "transport_failed"
	| "redirect_blocked"
	| "http_status"
	| "unsupported_content_type"
	| "response_too_large"
	| "invalid_response";

export class KnowledgeHttpError extends Error {
	constructor(
		public readonly code: KnowledgeHttpErrorCode,
		options?: ErrorOptions,
	) {
		super("External knowledge HTTP source failed.", options);
		this.name = "KnowledgeHttpError";
	}
}
