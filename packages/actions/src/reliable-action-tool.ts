import type { AgentTool, AgentToolResult, AgentToolUpdateCallback } from "@earendil-works/pi-agent-core";
import {
	DURABLE_TOOL_EXECUTION,
	type DurableExecutionAgentTool,
	DurableToolExecutionUnresolvedError,
	measureRunPhase,
	type PreparedDurableToolExecution,
	type TranscriptStartedToolRecovery,
} from "@opengameagent/agent";
import type { JsonValueLimits, RunCoordinate, StreamKey } from "@opengameagent/protocol";
import type { TSchema } from "typebox";
import { type ApprovedActionAdmission, approvalRevisionForAdmission } from "./admission.js";
import {
	type ActionApprovalPresentationDraft,
	type ActionApprovalCoordinator,
	ActionApprovalDeniedError,
	type ActionApprovalPolicy,
	type ActionApprovalRecord,
	type ActionInvocationScope,
	evaluateActionPolicy,
} from "./approvals.js";
import type { ActionCoordinator } from "./coordinator.js";
import { actionOperationId, fingerprintActionPayload, isActionOperationId } from "./identity.js";
import type {
	ActionAuthority,
	ActionGoalProjectionBinding,
	ActionIntentDraft,
	ActionRecord,
	ActionReplayPolicy,
} from "./types.js";

export interface ReliableActionToolDetails {
	operationId: string;
	status: ActionRecord["status"];
}

export interface ReliableActionTranscriptRecoveryOptions {
	actions: ActionCoordinator;
	now?: () => number;
}

export interface ReliableActionApprovalPresentation<TParameters> {
	limits: JsonValueLimits;
	project(parameters: TParameters, signal: AbortSignal): unknown | Promise<unknown>;
}

export interface ReliableActionApprovalBridge<TParameters = unknown> {
	coordinator: ActionApprovalCoordinator;
	admission: ApprovedActionAdmission;
	durationMs: number;
	presentation?: ReliableActionApprovalPresentation<TParameters>;
	waitForDecision(approval: ActionApprovalRecord, signal: AbortSignal): Promise<ActionApprovalRecord>;
}

type ReliableActionParameters<TParameters extends TSchema> = Parameters<AgentTool<TParameters>["execute"]>[1];

type ReliableActionAgentToolBase<TParameters extends TSchema> = DurableExecutionAgentTool<
	AgentTool<TParameters, ReliableActionToolDetails>
>;

export type ReliableActionAgentTool<TParameters extends TSchema> = ReliableActionAgentToolBase<TParameters> &
	Required<Pick<ReliableActionAgentToolBase<TParameters>, typeof DURABLE_TOOL_EXECUTION>> & {
		readonly executionMode: "sequential";
	};

export interface ReliableActionToolOptions<TParameters extends TSchema>
	extends Omit<AgentTool<TParameters, ReliableActionToolDetails>, "execute" | "executionMode">,
		StreamKey {
	actions: ActionCoordinator;
	policy: ActionApprovalPolicy;
	approval?: ReliableActionApprovalBridge<ReliableActionParameters<TParameters>>;
	readCoordinate(): RunCoordinate;
	readAuthority(
		parameters: ReliableActionParameters<TParameters>,
		signal: AbortSignal,
	): ActionAuthority | Promise<ActionAuthority>;
	resolveGoalProjection?(
		parameters: ReliableActionParameters<TParameters>,
		signal: AbortSignal,
	): ActionGoalProjectionBinding | undefined | Promise<ActionGoalProjectionBinding | undefined>;
	resolveScope(
		parameters: ReliableActionParameters<TParameters>,
		signal: AbortSignal,
	): ActionInvocationScope | Promise<ActionInvocationScope>;
	operationKey(toolCallId: string, parameters: ReliableActionParameters<TParameters>): string;
	conflictKeys(parameters: ReliableActionParameters<TParameters>): readonly string[];
	replayPolicy: ActionReplayPolicy | ((parameters: ReliableActionParameters<TParameters>) => ActionReplayPolicy);
	waitForSettlement(
		action: ActionRecord,
		signal: AbortSignal,
		onUpdate?: AgentToolUpdateCallback<ReliableActionToolDetails>,
	): Promise<ActionRecord>;
}

