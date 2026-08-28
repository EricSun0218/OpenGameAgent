import { createHash, timingSafeEqual } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type {
	GameSessionKey,
	GameTool,
	GameToolCall,
	GameToolDefinition,
	GameToolExecutionContext,
	JsonObject,
	JsonValue,
} from "@opengameagent/protocol";
import type {
	GameMediaGenerationProgress,
	GameMediaGenerationRequest,
	GameMediaGenerationResult,
	GameMediaKind,
	GameMediaRegistry,
	GameMediaResource,
	GameMediaResourceStore,
} from "./media.js";
import { validateMediaIdentifier } from "./media.js";

export type GameMediaAssetStatus =
	| "prepared"
	| "generating"
	| "generation-uncertain"
	| "generated"
	| "importing"
	| "import-uncertain"
	| "completed"
	| "rejected"
	| "failed";

export type GameMediaAssetImportStatus = "committed" | "rejected" | "failed" | "uncertain";

export interface GameMediaAssetRequest {
	operationId: string;
	session: GameSessionKey;
	assetType: string;
	provider: string;
	model: string;
	importerId: string;
	expectedRevision: number;
	generation: GameMediaGenerationRequest;
	metadata?: JsonObject;
}

export interface GameMediaAssetManifest {
	assetId: string;
	resources: readonly GameMediaResource[];
	provider: string;
	model: string;
	responseId?: string;
	usage?: JsonValue;
}

export interface GameMediaAssetImportReceipt {
	operationId: string;
	session: GameSessionKey;
	expectedRevision: number;
	status: GameMediaAssetImportStatus;
	stateRevision?: number;
	result?: JsonObject;
}

export interface GameMediaAssetFailure {
	category: string;
	message: string;
}

export interface GameMediaAssetJob {
	operationId: string;
	session: GameSessionKey;
	assetType: string;
	provider: string;
	model: string;
	importerId: string;
	expectedRevision: number;
	mediaKind: GameMediaKind;
	requestFingerprint: string;
	revision: number;
	status: GameMediaAssetStatus;
	createdAt: number;
	updatedAt: number;
	manifest?: GameMediaAssetManifest;
	importReceipt?: GameMediaAssetImportReceipt;
	failure?: GameMediaAssetFailure;
}

export interface GameMediaAssetSaveResult {
	saved: boolean;
	current: GameMediaAssetJob;
}

export interface GameMediaAssetJobStore {
	read(session: GameSessionKey, operationId: string, signal?: AbortSignal): Promise<GameMediaAssetJob | undefined>;
	save(job: GameMediaAssetJob, expectedRevision: number, signal?: AbortSignal): Promise<GameMediaAssetSaveResult>;
	listPending(session: GameSessionKey, maximum: number, signal?: AbortSignal): Promise<readonly GameMediaAssetJob[]>;
}

export interface GameMediaAssetImportContext {
	job: GameMediaAssetJob;
	manifest: GameMediaAssetManifest;
	resources: GameMediaResourceStore;
	importOperationId: string;
}

export interface GameMediaAssetImporter {
	readonly id: string;
	import(context: GameMediaAssetImportContext, signal?: AbortSignal): Promise<GameMediaAssetImportReceipt>;
	reconcile(context: GameMediaAssetImportContext, signal?: AbortSignal): Promise<GameMediaAssetImportReceipt>;
}

export interface DurableGameMediaPipelineOptions {
	maximumOutputs?: number;
	maximumResourceBytes?: number;
	maximumAggregateResourceBytes?: number;
	settlementTimeoutMilliseconds?: number;
}

export interface DurableGameMediaToolOptions {
	definition: GameToolDefinition;
	pipeline: DurableGameMediaPipeline;
	importer: GameMediaAssetImporter;
	createRequest(
		call: GameToolCall,
		context: GameToolExecutionContext,
	): GameMediaAssetRequest | Promise<GameMediaAssetRequest>;
	projectResult?: (job: GameMediaAssetJob) => JsonObject;
	maximumModelResultCharacters?: number;
}

const statuses = new Set<GameMediaAssetStatus>([
	"prepared",
	"generating",
	"generation-uncertain",
	"generated",
	"importing",
	"import-uncertain",
	"completed",
	"rejected",
	"failed",
]);

