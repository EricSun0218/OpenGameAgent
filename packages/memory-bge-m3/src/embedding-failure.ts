export class BgeM3ModelOutputError extends Error {
	constructor() {
		super("invalid_embedding");
		this.name = "BgeM3ModelOutputError";
	}
}

export function classifyBgeM3RequestFailure(error: unknown): "invalid_embedding" | "worker_failure" {
	return error instanceof BgeM3ModelOutputError ? "invalid_embedding" : "worker_failure";
}