export function createReliableActionTool<TParameters extends TSchema>(
	options: ReliableActionToolOptions<TParameters>,
): ReliableActionAgentTool<TParameters> {
	const prepareExecution = async (
		toolCallId: string,
		parameters: ReliableActionParameters<TParameters>,
		providedSignal?: AbortSignal,
	): Promise<PreparedDurableToolExecution<AgentTool<TParameters, ReliableActionToolDetails>>> => {
		const signal = providedSignal ?? new AbortController().signal;
		if (signal.aborted) throw signal.reason;
		const operationKey = options.operationKey(toolCallId, parameters);
		const readCoordinate = options.readCoordinate();
		const coordinate = Object.freeze({
			sessionId: readCoordinate.sessionId,
			actorId: readCoordinate.actorId,
			runId: readCoordinate.runId,
			turn: readCoordinate.turn,
		});
		if (coordinate.sessionId !== options.sessionId || coordinate.actorId !== options.actorId) {
			throw new TypeError("The reliable action Tool coordinate does not match its actor.");
		}
		const operationId = actionOperationId({
			sessionId: options.sessionId,
			actorId: options.actorId,
			coordinate,
			actionName: options.name,
			operationKey,
		});
		return {
			operationId,
			execute: async (onUpdate) => {
				signal.throwIfAborted();
				const scope = await options.resolveScope(parameters, signal);
				signal.throwIfAborted();
				const approvalTaskId = scope.taskId;
				const policy = evaluateActionPolicy(options.policy, options.name, scope);
				let approval: ReliableActionApprovalBridge | undefined;
				if (!policy.authorized) {
					if (policy.reason !== "approval-required") throw new ActionApprovalDeniedError(policy.reason);
					approval = options.approval;
					if (!approval) throw new ActionApprovalDeniedError("approval-required");
				}
				const expectedAuthority = await options.readAuthority(parameters, signal);
				signal.throwIfAborted();
				const goalProjection = await options.resolveGoalProjection?.(parameters, signal);
				signal.throwIfAborted();
				const replayPolicy =
					typeof options.replayPolicy === "function" ? options.replayPolicy(parameters) : options.replayPolicy;
				const draft: ActionIntentDraft = {
					sessionId: options.sessionId,
					actorId: options.actorId,
					coordinate,
					actionName: options.name,
					operationKey,
					payload: parameters,
					expectedAuthority,
					conflictKeys: options.conflictKeys(parameters),
					replayPolicy,
					...(goalProjection === undefined ? {} : { goalProjection }),
				};
				const prepared = Object.freeze(options.actions.prepare(draft));
				const preparedOperationId = prepared.operationId;
				const preparedIntentFingerprint = prepared.intentFingerprint;
				let approvedAdmission:
					| {
							approval: ReliableActionApprovalBridge<ReliableActionParameters<TParameters>>;
							approvalId: string;
							expectedRevision: string;
							currentAuthority: ActionAuthority;
					  }
					| undefined;
				if (approval) {
					const projection = approval.presentation;
					let presentation: ActionApprovalPresentationDraft | undefined;
					if (projection !== undefined) {
						const configuredLimits = projection.limits;
						const maxBytes = configuredLimits.maxBytes;
						const maxDepth = configuredLimits.maxDepth;
						presentation = {
							limits: { maxBytes, maxDepth },
							value: await projection.project(
								structuredClone(prepared.payload) as ReliableActionParameters<TParameters>,
								signal,
							),
						};
					}
					signal.throwIfAborted();
					const pending = await approval.coordinator.request(options.policy.policyId, prepared, approval.durationMs, {
						...(approvalTaskId === undefined ? {} : { taskId: approvalTaskId }),
						...(presentation === undefined ? {} : { presentation }),
					});
					signal.throwIfAborted();
					const decision = await measureRunPhase("approval", () => approval.waitForDecision(pending, signal));
					signal.throwIfAborted();
					if (decision.status !== "approved" && decision.status !== "consumed") {
						throw new ActionApprovalDeniedError("approval-required");
					}
					if (
						decision.request.approvalId !== pending.request.approvalId ||
						decision.request.requestFingerprint !== pending.request.requestFingerprint
					) {
						throw new TypeError("The approval decision belongs to a different approval request.");
					}
					const approvalId = decision.request.approvalId;
					const expectedRevision = approvalRevisionForAdmission(decision);
					const readCurrentAuthority = await options.readAuthority(parameters, signal);
					signal.throwIfAborted();
					const currentAuthority = snapshotAuthority(readCurrentAuthority);
					approvedAdmission = { approval, approvalId, expectedRevision, currentAuthority };
				}
				signal.throwIfAborted();
				let submitted: ActionRecord;
				if (approvedAdmission) {
					let admitted: Awaited<ReturnType<ApprovedActionAdmission["admit"]>>;
					try {
						admitted = await approvedAdmission.approval.admission.admit({
							sessionId: options.sessionId,
							actorId: options.actorId,
							coordinate,
							approvalId: approvedAdmission.approvalId,
							expectedRevision: approvedAdmission.expectedRevision,
							operationId: preparedOperationId,
							intentFingerprint: preparedIntentFingerprint,
							currentAuthority: approvedAdmission.currentAuthority,
							intent: prepared,
						});
					} catch (error) {
						throwUnresolvedActionExecution(error, signal);
					}
					signal.throwIfAborted();
					if (admitted.approval.status !== "consumed" && !admitted.action) {
						throw new ActionApprovalDeniedError("approval-required");
					}
					if (admitted.approval.status !== "consumed" || !admitted.action) {
						throwUnresolvedActionExecution(
							new TypeError("The approved admission returned an inconsistent Action result."),
							signal,
						);
					}
					submitted = admitted.action;
				} else {
					try {
						submitted = await options.actions.submitPrepared(prepared);
					} catch (error) {
						throwUnresolvedActionExecution(error, signal);
					}
					signal.throwIfAborted();
				}
				try {
					if (
						submitted.operationId !== preparedOperationId ||
						submitted.intentFingerprint !== preparedIntentFingerprint
					) {
						throw new TypeError("The action admission returned a different intent.");
					}
					const submittedOperationId = submitted.operationId;
					const submittedIntentFingerprint = submitted.intentFingerprint;
					const settled = await measureRunPhase("settlement", () =>
						options.waitForSettlement(submitted, signal, onUpdate),
					);
					signal.throwIfAborted();
					if (
						settled.operationId !== submittedOperationId ||
						settled.intentFingerprint !== submittedIntentFingerprint
					) {
						throw new TypeError("The reliable action settlement bridge returned a different action.");
					}
					if (settled.status === "pending" || settled.status === "claimed") {
						throw new TypeError("The reliable action settlement bridge returned a non-terminal action.");
					}
					return modelSafeActionResult(settled);
				} catch (error) {
					throwUnresolvedActionExecution(error, signal);
				}
			},
		};
	};
	return {
		name: options.name,
		label: options.label,
		description: options.description,
		parameters: options.parameters,
		executionMode: "sequential",
		...(options.prepareArguments === undefined ? {} : { prepareArguments: options.prepareArguments }),
		[DURABLE_TOOL_EXECUTION]: prepareExecution,
		execute: async (toolCallId, parameters, signal, onUpdate) =>
			(await prepareExecution(toolCallId, parameters, signal)).execute(onUpdate),
	};
}

