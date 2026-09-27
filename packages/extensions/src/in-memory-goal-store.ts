import {
	GoalAuthorityRetiredError,
	GoalAuthorityRetirementConflictError,
	GoalCapacityError,
	GoalConflictError,
	GoalNotFoundError,
	GoalRevisionConflictError,
	GoalValidationError,
} from "./goal-errors.js";
import type {
	GoalAuthorityRetirementDraft,
	GoalAuthorityRetirementRecord,
	GoalDraft,
	GoalKey,
	GoalLimits,
	GoalListRequest,
	GoalPage,
	GoalPartition,
	GoalRecord,
	GoalReplacement,
	GoalStore,
} from "./goal-types.js";
import {
	captureGoalLimits,
	compareGoalTicks,
	normalizeGoalAuthorityRetirement,
	normalizeGoalDraft,
	normalizeGoalKey,
	normalizeGoalPartition,
	normalizeGoalReplacement,
	normalizeGoalStatusFilter,
	serializeGoalPlan,
	serializeGoalRecord,
	validateGoalAfterKey,
	validateGoalPageLimit,
	validateGoalRevision,
	validateGoalTimestamp,
} from "./goal-validation.js";

export class InMemoryGoalStore implements GoalStore {
	readonly limits: Readonly<GoalLimits>;
	private readonly records = new Map<string, GoalRecord>();
	private readonly retirements = new Map<string, GoalAuthorityRetirementRecord>();
	private nextGoalKey = 1n;

	constructor(limits: GoalLimits) {
		this.limits = captureGoalLimits(limits);
	}

	async create(input: GoalDraft, maxOpenGoals: number, signal?: AbortSignal): Promise<GoalRecord> {
		const draft = normalizeGoalDraft(input, this.limits);
		validateGoalPageLimit(maxOpenGoals, this.limits.maxOpenGoalsPerOwnerGeneration, "invalid_goal_capacity");
		signal?.throwIfAborted();
		if (this.retirements.has(partitionKey(draft.partition))) throw new GoalAuthorityRetiredError();
		const key = mapKey(draft);
		if (this.records.has(key)) throw new GoalConflictError();
		const openGoals = [...this.records.values()].filter(
			(record) => sameOwnerGeneration(record.partition, draft.partition) && isOpen(record),
		).length;
		if (openGoals >= maxOpenGoals) throw new GoalCapacityError();
		const record: GoalRecord = {
			goalId: draft.goalId,
			partition: clone(draft.partition),
			objective: draft.objective,
			plan: clone(draft.plan),
			gameTime: { ...draft.gameTime },
			goalKey: this.nextGoalKey,
			revision: "1",
			status: "active",
			createdGameTime: { ...draft.gameTime },
			createdAt: draft.recordedAt,
			updatedAt: draft.recordedAt,
			lastHostAdvanceFingerprint: null,
		};
		serializeGoalPlan(record.plan, this.limits);
		serializeGoalRecord(record, this.limits);
		this.nextGoalKey += 1n;
		this.records.set(key, record);
		return clone(record);
	}

	async retireAuthority(
		draft: GoalAuthorityRetirementDraft,
		signal?: AbortSignal,
	): Promise<GoalAuthorityRetirementRecord> {
		return this.retireAuthoritySynchronously(draft, signal);
	}

	retireAuthoritySynchronously(
		input: GoalAuthorityRetirementDraft,
		signal?: AbortSignal,
	): GoalAuthorityRetirementRecord {
		const draft = normalizeGoalAuthorityRetirement(input);
		signal?.throwIfAborted();
		const key = partitionKey(draft.partition);
		const existing = this.retirements.get(key);
		if (existing !== undefined) {
			if (!sameRetirement(existing, draft)) throw new GoalAuthorityRetirementConflictError();
			return clone(existing);
		}
		const selected = [...this.records.entries()].filter(
			([, goal]) => samePartition(goal.partition, draft.partition) && isOpen(goal),
		);
		if (selected.length > this.limits.maxOpenGoalsPerOwnerGeneration) throw new GoalCapacityError();
		const updates = selected.map(([storageKey, goal]) => {
			if (compareGoalTicks(draft.gameTime.tick, goal.gameTime.tick) < 0) {
				throw new GoalValidationError("goal_time_regression");
			}
			const updated: GoalRecord = {
				...goal,
				status: "cancelled",
				gameTime: { ...draft.gameTime },
				updatedAt: draft.recordedAt,
				revision: (BigInt(goal.revision) + 1n).toString(),
				lastHostAdvanceFingerprint: null,
			};
			serializeGoalRecord(updated, this.limits);
			return [storageKey, updated] as const;
		});
		const retirement: GoalAuthorityRetirementRecord = {
			...clone(draft),
			cancelledGoalCount: updates.length,
		};
		for (const [storageKey, goal] of updates) this.records.set(storageKey, goal);
		this.retirements.set(key, retirement);
		return clone(retirement);
	}

