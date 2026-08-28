import type { GameActionClaim } from "@opengameagent/actions";
import type { GameToolApprovalRecord } from "@opengameagent/approvals";
import type {
	GameActionJournalEntry,
	GameActionReceipt,
	GameAgentEvent,
	GameConversationMessage,
	GameInput,
	GameRunCoordinate,
	GameSessionKey,
	JsonObject,
} from "@opengameagent/protocol";
import type { GameUsageSummary } from "@opengameagent/runtime";
import type { GameServerCapabilities } from "@opengameagent/server";

export interface GameAgentClientOptions {
	baseUrl: string;
	authentication?: JsonObject | (() => JsonObject | undefined | Promise<JsonObject | undefined>);
	fetch?: typeof fetch;
	maximumResponseBytes?: number;
	maximumEventBytes?: number;
}

export class GameAgentClientError extends Error {
	constructor(
		message: string,
		readonly status: number,
		readonly category: string,
	) {
		super(message);
		this.name = "GameAgentClientError";
	}
}

export interface GameTranscriptPage {
	revision: number;
	messages: readonly GameConversationMessage[];
	nextCursor: string | null;
}

export interface GameRunEventPage {
	events: readonly GameAgentEvent[];
	gap: boolean;
	nextSequence: number;
}

export class GameAgentClient {
	private readonly baseUrl: URL;
	private readonly fetcher: typeof fetch;
	private readonly maximumResponseBytes: number;
	private readonly maximumEventBytes: number;

	constructor(private readonly options: GameAgentClientOptions) {
		this.baseUrl = normalizeBaseUrl(options.baseUrl);
		this.fetcher = options.fetch ?? fetch;
		this.maximumResponseBytes = bounded(options.maximumResponseBytes, 4 * 1024 * 1024, 1024, 64 * 1024 * 1024);
		this.maximumEventBytes = bounded(options.maximumEventBytes, 1024 * 1024, 1024, 8 * 1024 * 1024);
	}

	async capabilities(signal?: AbortSignal): Promise<GameServerCapabilities> {
		const response = await this.fetcher(this.url("/v1/capabilities"), {
			method: "GET",
			...(signal === undefined ? {} : { signal }),
		});
		return this.readJson<GameServerCapabilities>(response);
	}

	async *run(input: GameInput, options: { runId?: string; signal?: AbortSignal } = {}): AsyncGenerator<GameAgentEvent> {
		const response = await this.postRaw(
			"/v1/runs/stream",
			{ input, ...(options.runId === undefined ? {} : { runId: options.runId }) },
			options.signal,
		);
		if (!response.ok) throw await this.error(response);
		if (!response.body) throw new GameAgentClientError("Run stream has no response body.", 502, "empty-stream");
		yield* parseEventStream<GameAgentEvent>(response.body, this.maximumEventBytes, options.signal);
	}

	async *streamActions(
		session: GameSessionKey,
		options: { maximum?: number; signal?: AbortSignal } = {},
	): AsyncGenerator<GameActionClaim> {
		const response = await this.postRaw(
			"/v1/actions/stream",
			{ session, ...(options.maximum === undefined ? {} : { maximum: options.maximum }) },
			options.signal,
		);
		if (!response.ok) throw await this.error(response);
		if (!response.body) throw new GameAgentClientError("Action stream has no response body.", 502, "empty-stream");
		yield* parseEventStream<GameActionClaim>(response.body, this.maximumEventBytes, options.signal);
	}

	steer(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput, signal?: AbortSignal) {
		return this.control("steer", session, expected, input, signal);
	}

	followUp(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput, signal?: AbortSignal) {
		return this.control("follow-up", session, expected, input, signal);
	}

	abort(session: GameSessionKey, expected: GameRunCoordinate, signal?: AbortSignal) {
		return this.control("abort", session, expected, undefined, signal);
	}

	async claimActions(session: GameSessionKey, maximum = 1, signal?: AbortSignal): Promise<readonly GameActionClaim[]> {
		return (
			await this.postJson<{ claims: readonly GameActionClaim[] }>("/v1/actions/claim", { session, maximum }, signal)
		).claims;
	}

