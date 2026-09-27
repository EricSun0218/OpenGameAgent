import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import {
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
} from "@opengameagent/protocol";
import { GameCharacterPersonaError } from "./errors.js";
import type {
	CompiledGameCharacterPersona,
	CompileGameCharacterPersonaOptions,
	GameCharacterBehavior,
	GameCharacterExample,
	GameCharacterPersonaLimits,
	GameCharacterPersonaV1,
	GameCharacterRelationship,
} from "./types.js";

const PERSONA_KEYS = new Set([
	"personaVersion",
	"characterId",
	"revision",
	"name",
	"identity",
	"goals",
	"behavior",
	"voice",
	"knowledgeBoundaries",
	"relationships",
	"examples",
]);
const BEHAVIOR_KEYS = new Set(["must", "mustNot"]);
const RELATIONSHIP_KEYS = new Set(["targetId", "description", "behavior"]);
const EXAMPLE_KEYS = new Set(["situation", "input", "response"]);
/** Root -> relationships -> relationship -> behavior -> string. */
const PERSONA_SCHEMA_DEPTH = 5;

const INSTRUCTION_HEADER = [
	"GAME_CHARACTER_PERSONA_V1",
	"Portray the character defined by the canonical game-owned persona below.",
	"The confirmed Tool results and current game context are authoritative; never invent or claim an unconfirmed game-state change.",
	"Non-negotiable behavior overrides examples; examples illustrate voice and choices only.",
	"Remain consistent with identity, goals, knowledge boundaries, relationships, and voice unless authoritative game context explicitly changes them.",
].join("\n");

export function compileGameCharacterPersona(
	value: unknown,
	options: Readonly<CompileGameCharacterPersonaOptions>,
): CompiledGameCharacterPersona {
	const configuration = captureConfiguration(options);
	const persona = capturePersona(value, configuration.limits);
	const canonical = canonicalizeJsonValue(persona as unknown as JsonValue);
	const instructionBytes =
		Buffer.byteLength(INSTRUCTION_HEADER) + Buffer.byteLength("\n") + Buffer.byteLength(canonical);
	if (instructionBytes > configuration.limits.maxInstructionsBytes) {
		throw new GameCharacterPersonaError("instructions_too_large");
	}
	const systemPrompt = `${INSTRUCTION_HEADER}\n${canonical}`;
	return Object.freeze({
		persona,
		systemPrompt,
		lineage: Object.freeze({
			sourceId: configuration.sourceId,
			referenceId: persona.characterId,
			revision: persona.revision,
			contentFingerprint: createHash("sha256").update(systemPrompt).digest("hex"),
		}),
	});
}

function captureConfiguration(options: unknown): {
	readonly sourceId: string;
	readonly limits: Readonly<GameCharacterPersonaLimits>;
} {
	try {
		if (!isRecord(options)) throw new TypeError();
		const sourceId = options.sourceId;
		const rawLimits = options.limits;
		if (typeof sourceId !== "string" || !isRecord(rawLimits)) throw new TypeError();
		const limits = Object.freeze({
			maxIdentifierBytes: positiveSafeInteger(rawLimits.maxIdentifierBytes),
			maxItemsPerSection: positiveSafeInteger(rawLimits.maxItemsPerSection),
			maxTextBytes: positiveSafeInteger(rawLimits.maxTextBytes),
			maxDefinitionBytes: positiveSafeInteger(rawLimits.maxDefinitionBytes),
			maxInstructionsBytes: positiveSafeInteger(rawLimits.maxInstructionsBytes),
		});
		assertBoundedIdentifier(sourceId, limits.maxIdentifierBytes);
		return Object.freeze({ sourceId, limits });
	} catch {
		throw new GameCharacterPersonaError("invalid_configuration");
	}
}

function capturePersona(
	rawValue: unknown,
	limits: Readonly<GameCharacterPersonaLimits>,
): Readonly<GameCharacterPersonaV1> {
	try {
		const value = normalizeJsonValueWithin(rawValue, {
			maxDepth: PERSONA_SCHEMA_DEPTH,
			maxBytes: limits.maxDefinitionBytes,
		});
		if (!isRecord(value) || !hasExactKeys(value, PERSONA_KEYS)) throw new TypeError();
		const personaVersion = value.personaVersion;
		const characterId = value.characterId;
		const revision = value.revision;
		const name = value.name;
		const identity = value.identity;
		const goals = value.goals;
		const behavior = value.behavior;
		const voice = value.voice;
		const knowledgeBoundaries = value.knowledgeBoundaries;
		const relationships = value.relationships;
		const examples = value.examples;
		if (personaVersion !== "1") throw new TypeError();
		if (typeof characterId !== "string" || typeof revision !== "string") throw new TypeError();
		assertBoundedIdentifier(characterId, limits.maxIdentifierBytes, true);
		assertBoundedIdentifier(revision, limits.maxIdentifierBytes, true);
		const captured = {
			personaVersion,
			characterId,
			revision,
			name: nonEmptyText(name, limits.maxTextBytes),
			identity: textList(identity, true, limits),
			...(goals === undefined ? {} : { goals: textList(goals, true, limits) }),
			behavior: captureBehavior(behavior, limits),
			...(voice === undefined ? {} : { voice: textList(voice, true, limits) }),
			...(knowledgeBoundaries === undefined
				? {}
				: { knowledgeBoundaries: textList(knowledgeBoundaries, true, limits) }),
			...(relationships === undefined ? {} : { relationships: captureRelationships(relationships, limits) }),
			...(examples === undefined ? {} : { examples: captureExamples(examples, limits) }),
		} satisfies GameCharacterPersonaV1;
		return Object.freeze(captured);
	} catch (error) {
		if (error instanceof GameCharacterPersonaError) throw error;
		if (error instanceof JsonValueLimitError && error.code === "json_too_large") {
			throw new GameCharacterPersonaError("definition_too_large");
		}
		throw new GameCharacterPersonaError("invalid_persona");
	}
}

