import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	BehaviorEvolutionService,
	type BehaviorLimits,
	BehaviorRegistryRevisionConflictError,
	type BehaviorRegistryScope,
	type BehaviorToolDescriptor,
	BehaviorVersionCapacityError,
} from "@opengameagent/extensions";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteBehaviorStore } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const limits = {
	maxSkillNameBytes: 80,
	maxDescriptionBytes: 256,
	maxReflectionBytes: 512,
	maxEvidencePerProposal: 4,
	maxEvidenceKindBytes: 32,
	maxEvidenceReferenceBytes: 128,
	maxStepsPerSkill: 4,
	maxStepInstructionBytes: 256,
	maxToolNameBytes: 64,
	maxDecisionCodeBytes: 64,
	maxJsonDepth: 16,
	maxToolsPerCatalog: 8,
	maxToolCatalogBytes: 16_384,
	maxToolSchemaDepth: 16,
	maxScopesPerView: 4,
	maxOpenProposalsPerOwnerGeneration: 8,
	maxVersionsPerSkill: 4,
	maxListPageSize: 8,
	maxPurgeBatchSize: 8,
} satisfies BehaviorLimits;

const partition = { ownerId: "owner", timelineId: "world", generation: "7" } as const;
const actorScope = { kind: "actor", actorId: "npc-1" } as const;
const sharedScope = { kind: "shared", registryId: "healers" } as const;
const tools: readonly BehaviorToolDescriptor[] = [
	{ name: "collect_herb", fingerprint: "a".repeat(64) },
	{ name: "brew_tonic", fingerprint: "b".repeat(64) },
];

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteBehaviorStore", () => {
	it("restores validated activation, immutable version, and change history after restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority, "aggressive");
		const created = await first.createProposal(proposal("proposal-1", actorScope, "prepare-tonic", 0));
		const reviewed = await first.reviewProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: created.revision,
			gameTime: gameTime("2"),
			recordedAt: timestamp(1),
		});
		expect(reviewed.activation?.version.version).toBe("1");
		firstAuthority.close();

		const reopened = service(open(path), "conservative");
		const restoredProposal = await reopened.getProposal({ ...partition, proposalId: "proposal-1" });
		expect(restoredProposal).toMatchObject({ status: "activated", revision: "3", decisionCode: "sandbox-pass" });
		const key = registryKey(actorScope, "prepare-tonic");
		const registry = await reopened.getRegistry(key);
		expect(registry).toMatchObject({ activeVersion: "1", latestVersion: "1", gameTime: gameTime("2") });
		const version = await reopened.getVersion({ ...key, version: "1" });
		expect(version).toMatchObject({
			sourceProposalId: "proposal-1",
			steps: proposalSteps().map((step, index) => ({
				...step,
				stepId: `proposal-1-step-${index + 1}`,
			})),
		});
		expect((await reopened.listChanges({ ...key, limit: 8 })).records).toMatchObject([
			{ type: "activate", registryRevision: "1", toVersion: "1", sourceProposalId: "proposal-1" },
		]);
		expect((await reopened.listActive({ ...partition, scopes: [actorScope], limit: 8 })).records).toHaveLength(1);
	});

	it("settles competing activation and registry transitions with shared-database CAS", async () => {
		const path = await databasePath();
		const left = service(open(path), "conservative");
		const right = service(open(path), "conservative");
		const initial = await left.createProposal(proposal("initial", actorScope, "prepare-tonic", 0));
		const initialReview = await left.reviewProposal({
			...partition,
			proposalId: initial.proposalId,
			expectedRevision: initial.revision,
			gameTime: gameTime("1"),
			recordedAt: timestamp(1),
		});
		await left.activateProposal({
			...partition,
			proposalId: initial.proposalId,
			expectedRevision: initialReview.proposal.revision,
			gameTime: gameTime("1"),
			recordedAt: timestamp(1),
		});

		const leftProposal = await left.createProposal(proposal("left", actorScope, "prepare-tonic", 2));
		const rightProposal = await right.createProposal(proposal("right", actorScope, "prepare-tonic", 2));
		const leftReview = await left.reviewProposal({
			...partition,
			proposalId: leftProposal.proposalId,
			expectedRevision: leftProposal.revision,
			gameTime: gameTime("2"),
			recordedAt: timestamp(3),
		});
		const rightReview = await right.reviewProposal({
			...partition,
			proposalId: rightProposal.proposalId,
			expectedRevision: rightProposal.revision,
			gameTime: gameTime("2"),
			recordedAt: timestamp(3),
		});
		const outcomes = await Promise.allSettled([
			left.activateProposal({
				...partition,
				proposalId: leftProposal.proposalId,
				expectedRevision: leftReview.proposal.revision,
				gameTime: gameTime("3"),
				recordedAt: timestamp(4),
			}),
			right.activateProposal({
				...partition,
				proposalId: rightProposal.proposalId,
				expectedRevision: rightReview.proposal.revision,
				gameTime: gameTime("3"),
				recordedAt: timestamp(4),
			}),
		]);
		expect(outcomes.filter((outcome) => outcome.status === "fulfilled")).toHaveLength(1);
		const rejected = outcomes.find((outcome) => outcome.status === "rejected");
		expect(rejected).toMatchObject({ reason: expect.any(BehaviorRegistryRevisionConflictError) });

		const key = registryKey(actorScope, "prepare-tonic");
		const stale = await right.getRegistry(key);
		if (stale === undefined) throw new Error("missing registry");
		const firstTransition = await left.transitionRegistry({
			...key,
			expectedRevision: stale.revision,
			targetVersion: "1",
			gameTime: gameTime("4"),
			recordedAt: timestamp(5),
		});
		expect(firstTransition.registry.activeVersion).toBe("1");
		await expect(
			right.transitionRegistry({
				...key,
				expectedRevision: stale.revision,
				gameTime: gameTime("4"),
				recordedAt: timestamp(5),
			}),
		).rejects.toBeInstanceOf(BehaviorRegistryRevisionConflictError);
	});

	it("applies ordered actor-over-shared scope precedence inside the bounded SQL query", async () => {
		const current = service(open(await databasePath()), "aggressive");
		for (const [id, scope] of [
			["shared", sharedScope],
			["actor", actorScope],
		] as const) {
			const created = await current.createProposal(proposal(id, scope, "prepare-tonic", 0));
			await current.reviewProposal({
				...partition,
				proposalId: created.proposalId,
				expectedRevision: created.revision,
				gameTime: gameTime("1"),
				recordedAt: timestamp(1),
			});
		}
		const actorFirst = await current.listActive({
			...partition,
			scopes: [actorScope, sharedScope],
			limit: 8,
		});
		expect(actorFirst.records).toHaveLength(1);
		expect(actorFirst.records[0]?.registry.scope).toEqual(actorScope);
		const sharedFirst = await current.listActive({
			...partition,
			scopes: [sharedScope, actorScope],
			limit: 8,
		});
		expect(sharedFirst.records[0]?.registry.scope).toEqual(sharedScope);
	});

	it("bounds versions and retains active provenance until explicit inactive cleanup", async () => {
		const path = await databasePath();
		const authority = open(path);
		const boundedLimits = { ...limits, maxVersionsPerSkill: 2 };
		const current = service(authority, "aggressive", boundedLimits);
		for (const [id, offset] of [
			["v1", 0],
			["v2", 2],
		] as const) {
			const created = await current.createProposal(proposal(id, actorScope, "prepare-tonic", offset));
			await current.reviewProposal({
				...partition,
				proposalId: created.proposalId,
				expectedRevision: created.revision,
				gameTime: gameTime(String(offset + 1)),
				recordedAt: timestamp(offset + 1),
			});
		}
		const third = await current.createProposal(proposal("v3", actorScope, "prepare-tonic", 4));
		await expect(
			current.reviewProposal({
				...partition,
				proposalId: third.proposalId,
				expectedRevision: third.revision,
				gameTime: gameTime("5"),
				recordedAt: timestamp(5),
			}),
		).rejects.toBeInstanceOf(BehaviorVersionCapacityError);

		const key = registryKey(actorScope, "prepare-tonic");
		const registry = await current.getRegistry(key);
		if (registry === undefined) throw new Error("missing registry");
		const rolledBack = await current.transitionRegistry({
			...key,
			expectedRevision: registry.revision,
			targetVersion: "1",
			gameTime: gameTime("6"),
			recordedAt: timestamp(6),
		});
		expect(await current.purgeInactiveVersions(key, timestamp(7), 8)).toEqual([2n]);
		expect(await current.purgeProposals(partition, timestamp(7), 8)).toEqual([2n]);
		expect(await current.getProposal({ ...partition, proposalId: "v1" })).toBeDefined();
		expect(await current.getProposal({ ...partition, proposalId: "v2" })).toBeUndefined();
		expect(await current.purgeChanges(key, timestamp(7), 8)).toEqual([1n, 2n, 3n]);
		expect((await current.listChanges({ ...key, limit: 8 })).records).toEqual([]);
		expect(rolledBack.registry.activeVersion).toBe("1");
		authority.verify("full");
	});

	it("fails closed on duplicate-key JSON in pending and activated records", async () => {
		const authority = open(await databasePath());
		const current = service(authority, "aggressive");
		const pending = await current.createProposal(proposal("corrupt-json", actorScope, "corrupt-json", 0));
		const duplicateReflection =
			'{"summary":"first","summary":"second","evidence":[{"kind":"receipt","referenceId":"r1"}]}';
		authority.write((database) =>
			database
				.prepare("UPDATE oga_behavior_proposals SET reflection_json = ? WHERE proposal_id = ?")
				.run(duplicateReflection, pending.proposalId),
		);
		await expect(current.getProposal({ ...partition, proposalId: pending.proposalId })).rejects.toMatchObject({
			code: "invalid_persisted_behavior_json",
		});

		const activated = await current.createProposal(proposal("activated-json", actorScope, "prepare-tonic", 2));
		await current.reviewProposal({
			...partition,
			proposalId: activated.proposalId,
			expectedRevision: activated.revision,
			gameTime: gameTime("3"),
			recordedAt: timestamp(3),
		});
		authority.write((database) =>
			database
				.prepare("UPDATE oga_behavior_versions SET reflection_json = ? WHERE version = '1'")
				.run(duplicateReflection),
		);
		await expect(current.getProposal({ ...partition, proposalId: activated.proposalId })).rejects.toMatchObject({
			code: "invalid_persisted_behavior_json",
		});
	});

	it("fails closed when an immutable version loses its proposal provenance", async () => {
		const authority = open(await databasePath());
		const current = service(authority, "aggressive");
		const activated = await current.createProposal(proposal("activated-provenance", actorScope, "prepare-tonic", 0));
		await current.reviewProposal({
			...partition,
			proposalId: activated.proposalId,
			expectedRevision: activated.revision,
			gameTime: gameTime("1"),
			recordedAt: timestamp(1),
		});
		authority.write((database) =>
			database.prepare("DELETE FROM oga_behavior_proposals WHERE proposal_id = ?").run(activated.proposalId),
		);
		await expect(
			current.getVersion({ ...registryKey(actorScope, "prepare-tonic"), version: "1" }),
		).rejects.toMatchObject({ code: "invalid_behavior_version_provenance" });
	});

	it("fails closed on an impossible active version pointer", async () => {
		const authority = open(await databasePath());
		const current = service(authority, "aggressive");
		const activated = await current.createProposal(proposal("activated-pointer", actorScope, "prepare-tonic", 0));
		await current.reviewProposal({
			...partition,
			proposalId: activated.proposalId,
			expectedRevision: activated.revision,
			gameTime: gameTime("1"),
			recordedAt: timestamp(1),
		});
		authority.write((database) =>
			database
				.prepare("UPDATE oga_behavior_registries SET active_version = '999' WHERE skill_name = ?")
				.run("prepare-tonic"),
		);
		await expect(current.getRegistry(registryKey(actorScope, "prepare-tonic"))).rejects.toMatchObject({
			code: "invalid_behavior_registry_version",
		});
	});

	it("fails closed when change history claims a future registry revision", async () => {
		const authority = open(await databasePath());
		const current = service(authority, "aggressive");
		const activated = await current.createProposal(proposal("activated-change", actorScope, "prepare-tonic", 0));
		await current.reviewProposal({
			...partition,
			proposalId: activated.proposalId,
			expectedRevision: activated.revision,
			gameTime: gameTime("1"),
			recordedAt: timestamp(1),
		});
		authority.write((database) =>
			database.prepare("UPDATE oga_behavior_changes SET registry_revision = '999' WHERE change_key = 1").run(),
		);
		await expect(current.listChanges({ ...registryKey(actorScope, "prepare-tonic"), limit: 8 })).rejects.toMatchObject({
			code: "invalid_behavior_registry_change",
		});
	});
});

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-evolution-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 1_000, verifyOnOpen: "full" });
	authorities.push(authority);
	return authority;
}

