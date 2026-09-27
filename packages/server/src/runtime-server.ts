import { Buffer, constants as bufferConstants } from "node:buffer";
import { createHash } from "node:crypto";
import { serve, type ServerType } from "@hono/node-server";
import {
	ActionApprovalConflictError,
	ActionApprovalNotFoundError,
	type ActionApprovalCoordinator,
	ActionClaimConflictError,
	type ActionCoordinator,
	ActionNotFoundError,
	ActionStateConflictError,
} from "@opengameagent/actions";
import {
	assertOpaqueIdentifier,
	captureCloseRunAuthorityRequest,
	type CloseRunAuthorityRequest,
	RunAuthorityClosedError,
	RunAuthorityConflictError,
	WorkAuthorityRetirementConflictError,
	RuntimeAuthorityCloseRequestSchema,
	RuntimeAuthorityReadRequestSchema,
	type CanonicalEvent,
	type ImageReferenceContent,
	RUNTIME_API_VERSION,
	RUNTIME_BASE_CAPABILITIES,
	RuntimeActionGetRequestSchema,
	RuntimeActionNextRequestSchema,
	RuntimeActionRecoveryRequestSchema,
	RuntimeActionReceiptRequestSchema,
	RuntimeActionReconcileRequestSchema,
	type RuntimeActionSseFrame,
	RuntimeApprovalDecisionRequestSchema,
	RuntimeApprovalListRequestSchema,
	RuntimeApprovalWaitRequestSchema,
	canonicalizeJsonValue,
	RuntimeAttachmentReadRequestSchema,
	RuntimeControlRequestSchema,
	RuntimeDeferredCommandRequestSchema,
	RuntimeDeferredReadRequestSchema,
	RuntimeEventPageRequestSchema,
	type RuntimeHealthSnapshot,
	RuntimeHealthSnapshotSchema,
	RuntimeRunRequestSchema,
	RuntimeStreamSelectorSchema,
	type RuntimeSseFrame,
	type RuntimeStreamSelector,
} from "@opengameagent/protocol";
import {
	BoundedAsyncQueue,
	type GameRuntime,
	RunInputConflictError,
	RuntimeAdmissionClosedError,
	RuntimeDeferredBusyError,
	RuntimeDeferredNotFoundError,
	RuntimeDeferredPendingError,
	RuntimeDeferredResumeUnavailableError,
	RuntimeInputLimitError,
	RuntimeQueueFullError,
} from "@opengameagent/runtime";
import { Hono, type Context } from "hono";
import { streamSSE } from "hono/streaming";
import type { ContentfulStatusCode } from "hono/utils/http-status";
import { Value } from "typebox/value";
import {
	projectEventForAudience,
	type AuthorizedRuntimeStream,
	type RuntimePrincipal,
	validateAuthorizedStream,
} from "./audience.js";
import { RequestBodyError, readBoundedJson } from "./bounded-json.js";

export interface RuntimeAccessController {
	/** Work authority requires an explicit host permission for its exact lane. */
	authorizeWorkRetirement?(
		principal: Readonly<RuntimePrincipal>,
		access: Readonly<AuthorizedRuntimeStream>,
		request: Readonly<CloseRunAuthorityRequest>,
		signal: AbortSignal,
	): boolean | Promise<boolean>;
	/** Goal authority requires an explicit host permission, independent of actor ownership. */
	authorizeGoalRetirement?(
		principal: Readonly<RuntimePrincipal>,
		access: Readonly<AuthorizedRuntimeStream>,
		request: Readonly<CloseRunAuthorityRequest>,
		signal: AbortSignal,
	): boolean | Promise<boolean>;
	authenticate(request: Request): RuntimePrincipal | undefined | Promise<RuntimePrincipal | undefined>;
	/** Grants only the requested stream; a different returned key denies the request. */
	authorize(
		principal: Readonly<RuntimePrincipal>,
		selector: Readonly<{ sessionId: string; actorId: string }>,
	): AuthorizedRuntimeStream | undefined | Promise<AuthorizedRuntimeStream | undefined>;
}

export interface RuntimeServerLimits {
	maxRequestBytes: number;
	maxResponseBytes: number;
	maxJsonDepth: number;
	maxEventPageSize: number;
	maxSseConnections: number;
	maxSseEventBytes: number;
	maxSseBacklog: number;
	maxActionClaimCandidates: number;
	actionClaimLeaseMs: number;
	actionStreamWaitMs: number;
	maxApprovalPageSize: number;
	maxApprovalWaitMilliseconds: number;
	maxApprovalWaiters: number;
}

const RUNTIME_SERVER_LIMIT_NAMES = [
	"maxRequestBytes",
	"maxResponseBytes",
	"maxJsonDepth",
	"maxEventPageSize",
	"maxSseConnections",
	"maxSseEventBytes",
	"maxSseBacklog",
	"maxActionClaimCandidates",
	"actionClaimLeaseMs",
	"actionStreamWaitMs",
	"maxApprovalPageSize",
	"maxApprovalWaitMilliseconds",
	"maxApprovalWaiters",
] as const satisfies readonly (keyof RuntimeServerLimits)[];

