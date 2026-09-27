import {
	type BehaviorActivationResult,
	type BehaviorActiveListRequest,
	type BehaviorActivePage,
	type BehaviorChangeListRequest,
	type BehaviorChangePage,
	BehaviorEvolutionValidationError,
	type BehaviorLimits,
	type BehaviorPartition,
	BehaviorProposalCapacityError,
	BehaviorProposalConflictError,
	type BehaviorProposalDecision,
	type BehaviorProposalDraft,
	type BehaviorProposalKey,
	type BehaviorProposalListRequest,
	BehaviorProposalNotFoundError,
	type BehaviorProposalPage,
	type BehaviorProposalRecord,
	BehaviorProposalRevisionConflictError,
	type BehaviorRegistryChange,
	type BehaviorRegistryKey,
	BehaviorRegistryNotFoundError,
	BehaviorRegistryRevisionConflictError,
	type BehaviorRegistryScope,
	type BehaviorRegistryState,
	type BehaviorSkillVersion,
	type BehaviorStore,
	BehaviorVersionCapacityError,
	type BehaviorVersionKey,
	BehaviorVersionNotFoundError,
	behaviorContentFingerprint,
	behaviorScopeKey,
	compareBehaviorDecimals,
	normalizeBehaviorPartition,
	normalizeBehaviorScope,
	normalizeBehaviorScopes,
	normalizeBehaviorStatusFilter,
	normalizePersistedSteps,
	normalizeReflection,
	validateBehaviorCursor,
	validateBehaviorLimits,
	validateBehaviorPageLimit,
	validateBehaviorProposalKey,
	validateBehaviorRegistryKey,
	validateBehaviorRevision,
	validateBehaviorStatus,
	validateBehaviorTime,
	validateBehaviorTimestamp,
	validateBehaviorVersion,
	validateDecisionCode,
	validateDescription,
	validateOptionalBehaviorVersion,
	validateSkillName,
} from "@opengameagent/extensions";
import { parseStrictJsonText } from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const EVOLUTION_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "behavior-evolution-v1",
		sql: `
			CREATE TABLE oga_behavior_proposals (
				proposal_key INTEGER PRIMARY KEY AUTOINCREMENT,
				proposal_id TEXT NOT NULL,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				scope_kind TEXT NOT NULL CHECK(scope_kind IN ('actor', 'shared')),
				scope_id TEXT NOT NULL,
				skill_name TEXT NOT NULL,
				description TEXT NOT NULL,
				reflection_json TEXT NOT NULL CHECK(json_valid(reflection_json)),
				steps_json TEXT NOT NULL CHECK(json_valid(steps_json)),
				base_version TEXT,
				content_fingerprint TEXT NOT NULL,
				status TEXT NOT NULL CHECK(status IN ('pending', 'validated', 'rejected', 'activated')),
				decision_code TEXT,
				revision TEXT NOT NULL,
				game_tick TEXT NOT NULL,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				CHECK(
					(status = 'pending' AND decision_code IS NULL) OR
					(status <> 'pending' AND decision_code IS NOT NULL)
				),
				UNIQUE(owner_id, timeline_id, generation, proposal_id)
			) STRICT;

			CREATE INDEX oga_behavior_proposals_open
			ON oga_behavior_proposals(owner_id, timeline_id, generation, status, proposal_key)
			WHERE status IN ('pending', 'validated');

			CREATE INDEX oga_behavior_proposals_retention
			ON oga_behavior_proposals(owner_id, timeline_id, generation, updated_at, proposal_key)
			WHERE status IN ('rejected', 'activated');

			CREATE TABLE oga_behavior_registries (
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				scope_kind TEXT NOT NULL CHECK(scope_kind IN ('actor', 'shared')),
				scope_id TEXT NOT NULL,
				skill_name TEXT NOT NULL,
				revision TEXT NOT NULL,
				latest_version TEXT NOT NULL,
				active_version TEXT,
				game_tick TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				PRIMARY KEY(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name)
			) STRICT;

			CREATE TABLE oga_behavior_versions (
				version_key INTEGER PRIMARY KEY AUTOINCREMENT,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				scope_kind TEXT NOT NULL CHECK(scope_kind IN ('actor', 'shared')),
				scope_id TEXT NOT NULL,
				skill_name TEXT NOT NULL,
				version TEXT NOT NULL,
				source_proposal_id TEXT NOT NULL,
				base_version TEXT,
				description TEXT NOT NULL,
				reflection_json TEXT NOT NULL CHECK(json_valid(reflection_json)),
				steps_json TEXT NOT NULL CHECK(json_valid(steps_json)),
				content_fingerprint TEXT NOT NULL,
				game_tick TEXT NOT NULL,
				created_at TEXT NOT NULL,
				UNIQUE(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name, version),
				UNIQUE(owner_id, timeline_id, generation, source_proposal_id),
				FOREIGN KEY(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name)
					REFERENCES oga_behavior_registries(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name)
			) STRICT;

			CREATE INDEX oga_behavior_versions_retention
			ON oga_behavior_versions(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name, created_at, version_key);

			CREATE TABLE oga_behavior_changes (
				change_key INTEGER PRIMARY KEY AUTOINCREMENT,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				scope_kind TEXT NOT NULL CHECK(scope_kind IN ('actor', 'shared')),
				scope_id TEXT NOT NULL,
				skill_name TEXT NOT NULL,
				registry_revision TEXT NOT NULL,
				change_type TEXT NOT NULL CHECK(change_type IN ('activate', 'rollback', 'demote')),
				from_version TEXT,
				to_version TEXT,
				source_proposal_id TEXT,
				game_tick TEXT NOT NULL,
				recorded_at TEXT NOT NULL,
				CHECK(
					(change_type = 'activate' AND to_version IS NOT NULL AND source_proposal_id IS NOT NULL) OR
					(change_type = 'rollback' AND to_version IS NOT NULL AND source_proposal_id IS NULL) OR
					(change_type = 'demote' AND to_version IS NULL AND source_proposal_id IS NULL)
				),
				FOREIGN KEY(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name)
					REFERENCES oga_behavior_registries(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name)
			) STRICT;

			CREATE INDEX oga_behavior_changes_registry
			ON oga_behavior_changes(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name, change_key);

			CREATE INDEX oga_behavior_changes_retention
			ON oga_behavior_changes(owner_id, timeline_id, generation, scope_kind, scope_id, skill_name, recorded_at, change_key);
		`,
	},
];

