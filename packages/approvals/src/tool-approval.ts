import { createHash, randomBytes } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type {
	GameInput,
	GameSessionKey,
	GameToolCall,
	GameToolDefinition,
	GameToolExecutionContext,
	GameToolResult,
	JsonObject,
} from "@opengameagent/protocol";
import type { GameToolExecutionMiddleware } from "@opengameagent/runtime";

export type GameToolApprovalMode = "disabled" | "explicit-only" | "confirm-once" | "allowed-in-task";
export type GameToolRisk = NonNullable<GameToolDefinition["risk"]>;
export type GameToolApprovalStatus =
	| "pending"
	| "approved"
	| "denied"
	| "timed-out"
	| "cancelled"
	| "consumed"
	| "expired";

export interface GameToolApprovalRule {
	id: string;
	mode: GameToolApprovalMode;
	toolName?: string;
	minimumRisk?: GameToolRisk;
}

export interface GameToolInvocationScope {
	explicitlyRequestedTools?: readonly string[];
	taskId?: string;
	taskAllowedTools?: readonly string[];
}

export interface GameToolInvocationScopeProvider {
	resolve(
		input: GameInput,
		tool: GameToolDefinition,
		call: GameToolCall,
		context: GameToolExecutionContext,
	): Promise<GameToolInvocationScope> | GameToolInvocationScope;
}

export interface GameToolApprovalWorldState {
	generationId: string;
	revision: number;
}

export interface GameToolApprovalWorldStateProvider {
	read(input: GameInput, signal: AbortSignal): Promise<GameToolApprovalWorldState> | GameToolApprovalWorldState;
}

export interface GameToolApprovalRequest {
	approvalId: string;
	policyId: string;
	session: GameSessionKey;
	inputId: string;
	runId: string;
	turn: number;
	toolCallIndex: number;
	toolCallId: string;
	toolName: string;
	risk: GameToolRisk;
	canonicalArguments: string;
	argumentsDigest: string;
	world: GameToolApprovalWorldState;
	taskId?: string;
	requestedAt: number;
	expiresAt: number;
}

export interface GameToolApprovalRecord {
	request: GameToolApprovalRequest;
	status: GameToolApprovalStatus;
	revision: number;
	updatedAt: number;
	reason?: string;
}

export interface GameToolApprovalResponse {
	session: GameSessionKey;
	approvalId: string;
	expectedRevision: number;
	decision: "approve" | "deny";
	reason?: string;
}

export interface GameToolApprovalEvent {
	approvalId: string;
	session: GameSessionKey;
	inputId: string;
	runId: string;
	turn: number;
	toolCallId: string;
	toolName: string;
	status: GameToolApprovalStatus;
	waitMilliseconds: number;
}