const RESPONSE_TOO_LARGE_BODY = JSON.stringify({
	error: { code: "response_too_large", message: "The response exceeds the configured byte limit." },
});
const MINIMUM_RESPONSE_BYTES = Buffer.byteLength(RESPONSE_TOO_LARGE_BODY);
const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;

export interface RuntimeServerOptions {
	runtime: GameRuntime;
	access: RuntimeAccessController;
	limits: RuntimeServerLimits;
	actions?: ActionCoordinator;
	approvals?: ActionApprovalCoordinator;
	attachments?: RuntimeAttachmentService;
	health?: RuntimeServerHealthService;
}

export interface RuntimeServerHealthService {
	inspect(signal: AbortSignal): Promise<RuntimeHealthSnapshot>;
}

export interface RuntimeAttachmentService {
	readonly maxResponseBytes: number;
	read(reference: Readonly<ImageReferenceContent>, signal: AbortSignal): Promise<Uint8Array>;
	authorize(
		principal: Readonly<RuntimePrincipal>,
		access: Readonly<AuthorizedRuntimeStream>,
		reference: Readonly<ImageReferenceContent>,
		signal: AbortSignal,
	): boolean | Promise<boolean>;
}

export interface RuntimeNodeServerOptions {
	app: Hono;
	hostname: string;
	port: number;
}