interface ProposalRow {
	proposal_key: bigint;
	proposal_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	scope_kind: string;
	scope_id: string;
	skill_name: string;
	description: string;
	reflection_json: string;
	steps_json: string;
	base_version: string | null;
	content_fingerprint: string;
	status: string;
	decision_code: string | null;
	revision: string;
	game_tick: string;
	created_at: string;
	updated_at: string;
}

interface RegistryRow {
	owner_id: string;
	timeline_id: string;
	generation: string;
	scope_kind: string;
	scope_id: string;
	skill_name: string;
	revision: string;
	latest_version: string;
	active_version: string | null;
	game_tick: string;
	updated_at: string;
}

interface VersionRow {
	version_key: bigint;
	owner_id: string;
	timeline_id: string;
	generation: string;
	scope_kind: string;
	scope_id: string;
	skill_name: string;
	version: string;
	source_proposal_id: string;
	base_version: string | null;
	description: string;
	reflection_json: string;
	steps_json: string;
	content_fingerprint: string;
	game_tick: string;
	created_at: string;
}

interface ChangeRow {
	change_key: bigint;
	owner_id: string;
	timeline_id: string;
	generation: string;
	scope_kind: string;
	scope_id: string;
	skill_name: string;
	registry_revision: string;
	change_type: string;
	from_version: string | null;
	to_version: string | null;
	source_proposal_id: string | null;
	game_tick: string;
	recorded_at: string;
}

interface ActiveKeyRow {
	owner_id: string;
	timeline_id: string;
	generation: string;
	scope_kind: string;
	scope_id: string;
	skill_name: string;
	version_key: bigint;
}

export class SqliteBehaviorStore implements BehaviorStore {
	readonly limits: BehaviorLimits;

	constructor(
		private readonly authority: SqliteAuthority,
		limits: BehaviorLimits,
	) {
		validateBehaviorLimits(limits);
		this.limits = Object.freeze({ ...limits });
		this.authority.install("behavior-evolution", EVOLUTION_MIGRATIONS);
	}

	async createProposal(input: BehaviorProposalDraft, maxOpenProposals: number): Promise<BehaviorProposalRecord> {
		const draft = this.normalizeDraft(input);
		validateBehaviorPageLimit(
			maxOpenProposals,
			this.limits.maxOpenProposalsPerOwnerGeneration,
			"invalid_behavior_capacity",
		);
		return this.authority.write((database) => {
			if (findProposalRow(database, draft.partition, draft.proposalId) !== undefined) {
				throw new BehaviorProposalConflictError();
			}
			const openCount = database
				.prepare(`
					SELECT COUNT(*) FROM oga_behavior_proposals
					WHERE owner_id = ? AND timeline_id = ? AND generation = ?
					  AND status IN ('pending', 'validated')
				`)
				.pluck()
				.get(draft.partition.ownerId, draft.partition.timelineId, draft.partition.generation) as number;
			if (openCount >= maxOpenProposals) throw new BehaviorProposalCapacityError();
			const scope = encodeScope(draft.scope);
			let result: Database.RunResult;
			try {
				result = database
					.prepare(`
						INSERT INTO oga_behavior_proposals(
							proposal_id, owner_id, timeline_id, generation, scope_kind, scope_id,
							skill_name, description, reflection_json, steps_json, base_version,
							content_fingerprint, status, decision_code, revision, game_tick, created_at, updated_at
						) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'pending', NULL, '1', ?, ?, ?)
					`)
					.run(
						draft.proposalId,
						draft.partition.ownerId,
						draft.partition.timelineId,
						draft.partition.generation,
						scope.kind,
						scope.id,
						draft.skillName,
						draft.description,
						JSON.stringify(draft.reflection),
						JSON.stringify(draft.steps),
						draft.baseVersion ?? null,
						draft.contentFingerprint,
						draft.gameTime.tick,
						draft.recordedAt,
						draft.recordedAt,
					);
			} catch (error) {
				if (isConstraintError(error)) throw new BehaviorProposalConflictError();
				throw error;
			}
			const row = findProposalRowByKey(database, BigInt(result.lastInsertRowid));
			if (row === undefined) throw new Error("Behavior proposal insert did not produce a readable row.");
			return this.hydrateProposal(database, row, true);
		});
	}

	async getProposal(key: BehaviorProposalKey): Promise<BehaviorProposalRecord | undefined> {
		validateBehaviorProposalKey(key);
		return this.authority.snapshot((database) => {
			const row = findProposalRow(database, key, key.proposalId);
			return row === undefined ? undefined : this.hydrateProposal(database, row, true);
		});
	}

