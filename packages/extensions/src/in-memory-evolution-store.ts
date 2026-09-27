import { Mutex } from "async-mutex";
import {
	BehaviorEvolutionValidationError,
	BehaviorProposalCapacityError,
	BehaviorProposalConflictError,
	BehaviorProposalNotFoundError,
	BehaviorProposalRevisionConflictError,
	BehaviorRegistryNotFoundError,
	BehaviorRegistryRevisionConflictError,
	BehaviorVersionCapacityError,
	BehaviorVersionNotFoundError,
} from "./evolution-errors.js";
import type {
	BehaviorActivationResult,
	BehaviorActiveListRequest,
	BehaviorActivePage,
	BehaviorChangeListRequest,
	BehaviorChangePage,
	BehaviorPartition,
	BehaviorProposalDecision,
	BehaviorProposalDraft,
	BehaviorProposalKey,
	BehaviorProposalListRequest,
	BehaviorProposalPage,
	BehaviorProposalRecord,
	BehaviorRegistryChange,
	BehaviorRegistryKey,
	BehaviorRegistryState,
	BehaviorSkillVersion,
	BehaviorStore,
	BehaviorVersionKey,
} from "./evolution-types.js";
import { behaviorScopeKey, compareBehaviorDecimals, normalizeBehaviorStatusFilter } from "./evolution-validation.js";

export class InMemoryBehaviorStore implements BehaviorStore {
	private readonly proposals = new Map<string, BehaviorProposalRecord>();
	private readonly registries = new Map<string, BehaviorRegistryState>();
	private readonly versions = new Map<string, BehaviorSkillVersion>();
	private readonly changes = new Map<bigint, BehaviorRegistryChange>();
	private readonly mutex = new Mutex();
	private nextProposalKey = 1n;
	private nextVersionKey = 1n;
	private nextChangeKey = 1n;

	async createProposal(draft: BehaviorProposalDraft, maxOpenProposals: number): Promise<BehaviorProposalRecord> {
		return await this.mutex.runExclusive(() => {
			const key = proposalMapKey(draft.partition, draft.proposalId);
			if (this.proposals.has(key)) throw new BehaviorProposalConflictError();
			const open = [...this.proposals.values()].filter(
				(record) => samePartition(record.partition, draft.partition) && isOpenProposal(record),
			).length;
			if (open >= maxOpenProposals) throw new BehaviorProposalCapacityError();
			const record: BehaviorProposalRecord = {
				...clone(draft),
				proposalKey: this.nextProposalKey,
				revision: "1",
				status: "pending",
				createdAt: draft.recordedAt,
				updatedAt: draft.recordedAt,
			};
			this.nextProposalKey += 1n;
			this.proposals.set(key, record);
			return clone(record);
		});
	}

	async getProposal(key: BehaviorProposalKey): Promise<BehaviorProposalRecord | undefined> {
		return await this.mutex.runExclusive(() => {
			const record = this.proposals.get(proposalMapKey(key, key.proposalId));
			return record === undefined ? undefined : clone(record);
		});
	}

	async decideProposal(
		key: BehaviorProposalKey,
		expectedRevision: string,
		decision: BehaviorProposalDecision,
	): Promise<BehaviorProposalRecord> {
		return await this.mutex.runExclusive(() => {
			const mapKey = proposalMapKey(key, key.proposalId);
			const current = this.proposals.get(mapKey);
			if (current === undefined) throw new BehaviorProposalNotFoundError();
			if (current.revision !== expectedRevision) throw new BehaviorProposalRevisionConflictError();
			if (current.status !== "pending") throw new BehaviorEvolutionValidationError("behavior_proposal_already_decided");
			assertChronology(current.gameTime.tick, current.updatedAt, decision.gameTime.tick, decision.recordedAt);
			const updated: BehaviorProposalRecord = {
				...current,
				revision: increment(current.revision),
				status: decision.status,
				decisionCode: decision.decisionCode,
				gameTime: { ...decision.gameTime },
				updatedAt: decision.recordedAt,
			};
			this.proposals.set(mapKey, updated);
			return clone(updated);
		});
	}

