import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import {
	ActionRecordSchema,
	ActionApprovalRecordSchema,
	RunAdmissionAuthoritySchema,
	RunAuthorityGoalRetirementSchema,
	WorkAuthorityRetirementRequestSchema,
	RunAuthorityWorkRetirementResultSchema,
	RuntimeAuthorityCloseRequestSchema,
	RuntimeAuthorityReadRequestSchema,
	RuntimeAuthorityClosureSchema,
	RuntimeAuthorityReadResponseSchema,
	RuntimeAvailableDependencyHealthSchema,
	RuntimeAvailableDependencyReasonSchema,
	RuntimeActionDeliverySchema,
	RuntimeActionGetRequestSchema,
	RuntimeActionGetResponseSchema,
	RuntimeActionNextRequestSchema,
	RuntimeActionNextResponseSchema,
	RuntimeActionRecoveryDeliverySchema,
	RuntimeActionRecoveryRequestSchema,
	RuntimeActionRecoveryResponseSchema,
	RuntimeActionReceiptRequestSchema,
	RuntimeActionReconcileRequestSchema,
	RuntimeActionRecordResponseSchema,
	RuntimeActionSseFrameSchema,
	RuntimeApprovalDecisionRequestSchema,
	RuntimeApprovalDecisionResponseSchema,
	RuntimeApprovalListRequestSchema,
	RuntimeApprovalListResponseSchema,
	RuntimeApprovalWaitRequestSchema,
	RuntimeApprovalWaitResponseSchema,
	RuntimeAttachmentReadRequestSchema,
	RuntimeControlRequestSchema,
	RuntimeControlResultReasonSchema,
	RuntimeControlResultSchema,
	RuntimeDeferredAbandonResponseSchema,
	RuntimeDeferredCommandRequestSchema,
	RuntimeDeferredOperationSchema,
	RuntimeDeferredReadRequestSchema,
	RuntimeDeferredReadResponseSchema,
	RuntimeDeclaredDependencyHealthSchema,
	RuntimeDependencyKindSchema,
	RuntimeDependencyReasonSchema,
	RuntimeDependencyRequirementSchema,
	RuntimeErrorSchema,
	RuntimeEventPageRequestSchema,
	RuntimeEventReplaySchema,
	RuntimeHealthSnapshotSchema,
	RuntimeHealthStatusSchema,
	RuntimeImpairedDependencyHealthSchema,
	RuntimeInitializeResponseSchema,
	RuntimeReadyDependencyHealthSchema,
	RuntimeRunAcceptedSchema,
	RuntimeRunRequestSchema,
	RuntimeSseFrameSchema,
} from "../packages/protocol/dist/index.js";