	async decideProposal(
		key: BehaviorProposalKey,
		expectedRevision: string,
		input: BehaviorProposalDecision,
	): Promise<BehaviorProposalRecord> {
		validateBehaviorProposalKey(key);
		validateBehaviorRevision(expectedRevision);
		return this.authority.write((database) => {
			const currentRow = findProposalRow(database, key, key.proposalId);
			if (currentRow === undefined) throw new BehaviorProposalNotFoundError();
			const current = this.hydrateProposal(database, currentRow, true);
			if (current.revision !== expectedRevision) throw new BehaviorProposalRevisionConflictError();
			if (current.status !== "pending") {
				throw new BehaviorEvolutionValidationError("behavior_proposal_already_decided");
			}
			const decision = this.normalizeDecision(input, current.partition);
			assertChronology(current.gameTime.tick, current.updatedAt, decision.gameTime.tick, decision.recordedAt);
			const revision = increment(current.revision);
			const result = database
				.prepare(`
					UPDATE oga_behavior_proposals
					SET status = ?, decision_code = ?, revision = ?, game_tick = ?, updated_at = ?
					WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND proposal_id = ?
					  AND status = 'pending' AND revision = ?
				`)
				.run(
					decision.status,
					decision.decisionCode,
					revision,
					decision.gameTime.tick,
					decision.recordedAt,
					key.ownerId,
					key.timelineId,
					key.generation,
					key.proposalId,
					expectedRevision,
				);
			if (result.changes !== 1) throw new BehaviorProposalRevisionConflictError();
			const updated = findProposalRow(database, key, key.proposalId);
			if (updated === undefined) throw new BehaviorProposalNotFoundError();
			return this.hydrateProposal(database, updated, true);
		});
	}

	async activateProposal(
		key: BehaviorProposalKey,
		expectedRevision: string,
		input: Pick<BehaviorProposalDecision, "gameTime" | "recordedAt">,
		maxVersions: number,
	): Promise<BehaviorActivationResult> {
		validateBehaviorProposalKey(key);
		validateBehaviorRevision(expectedRevision);
		validateBehaviorPageLimit(maxVersions, this.limits.maxVersionsPerSkill, "invalid_behavior_version_capacity");
		return this.authority.write((database) => {
			const proposalRow = findProposalRow(database, key, key.proposalId);
			if (proposalRow === undefined) throw new BehaviorProposalNotFoundError();
			const proposal = this.hydrateProposal(database, proposalRow, true);
			if (proposal.revision !== expectedRevision) throw new BehaviorProposalRevisionConflictError();
			if (proposal.status !== "validated") {
				throw new BehaviorEvolutionValidationError("behavior_proposal_not_validated");
			}
			const gameTime = validateBehaviorTime(input.gameTime, proposal.partition);
			validateBehaviorTimestamp(input.recordedAt);
			assertChronology(proposal.gameTime.tick, proposal.updatedAt, gameTime.tick, input.recordedAt);
			const registryKey = registryKeyFromProposal(proposal);
			const registryRow = findRegistryRow(database, registryKey);
			const currentRegistry = registryRow === undefined ? undefined : this.hydrateRegistry(database, registryRow);
			if (currentRegistry?.activeVersion !== proposal.baseVersion) {
				throw new BehaviorRegistryRevisionConflictError();
			}
			if (currentRegistry !== undefined) {
				assertChronology(currentRegistry.gameTime.tick, currentRegistry.updatedAt, gameTime.tick, input.recordedAt);
			}
			const encodedScope = encodeScope(registryKey.scope);
			const versionCount = database
				.prepare(`
					SELECT COUNT(*) FROM oga_behavior_versions
					WHERE owner_id = ? AND timeline_id = ? AND generation = ?
					  AND scope_kind = ? AND scope_id = ? AND skill_name = ?
				`)
				.pluck()
				.get(
					registryKey.ownerId,
					registryKey.timelineId,
					registryKey.generation,
					encodedScope.kind,
					encodedScope.id,
					registryKey.skillName,
				) as number;
			if (versionCount >= maxVersions) throw new BehaviorVersionCapacityError();

			const version = currentRegistry === undefined ? "1" : increment(currentRegistry.latestVersion);
			const registryRevision = currentRegistry === undefined ? "1" : increment(currentRegistry.revision);
			if (currentRegistry === undefined) {
				database
					.prepare(`
						INSERT INTO oga_behavior_registries(
							owner_id, timeline_id, generation, scope_kind, scope_id, skill_name,
							revision, latest_version, active_version, game_tick, updated_at
						) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
					`)
					.run(
						registryKey.ownerId,
						registryKey.timelineId,
						registryKey.generation,
						encodedScope.kind,
						encodedScope.id,
						registryKey.skillName,
						registryRevision,
						version,
						version,
						gameTime.tick,
						input.recordedAt,
					);
			} else {
				const updated = database
					.prepare(`
						UPDATE oga_behavior_registries
						SET revision = ?, latest_version = ?, active_version = ?, game_tick = ?, updated_at = ?
						WHERE owner_id = ? AND timeline_id = ? AND generation = ?
						  AND scope_kind = ? AND scope_id = ? AND skill_name = ? AND revision = ?
					`)
					.run(
						registryRevision,
						version,
						version,
						gameTime.tick,
						input.recordedAt,
						registryKey.ownerId,
						registryKey.timelineId,
						registryKey.generation,
						encodedScope.kind,
						encodedScope.id,
						registryKey.skillName,
						currentRegistry.revision,
					);
				if (updated.changes !== 1) throw new BehaviorRegistryRevisionConflictError();
			}

			const versionResult = database
				.prepare(`
					INSERT INTO oga_behavior_versions(
						owner_id, timeline_id, generation, scope_kind, scope_id, skill_name,
						version, source_proposal_id, base_version, description, reflection_json,
						steps_json, content_fingerprint, game_tick, created_at
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
				`)
				.run(
					registryKey.ownerId,
					registryKey.timelineId,
					registryKey.generation,
					encodedScope.kind,
					encodedScope.id,
					registryKey.skillName,
					version,
					proposal.proposalId,
					proposal.baseVersion ?? null,
					proposal.description,
					JSON.stringify(proposal.reflection),
					JSON.stringify(proposal.steps),
					proposal.contentFingerprint,
					gameTime.tick,
					input.recordedAt,
				);

			const proposalRevision = increment(proposal.revision);
			const proposalUpdated = database
				.prepare(`
					UPDATE oga_behavior_proposals
					SET status = 'activated', revision = ?, game_tick = ?, updated_at = ?
					WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND proposal_id = ?
					  AND status = 'validated' AND revision = ?
				`)
				.run(
					proposalRevision,
					gameTime.tick,
					input.recordedAt,
					key.ownerId,
					key.timelineId,
					key.generation,
					key.proposalId,
					expectedRevision,
				);
			if (proposalUpdated.changes !== 1) throw new BehaviorProposalRevisionConflictError();

			const changeResult = database
				.prepare(`
					INSERT INTO oga_behavior_changes(
						owner_id, timeline_id, generation, scope_kind, scope_id, skill_name,
						registry_revision, change_type, from_version, to_version, source_proposal_id,
						game_tick, recorded_at
					) VALUES (?, ?, ?, ?, ?, ?, ?, 'activate', ?, ?, ?, ?, ?)
				`)
				.run(
					registryKey.ownerId,
					registryKey.timelineId,
					registryKey.generation,
					encodedScope.kind,
					encodedScope.id,
					registryKey.skillName,
					registryRevision,
					currentRegistry?.activeVersion ?? null,
					version,
					proposal.proposalId,
					gameTime.tick,
					input.recordedAt,
				);

			const finalProposalRow = findProposalRow(database, key, key.proposalId);
			const finalRegistryRow = findRegistryRow(database, registryKey);
			const finalVersionRow = findVersionRowByKey(database, BigInt(versionResult.lastInsertRowid));
			const finalChangeRow = findChangeRowByKey(database, BigInt(changeResult.lastInsertRowid));
			if (!finalProposalRow || !finalRegistryRow || !finalVersionRow || !finalChangeRow) {
				throw new Error("Behavior activation did not produce readable state.");
			}
			return {
				proposal: this.hydrateProposal(database, finalProposalRow, true),
				registry: this.hydrateRegistry(database, finalRegistryRow),
				version: this.hydrateVersion(database, finalVersionRow),
				change: this.hydrateChange(database, finalChangeRow),
			};
		});
	}

