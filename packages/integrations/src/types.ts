import type { NormalizedGameInput } from "@opengameagent/agent";
import type { RunCoordinate } from "@opengameagent/protocol";

export interface DirectoryGameSkillRoot {
	/** Host-visible identity used in diagnostics. It is never exposed to the model. */
	readonly rootId: string;
	/** Absolute path or a path resolved against the configured working directory. */
	readonly path: string;
}

/** Every value is required host configuration; this package supplies no environment-size guesses. */
export interface DirectoryGameSkillLimits {
	readonly maxRoots: number;
	readonly maxTraversalDepth: number;
	readonly maxDirectories: number;
	readonly maxEntriesPerDirectory: number;
	readonly maxVisitedEntries: number;
	readonly maxReadFiles: number;
	readonly maxSkillFileBytes: number;
	readonly maxIgnoreFileBytes: number;
	readonly maxTotalReadBytes: number;
	readonly maxLoadedSkills: number;
	readonly maxDiagnostics: number;
	readonly maxVisibleSkills: number;
	readonly maxCatalogBytes: number;
	readonly maxAddressedPathBytes: number;
	readonly maxResourcePathBytes: number;
	readonly maxResourceBytes: number;
	readonly maxToolResultBytes: number;
}

export interface DirectoryGameSkillSummary {
	readonly name: string;
	readonly description: string;
	readonly rootId: string;
	readonly modelInvocable: boolean;
}

export type DirectoryGameSkillDiagnosticCode =
	| "file_info_failed"
	| "list_failed"
	| "read_failed"
	| "parse_failed"
	| "invalid_metadata"
	| "flat_skill_ignored"
	| "shadowed_skill";

export interface DirectoryGameSkillDiagnostic {
	readonly code: DirectoryGameSkillDiagnosticCode;
	readonly rootId: string;
}

export interface DirectoryGameSkillCatalogSnapshot {
	readonly revision: string;
	readonly skills: readonly DirectoryGameSkillSummary[];
	readonly diagnostics: readonly DirectoryGameSkillDiagnostic[];
}

export interface DirectoryGameSkillSelectionRequest {
	readonly input: Readonly<NormalizedGameInput>;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly visibleToolNames: readonly string[];
	readonly candidates: readonly DirectoryGameSkillSummary[];
}

export type DirectoryGameSkillSelector = (
	request: DirectoryGameSkillSelectionRequest,
	signal: AbortSignal,
) => readonly string[] | Promise<readonly string[]>;

export interface DirectoryGameSkillSourceOptions {
	readonly cwd: string;
	readonly sourceId: string;
	readonly roots: readonly DirectoryGameSkillRoot[];
	readonly limits: DirectoryGameSkillLimits;
	readonly toolName?: string;
	readonly select?: DirectoryGameSkillSelector;
	/** Stable host-owned revision for an immutable directory snapshot. Equal revisions reuse the last complete scan. */
	readonly catalogRevision?: () => string;
}

export interface ExplicitGameSkillInvocation {
	readonly name: string;
	readonly revision: string;
	readonly text: string;
}
