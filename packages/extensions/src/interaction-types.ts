import type { JsonValue, RunCoordinate } from "@opengameagent/protocol";

export interface InteractionLimits {
	readonly maxIdentifierBytes: number;
	readonly maxCounterDigits: number;
	readonly maxQuestionBytes: number;
	readonly maxHeaderBytes: number;
	readonly maxDetailBytes: number;
	readonly maxOptionLabelBytes: number;
	readonly maxOptionDescriptionBytes: number;
	readonly maxCustomAnswerBytes: number;
	readonly maxQuestionsPerRequest: number;
	readonly maxOptionsPerQuestion: number;
	readonly maxRequestBytes: number;
	readonly maxResponseBytes: number;
	readonly maxPendingRequests: number;
}

export interface InteractionInput {
	readonly sessionId: string;
	readonly actorId: string;
	readonly inputId: string;
	readonly payload: JsonValue;
}

export interface InteractionOption {
	readonly optionId: string;
	readonly label: string;
	readonly description?: string;
	readonly recommended: boolean;
}

export interface InteractionQuestion {
	readonly questionId: string;
	readonly question: string;
	readonly header?: string;
	readonly detail?: string;
	readonly options: readonly InteractionOption[];
	readonly multiSelect: boolean;
	readonly allowCustomAnswer: boolean;
}

export interface InteractionRequest {
	readonly requestId: string;
	readonly requestFingerprint: string;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly inputId: string;
	readonly toolCallId: string;
	readonly toolName: string;
	readonly questions: readonly InteractionQuestion[];
}

export interface InteractionAnswer {
	readonly questionId: string;
	readonly selectedOptionIds: readonly string[];
	readonly customAnswer?: string;
}

export type InteractionResponse =
	| { readonly status: "cancelled" }
	| { readonly status: "answered"; readonly answers: readonly InteractionAnswer[] };

export interface InteractionBroker {
	ask(request: Readonly<InteractionRequest>, signal: AbortSignal): Promise<unknown>;
}

export interface InteractionToolDetails {
	readonly requestId: string;
	readonly status: InteractionResponse["status"];
}

export interface InteractionServiceOptions {
	readonly broker: InteractionBroker;
	readonly limits: InteractionLimits;
	readonly toolName?: string;
}