function throwUnresolvedActionExecution(error: unknown, signal: AbortSignal): never {
	if (signal.aborted) throw signal.reason;
	if (error instanceof DurableToolExecutionUnresolvedError) throw error;
	throw new DurableToolExecutionUnresolvedError(error);
}

function snapshotAuthority(authority: ActionAuthority): Readonly<ActionAuthority> {
	return Object.freeze({
		timelineId: authority.timelineId,
		generation: authority.generation,
		revision: authority.revision,
	});
}

export function modelSafeActionResult(record: ActionRecord) {
	const projection = {
		operationId: record.operationId,
		status: record.status,
		...(record.receipt?.modelResult === undefined ? {} : { result: record.receipt.modelResult }),
	};
	return {
		content: [{ type: "text", text: JSON.stringify(projection) }],
		details: { operationId: record.operationId, status: record.status },
	} satisfies AgentToolResult<ReliableActionToolDetails>;
}

/** Query the durable action authority for a started transcript Tool; never submits or replays an action. */
export function createReliableActionTranscriptRecovery(
	options: ReliableActionTranscriptRecoveryOptions,
): TranscriptStartedToolRecovery {
	const now = options.now ?? Date.now;
	return async ({ start, toolCall }) => {
		const operationId = start.operationId;
		if (!isActionOperationId(operationId)) return undefined;
		if (toolCall.id !== start.toolCallId || toolCall.name !== start.toolName) {
			throw new TypeError("The transcript Tool start does not match its assistant call.");
		}
		const action = await options.actions.get(start.coordinate, operationId);
		if (!action) {
			return {
				outcome: "not_started",
				content: [
					{
						type: "text",
						text: JSON.stringify({ code: "transcript_action_not_started", operationId }),
					},
				],
				isError: true,
				timestamp: now(),
			};
		}
		if (
			action.actionName !== start.toolName ||
			action.coordinate.runId !== start.coordinate.runId ||
			action.coordinate.turn !== start.coordinate.turn ||
			action.payloadFingerprint !== fingerprintActionPayload(start.arguments)
		) {
			throw new TypeError("The durable action does not match the transcript Tool start.");
		}
		if (action.status === "succeeded" || action.status === "failed") {
			const projected = modelSafeActionResult(action);
			return {
				outcome: "completed",
				content: projected.content.map((part) => ({ type: "text" as const, text: part.text })),
				isError: false,
				timestamp: Date.parse(action.receipt?.receivedAt ?? action.updatedAt),
			};
		}
		return {
			outcome: "outcome_unknown",
			content: [
				{
					type: "text",
					text: JSON.stringify({ code: "transcript_action_outcome_unknown", operationId, status: action.status }),
				},
			],
			isError: true,
			timestamp: Date.parse(action.updatedAt),
		};
	};
}
