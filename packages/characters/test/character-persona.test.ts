import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import { compileGameCharacterPersona, GameCharacterPersonaError, type GameCharacterPersonaV1 } from "../src/index.js";

const PERSONA: GameCharacterPersonaV1 = {
	personaVersion: "1",
	characterId: "healer-lin",
	revision: "profile-7",
	name: "Lin",
	identity: ["Village physician", "Protects patients without pretending certainty"],
	goals: ["Keep the wounded alive", "Discover the source of the blight"],
	behavior: {
		must: ["Ask before treating a conscious patient", "State uncertainty plainly"],
		mustNot: ["Claim a cure succeeded without confirmed game state"],
	},
	voice: ["Concise", "Warm but unsentimental"],
	knowledgeBoundaries: ["Does not know events outside witnessed or supplied game context"],
	relationships: [
		{
			targetId: "captain-ren",
			description: "Distrusts Ren's methods but will protect his soldiers",
			behavior: ["Challenge reckless orders directly"],
		},
	],
	examples: [
		{
			situation: "A patient demands certainty",
			input: "Will this medicine save him?",
			response: "It may slow the fever. I will not promise what I cannot know.",
		},
	],
};

const LIMITS = {
	maxIdentifierBytes: 64,
	maxItemsPerSection: 32,
	maxTextBytes: 1_024,
	maxDefinitionBytes: 4_096,
	maxInstructionsBytes: 8_192,
};