	async activateProposal(
		key: BehaviorProposalKey,
		expectedRevision: string,
		activation: Pick<BehaviorProposalDecision, "gameTime" | "recordedAt">,
		maxVersions: number,
	): Promise<BehaviorActivationResult> {
		return await this.mutex.runExclusive(() => {
			const proposalKey = proposalMapKey(key, key.proposalId);
			const proposal = this.proposals.get(proposalKey);
			if (proposal === undefined) throw new BehaviorProposalNotFoundError();
			if (proposal.revision !== expectedRevision) throw new BehaviorProposalRevisionConflictError();
			if (proposal.status !== "validated")
				throw new BehaviorEvolutionValidationError("behavior_proposal_not_validated");
			assertChronology(proposal.gameTime.tick, proposal.updatedAt, activation.gameTime.tick, activation.recordedAt);

			const registryKey = registryMapKey(registryKeyFromProposal(proposal));
			const currentRegistry = this.registries.get(registryKey);
			if (currentRegistry?.activeVersion !== proposal.baseVersion) {
				throw new BehaviorRegistryRevisionConflictError();
			}
			if (currentRegistry !== undefined) {
				assertChronology(
					currentRegistry.gameTime.tick,
					currentRegistry.updatedAt,
					activation.gameTime.tick,
					activation.recordedAt,
				);
			}
			const versionCount = [...this.versions.values()].filter(
				(version) => registryMapKey(version) === registryKey,
			).length;
			if (versionCount >= maxVersions) throw new BehaviorVersionCapacityError();

			const versionNumber = currentRegistry === undefined ? "1" : increment(currentRegistry.latestVersion);
			const version: BehaviorSkillVersion = {
				...registryKeyFromProposal(proposal),
				versionKey: this.nextVersionKey,
				version: versionNumber,
				sourceProposalId: proposal.proposalId,
				...(proposal.baseVersion === undefined ? {} : { baseVersion: proposal.baseVersion }),
				description: proposal.description,
				reflection: clone(proposal.reflection),
				steps: clone(proposal.steps),
				contentFingerprint: proposal.contentFingerprint,
				gameTime: { ...activation.gameTime },
				createdAt: activation.recordedAt,
			};
			this.nextVersionKey += 1n;
			this.versions.set(versionMapKey(version, version.version), version);

			const registryRevision = currentRegistry === undefined ? "1" : increment(currentRegistry.revision);
			const registry: BehaviorRegistryState = {
				...registryKeyFromProposal(proposal),
				revision: registryRevision,
				latestVersion: versionNumber,
				activeVersion: versionNumber,
				gameTime: { ...activation.gameTime },
				updatedAt: activation.recordedAt,
			};
			this.registries.set(registryKey, registry);

			const updatedProposal: BehaviorProposalRecord = {
				...proposal,
				revision: increment(proposal.revision),
				status: "activated",
				gameTime: { ...activation.gameTime },
				updatedAt: activation.recordedAt,
			};
			this.proposals.set(proposalKey, updatedProposal);

			const change: BehaviorRegistryChange = {
				...registryKeyFromProposal(proposal),
				changeKey: this.nextChangeKey,
				registryRevision,
				type: "activate",
				...(currentRegistry?.activeVersion === undefined ? {} : { fromVersion: currentRegistry.activeVersion }),
				toVersion: versionNumber,
				sourceProposalId: proposal.proposalId,
				gameTime: { ...activation.gameTime },
				recordedAt: activation.recordedAt,
			};
			this.nextChangeKey += 1n;
			this.changes.set(change.changeKey, change);
			return {
				proposal: clone(updatedProposal),
				version: clone(version),
				registry: clone(registry),
				change: clone(change),
			};
		});
	}

	async getRegistry(key: BehaviorRegistryKey): Promise<BehaviorRegistryState | undefined> {
		return await this.mutex.runExclusive(() => {
			const record = this.registries.get(registryMapKey(key));
			return record === undefined ? undefined : clone(record);
		});
	}

	async getVersion(key: BehaviorVersionKey): Promise<BehaviorSkillVersion | undefined> {
		return await this.mutex.runExclusive(() => {
			const record = this.versions.get(versionMapKey(key, key.version));
			return record === undefined ? undefined : clone(record);
		});
	}

