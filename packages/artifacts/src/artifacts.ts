import { createHash } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type {
	GameInput,
	GameInputContent,
	GameMoment,
	GameSessionKey,
	GameTool,
	GameToolResult,
	JsonObject,
	JsonValue,
} from "@opengameagent/protocol";
import type { GameToolExecutionMiddleware, GameToolProvider } from "@opengameagent/runtime";

export interface GameArtifact {
	id: string;
	session: GameSessionKey;
	mediaType: string;
	content: string;
	moment: GameMoment;
	metadata?: Readonly<Record<string, string>>;
	createdAt: number;
}

export interface GameArtifactSlice {
	id: string;
	mediaType: string;
	offset: number;
	content: string;
	totalCharacters: number;
	truncated: boolean;
	metadata?: Readonly<Record<string, string>>;
}

export interface GameArtifactStore {
	put(artifact: GameArtifact, signal?: AbortSignal): Promise<boolean>;
	read(
		session: GameSessionKey,
		id: string,
		offset: number,
		maximumCharacters: number,
		signal?: AbortSignal,
	): Promise<GameArtifactSlice | undefined>;
}

export interface SqliteGameArtifactStoreOptions {
	capacity?: number;
	maximumArtifactCharacters?: number;
	maximumMetadataBytes?: number;
}

interface ArtifactRow {
	artifact_id: string;
	media_type: string;
	content: string;
	moment_json: string;
	metadata_json: string;
	created_at: number;
}

const sessionWhere =
	"world_id=? AND save_id=? AND timeline_id=? AND generation=? AND owner_id=? AND session_id=? AND actor_id=?";

function sessionValues(session: GameSessionKey): readonly (string | number)[] {
	return [
		session.worldId,
		session.saveId,
		session.timelineId,
		session.generation,
		session.ownerId,
		session.sessionId,
		session.actorId,
	];
}

function validateSession(session: GameSessionKey): void {
	for (const [name, value] of Object.entries(session)) {
		if (name === "generation") {
			if (!Number.isSafeInteger(value) || (value as number) < 0) throw new TypeError("Session generation is invalid.");
		} else validateText(value as string, `Session ${name}`, 1024);
	}
}

function sameSession(left: GameSessionKey, right: GameSessionKey): boolean {
	return (
		left.worldId === right.worldId &&
		left.saveId === right.saveId &&
		left.timelineId === right.timelineId &&
		left.generation === right.generation &&
		left.ownerId === right.ownerId &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId
	);
}

function validateText(value: string, name: string, maximum: number): void {
	if (!value || value.length > maximum) throw new TypeError(`${name} must be bounded and non-empty.`);
	for (const character of value) {
		const code = character.codePointAt(0) ?? 0;
		if (code < 32 || code === 127) throw new TypeError(`${name} contains a control character.`);
	}
}

function normalizeMetadata(
	metadata: Readonly<Record<string, string>> | undefined,
	maximumBytes: number,
): Readonly<Record<string, string>> | undefined {
	if (metadata === undefined) return undefined;
	const entries = Object.entries(metadata);
	if (entries.length > 128) throw new RangeError("Artifact metadata has too many fields.");
	for (const [key, value] of entries) {
		validateText(key, "Artifact metadata key", 256);
		if (typeof value !== "string" || value.length > 16_384) throw new RangeError("Artifact metadata value is invalid.");
	}
	const normalized = Object.fromEntries(entries.sort(([left], [right]) => left.localeCompare(right)));
	if (Buffer.byteLength(JSON.stringify(normalized), "utf8") > maximumBytes)
		throw new RangeError("Artifact metadata is too large.");
	return normalized;
}

function validateArtifact(
	artifact: GameArtifact,
	maximumCharacters: number,
	maximumMetadataBytes: number,
): GameArtifact {
	validateSession(artifact.session);
	validateText(artifact.id, "Artifact id", 512);
	validateText(artifact.mediaType, "Artifact media type", 256);
	if (artifact.content.length > maximumCharacters) throw new RangeError("Artifact content is too large.");
	if (!Number.isSafeInteger(artifact.moment.tick) || artifact.moment.tick < 0)
		throw new TypeError("Artifact moment is invalid.");
	if (!Number.isSafeInteger(artifact.createdAt) || artifact.createdAt < 0)
		throw new TypeError("Artifact creation time is invalid.");
	return {
		...structuredClone(artifact),
		...(artifact.metadata === undefined
			? {}
			: { metadata: normalizeMetadata(artifact.metadata, maximumMetadataBytes) as Readonly<Record<string, string>> }),
	};
}

function sameArtifact(left: GameArtifact, right: GameArtifact): boolean {
	return JSON.stringify(left) === JSON.stringify(right);
}