function service(
	authority: SqliteAuthority,
	mode: "off" | "conservative" | "aggressive",
	configuredLimits: BehaviorLimits = limits,
): BehaviorEvolutionService {
	return new BehaviorEvolutionService({
		store: new SqliteBehaviorStore(authority, configuredLimits),
		limits: configuredLimits,
		policy: { resolve: async () => mode },
		tools: { list: async () => tools },
		evidence: { validate: async () => ({ accepted: true }) },
		sandbox: { validate: async () => ({ accepted: true, decisionCode: "sandbox-pass" }) },
	});
}

function proposal(proposalId: string, scope: BehaviorRegistryScope, skillName: string, offset: number) {
	return {
		proposalId,
		partition,
		scope,
		skillName,
		description: `Behavior ${skillName}`,
		reflection: {
			summary: `Observed outcome for ${proposalId}`,
			evidence: [{ kind: "receipt", referenceId: `receipt-${proposalId}` }],
		},
		steps: proposalSteps().map((step, index) => ({ ...step, stepId: `${proposalId}-step-${index + 1}` })),
		gameTime: gameTime(String(offset)),
		recordedAt: timestamp(offset),
	};
}

function proposalSteps() {
	return [
		{ stepId: "step-1", instruction: "Collect the herb.", toolName: "collect_herb" },
		{ stepId: "step-2", instruction: "Brew the tonic.", toolName: "brew_tonic" },
	];
}

function registryKey(scope: BehaviorRegistryScope, skillName: string) {
	return { ...partition, scope, skillName };
}

function gameTime(tick: string) {
	return { timelineId: "world", generation: "7", tick } as const;
}

function timestamp(offset: number): string {
	return new Date(Date.UTC(2026, 7, 29, 0, 0, offset)).toISOString();
}