export function createRuntimeServer(inputOptions: RuntimeServerOptions): Hono {
	const options = prepareRuntimeServerOptions(inputOptions);
	const app = new Hono();
	let sseConnections = 0;
	let approvalWaiters = 0;
	const jsonResponse = (context: Context, value: unknown, status: ContentfulStatusCode = 200) =>
		boundedJsonResponse(context, value, status, options.limits.maxResponseBytes);
	const errorResponse = (context: Context, status: ContentfulStatusCode, code: string, message: string) =>
		boundedErrorResponse(context, status, code, message, options.limits.maxResponseBytes);

	app.onError((error, context) => {
		if (error instanceof RunAuthorityClosedError) {
			return errorResponse(context, 409, "authority_closed", "The run authority is permanently closed.");
		}
		if (error instanceof RunAuthorityConflictError || error instanceof WorkAuthorityRetirementConflictError) {
			return errorResponse(
				context,
				409,
				"authority_conflict",
				"The authority closure conflicts with its durable identity.",
			);
		}
		if (error instanceof RequestBodyError) {
			const status = error.code === "body_too_large" ? 413 : 400;
			return errorResponse(context, status, error.code, "The JSON request body is invalid.");
		}
		return errorResponse(context, 500, "internal_error", "The runtime server could not complete the request.");
	});

	app.post("/runtime/v1/initialize", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const capabilities = [
			...RUNTIME_BASE_CAPABILITIES,
			...(options.actions ? (["actions.exchange", "actions.sse"] as const) : []),
			...(options.approvals ? (["approvals.exact"] as const) : []),
			...(options.attachments ? (["attachments.read"] as const) : []),
			...(options.health ? (["health.snapshot"] as const) : []),
		];
		return jsonResponse(context, { protocolVersion: RUNTIME_API_VERSION, capabilities });
	});

	app.get("/runtime/v1/health", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.health) {
			return errorResponse(context, 501, "not_supported", "Runtime health is not configured.");
		}
		const snapshot = await options.health.inspect(context.req.raw.signal);
		if (!Value.Check(RuntimeHealthSnapshotSchema, snapshot)) {
			throw new TypeError("The Runtime health service returned an invalid snapshot.");
		}
		const response = jsonResponse(context, snapshot);
		response.headers.set("Cache-Control", "private, no-store");
		return response;
	});

	app.post("/runtime/v1/runs", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeRunRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The run request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		try {
			const run = await options.runtime.submit({
				...access.stream,
				inputId: body.inputId,
				payload: body.payload,
				...(body.admissionAuthority === undefined ? {} : { admissionAuthority: body.admissionAuthority }),
			});
			return jsonResponse(
				context,
				{ runId: run.runId, initialCoordinate: run.initialCoordinate, eventCursor: run.eventCursor },
				202,
			);
		} catch (error) {
			if (error instanceof RuntimeInputLimitError) {
				return errorResponse(context, error.code === "input_too_large" ? 413 : 400, error.code, error.message);
			}
			if (error instanceof RunInputConflictError) {
				return errorResponse(
					context,
					409,
					"input_conflict",
					"The input identifier is already associated with different content.",
				);
			}
			if (error instanceof RuntimeQueueFullError) {
				return errorResponse(context, 429, "queue_full", "The runtime run queue is full.");
			}
			if (error instanceof RuntimeAdmissionClosedError) {
				return errorResponse(context, 503, "runtime_stopping", "The runtime is not accepting new runs.");
			}
			if (error instanceof RuntimeDeferredPendingError) {
				return errorResponse(context, 409, "deferred_pending", "A deferred operation is pending for this actor.");
			}
			if (error instanceof RuntimeDeferredBusyError) {
				return errorResponse(context, 409, "deferred_busy", "The actor is completing a deferred transition.");
			}
			throw error;
		}
	});

	app.post("/runtime/v1/authority/close", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeAuthorityCloseRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The authority close request is invalid.");
		}
		let request: Readonly<CloseRunAuthorityRequest>;
		try {
			request = captureCloseRunAuthorityRequest(body);
		} catch {
			return errorResponse(context, 400, "invalid_request", "The authority close request is invalid.");
		}
		const access = await authorize(options.access, principal, request);
		if (!access?.isOwner)
			return errorResponse(context, 403, "forbidden", "Authority closure requires actor ownership.");
		if (
			request.goalRetirement !== undefined &&
			(await options.access.authorizeGoalRetirement?.(principal, access, request, context.req.raw.signal)) !== true
		) {
			return errorResponse(context, 403, "forbidden", "Goal authority closure requires explicit partition permission.");
		}
		if (
			request.workRetirement !== undefined &&
			(await options.access.authorizeWorkRetirement?.(principal, access, request, context.req.raw.signal)) !== true
		) {
			return errorResponse(context, 403, "forbidden", "Work authority closure requires explicit lane permission.");
		}
		context.req.raw.signal.throwIfAborted();
		return jsonResponse(context, await options.runtime.closeAuthority(request));
	});

	app.post("/runtime/v1/authority/read", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeAuthorityReadRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The authority read request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.isOwner)
			return errorResponse(context, 403, "forbidden", "Authority inspection requires actor ownership.");
		return jsonResponse(context, {
			closure: (await options.runtime.readAuthorityClosure({ ...body, ...access.stream })) ?? null,
		});
	});

	app.post("/runtime/v1/deferred/read", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeDeferredReadRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The deferred read request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		const pending = options.runtime.readDeferred(access.stream);
		return jsonResponse(context, {
			deferred: pending
				? {
						operationId: pending.operationId,
						inputId: pending.input.inputId,
						coordinate: { ...pending.coordinate },
					}
				: null,
		});
	});

	app.post("/runtime/v1/deferred/resume", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeDeferredCommandRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The deferred resume request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		try {
			const run = await options.runtime.resumeDeferred(access.stream, body.operationId);
			return jsonResponse(
				context,
				{ runId: run.runId, initialCoordinate: run.initialCoordinate, eventCursor: run.eventCursor },
				202,
			);
		} catch (error) {
			return deferredTransitionError(context, error, options.limits.maxResponseBytes);
		}
	});

	app.post("/runtime/v1/deferred/abandon", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeDeferredCommandRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The deferred abandon request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		try {
			return jsonResponse(context, await options.runtime.abandonDeferred(access.stream, body.operationId));
		} catch (error) {
			return deferredTransitionError(context, error, options.limits.maxResponseBytes);
		}
	});

	app.post("/runtime/v1/actions/next", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.actions) return errorResponse(context, 501, "not_supported", "Action exchange is not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeActionNextRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The action delivery request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowActionExchange) {
			return errorResponse(context, 403, "forbidden", "Action exchange is not available to this principal.");
		}
		const delivery = await options.actions.nextDelivery({
			...access.stream,
			...(body.runId === undefined ? {} : { runId: body.runId }),
			claimantId: principal.principalId,
			leaseDurationMs: options.limits.actionClaimLeaseMs,
			candidateLimit: options.limits.maxActionClaimCandidates,
		});
		return jsonResponse(context, { delivery: delivery ?? null });
	});

	app.post("/runtime/v1/actions/recovery", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.actions) return errorResponse(context, 501, "not_supported", "Action exchange is not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeActionRecoveryRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The action recovery request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowActionExchange) {
			return errorResponse(context, 403, "forbidden", "Action exchange is not available to this principal.");
		}
		const delivery = await options.actions.recoverDelivery(access.stream);
		return jsonResponse(context, { delivery: delivery ?? null });
	});

	app.post("/runtime/v1/actions/stream", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.actions) return errorResponse(context, 501, "not_supported", "Action exchange is not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeActionNextRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The action stream request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowActionExchange) {
			return errorResponse(context, 403, "forbidden", "Action exchange is not available to this principal.");
		}
		if (sseConnections >= options.limits.maxSseConnections) {
			return errorResponse(context, 503, "stream_capacity", "The runtime action stream is at capacity.");
		}

		sseConnections += 1;
		const actions = options.actions;
		return streamSSE(context, async (stream) => {
			const disconnected = new AbortController();
			let lastDeliveryFingerprint: string | undefined;
			stream.onAbort(() => disconnected.abort());
			try {
				while (!disconnected.signal.aborted) {
					const revision = actions.changeRevision();
					const delivery = await actions.nextDelivery({
						...access.stream,
						...(body.runId === undefined ? {} : { runId: body.runId }),
						claimantId: principal.principalId,
						leaseDurationMs: options.limits.actionClaimLeaseMs,
						candidateLimit: options.limits.maxActionClaimCandidates,
					});
					if (!delivery) {
						lastDeliveryFingerprint = undefined;
					} else {
						const fingerprint = actionDeliveryFingerprint(delivery.action);
						if (fingerprint !== lastDeliveryFingerprint) {
							const written = await writeActionFrame(
								stream,
								{ type: "delivery", delivery },
								options.limits.maxSseEventBytes,
							);
							if (!written) return;
							lastDeliveryFingerprint = fingerprint;
						}
					}
					await actions.waitForChange(revision, options.limits.actionStreamWaitMs, disconnected.signal);
				}
			} catch (error) {
				if (!disconnected.signal.aborted) throw error;
			} finally {
				disconnected.abort();
				sseConnections -= 1;
			}
		});
	});

	app.post("/runtime/v1/actions/get", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.actions) return errorResponse(context, 501, "not_supported", "Action exchange is not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeActionGetRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The action lookup request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowActionExchange) {
			return errorResponse(context, 403, "forbidden", "Action exchange is not available to this principal.");
		}
		return jsonResponse(context, { action: (await options.actions.get(access.stream, body.operationId)) ?? null });
	});

	app.post("/runtime/v1/actions/receipt", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.actions) return errorResponse(context, 501, "not_supported", "Action exchange is not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeActionReceiptRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The action receipt is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowActionExchange) {
			return errorResponse(context, 403, "forbidden", "Action exchange is not available to this principal.");
		}
		try {
			const action = await options.actions.recordReceipt({
				...access.stream,
				operationId: body.operationId,
				claimToken: body.claimToken,
				attempt: body.attempt,
				outcome: body.outcome,
				...(body.result === undefined ? {} : { result: body.result }),
				...(body.modelResult === undefined ? {} : { modelResult: body.modelResult }),
				...(body.authorityAfter === undefined ? {} : { authorityAfter: body.authorityAfter }),
			});
			return jsonResponse(context, { action });
		} catch (error) {
			return actionTransitionError(context, error, options.limits.maxResponseBytes);
		}
	});

	app.post("/runtime/v1/actions/reconcile", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.actions) return errorResponse(context, 501, "not_supported", "Action exchange is not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeActionReconcileRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The action reconciliation is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowActionExchange) {
			return errorResponse(context, 403, "forbidden", "Action exchange is not available to this principal.");
		}
		try {
			const action = await options.actions.reconcile({
				...access.stream,
				operationId: body.operationId,
				claimToken: body.claimToken,
				attempt: body.attempt,
				resolution: body.resolution,
				...(body.result === undefined ? {} : { result: body.result }),
				...(body.modelResult === undefined ? {} : { modelResult: body.modelResult }),
				...(body.authorityAfter === undefined ? {} : { authorityAfter: body.authorityAfter }),
			});
			return jsonResponse(context, { action });
		} catch (error) {
			return actionTransitionError(context, error, options.limits.maxResponseBytes);
		}
	});

	app.post("/runtime/v1/approvals", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.approvals) return errorResponse(context, 501, "not_supported", "Approvals are not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeApprovalListRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The approval list request is invalid.");
		}
		if (body.limit > options.limits.maxApprovalPageSize) {
			return errorResponse(context, 400, "page_limit_exceeded", "The requested approval page is too large.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowApprovals) {
			return errorResponse(context, 403, "forbidden", "Approvals are not available to this principal.");
		}
		return jsonResponse(context, {
			approvals: await options.approvals.listPending({
				...access.stream,
				...(body.runId === undefined ? {} : { runId: body.runId }),
				limit: body.limit,
			}),
		});
	});

	app.post("/runtime/v1/approvals/decision", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.approvals) return errorResponse(context, 501, "not_supported", "Approvals are not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeApprovalDecisionRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The approval decision is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowApprovals) {
			return errorResponse(context, 403, "forbidden", "Approvals are not available to this principal.");
		}
		try {
			const approval = await options.approvals.decide({
				...access.stream,
				approvalId: body.approvalId,
				expectedRevision: body.expectedRevision,
				requestFingerprint: body.requestFingerprint,
				decision: body.decision,
				reviewerId: principal.principalId,
				...(body.reason === undefined ? {} : { reason: body.reason }),
			});
			return jsonResponse(context, { approval });
		} catch (error) {
			if (error instanceof ActionApprovalNotFoundError) {
				return errorResponse(context, 404, "approval_not_found", "The approval was not found.");
			}
			if (error instanceof ActionApprovalConflictError) {
				return errorResponse(context, 409, "approval_conflict", "The approval changed or was already decided.");
			}
			throw error;
		}
	});

	app.post("/runtime/v1/approvals/wait", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.approvals) return errorResponse(context, 501, "not_supported", "Approvals are not configured.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeApprovalWaitRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The approval wait request is invalid.");
		}
		if (body.limit > options.limits.maxApprovalPageSize) {
			return errorResponse(context, 400, "page_limit_exceeded", "The requested approval page is too large.");
		}
		if (body.waitMilliseconds > options.limits.maxApprovalWaitMilliseconds) {
			return errorResponse(context, 400, "wait_limit_exceeded", "The requested approval wait is too long.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access?.allowApprovals) {
			return errorResponse(context, 403, "forbidden", "Approvals are not available to this principal.");
		}
		if (approvalWaiters >= options.limits.maxApprovalWaiters) {
			return errorResponse(context, 503, "approval_wait_capacity", "Approval wait capacity is exhausted.");
		}
		approvalWaiters += 1;
		try {
			const deadline = Date.now() + body.waitMilliseconds;
			while (true) {
				const revision = options.approvals.changeRevision();
				const approvals = await options.approvals.listPending({
					...access.stream,
					runId: body.runId,
					limit: body.limit,
				});
				const cursor = approvalCursor(approvals);
				const remaining = deadline - Date.now();
				if (body.afterCursor === undefined || cursor !== body.afterCursor || remaining <= 0) {
					return jsonResponse(context, { approvals, cursor });
				}
				await options.approvals.waitForChange(revision, remaining, context.req.raw.signal);
			}
		} finally {
			approvalWaiters -= 1;
		}
	});

	app.post("/runtime/v1/events", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeEventPageRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The event page request is invalid.");
		}
		if (body.limit > options.limits.maxEventPageSize) {
			return errorResponse(context, 400, "page_limit_exceeded", "The requested event page is too large.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		const replay = await options.runtime.replayEvents(access.stream, {
			...(body.afterRevision === undefined ? {} : { afterRevision: body.afterRevision }),
			limit: body.limit,
		});
		if (replay.kind === "gap") return jsonResponse(context, replay);
		return jsonResponse(context, {
			...replay,
			events: replay.events.flatMap((event) => {
				const projected = projectEventForAudience(event, access);
				return projected ? [projected] : [];
			}),
		});
	});

	app.post("/runtime/v1/control", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeControlRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The control request is invalid.");
		}
		const access = await authorize(options.access, principal, body.coordinate);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		const coordinate = { ...body.coordinate, ...access.stream };
		return jsonResponse(context, await options.runtime.control(coordinate, body.command));
	});

	app.post("/runtime/v1/attachments/read", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		if (!options.attachments) {
			return errorResponse(context, 501, "not_supported", "Attachment reads are not configured.");
		}
		const body = await parseRequest(context.req.raw, options.limits);
		if (!Value.Check(RuntimeAttachmentReadRequestSchema, body)) {
			return errorResponse(context, 400, "invalid_request", "The attachment read request is invalid.");
		}
		const access = await authorize(options.access, principal, body);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		if (body.reference.mediaType !== "image/png") {
			return errorResponse(context, 400, "unsupported_media_type", "The attachment media type is not supported.");
		}
		if (body.reference.bytes > options.attachments.maxResponseBytes) {
			return errorResponse(context, 413, "attachment_too_large", "The attachment exceeds the response limit.");
		}
		const reference = Object.freeze({ ...body.reference });
		const allowed = await options.attachments.authorize(principal, access, reference, context.req.raw.signal);
		context.req.raw.signal.throwIfAborted();
		if (allowed !== true) {
			return errorResponse(context, 403, "forbidden", "The attachment is not available to this principal.");
		}
		const candidate = await options.attachments.read(reference, context.req.raw.signal);
		context.req.raw.signal.throwIfAborted();
		if (!(candidate instanceof Uint8Array)) throw new RuntimeAttachmentIntegrityError();
		const bytes = Uint8Array.from(candidate);
		if (bytes.byteLength !== reference.bytes || bytes.byteLength > options.attachments.maxResponseBytes) {
			throw new RuntimeAttachmentIntegrityError();
		}
		const digest = createHash("sha256").update(bytes).digest();
		if (`img_${digest.toString("hex")}` !== reference.attachmentId) throw new RuntimeAttachmentIntegrityError();
		return context.body(bytes, 200, {
			"Cache-Control": "private, no-store",
			"Content-Digest": `sha-256=:${digest.toString("base64")}:`,
			"Content-Length": String(bytes.byteLength),
			"Content-Type": reference.mediaType,
			ETag: `"${reference.attachmentId}"`,
			"X-Content-Type-Options": "nosniff",
		});
	});

	app.get("/runtime/v1/events/stream", async (context) => {
		const principal = await authenticate(options.access, context.req.raw);
		if (!principal) return errorResponse(context, 401, "unauthorized", "Authentication is required.");
		const candidate = { sessionId: context.req.query("sessionId"), actorId: context.req.query("actorId") };
		if (!Value.Check(RuntimeStreamSelectorSchema, candidate)) {
			return errorResponse(context, 400, "invalid_request", "The stream selector is invalid.");
		}
		const selector = candidate as RuntimeStreamSelector;
		const access = await authorize(options.access, principal, selector);
		if (!access) return errorResponse(context, 403, "forbidden", "The actor is not available to this principal.");
		if (sseConnections >= options.limits.maxSseConnections) {
			return errorResponse(context, 503, "stream_capacity", "The runtime event stream is at capacity.");
		}
		const afterRevision = context.req.header("Last-Event-ID") ?? context.req.query("afterRevision") ?? "0";
		if (!/^(0|[1-9][0-9]*)$/u.test(afterRevision)) {
			return errorResponse(context, 400, "invalid_cursor", "The event cursor is invalid.");
		}
		sseConnections += 1;
		return streamSSE(context, async (stream) => {
			const queue = new BoundedAsyncQueue<QueuedRuntimeEvent>(options.limits.maxSseBacklog);
			let stop: () => void = () => undefined;
			stream.onAbort(() => queue.close());
			stop = options.runtime.subscribe(access.stream, (event) => {
				const projected = projectEventForAudience(event, access);
				if (
					projected &&
					!runtimeSseFrameFits(
						{ type: "event", event: projected },
						event.revision,
						"event",
						options.limits.maxSseEventBytes,
					)
				) {
					queue.close();
					stop();
					return;
				}
				const queued = Object.freeze({
					revision: event.revision,
					...(projected ? { projected: structuredClone(projected) } : {}),
				});
				if (!queue.push(queued)) stop();
			});
			let cursor = afterRevision;
			try {
				while (true) {
					const replay = await options.runtime.replayEvents(access.stream, {
						afterRevision: cursor,
						limit: options.limits.maxEventPageSize,
					});
					if (replay.kind === "gap") {
						if (
							!(await writeFrame(
								stream,
								{ type: "gap", gap: replay },
								replay.nextAfterRevision,
								"gap",
								options.limits.maxSseEventBytes,
							))
						)
							return;
						return;
					}
					for (const event of replay.events) {
						if (!(await writeProjectedEvent(stream, event, access, options.limits.maxSseEventBytes))) return;
					}
					cursor = replay.cursor;
					if (
						!(await writeFrame(
							stream,
							{ type: "cursor", revision: cursor },
							cursor,
							"cursor",
							options.limits.maxSseEventBytes,
						))
					)
						return;
					if (!replay.hasMore) break;
				}

				while (true) {
					const event = await queue.next();
					if (!event) return;
					if (BigInt(event.revision) <= BigInt(cursor)) continue;
					if (event.projected) {
						if (
							!(await writeFrame(
								stream,
								{ type: "event", event: event.projected },
								event.revision,
								"event",
								options.limits.maxSseEventBytes,
							))
						)
							return;
					} else if (
						!(await writeFrame(
							stream,
							{ type: "cursor", revision: event.revision },
							event.revision,
							"cursor",
							options.limits.maxSseEventBytes,
						))
					) {
						return;
					}
					cursor = event.revision;
				}
			} finally {
				stop();
				queue.close();
				sseConnections -= 1;
			}
		});
	});

	return app;
}

