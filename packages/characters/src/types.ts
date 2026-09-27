import type {
	GameContextContribution,
	GameContextSource,
	GameModelContextLineageReference,
	NormalizedGameInput,
} from "@opengameagent/agent";
import type { RunCoordinate } from "@opengameagent/protocol";

export interface GameCharacterPersonaLimits {
	readonly maxIdentifierBytes: number;
	readonly maxItemsPerSection: number;
	readonly maxTextBytes: number;
	readonly maxDefinitionBytes: number;
	readonly maxInstructionsBytes: number;
}

export interface GameCharacterBehavior {
	readonly must: readonly string[];
	readonly mustNot?: readonly string[];
}

export interface GameCharacterRelationship {
	readonly targetId: string;
	readonly description: string;
	readonly behavior?: readonly string[];
}

export interface GameCharacterExample {
	readonly situation?: string;
	readonly input: string;
	readonly response: string;
}

/** Native project-owned persona data. This is not an external-content interchange format. */
export interface GameCharacterPersonaV1 {
	readonly personaVersion: "1";
	readonly characterId: string;
	readonly revision: string;
	readonly name: string;
	readonly identity: readonly string[];
	readonly goals?: readonly string[];
	readonly behavior: Readonly<GameCharacterBehavior>;
	readonly voice?: readonly string[];
	readonly knowledgeBoundaries?: readonly string[];
	readonly relationships?: readonly Readonly<GameCharacterRelationship>[];
	readonly examples?: readonly Readonly<GameCharacterExample>[];
}

export interface CompileGameCharacterPersonaOptions {
	/** Host-owned provenance authority for this checked-in or otherwise immutable game definition. */
	readonly sourceId: string;
	readonly limits: Readonly<GameCharacterPersonaLimits>;
}

export interface CompiledGameCharacterPersona {
	readonly persona: Readonly<GameCharacterPersonaV1>;
	readonly systemPrompt: string;
	readonly lineage: Readonly<GameModelContextLineageReference>;
}

export type GameWorldbookActivationPhase = "initial" | "recursive" | "any";
export type GameWorldbookSecondaryMode = "any" | "all" | "none";

export interface GameWorldbookSecondaryKeywords {
	readonly mode: GameWorldbookSecondaryMode;
	readonly keywords: readonly string[];
}

export type GameWorldbookActivation =
	| { readonly kind: "constant" }
	| {
			readonly kind: "keywords";
			readonly phase: GameWorldbookActivationPhase;
			readonly caseSensitive: boolean;
			readonly primary: readonly string[];
			readonly secondary?: Readonly<GameWorldbookSecondaryKeywords>;
	  };

export interface GameWorldbookEntryScope {
	readonly actorIds?: readonly string[];
	readonly requiredTags?: readonly string[];
	readonly excludedTags?: readonly string[];
}

export interface GameWorldbookEntryV1 {
	readonly entryId: string;
	readonly revision: string;
	/** Higher values are considered first; equal values retain definition order. */
	readonly priority: number;
	readonly content: string;
	/** Whether this entry's content may activate recursive-phase entries. */
	readonly recursive: boolean;
	readonly activation: Readonly<GameWorldbookActivation>;
	readonly scope?: Readonly<GameWorldbookEntryScope>;
}

/** Native game-owned world context. This is not an external-content interchange format. */
export interface GameWorldbookV1 {
	readonly worldbookVersion: "1";
	readonly worldbookId: string;
	readonly revision: string;
	readonly entries: readonly Readonly<GameWorldbookEntryV1>[];
}

export interface GameWorldbookScanSegment {
	readonly segmentId: string;
	readonly text: string;
}

export interface GameWorldbookScanSnapshot {
	readonly scanVersion: "1";
	readonly revision: string;
	readonly segments: readonly Readonly<GameWorldbookScanSegment>[];
	readonly tags: readonly string[];
}

/** All values are mandatory host configuration; no workload default is supplied. */
export interface GameWorldbookLimits {
	/** Must be at least 64 bytes so the generated lowercase SHA-256 context revision is representable. */
	readonly maxIdentifierBytes: number;
	readonly maxDefinitionBytes: number;
	readonly maxEntries: number;
	readonly maxKeywordsPerEntry: number;
	readonly maxKeywordBytes: number;
	readonly maxContentBytes: number;
	readonly maxScanSegments: number;
	readonly maxScanBytes: number;
	readonly maxScanTags: number;
	/** One evaluation is one keyword-versus-segment inclusion check; constant activation is not charged. */
	readonly maxMatchEvaluations: number;
	readonly maxRecursionSteps: number;
	readonly maxActivatedEntries: number;
	readonly maxContextBytes: number;
	readonly maxContextTokenUpperBound: number;
}

export type GameWorldbookActivationReason =
	| { readonly kind: "constant" }
	| {
			readonly kind: "keyword";
			readonly phase: "initial" | "recursive";
			readonly primaryKeyword: string;
			readonly segmentId: string;
	  };

export interface ActivatedGameWorldbookEntry {
	readonly entryId: string;
	readonly revision: string;
	readonly content: string;
	readonly reason: Readonly<GameWorldbookActivationReason>;
}

export interface OmittedGameWorldbookEntry {
	readonly entryId: string;
	readonly reason: "byte_budget" | "token_budget";
}

export interface GameWorldbookSelection {
	readonly activated: readonly Readonly<ActivatedGameWorldbookEntry>[];
	readonly selected: readonly Readonly<ActivatedGameWorldbookEntry>[];
	readonly omitted: readonly Readonly<OmittedGameWorldbookEntry>[];
	readonly contextBytes: number;
	/** Host-attested upper bound across every provider/model route eligible for this run. */
	readonly contextTokenUpperBound: number;
	readonly contribution: Readonly<GameContextContribution>;
}

export type GameWorldbookScanResolver = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
) => GameWorldbookScanSnapshot | Promise<GameWorldbookScanSnapshot>;

export type GameWorldbookTokenUpperBoundCounter = (text: string, signal: AbortSignal) => number | Promise<number>;

export interface GameWorldbookContextSourceOptions {
	readonly sourceId: string;
	readonly worldbook: Readonly<GameWorldbookV1>;
	readonly limits: Readonly<GameWorldbookLimits>;
	readonly resolveScan: GameWorldbookScanResolver;
	/**
	 * Returns a conservative token-count upper bound for the exact canonical body across every provider/model route
	 * the Agent's provider-attempt lifecycle may select. A single-route host may return that model's exact count.
	 */
	readonly countTokenUpperBound: GameWorldbookTokenUpperBoundCounter;
}

export interface GameWorldbookContextSource extends GameContextSource {
	readonly provenanceKind: "context";
	select(
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
	): Promise<GameWorldbookSelection>;
}