export class SqliteGameArtifactStore implements GameArtifactStore, Disposable {
	private readonly database: DatabaseSync;
	private readonly capacity: number;
	private readonly maximumArtifactCharacters: number;
	private readonly maximumMetadataBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGameArtifactStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.capacity = options.capacity ?? 100_000;
		this.maximumArtifactCharacters = options.maximumArtifactCharacters ?? 10_000_000;
		this.maximumMetadataBytes = options.maximumMetadataBytes ?? 256 * 1024;
		for (const [name, value, minimum, maximum] of [
			["capacity", this.capacity, 1, 10_000_000],
			["maximumArtifactCharacters", this.maximumArtifactCharacters, 1024, 100_000_000],
			["maximumMetadataBytes", this.maximumMetadataBytes, 0, 4 * 1024 * 1024],
		] as const) {
			if (!Number.isInteger(value) || value < minimum || value > maximum) throw new RangeError(`${name} is invalid.`);
		}
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_artifacts (
				world_id TEXT NOT NULL,save_id TEXT NOT NULL,timeline_id TEXT NOT NULL,generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL,session_id TEXT NOT NULL,actor_id TEXT NOT NULL,artifact_id TEXT NOT NULL,
				media_type TEXT NOT NULL,content TEXT NOT NULL,moment_json TEXT NOT NULL,metadata_json TEXT NOT NULL,
				created_at INTEGER NOT NULL,
				PRIMARY KEY(world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,artifact_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_artifacts_created ON game_artifacts(
				world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,created_at,artifact_id);
		`);
	}

	async put(artifactValue: GameArtifact, signal?: AbortSignal): Promise<boolean> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const artifact = validateArtifact(artifactValue, this.maximumArtifactCharacters, this.maximumMetadataBytes);
		const existing = this.readWhole(artifact.session, artifact.id);
		if (existing) {
			if (!sameArtifact(existing, artifact)) throw new Error("An artifact id identifies different content.");
			return false;
		}
		const count = this.database.prepare("SELECT COUNT(*) AS count FROM game_artifacts").get() as { count: number };
		if (count.count >= this.capacity) throw new Error("Artifact store capacity is exhausted.");
		this.database
			.prepare(`INSERT INTO game_artifacts (
				world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,artifact_id,
				media_type,content,moment_json,metadata_json,created_at
			) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)`)
			.run(
				...sessionValues(artifact.session),
				artifact.id,
				artifact.mediaType,
				artifact.content,
				JSON.stringify(artifact.moment),
				JSON.stringify(artifact.metadata ?? {}),
				artifact.createdAt,
			);
		return true;
	}

	async read(
		session: GameSessionKey,
		id: string,
		offset: number,
		maximumCharacters: number,
		signal?: AbortSignal,
	): Promise<GameArtifactSlice | undefined> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		validateText(id, "Artifact id", 512);
		if (!Number.isInteger(offset) || offset < 0 || offset > this.maximumArtifactCharacters)
			throw new RangeError("Artifact offset is invalid.");
		if (!Number.isInteger(maximumCharacters) || maximumCharacters < 1 || maximumCharacters > 262_144)
			throw new RangeError("maximumCharacters must be 1..262144.");
		const row = this.database
			.prepare(`SELECT artifact_id,media_type,content,moment_json,metadata_json,created_at
				FROM game_artifacts WHERE ${sessionWhere} AND artifact_id=?`)
			.get(...sessionValues(session), id) as ArtifactRow | undefined;
		if (!row) return undefined;
		const artifact = this.toArtifact(session, row);
		const end = Math.min(artifact.content.length, offset + maximumCharacters);
		return {
			id: artifact.id,
			mediaType: artifact.mediaType,
			offset,
			content: artifact.content.slice(offset, end),
			totalCharacters: artifact.content.length,
			truncated: end < artifact.content.length,
			...(artifact.metadata === undefined ? {} : { metadata: artifact.metadata }),
		};
	}

	[Symbol.dispose](): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	private readWhole(session: GameSessionKey, id: string): GameArtifact | undefined {
		const row = this.database
			.prepare(`SELECT artifact_id,media_type,content,moment_json,metadata_json,created_at
				FROM game_artifacts WHERE ${sessionWhere} AND artifact_id=?`)
			.get(...sessionValues(session), id) as ArtifactRow | undefined;
		return row ? this.toArtifact(session, row) : undefined;
	}

	private toArtifact(session: GameSessionKey, row: ArtifactRow): GameArtifact {
		try {
			const metadata = JSON.parse(row.metadata_json) as Record<string, string>;
			const artifact: GameArtifact = {
				id: row.artifact_id,
				session: structuredClone(session),
				mediaType: row.media_type,
				content: row.content,
				moment: JSON.parse(row.moment_json) as GameMoment,
				...(Object.keys(metadata).length === 0 ? {} : { metadata }),
				createdAt: row.created_at,
			};
			return validateArtifact(artifact, this.maximumArtifactCharacters, this.maximumMetadataBytes);
		} catch {
			throw new Error("Stored game artifact is corrupt.");
		}
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("Artifact store is closed.");
	}
}

export interface GameArtifactResourcesOptions {
	store: GameArtifactStore;
	maximumInlineCharacters?: number;
	maximumReadCharacters?: number;
	maximumPreviewCharacters?: number;
}

export interface GameArtifactResources {
	toolProvider: GameToolProvider;
	execution: GameToolExecutionMiddleware;
}

function serializeArtifactContent(content: readonly GameInputContent[]): string | undefined {
	if (content.some((item) => item.type === "image")) return undefined;
	return JSON.stringify(
		content.filter((item) => item.type === "text" || item.type === "json").map((item) => structuredClone(item)),
	);
}

function createArtifactId(
	input: GameInput,
	runId: string,
	turn: number,
	toolCallIndex: number,
	toolName: string,
	content: string,
) {
	const hash = createHash("sha256");
	for (const value of [
		"oga-artifact-v1",
		input.session.worldId,
		input.session.saveId,
		input.session.timelineId,
		String(input.session.generation),
		input.session.ownerId,
		input.session.sessionId,
		input.session.actorId,
		input.id,
		runId,
		String(turn),
		String(toolCallIndex),
		toolName,
		content,
	])
		hash.update(value).update("\0");
	return `artifact-v1-${hash.digest("hex")}`;
}

function requiredInteger(argumentsValue: JsonObject, name: string, fallback?: number): number {
	const value = argumentsValue[name] ?? fallback;
	if (typeof value !== "number" || !Number.isInteger(value)) throw new TypeError(`${name} must be an integer.`);
	return value;
}

export function createGameArtifactResources(options: GameArtifactResourcesOptions): GameArtifactResources {
	const maximumInlineCharacters = options.maximumInlineCharacters ?? 64 * 1024;
	const maximumReadCharacters = options.maximumReadCharacters ?? 64 * 1024;
	const maximumPreviewCharacters = options.maximumPreviewCharacters ?? Math.min(2048, maximumInlineCharacters);
	if (
		!Number.isInteger(maximumInlineCharacters) ||
		maximumInlineCharacters < 1024 ||
		!Number.isInteger(maximumReadCharacters) ||
		maximumReadCharacters < 1024 ||
		maximumReadCharacters > 262_144 ||
		!Number.isInteger(maximumPreviewCharacters) ||
		maximumPreviewCharacters < 0 ||
		maximumPreviewCharacters > maximumInlineCharacters
	)
		throw new RangeError("Artifact resource limits are invalid.");

	return {
		toolProvider: {
			async provide(input): Promise<readonly GameTool[]> {
				return [
					{
						definition: {
							name: "read_agent_artifact",
							label: "Read agent artifact",
							description: "Read a bounded page from an artifact created for this exact game character session.",
							parameters: {
								type: "object",
								properties: {
									id: { type: "string", minLength: 1, maxLength: 512 },
									offset: { type: "integer", minimum: 0 },
									maximumCharacters: {
										type: "integer",
										minimum: 1,
										maximum: maximumReadCharacters,
									},
								},
								required: ["id"],
								additionalProperties: false,
							},
							risk: "read",
						},
						async execute(call, context) {
							if (!sameSession(input.session, context.input.session))
								return { isError: true, content: [{ type: "json", value: { error: "artifact_not_found" } }] };
							const id = call.arguments["id"];
							if (typeof id !== "string") throw new TypeError("id must be a string.");
							const result = await options.store.read(
								context.input.session,
								id,
								requiredInteger(call.arguments, "offset", 0),
								requiredInteger(call.arguments, "maximumCharacters", maximumReadCharacters),
								context.signal,
							);
							return result
								? { content: [{ type: "json", value: result as unknown as JsonValue }] }
								: { isError: true, content: [{ type: "json", value: { error: "artifact_not_found" } }] };
						},
					},
				];
			},
		},
		execution: {
			async execute(tool, _call, context, next): Promise<GameToolResult> {
				const result = await next();
				const serialized = serializeArtifactContent(result.content);
				if (serialized === undefined || serialized.length <= maximumInlineCharacters) return result;
				const id = createArtifactId(
					context.input,
					context.runId,
					context.turn,
					context.toolCallIndex,
					tool.name,
					serialized,
				);
				try {
					await options.store.put(
						{
							id,
							session: context.input.session,
							mediaType: "application/vnd.opengameagent.tool-result+json",
							content: serialized,
							moment: context.input.moment,
							metadata: { toolName: tool.name },
							createdAt: Date.now(),
						},
						context.signal,
					);
				} catch (error) {
					if (context.signal.aborted) throw error;
					return result;
				}
				const preview = serialized.slice(0, maximumPreviewCharacters);
				return {
					...result,
					content: [
						{
							type: "json",
							value: {
								artifactId: id,
								mediaType: "application/vnd.opengameagent.tool-result+json",
								totalCharacters: serialized.length,
								preview,
								truncated: true,
								readTool: "read_agent_artifact",
							},
						},
						...result.content.filter((item) => item.type === "imageRef"),
					],
				};
			},
		},
	};
}
