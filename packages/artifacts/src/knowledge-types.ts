import type { NormalizedGameInput } from "@opengameagent/agent";
import type { JsonValue, RunCoordinate } from "@opengameagent/protocol";

export interface KnowledgeLimits {
	readonly maxIdentifierBytes: number;
	readonly maxCounterDigits: number;
	readonly maxSources: number;
	readonly maxSourceDescriptionBytes: number;
	readonly maxToolDescriptionBytes: number;
	readonly maxQueryBytes: number;
	readonly maxItemsPerQuery: number;
	readonly maxTitleBytes: number;
	readonly maxSummaryBytes: number;
	readonly maxUriBytes: number;
	readonly maxMetadataEntries: number;
	readonly maxMetadataKeyBytes: number;
	readonly maxMetadataValueBytes: number;
	readonly maxItemBytes: number;
	readonly maxResultBytes: number;
	readonly maxJsonDepth: number;
	readonly maxConcurrentQueriesPerSource: number;
	readonly maxQueryDurationMs: number;
}

export interface KnowledgeItem {
	readonly itemId: string;
	readonly title: string;
	readonly content: JsonValue;
	readonly summary?: string;
	readonly uri?: string;
	readonly metadata?: Readonly<Record<string, string>>;
}

export interface KnowledgeSourceRequest {
	readonly input: Readonly<NormalizedGameInput>;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly query: JsonValue;
	readonly maxItems: number;
}

export interface KnowledgeSource {
	readonly sourceId: string;
	readonly description: string;
	query(request: Readonly<KnowledgeSourceRequest>, signal: AbortSignal): Promise<unknown>;
}

export interface KnowledgeServiceOptions {
	readonly sources: readonly KnowledgeSource[];
	readonly limits: KnowledgeLimits;
	readonly sourceId?: string;
	readonly queryToolName?: string;
}

export interface KnowledgeToolDetails {
	readonly knowledgeStatus:
		| "ok"
		| "capacity_exceeded"
		| "source_timeout"
		| "source_unavailable"
		| "invalid_source_result"
		| "result_unavailable";
	readonly sourceId?: string;
	readonly itemCount?: number;
}

export interface JsonHttpKnowledgeLimits {
	readonly maxEndpointBytes: number;
	readonly maxRequestBytes: number;
	readonly maxResponseBytes: number;
	readonly maxJsonDepth: number;
	readonly maxHeaders: number;
	readonly maxHeaderNameBytes: number;
	readonly maxHeaderValueBytes: number;
	readonly requestTimeoutMs: number;
}

export interface JsonHttpKnowledgeSourceOptions {
	readonly sourceId: string;
	readonly description: string;
	readonly endpoint: string;
	readonly limits: JsonHttpKnowledgeLimits;
	readonly headers?: (signal: AbortSignal) => Promise<Readonly<Record<string, string>>>;
	readonly projectContext?: (
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
	) => JsonValue | Promise<JsonValue>;
	readonly fetch?: typeof globalThis.fetch;
}
