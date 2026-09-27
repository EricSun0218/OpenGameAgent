import { Buffer, constants as bufferConstants } from "node:buffer";
import { createHash } from "node:crypto";
import {
	assertRuntimeActionRecoveryResponse,
	assertRuntimeAuthorityClosure,
	assertRuntimeAuthorityReadResponse,
	assertCanonicalTimestamp,
	canonicalizeJsonValue,
	RuntimeProtocolValidationError,
	captureCloseRunAuthorityRequest,
	captureRunAuthorityKey,
	captureRunAdmissionAuthority,
} from "@opengameagent/protocol";
import { BoundedFetchResponseLimitError, createBoundedFetch } from "./bounded-fetch.js";
import {
	createRuntimeRunSession,
	prepareRuntimeRunSessionOptions,
	prepareRuntimeSseRetryPolicy,
	type RuntimeRunSession,
	type RuntimeRunSessionOptions,
	type RuntimeSseRetryPolicy,
} from "./runtime-run-session.js";
import { type Client, createClient, type ServerSentEventsResult } from "./generated/client/index.js";
import {
	abandonDeferred,
	closeAuthority,
	readAuthorityClosure,
	controlRun,
	decideApproval,
	getAction,
	initializeRuntime,
	listApprovals,
	nextAction,
	recoverAction,
	readAttachment as readAttachmentRequest,
	readDeferred,
	readEvents,
	readRuntimeHealth,
	reconcileAction,
	recordActionReceipt,
	resumeDeferred,
	streamActions,
	streamEvents,
	submitRun,
	waitApprovals,
} from "./generated/sdk.gen.js";
import type {
	RuntimeAuthorityCloseRequest,
	RuntimeAuthorityReadRequest,
	RuntimeActionGetRequest,
	RuntimeActionNextRequest,
	RuntimeActionRecoveryRequest,
	RuntimeActionReceiptRequest,
	RuntimeActionReconcileRequest,
	RuntimeActionSseFrame,
	RuntimeApprovalDecisionRequest,
	RuntimeApprovalListRequest,
	RuntimeApprovalWaitRequest,
	RuntimeAttachmentReadRequest,
	RuntimeControlRequest,
	RuntimeDeferredCommandRequest,
	RuntimeDeferredReadRequest,
	RuntimeEventPageRequest,
	RuntimeEventReplay,
	RuntimeInitializeResponse,
	RuntimeRunAccepted,
	RuntimeRunRequest,
	RuntimeSseFrame,
} from "./generated/types.gen.js";

export * from "./bounded-fetch.js";
export type * from "./generated/types.gen.js";
export {
	RuntimeRunSessionApprovalNotFoundError,
	RuntimeRunSessionClosedError,
	RuntimeRunSessionGapError,
	RuntimeRunSessionHandlerTimeoutError,
	RuntimeRunSessionIdentityError,
	RuntimeRunSessionStreamEndedError,
} from "./runtime-run-session.js";
export type {
	RuntimeRunApproval,
	RuntimeRunApprovalDecision,
	RuntimeRunSession,
	RuntimeRunSessionAccepted,
	RuntimeRunSessionOptions,
	RuntimeRunTerminalEvent,
	RuntimeSseRetryPolicy,
} from "./runtime-run-session.js";

export class RuntimeClientResponseLimitError extends BoundedFetchResponseLimitError {
	constructor(kind: "http-response" | "sse-event") {
		super(kind);
		this.name = "RuntimeClientResponseLimitError";
	}
}

export class RuntimeSseDisconnectedError extends Error {
	constructor(
		public readonly connectionAttempts: number,
		cause?: unknown,
	) {
		super(
			`The Runtime SSE stream ended after ${connectionAttempts} connection attempts.`,
			cause === undefined ? undefined : { cause },
		);
		this.name = "RuntimeSseDisconnectedError";
	}
}

export interface RuntimeClientOptions {
	baseUrl: string;
	maxHttpResponseBytes: number;
	maxAttachmentResponseBytes: number;
	maxSseEventBytes: number;
	fetch?: typeof fetch;
	headers?: HeadersInit;
}

function captureRunRequest(body: RuntimeRunRequest): RuntimeRunRequest {
	const admissionAuthority = body.admissionAuthority;
	return Object.freeze({
		sessionId: body.sessionId,
		actorId: body.actorId,
		inputId: body.inputId,
		payload: structuredClone(body.payload),
		...(admissionAuthority === undefined
			? {}
			: { admissionAuthority: captureRunAdmissionAuthority(admissionAuthority) }),
	});
}

export interface RuntimeAttachmentReadResult {
	readonly data: Uint8Array;
	readonly request: Request;
	readonly response: Response;
}