function captureBehavior(
	value: unknown,
	limits: Readonly<GameCharacterPersonaLimits>,
): Readonly<GameCharacterBehavior> {
	if (!isRecord(value) || !hasExactKeys(value, BEHAVIOR_KEYS)) throw new TypeError();
	const must = value.must;
	const mustNot = value.mustNot;
	return Object.freeze({
		must: textList(must, true, limits),
		...(mustNot === undefined ? {} : { mustNot: textList(mustNot, true, limits) }),
	});
}

function captureRelationships(
	value: unknown,
	limits: Readonly<GameCharacterPersonaLimits>,
): readonly Readonly<GameCharacterRelationship>[] {
	if (!Array.isArray(value)) throw new TypeError();
	const length = value.length;
	if (length === 0) throw new TypeError();
	if (length > limits.maxItemsPerSection) throw new GameCharacterPersonaError("definition_too_large");
	const targetIds = new Set<string>();
	const captured: Readonly<GameCharacterRelationship>[] = [];
	for (let index = 0; index < length; index += 1) {
		const item = value[index];
		if (!isRecord(item) || !hasExactKeys(item, RELATIONSHIP_KEYS)) throw new TypeError();
		const targetId = item.targetId;
		const description = item.description;
		const behavior = item.behavior;
		if (typeof targetId !== "string") throw new TypeError();
		assertBoundedIdentifier(targetId, limits.maxIdentifierBytes, true);
		if (targetIds.has(targetId)) throw new TypeError();
		targetIds.add(targetId);
		captured.push(
			Object.freeze({
				targetId,
				description: nonEmptyText(description, limits.maxTextBytes),
				...(behavior === undefined ? {} : { behavior: textList(behavior, true, limits) }),
			}),
		);
	}
	return Object.freeze(captured);
}

function captureExamples(
	value: unknown,
	limits: Readonly<GameCharacterPersonaLimits>,
): readonly Readonly<GameCharacterExample>[] {
	if (!Array.isArray(value)) throw new TypeError();
	const length = value.length;
	if (length === 0) throw new TypeError();
	if (length > limits.maxItemsPerSection) throw new GameCharacterPersonaError("definition_too_large");
	const captured: Readonly<GameCharacterExample>[] = [];
	for (let index = 0; index < length; index += 1) {
		const item = value[index];
		if (!isRecord(item) || !hasExactKeys(item, EXAMPLE_KEYS)) throw new TypeError();
		const situation = item.situation;
		const input = item.input;
		const response = item.response;
		captured.push(
			Object.freeze({
				...(situation === undefined ? {} : { situation: nonEmptyText(situation, limits.maxTextBytes) }),
				input: nonEmptyText(input, limits.maxTextBytes),
				response: nonEmptyText(response, limits.maxTextBytes),
			}),
		);
	}
	return Object.freeze(captured);
}

function textList(
	value: unknown,
	requireOne: boolean,
	limits: Readonly<GameCharacterPersonaLimits>,
): readonly string[] {
	if (!Array.isArray(value)) throw new TypeError();
	const length = value.length;
	if (requireOne && length === 0) throw new TypeError();
	if (length > limits.maxItemsPerSection) throw new GameCharacterPersonaError("definition_too_large");
	const captured: string[] = [];
	for (let index = 0; index < length; index += 1) {
		captured.push(nonEmptyText(value[index], limits.maxTextBytes));
	}
	return Object.freeze(captured);
}

function nonEmptyText(value: unknown, maxBytes: number): string {
	if (typeof value !== "string" || value.length === 0) throw new TypeError();
	if (Buffer.byteLength(value) > maxBytes) throw new GameCharacterPersonaError("definition_too_large");
	return value;
}

function assertBoundedIdentifier(value: string, maxBytes: number, definition = false): void {
	assertOpaqueIdentifier(value);
	if (Buffer.byteLength(value) > maxBytes) {
		if (definition) throw new GameCharacterPersonaError("definition_too_large");
		throw new TypeError();
	}
}

function positiveSafeInteger(value: unknown): number {
	if (!Number.isSafeInteger(value) || (value as number) < 1) throw new TypeError();
	return value as number;
}

function hasExactKeys(value: Readonly<Record<string, unknown>>, allowed: ReadonlySet<string>): boolean {
	const keys = Object.keys(value);
	return keys.every((key) => allowed.has(key));
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}