const reference = (name) => ({ $ref: `#/components/schemas/${name}` });
const jsonContent = (schema) => ({ content: { "application/json": { schema } } });
const errorResponses = {
	400: { description: "Invalid request", ...jsonContent(reference("RuntimeError")) },
	401: { description: "Authentication required", ...jsonContent(reference("RuntimeError")) },
	403: { description: "Actor access denied", ...jsonContent(reference("RuntimeError")) },
	500: { description: "Internal runtime failure", ...jsonContent(reference("RuntimeError")) },
};
const requestBodyErrorResponses = {
	...errorResponses,
	413: { description: "Request body limit exceeded", ...jsonContent(reference("RuntimeError")) },
};
const runtimeRunRequestOpenApiSchema = {
	...RuntimeRunRequestSchema,
	properties: { ...RuntimeRunRequestSchema.properties, admissionAuthority: reference("RuntimeRunAdmissionAuthority") },
};
const runtimeAuthorityCloseRequestOpenApiSchema = {
	...RuntimeAuthorityCloseRequestSchema,
	properties: {
		...RuntimeAuthorityCloseRequestSchema.properties,
		goalRetirement: reference("RuntimeAuthorityGoalRetirement"),
		workRetirement: reference("RuntimeAuthorityWorkRetirement"),
	},
};
const runtimeAuthorityClosureOpenApiSchema = {
	...RuntimeAuthorityClosureSchema,
	properties: {
		...RuntimeAuthorityClosureSchema.properties,
		goalRetirement: reference("RuntimeAuthorityGoalRetirement"),
		workRetirement: reference("RuntimeAuthorityWorkRetirement"),
		workRetirementResult: reference("RuntimeAuthorityWorkRetirementResult"),
	},
};
const runtimeAuthorityReadResponseOpenApiSchema = {
	...RuntimeAuthorityReadResponseSchema,
	properties: { closure: { anyOf: [reference("RuntimeAuthorityClosure"), { type: "null" }] } },
};
const runtimeDeferredReadResponseOpenApiSchema = {
	...RuntimeDeferredReadResponseSchema,
	properties: {
		...RuntimeDeferredReadResponseSchema.properties,
		deferred: { anyOf: [reference("RuntimeDeferredOperation"), { type: "null" }] },
	},
};
const runtimeActionDeliveryOpenApiSchema = {
	...RuntimeActionDeliverySchema,
	properties: {
		...RuntimeActionDeliverySchema.properties,
		action: reference("RuntimeActionRecord"),
	},
};
const runtimeActionNextResponseOpenApiSchema = {
	...RuntimeActionNextResponseSchema,
	properties: {
		delivery: { anyOf: [reference("RuntimeActionDelivery"), { type: "null" }] },
	},
};
const runtimeActionRecoveryDeliveryOpenApiSchema = {
	...RuntimeActionRecoveryDeliverySchema,
	properties: {
		...RuntimeActionRecoveryDeliverySchema.properties,
		action: reference("RuntimeActionRecord"),
	},
};
const runtimeActionRecoveryResponseOpenApiSchema = {
	...RuntimeActionRecoveryResponseSchema,
	properties: {
		delivery: { anyOf: [reference("RuntimeActionRecoveryDelivery"), { type: "null" }] },
	},
};
const runtimeActionSseFrameOpenApiSchema = {
	...RuntimeActionSseFrameSchema,
	properties: {
		...RuntimeActionSseFrameSchema.properties,
		delivery: reference("RuntimeActionDelivery"),
	},
};
const runtimeApprovalListResponseOpenApiSchema = {
	...RuntimeApprovalListResponseSchema,
	properties: { approvals: { type: "array", items: reference("RuntimeApproval") } },
};
const runtimeApprovalWaitResponseOpenApiSchema = {
	...RuntimeApprovalWaitResponseSchema,
	properties: {
		...RuntimeApprovalWaitResponseSchema.properties,
		approvals: { type: "array", items: reference("RuntimeApproval") },
	},
};
const runtimeApprovalDecisionResponseOpenApiSchema = {
	...RuntimeApprovalDecisionResponseSchema,
	properties: { approval: reference("RuntimeApproval") },
};
const runtimeActionGetResponseOpenApiSchema = {
	...RuntimeActionGetResponseSchema,
	properties: {
		action: { anyOf: [reference("RuntimeActionRecord"), { type: "null" }] },
	},
};
const runtimeActionRecordResponseOpenApiSchema = {
	...RuntimeActionRecordResponseSchema,
	properties: {
		action: reference("RuntimeActionRecord"),
	},
};
const runtimeControlResultFailureOpenApiSchema = RuntimeControlResultSchema.anyOf[1];
const runtimeControlResultOpenApiSchema = {
	...RuntimeControlResultSchema,
	anyOf: [
		RuntimeControlResultSchema.anyOf[0],
		{
			...runtimeControlResultFailureOpenApiSchema,
			properties: {
				...runtimeControlResultFailureOpenApiSchema.properties,
				reason: reference("RuntimeControlResultReason"),
			},
		},
	],
};
const runtimeDependencyIdentityOpenApiProperties = {
	id: RuntimeDeclaredDependencyHealthSchema.properties.id,
	kind: reference("RuntimeDependencyKind"),
	requirement: reference("RuntimeDependencyRequirement"),
};
const runtimeDeclaredDependencyHealthOpenApiSchema = {
	...RuntimeDeclaredDependencyHealthSchema,
	properties: {
		...RuntimeDeclaredDependencyHealthSchema.properties,
		...runtimeDependencyIdentityOpenApiProperties,
	},
};
const runtimeAvailableDependencyHealthOpenApiSchema = {
	...RuntimeAvailableDependencyHealthSchema,
	properties: {
		...RuntimeAvailableDependencyHealthSchema.properties,
		...runtimeDependencyIdentityOpenApiProperties,
		reason: reference("RuntimeAvailableDependencyReason"),
	},
};
const runtimeReadyDependencyHealthOpenApiSchema = {
	...RuntimeReadyDependencyHealthSchema,
	properties: {
		...RuntimeReadyDependencyHealthSchema.properties,
		...runtimeDependencyIdentityOpenApiProperties,
	},
};
const runtimeImpairedDependencyHealthOpenApiSchema = {
	...RuntimeImpairedDependencyHealthSchema,
	properties: {
		...RuntimeImpairedDependencyHealthSchema.properties,
		...runtimeDependencyIdentityOpenApiProperties,
		reason: reference("RuntimeDependencyReason"),
	},
};
const runtimeDependencyHealthOpenApiSchema = {
	anyOf: [
		reference("RuntimeDeclaredDependencyHealth"),
		reference("RuntimeAvailableDependencyHealth"),
		reference("RuntimeReadyDependencyHealth"),
		reference("RuntimeImpairedDependencyHealth"),
	],
};
const runtimeHealthSnapshotOpenApiSchema = {
	...RuntimeHealthSnapshotSchema,
	properties: {
		...RuntimeHealthSnapshotSchema.properties,
		status: reference("RuntimeHealthStatus"),
		dependencies: { type: "array", items: reference("RuntimeDependencyHealth") },
	},
};