const terminalStatuses = new Set<GameMediaAssetStatus>(["completed", "rejected", "failed"]);

function clone<T>(value: T): T {
	return structuredClone(value);
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

function canonical(value: JsonValue): string {
	if (value === null || typeof value !== "object") return JSON.stringify(value);
	if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
	return `{${Object.keys(value)
		.sort()
		.map((key) => `${JSON.stringify(key)}:${canonical(value[key] ?? null)}`)
		.join(",")}}`;
}

function digest(value: string | Uint8Array): string {
	return createHash("sha256").update(value).digest("hex");
}

function safeEqual(left: string, right: string): boolean {
	const a = Buffer.from(left);
	const b = Buffer.from(right);
	return a.byteLength === b.byteLength && timingSafeEqual(a, b);
}

function positiveInteger(value: number, name: string, minimum: number, maximum: number): number {
	if (!Number.isInteger(value) || value < minimum || value > maximum) throw new RangeError(`${name} is invalid.`);
	return value;
}

function validateSession(session: GameSessionKey): void {
	for (const [name, value] of Object.entries(session)) {
		if (name === "generation") {
			positiveInteger(value as number, "session.generation", 0, Number.MAX_SAFE_INTEGER);
			continue;
		}
		if (
			typeof value !== "string" ||
			value.length < 1 ||
			value.length > 512 ||
			[...value].some((character) => {
				const code = character.codePointAt(0) ?? 0;
				return code < 32 || code === 127;
			})
		) {
			throw new TypeError(`session.${name} is invalid.`);
		}
	}
}

function fingerprint(request: GameMediaAssetRequest): string {
	return digest(
		canonical({
			version: 1,
			operationId: request.operationId,
			session: { ...request.session },
			assetType: request.assetType,
			provider: request.provider,
			model: request.model,
			importerId: request.importerId,
			expectedRevision: request.expectedRevision,
			generation: {
				id: request.generation.id,
				kind: request.generation.kind,
				prompt: request.generation.prompt,
				parameters: request.generation.parameters ?? null,
				sources: request.generation.sources.map((source) => ({
					kind: source.kind,
					mimeType: source.mimeType,
					name: source.name ?? null,
					sha256: digest(source.data),
					bytes: source.data.byteLength,
				})),
			},
			metadata: request.metadata ?? null,
		}),
	);
}

function validateRequest(request: GameMediaAssetRequest): string {
	validateMediaIdentifier(request.operationId, "Media asset operation id");
	validateSession(request.session);
	validateMediaIdentifier(request.assetType, "Media asset type");
	validateMediaIdentifier(request.provider, "Media provider id");
	validateMediaIdentifier(request.model, "Media model id");
	validateMediaIdentifier(request.importerId, "Media importer id");
	positiveInteger(request.expectedRevision, "expectedRevision", 0, Number.MAX_SAFE_INTEGER);
	if (!sameSession(request.session, request.generation.session)) {
		throw new Error("Media generation session does not match the asset owner.");
	}
	return fingerprint(request);
}

function validateManifest(manifest: GameMediaAssetManifest, job: GameMediaAssetJob): void {
	validateMediaIdentifier(manifest.assetId, "Media asset id");
	if (manifest.provider !== job.provider || manifest.model !== job.model) {
		throw new Error("Media manifest provider identity does not match its job.");
	}
	if (manifest.resources.length < 1 || manifest.resources.length > 1_000) {
		throw new RangeError("Media asset manifest resource count is invalid.");
	}
	if (new Set(manifest.resources.map((resource) => resource.id)).size !== manifest.resources.length) {
		throw new Error("Media asset manifest resource identities must be unique.");
	}
}

function validateReceipt(receipt: GameMediaAssetImportReceipt, job: GameMediaAssetJob): void {
	if (receipt.operationId !== createGameMediaImportOperationId(job)) {
		throw new Error("Media import receipt operation does not match its job.");
	}
	if (!sameSession(receipt.session, job.session))
		throw new Error("Media import receipt session does not match its job.");
	if (receipt.expectedRevision !== job.expectedRevision) {
		throw new Error("Media import receipt expected revision does not match its job.");
	}
	if (!(["committed", "rejected", "failed", "uncertain"] as const).includes(receipt.status)) {
		throw new Error("Media import receipt status is invalid.");
	}
	if (
		receipt.stateRevision !== undefined &&
		(!Number.isSafeInteger(receipt.stateRevision) || receipt.stateRevision < 0)
	) {
		throw new Error("Media import receipt state revision is invalid.");
	}
}

function validateJob(job: GameMediaAssetJob): void {
	validateMediaIdentifier(job.operationId, "Media asset operation id");
	validateSession(job.session);
	if (!statuses.has(job.status)) throw new Error("Media asset status is invalid.");
	positiveInteger(job.revision, "revision", 1, Number.MAX_SAFE_INTEGER);
	positiveInteger(job.expectedRevision, "expectedRevision", 0, Number.MAX_SAFE_INTEGER);
	if (!/^[0-9a-f]{64}$/u.test(job.requestFingerprint)) throw new Error("Media asset fingerprint is invalid.");
	if (!Number.isSafeInteger(job.createdAt) || !Number.isSafeInteger(job.updatedAt) || job.updatedAt < job.createdAt) {
		throw new Error("Media asset timestamps are invalid.");
	}
	if (
		["generated", "importing", "import-uncertain", "completed", "rejected"].includes(job.status) &&
		job.manifest === undefined
	) {
		throw new Error("Media asset state requires a manifest.");
	}
	if (job.manifest) validateManifest(job.manifest, job);
	if (["completed", "rejected"].includes(job.status) && job.importReceipt === undefined) {
		throw new Error("Media asset terminal state requires an import receipt.");
	}
	if (job.importReceipt) validateReceipt(job.importReceipt, job);
	if (job.status === "completed" && job.importReceipt?.status !== "committed") {
		throw new Error("Completed media asset requires a committed import receipt.");
	}
	if (job.status === "rejected" && job.importReceipt?.status !== "rejected") {
		throw new Error("Rejected media asset requires a rejected import receipt.");
	}
}

function allowedTransition(previous: GameMediaAssetStatus, next: GameMediaAssetStatus): boolean {
	if (previous === next) return true;
	if (previous === "prepared") return next === "generating";
	if (previous === "generating") return next === "generated" || next === "generation-uncertain" || next === "failed";
	if (previous === "generation-uncertain") return next === "generated" || next === "failed";
	if (previous === "generated") return next === "importing";
	if (previous === "importing" || previous === "import-uncertain") {
		return next === "import-uncertain" || terminalStatuses.has(next);
	}
	return false;
}

function advance(
	job: GameMediaAssetJob,
	status: GameMediaAssetStatus,
	values: {
		manifest?: GameMediaAssetManifest;
		importReceipt?: GameMediaAssetImportReceipt;
		failure?: GameMediaAssetFailure;
	} = {},
): GameMediaAssetJob {
	const { failure: _previousFailure, ...current } = job;
	const next: GameMediaAssetJob = {
		...current,
		revision: job.revision + 1,
		status,
		updatedAt: Date.now(),
		...(values.manifest === undefined ? {} : { manifest: values.manifest }),
		...(values.importReceipt === undefined ? {} : { importReceipt: values.importReceipt }),
		...(values.failure === undefined ? {} : { failure: values.failure }),
	};
	validateJob(next);
	if (!allowedTransition(job.status, next.status)) throw new Error("Invalid media asset state transition.");
	return next;
}

function failure(category: string, message: string): GameMediaAssetFailure {
	validateMediaIdentifier(category, "Media failure category");
	return { category, message: message.slice(0, 1_024) };
}

export function createGameMediaImportOperationId(job: GameMediaAssetJob): string {
	return `media_import_v1_${digest(
		canonical({
			version: 1,
			session: { ...job.session },
			operationId: job.operationId,
			importerId: job.importerId,
		}),
	).slice(0, 40)}`;
}

export function createGameMediaAssetOperationId(
	context: Pick<GameToolExecutionContext, "input" | "runId" | "turn" | "toolCallIndex">,
	toolName: string,
): string {
	validateMediaIdentifier(toolName, "Media tool name");
	return `media_asset_v1_${digest(
		canonical({
			version: 1,
			session: { ...context.input.session },
			inputId: context.input.id,
			runId: context.runId,
			turn: context.turn,
			toolCallIndex: context.toolCallIndex,
			toolName,
		}),
	).slice(0, 40)}`;
}

export class InMemoryGameMediaAssetJobStore implements GameMediaAssetJobStore {
	private readonly jobs = new Map<string, GameMediaAssetJob>();
	private gate: Promise<void> = Promise.resolve();

	async read(
		session: GameSessionKey,
		operationId: string,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob | undefined> {
		return this.exclusive(() => {
			signal?.throwIfAborted();
			const job = this.jobs.get(operationId);
			return job && sameSession(job.session, session) ? clone(job) : undefined;
		});
	}

	async save(
		job: GameMediaAssetJob,
		expectedRevision: number,
		signal?: AbortSignal,
	): Promise<GameMediaAssetSaveResult> {
		return this.exclusive(() => {
			signal?.throwIfAborted();
			validateJob(job);
			const current = this.jobs.get(job.operationId);
			if (!current) {
				if (expectedRevision !== 0 || job.revision !== 1 || job.status !== "prepared") {
					throw new Error("A new media asset job must begin at prepared revision 1.");
				}
				this.jobs.set(job.operationId, clone(job));
				return { saved: true, current: clone(job) };
			}
			if (
				!sameSession(current.session, job.session) ||
				!safeEqual(current.requestFingerprint, job.requestFingerprint)
			) {
				throw new Error("Media asset operation is already bound to another request.");
			}
			if (current.revision !== expectedRevision) return { saved: false, current: clone(current) };
			if (job.revision !== expectedRevision + 1 || !allowedTransition(current.status, job.status)) {
				throw new Error("Media asset job transition is invalid.");
			}
			this.jobs.set(job.operationId, clone(job));
			return { saved: true, current: clone(job) };
		});
	}

	async listPending(
		session: GameSessionKey,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameMediaAssetJob[]> {
		positiveInteger(maximum, "maximum", 1, 256);
		return this.exclusive(() => {
			signal?.throwIfAborted();
			return [...this.jobs.values()]
				.filter((job) => sameSession(job.session, session) && !terminalStatuses.has(job.status))
				.sort((left, right) => left.createdAt - right.createdAt || left.operationId.localeCompare(right.operationId))
				.slice(0, maximum)
				.map(clone);
		});
	}

	private async exclusive<T>(action: () => T): Promise<T> {
		const previous = this.gate;
		let release = () => {};
		this.gate = new Promise<void>((resolve) => {
			release = resolve;
		});
		await previous;
		try {
			return action();
		} finally {
			release();
		}
	}
}

interface MediaJobRow {
	operation_id: string;
	job_json: string;
	revision: number;
	status: string;
}

export interface SqliteGameMediaAssetJobStoreOptions {
	maximumRecordBytes?: number;
}

export class SqliteGameMediaAssetJobStore implements GameMediaAssetJobStore, Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumRecordBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGameMediaAssetJobStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumRecordBytes = positiveInteger(
			options.maximumRecordBytes ?? 2_000_000,
			"maximumRecordBytes",
			1_024,
			16_000_000,
		);
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_media_asset_jobs (
				operation_id TEXT PRIMARY KEY,
				world_id TEXT NOT NULL,
				save_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL,
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				revision INTEGER NOT NULL,
				status TEXT NOT NULL,
				created_at INTEGER NOT NULL,
				job_json TEXT NOT NULL
			) STRICT;
			CREATE INDEX IF NOT EXISTS ix_game_media_asset_pending
			ON game_media_asset_jobs(world_id, save_id, timeline_id, generation, owner_id, session_id, actor_id, status, created_at);
		`);
	}

	async read(
		session: GameSessionKey,
		operationId: string,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob | undefined> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		validateMediaIdentifier(operationId, "Media asset operation id");
		const row = this.database
			.prepare(`SELECT operation_id,job_json,revision,status FROM game_media_asset_jobs
				WHERE operation_id=? AND world_id=? AND save_id=? AND timeline_id=? AND generation=?
				AND owner_id=? AND session_id=? AND actor_id=?`)
			.get(
				operationId,
				session.worldId,
				session.saveId,
				session.timelineId,
				session.generation,
				session.ownerId,
				session.sessionId,
				session.actorId,
			) as MediaJobRow | undefined;
		return row ? this.decode(row) : undefined;
	}

	async save(
		job: GameMediaAssetJob,
		expectedRevision: number,
		signal?: AbortSignal,
	): Promise<GameMediaAssetSaveResult> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateJob(job);
		const json = JSON.stringify(job);
		if (Buffer.byteLength(json) > this.maximumRecordBytes)
			throw new RangeError("Media asset job exceeds its record limit.");
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const row = this.readRow(job.operationId);
			if (!row) {
				if (expectedRevision !== 0 || job.revision !== 1 || job.status !== "prepared") {
					throw new Error("A new media asset job must begin at prepared revision 1.");
				}
				this.database
					.prepare(`INSERT INTO game_media_asset_jobs(
					operation_id,world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,
					revision,status,created_at,job_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)`)
					.run(
						job.operationId,
						job.session.worldId,
						job.session.saveId,
						job.session.timelineId,
						job.session.generation,
						job.session.ownerId,
						job.session.sessionId,
						job.session.actorId,
						job.revision,
						job.status,
						job.createdAt,
						json,
					);
				this.database.exec("COMMIT");
				return { saved: true, current: clone(job) };
			}
			const current = this.decode(row);
			if (
				!sameSession(current.session, job.session) ||
				!safeEqual(current.requestFingerprint, job.requestFingerprint)
			) {
				throw new Error("Media asset operation is already bound to another request.");
			}
			if (current.revision !== expectedRevision) {
				this.database.exec("COMMIT");
				return { saved: false, current };
			}
			if (job.revision !== expectedRevision + 1 || !allowedTransition(current.status, job.status)) {
				throw new Error("Media asset job transition is invalid.");
			}
			this.database
				.prepare("UPDATE game_media_asset_jobs SET revision=?,status=?,job_json=? WHERE operation_id=?")
				.run(job.revision, job.status, json, job.operationId);
			this.database.exec("COMMIT");
			return { saved: true, current: clone(job) };
		} catch (error) {
			try {
				this.database.exec("ROLLBACK");
			} catch {
				// Preserve the original bounded persistence failure.
			}
			throw error;
		}
	}

	async listPending(
		session: GameSessionKey,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameMediaAssetJob[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		positiveInteger(maximum, "maximum", 1, 256);
		const rows = this.database
			.prepare(`SELECT operation_id,job_json,revision,status FROM game_media_asset_jobs
				WHERE world_id=? AND save_id=? AND timeline_id=? AND generation=? AND owner_id=? AND session_id=? AND actor_id=?
				AND status NOT IN ('completed','rejected','failed') ORDER BY created_at,operation_id LIMIT ?`)
			.all(
				session.worldId,
				session.saveId,
				session.timelineId,
				session.generation,
				session.ownerId,
				session.sessionId,
				session.actorId,
				maximum,
			) as unknown as MediaJobRow[];
		return rows.map((row) => this.decode(row));
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	[Symbol.dispose](): void {
		this.close();
	}

	private readRow(operationId: string): MediaJobRow | undefined {
		return this.database
			.prepare("SELECT operation_id,job_json,revision,status FROM game_media_asset_jobs WHERE operation_id=?")
			.get(operationId) as MediaJobRow | undefined;
	}

	private decode(row: MediaJobRow): GameMediaAssetJob {
		try {
			const job = JSON.parse(row.job_json) as GameMediaAssetJob;
			validateJob(job);
			if (job.operationId !== row.operation_id || job.revision !== row.revision || job.status !== row.status) {
				throw new Error("Stored media asset job identity is corrupt.");
			}
			return job;
		} catch {
			throw new Error("Stored media asset job is corrupt.");
		}
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("Media asset job store is closed.");
	}
}

export class DurableGameMediaPipeline {
	private readonly maximumOutputs: number;
	private readonly maximumResourceBytes: number;
	private readonly maximumAggregateResourceBytes: number;
	private readonly settlementTimeoutMilliseconds: number;

	constructor(
		private readonly registry: GameMediaRegistry,
		private readonly jobs: GameMediaAssetJobStore,
		private readonly resources: GameMediaResourceStore,
		options: DurableGameMediaPipelineOptions = {},
	) {
		this.maximumOutputs = positiveInteger(options.maximumOutputs ?? 32, "maximumOutputs", 1, 1_000);
		this.maximumResourceBytes = positiveInteger(
			options.maximumResourceBytes ?? 64 * 1024 * 1024,
			"maximumResourceBytes",
			1,
			512 * 1024 * 1024,
		);
		this.maximumAggregateResourceBytes = positiveInteger(
			options.maximumAggregateResourceBytes ?? 256 * 1024 * 1024,
			"maximumAggregateResourceBytes",
			this.maximumResourceBytes,
			2_000_000_000,
		);
		this.settlementTimeoutMilliseconds = positiveInteger(
			options.settlementTimeoutMilliseconds ?? 10_000,
			"settlementTimeoutMilliseconds",
			100,
			300_000,
		);
	}

	async execute(
		request: GameMediaAssetRequest,
		importer: GameMediaAssetImporter,
		onProgress?: (progress: GameMediaGenerationProgress) => void | Promise<void>,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		const requestFingerprint = validateRequest(request);
		if (importer.id !== request.importerId) throw new Error("Media importer does not match the request.");
		let job = await this.reserve(request, requestFingerprint, signal);
		if (job.status !== "prepared") return this.continueImport(job, importer, false, signal);
		const claimed = await this.jobs.save(advance(job, "generating"), job.revision, signal);
		if (!claimed.saved) return claimed.current;
		job = claimed.current;

		let generated: GameMediaGenerationResult;
		try {
			generated = await this.registry.generate(request.provider, request.model, request.generation, onProgress, signal);
		} catch (error) {
			await this.settle(
				advance(job, "generation-uncertain", {
					failure: failure(
						signal?.aborted ? "generation-cancelled-after-dispatch" : "generation-outcome-uncertain",
						"Generation was dispatched but its final outcome is not authoritative; it must not be replayed blindly.",
					),
				}),
				job.revision,
			);
			if (signal?.aborted) throw error;
			return (await this.jobs.read(request.session, request.operationId)) ?? job;
		}

		let manifest: GameMediaAssetManifest;
		try {
			manifest = await this.persistOutputs(job, generated, signal);
		} catch (error) {
			await this.settle(
				advance(job, "generation-uncertain", {
					failure: failure(
						signal?.aborted ? "asset-persistence-cancelled" : "asset-persistence-uncertain",
						"Generated output could not be settled; inspect this operation before any provider retry.",
					),
				}),
				job.revision,
			);
			if (signal?.aborted) throw error;
			return (await this.jobs.read(request.session, request.operationId)) ?? job;
		}

		const saved = await this.jobs.save(advance(job, "generated", { manifest }), job.revision, signal);
		return saved.saved ? this.continueImport(saved.current, importer, false, signal) : saved.current;
	}

	async read(
		session: GameSessionKey,
		operationId: string,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob | undefined> {
		return this.jobs.read(session, operationId, signal);
	}

	async resumeImport(
		session: GameSessionKey,
		operationId: string,
		importer: GameMediaAssetImporter,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		const job = await this.require(session, operationId, signal);
		if (job.importerId !== importer.id) throw new Error("Media importer does not match the prepared job.");
		return this.continueImport(job, importer, true, signal);
	}

	async resolveGeneration(
		session: GameSessionKey,
		operationId: string,
		result: GameMediaGenerationResult,
		importer: GameMediaAssetImporter,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		const job = await this.require(session, operationId, signal);
		if (job.status !== "generating" && job.status !== "generation-uncertain") {
			throw new Error("Media asset job is not awaiting generation reconciliation.");
		}
		if (job.importerId !== importer.id) throw new Error("Media importer does not match the prepared job.");
		const manifest = await this.persistOutputs(job, result, signal);
		const saved = await this.jobs.save(advance(job, "generated", { manifest }), job.revision, signal);
		return saved.saved ? this.continueImport(saved.current, importer, false, signal) : saved.current;
	}

	async failGeneration(
		session: GameSessionKey,
		operationId: string,
		category: string,
		message: string,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		const job = await this.require(session, operationId, signal);
		if (job.status !== "generating" && job.status !== "generation-uncertain") {
			throw new Error("Media asset job is not awaiting generation reconciliation.");
		}
		const saved = await this.jobs.save(
			advance(job, "failed", { failure: failure(category, message) }),
			job.revision,
			signal,
		);
		return saved.current;
	}

	private async reserve(
		request: GameMediaAssetRequest,
		requestFingerprint: string,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		const current = await this.jobs.read(request.session, request.operationId, signal);
		if (current) {
			if (!safeEqual(current.requestFingerprint, requestFingerprint)) {
				throw new Error("Media asset operation is already bound to another request.");
			}
			return current;
		}
		const now = Date.now();
		const prepared: GameMediaAssetJob = {
			operationId: request.operationId,
			session: clone(request.session),
			assetType: request.assetType,
			provider: request.provider,
			model: request.model,
			importerId: request.importerId,
			expectedRevision: request.expectedRevision,
			mediaKind: request.generation.kind,
			requestFingerprint,
			revision: 1,
			status: "prepared",
			createdAt: now,
			updatedAt: now,
		};
		const saved = await this.jobs.save(prepared, 0, signal);
		if (!safeEqual(saved.current.requestFingerprint, requestFingerprint)) {
			throw new Error("Media asset operation is already bound to another request.");
		}
		return saved.current;
	}

	private async persistOutputs(
		job: GameMediaAssetJob,
		result: GameMediaGenerationResult,
		signal?: AbortSignal,
	): Promise<GameMediaAssetManifest> {
		if (result.provider !== job.provider || result.model !== job.model) {
			throw new Error("Generated media provider identity does not match the prepared job.");
		}
		if (result.outputs.length < 1 || result.outputs.length > this.maximumOutputs) {
			throw new RangeError("Generated media output count is invalid.");
		}
		let aggregate = 0;
		const resources: GameMediaResource[] = [];
		for (const output of result.outputs) {
			signal?.throwIfAborted();
			if (output.kind !== job.mediaKind) throw new Error("Generated media kind does not match the prepared job.");
			if (output.data.byteLength > this.maximumResourceBytes)
				throw new RangeError("Generated media output is too large.");
			aggregate += output.data.byteLength;
			if (aggregate > this.maximumAggregateResourceBytes)
				throw new RangeError("Generated media outputs are too large.");
			resources.push(await this.resources.save(output, signal));
		}
		return {
			assetId: `media_asset_v1_${digest(`${job.session.sessionId}\n${job.session.actorId}\n${job.operationId}`).slice(0, 40)}`,
			resources,
			provider: result.provider,
			model: result.model,
			...(result.responseId === undefined ? {} : { responseId: result.responseId }),
			...(result.usage === undefined ? {} : { usage: clone(result.usage) }),
		};
	}

	private async continueImport(
		job: GameMediaAssetJob,
		importer: GameMediaAssetImporter,
		allowReconcile: boolean,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		if (terminalStatuses.has(job.status) || ["prepared", "generating", "generation-uncertain"].includes(job.status)) {
			return job;
		}
		if (!job.manifest) throw new Error("Media asset job is missing its manifest.");
		if (job.status === "generated") {
			const claimed = await this.jobs.save(advance(job, "importing"), job.revision, signal);
			if (!claimed.saved) return claimed.current;
			return this.invokeImporter(claimed.current, importer, false, signal);
		}
		if ((job.status === "importing" || job.status === "import-uncertain") && allowReconcile) {
			return this.invokeImporter(job, importer, true, signal);
		}
		return job;
	}

	private async invokeImporter(
		job: GameMediaAssetJob,
		importer: GameMediaAssetImporter,
		reconcile: boolean,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		if (!job.manifest) throw new Error("Media asset job is missing its manifest.");
		const context: GameMediaAssetImportContext = {
			job,
			manifest: job.manifest,
			resources: this.resources,
			importOperationId: createGameMediaImportOperationId(job),
		};
		let receipt: GameMediaAssetImportReceipt;
		try {
			receipt = reconcile ? await importer.reconcile(context, signal) : await importer.import(context, signal);
			validateReceipt(receipt, job);
		} catch (error) {
			await this.settle(
				advance(job, "import-uncertain", {
					failure: failure(
						signal?.aborted ? "import-cancelled-after-dispatch" : "import-outcome-uncertain",
						"Import may have reached the authoritative game; reconcile the stable import operation before retrying.",
					),
				}),
				job.revision,
			);
			if (signal?.aborted) throw error;
			return (await this.jobs.read(job.session, job.operationId)) ?? job;
		}
		const status: GameMediaAssetStatus =
			receipt.status === "committed"
				? "completed"
				: receipt.status === "rejected"
					? "rejected"
					: receipt.status === "failed"
						? "failed"
						: "import-uncertain";
		const saved = await this.settle(advance(job, status, { importReceipt: receipt }), job.revision);
		return saved;
	}

	private async settle(job: GameMediaAssetJob, expectedRevision: number): Promise<GameMediaAssetJob> {
		const saved = await this.jobs.save(job, expectedRevision, AbortSignal.timeout(this.settlementTimeoutMilliseconds));
		return saved.current;
	}

	private async require(
		session: GameSessionKey,
		operationId: string,
		signal?: AbortSignal,
	): Promise<GameMediaAssetJob> {
		const job = await this.jobs.read(session, operationId, signal);
		if (!job) throw new Error("Media asset job does not exist for this session.");
		return job;
	}
}

function defaultModelProjection(job: GameMediaAssetJob): JsonObject {
	return {
		status: job.status,
		assetType: job.assetType,
		...(job.manifest === undefined
			? {}
			: {
					resources: job.manifest.resources.map((resource) => ({
						kind: resource.kind,
						mimeType: resource.mimeType,
						bytes: resource.bytes,
						...(resource.name === undefined ? {} : { name: resource.name }),
					})),
				}),
		...(job.importReceipt === undefined ? {} : { importStatus: job.importReceipt.status }),
		...(job.failure === undefined ? {} : { failure: job.failure.category }),
	};
}

function boundedProjection(value: unknown, maximumCharacters: number): JsonObject | undefined {
	try {
		const json = JSON.stringify(value);
		if (json.length > maximumCharacters) return undefined;
		const parsed = JSON.parse(json) as unknown;
		return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as JsonObject) : undefined;
	} catch {
		return undefined;
	}
}

/**
 * Exposes durable media generation/import as an ordinary model-callable Tool.
 * Canonical job coordinates remain in `details`; model-visible content receives
 * only a bounded semantic projection.
 */
export function createDurableGameMediaTool(options: DurableGameMediaToolOptions): GameTool {
	const maximumCharacters = positiveInteger(
		options.maximumModelResultCharacters ?? 64_000,
		"maximumModelResultCharacters",
		64,
		1_000_000,
	);
	return {
		definition: options.definition,
		async execute(call, context) {
			if (call.name !== options.definition.name) throw new Error("Tool call name does not match the media definition.");
			context.signal.throwIfAborted();
			const request = await options.createRequest(call, context);
			if (!sameSession(request.session, context.input.session)) {
				throw new Error("Media Tool request changed the authoritative input session.");
			}
			const job = await options.pipeline.execute(request, options.importer, undefined, context.signal);
			let projected: JsonObject | undefined;
			try {
				projected = boundedProjection(options.projectResult?.(job) ?? defaultModelProjection(job), maximumCharacters);
			} catch {
				projected = undefined;
			}
			if (!projected) {
				return {
					content: [{ type: "json", value: { status: "projection-failed" } }],
					details: JSON.parse(JSON.stringify(job)) as JsonValue,
					isError: true,
				};
			}
			return {
				content: [{ type: "json", value: projected }],
				details: JSON.parse(JSON.stringify(job)) as JsonValue,
				isError: job.status !== "completed",
			};
		},
	};
}