interface PreparedRuntimeClientOptions {
	readonly baseUrl: string;
	readonly maxHttpResponseBytes: number;
	readonly maxAttachmentResponseBytes: number;
	readonly maxSseEventBytes: number;
	readonly fetch: typeof fetch;
	readonly headers?: Headers;
}

export class RuntimeAttachmentIntegrityError extends Error {
	constructor(public readonly code: "invalid_response" | "content_mismatch") {
		super(code);
		this.name = "RuntimeAttachmentIntegrityError";
	}
}

export class RuntimeClient {
	private readonly client: Client;

	constructor(options: RuntimeClientOptions) {
		const prepared = prepareRuntimeClientOptions(options);
		this.client = createClient({
			baseUrl: prepared.baseUrl,
			fetch: createBoundedFetch(
				prepared.fetch,
				prepared.maxHttpResponseBytes,
				prepared.maxSseEventBytes,
				(kind) => new RuntimeClientResponseLimitError(kind),
				(requestUrl, response) =>
					isSuccessfulAttachmentResponse(requestUrl, response)
						? prepared.maxAttachmentResponseBytes
						: prepared.maxHttpResponseBytes,
				() => undefined,
			),
			...(prepared.headers === undefined ? {} : { headers: prepared.headers }),
		});
	}

	async readAttachment(body: RuntimeAttachmentReadRequest, signal?: AbortSignal): Promise<RuntimeAttachmentReadResult> {
		const result = await readAttachmentRequest({
			client: this.client,
			body,
			parseAs: "arrayBuffer",
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
		const candidate: unknown = result.data;
		if (!(candidate instanceof ArrayBuffer)) throw new RuntimeAttachmentIntegrityError("invalid_response");
		const data = new Uint8Array(candidate);
		validateAttachmentResponse(body, data, result.response);
		return Object.freeze({ data, request: result.request, response: result.response });
	}

	initialize(signal?: AbortSignal) {
		return initializeRuntime({
			client: this.client,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	readHealth(signal?: AbortSignal) {
		return readRuntimeHealth({
			client: this.client,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	submitRun(body: RuntimeRunRequest, signal?: AbortSignal) {
		return submitRun({
			client: this.client,
			body: captureRunRequest(body),
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	async closeAuthority(body: RuntimeAuthorityCloseRequest, signal?: AbortSignal) {
		const request = captureCloseRunAuthorityRequest(body);
		const result = await closeAuthority({
			client: this.client,
			body: request,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
		assertRuntimeAuthorityClosure(result.data);
		assertCanonicalTimestamp(result.data.closedAt);
		if (canonicalizeJsonValue(captureCloseRunAuthorityRequest(result.data)) !== canonicalizeJsonValue(request))
			throw new RuntimeProtocolValidationError();
		return result;
	}

	async readAuthorityClosure(body: RuntimeAuthorityReadRequest, signal?: AbortSignal) {
		const request = captureRunAuthorityKey(body);
		const result = await readAuthorityClosure({
			client: this.client,
			body: request,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
		assertRuntimeAuthorityReadResponse(result.data);
		if (result.data.closure !== null) {
			assertCanonicalTimestamp(result.data.closure.closedAt);
			captureCloseRunAuthorityRequest(result.data.closure);
			if (canonicalizeJsonValue(captureRunAuthorityKey(result.data.closure)) !== canonicalizeJsonValue(request))
				throw new RuntimeProtocolValidationError();
		}
		return result;
	}

	async startRunSession(body: RuntimeRunRequest, options: RuntimeRunSessionOptions): Promise<RuntimeRunSession> {
		const prepared = prepareRuntimeRunSessionOptions(options);
		const request = captureRunRequest(body);
		let result: Awaited<ReturnType<RuntimeClient["submitRun"]>>;
		try {
			result = await this.submitRun(request, prepared.signal);
		} catch (error) {
			if (prepared.signal?.aborted === true) throw prepared.signal.reason;
			throw error;
		}
		return createRuntimeRunSession(this, result.data, request, prepared);
	}

	readDeferred(body: RuntimeDeferredReadRequest, signal?: AbortSignal) {
		return readDeferred({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	resumeDeferred(body: RuntimeDeferredCommandRequest, signal?: AbortSignal) {
		return resumeDeferred({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	abandonDeferred(body: RuntimeDeferredCommandRequest, signal?: AbortSignal) {
		return abandonDeferred({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	readEvents(body: RuntimeEventPageRequest, signal?: AbortSignal) {
		return readEvents({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	controlRun(body: RuntimeControlRequest, signal?: AbortSignal) {
		return controlRun({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	nextAction(body: RuntimeActionNextRequest, signal?: AbortSignal) {
		return nextAction({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	async recoverAction(body: RuntimeActionRecoveryRequest, signal?: AbortSignal) {
		const result = await recoverAction({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
		assertRuntimeActionRecoveryResponse(result.data);
		return result;
	}

	getAction(body: RuntimeActionGetRequest, signal?: AbortSignal) {
		return getAction({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	recordActionReceipt(body: RuntimeActionReceiptRequest, signal?: AbortSignal) {
		return recordActionReceipt({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	reconcileAction(body: RuntimeActionReconcileRequest, signal?: AbortSignal) {
		return reconcileAction({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	async streamActions(
		body: RuntimeActionNextRequest,
		retry: RuntimeSseRetryPolicy,
		signal?: AbortSignal,
	): Promise<ServerSentEventsResult<{ 200: RuntimeActionSseFrame }>> {
		const preparedRetry = prepareRuntimeSseRetryPolicy(retry);
		let terminalError: unknown;
		const result = await streamActions({
			client: this.client,
			body,
			throwOnError: true,
			onSseError: (error) => {
				terminalError = error;
			},
			sseDefaultRetryDelay: preparedRetry.defaultDelayMs,
			sseMaxRetryDelay: preparedRetry.maxDelayMs,
			sseMaxRetryAttempts: preparedRetry.maxAttempts,
			...(signal === undefined ? {} : { signal }),
		});
		const checkedStream = async function* () {
			yield* result.stream;
			if (terminalError !== undefined && signal?.aborted !== true) throw terminalError;
		};
		return { stream: checkedStream() };
	}

	listApprovals(body: RuntimeApprovalListRequest, signal?: AbortSignal) {
		return listApprovals({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	waitApprovals(body: RuntimeApprovalWaitRequest, signal?: AbortSignal) {
		return waitApprovals({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	decideApproval(body: RuntimeApprovalDecisionRequest, signal?: AbortSignal) {
		return decideApproval({
			client: this.client,
			body,
			throwOnError: true,
			...(signal === undefined ? {} : { signal }),
		});
	}

	async streamEvents(
		query: { sessionId: string; actorId: string; afterRevision?: string },
		retry: RuntimeSseRetryPolicy,
		signal?: AbortSignal,
	): Promise<ServerSentEventsResult<{ 200: RuntimeSseFrame }>> {
		const preparedRetry = prepareRuntimeSseRetryPolicy(retry);
		const client = this.client;
		const preparedQuery = {
			sessionId: query.sessionId,
			actorId: query.actorId,
			...(query.afterRevision === undefined ? {} : { afterRevision: query.afterRevision }),
		};
		const checkedStream = async function* () {
			let lastEventId: string | undefined;
			let lastFailure: unknown;
			let retryDelay = preparedRetry.defaultDelayMs;
			for (let attempt = 1; attempt <= preparedRetry.maxAttempts; attempt += 1) {
				let connectionFailure: unknown;
				const result = await streamEvents({
					client,
					query: preparedQuery,
					throwOnError: true,
					onSseError: (error) => {
						connectionFailure = error;
					},
					onSseEvent: (event) => {
						if (event.retry !== undefined && event.retry >= 0) {
							retryDelay = Math.min(event.retry, preparedRetry.maxDelayMs);
						}
					},
					sseDefaultRetryDelay: preparedRetry.defaultDelayMs,
					sseMaxRetryDelay: preparedRetry.maxDelayMs,
					sseMaxRetryAttempts: 1,
					...(lastEventId === undefined ? {} : { headers: { "Last-Event-ID": lastEventId } }),
					...(signal === undefined ? {} : { signal }),
				});
				for await (const frame of result.stream) {
					if (frame.type === "cursor") lastEventId = frame.revision;
					if (frame.type === "event") lastEventId = frame.event.revision;
					yield frame;
					if (frame.type === "gap") return;
				}
				if (signal?.aborted === true) return;
				lastFailure = connectionFailure;
				if (attempt < preparedRetry.maxAttempts) {
					const delay = Math.min(retryDelay * 2 ** (attempt - 1), preparedRetry.maxDelayMs);
					if (await waitForRetry(delay, signal)) return;
				}
			}
			throw new RuntimeSseDisconnectedError(preparedRetry.maxAttempts, lastFailure);
		};
		return { stream: checkedStream() };
	}
}

function assertAllowedRuntimeBaseUrl(value: string): void {
	if (typeof value !== "string" || value.trim() !== value || value.includes("\\")) {
		throw new TypeError("baseUrl must be an HTTPS URL or an exact loopback HTTP URL.");
	}
	let url: URL;
	try {
		url = new URL(value);
	} catch {
		throw new TypeError("baseUrl must be an HTTPS URL or an exact loopback HTTP URL.");
	}
	const isHttps = url.protocol === "https:";
	const isLoopbackHttp = url.protocol === "http:" && isLoopbackHost(url.hostname);
	if (
		(!isHttps && !isLoopbackHttp) ||
		url.username !== "" ||
		url.password !== "" ||
		url.search !== "" ||
		url.hash !== ""
	) {
		throw new TypeError(
			"baseUrl must use HTTPS or loopback HTTP without credentials, query, fragment, or backslashes.",
		);
	}
}

function isLoopbackHost(hostname: string): boolean {
	if (hostname.toLowerCase() === "localhost" || hostname === "[::1]") return true;
	const parts = hostname.split(".");
	return (
		parts.length === 4 &&
		parts[0] === "127" &&
		parts.every((part) => /^(?:0|[1-9][0-9]{0,2})$/u.test(part) && Number(part) <= 255)
	);
}

function prepareRuntimeClientOptions(options: RuntimeClientOptions): PreparedRuntimeClientOptions {
	const baseUrl = options.baseUrl;
	const maxHttpResponseBytes = options.maxHttpResponseBytes;
	const maxAttachmentResponseBytes = options.maxAttachmentResponseBytes;
	const maxSseEventBytes = options.maxSseEventBytes;
	const configuredFetch = options.fetch;
	const headers = options.headers;
	assertAllowedRuntimeBaseUrl(baseUrl);
	assertPositiveSafeInteger(maxHttpResponseBytes, "maxHttpResponseBytes");
	assertAttachmentByteLimit(maxAttachmentResponseBytes);
	assertPositiveSafeInteger(maxSseEventBytes, "maxSseEventBytes");
	const upstreamFetch = configuredFetch ?? globalThis.fetch;
	if (typeof upstreamFetch !== "function") throw new TypeError("A Fetch API implementation is required.");
	return Object.freeze({
		baseUrl,
		maxHttpResponseBytes,
		maxAttachmentResponseBytes,
		maxSseEventBytes,
		fetch: upstreamFetch,
		...(headers === undefined ? {} : { headers: new Headers(headers) }),
	});
}

function waitForRetry(delayMs: number, signal?: AbortSignal): Promise<boolean> {
	if (signal?.aborted === true) return Promise.resolve(true);
	return new Promise<boolean>((resolve) => {
		const timeout = setTimeout(() => finish(false), delayMs);
		const onAbort = () => {
			clearTimeout(timeout);
			finish(true);
		};
		function finish(aborted: boolean) {
			signal?.removeEventListener("abort", onAbort);
			resolve(aborted);
		}
		signal?.addEventListener("abort", onAbort, { once: true });
	});
}

function assertPositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
}

function assertAttachmentByteLimit(value: number): void {
	if (!Number.isSafeInteger(value) || value <= 0 || value > bufferConstants.MAX_LENGTH) {
		throw new TypeError("maxAttachmentResponseBytes must be a positive platform-supported byte length.");
	}
}

function isSuccessfulAttachmentResponse(requestUrl: string, response: Readonly<Response>): boolean {
	try {
		return new URL(requestUrl).pathname === "/runtime/v1/attachments/read" && response.ok;
	} catch {
		return false;
	}
}

function validateAttachmentResponse(
	request: Readonly<RuntimeAttachmentReadRequest>,
	data: Uint8Array,
	response: Readonly<Response>,
): void {
	const reference = request.reference;
	const contentLength = response.headers.get("content-length");
	if (
		normalizeMediaType(response.headers.get("content-type")) !== reference.mediaType ||
		contentLength === null ||
		!/^[1-9][0-9]*$/u.test(contentLength) ||
		Number(contentLength) !== data.byteLength ||
		data.byteLength !== reference.bytes ||
		response.headers.get("etag") !== `"${reference.attachmentId}"` ||
		response.headers.get("cache-control") !== "private, no-store" ||
		response.headers.get("x-content-type-options")?.toLowerCase() !== "nosniff"
	) {
		throw new RuntimeAttachmentIntegrityError("invalid_response");
	}
	const digest = createHash("sha256").update(data).digest();
	if (
		`img_${digest.toString("hex")}` !== reference.attachmentId ||
		response.headers.get("content-digest") !== `sha-256=:${Buffer.from(digest).toString("base64")}:`
	) {
		throw new RuntimeAttachmentIntegrityError("content_mismatch");
	}
}

function normalizeMediaType(value: string | null): string | undefined {
	return value?.split(";", 1)[0]?.trim().toLowerCase();
}

export type RuntimeInitializeResult = RuntimeInitializeResponse;
export type RuntimeRunSubmissionResult = RuntimeRunAccepted;
export type RuntimeEventReadResult = RuntimeEventReplay;
