import { createHash } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type {
	GameInput,
	GameSessionKey,
	GameTool,
	GameToolDefinition,
	JsonObject,
	JsonValue,
} from "@opengameagent/protocol";
import type { GamePostToolContextProvider, GameToolProvider } from "@opengameagent/runtime";
import type { GameSkill, GameSkillSource } from "@opengameagent/skills";

export type GameEvolutionMode = "off" | "conservative" | "aggressive";
export type GameBehaviorScope = "actor" | "world";
export type GameBehaviorOutcome = "success" | "partial" | "failure" | "cancelled";
export type GameBehaviorVersionStatus = "validated" | "rejected";

export interface GameBehaviorReflection {
	id: string;
	session: GameSessionKey;
	inputId: string;
	outcome: GameBehaviorOutcome;
	summary: string;
	observations: readonly string[];
	patterns: readonly string[];
	failures: readonly string[];
	evidence?: JsonValue;
	createdAt: number;
}

export interface GameBehaviorStep {
	id: string;
	tool: string;
	instruction: string;
}

export interface GameBehaviorSkillCandidate {
	id: string;
	name: string;
	description: string;
	instructions: string;
	scope: GameBehaviorScope;
	inputTypes: readonly string[];
	steps: readonly GameBehaviorStep[];
	reflectionId: string;
	priority?: number;
}

export interface GameBehaviorSkillVersion extends GameBehaviorSkillCandidate {
	version: number;
	digest: string;
	status: GameBehaviorVersionStatus;
	active: boolean;
	validationEvidence?: JsonValue;
	rejectionReason?: string;
	createdAt: number;
}

export interface GameBehaviorValidationContext {
	input: GameInput;
	reflection: GameBehaviorReflection;
	candidate: GameBehaviorSkillCandidate;
	availableTools: readonly GameToolDefinition[];
}

export type GameBehaviorValidationResult =
	| { accepted: true; evidence?: JsonValue }
	| { accepted: false; reason: string; evidence?: JsonValue };

export interface GameBehaviorSkillValidator {
	validate(
		context: GameBehaviorValidationContext,
		signal?: AbortSignal,
	): Promise<GameBehaviorValidationResult> | GameBehaviorValidationResult;
}

export type GameBehaviorEvolutionEvent =
	| { type: "reflection.recorded"; session: GameSessionKey; inputId: string; reflectionId: string }
	| { type: "skill.validated" | "skill.rejected"; session: GameSessionKey; skillId: string; version: number }
	| {
			type: "skill.activated" | "skill.rolled-back" | "skill.retired";
			session: GameSessionKey;
			skillId: string;
			version?: number;
	  };

export interface SqliteGameBehaviorStoreOptions {
	maximumReflectionsPerActor?: number;
	maximumVersionsPerSkill?: number;
	maximumRecordBytes?: number;
}

export interface GameBehaviorEvolutionOptions {
	store: SqliteGameBehaviorStore;
	validator: GameBehaviorSkillValidator;
	availableTools(
		input: GameInput,
		signal?: AbortSignal,
	): Promise<readonly GameToolDefinition[]> | readonly GameToolDefinition[];
	mode: GameEvolutionMode | ((input: GameInput) => GameEvolutionMode);
	allowedInputTypes?: readonly string[];
	allowAutomaticWorldActivation?: boolean;
	onEvent?(event: GameBehaviorEvolutionEvent): Promise<void> | void;
}

export interface GameBehaviorReviewInputOptions {
	id: string;
	source: GameInput;
	outcome: GameBehaviorOutcome;
	visibleSummary: string;
	visibleEvidence?: JsonValue;
}

interface VersionRow {
	version: number;
	status: string;
	record_json: string;
	active_version: number | null;
	scope_kind?: string;
}

const portableId = /^[a-z0-9](?:[a-z0-9._:-]{0,190}[a-z0-9])?$/i;
const evolutionToolNames = new Set(["record_game_reflection", "propose_game_behavior_skill"]);

function boundedInteger(
	value: number | undefined,
	fallback: number,
	minimum: number,
	maximum: number,
	name: string,
): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < minimum || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function validateId(value: string, name: string): void {
	if (!portableId.test(value)) throw new TypeError(`${name} is not a portable bounded identifier.`);
}