export function serveRuntimeServer(options: RuntimeNodeServerOptions): ServerType {
	if (!Number.isInteger(options.port) || options.port < 0 || options.port > 65_535) {
		throw new RangeError("port must be an integer in the TCP port range.");
	}
	if (options.hostname.length === 0) throw new TypeError("hostname must not be empty.");
	return serve({ fetch: options.app.fetch, hostname: options.hostname, port: options.port });
}

async function authorize(
	controller: RuntimeAccessController,
	principal: RuntimePrincipal,
	selector: { sessionId: string; actorId: string },
): Promise<AuthorizedRuntimeStream | undefined> {
	const requested = Object.freeze({ sessionId: selector.sessionId, actorId: selector.actorId });
	const access = await controller.authorize(principal, requested);
	if (!access) return undefined;
	const { stream, isOwner, recipientIds, allowInternal, allowActionExchange, allowApprovals } = access;
	const captured = Object.freeze({
		stream: Object.freeze({ sessionId: stream.sessionId, actorId: stream.actorId }),
		isOwner,
		recipientIds: Object.freeze([...recipientIds]),
		allowInternal,
		...(allowActionExchange === undefined ? {} : { allowActionExchange }),
		...(allowApprovals === undefined ? {} : { allowApprovals }),
	});
	validateAuthorizedStream(captured);
	if (captured.stream.sessionId !== requested.sessionId || captured.stream.actorId !== requested.actorId)
		return undefined;
	return captured;
}