	async transitionRegistry(
		key: BehaviorRegistryKey,
		expectedRevision: string,
		targetVersion: string | undefined,
		transition: Pick<BehaviorProposalDecision, "gameTime" | "recordedAt">,
	): Promise<{ readonly registry: BehaviorRegistryState; readonly change: BehaviorRegistryChange }> {
		return await this.mutex.runExclusive(() => {
			const mapKey = registryMapKey(key);
			const current = this.registries.get(mapKey);
			if (current === undefined) throw new BehaviorRegistryNotFoundError();
			if (current.revision !== expectedRevision) throw new BehaviorRegistryRevisionConflictError();
			if (current.activeVersion === targetVersion) throw new BehaviorEvolutionValidationError("behavior_registry_noop");
			assertChronology(current.gameTime.tick, current.updatedAt, transition.gameTime.tick, transition.recordedAt);
			if (targetVersion !== undefined && !this.versions.has(versionMapKey(key, targetVersion))) {
				throw new BehaviorVersionNotFoundError();
			}
			const revision = increment(current.revision);
			const { activeVersion: _activeVersion, ...registryWithoutActiveVersion } = current;
			const registry: BehaviorRegistryState = {
				...registryWithoutActiveVersion,
				revision,
				...(targetVersion === undefined ? {} : { activeVersion: targetVersion }),
				gameTime: { ...transition.gameTime },
				updatedAt: transition.recordedAt,
			};
			this.registries.set(mapKey, registry);
			const change: BehaviorRegistryChange = {
				...registryKeyOnly(key),
				changeKey: this.nextChangeKey,
				registryRevision: revision,
				type: targetVersion === undefined ? "demote" : "rollback",
				...(current.activeVersion === undefined ? {} : { fromVersion: current.activeVersion }),
				...(targetVersion === undefined ? {} : { toVersion: targetVersion }),
				gameTime: { ...transition.gameTime },
				recordedAt: transition.recordedAt,
			};
			this.nextChangeKey += 1n;
			this.changes.set(change.changeKey, change);
			return { registry: clone(registry), change: clone(change) };
		});
	}

	async listActive(request: BehaviorActiveListRequest): Promise<BehaviorActivePage> {
		return await this.mutex.runExclusive(() => {
			const scopeOrder = new Map(request.scopes.map((scope, index) => [behaviorScopeKey(scope), index]));
			const candidates = [...this.registries.values()]
				.filter(
					(registry) =>
						samePartition(registry, request) &&
						registry.activeVersion !== undefined &&
						scopeOrder.has(behaviorScopeKey(registry.scope)),
				)
				.map((registry) => {
					const version = this.versions.get(versionMapKey(registry, registry.activeVersion as string));
					if (version === undefined) throw new BehaviorVersionNotFoundError();
					return { registry, version };
				});
			const winners = new Map<string, (typeof candidates)[number]>();
			for (const candidate of candidates) {
				const current = winners.get(candidate.registry.skillName);
				if (
					current === undefined ||
					(scopeOrder.get(behaviorScopeKey(candidate.registry.scope)) as number) <
						(scopeOrder.get(behaviorScopeKey(current.registry.scope)) as number)
				) {
					winners.set(candidate.registry.skillName, candidate);
				}
			}
			const matches = [...winners.values()]
				.filter(({ version }) => request.afterVersionKey === undefined || version.versionKey > request.afterVersionKey)
				.sort((left, right) => compareBigInt(left.version.versionKey, right.version.versionKey));
			const selected = matches.slice(0, request.limit);
			const next = matches.length > selected.length ? selected.at(-1)?.version.versionKey : undefined;
			return {
				records: selected.map(clone),
				...(next === undefined ? {} : { nextAfterVersionKey: next }),
			};
		});
	}

	async listProposals(request: BehaviorProposalListRequest): Promise<BehaviorProposalPage> {
		return await this.mutex.runExclusive(() => {
			const statuses = normalizeBehaviorStatusFilter(request.statuses);
			const matches = [...this.proposals.values()]
				.filter(
					(record) =>
						samePartition(record.partition, request) &&
						(statuses === undefined || statuses.includes(record.status)) &&
						(request.afterProposalKey === undefined || record.proposalKey > request.afterProposalKey),
				)
				.sort((left, right) => compareBigInt(left.proposalKey, right.proposalKey));
			const selected = matches.slice(0, request.limit);
			const next = matches.length > selected.length ? selected.at(-1)?.proposalKey : undefined;
			return {
				records: selected.map(clone),
				...(next === undefined ? {} : { nextAfterProposalKey: next }),
			};
		});
	}

	async listChanges(request: BehaviorChangeListRequest): Promise<BehaviorChangePage> {
		return await this.mutex.runExclusive(() => {
			const key = registryMapKey(request);
			const matches = [...this.changes.values()]
				.filter(
					(change) =>
						registryMapKey(change) === key &&
						(request.afterChangeKey === undefined || change.changeKey > request.afterChangeKey),
				)
				.sort((left, right) => compareBigInt(left.changeKey, right.changeKey));
			const selected = matches.slice(0, request.limit);
			const next = matches.length > selected.length ? selected.at(-1)?.changeKey : undefined;
			return {
				records: selected.map(clone),
				...(next === undefined ? {} : { nextAfterChangeKey: next }),
			};
		});
	}