export interface GameToolApprovalStore {
	create(request: GameToolApprovalRequest, signal?: AbortSignal): Promise<GameToolApprovalRecord>;
	read(session: GameSessionKey, approvalId: string, signal?: AbortSignal): Promise<GameToolApprovalRecord | undefined>;
	listPending(
		session: GameSessionKey,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameToolApprovalRecord[]>;
	respond(
		response: GameToolApprovalResponse,
		credentialDigest: string | undefined,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord>;
	consume(
		request: GameToolApprovalRequest,
		credentialDigest: string,
		expectedRevision: number,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord>;
	settle(
		request: GameToolApprovalRequest,
		expectedRevision: number,
		status: "timed-out" | "cancelled" | "expired",
		reason: string,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord>;
}

interface ApprovalRow {
	revision: number;
	status: string;
	request_json: string;
	updated_at: number;
	reason: string | null;
	credential_digest: string | null;
}

const riskRank: Record<GameToolRisk, number> = { read: 0, low: 1, medium: 2, high: 3, critical: 4 };
const settledStatuses = new Set<GameToolApprovalStatus>(["denied", "timed-out", "cancelled", "consumed", "expired"]);

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

function sessionWhere(): string {
	return "world_id=? AND save_id=? AND timeline_id=? AND generation=? AND owner_id=? AND session_id=? AND actor_id=?";
}

function boundedId(value: string, name: string, maximum = 192): string {
	const containsControl = [...value].some((character) => {
		const code = character.codePointAt(0) ?? 0;
		return code < 32 || code === 127;
	});
	if (!value || value.length > maximum || containsControl) {
		throw new TypeError(`${name} must be a bounded non-empty identifier.`);
	}
	return value;
}

function canonicalJson(value: JsonObject, maximumBytes = 1_000_000): string {
	const visit = (item: unknown, depth: number): unknown => {
		if (depth > 128) throw new RangeError("Tool arguments exceed the maximum JSON depth.");
		if (item === null || typeof item === "string" || typeof item === "boolean") return item;
		if (typeof item === "number") {
			if (!Number.isFinite(item)) throw new TypeError("Tool arguments contain a non-finite number.");
			return item;
		}
		if (Array.isArray(item)) return item.map((child) => visit(child, depth + 1));
		if (typeof item === "object") {
			return Object.fromEntries(
				Object.entries(item as Record<string, unknown>)
					.sort(([left], [right]) => left.localeCompare(right))
					.map(([key, child]) => [key, visit(child, depth + 1)]),
			);
		}
		throw new TypeError("Tool arguments are not valid JSON.");
	};
	const result = JSON.stringify(visit(value, 0));
	if (Buffer.byteLength(result, "utf8") > maximumBytes)
		throw new RangeError("Tool arguments exceed the approval limit.");
	return result;
}

function digest(value: string): string {
	return createHash("sha256").update(value).digest("base64url");
}

function sameRequest(left: GameToolApprovalRequest, right: GameToolApprovalRequest): boolean {
	return JSON.stringify(left) === JSON.stringify(right);
}

function parseRecord(row: ApprovalRow): GameToolApprovalRecord {
	const request = JSON.parse(row.request_json) as GameToolApprovalRequest;
	if (!request.approvalId || !request.session || !request.toolName)
		throw new Error("Tool approval storage is corrupt.");
	return {
		request,
		status: row.status as GameToolApprovalStatus,
		revision: row.revision,
		updatedAt: row.updated_at,
		...(row.reason === null ? {} : { reason: row.reason }),
	};
}

export interface SqliteGameToolApprovalStoreOptions {
	maximumRecordsPerSession?: number;
	maximumPendingPerSession?: number;
}

export class SqliteGameToolApprovalStore implements GameToolApprovalStore, Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumRecordsPerSession: number;
	private readonly maximumPendingPerSession: number;
	private closed = false;

	constructor(path: string, options: SqliteGameToolApprovalStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumRecordsPerSession = options.maximumRecordsPerSession ?? 512;
		this.maximumPendingPerSession = options.maximumPendingPerSession ?? 64;
		if (!Number.isInteger(this.maximumRecordsPerSession) || this.maximumRecordsPerSession < 16)
			throw new RangeError("maximumRecordsPerSession must be at least 16.");
		if (!Number.isInteger(this.maximumPendingPerSession) || this.maximumPendingPerSession < 1)
			throw new RangeError("maximumPendingPerSession must be positive.");
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`CREATE TABLE IF NOT EXISTS game_tool_approvals (
			approval_id TEXT PRIMARY KEY, world_id TEXT NOT NULL, save_id TEXT NOT NULL, timeline_id TEXT NOT NULL,
			generation INTEGER NOT NULL, owner_id TEXT NOT NULL, session_id TEXT NOT NULL, actor_id TEXT NOT NULL,
			revision INTEGER NOT NULL, status TEXT NOT NULL, request_json TEXT NOT NULL, updated_at INTEGER NOT NULL,
			reason TEXT, credential_digest TEXT
		) STRICT;
		CREATE INDEX IF NOT EXISTS game_tool_approvals_owner ON game_tool_approvals(
			world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,status,updated_at);`);
	}

	async create(request: GameToolApprovalRequest, signal?: AbortSignal): Promise<GameToolApprovalRecord> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.readRow(request.session, request.approvalId);
			if (existing) {
				const record = parseRecord(existing);
				if (!sameRequest(record.request, request)) throw new Error("Approval id identifies a different request.");
				this.database.exec("COMMIT");
				return structuredClone(record);
			}
			const count = this.database
				.prepare(
					`SELECT COUNT(*) AS count FROM game_tool_approvals WHERE ${sessionWhere()} AND status IN ('pending','approved')`,
				)
				.get(...sessionValues(request.session)) as unknown as { count: number };
			if (count.count >= this.maximumPendingPerSession) throw new Error("Pending approval capacity is exhausted.");
			this.database
				.prepare(`INSERT INTO game_tool_approvals VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
				.run(
					request.approvalId,
					...sessionValues(request.session),
					1,
					"pending",
					JSON.stringify(request),
					request.requestedAt,
					null,
					null,
				);
			this.prune(request.session);
			this.database.exec("COMMIT");
			return { request: structuredClone(request), status: "pending", revision: 1, updatedAt: request.requestedAt };
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async read(
		session: GameSessionKey,
		approvalId: string,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord | undefined> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const row = this.readRow(session, approvalId);
		return row ? structuredClone(parseRecord(row)) : undefined;
	}

	async listPending(
		session: GameSessionKey,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameToolApprovalRecord[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		if (!Number.isInteger(maximum) || maximum < 1 || maximum > 256) throw new RangeError("maximum must be 1..256.");
		return (
			this.database
				.prepare(`SELECT revision,status,request_json,updated_at,reason,credential_digest FROM game_tool_approvals
					WHERE ${sessionWhere()} AND status='pending' ORDER BY updated_at,approval_id LIMIT ?`)
				.all(...sessionValues(session), maximum) as unknown as ApprovalRow[]
		).map((row) => structuredClone(parseRecord(row)));
	}

	async respond(
		response: GameToolApprovalResponse,
		credentialDigest: string | undefined,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord> {
		return this.update(
			response.session,
			response.approvalId,
			response.expectedRevision,
			["pending"],
			() => ({
				status: response.decision === "approve" ? "approved" : "denied",
				...(response.reason === undefined ? {} : { reason: response.reason.slice(0, 4096) }),
				...(credentialDigest === undefined ? {} : { credentialDigest }),
			}),
			signal,
		);
	}

	async consume(
		request: GameToolApprovalRequest,
		credentialDigest: string,
		expectedRevision: number,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord> {
		return this.update(
			request.session,
			request.approvalId,
			expectedRevision,
			["approved"],
			(current, storedDigest) => {
				if (!sameRequest(current.request, request) || !storedDigest || storedDigest !== credentialDigest)
					throw new Error("Approval credential is invalid or stale.");
				return { status: "consumed" };
			},
			signal,
		);
	}

	async settle(
		request: GameToolApprovalRequest,
		expectedRevision: number,
		status: "timed-out" | "cancelled" | "expired",
		reason: string,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord> {
		return this.update(
			request.session,
			request.approvalId,
			expectedRevision,
			["pending", "approved"],
			(current) => {
				if (!sameRequest(current.request, request)) throw new Error("Approval request is stale.");
				return { status, reason: reason.slice(0, 4096) };
			},
			signal,
		);
	}

	[Symbol.dispose](): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	private async update(
		session: GameSessionKey,
		approvalId: string,
		expectedRevision: number,
		allowed: readonly GameToolApprovalStatus[],
		mutate: (
			current: GameToolApprovalRecord,
			credentialDigest: string | null,
		) => {
			status: GameToolApprovalStatus;
			reason?: string;
			credentialDigest?: string;
		},
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const row = this.readRow(session, approvalId);
			if (!row) throw new Error("Approval was not found.");
			const current = parseRecord(row);
			if (current.revision !== expectedRevision || !allowed.includes(current.status))
				throw new Error("Approval revision or status conflict.");
			const change = mutate(current, row.credential_digest);
			const updatedAt = Date.now();
			const result = this.database
				.prepare(`UPDATE game_tool_approvals SET revision=revision+1,status=?,updated_at=?,reason=?,credential_digest=?
					WHERE approval_id=? AND ${sessionWhere()} AND revision=?`)
				.run(
					change.status,
					updatedAt,
					change.reason ?? null,
					change.credentialDigest ?? row.credential_digest,
					approvalId,
					...sessionValues(session),
					expectedRevision,
				);
			if (result.changes !== 1) throw new Error("Approval revision conflict.");
			this.database.exec("COMMIT");
			return {
				request: current.request,
				status: change.status,
				revision: expectedRevision + 1,
				updatedAt,
				...(change.reason === undefined ? {} : { reason: change.reason }),
			};
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	private readRow(session: GameSessionKey, approvalId: string): ApprovalRow | undefined {
		return this.database
			.prepare(`SELECT revision,status,request_json,updated_at,reason,credential_digest FROM game_tool_approvals
				WHERE approval_id=? AND ${sessionWhere()}`)
			.get(approvalId, ...sessionValues(session)) as unknown as ApprovalRow | undefined;
	}

	private prune(session: GameSessionKey): void {
		this.database
			.prepare(`DELETE FROM game_tool_approvals WHERE approval_id IN (
				SELECT approval_id FROM game_tool_approvals WHERE ${sessionWhere()}
					AND status IN ('denied','timed-out','cancelled','consumed','expired')
					ORDER BY updated_at DESC,approval_id DESC LIMIT -1 OFFSET ?)`)
			.run(...sessionValues(session), this.maximumRecordsPerSession);
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("Tool approval store is closed.");
	}
}

interface ApprovalWaiter {
	resolve(value: { record: GameToolApprovalRecord; credential?: string }): void;
	reject(error: unknown): void;
}

export class GameToolApprovalBroker {
	private readonly waiters = new Map<string, Set<ApprovalWaiter>>();
	private readonly credentials = new Map<string, string>();

	constructor(private readonly store: GameToolApprovalStore) {}

	listPending(
		session: GameSessionKey,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameToolApprovalRecord[]> {
		return this.store.listPending(session, maximum, signal);
	}

	prepare(request: GameToolApprovalRequest, signal?: AbortSignal): Promise<GameToolApprovalRecord> {
		return this.store.create(request, signal);
	}

	async respond(response: GameToolApprovalResponse, signal?: AbortSignal): Promise<GameToolApprovalRecord> {
		const credential = response.decision === "approve" ? randomBytes(32).toString("base64url") : undefined;
		if (credential !== undefined) this.credentials.set(response.approvalId, credential);
		let record: GameToolApprovalRecord;
		try {
			record = await this.store.respond(response, credential === undefined ? undefined : digest(credential), signal);
		} catch (error) {
			if (credential !== undefined) this.credentials.delete(response.approvalId);
			throw error;
		}
		this.resolve(record.request.approvalId, { record, ...(credential === undefined ? {} : { credential }) });
		return record;
	}

	async waitForDecision(
		request: GameToolApprovalRequest,
		signal: AbortSignal,
	): Promise<{ record: GameToolApprovalRecord; credential?: string }> {
		const record = await this.store.create(request, signal);
		if (record.status !== "pending") {
			const credential = this.credentials.get(request.approvalId);
			return { record, ...(credential === undefined ? {} : { credential }) };
		}
		return new Promise((resolve, reject) => {
			const waiter: ApprovalWaiter = { resolve, reject };
			const set = this.waiters.get(request.approvalId) ?? new Set<ApprovalWaiter>();
			set.add(waiter);
			this.waiters.set(request.approvalId, set);
			const timeout = setTimeout(
				() => {
					this.removeWaiter(request.approvalId, waiter);
					void this.store
						.settle(request, record.revision, "timed-out", "Approval timed out.")
						.then((settled) => resolve({ record: settled }), reject);
				},
				Math.max(1, request.expiresAt - Date.now()),
			);
			const abort = () => {
				clearTimeout(timeout);
				this.removeWaiter(request.approvalId, waiter);
				void this.store
					.settle(request, record.revision, "cancelled", "Approval wait was cancelled.")
					.then(() => reject(signal.reason ?? new Error("Approval wait was cancelled.")), reject);
			};
			signal.addEventListener("abort", abort, { once: true });
			const finish = waiter.resolve;
			waiter.resolve = (value) => {
				clearTimeout(timeout);
				signal.removeEventListener("abort", abort);
				finish(value);
			};
			void this.store.read(request.session, request.approvalId, signal).then(
				(latest) => {
					if (!latest || latest.status === "pending") return;
					const credential = this.credentials.get(request.approvalId);
					this.resolve(request.approvalId, {
						record: latest,
						...(credential === undefined ? {} : { credential }),
					});
				},
				(error) => waiter.reject(error),
			);
		});
	}

	consume(
		request: GameToolApprovalRequest,
		credential: string,
		expectedRevision: number,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord> {
		this.credentials.delete(request.approvalId);
		return this.store.consume(request, digest(credential), expectedRevision, signal);
	}

	settle(
		request: GameToolApprovalRequest,
		expectedRevision: number,
		status: "timed-out" | "cancelled" | "expired",
		reason: string,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord> {
		this.credentials.delete(request.approvalId);
		return this.store.settle(request, expectedRevision, status, reason, signal);
	}

	private resolve(approvalId: string, value: { record: GameToolApprovalRecord; credential?: string }): void {
		const waiters = this.waiters.get(approvalId);
		this.waiters.delete(approvalId);
		for (const waiter of waiters ?? []) waiter.resolve(value);
	}

	private removeWaiter(approvalId: string, waiter: ApprovalWaiter): void {
		const set = this.waiters.get(approvalId);
		set?.delete(waiter);
		if (set?.size === 0) this.waiters.delete(approvalId);
	}
}

export class GameToolApprovalDeniedError extends Error {
	constructor(
		message: string,
		readonly status: GameToolApprovalStatus,
	) {
		super(message);
		this.name = "GameToolApprovalDeniedError";
	}
}

export interface GameToolApprovalMiddlewareOptions {
	rules: readonly GameToolApprovalRule[];
	broker: GameToolApprovalBroker;
	worldState: GameToolApprovalWorldStateProvider;
	scope?: GameToolInvocationScopeProvider;
	timeoutMilliseconds?: number;
	onEvent?: (event: GameToolApprovalEvent) => Promise<void> | void;
}

export class GameToolApprovalMiddleware implements GameToolExecutionMiddleware {
	private readonly rules: readonly GameToolApprovalRule[];
	private readonly timeoutMilliseconds: number;

	constructor(private readonly options: GameToolApprovalMiddlewareOptions) {
		this.rules = options.rules.map((rule) => {
			boundedId(rule.id, "Approval rule id");
			if (rule.toolName === undefined && rule.minimumRisk === undefined)
				throw new TypeError("An approval rule must match a tool name, a minimum risk, or both.");
			if (rule.toolName !== undefined) boundedId(rule.toolName, "Tool name", 128);
			return structuredClone(rule);
		});
		if (new Set(this.rules.map((rule) => rule.id)).size !== this.rules.length)
			throw new TypeError("Approval rule ids must be unique.");
		this.timeoutMilliseconds = options.timeoutMilliseconds ?? 120_000;
		if (
			!Number.isInteger(this.timeoutMilliseconds) ||
			this.timeoutMilliseconds < 1 ||
			this.timeoutMilliseconds > 3_600_000
		)
			throw new RangeError("timeoutMilliseconds must be 1..3600000.");
	}

	async execute(
		tool: GameToolDefinition,
		call: GameToolCall,
		context: GameToolExecutionContext,
		next: () => Promise<GameToolResult>,
	): Promise<GameToolResult> {
		const risk = tool.risk ?? "low";
		const rule = this.rules.find(
			(candidate) =>
				(candidate.toolName === undefined || candidate.toolName === tool.name) &&
				(candidate.minimumRisk === undefined || riskRank[risk] >= riskRank[candidate.minimumRisk]),
		);
		if (!rule) return next();
		const scope = (await this.options.scope?.resolve(context.input, tool, call, context)) ?? {};
		const explicit = new Set(scope.explicitlyRequestedTools ?? []);
		const taskAllowed = new Set(scope.taskAllowedTools ?? []);
		if (rule.mode === "disabled") throw new GameToolApprovalDeniedError("Tool is disabled by host policy.", "denied");
		if (rule.mode === "explicit-only" && !explicit.has(tool.name))
			throw new GameToolApprovalDeniedError("Tool requires an explicit host-attested request.", "denied");
		if (rule.mode === "allowed-in-task" && (!scope.taskId || !taskAllowed.has(tool.name)))
			throw new GameToolApprovalDeniedError("Tool is not allowed by the host-attested task.", "denied");
		if (rule.mode !== "confirm-once") return next();

		const startedAt = Date.now();
		const initialWorld = await this.options.worldState.read(context.input, context.signal);
		const canonicalArguments = canonicalJson(call.arguments);
		const argumentsDigest = digest(canonicalArguments);
		const binding = [
			"approval-v1",
			rule.id,
			...sessionValues(context.input.session).map(String),
			context.input.id,
			context.runId,
			String(context.turn),
			String(context.toolCallIndex),
			call.id,
			tool.name,
			argumentsDigest,
			initialWorld.generationId,
			String(initialWorld.revision),
			scope.taskId ?? "",
		].join("\n");
		const request: GameToolApprovalRequest = {
			approvalId: `approval-v1-${digest(binding)}`,
			policyId: rule.id,
			session: structuredClone(context.input.session),
			inputId: context.input.id,
			runId: context.runId,
			turn: context.turn,
			toolCallIndex: context.toolCallIndex,
			toolCallId: call.id,
			toolName: tool.name,
			risk,
			canonicalArguments,
			argumentsDigest,
			world: structuredClone(initialWorld),
			...(scope.taskId === undefined ? {} : { taskId: boundedId(scope.taskId, "Task id", 1024) }),
			requestedAt: startedAt,
			expiresAt: startedAt + this.timeoutMilliseconds,
		};
		const prepared = await this.options.broker.prepare(request, context.signal);
		if (prepared.status !== "pending")
			throw new GameToolApprovalDeniedError("The one-time approval is no longer pending.", prepared.status);
		await this.emit(request, "pending", 0);
		let decision: { record: GameToolApprovalRecord; credential?: string };
		try {
			decision = await this.options.broker.waitForDecision(request, context.signal);
		} catch (error) {
			await this.emit(request, "cancelled", Date.now() - startedAt);
			throw error;
		}
		if (decision.record.status !== "approved" || !decision.credential) {
			await this.emit(request, decision.record.status, Date.now() - startedAt);
			throw new GameToolApprovalDeniedError(
				decision.record.reason ?? "Tool approval was not granted.",
				decision.record.status,
			);
		}
		const currentWorld = await this.options.worldState.read(context.input, context.signal);
		if (
			currentWorld.generationId !== initialWorld.generationId ||
			currentWorld.revision !== initialWorld.revision ||
			context.input.session.generation !== request.session.generation
		) {
			await this.options.broker.settle(
				request,
				decision.record.revision,
				"expired",
				"The authoritative world changed while approval was pending.",
			);
			await this.emit(request, "expired", Date.now() - startedAt);
			throw new GameToolApprovalDeniedError("The authoritative world changed while approval was pending.", "expired");
		}
		const consumed = await this.options.broker.consume(
			request,
			decision.credential,
			decision.record.revision,
			context.signal,
		);
		await this.emit(request, consumed.status, Date.now() - startedAt);
		return next();
	}

	private async emit(
		request: GameToolApprovalRequest,
		status: GameToolApprovalStatus,
		waitMilliseconds: number,
	): Promise<void> {
		await this.options.onEvent?.({
			approvalId: request.approvalId,
			session: request.session,
			inputId: request.inputId,
			runId: request.runId,
			turn: request.turn,
			toolCallId: request.toolCallId,
			toolName: request.toolName,
			status,
			waitMilliseconds,
		});
	}
}

export function isSettledGameToolApproval(status: GameToolApprovalStatus): boolean {
	return settledStatuses.has(status);
}