async function parseRequest(request: Request, limits: RuntimeServerLimits): Promise<unknown> {
	try {
		return await readBoundedJson(request, limits.maxRequestBytes, limits.maxJsonDepth);
	} catch (error) {
		if (error instanceof RequestBodyError) throw error;
		throw new RequestBodyError("invalid_json");
	}
}

interface QueuedRuntimeEvent {
	readonly revision: string;
	readonly projected?: CanonicalEvent;
}

async function writeProjectedEvent(
	stream: { writeSSE(message: { data: string; event?: string; id?: string }): Promise<void> },
	event: CanonicalEvent,
	access: AuthorizedRuntimeStream,
	maxSseEventBytes: number,
): Promise<boolean> {
	const projected = projectEventForAudience(event, access);
	return projected
		? await writeFrame(stream, { type: "event", event: projected }, event.revision, "event", maxSseEventBytes)
		: true;
}

async function writeFrame(
	stream: { writeSSE(message: { data: string; event?: string; id?: string }): Promise<void> },
	frame: RuntimeSseFrame,
	id: string,
	event: string,
	maxSseEventBytes: number,
): Promise<boolean> {
	const message = { data: JSON.stringify(frame), id, event };
	if (!sseMessageFits(message, maxSseEventBytes)) return false;
	await stream.writeSSE(message);
	return true;
}