function validateText(value: string, name: string, maximum: number): void {
	if (typeof value !== "string" || value.trim().length < 1 || value.length > maximum)
		throw new RangeError(`${name} is empty or exceeds its configured limit.`);
}

function stringArray(value: unknown, name: string, maximumItems: number, maximumLength: number): string[] {
	if (!Array.isArray(value) || value.length > maximumItems || value.some((item) => typeof item !== "string"))
		throw new TypeError(`${name} must be a bounded string array.`);
	const result = value as string[];
	for (const item of result) validateText(item, name, maximumLength);
	return [...result];
}

function canonical(value: JsonValue): string {
	if (value === null || typeof value !== "object") return JSON.stringify(value);
	if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
	return `{${Object.keys(value)
		.sort()
		.map((key) => `${JSON.stringify(key)}:${canonical(value[key] as JsonValue)}`)
		.join(",")}}`;
}

function actorScope(session: GameSessionKey): string {
	return canonical(session as unknown as JsonValue);
}

function worldScope(session: GameSessionKey): string {
	return canonical({
		worldId: session.worldId,
		saveId: session.saveId,
		timelineId: session.timelineId,
		generation: session.generation,
		ownerId: session.ownerId,
	});
}

function scopeKey(session: GameSessionKey, scope: GameBehaviorScope): string {
	return scope === "actor" ? actorScope(session) : worldScope(session);
}

function digestCandidate(candidate: GameBehaviorSkillCandidate): string {
	return createHash("sha256")
		.update(canonical(candidate as unknown as JsonValue))
		.digest("base64url");
}

function clone<T>(value: T): T {
	return structuredClone(value);
}

function parseJson<T>(text: string, maximumBytes: number): T {
	if (Buffer.byteLength(text, "utf8") > maximumBytes)
		throw new Error("Stored behavior record exceeds its configured limit.");
	try {
		return JSON.parse(text) as T;
	} catch {
		throw new Error("Stored behavior record is corrupt.");
	}
}

export function createGameBehaviorReviewInput(options: GameBehaviorReviewInputOptions): GameInput {
	validateId(options.id, "Review input id");
	validateText(options.visibleSummary, "Visible outcome summary", 16_384);
	if (
		options.outcome !== "success" &&
		options.outcome !== "partial" &&
		options.outcome !== "failure" &&
		options.outcome !== "cancelled"
	)
		throw new TypeError("Review outcome is invalid.");
	const value: JsonObject = {
		sourceInputId: options.source.id,
		outcome: options.outcome,
		visibleSummary: options.visibleSummary,
		...(options.visibleEvidence === undefined ? {} : { visibleEvidence: options.visibleEvidence }),
	};
	if (Buffer.byteLength(canonical(value), "utf8") > 64 * 1_024)
		throw new RangeError("Behavior review input exceeds its configured byte limit.");
	return {
		id: options.id,
		type: "agent.reflection",
		session: clone(options.source.session),
		moment: clone(options.source.moment),
		content: [{ type: "json", value }],
	};
}