	async getRegistry(key: BehaviorRegistryKey): Promise<BehaviorRegistryState | undefined> {
		validateBehaviorRegistryKey(key, this.limits);
		return this.authority.snapshot((database) => {
			const row = findRegistryRow(database, key);
			return row === undefined ? undefined : this.hydrateRegistry(database, row);
		});
	}

	async getVersion(key: BehaviorVersionKey): Promise<BehaviorSkillVersion | undefined> {
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorVersion(key.version);
		return this.authority.snapshot((database) => {
			const row = findVersionRow(database, key, key.version);
			return row === undefined ? undefined : this.hydrateVersion(database, row);
		});
	}

	async transitionRegistry(
		key: BehaviorRegistryKey,
		expectedRevision: string,
		targetVersion: string | undefined,
		input: Pick<BehaviorProposalDecision, "gameTime" | "recordedAt">,
	): Promise<{ readonly registry: BehaviorRegistryState; readonly change: BehaviorRegistryChange }> {
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorRevision(expectedRevision);
		if (targetVersion !== undefined) validateBehaviorVersion(targetVersion);
		const gameTime = validateBehaviorTime(input.gameTime, key);
		validateBehaviorTimestamp(input.recordedAt);
		return this.authority.write((database) => {
			const currentRow = findRegistryRow(database, key);
			if (currentRow === undefined) throw new BehaviorRegistryNotFoundError();
			const current = this.hydrateRegistry(database, currentRow);
			if (current.revision !== expectedRevision) throw new BehaviorRegistryRevisionConflictError();
			if (current.activeVersion === targetVersion) {
				throw new BehaviorEvolutionValidationError("behavior_registry_noop");
			}
			assertChronology(current.gameTime.tick, current.updatedAt, gameTime.tick, input.recordedAt);
			if (targetVersion !== undefined) {
				const version = findVersionRow(database, key, targetVersion);
				if (version === undefined) throw new BehaviorVersionNotFoundError();
				this.hydrateVersion(database, version);
			}
			const encodedScope = encodeScope(key.scope);
			const revision = increment(current.revision);
			const update = database
				.prepare(`
					UPDATE oga_behavior_registries
					SET revision = ?, active_version = ?, game_tick = ?, updated_at = ?
					WHERE owner_id = ? AND timeline_id = ? AND generation = ?
					  AND scope_kind = ? AND scope_id = ? AND skill_name = ? AND revision = ?
				`)
				.run(
					revision,
					targetVersion ?? null,
					gameTime.tick,
					input.recordedAt,
					key.ownerId,
					key.timelineId,
					key.generation,
					encodedScope.kind,
					encodedScope.id,
					key.skillName,
					expectedRevision,
				);
			if (update.changes !== 1) throw new BehaviorRegistryRevisionConflictError();
			const changeResult = database
				.prepare(`
					INSERT INTO oga_behavior_changes(
						owner_id, timeline_id, generation, scope_kind, scope_id, skill_name,
						registry_revision, change_type, from_version, to_version, source_proposal_id,
						game_tick, recorded_at
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, ?)
				`)
				.run(
					key.ownerId,
					key.timelineId,
					key.generation,
					encodedScope.kind,
					encodedScope.id,
					key.skillName,
					revision,
					targetVersion === undefined ? "demote" : "rollback",
					current.activeVersion ?? null,
					targetVersion ?? null,
					gameTime.tick,
					input.recordedAt,
				);
			const registryRow = findRegistryRow(database, key);
			const changeRow = findChangeRowByKey(database, BigInt(changeResult.lastInsertRowid));
			if (!registryRow || !changeRow) throw new Error("Behavior transition did not produce readable state.");
			return {
				registry: this.hydrateRegistry(database, registryRow),
				change: this.hydrateChange(database, changeRow),
			};
		});
	}