async function writeActionFrame(
	stream: { writeSSE(message: { data: string; event?: string; id?: string }): Promise<void> },
	frame: RuntimeActionSseFrame,
	maxSseEventBytes: number,
): Promise<boolean> {
	const action = frame.delivery.action;
	const message = {
		data: JSON.stringify(frame),
		id: `${action.operationId}:${action.attempt}:${frame.delivery.kind}`,
		event: "action",
	};
	if (!sseMessageFits(message, maxSseEventBytes)) return false;
	await stream.writeSSE(message);
	return true;
}

function runtimeSseFrameFits(frame: RuntimeSseFrame, id: string, event: string, maxSseEventBytes: number): boolean {
	return sseMessageFits({ data: JSON.stringify(frame), id, event }, maxSseEventBytes);
}

function sseMessageFits(
	message: Readonly<{ data: string; event?: string; id?: string; retry?: number }>,
	maxSseEventBytes: number,
): boolean {
	return Buffer.byteLength(serializeSseMessage(message)) <= maxSseEventBytes;
}

function serializeSseMessage(message: Readonly<{ data: string; event?: string; id?: string; retry?: number }>): string {
	const dataLines = message.data
		.split(/\r\n|\r|\n/u)
		.map((line) => `data: ${line}`)
		.join("\n");
	for (const value of [message.event, message.id]) {
		if (value && /[\r\n]/u.test(value)) throw new TypeError("SSE event and id fields must not contain line breaks.");
	}
	return `${[
		message.event && `event: ${message.event}`,
		dataLines,
		message.id !== undefined && `id: ${message.id}`,
		message.retry !== undefined && `retry: ${message.retry}`,
	]
		.filter(Boolean)
		.join("\n")}\n\n`;
}

