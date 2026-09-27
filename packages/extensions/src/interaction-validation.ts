import {
	assertEventStreamCoordinate,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	normalizeJsonValue,
	type JsonValue,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { InteractionBrokerResponseError, InteractionValidationError } from "./interaction-errors.js";
import type {
	InteractionAnswer,
	InteractionInput,
	InteractionLimits,
	InteractionQuestion,
	InteractionResponse,
} from "./interaction-types.js";

const encoder = new TextEncoder();
const limitNames = [
	"maxIdentifierBytes",
	"maxCounterDigits",
	"maxQuestionBytes",
	"maxHeaderBytes",
	"maxDetailBytes",
	"maxOptionLabelBytes",
	"maxOptionDescriptionBytes",
	"maxCustomAnswerBytes",
	"maxQuestionsPerRequest",
	"maxOptionsPerQuestion",
	"maxRequestBytes",
	"maxResponseBytes",
	"maxPendingRequests",
] as const satisfies readonly (keyof InteractionLimits)[];

export function freezeInteractionLimits(limits: InteractionLimits): Readonly<InteractionLimits> {
	const copy = { ...limits };
	for (const name of limitNames) {
		if (!Number.isSafeInteger(copy[name]) || copy[name] <= 0) {
			throw new InteractionValidationError(`${name} must be a positive safe integer.`);
		}
	}
	const minimumRequest = canonicalByteLength([
		{
			questionId: "q",
			question: "q",
			options: [],
			multiSelect: false,
			allowCustomAnswer: true,
		},
	]);
	if (copy.maxRequestBytes < minimumRequest) {
		throw new InteractionValidationError("maxRequestBytes cannot represent the minimum valid interaction request.");
	}
	const minimumResponse = canonicalByteLength({ requestId: "0".repeat(64), status: "cancelled" });
	if (copy.maxResponseBytes < minimumResponse) {
		throw new InteractionValidationError("maxResponseBytes cannot represent the minimum valid interaction response.");
	}
	return Object.freeze(copy);
}

export function validateInteractionBinding(
	input: Readonly<InteractionInput>,
	coordinate: Readonly<RunCoordinate>,
	limits: Readonly<InteractionLimits>,
): { inputId: string; coordinate: RunCoordinate } {
	assertEventStreamCoordinate({ sessionId: input.sessionId, actorId: input.actorId }, coordinate);
	validateIdentifier(input.sessionId, limits.maxIdentifierBytes, "sessionId");
	validateIdentifier(input.actorId, limits.maxIdentifierBytes, "actorId");
	validateIdentifier(input.inputId, limits.maxIdentifierBytes, "inputId");
	validateIdentifier(coordinate.runId, limits.maxIdentifierBytes, "runId");
	if (coordinate.turn.length > limits.maxCounterDigits) {
		throw new InteractionValidationError("turn exceeds the configured digit limit.");
	}
	return { inputId: input.inputId, coordinate: { ...coordinate } };
}

export function validateInteractionIdentifier(
	value: string,
	limits: Pick<InteractionLimits, "maxIdentifierBytes">,
	name: string,
): string {
	validateIdentifier(value, limits.maxIdentifierBytes, name);
	return value;
}

export function normalizeInteractionQuestions(
	value: unknown,
	limits: Readonly<InteractionLimits>,
): readonly InteractionQuestion[] {
	let normalized: JsonValue;
	try {
		normalized = normalizeJsonValue(value);
	} catch (error) {
		throw new InteractionValidationError("Interaction arguments must be strict JSON.", { cause: error });
	}
	if (!isRecord(normalized)) throw new InteractionValidationError("Interaction arguments must be an object.");
	assertOnlyKeys(normalized, ["questions"], "Interaction arguments");
	const source = normalized.questions;
	if (!Array.isArray(source) || source.length === 0 || source.length > limits.maxQuestionsPerRequest) {
		throw new InteractionValidationError("Interaction questions exceed the configured request count.");
	}

	const questionIds = new Set<string>();
	const questions = source.map((candidate, questionIndex): InteractionQuestion => {
		if (!isRecord(candidate)) throw new InteractionValidationError(`Question ${questionIndex} must be an object.`);
		assertOnlyKeys(
			candidate,
			["questionId", "question", "header", "detail", "options", "multiSelect", "allowCustomAnswer"],
			`Question ${questionIndex}`,
		);
		const questionId = text(candidate.questionId, limits.maxIdentifierBytes, `Question ${questionIndex} id`, true);
		if (questionIds.has(questionId)) throw new InteractionValidationError("Interaction question ids must be unique.");
		questionIds.add(questionId);
		const question = text(candidate.question, limits.maxQuestionBytes, `Question ${questionId} text`);
		const header = optionalText(candidate.header, limits.maxHeaderBytes, `Question ${questionId} header`);
		const detail = optionalText(candidate.detail, limits.maxDetailBytes, `Question ${questionId} detail`);
		const multiSelect = optionalBoolean(candidate.multiSelect, false, `Question ${questionId} multiSelect`);
		const allowCustomAnswer = optionalBoolean(
			candidate.allowCustomAnswer,
			true,
			`Question ${questionId} allowCustomAnswer`,
		);
		const optionSource = candidate.options ?? [];
		if (!Array.isArray(optionSource) || optionSource.length > limits.maxOptionsPerQuestion) {
			throw new InteractionValidationError(`Question ${questionId} exceeds the configured option count.`);
		}
		if (optionSource.length === 0 && !allowCustomAnswer) {
			throw new InteractionValidationError(`Question ${questionId} has no available answer mode.`);
		}

		const optionIds = new Set<string>();
		let recommendations = 0;
		const options = optionSource.map((option, optionIndex) => {
			if (!isRecord(option)) {
				throw new InteractionValidationError(`Option ${optionIndex} for question ${questionId} must be an object.`);
			}
			assertOnlyKeys(option, ["optionId", "label", "description", "recommended"], `Option ${optionIndex}`);
			const optionId = text(option.optionId, limits.maxIdentifierBytes, `Option ${optionIndex} id`, true);
			if (optionIds.has(optionId)) throw new InteractionValidationError("Interaction option ids must be unique.");
			optionIds.add(optionId);
			const recommended = optionalBoolean(option.recommended, false, `Option ${optionId} recommended`);
			if (recommended) recommendations += 1;
			return {
				optionId,
				label: text(option.label, limits.maxOptionLabelBytes, `Option ${optionId} label`),
				...(option.description === undefined
					? {}
					: {
							description: text(option.description, limits.maxOptionDescriptionBytes, `Option ${optionId} description`),
						}),
				recommended,
			};
		});
		if (recommendations > 1) {
			throw new InteractionValidationError(`Question ${questionId} may recommend at most one option.`);
		}

		return {
			questionId,
			question,
			...(header === undefined ? {} : { header }),
			...(detail === undefined ? {} : { detail }),
			options,
			multiSelect,
			allowCustomAnswer,
		};
	});

	assertCanonicalBytes(questions, limits.maxRequestBytes, "Interaction request");
	return questions;
}

export function normalizeInteractionResponse(
	value: unknown,
	questions: readonly InteractionQuestion[],
	requestId: string,
	limits: Readonly<InteractionLimits>,
): InteractionResponse {
	try {
		const normalized = normalizeJsonValue(value);
		if (!isRecord(normalized)) throw new TypeError();
		assertExactKeys(normalized, normalized.status === "cancelled" ? ["status"] : ["status", "answers"]);
		if (normalized.status === "cancelled") {
			const result = { status: "cancelled" } as const;
			assertCanonicalBytes({ requestId, ...result }, limits.maxResponseBytes, "Interaction response");
			return result;
		}
		if (
			normalized.status !== "answered" ||
			!Array.isArray(normalized.answers) ||
			normalized.answers.length !== questions.length
		) {
			throw new TypeError();
		}

		const supplied = new Map<string, InteractionAnswer>();
		for (const candidate of normalized.answers) {
			if (!isRecord(candidate)) throw new TypeError();
			assertExactKeys(
				candidate,
				candidate.customAnswer === undefined
					? ["questionId", "selectedOptionIds"]
					: ["questionId", "selectedOptionIds", "customAnswer"],
			);
			const questionId = responseIdentifier(candidate.questionId, limits.maxIdentifierBytes);
			if (supplied.has(questionId) || !Array.isArray(candidate.selectedOptionIds)) throw new TypeError();
			const selectedOptionIds = candidate.selectedOptionIds.map((entry) =>
				responseIdentifier(entry, limits.maxIdentifierBytes),
			);
			if (new Set(selectedOptionIds).size !== selectedOptionIds.length) throw new TypeError();
			const customAnswer = responseOptionalText(candidate.customAnswer, limits.maxCustomAnswerBytes);
			supplied.set(questionId, {
				questionId,
				selectedOptionIds,
				...(customAnswer === undefined ? {} : { customAnswer }),
			});
		}

		const answers = questions.map((question): InteractionAnswer => {
			const answer = supplied.get(question.questionId);
			if (!answer) throw new TypeError();
			const validOptions = new Set(question.options.map((option) => option.optionId));
			if (answer.selectedOptionIds.some((optionId) => !validOptions.has(optionId))) throw new TypeError();
			if (!question.multiSelect && answer.selectedOptionIds.length > 1) throw new TypeError();
			if (answer.customAnswer !== undefined && !question.allowCustomAnswer) throw new TypeError();
			if (!question.multiSelect && answer.customAnswer !== undefined && answer.selectedOptionIds.length > 0) {
				throw new TypeError();
			}
			if (answer.selectedOptionIds.length === 0 && answer.customAnswer === undefined) throw new TypeError();
			return answer;
		});
		if (supplied.size !== questions.length) throw new TypeError();
		const result = { status: "answered", answers } as const;
		assertCanonicalBytes({ requestId, ...result }, limits.maxResponseBytes, "Interaction response");
		return result;
	} catch (error) {
		if (error instanceof InteractionBrokerResponseError) throw error;
		throw new InteractionBrokerResponseError({ cause: error });
	}
}

function validateIdentifier(value: unknown, maximumBytes: number, name: string): asserts value is string {
	if (typeof value !== "string") throw new InteractionValidationError(`${name} must be an identifier.`);
	try {
		assertOpaqueIdentifier(value);
	} catch (error) {
		throw new InteractionValidationError(`${name} must be an identifier.`, { cause: error });
	}
	if (encoder.encode(value).byteLength > maximumBytes) {
		throw new InteractionValidationError(`${name} exceeds the configured identifier byte limit.`);
	}
}

function text(value: unknown, maximumBytes: number, name: string, identifier = false): string {
	if (identifier) {
		validateIdentifier(value, maximumBytes, name);
		return value;
	}
	if (typeof value !== "string" || value.trim().length === 0) {
		throw new InteractionValidationError(`${name} must be non-empty text.`);
	}
	if (encoder.encode(value).byteLength > maximumBytes) {
		throw new InteractionValidationError(`${name} exceeds its configured byte limit.`);
	}
	return value;
}

function optionalText(value: unknown, maximumBytes: number, name: string): string | undefined {
	return value === undefined ? undefined : text(value, maximumBytes, name);
}

function optionalBoolean(value: unknown, fallback: boolean, name: string): boolean {
	if (value === undefined) return fallback;
	if (typeof value !== "boolean") throw new InteractionValidationError(`${name} must be boolean.`);
	return value;
}

function responseIdentifier(value: unknown, maximumBytes: number): string {
	if (typeof value !== "string") throw new TypeError();
	assertOpaqueIdentifier(value);
	if (encoder.encode(value).byteLength > maximumBytes) throw new TypeError();
	return value;
}

function responseOptionalText(value: unknown, maximumBytes: number): string | undefined {
	if (value === undefined) return undefined;
	if (typeof value !== "string" || value.trim().length === 0 || encoder.encode(value).byteLength > maximumBytes) {
		throw new TypeError();
	}
	return value;
}

function assertCanonicalBytes(value: unknown, maximumBytes: number, name: string): void {
	if (canonicalByteLength(value) > maximumBytes) {
		if (name === "Interaction response") throw new InteractionBrokerResponseError();
		throw new InteractionValidationError(`${name} exceeds its configured canonical byte limit.`);
	}
}

function canonicalByteLength(value: unknown): number {
	return encoder.encode(canonicalizeJsonValue(normalizeJsonValue(value))).byteLength;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function assertOnlyKeys(value: Record<string, unknown>, allowed: readonly string[], name: string): void {
	const allowedSet = new Set(allowed);
	if (Object.keys(value).some((key) => !allowedSet.has(key))) {
		throw new InteractionValidationError(`${name} contains an unknown field.`);
	}
}

function assertExactKeys(value: Record<string, unknown>, expected: readonly string[]): void {
	const actual = Object.keys(value).sort();
	const sortedExpected = [...expected].sort();
	if (actual.length !== sortedExpected.length || actual.some((key, index) => key !== sortedExpected[index])) {
		throw new TypeError();
	}
}