	async purgeProposals(
		partition: BehaviorPartition,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		return await this.mutex.runExclusive(() => {
			const candidates = [...this.proposals.entries()]
				.filter(
					([, proposal]) =>
						samePartition(proposal.partition, partition) &&
						isPurgeableProposal(proposal, this.versions) &&
						proposal.updatedAt < beforeRecordedAt,
				)
				.sort(([, left], [, right]) => compareBigInt(left.proposalKey, right.proposalKey))
				.slice(0, limit);
			for (const [key] of candidates) this.proposals.delete(key);
			return candidates.map(([, proposal]) => proposal.proposalKey);
		});
	}

	async purgeInactiveVersions(
		key: BehaviorRegistryKey,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		return await this.mutex.runExclusive(() => {
			const registry = this.registries.get(registryMapKey(key));
			if (registry === undefined) throw new BehaviorRegistryNotFoundError();
			const candidates = [...this.versions.entries()]
				.filter(
					([, version]) =>
						registryMapKey(version) === registryMapKey(key) &&
						version.version !== registry.activeVersion &&
						version.createdAt < beforeRecordedAt,
				)
				.sort(([, left], [, right]) => compareBigInt(left.versionKey, right.versionKey))
				.slice(0, limit);
			for (const [versionKey] of candidates) this.versions.delete(versionKey);
			return candidates.map(([, version]) => version.versionKey);
		});
	}

	async purgeChanges(key: BehaviorRegistryKey, beforeRecordedAt: string, limit: number): Promise<readonly bigint[]> {
		return await this.mutex.runExclusive(() => {
			const registryKey = registryMapKey(key);
			const candidates = [...this.changes.entries()]
				.filter(([, change]) => registryMapKey(change) === registryKey && change.recordedAt < beforeRecordedAt)
				.sort(([, left], [, right]) => compareBigInt(left.changeKey, right.changeKey))
				.slice(0, limit);
			for (const [changeKey] of candidates) this.changes.delete(changeKey);
			return candidates.map(([, change]) => change.changeKey);
		});
	}
}

function proposalMapKey(partition: BehaviorPartition, proposalId: string): string {
	return JSON.stringify([partition.ownerId, partition.timelineId, partition.generation, proposalId]);
}

function registryMapKey(key: BehaviorRegistryKey): string {
	return JSON.stringify([key.ownerId, key.timelineId, key.generation, behaviorScopeKey(key.scope), key.skillName]);
}

function versionMapKey(key: BehaviorRegistryKey, version: string): string {
	return `${registryMapKey(key)}\0${version}`;
}

function registryKeyFromProposal(proposal: BehaviorProposalRecord): BehaviorRegistryKey {
	return {
		...proposal.partition,
		scope: clone(proposal.scope),
		skillName: proposal.skillName,
	};
}

function registryKeyOnly(key: BehaviorRegistryKey): BehaviorRegistryKey {
	return {
		ownerId: key.ownerId,
		timelineId: key.timelineId,
		generation: key.generation,
		scope: clone(key.scope),
		skillName: key.skillName,
	};
}

function samePartition(left: BehaviorPartition, right: BehaviorPartition): boolean {
	return left.ownerId === right.ownerId && left.timelineId === right.timelineId && left.generation === right.generation;
}

function isOpenProposal(proposal: BehaviorProposalRecord): boolean {
	return proposal.status === "pending" || proposal.status === "validated";
}

function isPurgeableProposal(
	proposal: BehaviorProposalRecord,
	versions: ReadonlyMap<string, BehaviorSkillVersion>,
): boolean {
	if (proposal.status === "rejected") return true;
	if (proposal.status !== "activated") return false;
	return ![...versions.values()].some(
		(version) => samePartition(version, proposal.partition) && version.sourceProposalId === proposal.proposalId,
	);
}

function increment(value: string): string {
	return (BigInt(value) + 1n).toString();
}

function assertChronology(currentTick: string, currentAt: string, nextTick: string, nextAt: string): void {
	if (compareBehaviorDecimals(nextTick, currentTick) < 0 || nextAt < currentAt) {
		throw new BehaviorEvolutionValidationError("behavior_time_regression");
	}
}

function compareBigInt(left: bigint, right: bigint): number {
	return left < right ? -1 : left > right ? 1 : 0;
}

function clone<T>(value: T): T {
	return structuredClone(value);
}