function actionDeliveryFingerprint(action: { operationId: string; attempt: string; status: string }): string {
	return JSON.stringify([action.operationId, action.attempt, action.status]);
}

function prepareRuntimeServerOptions(options: RuntimeServerOptions) {
	if (!options?.runtime || !options.access || !options.limits) {
		throw new TypeError("Runtime server options are required.");
	}
	const runtime = options.runtime;
	const access = options.access;
	const authorizeGoalRetirement = access.authorizeGoalRetirement;
	const authorizeWorkRetirement = access.authorizeWorkRetirement;
	if (authorizeWorkRetirement !== undefined && typeof authorizeWorkRetirement !== "function") {
		throw new TypeError("Work retirement authorization must be a function.");
	}
	if (authorizeGoalRetirement !== undefined && typeof authorizeGoalRetirement !== "function") {
		throw new TypeError("Goal retirement authorization must be a function.");
	}
	if (typeof access.authenticate !== "function" || typeof access.authorize !== "function") {
		throw new TypeError("Runtime access callbacks are required.");
	}
	const actions = options.actions;
	const approvals = options.approvals;
	const health = options.health;
	if (health !== undefined && typeof health.inspect !== "function") {
		throw new TypeError("A Runtime health inspect callback is required.");
	}
	return Object.freeze({
		runtime: Object.freeze({
			closeAuthority: runtime.closeAuthority.bind(runtime),
			readAuthorityClosure: runtime.readAuthorityClosure.bind(runtime),
			submit: runtime.submit.bind(runtime),
			readDeferred: runtime.readDeferred.bind(runtime),
			resumeDeferred: runtime.resumeDeferred.bind(runtime),
			abandonDeferred: runtime.abandonDeferred.bind(runtime),
			replayEvents: runtime.replayEvents.bind(runtime),
			control: runtime.control.bind(runtime),
			subscribe: runtime.subscribe.bind(runtime),
		}),
		access: Object.freeze({
			...(authorizeWorkRetirement === undefined
				? {}
				: { authorizeWorkRetirement: authorizeWorkRetirement.bind(access) }),
			...(authorizeGoalRetirement === undefined
				? {}
				: { authorizeGoalRetirement: authorizeGoalRetirement.bind(access) }),
			authenticate: access.authenticate.bind(access),
			authorize: access.authorize.bind(access),
		}),
		limits: prepareLimits(options.limits),
		...(actions
			? {
					actions: Object.freeze({
						changeRevision: actions.changeRevision.bind(actions),
						waitForChange: actions.waitForChange.bind(actions),
						recoverDelivery: actions.recoverDelivery.bind(actions),
						nextDelivery: actions.nextDelivery.bind(actions),
						get: actions.get.bind(actions),
						recordReceipt: actions.recordReceipt.bind(actions),
						reconcile: actions.reconcile.bind(actions),
					}),
				}
			: {}),
		...(approvals
			? {
					approvals: Object.freeze({
						changeRevision: approvals.changeRevision.bind(approvals),
						waitForChange: approvals.waitForChange.bind(approvals),
						listPending: approvals.listPending.bind(approvals),
						decide: approvals.decide.bind(approvals),
					}),
				}
			: {}),
		...(options.attachments ? { attachments: prepareAttachmentService(options.attachments) } : {}),
		...(health ? { health: Object.freeze({ inspect: health.inspect.bind(health) }) } : {}),
	});
}

