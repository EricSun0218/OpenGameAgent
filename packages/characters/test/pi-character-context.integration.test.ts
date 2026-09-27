import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import {
	DurableTranscript,
	GameAgent,
	type GameModelRequestProvenance,
	InMemoryTranscriptStore,
} from "@opengameagent/agent";
import {
	eventAudience,
	type CanonicalEvent,
	type CanonicalEventData,
	PROTOCOL_VERSION,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { TEST_MODEL, textResponse } from "../../devtools/src/pi-fakes.js";
import { createGameProvenanceAlias } from "../../agent/src/provenance-alias.js";
import { compileGameCharacterPersona, createGameWorldbookContextSource } from "../src/index.js";

describe("native character context with Pi", () => {
	it("places the persona in Pi system instructions and selected worldbook facts in the same provider request", async () => {
		const input = {
			sessionId: "session",
			actorId: "healer-lin",
			inputId: "input",
			payload: { text: "The fever has returned." },
		} as const;
		const persona = compileGameCharacterPersona(
			{
				personaVersion: "1",
				characterId: input.actorId,
				revision: "profile-1",
				name: "Lin",
				identity: ["Village physician"],
				behavior: { must: ["State uncertainty plainly"] },
			},
			{
				sourceId: "game-character-config",
				limits: {
					maxIdentifierBytes: 64,
					maxItemsPerSection: 16,
					maxTextBytes: 1_024,
					maxDefinitionBytes: 2_048,
					maxInstructionsBytes: 4_096,
				},
			},
		);
		const worldbook = createGameWorldbookContextSource({
			sourceId: "game-worldbook",
			worldbook: {
				worldbookVersion: "1",
				worldbookId: "village-world",
				revision: "world-1",
				entries: [
					{
						entryId: "fever",
						revision: "fact-1",
						priority: 1,
						content: "The current fever responds poorly to willow bark.",
						recursive: false,
						activation: {
							kind: "keywords",
							phase: "initial",
							caseSensitive: false,
							primary: ["fever"],
						},
					},
				],
			},
			limits: {
				maxIdentifierBytes: 64,
				maxDefinitionBytes: 4_096,
				maxEntries: 4,
				maxKeywordsPerEntry: 4,
				maxKeywordBytes: 64,
				maxContentBytes: 1_024,
				maxScanSegments: 4,
				maxScanBytes: 1_024,
				maxScanTags: 4,
				maxMatchEvaluations: 16,
				maxRecursionSteps: 2,
				maxActivatedEntries: 4,
				maxContextBytes: 2_048,
				maxContextTokenUpperBound: 2_048,
			},
			resolveScan: async (current) => {
				const payload = current.payload;
				const text =
					typeof payload === "object" && payload !== null && !Array.isArray(payload) && typeof payload.text === "string"
						? payload.text
						: "";
				return {
					scanVersion: "1",
					revision: current.inputId,
					segments: [{ segmentId: "current-input", text }],
					tags: [],
				};
			},
			countTokenUpperBound: async (text) => new TextEncoder().encode(text).byteLength,
		});
		let providerContext: Context | undefined;
		const modelStream: StreamFn = (_model, context) => {
			providerContext = copyContext(context);
			return textResponse("I need to examine the patient before choosing a treatment.");
		};
		let sequence = 0;
		let transcriptRecord = 0;
		const durableTranscript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			newRecordId: () => `character-transcript-${++transcriptRecord}`,
		});
		let requestProvenance: GameModelRequestProvenance | undefined;
		const agent = new GameAgent({
			sessionId: input.sessionId,
			actorId: input.actorId,
			model: TEST_MODEL,
			modelStream,
			systemPrompt: persona.systemPrompt,
			transcript: {
				transcript: durableTranscript,
				restore: await durableTranscript.restore({ sessionId: input.sessionId, actorId: input.actorId }),
			},
			providerProvenance: {
				scopeId: "game-character-test",
				baseInstructions: persona.lineage,
				sink: {
					recordRequest: async (request) => {
						requestProvenance = structuredClone(request);
						return createGameProvenanceAlias("record", "character-request");
					},
					recordResponse: async () => undefined,
				},
			},
			contextSources: [worldbook],
			contextLimits: { maxReferencesPerSnapshot: 4, maxSnapshotBytes: 4_096 },
			emitEvent: async (coordinate, data) => event(++sequence, coordinate, data),
			newId: () => `id-${sequence + 1}`,
			now: () => 1,
		});

		await expect(agent.start(input, "run-1").completion).resolves.toEqual({
			runId: "run-1",
			status: "completed",
			finalOutput: "I need to examine the patient before choosing a treatment.",
		});

		expect(providerContext?.systemPrompt).toBe(persona.systemPrompt);
		const injected = providerContext?.messages.find(
			(message) =>
				message.role === "user" &&
				typeof message.content === "string" &&
				message.content.includes('"kind":"game-reference-context"'),
		);
		expect(injected?.content).toContain('"kind":"game-worldbook"');
		expect(injected?.content).toContain("The current fever responds poorly to willow bark.");
		expect(JSON.stringify(providerContext?.messages)).not.toContain("player-facing");
		expect(requestProvenance?.lineage.baseInstructions.contentFingerprint).toBe(persona.lineage.contentFingerprint);
		expect(requestProvenance?.sources).toHaveLength(1);
	});
});

function copyContext(context: Context): Context {
	return {
		...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
		messages: structuredClone(context.messages),
		...(context.tools === undefined ? {} : { tools: [...context.tools] }),
	};
}

function event(
	sequence: number,
	coordinate: Readonly<RunCoordinate>,
	data: Readonly<CanonicalEventData>,
): CanonicalEvent {
	const ordinal = String(sequence);
	return {
		protocolVersion: PROTOCOL_VERSION,
		eventId: `event-${ordinal}`,
		sequence: ordinal,
		revision: ordinal,
		occurredAt: "2026-08-31T00:00:00.000Z",
		coordinate: structuredClone(coordinate),
		audience: eventAudience(data),
		data: structuredClone(data),
	};
}