const document = {
	openapi: "3.1.0",
	info: {
		title: "OpenGameAgent Runtime API",
		version: "1.0.0-alpha.0",
	},
	security: [{ bearerAuth: [] }],
	paths: {
		"/runtime/v1/initialize": {
			post: {
				operationId: "initializeRuntime",
				responses: {
					200: { description: "Negotiated capabilities", ...jsonContent(reference("RuntimeInitializeResponse")) },
					401: errorResponses[401],
					500: errorResponses[500],
				},
			},
		},
		"/runtime/v1/health": {
			get: {
				operationId: "readRuntimeHealth",
				responses: {
					200: {
						description: "Authenticated bounded Runtime dependency health",
						headers: {
							"Cache-Control": { schema: { type: "string", enum: ["private, no-store"] } },
						},
						...jsonContent(reference("RuntimeHealthSnapshot")),
					},
					401: errorResponses[401],
					500: errorResponses[500],
					501: { description: "Runtime health unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/runs": {
			post: {
				operationId: "submitRun",
				requestBody: { required: true, ...jsonContent(reference("RuntimeRunRequest")) },
				responses: {
					202: { description: "Run admitted", ...jsonContent(reference("RuntimeRunAccepted")) },
					...requestBodyErrorResponses,
					400: {
						description: "Invalid request or runtime payload depth limit exceeded (input_too_deep)",
						...jsonContent(reference("RuntimeError")),
					},
					413: {
						description: "Request body or runtime payload byte limit exceeded (body_too_large or input_too_large)",
						...jsonContent(reference("RuntimeError")),
					},
					409: {
						description: "Input identifier or deferred state conflict",
						...jsonContent(reference("RuntimeError")),
					},
					429: { description: "Run queue full", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/deferred/read": {
			post: {
				operationId: "readDeferred",
				requestBody: { required: true, ...jsonContent(reference("RuntimeDeferredReadRequest")) },
				responses: {
					200: {
						description: "Current safe deferred operation projection or an empty result",
						...jsonContent(reference("RuntimeDeferredReadResponse")),
					},
					...requestBodyErrorResponses,
				},
			},
		},
		"/runtime/v1/authority/close": {
			post: {
				operationId: "closeAuthority",
				requestBody: { required: true, ...jsonContent(reference("RuntimeAuthorityCloseRequest")) },
				responses: {
					200: {
						description: "Durable scope closure or its identical replay",
						...jsonContent(reference("RuntimeAuthorityClosure")),
					},
					...requestBodyErrorResponses,
					409: { description: "Authority closure identity conflict", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/authority/read": {
			post: {
				operationId: "readAuthorityClosure",
				requestBody: { required: true, ...jsonContent(reference("RuntimeAuthorityReadRequest")) },
				responses: {
					200: {
						description: "Exact scope closure or an empty result",
						...jsonContent(reference("RuntimeAuthorityReadResponse")),
					},
					...requestBodyErrorResponses,
				},
			},
		},
		"/runtime/v1/deferred/resume": {
			post: {
				operationId: "resumeDeferred",
				requestBody: { required: true, ...jsonContent(reference("RuntimeDeferredCommandRequest")) },
				responses: {
					202: { description: "Deferred fetch admitted", ...jsonContent(reference("RuntimeRunAccepted")) },
					...requestBodyErrorResponses,
					404: { description: "Deferred operation not pending", ...jsonContent(reference("RuntimeError")) },
					409: { description: "Deferred transition conflict", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/deferred/abandon": {
			post: {
				operationId: "abandonDeferred",
				requestBody: { required: true, ...jsonContent(reference("RuntimeDeferredCommandRequest")) },
				responses: {
					200: {
						description: "Authoritative local abandonment and provider cancellation result",
						...jsonContent(reference("RuntimeDeferredAbandonResponse")),
					},
					...requestBodyErrorResponses,
					404: { description: "Deferred operation not pending", ...jsonContent(reference("RuntimeError")) },
					409: { description: "Deferred transition conflict", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/events": {
			post: {
				operationId: "readEvents",
				requestBody: { required: true, ...jsonContent(reference("RuntimeEventPageRequest")) },
				responses: {
					200: {
						description: "Projected replay page or reconciliation gap",
						...jsonContent(reference("RuntimeEventReplay")),
					},
					...requestBodyErrorResponses,
				},
			},
		},
		"/runtime/v1/actions/next": {
			post: {
				operationId: "nextAction",
				requestBody: { required: true, ...jsonContent(reference("RuntimeActionNextRequest")) },
				responses: {
					200: {
						description: "Next atomic dispatch or reconciliation delivery, or an empty result",
						...jsonContent(reference("RuntimeActionNextResponse")),
					},
					...requestBodyErrorResponses,
					501: { description: "Action exchange unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/actions/recovery": {
			post: {
				operationId: "recoverAction",
				requestBody: { required: true, ...jsonContent(reference("RuntimeActionRecoveryRequest")) },
				responses: {
					200: {
						description: "Oldest unsettled reconciliation delivery, or an empty result",
						...jsonContent(reference("RuntimeActionRecoveryResponse")),
					},
					...requestBodyErrorResponses,
					501: { description: "Action exchange unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/actions/get": {
			post: {
				operationId: "getAction",
				requestBody: { required: true, ...jsonContent(reference("RuntimeActionGetRequest")) },
				responses: {
					200: {
						description: "Durable action record or an empty result",
						...jsonContent(reference("RuntimeActionGetResponse")),
					},
					...requestBodyErrorResponses,
					501: { description: "Action exchange unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/actions/stream": {
			post: {
				operationId: "streamActions",
				requestBody: { required: true, ...jsonContent(reference("RuntimeActionNextRequest")) },
				responses: {
					200: {
						description: "Durable action dispatch and reconcile stream",
						content: { "text/event-stream": { schema: reference("RuntimeActionSseFrame") } },
					},
					...requestBodyErrorResponses,
					501: { description: "Action exchange unavailable", ...jsonContent(reference("RuntimeError")) },
					503: { description: "Stream capacity reached", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/actions/receipt": {
			post: {
				operationId: "recordActionReceipt",
				requestBody: { required: true, ...jsonContent(reference("RuntimeActionReceiptRequest")) },
				responses: {
					200: { description: "Settled durable action", ...jsonContent(reference("RuntimeActionRecordResponse")) },
					...requestBodyErrorResponses,
					404: { description: "Action not found", ...jsonContent(reference("RuntimeError")) },
					409: { description: "Action claim or state conflict", ...jsonContent(reference("RuntimeError")) },
					501: { description: "Action exchange unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/actions/reconcile": {
			post: {
				operationId: "reconcileAction",
				requestBody: { required: true, ...jsonContent(reference("RuntimeActionReconcileRequest")) },
				responses: {
					200: {
						description: "Authoritatively reconciled action",
						...jsonContent(reference("RuntimeActionRecordResponse")),
					},
					...requestBodyErrorResponses,
					404: { description: "Action not found", ...jsonContent(reference("RuntimeError")) },
					409: { description: "Action state conflict", ...jsonContent(reference("RuntimeError")) },
					501: { description: "Action exchange unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/approvals": {
			post: {
				operationId: "listApprovals",
				requestBody: { required: true, ...jsonContent(reference("RuntimeApprovalListRequest")) },
				responses: {
					200: { description: "Pending approvals", ...jsonContent(reference("RuntimeApprovalListResponse")) },
					...requestBodyErrorResponses,
					501: { description: "Approvals unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/approvals/decision": {
			post: {
				operationId: "decideApproval",
				requestBody: { required: true, ...jsonContent(reference("RuntimeApprovalDecisionRequest")) },
				responses: {
					200: {
						description: "Durable approval decision",
						...jsonContent(reference("RuntimeApprovalDecisionResponse")),
					},
					...requestBodyErrorResponses,
					404: { description: "Approval not found", ...jsonContent(reference("RuntimeError")) },
					409: { description: "Approval decision conflict", ...jsonContent(reference("RuntimeError")) },
					501: { description: "Approvals unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/approvals/wait": {
			post: {
				operationId: "waitApprovals",
				requestBody: { required: true, ...jsonContent(reference("RuntimeApprovalWaitRequest")) },
				responses: {
					200: {
						description: "Pending approvals at the current cursor",
						...jsonContent(reference("RuntimeApprovalWaitResponse")),
					},
					...requestBodyErrorResponses,
					501: { description: "Approvals unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/control": {
			post: {
				operationId: "controlRun",
				requestBody: { required: true, ...jsonContent(reference("RuntimeControlRequest")) },
				responses: {
					200: { description: "Exact control result", ...jsonContent(reference("RuntimeControlResult")) },
					...requestBodyErrorResponses,
				},
			},
		},
		"/runtime/v1/attachments/read": {
			post: {
				operationId: "readAttachment",
				requestBody: { required: true, ...jsonContent(reference("RuntimeAttachmentReadRequest")) },
				responses: {
					200: {
						description: "Authorized verified attachment bytes",
						headers: {
							"Cache-Control": { schema: { type: "string", enum: ["private, no-store"] } },
							"Content-Length": { schema: { type: "integer", minimum: 1 } },
							"Content-Digest": {
								schema: { type: "string", pattern: "^sha-256=:[A-Za-z0-9+/]{43}=:$" },
							},
							ETag: { schema: { type: "string", pattern: '^"img_[0-9a-f]{64}"$' } },
							"X-Content-Type-Options": { schema: { type: "string", enum: ["nosniff"] } },
						},
						content: { "image/png": { schema: { type: "string", format: "binary" } } },
					},
					...requestBodyErrorResponses,
					413: {
						description: "Request body or attachment response limit exceeded",
						...jsonContent(reference("RuntimeError")),
					},
					501: { description: "Attachment reads unavailable", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
		"/runtime/v1/events/stream": {
			get: {
				operationId: "streamEvents",
				parameters: [
					{ in: "query", name: "sessionId", required: true, schema: { type: "string", minLength: 1 } },
					{ in: "query", name: "actorId", required: true, schema: { type: "string", minLength: 1 } },
					{
						in: "query",
						name: "afterRevision",
						required: false,
						schema: { type: "string", pattern: "^(0|[1-9][0-9]*)$" },
					},
					{
						in: "header",
						name: "Last-Event-ID",
						required: false,
						schema: { type: "string", pattern: "^(0|[1-9][0-9]*)$" },
					},
				],
				responses: {
					200: {
						description: "Resumable projected event stream",
						content: { "text/event-stream": { schema: reference("RuntimeSseFrame") } },
					},
					...errorResponses,
					503: { description: "Stream capacity reached", ...jsonContent(reference("RuntimeError")) },
				},
			},
		},
	},
	components: {
		securitySchemes: { bearerAuth: { type: "http", scheme: "bearer" } },
		schemas: {
			RuntimeInitializeResponse: RuntimeInitializeResponseSchema,
			RuntimeDependencyKind: RuntimeDependencyKindSchema,
			RuntimeDependencyRequirement: RuntimeDependencyRequirementSchema,
			RuntimeDependencyReason: RuntimeDependencyReasonSchema,
			RuntimeAvailableDependencyReason: RuntimeAvailableDependencyReasonSchema,
			RuntimeDeclaredDependencyHealth: runtimeDeclaredDependencyHealthOpenApiSchema,
			RuntimeAvailableDependencyHealth: runtimeAvailableDependencyHealthOpenApiSchema,
			RuntimeReadyDependencyHealth: runtimeReadyDependencyHealthOpenApiSchema,
			RuntimeImpairedDependencyHealth: runtimeImpairedDependencyHealthOpenApiSchema,
			RuntimeDependencyHealth: runtimeDependencyHealthOpenApiSchema,
			RuntimeHealthStatus: RuntimeHealthStatusSchema,
			RuntimeHealthSnapshot: runtimeHealthSnapshotOpenApiSchema,
			RuntimeRunRequest: runtimeRunRequestOpenApiSchema,
			RuntimeRunAdmissionAuthority: RunAdmissionAuthoritySchema,
			RuntimeAuthorityGoalRetirement: RunAuthorityGoalRetirementSchema,
			RuntimeAuthorityWorkRetirement: WorkAuthorityRetirementRequestSchema,
			RuntimeAuthorityWorkRetirementResult: RunAuthorityWorkRetirementResultSchema,
			RuntimeAuthorityCloseRequest: runtimeAuthorityCloseRequestOpenApiSchema,
			RuntimeAuthorityReadRequest: RuntimeAuthorityReadRequestSchema,
			RuntimeAuthorityClosure: runtimeAuthorityClosureOpenApiSchema,
			RuntimeAuthorityReadResponse: runtimeAuthorityReadResponseOpenApiSchema,
			RuntimeRunAccepted: RuntimeRunAcceptedSchema,
			RuntimeDeferredOperation: RuntimeDeferredOperationSchema,
			RuntimeDeferredReadRequest: RuntimeDeferredReadRequestSchema,
			RuntimeDeferredReadResponse: runtimeDeferredReadResponseOpenApiSchema,
			RuntimeDeferredCommandRequest: RuntimeDeferredCommandRequestSchema,
			RuntimeDeferredAbandonResponse: RuntimeDeferredAbandonResponseSchema,
			RuntimeEventPageRequest: RuntimeEventPageRequestSchema,
			RuntimeEventReplay: RuntimeEventReplaySchema,
			RuntimeControlRequest: RuntimeControlRequestSchema,
			RuntimeControlResultReason: RuntimeControlResultReasonSchema,
			RuntimeControlResult: runtimeControlResultOpenApiSchema,
			RuntimeSseFrame: RuntimeSseFrameSchema,
			RuntimeError: RuntimeErrorSchema,
			RuntimeActionRecord: ActionRecordSchema,
			RuntimeActionDelivery: runtimeActionDeliveryOpenApiSchema,
			RuntimeActionGetRequest: RuntimeActionGetRequestSchema,
			RuntimeActionGetResponse: runtimeActionGetResponseOpenApiSchema,
			RuntimeActionNextRequest: RuntimeActionNextRequestSchema,
			RuntimeActionNextResponse: runtimeActionNextResponseOpenApiSchema,
			RuntimeActionRecoveryDelivery: runtimeActionRecoveryDeliveryOpenApiSchema,
			RuntimeActionRecoveryRequest: RuntimeActionRecoveryRequestSchema,
			RuntimeActionRecoveryResponse: runtimeActionRecoveryResponseOpenApiSchema,
			RuntimeActionReceiptRequest: RuntimeActionReceiptRequestSchema,
			RuntimeActionReconcileRequest: RuntimeActionReconcileRequestSchema,
			RuntimeActionRecordResponse: runtimeActionRecordResponseOpenApiSchema,
			RuntimeActionSseFrame: runtimeActionSseFrameOpenApiSchema,
			RuntimeApprovalListRequest: RuntimeApprovalListRequestSchema,
			RuntimeApproval: ActionApprovalRecordSchema,
			RuntimeApprovalListResponse: runtimeApprovalListResponseOpenApiSchema,
			RuntimeApprovalWaitRequest: RuntimeApprovalWaitRequestSchema,
			RuntimeApprovalWaitResponse: runtimeApprovalWaitResponseOpenApiSchema,
			RuntimeApprovalDecisionRequest: RuntimeApprovalDecisionRequestSchema,
			RuntimeApprovalDecisionResponse: runtimeApprovalDecisionResponseOpenApiSchema,
			RuntimeAttachmentReadRequest: RuntimeAttachmentReadRequestSchema,
		},
	},
};

const outputPath = resolve("protocol/runtime-v1.openapi.json");
mkdirSync(dirname(outputPath), { recursive: true });
writeFileSync(outputPath, `${JSON.stringify(document, null, 2)}\n`, "utf8");