export class SqliteGameBehaviorStore implements Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumReflectionsPerActor: number;
	private readonly maximumVersionsPerSkill: number;
	private readonly maximumRecordBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGameBehaviorStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumReflectionsPerActor = boundedInteger(
			options.maximumReflectionsPerActor,
			256,
			1,
			100_000,
			"maximumReflectionsPerActor",
		);
		this.maximumVersionsPerSkill = boundedInteger(
			options.maximumVersionsPerSkill,
			32,
			2,
			1_024,
			"maximumVersionsPerSkill",
		);
		this.maximumRecordBytes = boundedInteger(
			options.maximumRecordBytes,
			512 * 1_024,
			1_024,
			16 * 1_024 * 1_024,
			"maximumRecordBytes",
		);
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_behavior_reflections (
				actor_scope TEXT NOT NULL,input_id TEXT NOT NULL,reflection_id TEXT NOT NULL,created_at INTEGER NOT NULL,record_json TEXT NOT NULL,
				PRIMARY KEY(actor_scope,input_id),UNIQUE(reflection_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_behavior_reflections_actor ON game_behavior_reflections(actor_scope,created_at DESC);
			CREATE TABLE IF NOT EXISTS game_behavior_versions (
				scope_kind TEXT NOT NULL,scope_key TEXT NOT NULL,skill_id TEXT NOT NULL,version INTEGER NOT NULL,
				source_input_id TEXT NOT NULL,digest TEXT NOT NULL,status TEXT NOT NULL,created_at INTEGER NOT NULL,record_json TEXT NOT NULL,
				PRIMARY KEY(scope_kind,scope_key,skill_id,version),UNIQUE(scope_kind,scope_key,skill_id,source_input_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_behavior_versions_latest ON game_behavior_versions(scope_kind,scope_key,skill_id,version DESC);
			CREATE TABLE IF NOT EXISTS game_behavior_active (
				scope_kind TEXT NOT NULL,scope_key TEXT NOT NULL,skill_id TEXT NOT NULL,version INTEGER NOT NULL,updated_at INTEGER NOT NULL,
				PRIMARY KEY(scope_kind,scope_key,skill_id)
			) STRICT;
		`);
	}

	recordReflection(reflection: GameBehaviorReflection): GameBehaviorReflection {
		this.ensureOpen();
		validateId(reflection.id, "Reflection id");
		validateId(reflection.inputId, "Input id");
		validateText(reflection.summary, "Reflection summary", 8_192);
		if (reflection.observations.length > 64 || reflection.patterns.length > 64 || reflection.failures.length > 64)
			throw new RangeError("Reflection arrays exceed their configured limits.");
		for (const text of [...reflection.observations, ...reflection.patterns, ...reflection.failures])
			validateText(text, "Reflection item", 2_048);
		const json = canonical(reflection as unknown as JsonValue);
		this.ensureRecordSize(json);
		const key = actorScope(reflection.session);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.database
				.prepare("SELECT record_json FROM game_behavior_reflections WHERE actor_scope=? AND input_id=?")
				.get(key, reflection.inputId) as { record_json: string } | undefined;
			if (existing) {
				const stored = parseJson<GameBehaviorReflection>(existing.record_json, this.maximumRecordBytes);
				if (
					canonical({ ...stored, createdAt: 0 } as unknown as JsonValue) !==
					canonical({ ...reflection, createdAt: 0 } as unknown as JsonValue)
				)
					throw new Error("This input already has a different reflection.");
				this.database.exec("COMMIT");
				return stored;
			}
			this.database
				.prepare("INSERT INTO game_behavior_reflections VALUES(?,?,?,?,?)")
				.run(key, reflection.inputId, reflection.id, reflection.createdAt, json);
			this.database
				.prepare(`DELETE FROM game_behavior_reflections WHERE rowid IN (
					SELECT rowid FROM game_behavior_reflections WHERE actor_scope=? ORDER BY created_at DESC,input_id DESC LIMIT -1 OFFSET ?)`)
				.run(key, this.maximumReflectionsPerActor);
			this.database.exec("COMMIT");
			return clone(reflection);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	readReflection(session: GameSessionKey, reflectionId: string): GameBehaviorReflection | undefined {
		this.ensureOpen();
		const row = this.database
			.prepare("SELECT record_json FROM game_behavior_reflections WHERE actor_scope=? AND reflection_id=?")
			.get(actorScope(session), reflectionId) as { record_json: string } | undefined;
		return row ? parseJson<GameBehaviorReflection>(row.record_json, this.maximumRecordBytes) : undefined;
	}

	createVersion(
		input: GameInput,
		candidate: GameBehaviorSkillCandidate,
		result: GameBehaviorValidationResult,
		activate: boolean,
	): GameBehaviorSkillVersion {
		this.ensureOpen();
		this.validateCandidate(candidate);
		const key = scopeKey(input.session, candidate.scope);
		const candidateDigest = digestCandidate(candidate);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const replay = this.database
				.prepare(`SELECT v.version,v.status,v.record_json,a.version active_version
					FROM game_behavior_versions v LEFT JOIN game_behavior_active a USING(scope_kind,scope_key,skill_id)
					WHERE v.scope_kind=? AND v.scope_key=? AND v.skill_id=? AND v.source_input_id=?`)
				.get(candidate.scope, key, candidate.id, input.id) as VersionRow | undefined;
			if (replay) {
				const record = this.parseVersion(replay);
				if (record.digest !== candidateDigest) throw new Error("This input already proposed different skill content.");
				this.database.exec("COMMIT");
				return record;
			}
			const latest = this.database
				.prepare(`SELECT COALESCE(MAX(version),0) value FROM game_behavior_versions
					WHERE scope_kind=? AND scope_key=? AND skill_id=?`)
				.get(candidate.scope, key, candidate.id) as { value: number };
			const version = latest.value + 1;
			const createdAt = Date.now();
			const record: GameBehaviorSkillVersion = {
				...clone(candidate),
				version,
				digest: candidateDigest,
				status: result.accepted ? "validated" : "rejected",
				active: result.accepted && activate,
				...(result.evidence === undefined ? {} : { validationEvidence: result.evidence }),
				...(result.accepted ? {} : { rejectionReason: result.reason }),
				createdAt,
			};
			const json = canonical(record as unknown as JsonValue);
			this.ensureRecordSize(json);
			this.database
				.prepare("INSERT INTO game_behavior_versions VALUES(?,?,?,?,?,?,?,?,?)")
				.run(candidate.scope, key, candidate.id, version, input.id, candidateDigest, record.status, createdAt, json);
			if (record.active) this.upsertActive(candidate.scope, key, candidate.id, version);
			this.pruneVersions(candidate.scope, key, candidate.id);
			this.database.exec("COMMIT");
			return clone(record);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	activate(
		session: GameSessionKey,
		scope: GameBehaviorScope,
		skillId: string,
		version: number,
	): GameBehaviorSkillVersion {
		return this.selectVersion(session, scope, skillId, version, false);
	}

	rollback(
		session: GameSessionKey,
		scope: GameBehaviorScope,
		skillId: string,
		version: number,
	): GameBehaviorSkillVersion {
		return this.selectVersion(session, scope, skillId, version, true);
	}

	retire(session: GameSessionKey, scope: GameBehaviorScope, skillId: string): boolean {
		this.ensureOpen();
		validateId(skillId, "Skill id");
		const result = this.database
			.prepare("DELETE FROM game_behavior_active WHERE scope_kind=? AND scope_key=? AND skill_id=?")
			.run(scope, scopeKey(session, scope), skillId);
		return result.changes > 0;
	}

	listVersions(
		session: GameSessionKey,
		scope: GameBehaviorScope,
		skillId: string,
	): readonly GameBehaviorSkillVersion[] {
		this.ensureOpen();
		validateId(skillId, "Skill id");
		const rows = this.database
			.prepare(`SELECT v.version,v.status,v.record_json,a.version active_version
				FROM game_behavior_versions v LEFT JOIN game_behavior_active a USING(scope_kind,scope_key,skill_id)
				WHERE v.scope_kind=? AND v.scope_key=? AND v.skill_id=? ORDER BY v.version DESC`)
			.all(scope, scopeKey(session, scope), skillId) as unknown as VersionRow[];
		return rows.map((row) => this.parseVersion(row));
	}

	listActive(input: GameInput): readonly GameBehaviorSkillVersion[] {
		this.ensureOpen();
		const rows = this.database
			.prepare(`SELECT v.version,v.status,v.record_json,a.version active_version,a.scope_kind
				FROM game_behavior_active a JOIN game_behavior_versions v USING(scope_kind,scope_key,skill_id,version)
				WHERE (a.scope_kind='actor' AND a.scope_key=?) OR (a.scope_kind='world' AND a.scope_key=?)
				ORDER BY CASE a.scope_kind WHEN 'world' THEN 0 ELSE 1 END,v.skill_id`)
			.all(actorScope(input.session), worldScope(input.session)) as unknown as VersionRow[];
		const byId = new Map<string, GameBehaviorSkillVersion>();
		for (const row of rows) {
			const record = this.parseVersion(row);
			byId.set(record.id, record);
		}
		return [...byId.values()];
	}

	[Symbol.dispose](): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	private selectVersion(
		session: GameSessionKey,
		scope: GameBehaviorScope,
		skillId: string,
		version: number,
		rollback: boolean,
	): GameBehaviorSkillVersion {
		this.ensureOpen();
		validateId(skillId, "Skill id");
		if (!Number.isInteger(version) || version < 1) throw new RangeError("Skill version is invalid.");
		const key = scopeKey(session, scope);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			if (rollback) {
				const active = this.database
					.prepare("SELECT version FROM game_behavior_active WHERE scope_kind=? AND scope_key=? AND skill_id=?")
					.get(scope, key, skillId) as { version: number } | undefined;
				if (!active) throw new Error("Behavior skill is not active.");
				if (version >= active.version) throw new Error("Rollback target must be older than the active version.");
			}
			const row = this.database
				.prepare(`SELECT v.version,v.status,v.record_json,a.version active_version
					FROM game_behavior_versions v LEFT JOIN game_behavior_active a USING(scope_kind,scope_key,skill_id)
					WHERE v.scope_kind=? AND v.scope_key=? AND v.skill_id=? AND v.version=?`)
				.get(scope, key, skillId, version) as VersionRow | undefined;
			if (!row) throw new Error("Behavior skill version was not found.");
			const record = this.parseVersion(row);
			if (record.status !== "validated") throw new Error("Only validated behavior skill versions can be activated.");
			this.upsertActive(scope, key, skillId, version);
			this.database.exec("COMMIT");
			return { ...record, active: true };
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	private upsertActive(scope: GameBehaviorScope, key: string, skillId: string, version: number): void {
		this.database
			.prepare(`INSERT INTO game_behavior_active VALUES(?,?,?,?,?)
				ON CONFLICT(scope_kind,scope_key,skill_id) DO UPDATE SET version=excluded.version,updated_at=excluded.updated_at`)
			.run(scope, key, skillId, version, Date.now());
	}

	private pruneVersions(scope: GameBehaviorScope, key: string, skillId: string): void {
		this.database
			.prepare(`DELETE FROM game_behavior_versions WHERE rowid IN (
				SELECT v.rowid FROM game_behavior_versions v LEFT JOIN game_behavior_active a
				ON a.scope_kind=v.scope_kind AND a.scope_key=v.scope_key AND a.skill_id=v.skill_id AND a.version=v.version
				WHERE v.scope_kind=? AND v.scope_key=? AND v.skill_id=? AND a.version IS NULL
				ORDER BY v.version DESC LIMIT -1 OFFSET ?)`)
			.run(scope, key, skillId, this.maximumVersionsPerSkill - 1);
	}

	private parseVersion(row: VersionRow): GameBehaviorSkillVersion {
		const record = parseJson<GameBehaviorSkillVersion>(row.record_json, this.maximumRecordBytes);
		return {
			...record,
			status: row.status as GameBehaviorVersionStatus,
			active: row.active_version === row.version,
		};
	}

	private validateCandidate(candidate: GameBehaviorSkillCandidate): void {
		validateId(candidate.id, "Skill id");
		validateText(candidate.name, "Skill name", 128);
		validateText(candidate.description, "Skill description", 1_024);
		validateText(candidate.instructions, "Skill instructions", 64_000);
		validateId(candidate.reflectionId, "Reflection id");
		if (candidate.scope !== "actor" && candidate.scope !== "world")
			throw new TypeError("Behavior skill scope is invalid.");
		if (candidate.inputTypes.length > 128 || candidate.steps.length < 1 || candidate.steps.length > 64)
			throw new RangeError("Behavior skill inputs or steps exceed their configured limits.");
		for (const inputType of candidate.inputTypes) validateText(inputType, "Input type", 192);
		const stepIds = new Set<string>();
		for (const step of candidate.steps) {
			validateId(step.id, "Step id");
			validateId(step.tool, "Step tool");
			validateText(step.instruction, "Step instruction", 2_048);
			if (stepIds.has(step.id)) throw new Error("Behavior skill step ids must be unique.");
			stepIds.add(step.id);
		}
		if (
			candidate.priority !== undefined &&
			(!Number.isInteger(candidate.priority) || candidate.priority < -1_000_000 || candidate.priority > 1_000_000)
		)
			throw new RangeError("Behavior skill priority is invalid.");
	}

	private ensureRecordSize(json: string): void {
		if (Buffer.byteLength(json, "utf8") > this.maximumRecordBytes)
			throw new RangeError("Behavior record exceeds its configured byte limit.");
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("Behavior store is closed.");
	}
}

function parseReflection(input: GameInput, args: JsonObject): GameBehaviorReflection {
	const outcome = args["outcome"];
	const summary = args["summary"];
	if (outcome !== "success" && outcome !== "partial" && outcome !== "failure" && outcome !== "cancelled")
		throw new TypeError("Reflection outcome is invalid.");
	if (typeof summary !== "string") throw new TypeError("Reflection summary is required.");
	const observations = stringArray(args["observations"], "observations", 64, 2_048);
	const patterns = stringArray(args["patterns"], "patterns", 64, 2_048);
	const failures = stringArray(args["failures"], "failures", 64, 2_048);
	const id = `reflection-${createHash("sha256")
		.update(actorScope(input.session))
		.update("\0")
		.update(input.id)
		.digest("hex")
		.slice(0, 32)}`;
	return {
		id,
		session: clone(input.session),
		inputId: input.id,
		outcome,
		summary,
		observations,
		patterns,
		failures,
		...(args["evidence"] === undefined ? {} : { evidence: args["evidence"] as JsonValue }),
		createdAt: Date.now(),
	};
}

function parseCandidate(args: JsonObject): GameBehaviorSkillCandidate {
	const stepsValue = args["steps"];
	if (!Array.isArray(stepsValue)) throw new TypeError("Behavior skill steps are required.");
	const steps = stepsValue.map((value) => {
		if (value === null || typeof value !== "object" || Array.isArray(value))
			throw new TypeError("Behavior skill step is invalid.");
		const step = value as JsonObject;
		if (typeof step["id"] !== "string" || typeof step["tool"] !== "string" || typeof step["instruction"] !== "string")
			throw new TypeError("Behavior skill step fields are invalid.");
		return { id: step["id"], tool: step["tool"], instruction: step["instruction"] };
	});
	if (
		typeof args["id"] !== "string" ||
		typeof args["name"] !== "string" ||
		typeof args["description"] !== "string" ||
		typeof args["instructions"] !== "string" ||
		(args["scope"] !== "actor" && args["scope"] !== "world") ||
		typeof args["reflectionId"] !== "string"
	)
		throw new TypeError("Behavior skill proposal fields are invalid.");
	const priority = args["priority"];
	if (priority !== undefined && typeof priority !== "number")
		throw new TypeError("Behavior skill priority is invalid.");
	return {
		id: args["id"],
		name: args["name"],
		description: args["description"],
		instructions: args["instructions"],
		scope: args["scope"],
		inputTypes: stringArray(args["inputTypes"], "inputTypes", 128, 192),
		steps,
		reflectionId: args["reflectionId"],
		...(priority === undefined ? {} : { priority }),
	};
}

function evolutionTools(
	options: GameBehaviorEvolutionOptions,
	input: GameInput,
	mode: GameEvolutionMode,
): readonly GameTool[] {
	const recordReflection: GameTool = {
		definition: {
			name: "record_game_reflection",
			label: "Record game behavior reflection",
			description:
				"Record a structured, visible reflection about a completed game-agent outcome. Do not include hidden reasoning.",
			risk: "low",
			parameters: {
				type: "object",
				properties: {
					outcome: { type: "string", enum: ["success", "partial", "failure", "cancelled"] },
					summary: { type: "string", minLength: 1, maxLength: 8_192 },
					observations: {
						type: "array",
						maxItems: 64,
						items: { type: "string", minLength: 1, maxLength: 2_048 },
					},
					patterns: {
						type: "array",
						maxItems: 64,
						items: { type: "string", minLength: 1, maxLength: 2_048 },
					},
					failures: {
						type: "array",
						maxItems: 64,
						items: { type: "string", minLength: 1, maxLength: 2_048 },
					},
					evidence: {},
				},
				required: ["outcome", "summary", "observations", "patterns", "failures"],
				additionalProperties: false,
			},
		},
		async execute(call) {
			const reflection = options.store.recordReflection(parseReflection(input, call.arguments));
			await options.onEvent?.({
				type: "reflection.recorded",
				session: clone(input.session),
				inputId: input.id,
				reflectionId: reflection.id,
			});
			return {
				content: [{ type: "json", value: { reflectionId: reflection.id, recorded: true } }],
				details: { reflectionId: reflection.id },
			};
		},
	};

	const proposeSkill: GameTool = {
		definition: {
			name: "propose_game_behavior_skill",
			label: "Propose game behavior skill",
			description:
				"Propose a versioned composite behavior using only currently available game tools and a recorded reflection.",
			risk: "medium",
			parameters: {
				type: "object",
				properties: {
					id: { type: "string", minLength: 1, maxLength: 192 },
					name: { type: "string", minLength: 1, maxLength: 128 },
					description: { type: "string", minLength: 1, maxLength: 1_024 },
					instructions: { type: "string", minLength: 1, maxLength: 64_000 },
					scope: { type: "string", enum: ["actor", "world"] },
					inputTypes: {
						type: "array",
						maxItems: 128,
						items: { type: "string", minLength: 1, maxLength: 192 },
					},
					steps: {
						type: "array",
						minItems: 1,
						maxItems: 64,
						items: {
							type: "object",
							properties: {
								id: { type: "string", minLength: 1, maxLength: 192 },
								tool: { type: "string", minLength: 1, maxLength: 192 },
								instruction: { type: "string", minLength: 1, maxLength: 2_048 },
							},
							required: ["id", "tool", "instruction"],
							additionalProperties: false,
						},
					},
					reflectionId: { type: "string", minLength: 1, maxLength: 192 },
					priority: { type: "integer", minimum: -1_000_000, maximum: 1_000_000 },
				},
				required: ["id", "name", "description", "instructions", "scope", "inputTypes", "steps", "reflectionId"],
				additionalProperties: false,
			},
		},
		async execute(call, context) {
			const candidate = parseCandidate(call.arguments);
			const reflection = options.store.readReflection(input.session, candidate.reflectionId);
			if (!reflection || reflection.inputId !== input.id)
				throw new Error("The proposal requires this input's recorded reflection.");
			const availableTools = await options.availableTools(input, context.signal);
			const byName = new Set(availableTools.map((tool) => tool.name));
			for (const step of candidate.steps) {
				if (evolutionToolNames.has(step.tool) || !byName.has(step.tool))
					throw new Error(`Composite behavior step '${step.id}' references an unavailable tool.`);
			}
			const validation = await options.validator.validate(
				{ input, reflection, candidate, availableTools },
				context.signal,
			);
			if (!validation.accepted) validateText(validation.reason, "Validation rejection", 4_096);
			const version = options.store.createVersion(
				input,
				candidate,
				validation,
				validation.accepted &&
					mode === "aggressive" &&
					(candidate.scope === "actor" || options.allowAutomaticWorldActivation === true),
			);
			await options.onEvent?.({
				type: validation.accepted ? "skill.validated" : "skill.rejected",
				session: clone(input.session),
				skillId: version.id,
				version: version.version,
			});
			if (version.active)
				await options.onEvent?.({
					type: "skill.activated",
					session: clone(input.session),
					skillId: version.id,
					version: version.version,
				});
			return {
				content: [
					{
						type: "json",
						value: {
							skillId: version.id,
							version: version.version,
							status: version.status,
							active: version.active,
						},
					},
				],
				details: {
					skillId: version.id,
					version: version.version,
					status: version.status,
					active: version.active,
				},
				...(validation.accepted ? {} : { isError: true }),
			};
		},
	};
	return [recordReflection, proposeSkill];
}

export class GameBehaviorEvolutionController {
	constructor(private readonly options: GameBehaviorEvolutionOptions) {}

	async activate(
		session: GameSessionKey,
		scope: GameBehaviorScope,
		skillId: string,
		version: number,
	): Promise<GameBehaviorSkillVersion> {
		const result = this.options.store.activate(session, scope, skillId, version);
		await this.options.onEvent?.({ type: "skill.activated", session: clone(session), skillId, version });
		return result;
	}

	async rollback(
		session: GameSessionKey,
		scope: GameBehaviorScope,
		skillId: string,
		version: number,
	): Promise<GameBehaviorSkillVersion> {
		const result = this.options.store.rollback(session, scope, skillId, version);
		await this.options.onEvent?.({ type: "skill.rolled-back", session: clone(session), skillId, version });
		return result;
	}

	async retire(session: GameSessionKey, scope: GameBehaviorScope, skillId: string): Promise<boolean> {
		const changed = this.options.store.retire(session, scope, skillId);
		if (changed) await this.options.onEvent?.({ type: "skill.retired", session: clone(session), skillId });
		return changed;
	}
}

export class EvolvedGameSkillSource implements GameSkillSource {
	constructor(private readonly store: SqliteGameBehaviorStore) {}

	async list(): Promise<readonly GameSkill[]> {
		return [];
	}

	async listForInput(input: GameInput, signal?: AbortSignal): Promise<readonly GameSkill[]> {
		signal?.throwIfAborted();
		return this.store.listActive(input).map((record) => {
			const requiredTools = [...new Set(record.steps.map((step) => step.tool))].sort();
			const orderedSteps = record.steps
				.map((step, index) => `${index + 1}. [${step.tool}] ${step.instruction}`)
				.join("\n");
			return {
				id: record.id,
				name: record.name,
				description: record.description,
				instructions: `${record.instructions}\n\nOrdered tool composition:\n${orderedSteps}`,
				inputTypes: [...record.inputTypes],
				requiredTools,
				priority: record.priority ?? 0,
				version: String(record.version),
				digest: record.digest,
				disableModelInvocation: false,
			};
		});
	}
}

export interface GameBehaviorEvolutionResources {
	toolProvider: GameToolProvider;
	postToolContextProvider: GamePostToolContextProvider;
	skillSource: EvolvedGameSkillSource;
	controller: GameBehaviorEvolutionController;
}

export function createGameBehaviorEvolution(options: GameBehaviorEvolutionOptions): GameBehaviorEvolutionResources {
	if (!options.store || !options.validator || !options.availableTools)
		throw new TypeError("Evolution store, validator, and tool catalog are required.");
	const allowedInputTypes = new Set(options.allowedInputTypes ?? ["agent.reflection"]);
	const resolveMode = (input: GameInput): GameEvolutionMode =>
		typeof options.mode === "function" ? options.mode(input) : options.mode;
	const toolProvider: GameToolProvider = {
		async provide(input, signal) {
			signal.throwIfAborted();
			const mode = resolveMode(input);
			if (mode !== "off" && mode !== "conservative" && mode !== "aggressive")
				throw new TypeError("Evolution mode is invalid.");
			if (mode === "off" || !allowedInputTypes.has(input.type)) return [];
			return evolutionTools(options, input, mode);
		},
	};
	const postToolContextProvider: GamePostToolContextProvider = {
		async provide(input, definitions, signal) {
			signal.throwIfAborted();
			const mode = resolveMode(input);
			if (mode === "off" || !allowedInputTypes.has(input.type)) return undefined;
			const tools = new Set(definitions.map((definition) => definition.name));
			if (!tools.has("record_game_reflection") || !tools.has("propose_game_behavior_skill")) return undefined;
			return {
				name: "game-behavior-evolution",
				priority: 80,
				value: {
					mode,
					workflow: [
						"Record one structured reflection using only visible outcomes and host evidence.",
						"Propose a skill only when the reflection contains a reusable behavior pattern.",
					],
					constraints: [
						"Never include hidden reasoning or a private chain of thought.",
						"Composite steps may reference only tools currently advertised by the host.",
						"Prefer actor scope. World scope is shared only after host-authorized activation.",
					],
				},
			};
		},
	};
	return {
		toolProvider,
		postToolContextProvider,
		skillSource: new EvolvedGameSkillSource(options.store),
		controller: new GameBehaviorEvolutionController(options),
	};
}
