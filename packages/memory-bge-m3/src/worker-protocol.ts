import type { ValidatedBgeM3Configuration } from "./validation.js";

export interface BgeM3WorkerData {
	readonly configuration: ValidatedBgeM3Configuration;
}

export type BgeM3WorkerRequest = {
	readonly kind: "embed";
	readonly requestId: number;
	readonly texts: readonly string[];
};

export type BgeM3WorkerResponse =
	| { readonly kind: "ready" }
	| {
			readonly kind: "result";
			readonly requestId: number;
			readonly vectors: readonly ArrayBuffer[];
	  }
	| {
			readonly kind: "failure";
			readonly requestId?: number;
			readonly code: "invalid_artifact" | "invalid_configuration" | "invalid_embedding" | "worker_failure";
			readonly terminal: boolean;
	  };
