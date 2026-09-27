import { createHash } from "node:crypto";
import type { AgentTool } from "@earendil-works/pi-agent-core";
import { canonicalizeJsonValue, normalizeJsonValue, type RunCoordinate } from "@opengameagent/protocol";
import { Mutex } from "async-mutex";
import { Type } from "typebox";
import {
	InteractionBrokerError,
	InteractionBrokerResponseError,
	InteractionCapacityError,
	InteractionConflictError,
	InteractionValidationError,
} from "./interaction-errors.js";
import type {
	InteractionBroker,
	InteractionInput,
	InteractionLimits,
	InteractionRequest,
	InteractionResponse,
	InteractionServiceOptions,
	InteractionToolDetails,
} from "./interaction-types.js";
import {
	freezeInteractionLimits,
	normalizeInteractionQuestions,
	normalizeInteractionResponse,
	validateInteractionBinding,
	validateInteractionIdentifier,
} from "./interaction-validation.js";

interface ActiveInteraction {
	readonly fingerprint: string;
	readonly response: Promise<InteractionResponse>;
}

export class GameInteractionService {
	readonly limits: Readonly<InteractionLimits>;
	private readonly mutex = new Mutex();
	private readonly active = new Map<string, ActiveInteraction>();
	private readonly broker: InteractionBroker;
	private readonly toolName: string;
	private readonly parameters: ReturnType<typeof interactionParameters>;

	constructor(options: InteractionServiceOptions) {
		this.limits = freezeInteractionLimits(options.limits);
		if (typeof options.broker !== "object" || options.broker === null || typeof options.broker.ask !== "function") {
			throw new InteractionValidationError("broker must provide an ask function.");
		}
		this.broker = options.broker;
		this.toolName = validateInteractionIdentifier(options.toolName ?? "ask_player", this.limits, "toolName");
		this.parameters = interactionParameters(this.limits);
	}

	createTools(
		input: Readonly<InteractionInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
	): readonly AgentTool[] {
		const binding = validateInteractionBinding(input, coordinate, this.limits);
		const tool: AgentTool<typeof this.parameters, InteractionToolDetails> = {
			name: this.toolName,
			label: "Ask player",
			description:
				"Ask one or more related questions only when progress requires a player decision. Choices may include one presentation-only recommendation.",
			parameters: this.parameters,
			executionMode: "sequential",
			execute: async (toolCallId, parameters, executionSignal) => {
				const activeSignal = executionSignal ?? signal;
				activeSignal.throwIfAborted();
				validateInteractionIdentifier(toolCallId, this.limits, "toolCallId");
				const questions = normalizeInteractionQuestions(parameters, this.limits);
				const identity = {
					coordinate: binding.coordinate,
					inputId: binding.inputId,
					toolCallId,
					toolName: this.toolName,
				};
				const requestId = digest(identity);
				const request: InteractionRequest = {
					requestId,
					requestFingerprint: digest({ ...identity, questions }),
					...identity,
					questions,
				};
				const response = await this.request(request, activeSignal);
				activeSignal.throwIfAborted();
				const projected = normalizeJsonValue({ requestId, ...response });
				return {
					content: [{ type: "text", text: canonicalizeJsonValue(projected) }],
					details: { requestId, status: response.status },
				};
			},
		};
		return [tool];
	}

	private async request(request: InteractionRequest, signal: AbortSignal): Promise<InteractionResponse> {
		signal.throwIfAborted();
		const interaction = await this.mutex.runExclusive(() => {
			const existing = this.active.get(request.requestId);
			if (existing) {
				if (existing.fingerprint !== request.requestFingerprint) throw new InteractionConflictError();
				return existing;
			}
			if (this.active.size >= this.limits.maxPendingRequests) throw new InteractionCapacityError();

			let entry: ActiveInteraction;
			const response = Promise.resolve()
				.then(() => this.invokeBroker(request, signal))
				.finally(() =>
					this.mutex.runExclusive(() => {
						if (this.active.get(request.requestId) === entry) this.active.delete(request.requestId);
					}),
				);
			entry = { fingerprint: request.requestFingerprint, response };
			this.active.set(request.requestId, entry);
			return entry;
		});
		return await waitForAbort(interaction.response, signal);
	}

	private async invokeBroker(request: InteractionRequest, signal: AbortSignal): Promise<InteractionResponse> {
		signal.throwIfAborted();
		let response: unknown;
		try {
			response = await waitForAbort(this.broker.ask(cloneRequest(request), signal), signal);
		} catch (error) {
			if (signal.aborted) throw signal.reason ?? error;
			if (error instanceof InteractionBrokerResponseError) throw error;
			throw new InteractionBrokerError({ cause: error });
		}
		return normalizeInteractionResponse(response, request.questions, request.requestId, this.limits);
	}
}

function interactionParameters(limits: Readonly<InteractionLimits>) {
	const option = Type.Object(
		{
			optionId: Type.String({ minLength: 1 }),
			label: Type.String({ minLength: 1 }),
			description: Type.Optional(Type.String({ minLength: 1 })),
			recommended: Type.Optional(Type.Boolean()),
		},
		{ additionalProperties: false },
	);
	const question = Type.Object(
		{
			questionId: Type.String({ minLength: 1 }),
			question: Type.String({ minLength: 1 }),
			header: Type.Optional(Type.String({ minLength: 1 })),
			detail: Type.Optional(Type.String({ minLength: 1 })),
			options: Type.Optional(Type.Array(option, { maxItems: limits.maxOptionsPerQuestion })),
			multiSelect: Type.Optional(Type.Boolean()),
			allowCustomAnswer: Type.Optional(Type.Boolean()),
		},
		{ additionalProperties: false },
	);
	return Type.Object(
		{
			questions: Type.Array(question, { minItems: 1, maxItems: limits.maxQuestionsPerRequest }),
		},
		{ additionalProperties: false },
	);
}

function digest(value: unknown): string {
	return createHash("sha256")
		.update(canonicalizeJsonValue(normalizeJsonValue(value)))
		.digest("hex");
}

function cloneRequest(request: InteractionRequest): InteractionRequest {
	return {
		...request,
		coordinate: { ...request.coordinate },
		questions: request.questions.map((question) => ({
			...question,
			options: question.options.map((option) => ({ ...option })),
		})),
	};
}

function waitForAbort<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
	if (signal.aborted) return Promise.reject(signal.reason);
	return new Promise<T>((resolve, reject) => {
		const aborted = () => {
			cleanup();
			reject(signal.reason);
		};
		const cleanup = () => signal.removeEventListener("abort", aborted);
		signal.addEventListener("abort", aborted, { once: true });
		operation.then(
			(value) => {
				cleanup();
				resolve(value);
			},
			(error: unknown) => {
				cleanup();
				reject(error);
			},
		);
	});
}