	async submitActionReceipt(
		session: GameSessionKey,
		receipt: GameActionReceipt,
		signal?: AbortSignal,
	): Promise<GameActionJournalEntry> {
		return (await this.postJson<{ entry: GameActionJournalEntry }>("/v1/actions/receipt", { session, receipt }, signal))
			.entry;
	}

	async reconcileAction(
		session: GameSessionKey,
		operationId: string,
		signal?: AbortSignal,
	): Promise<GameActionJournalEntry> {
		return (
			await this.postJson<{ entry: GameActionJournalEntry }>("/v1/actions/reconcile", { session, operationId }, signal)
		).entry;
	}

	readTranscript(session: GameSessionKey, options: { cursor?: string; limit?: number; signal?: AbortSignal } = {}) {
		return this.postJson<GameTranscriptPage>(
			"/v1/sessions/transcript/read",
			{
				session,
				...(options.cursor === undefined ? {} : { cursor: options.cursor }),
				...(options.limit === undefined ? {} : { limit: options.limit }),
			},
			options.signal,
		);
	}

	readRunEvents(
		session: GameSessionKey,
		runId: string,
		options: { afterSequence?: number; maximum?: number; signal?: AbortSignal } = {},
	) {
		return this.postJson<GameRunEventPage>(
			"/v1/runs/events/read",
			{
				session,
				runId,
				...(options.afterSequence === undefined ? {} : { afterSequence: options.afterSequence }),
				...(options.maximum === undefined ? {} : { maximum: options.maximum }),
			},
			options.signal,
		);
	}

	readUsage(session: GameSessionKey, signal?: AbortSignal): Promise<{ summary: GameUsageSummary }> {
		return this.postJson("/v1/sessions/usage", { session }, signal);
	}

	async readAttachment(session: GameSessionKey, attachmentId: string, signal?: AbortSignal): Promise<Uint8Array> {
		const response = await this.postRaw("/v1/sessions/attachments/read", { session, attachmentId }, signal);
		if (!response.ok) throw await this.error(response);
		const declared = Number(response.headers.get("content-length") ?? "0");
		if (declared > this.maximumResponseBytes) throw new RangeError("Attachment response is too large.");
		const bytes = new Uint8Array(await response.arrayBuffer());
		if (bytes.byteLength > this.maximumResponseBytes) throw new RangeError("Attachment response is too large.");
		return bytes;
	}

	async listApprovals(
		session: GameSessionKey,
		maximum = 32,
		signal?: AbortSignal,
	): Promise<readonly GameToolApprovalRecord[]> {
		return (
			await this.postJson<{ approvals: readonly GameToolApprovalRecord[] }>(
				"/v1/tool-approvals/list",
				{ session, maximum },
				signal,
			)
		).approvals;
	}

	async respondApproval(
		session: GameSessionKey,
		approvalId: string,
		expectedRevision: number,
		decision: "approve" | "deny",
		reason?: string,
		signal?: AbortSignal,
	): Promise<GameToolApprovalRecord> {
		return (
			await this.postJson<{ approval: GameToolApprovalRecord }>(
				"/v1/tool-approvals/respond",
				{
					session,
					approvalId,
					expectedRevision,
					decision,
					...(reason === undefined ? {} : { reason }),
				},
				signal,
			)
		).approval;
	}

	private async control(
		operation: "steer" | "follow-up" | "abort",
		session: GameSessionKey,
		expected: GameRunCoordinate,
		input?: GameInput,
		signal?: AbortSignal,
	): Promise<{ accepted: boolean; reason?: string }> {
		return this.postJson(
			`/v1/control/${operation}`,
			{ session, expected, ...(input === undefined ? {} : { input }) },
			signal,
		);
	}

	private async postJson<T>(path: string, body: JsonObject | object, signal?: AbortSignal): Promise<T> {
		return this.readJson<T>(await this.postRaw(path, body, signal));
	}

	private async postRaw(path: string, body: object, signal?: AbortSignal): Promise<Response> {
		const authentication =
			typeof this.options.authentication === "function"
				? await this.options.authentication()
				: this.options.authentication;
		return this.fetcher(this.url(path), {
			method: "POST",
			headers: { "content-type": "application/json" },
			body: JSON.stringify({ ...body, ...(authentication === undefined ? {} : { authentication }) }),
			...(signal === undefined ? {} : { signal }),
			redirect: "error",
		});
	}

