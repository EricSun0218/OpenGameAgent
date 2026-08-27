import { createHash } from "node:crypto";
import type {
	GameInput,
	GameSessionKey,
	GameTool,
	GameToolCall,
	GameToolExecutionContext,
	JsonObject,
} from "@opengameagent/protocol";
import type { GameToolProvider } from "@opengameagent/runtime";

export interface GameInteractionOption {
	id: string;
	label: string;
	description: string;
	recommended: boolean;
}

export interface GameInteractionQuestion {
	id: string;
	prompt: string;
	options: readonly GameInteractionOption[];
	multiSelect: boolean;
	allowCustomAnswer: boolean;
}

export interface GameInteractionRequest {
	requestId: string;
	session: GameSessionKey;
	inputId: string;
	runId: string;
	turn: number;
	toolCallId: string;
	questions: readonly GameInteractionQuestion[];
}

export interface GameInteractionAnswer {
	questionId: string;
	selectedOptionIds: readonly string[];
	customAnswer?: string;
}

export type GameInteractionResponse =
	| { cancelled: true; answers?: never }
	| { cancelled: false; answers: readonly GameInteractionAnswer[] };

export interface GameInteractionBroker {
	prompt(request: GameInteractionRequest, signal: AbortSignal): Promise<GameInteractionResponse>;
}

export type GameInteractionEvent =
	| { type: "interaction.started"; request: GameInteractionRequest }
	| { type: "interaction.completed"; request: GameInteractionRequest; response: GameInteractionResponse };

export interface StructuredGameInteractionOptions {
	broker: GameInteractionBroker;
	toolName?: string;
	maximumRequestCharacters?: number;
	onEvent?: (event: GameInteractionEvent) => Promise<void> | void;
}

const interactionParameters: JsonObject = {
	type: "object",
	properties: {
		questions: {
			type: "array",
			minItems: 1,
			maxItems: 8,
			items: {
				type: "object",
				properties: {
					id: { type: "string", minLength: 1, maxLength: 128 },
					prompt: { type: "string", minLength: 1, maxLength: 8192 },
					multiSelect: { type: "boolean" },
					allowCustomAnswer: { type: "boolean" },
					options: {
						type: "array",
						minItems: 2,
						maxItems: 8,
						items: {
							type: "object",
							properties: {
								id: { type: "string", minLength: 1, maxLength: 128 },
								label: { type: "string", minLength: 1, maxLength: 256 },
								description: { type: "string", minLength: 1, maxLength: 4096 },
								recommended: { type: "boolean" },
							},
							required: ["id", "label", "description"],
							additionalProperties: false,
						},
					},
				},
				required: ["id", "prompt", "options"],
				additionalProperties: false,
			},
		},
	},
	required: ["questions"],
	additionalProperties: false,
};