describe("compileGameCharacterPersona", () => {
	it("compiles one immutable developer-authored persona into authoritative Pi system instructions and lineage", () => {
		const compiled = compileGameCharacterPersona(PERSONA, { sourceId: "game-character-config", limits: LIMITS });

		expect(compiled.persona).toEqual(PERSONA);
		expect(Object.isFrozen(compiled)).toBe(true);
		expect(Object.isFrozen(compiled.persona)).toBe(true);
		expect(Object.isFrozen(compiled.persona.behavior.must)).toBe(true);
		expect(compiled.systemPrompt).toContain("GAME_CHARACTER_PERSONA_V1");
		expect(compiled.systemPrompt).toContain("confirmed Tool results and current game context are authoritative");
		expect(compiled.systemPrompt).toContain("Non-negotiable behavior overrides examples");
		expect(compiled.systemPrompt.indexOf('"behavior"')).toBeLessThan(compiled.systemPrompt.indexOf('"examples"'));
		expect(compiled.lineage).toEqual({
			sourceId: "game-character-config",
			referenceId: "healer-lin",
			revision: "profile-7",
			contentFingerprint: createHash("sha256").update(compiled.systemPrompt).digest("hex"),
		});
	});

	it("detaches source arrays before compiling instructions", () => {
		const must = ["Keep promises"];
		const persona = {
			...PERSONA,
			behavior: {
				mustNot: PERSONA.behavior.mustNot,
				must,
			},
		};

		const compiled = compileGameCharacterPersona(persona, { sourceId: "game-character-config", limits: LIMITS });
		must[0] = "mutated";

		expect(compiled.persona.behavior.must).toEqual(["Keep promises"]);
		expect(compiled.systemPrompt).not.toContain("mutated");
	});

	it("captures array inputs without invoking host-defined iteration or mapping hooks", () => {
		const identity = ["Village physician"];
		Object.defineProperties(identity, {
			[Symbol.iterator]: {
				value: () => {
					throw new Error("host iterator must not run");
				},
			},
			map: {
				value: () => {
					throw new Error("host map must not run");
				},
			},
		});

		const compiled = compileGameCharacterPersona({ ...PERSONA, identity }, { sourceId: "config", limits: LIMITS });

		expect(compiled.persona.identity).toEqual(["Village physician"]);
	});

	it("rejects inherited definitions and Proxy arrays instead of treating them as snapshots", () => {
		const inherited = Object.create(PERSONA) as GameCharacterPersonaV1;
		const accessor = { ...PERSONA };
		Object.defineProperty(accessor, "name", { enumerable: true, get: () => "Lin" });
		let lengthReads = 0;
		const unstableIdentity = new Proxy(["Village physician"], {
			get(target, property, receiver) {
				if (property === "length") {
					lengthReads += 1;
					return lengthReads === 1 ? 1 : 0;
				}
				return Reflect.get(target, property, receiver);
			},
		});

		for (const persona of [inherited, accessor, { ...PERSONA, identity: unstableIdentity }]) {
			expect(() => compileGameCharacterPersona(persona, { sourceId: "config", limits: LIMITS })).toThrow(
				Object.assign(new GameCharacterPersonaError("invalid_persona"), { name: "GameCharacterPersonaError" }),
			);
		}
	});

	it("rejects a structurally oversized persona before reading array elements", () => {
		const identity = new Array<string>(LIMITS.maxDefinitionBytes + 1);
		Object.defineProperty(identity, 0, {
			get() {
				throw new Error("persona item must not be read after structural budget failure");
			},
		});

		expect(() => compileGameCharacterPersona({ ...PERSONA, identity }, { sourceId: "config", limits: LIMITS })).toThrow(
			Object.assign(new GameCharacterPersonaError("definition_too_large"), { name: "GameCharacterPersonaError" }),
		);
	});

	it("captures each host limit once and distinguishes definition from instruction capacity", () => {
		const reads = { identifiers: 0, items: 0, text: 0, definition: 0, instructions: 0 };
		const limits = {
			get maxIdentifierBytes() {
				reads.identifiers += 1;
				return LIMITS.maxIdentifierBytes;
			},
			get maxItemsPerSection() {
				reads.items += 1;
				return LIMITS.maxItemsPerSection;
			},
			get maxTextBytes() {
				reads.text += 1;
				return LIMITS.maxTextBytes;
			},
			get maxDefinitionBytes() {
				reads.definition += 1;
				return 1;
			},
			get maxInstructionsBytes() {
				reads.instructions += 1;
				return LIMITS.maxInstructionsBytes;
			},
		};

		expect(() => compileGameCharacterPersona(PERSONA, { sourceId: "config", limits })).toThrow(
			Object.assign(new GameCharacterPersonaError("definition_too_large"), { name: "GameCharacterPersonaError" }),
		);
		expect(reads).toEqual({ identifiers: 1, items: 1, text: 1, definition: 1, instructions: 1 });

		const roomyDefinition = { ...LIMITS, maxInstructionsBytes: 1 };
		expect(() => compileGameCharacterPersona(PERSONA, { sourceId: "config", limits: roomyDefinition })).toThrow(
			Object.assign(new GameCharacterPersonaError("instructions_too_large"), { name: "GameCharacterPersonaError" }),
		);
	});

	it.each([
		["per-section item count", { ...PERSONA, identity: ["one", "two"] }, { maxItemsPerSection: 1 }],
		["individual text bytes", { ...PERSONA, name: "Lin" }, { maxTextBytes: 2 }],
	] as const)("preflights the host %s budget before canonical compilation", (_name, persona, override) => {
		expect(() =>
			compileGameCharacterPersona(persona, {
				sourceId: "config",
				limits: { ...LIMITS, ...override },
			}),
		).toThrow(
			Object.assign(new GameCharacterPersonaError("definition_too_large"), { name: "GameCharacterPersonaError" }),
		);
	});

	it("classifies an overlong persona identifier as definition capacity", () => {
		expect(() =>
			compileGameCharacterPersona(
				{ ...PERSONA, characterId: "x".repeat(LIMITS.maxIdentifierBytes + 1) },
				{
					sourceId: "config",
					limits: LIMITS,
				},
			),
		).toThrow(
			Object.assign(new GameCharacterPersonaError("definition_too_large"), { name: "GameCharacterPersonaError" }),
		);
	});

	it.each([
		["unknown version", { ...PERSONA, personaVersion: "2" }],
		["unknown field", { ...PERSONA, prompt: "bypass" }],
		["empty identity", { ...PERSONA, identity: [] }],
		["empty rule", { ...PERSONA, behavior: { ...PERSONA.behavior, must: [""] } }],
		[
			"duplicate relationship target",
			{ ...PERSONA, relationships: [PERSONA.relationships?.[0], PERSONA.relationships?.[0]] },
		],
		["control character identifier", { ...PERSONA, characterId: "actor\0hidden" }],
	] as const)("rejects an invalid native persona: %s", (_name, persona) => {
		expect(() =>
			compileGameCharacterPersona(persona as GameCharacterPersonaV1, {
				sourceId: "game-character-config",
				limits: LIMITS,
			}),
		).toThrow(Object.assign(new GameCharacterPersonaError("invalid_persona"), { name: "GameCharacterPersonaError" }));
	});

	it("rejects malformed or overlong host-owned lineage configuration", () => {
		expect(() => compileGameCharacterPersona(PERSONA, undefined as never)).toThrow(
			Object.assign(new GameCharacterPersonaError("invalid_configuration"), { name: "GameCharacterPersonaError" }),
		);
		expect(() =>
			compileGameCharacterPersona(PERSONA, {
				sourceId: "source-too-long",
				limits: { ...LIMITS, maxIdentifierBytes: 2 },
			}),
		).toThrow(
			Object.assign(new GameCharacterPersonaError("invalid_configuration"), { name: "GameCharacterPersonaError" }),
		);
	});
});