	private async readJson<T>(response: Response): Promise<T> {
		if (!response.ok) throw await this.error(response);
		const text = await readBoundedText(response, this.maximumResponseBytes);
		return JSON.parse(text) as T;
	}

	private async error(response: Response): Promise<GameAgentClientError> {
		let category = `http-${response.status}`;
		try {
			const value = JSON.parse(await readBoundedText(response, 64 * 1024)) as { error?: unknown };
			if (typeof value.error === "string" && /^[a-z0-9-]{1,64}$/.test(value.error)) category = value.error;
		} catch {}
		return new GameAgentClientError(
			`Game Agent server request failed (${response.status}, ${category}).`,
			response.status,
			category,
		);
	}

	private url(path: string): URL {
		return new URL(path, this.baseUrl);
	}
}

function normalizeBaseUrl(value: string): URL {
	const url = new URL(value);
	if (url.username || url.password || url.search || url.hash)
		throw new TypeError("baseUrl must not contain credentials, query, or fragment.");
	if (
		url.protocol !== "https:" &&
		!(
			url.protocol === "http:" &&
			(url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "::1")
		)
	) {
		throw new TypeError("Remote Game Agent servers require HTTPS; HTTP is restricted to loopback.");
	}
	url.pathname = url.pathname.endsWith("/") ? url.pathname : `${url.pathname}/`;
	return url;
}

function bounded(value: number | undefined, fallback: number, minimum: number, maximum: number): number {
	const resolved = value ?? fallback;
	if (!Number.isInteger(resolved) || resolved < minimum || resolved > maximum)
		throw new RangeError("Client limit is outside the supported range.");
	return resolved;
}

async function readBoundedText(response: Response, maximumBytes: number): Promise<string> {
	const declared = Number(response.headers.get("content-length") ?? "0");
	if (declared > maximumBytes) throw new RangeError("Server response is too large.");
	const text = await response.text();
	if (new TextEncoder().encode(text).byteLength > maximumBytes) throw new RangeError("Server response is too large.");
	return text;
}

async function* parseEventStream<T>(
	stream: ReadableStream<Uint8Array>,
	maximumEventBytes: number,
	signal?: AbortSignal,
): AsyncGenerator<T> {
	const reader = stream.getReader();
	const decoder = new TextDecoder();
	let pending = "";
	try {
		while (true) {
			signal?.throwIfAborted();
			const { done, value } = await reader.read();
			pending += decoder.decode(value, { stream: !done });
			let delimiter = findEventDelimiter(pending);
			while (delimiter) {
				const frame = pending.slice(0, delimiter.index).replaceAll("\r", "");
				if (new TextEncoder().encode(frame).byteLength > maximumEventBytes)
					throw new RangeError("Server event is too large.");
				pending = pending.slice(delimiter.index + delimiter.length);
				const data = frame
					.split("\n")
					.filter((line) => line.startsWith("data:"))
					.map((line) => line.slice(5).trimStart())
					.join("\n");
				if (!data) continue;
				const parsed: unknown = JSON.parse(data);
				if (typeof parsed !== "object" || parsed === null)
					throw new GameAgentClientError("Game Agent run stream returned an invalid event.", 502, "invalid-event");
				if ("error" in parsed) {
					const error = (parsed as { error?: unknown }).error;
					throw new GameAgentClientError(
						"Game Agent run stream failed.",
						502,
						typeof error === "string" ? error : "stream-failed",
					);
				}
				yield parsed as T;
				delimiter = findEventDelimiter(pending);
			}
			if (new TextEncoder().encode(pending).byteLength > maximumEventBytes)
				throw new RangeError("Server event is too large.");
			if (done) break;
		}
		if (pending.trim().length > 0)
			throw new GameAgentClientError("Game Agent run stream ended with an incomplete event.", 502, "incomplete-stream");
	} finally {
		await reader.cancel().catch(() => undefined);
	}
}

function findEventDelimiter(value: string): { index: number; length: number } | undefined {
	const lineFeed = value.indexOf("\n\n");
	const carriageReturn = value.indexOf("\r\n\r\n");
	if (lineFeed < 0 && carriageReturn < 0) return undefined;
	if (carriageReturn >= 0 && (lineFeed < 0 || carriageReturn < lineFeed)) return { index: carriageReturn, length: 4 };
	return { index: lineFeed, length: 2 };
}