function isObject(value: unknown): value is Record<string, unknown> {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

function boundedText(value: unknown, name: string, maximum: number): string {
	if (typeof value !== "string" || value.trim().length === 0 || value.length > maximum) {
		throw new TypeError(`${name} must contain 1 to ${maximum} characters.`);
	}
	return value;
}

function optionalBoolean(value: unknown, fallback: boolean, name: string): boolean {
	if (value === undefined) return fallback;
	if (typeof value !== "boolean") throw new TypeError(`${name} must be boolean.`);
	return value;
}

function parseQuestions(argumentsValue: JsonObject, maximumRequestCharacters: number): GameInteractionQuestion[] {
	const serialized = JSON.stringify(argumentsValue);
	if (serialized.length > maximumRequestCharacters)
		throw new RangeError("Structured interaction request is too large.");
	const questionsValue = argumentsValue["questions"];
	if (!Array.isArray(questionsValue) || questionsValue.length < 1 || questionsValue.length > 8) {
		throw new TypeError("Structured interaction requires 1 to 8 questions.");
	}
	const questionIds = new Set<string>();
	return questionsValue.map((candidate, questionIndex) => {
		if (!isObject(candidate)) throw new TypeError(`Question ${questionIndex} must be an object.`);
		const id = boundedText(candidate["id"], `Question ${questionIndex} id`, 128);
		if (questionIds.has(id)) throw new TypeError(`Duplicate question id '${id}'.`);
		questionIds.add(id);
		const optionsValue = candidate["options"];
		if (!Array.isArray(optionsValue) || optionsValue.length < 2 || optionsValue.length > 8) {
			throw new TypeError(`Question '${id}' requires 2 to 8 options.`);
		}
		const optionIds = new Set<string>();
		let recommended = 0;
		const options = optionsValue.map((option, optionIndex): GameInteractionOption => {
			if (!isObject(option)) throw new TypeError(`Option ${optionIndex} for '${id}' must be an object.`);
			const optionId = boundedText(option["id"], `Option ${optionIndex} id`, 128);
			if (optionIds.has(optionId)) throw new TypeError(`Question '${id}' has duplicate option '${optionId}'.`);
			optionIds.add(optionId);
			const isRecommended = optionalBoolean(option["recommended"], false, `Option '${optionId}' recommended`);
			if (isRecommended) recommended += 1;
			return {
				id: optionId,
				label: boundedText(option["label"], `Option '${optionId}' label`, 256),
				description: boundedText(option["description"], `Option '${optionId}' description`, 4096),
				recommended: isRecommended,
			};
		});
		if (recommended > 1) throw new TypeError(`Question '${id}' can recommend at most one option.`);
		return {
			id,
			prompt: boundedText(candidate["prompt"], `Question '${id}' prompt`, 8192),
			options,
			multiSelect: optionalBoolean(candidate["multiSelect"], false, `Question '${id}' multiSelect`),
			allowCustomAnswer: optionalBoolean(candidate["allowCustomAnswer"], true, `Question '${id}' allowCustomAnswer`),
		};
	});
}

function requestId(input: GameInput, context: GameToolExecutionContext, call: GameToolCall): string {
	const values = [
		"interaction-v1",
		input.session.worldId,
		input.session.saveId,
		input.session.timelineId,
		String(input.session.generation),
		input.session.ownerId,
		input.session.sessionId,
		input.session.actorId,
		input.id,
		context.runId,
		String(context.turn),
		String(context.toolCallIndex),
		call.id,
		call.name,
	];
	return `interaction_v1_${createHash("sha256").update(values.join("\n")).digest("hex")}`;
}

function validateResponse(request: GameInteractionRequest, response: GameInteractionResponse): GameInteractionResponse {
	if (!isObject(response) || typeof response.cancelled !== "boolean") {
		throw new TypeError("The interaction broker returned an invalid response.");
	}
	if (response.cancelled) {
		if ("answers" in response && response.answers !== undefined)
			throw new TypeError("A cancelled interaction cannot contain answers.");
		return { cancelled: true };
	}
	if (!Array.isArray(response.answers) || response.answers.length !== request.questions.length) {
		throw new TypeError("The interaction broker must answer every question or cancel.");
	}
	const questions = new Map(request.questions.map((question) => [question.id, question]));
	const answered = new Set<string>();
	const answers = response.answers.map((answer, index): GameInteractionAnswer => {
		if (!isObject(answer)) throw new TypeError(`Interaction answer ${index} must be an object.`);
		const questionId = boundedText(answer["questionId"], `Interaction answer ${index} questionId`, 128);
		const question = questions.get(questionId);
		if (!question) throw new TypeError(`Interaction broker answered unknown question '${questionId}'.`);
		if (answered.has(questionId)) throw new TypeError(`Interaction broker answered '${questionId}' more than once.`);
		answered.add(questionId);
		if (!Array.isArray(answer["selectedOptionIds"]) || answer["selectedOptionIds"].length > 8) {
			throw new TypeError(`Interaction answer '${questionId}' has an invalid selection list.`);
		}
		const selectedOptionIds = answer["selectedOptionIds"].map((value) =>
			boundedText(value, `Interaction answer '${questionId}' option`, 128),
		);
		if (new Set(selectedOptionIds).size !== selectedOptionIds.length) {
			throw new TypeError(`Interaction answer '${questionId}' repeats a selection.`);
		}
		if (!question.multiSelect && selectedOptionIds.length > 1) {
			throw new TypeError(`Interaction question '${questionId}' does not allow multiple selections.`);
		}
		const validIds = new Set(question.options.map((option) => option.id));
		if (selectedOptionIds.some((value) => !validIds.has(value))) {
			throw new TypeError(`Interaction answer '${questionId}' selected an unknown option.`);
		}
		const customAnswer =
			answer["customAnswer"] === undefined
				? undefined
				: boundedText(answer["customAnswer"], `Interaction answer '${questionId}' customAnswer`, 32_768);
		if (!question.allowCustomAnswer && customAnswer !== undefined) {
			throw new TypeError(`Interaction question '${questionId}' does not allow a custom answer.`);
		}
		if (selectedOptionIds.length === 0 && customAnswer === undefined) {
			throw new TypeError(`Interaction question '${questionId}' requires a selection or custom answer.`);
		}
		return { questionId, selectedOptionIds, ...(customAnswer === undefined ? {} : { customAnswer }) };
	});
	return { cancelled: false, answers };
}

export function createStructuredGameInteractionToolProvider(
	options: StructuredGameInteractionOptions,
): GameToolProvider {
	const toolName = options.toolName ?? "ask_player";
	if (!/^[A-Za-z0-9_.:-]{1,128}$/u.test(toolName)) throw new TypeError("Structured interaction toolName is invalid.");
	const maximumRequestCharacters = options.maximumRequestCharacters ?? 64 * 1024;
	if (
		!Number.isSafeInteger(maximumRequestCharacters) ||
		maximumRequestCharacters < 1024 ||
		maximumRequestCharacters > 1024 * 1024
	) {
		throw new RangeError("maximumRequestCharacters must be between 1024 and 1048576.");
	}
	return {
		async provide(input) {
			const tool: GameTool = {
				definition: {
					name: toolName,
					label: "Ask player",
					description:
						"Ask one or more bounded questions only when progress requires a player decision. Explain each option and recommend at most one option per question.",
					parameters: interactionParameters,
					executionMode: "sequential",
					risk: "medium",
				},
				async execute(call, context) {
					try {
						const request: GameInteractionRequest = {
							requestId: requestId(input, context, call),
							session: structuredClone(input.session),
							inputId: input.id,
							runId: context.runId,
							turn: context.turn,
							toolCallId: call.id,
							questions: parseQuestions(call.arguments, maximumRequestCharacters),
						};
						await options.onEvent?.({ type: "interaction.started", request: structuredClone(request) });
						const response = validateResponse(request, await options.broker.prompt(request, context.signal));
						await options.onEvent?.({
							type: "interaction.completed",
							request: structuredClone(request),
							response: structuredClone(response),
						});
						const responseValue: JsonObject = response.cancelled
							? { requestId: request.requestId, cancelled: true }
							: {
									requestId: request.requestId,
									cancelled: false,
									answers: response.answers.map((answer) => ({
										questionId: answer.questionId,
										selectedOptionIds: [...answer.selectedOptionIds],
										...(answer.customAnswer === undefined ? {} : { customAnswer: answer.customAnswer }),
									})),
								};
						return { content: [{ type: "json", value: responseValue }] };
					} catch (error) {
						if (context.signal.aborted) throw context.signal.reason ?? error;
						return {
							content: [
								{
									type: "json",
									value: {
										error: "interaction_failed",
										message: "Structured interaction could not be completed.",
									},
								},
							],
							isError: true,
						};
					}
				},
			};
			return [tool];
		},
	};
}