	async listActive(request: BehaviorActiveListRequest): Promise<BehaviorActivePage> {
		const partition = normalizeBehaviorPartition(request);
		const scopes = normalizeBehaviorScopes(request.scopes, this.limits.maxScopesPerView);
		validateBehaviorCursor(request.afterVersionKey);
		validateBehaviorPageLimit(request.limit, this.limits.maxListPageSize, "invalid_behavior_page_limit");
		return this.authority.snapshot((database) => {
			const scopeValues = scopes.map(() => "(?, ?, ?)").join(", ");
			const scopeArguments = scopes.flatMap((scope, index) => {
				const encoded = encodeScope(scope);
				return [encoded.kind, encoded.id, index];
			});
			const statement = database.prepare(`
				WITH requested_scopes(scope_kind, scope_id, scope_rank) AS (VALUES ${scopeValues}),
				candidates AS (
					SELECT r.owner_id, r.timeline_id, r.generation, r.scope_kind, r.scope_id,
					       r.skill_name, v.version_key,
					       ROW_NUMBER() OVER (PARTITION BY r.skill_name ORDER BY s.scope_rank) AS scope_choice
					FROM oga_behavior_registries r
					JOIN requested_scopes s ON s.scope_kind = r.scope_kind AND s.scope_id = r.scope_id
					JOIN oga_behavior_versions v
					  ON v.owner_id = r.owner_id AND v.timeline_id = r.timeline_id AND v.generation = r.generation
					 AND v.scope_kind = r.scope_kind AND v.scope_id = r.scope_id AND v.skill_name = r.skill_name
					 AND v.version = r.active_version
					WHERE r.owner_id = ? AND r.timeline_id = ? AND r.generation = ? AND r.active_version IS NOT NULL
				)
				SELECT owner_id, timeline_id, generation, scope_kind, scope_id, skill_name, version_key
				FROM candidates
				WHERE scope_choice = 1 AND version_key > ?
				ORDER BY version_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				...scopeArguments,
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				request.afterVersionKey ?? 0n,
				BigInt(request.limit + 1),
			) as ActiveKeyRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => {
				const key = registryKeyFromRow(row);
				const registryRow = findRegistryRow(database, key);
				const versionRow = findVersionRowByKey(database, row.version_key);
				if (!registryRow || !versionRow) throw new BehaviorVersionNotFoundError();
				return {
					registry: this.hydrateRegistry(database, registryRow),
					version: this.hydrateVersion(database, versionRow),
				};
			});
			const next = hasMore ? records.at(-1)?.version.versionKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterVersionKey: next }) };
		});
	}

	async listProposals(request: BehaviorProposalListRequest): Promise<BehaviorProposalPage> {
		const partition = normalizeBehaviorPartition(request);
		const statuses = normalizeBehaviorStatusFilter(request.statuses);
		validateBehaviorCursor(request.afterProposalKey);
		validateBehaviorPageLimit(request.limit, this.limits.maxListPageSize, "invalid_behavior_page_limit");
		return this.authority.snapshot((database) => {
			const selectedStatuses = statuses ?? [];
			const statusSql =
				selectedStatuses.length === 0 ? "" : `AND status IN (${selectedStatuses.map(() => "?").join(", ")})`;
			const statement = database.prepare(`
				SELECT * FROM oga_behavior_proposals
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND proposal_key > ? ${statusSql}
				ORDER BY proposal_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				request.afterProposalKey ?? 0n,
				...selectedStatuses,
				BigInt(request.limit + 1),
			) as ProposalRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => this.hydrateProposal(database, row, true));
			const next = hasMore ? records.at(-1)?.proposalKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterProposalKey: next }) };
		});
	}

	async listChanges(request: BehaviorChangeListRequest): Promise<BehaviorChangePage> {
		validateBehaviorRegistryKey(request, this.limits);
		validateBehaviorCursor(request.afterChangeKey);
		validateBehaviorPageLimit(request.limit, this.limits.maxListPageSize, "invalid_behavior_page_limit");
		return this.authority.snapshot((database) => {
			const scope = encodeScope(request.scope);
			const statement = database.prepare(`
				SELECT * FROM oga_behavior_changes
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND scope_kind = ? AND scope_id = ? AND skill_name = ? AND change_key > ?
				ORDER BY change_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				request.ownerId,
				request.timelineId,
				request.generation,
				scope.kind,
				scope.id,
				request.skillName,
				request.afterChangeKey ?? 0n,
				BigInt(request.limit + 1),
			) as ChangeRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => this.hydrateChange(database, row));
			const next = hasMore ? records.at(-1)?.changeKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterChangeKey: next }) };
		});
	}

	async purgeProposals(input: BehaviorPartition, beforeRecordedAt: string, limit: number): Promise<readonly bigint[]> {
		const partition = normalizeBehaviorPartition(input);
		validateBehaviorTimestamp(beforeRecordedAt);
		validateBehaviorPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_behavior_purge_limit");
		return this.authority.write((database) => {
			const statement = database.prepare(`
				SELECT p.proposal_key
				FROM oga_behavior_proposals p
				WHERE p.owner_id = ? AND p.timeline_id = ? AND p.generation = ? AND p.updated_at < ?
				  AND (
					p.status = 'rejected' OR
					(p.status = 'activated' AND NOT EXISTS (
						SELECT 1 FROM oga_behavior_versions v
						WHERE v.owner_id = p.owner_id AND v.timeline_id = p.timeline_id
						  AND v.generation = p.generation AND v.source_proposal_id = p.proposal_id
					))
				  )
				ORDER BY p.proposal_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				beforeRecordedAt,
				BigInt(limit),
			) as { proposal_key: bigint }[];
			const remove = database.prepare("DELETE FROM oga_behavior_proposals WHERE proposal_key = ?");
			for (const row of rows) remove.run(row.proposal_key);
			return rows.map((row) => row.proposal_key);
		});
	}

	async purgeInactiveVersions(
		key: BehaviorRegistryKey,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorTimestamp(beforeRecordedAt);
		validateBehaviorPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_behavior_purge_limit");
		return this.authority.write((database) => {
			const registryRow = findRegistryRow(database, key);
			if (registryRow === undefined) throw new BehaviorRegistryNotFoundError();
			const registry = this.hydrateRegistry(database, registryRow);
			const scope = encodeScope(key.scope);
			const statement = database.prepare(`
				SELECT version_key FROM oga_behavior_versions
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND scope_kind = ? AND scope_id = ? AND skill_name = ?
				  AND (? IS NULL OR version <> ?) AND created_at < ?
				ORDER BY version_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				key.ownerId,
				key.timelineId,
				key.generation,
				scope.kind,
				scope.id,
				key.skillName,
				registry.activeVersion ?? null,
				registry.activeVersion ?? null,
				beforeRecordedAt,
				BigInt(limit),
			) as { version_key: bigint }[];
			const remove = database.prepare("DELETE FROM oga_behavior_versions WHERE version_key = ?");
			for (const row of rows) remove.run(row.version_key);
			return rows.map((row) => row.version_key);
		});
	}

	async purgeChanges(key: BehaviorRegistryKey, beforeRecordedAt: string, limit: number): Promise<readonly bigint[]> {
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorTimestamp(beforeRecordedAt);
		validateBehaviorPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_behavior_purge_limit");
		return this.authority.write((database) => {
			const scope = encodeScope(key.scope);
			const statement = database.prepare(`
				SELECT change_key FROM oga_behavior_changes
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND scope_kind = ? AND scope_id = ? AND skill_name = ? AND recorded_at < ?
				ORDER BY change_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				key.ownerId,
				key.timelineId,
				key.generation,
				scope.kind,
				scope.id,
				key.skillName,
				beforeRecordedAt,
				BigInt(limit),
			) as { change_key: bigint }[];
			const remove = database.prepare("DELETE FROM oga_behavior_changes WHERE change_key = ?");
			for (const row of rows) remove.run(row.change_key);
			return rows.map((row) => row.change_key);
		});
	}

	private normalizeDraft(input: BehaviorProposalDraft): BehaviorProposalDraft {
		const partition = normalizeBehaviorPartition(input.partition);
		validateBehaviorProposalKey({ ...partition, proposalId: input.proposalId });
		const scope = normalizeBehaviorScope(input.scope);
		validateSkillName(input.skillName, this.limits);
		validateDescription(input.description, this.limits);
		const reflection = normalizeReflection(input.reflection, this.limits);
		const steps = normalizePersistedSteps(input.steps, this.limits);
		validateOptionalBehaviorVersion(input.baseVersion);
		const gameTime = validateBehaviorTime(input.gameTime, partition);
		validateBehaviorTimestamp(input.recordedAt);
		const fingerprint = behaviorContentFingerprint({
			partition,
			scope,
			skillName: input.skillName,
			description: input.description,
			reflection,
			steps,
			...(input.baseVersion === undefined ? {} : { baseVersion: input.baseVersion }),
		});
		if (fingerprint !== input.contentFingerprint) {
			throw new BehaviorEvolutionValidationError("behavior_content_fingerprint_mismatch");
		}
		return {
			proposalId: input.proposalId,
			partition,
			scope,
			skillName: input.skillName,
			description: input.description,
			reflection,
			steps,
			...(input.baseVersion === undefined ? {} : { baseVersion: input.baseVersion }),
			contentFingerprint: fingerprint,
			gameTime,
			recordedAt: input.recordedAt,
		};
	}

	private normalizeDecision(input: BehaviorProposalDecision, partition: BehaviorPartition): BehaviorProposalDecision {
		if (input.status !== "validated" && input.status !== "rejected") {
			throw new BehaviorEvolutionValidationError("invalid_behavior_decision_status");
		}
		validateDecisionCode(input.decisionCode, this.limits);
		const gameTime = validateBehaviorTime(input.gameTime, partition);
		validateBehaviorTimestamp(input.recordedAt);
		return { status: input.status, decisionCode: input.decisionCode, gameTime, recordedAt: input.recordedAt };
	}

	private hydrateProposal(
		database: Database.Database,
		row: ProposalRow,
		verifyActivation: boolean,
	): BehaviorProposalRecord {
		const partition = normalizeBehaviorPartition({
			ownerId: row.owner_id,
			timelineId: row.timeline_id,
			generation: row.generation,
		});
		validateBehaviorProposalKey({ ...partition, proposalId: row.proposal_id });
		const scope = decodeScope(row.scope_kind, row.scope_id);
		validateSkillName(row.skill_name, this.limits);
		validateDescription(row.description, this.limits);
		const reflection = normalizeReflection(this.parse(row.reflection_json), this.limits);
		const steps = normalizePersistedSteps(this.parse(row.steps_json), this.limits);
		const baseVersion = row.base_version ?? undefined;
		validateOptionalBehaviorVersion(baseVersion);
		const fingerprint = behaviorContentFingerprint({
			partition,
			scope,
			skillName: row.skill_name,
			description: row.description,
			reflection,
			steps,
			...(baseVersion === undefined ? {} : { baseVersion }),
		});
		if (fingerprint !== row.content_fingerprint) {
			throw new BehaviorEvolutionValidationError("behavior_content_fingerprint_mismatch");
		}
		validateBehaviorStatus(row.status);
		validateBehaviorRevision(row.revision);
		validateBehaviorTimestamp(row.created_at);
		validateBehaviorTimestamp(row.updated_at);
		if (row.updated_at < row.created_at) throw new BehaviorEvolutionValidationError("behavior_time_regression");
		const expectedRevision = row.status === "pending" ? "1" : row.status === "activated" ? "3" : "2";
		if (row.revision !== expectedRevision)
			throw new BehaviorEvolutionValidationError("invalid_behavior_proposal_revision");
		if (row.status === "pending") {
			if (row.decision_code !== null || row.updated_at !== row.created_at) {
				throw new BehaviorEvolutionValidationError("invalid_behavior_proposal_decision");
			}
		} else {
			if (row.decision_code === null) throw new BehaviorEvolutionValidationError("invalid_behavior_proposal_decision");
			validateDecisionCode(row.decision_code, this.limits);
		}
		const gameTime = validateBehaviorTime(
			{ timelineId: partition.timelineId, generation: partition.generation, tick: row.game_tick },
			partition,
		);
		const record: BehaviorProposalRecord = {
			proposalKey: row.proposal_key,
			proposalId: row.proposal_id,
			partition,
			scope,
			skillName: row.skill_name,
			description: row.description,
			reflection,
			steps,
			...(baseVersion === undefined ? {} : { baseVersion }),
			contentFingerprint: fingerprint,
			gameTime,
			recordedAt: row.created_at,
			revision: row.revision,
			status: row.status,
			...(row.decision_code === null ? {} : { decisionCode: row.decision_code }),
			createdAt: row.created_at,
			updatedAt: row.updated_at,
		};
		if (verifyActivation && record.status === "activated") {
			const linked = findVersionRowByProposal(database, record.partition, record.proposalId);
			if (linked === undefined) {
				throw new BehaviorEvolutionValidationError("invalid_behavior_activation_link");
			}
			const version = this.hydrateVersion(database, linked);
			if (
				version.sourceProposalId !== record.proposalId ||
				behaviorScopeKey(version.scope) !== behaviorScopeKey(record.scope) ||
				version.skillName !== record.skillName ||
				version.contentFingerprint !== record.contentFingerprint ||
				version.baseVersion !== record.baseVersion ||
				version.gameTime.tick !== record.gameTime.tick ||
				version.createdAt !== record.updatedAt
			) {
				throw new BehaviorEvolutionValidationError("invalid_behavior_activation_link");
			}
		}
		return record;
	}

	private hydrateRegistry(database: Database.Database, row: RegistryRow): BehaviorRegistryState {
		const key = registryKeyFromRow(row);
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorRevision(row.revision);
		validateBehaviorVersion(row.latest_version);
		const activeVersion = row.active_version ?? undefined;
		validateOptionalBehaviorVersion(activeVersion);
		if (activeVersion !== undefined && compareBehaviorDecimals(activeVersion, row.latest_version) > 0) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_registry_version");
		}
		if (compareBehaviorDecimals(row.latest_version, row.revision) > 0) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_registry_revision");
		}
		const gameTime = validateBehaviorTime(
			{ timelineId: key.timelineId, generation: key.generation, tick: row.game_tick },
			key,
		);
		validateBehaviorTimestamp(row.updated_at);
		if (activeVersion !== undefined) {
			const active = findVersionRow(database, key, activeVersion);
			if (active === undefined) throw new BehaviorVersionNotFoundError();
			const version = this.hydrateVersion(database, active);
			if (compareBehaviorDecimals(version.gameTime.tick, gameTime.tick) > 0 || version.createdAt > row.updated_at) {
				throw new BehaviorEvolutionValidationError("invalid_behavior_registry_time");
			}
		}
		return {
			...key,
			revision: row.revision,
			latestVersion: row.latest_version,
			...(activeVersion === undefined ? {} : { activeVersion }),
			gameTime,
			updatedAt: row.updated_at,
		};
	}

	private hydrateVersion(database: Database.Database, row: VersionRow): BehaviorSkillVersion {
		const key = registryKeyFromRow(row);
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorVersion(row.version);
		validateBehaviorProposalKey({ ...key, proposalId: row.source_proposal_id });
		const baseVersion = row.base_version ?? undefined;
		validateOptionalBehaviorVersion(baseVersion);
		validateDescription(row.description, this.limits);
		const reflection = normalizeReflection(this.parse(row.reflection_json), this.limits);
		const steps = normalizePersistedSteps(this.parse(row.steps_json), this.limits);
		const fingerprint = behaviorContentFingerprint({
			partition: { ownerId: key.ownerId, timelineId: key.timelineId, generation: key.generation },
			scope: key.scope,
			skillName: key.skillName,
			description: row.description,
			reflection,
			steps,
			...(baseVersion === undefined ? {} : { baseVersion }),
		});
		if (fingerprint !== row.content_fingerprint) {
			throw new BehaviorEvolutionValidationError("behavior_content_fingerprint_mismatch");
		}
		const gameTime = validateBehaviorTime(
			{ timelineId: key.timelineId, generation: key.generation, tick: row.game_tick },
			key,
		);
		validateBehaviorTimestamp(row.created_at);
		const proposalRow = findProposalRow(database, key, row.source_proposal_id);
		if (proposalRow === undefined) throw new BehaviorEvolutionValidationError("invalid_behavior_version_provenance");
		const proposal = this.hydrateProposal(database, proposalRow, false);
		if (
			proposal.status !== "activated" ||
			behaviorScopeKey(proposal.scope) !== behaviorScopeKey(key.scope) ||
			proposal.skillName !== key.skillName ||
			proposal.contentFingerprint !== fingerprint ||
			proposal.baseVersion !== baseVersion ||
			proposal.gameTime.tick !== gameTime.tick ||
			proposal.updatedAt !== row.created_at
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_version_provenance");
		}
		return {
			...key,
			versionKey: row.version_key,
			version: row.version,
			sourceProposalId: row.source_proposal_id,
			...(baseVersion === undefined ? {} : { baseVersion }),
			description: row.description,
			reflection,
			steps,
			contentFingerprint: fingerprint,
			gameTime,
			createdAt: row.created_at,
		};
	}

	private hydrateChange(database: Database.Database, row: ChangeRow): BehaviorRegistryChange {
		const key = registryKeyFromRow(row);
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorRevision(row.registry_revision);
		const fromVersion = row.from_version ?? undefined;
		const toVersion = row.to_version ?? undefined;
		const sourceProposalId = row.source_proposal_id ?? undefined;
		validateOptionalBehaviorVersion(fromVersion);
		validateOptionalBehaviorVersion(toVersion);
		if (sourceProposalId !== undefined) validateBehaviorProposalKey({ ...key, proposalId: sourceProposalId });
		if (
			(row.change_type === "activate" && (toVersion === undefined || sourceProposalId === undefined)) ||
			(row.change_type === "rollback" && (toVersion === undefined || sourceProposalId !== undefined)) ||
			(row.change_type === "demote" && (toVersion !== undefined || sourceProposalId !== undefined)) ||
			(row.change_type !== "activate" && row.change_type !== "rollback" && row.change_type !== "demote")
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_registry_change");
		}
		const registryRow = findRegistryRow(database, key);
		if (registryRow === undefined) throw new BehaviorRegistryNotFoundError();
		const registry = this.hydrateRegistry(database, registryRow);
		if (compareBehaviorDecimals(row.registry_revision, registry.revision) > 0) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_registry_change");
		}
		const gameTime = validateBehaviorTime(
			{ timelineId: key.timelineId, generation: key.generation, tick: row.game_tick },
			key,
		);
		validateBehaviorTimestamp(row.recorded_at);
		if (compareBehaviorDecimals(gameTime.tick, registry.gameTime.tick) > 0 || row.recorded_at > registry.updatedAt) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_registry_change");
		}
		return {
			...key,
			changeKey: row.change_key,
			registryRevision: row.registry_revision,
			type: row.change_type,
			...(fromVersion === undefined ? {} : { fromVersion }),
			...(toVersion === undefined ? {} : { toVersion }),
			...(sourceProposalId === undefined ? {} : { sourceProposalId }),
			gameTime,
			recordedAt: row.recorded_at,
		};
	}

	private parse(text: string): unknown {
		try {
			return parseStrictJsonText(text, this.limits.maxJsonDepth);
		} catch {
			throw new BehaviorEvolutionValidationError("invalid_persisted_behavior_json");
		}
	}
}