	async get(key: GoalKey): Promise<GoalRecord | undefined> {
		const normalized = normalizeGoalKey(key);
		const record = this.records.get(mapKey(normalized));
		return record === undefined || !samePartition(record.partition, normalized) ? undefined : clone(record);
	}

	async replace(key: GoalKey, expectedRevision: string, input: GoalReplacement): Promise<GoalRecord> {
		const normalized = normalizeGoalKey(key);
		validateGoalRevision(expectedRevision);
		const replacement = normalizeGoalReplacement(input, normalized, this.limits);
		if (this.retirements.has(partitionKey(normalized))) throw new GoalAuthorityRetiredError();
		const storageKey = mapKey(normalized);
		const current = this.records.get(storageKey);
		if (current === undefined || !samePartition(current.partition, normalized)) throw new GoalNotFoundError();
		if (current.revision !== expectedRevision) throw new GoalRevisionConflictError();
		const updated: GoalRecord = {
			...current,
			plan: clone(replacement.plan),
			status: replacement.status,
			gameTime: { ...replacement.gameTime },
			updatedAt: replacement.recordedAt,
			revision: (BigInt(current.revision) + 1n).toString(),
			lastHostAdvanceFingerprint: replacement.lastHostAdvanceFingerprint,
		};
		serializeGoalPlan(updated.plan, this.limits);
		serializeGoalRecord(updated, this.limits);
		this.records.set(storageKey, updated);
		return clone(updated);
	}

	async list(request: GoalListRequest): Promise<GoalPage> {
		const statusesInput = request.statuses;
		const afterGoalKey = request.afterGoalKey;
		const limit = request.limit;
		const partition = normalizeGoalPartition(request);
		const statuses = normalizeGoalStatusFilter(statusesInput);
		validateGoalAfterKey(afterGoalKey);
		validateGoalPageLimit(limit, this.limits.maxListPageSize, "invalid_goal_page_limit");
		const matches = [...this.records.values()]
			.filter(
				(record) =>
					sameOwnerGeneration(record.partition, partition) &&
					(partition.sessionId === undefined || record.partition.sessionId === partition.sessionId) &&
					(partition.actorId === undefined || record.partition.actorId === partition.actorId) &&
					(statuses === undefined || statuses.includes(record.status)) &&
					(afterGoalKey === undefined || record.goalKey > afterGoalKey),
			)
			.sort((left, right) => (left.goalKey < right.goalKey ? -1 : left.goalKey > right.goalKey ? 1 : 0));
		const selected = matches.slice(0, limit);
		const next = matches.length > selected.length ? selected.at(-1)?.goalKey : undefined;
		return {
			records: selected.map(clone),
			...(next === undefined ? {} : { nextAfterGoalKey: next }),
		};
	}

	async purgeTerminal(
		input: Pick<GoalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const partition = normalizeGoalPartition(input);
		validateGoalTimestamp(beforeRecordedAt);
		validateGoalPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_goal_purge_limit");
		const candidates = [...this.records.entries()]
			.filter(
				([, record]) =>
					sameOwnerGeneration(record.partition, partition) && !isOpen(record) && record.updatedAt < beforeRecordedAt,
			)
			.sort(([, left], [, right]) => (left.goalKey < right.goalKey ? -1 : 1))
			.slice(0, limit);
		for (const [key] of candidates) this.records.delete(key);
		return candidates.map(([, record]) => record.goalKey);
	}
}

function mapKey(key: GoalKey | GoalDraft): string {
	const partition = "partition" in key ? key.partition : key;
	return `${partition.ownerId}\0${partition.timelineId}\0${partition.generation}\0${key.goalId}`;
}

function partitionKey(partition: GoalPartition): string {
	return `${partition.ownerId}\0${partition.timelineId}\0${partition.generation}\0${partition.sessionId ?? ""}\0${partition.actorId ?? ""}`;
}

function sameOwnerGeneration(
	left: Pick<GoalPartition, "ownerId" | "timelineId" | "generation">,
	right: Pick<GoalPartition, "ownerId" | "timelineId" | "generation">,
): boolean {
	return left.ownerId === right.ownerId && left.timelineId === right.timelineId && left.generation === right.generation;
}

function samePartition(left: GoalPartition, right: GoalPartition): boolean {
	return (
		left.ownerId === right.ownerId &&
		left.timelineId === right.timelineId &&
		left.generation === right.generation &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId
	);
}

function isOpen(record: GoalRecord): boolean {
	return record.status === "active" || record.status === "paused";
}

function sameRetirement(left: GoalAuthorityRetirementRecord, right: GoalAuthorityRetirementDraft): boolean {
	return (
		left.retirementId === right.retirementId &&
		left.stateRevision === right.stateRevision &&
		samePartition(left.partition, right.partition) &&
		left.gameTime.timelineId === right.gameTime.timelineId &&
		left.gameTime.generation === right.gameTime.generation &&
		left.gameTime.tick === right.gameTime.tick &&
		left.recordedAt === right.recordedAt
	);
}

function clone<T>(value: T): T {
	return structuredClone(value);
}
