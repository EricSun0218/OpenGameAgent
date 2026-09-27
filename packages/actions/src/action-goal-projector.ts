import {
	assertCanonicalTimestamp,
	assertGameTime,
	assertOpaqueIdentifier,
	type GameTime,
	type StreamKey,
} from "@opengameagent/protocol";
import type { ActionCoordinator } from "./coordinator.js";
import type { ActionGoalProjectionBinding, ActionRecord } from "./types.js";
import { isActionSourceFailed } from "./validation.js";

export interface ActionGoalProjectionEvidence {
	readonly kind: string;
	readonly referenceId: string;
}

export interface ActionGoalProjectionVerification {
	readonly evidence: ActionGoalProjectionEvidence;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
}

export interface ActionGoalProjectionVerifier {
	verify(request: {
		readonly action: ActionRecord;
		readonly binding: ActionGoalProjectionBinding;
		readonly signal?: AbortSignal;
	}): Promise<ActionGoalProjectionVerification | undefined>;
}

export interface ActionGoalAdvancer {
	advanceFromHost(request: {
		readonly goalId: string;
		readonly partition: ActionGoalProjectionBinding["partition"];
		readonly stepId: string;
		readonly inputId: string;
		readonly expectedRevision: string;
		readonly evidence: ActionGoalProjectionEvidence;
		readonly gameTime: GameTime;
		readonly recordedAt: string;
		readonly signal?: AbortSignal;
	}): Promise<{ readonly status: string }>;
}

export interface ActionGoalProjectorOptions {
	readonly actions: ActionCoordinator;
	readonly verifier: ActionGoalProjectionVerifier;
	readonly goals: ActionGoalAdvancer;
}

export interface ActionGoalProjectionRequest extends StreamKey {
	readonly afterSequence?: string;
	readonly limit: number;
	readonly signal?: AbortSignal;
}

export type ActionGoalProjectionResult =
	| { readonly sequence: string; readonly operationId: string; readonly status: "completed" }
	| {
			readonly sequence: string;
			readonly operationId: string;
			readonly status: "dismissed";
			readonly reason: "source_failed";
	  }
	| {
			readonly sequence: string;
			readonly operationId: string;
			readonly status: "blocked";
			readonly reason: "source_not_succeeded" | "source_rejected" | "goal_not_advanced";
			readonly goalStatus?: string;
	  };

export interface ActionGoalProjectionBatchResult {
	readonly results: readonly ActionGoalProjectionResult[];
	readonly nextAfterSequence?: string;
}

export class ActionGoalProjector {
	private readonly actions: ActionCoordinator;
	private readonly verify: ActionGoalProjectionVerifier["verify"];
	private readonly advanceFromHost: ActionGoalAdvancer["advanceFromHost"];

	constructor(options: ActionGoalProjectorOptions) {
		const actions = options.actions;
		const verifier = options.verifier;
		const goals = options.goals;
		this.actions = actions;
		this.verify = verifier.verify.bind(verifier);
		this.advanceFromHost = goals.advanceFromHost.bind(goals);
	}

	async projectPending(request: ActionGoalProjectionRequest): Promise<ActionGoalProjectionBatchResult> {
		const signal = request.signal;
		const sessionId = request.sessionId;
		const actorId = request.actorId;
		const afterSequence = request.afterSequence;
		const limit = request.limit;
		throwIfAborted(signal);
		const page = await this.actions.listGoalProjections({
			sessionId,
			actorId,
			...(afterSequence === undefined ? {} : { afterSequence }),
			limit,
		});
		throwIfAborted(signal);
		const candidates = page.candidates;
		const nextAfterSequence = page.nextAfterSequence;
		const results: ActionGoalProjectionResult[] = [];
		for (const candidate of candidates) {
			throwIfAborted(signal);
			const sequence = candidate.sequence;
			const action = structuredClone(candidate.action);
			results.push(await this.project(sequence, action, signal));
		}
		return {
			results,
			...(nextAfterSequence === undefined ? {} : { nextAfterSequence }),
		};
	}

	private async project(
		sequence: string,
		action: ActionRecord,
		signal: AbortSignal | undefined,
	): Promise<ActionGoalProjectionResult> {
		const binding = action.goalProjection;
		const operationId = action.operationId;
		const sessionId = action.sessionId;
		const actorId = action.actorId;
		const intentFingerprint = action.intentFingerprint;
		const sourceStatus = action.status;
		const receiptOutcome = action.receipt?.outcome;
		if (binding !== undefined && isActionSourceFailed(action)) {
			await this.actions.settleGoalProjection({
				sessionId,
				actorId,
				operationId,
				intentFingerprint,
				outcome: "source_failed",
			});
			throwIfAborted(signal);
			return { sequence, operationId, status: "dismissed", reason: "source_failed" };
		}
		if (binding === undefined || sourceStatus !== "succeeded" || receiptOutcome !== "succeeded") {
			return { sequence, operationId, status: "blocked", reason: "source_not_succeeded" };
		}
		const verified = await this.verify({
			action: structuredClone(action),
			binding: structuredClone(binding),
			...(signal === undefined ? {} : { signal }),
		});
		throwIfAborted(signal);
		if (verified === undefined) {
			return { sequence, operationId, status: "blocked", reason: "source_rejected" };
		}
		const verifiedEvidence = verified.evidence;
		const verifiedGameTime = verified.gameTime;
		const evidence = {
			kind: verifiedEvidence.kind,
			referenceId: verifiedEvidence.referenceId,
		};
		const gameTime = {
			timelineId: verifiedGameTime.timelineId,
			generation: verifiedGameTime.generation,
			tick: verifiedGameTime.tick,
		};
		const recordedAt = verified.recordedAt;
		assertOpaqueIdentifier(evidence.kind);
		assertOpaqueIdentifier(evidence.referenceId);
		assertGameTime(gameTime);
		assertCanonicalTimestamp(recordedAt);
		if (gameTime.timelineId !== binding.partition.timelineId || gameTime.generation !== binding.partition.generation) {
			throw new TypeError("The verified Goal projection time does not match its durable partition.");
		}
		const advanced = await this.advanceFromHost({
			goalId: binding.goalId,
			partition: structuredClone(binding.partition),
			stepId: binding.stepId,
			inputId: binding.inputId,
			expectedRevision: binding.expectedRevision,
			evidence,
			gameTime,
			recordedAt,
			...(signal === undefined ? {} : { signal }),
		});
		throwIfAborted(signal);
		const goalStatus = advanced.status;
		if (goalStatus !== "advanced" && goalStatus !== "already_advanced") {
			return {
				sequence,
				operationId,
				status: "blocked",
				reason: "goal_not_advanced",
				goalStatus,
			};
		}
		await this.actions.settleGoalProjection({
			sessionId,
			actorId,
			operationId,
			intentFingerprint,
			outcome: "advanced",
		});
		throwIfAborted(signal);
		return { sequence, operationId, status: "completed" };
	}
}

function throwIfAborted(signal: AbortSignal | undefined): void {
	if (signal?.aborted === true) throw signal.reason;
}