function findProposalRow(
	database: Database.Database,
	partition: BehaviorPartition,
	proposalId: string,
): ProposalRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_behavior_proposals
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND proposal_id = ?
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(partition.ownerId, partition.timelineId, partition.generation, proposalId) as
		| ProposalRow
		| undefined;
}

function findProposalRowByKey(database: Database.Database, proposalKey: bigint): ProposalRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_behavior_proposals WHERE proposal_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(proposalKey) as ProposalRow | undefined;
}

function findRegistryRow(database: Database.Database, key: BehaviorRegistryKey): RegistryRow | undefined {
	const scope = encodeScope(key.scope);
	return database
		.prepare(`
			SELECT * FROM oga_behavior_registries
			WHERE owner_id = ? AND timeline_id = ? AND generation = ?
			  AND scope_kind = ? AND scope_id = ? AND skill_name = ?
			LIMIT 1
		`)
		.get(key.ownerId, key.timelineId, key.generation, scope.kind, scope.id, key.skillName) as RegistryRow | undefined;
}

function findVersionRow(
	database: Database.Database,
	key: BehaviorRegistryKey,
	version: string,
): VersionRow | undefined {
	const scope = encodeScope(key.scope);
	const statement = database.prepare(`
		SELECT * FROM oga_behavior_versions
		WHERE owner_id = ? AND timeline_id = ? AND generation = ?
		  AND scope_kind = ? AND scope_id = ? AND skill_name = ? AND version = ?
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(key.ownerId, key.timelineId, key.generation, scope.kind, scope.id, key.skillName, version) as
		| VersionRow
		| undefined;
}

function findVersionRowByKey(database: Database.Database, versionKey: bigint): VersionRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_behavior_versions WHERE version_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(versionKey) as VersionRow | undefined;
}

function findVersionRowByProposal(
	database: Database.Database,
	partition: BehaviorPartition,
	proposalId: string,
): VersionRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_behavior_versions
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND source_proposal_id = ?
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(partition.ownerId, partition.timelineId, partition.generation, proposalId) as
		| VersionRow
		| undefined;
}

function findChangeRowByKey(database: Database.Database, changeKey: bigint): ChangeRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_behavior_changes WHERE change_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(changeKey) as ChangeRow | undefined;
}

function registryKeyFromProposal(proposal: BehaviorProposalRecord): BehaviorRegistryKey {
	return { ...proposal.partition, scope: proposal.scope, skillName: proposal.skillName };
}

function registryKeyFromRow(row: {
	owner_id: string;
	timeline_id: string;
	generation: string;
	scope_kind: string;
	scope_id: string;
	skill_name: string;
}): BehaviorRegistryKey {
	return {
		ownerId: row.owner_id,
		timelineId: row.timeline_id,
		generation: row.generation,
		scope: decodeScope(row.scope_kind, row.scope_id),
		skillName: row.skill_name,
	};
}

function encodeScope(scope: BehaviorRegistryScope): { readonly kind: "actor" | "shared"; readonly id: string } {
	return scope.kind === "actor" ? { kind: "actor", id: scope.actorId } : { kind: "shared", id: scope.registryId };
}

function decodeScope(kind: string, id: string): BehaviorRegistryScope {
	if (kind === "actor") return normalizeBehaviorScope({ kind, actorId: id });
	if (kind === "shared") return normalizeBehaviorScope({ kind, registryId: id });
	throw new BehaviorEvolutionValidationError("invalid_behavior_scope");
}

function increment(value: string): string {
	return (BigInt(value) + 1n).toString();
}

function assertChronology(currentTick: string, currentAt: string, nextTick: string, nextAt: string): void {
	if (compareBehaviorDecimals(nextTick, currentTick) < 0 || nextAt < currentAt) {
		throw new BehaviorEvolutionValidationError("behavior_time_regression");
	}
}

function isConstraintError(error: unknown): boolean {
	return (
		typeof error === "object" &&
		error !== null &&
		"code" in error &&
		typeof error.code === "string" &&
		error.code.startsWith("SQLITE_CONSTRAINT")
	);
}