function prepareLimits(limits: RuntimeServerLimits): Readonly<RuntimeServerLimits> {
	if (!limits) throw new TypeError("Runtime server limits are required.");
	const prepared = { ...limits };
	for (const name of RUNTIME_SERVER_LIMIT_NAMES) {
		const value = prepared[name];
		if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
	}
	if (prepared.actionStreamWaitMs > NODE_MAX_TIMER_DELAY_MS) {
		throw new TypeError(`actionStreamWaitMs must not exceed ${NODE_MAX_TIMER_DELAY_MS}.`);
	}
	if (prepared.maxApprovalWaitMilliseconds > NODE_MAX_TIMER_DELAY_MS) {
		throw new TypeError(`Approval wait limits must not exceed ${NODE_MAX_TIMER_DELAY_MS}.`);
	}
	if (prepared.maxResponseBytes < MINIMUM_RESPONSE_BYTES) {
		throw new TypeError("maxResponseBytes cannot represent the bounded response error.");
	}
	if (
		prepared.maxResponseBytes > bufferConstants.MAX_LENGTH ||
		prepared.maxSseEventBytes > bufferConstants.MAX_LENGTH
	) {
		throw new TypeError("Runtime server byte limits must be supported by the host platform.");
	}
	return Object.freeze(prepared);
}

function approvalCursor(approvals: readonly unknown[]): string {
	return createHash("sha256").update(canonicalizeJsonValue(approvals)).digest("hex");
}

function prepareAttachmentService(service: RuntimeAttachmentService) {
	if (
		!service ||
		typeof service.read !== "function" ||
		typeof service.authorize !== "function" ||
		!Number.isSafeInteger(service.maxResponseBytes) ||
		service.maxResponseBytes <= 0 ||
		service.maxResponseBytes > bufferConstants.MAX_LENGTH
	) {
		throw new TypeError("Attachment service callbacks and a platform-supported response byte limit are required.");
	}
	return Object.freeze({
		maxResponseBytes: service.maxResponseBytes,
		read: service.read.bind(service),
		authorize: service.authorize.bind(service),
	});
}

async function authenticate(
	controller: RuntimeAccessController,
	request: Request,
): Promise<RuntimePrincipal | undefined> {
	const principal = await controller.authenticate(request);
	if (!principal) return undefined;
	assertOpaqueIdentifier(principal.principalId);
	return Object.freeze({ principalId: principal.principalId });
}

class RuntimeAttachmentIntegrityError extends Error {
	constructor() {
		super("Attachment reader returned content that did not match the authorized reference.");
		this.name = "RuntimeAttachmentIntegrityError";
	}
}

function deferredTransitionError(context: Context, error: unknown, maxResponseBytes: number): Response {
	if (error instanceof RuntimeAdmissionClosedError) {
		return boundedErrorResponse(
			context,
			503,
			"runtime_stopping",
			"The runtime is not accepting new runs.",
			maxResponseBytes,
		);
	}
	if (error instanceof RuntimeDeferredNotFoundError) {
		return boundedErrorResponse(
			context,
			404,
			"deferred_not_found",
			"The deferred operation is not pending for this actor.",
			maxResponseBytes,
		);
	}
	if (error instanceof RuntimeDeferredBusyError) {
		return boundedErrorResponse(
			context,
			409,
			"deferred_busy",
			"The actor is completing a deferred transition.",
			maxResponseBytes,
		);
	}
	if (error instanceof RuntimeDeferredResumeUnavailableError) {
		return boundedErrorResponse(
			context,
			409,
			"deferred_resume_unavailable",
			"The deferred operation cannot be resumed by this actor configuration.",
			maxResponseBytes,
		);
	}
	throw error;
}

function actionTransitionError(context: Context, error: unknown, maxResponseBytes: number): Response {
	if (error instanceof ActionNotFoundError) {
		return boundedErrorResponse(
			context,
			404,
			"action_not_found",
			"The action operation was not found.",
			maxResponseBytes,
		);
	}
	if (error instanceof ActionClaimConflictError || error instanceof ActionStateConflictError) {
		return boundedErrorResponse(
			context,
			409,
			"action_conflict",
			"The action claim or state changed.",
			maxResponseBytes,
		);
	}
	throw error;
}

function boundedErrorResponse(
	context: Context,
	status: ContentfulStatusCode,
	code: string,
	message: string,
	maxResponseBytes: number,
): Response {
	return boundedJsonResponse(context, { error: { code, message } }, status, maxResponseBytes);
}

function boundedJsonResponse(
	context: Context,
	value: unknown,
	status: ContentfulStatusCode,
	maxResponseBytes: number,
): Response {
	const body = JSON.stringify(value);
	if (body === undefined || Buffer.byteLength(body) > maxResponseBytes) {
		return context.body(RESPONSE_TOO_LARGE_BODY, 500, jsonResponseHeaders(RESPONSE_TOO_LARGE_BODY));
	}
	return context.body(body, status, jsonResponseHeaders(body));
}

function jsonResponseHeaders(body: string): Record<string, string> {
	return {
		"Content-Length": String(Buffer.byteLength(body)),
		"Content-Type": "application/json; charset=UTF-8",
		"X-Content-Type-Options": "nosniff",
	};
}
