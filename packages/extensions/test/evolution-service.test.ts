import type { AgentTool } from "@earendil-works/pi-agent-core";
import { Type } from "typebox";
import type { RunCoordinate } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import {
	BehaviorContextSource,
	type BehaviorEvidenceValidationRequest,
	type BehaviorEvidenceValidationResult,
	BehaviorEvolutionDisabledError,
	type BehaviorEvolutionMode,
	BehaviorEvolutionService,
	BehaviorEvolutionValidationError,
	type BehaviorLimits,
	BehaviorProposalCapacityError,
	BehaviorRegistryRevisionConflictError,
	type BehaviorRegistryScope,
	type BehaviorSandboxValidationRequest,
	type BehaviorSandboxValidationResult,
	type BehaviorToolDescriptor,
	BehaviorToolUnavailableError,
	createBehaviorEvolutionTools,
	createBehaviorToolDescriptors,
	InMemoryBehaviorStore,
} from "../src/index.js";

const contextCoordinate: RunCoordinate = {
	sessionId: "session-1",
	actorId: "npc-1",
	runId: "run-1",
	turn: "1",
};

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
	maxOpenProposalsPerOwnerGeneration: 4,
	maxVersionsPerSkill: 4,
	maxListPageSize: 8,
	maxPurgeBatchSize: 8,
} satisfies BehaviorLimits;

const partition = { ownerId: "owner", timelineId: "world", generation: "3" } as const;
const actorScope = { kind: "actor", actorId: "npc-1" } as const;
const sharedScope = { kind: "shared", registryId: "faction-healers" } as const;
const gameTime = { timelineId: "world", generation: "3", tick: "40" } as const;
const laterGameTime = { timelineId: "world", generation: "3", tick: "41" } as const;
const recordedAt = "2026-08-29T00:00:00.000Z";
const laterAt = "2026-08-29T00:00:01.000Z";
const toolA = { name: "collect_herb", fingerprint: "a".repeat(64) } as const;
const toolB = { name: "brew_tonic", fingerprint: "b".repeat(64) } as const;

function proposalRequest(proposalId: string, scope: BehaviorRegistryScope = actorScope, skillName = "prepare-tonic") {
	return {
		proposalId,
		partition,
		scope,
		skillName,
		description: "Gather an ingredient and prepare a tonic.",
		reflection: {
			summary: "A prior treatment failed because the ingredient was not gathered first.",
			evidence: [{ kind: "action-receipt", referenceId: `receipt-${proposalId}` }],
		},
		steps: [
			{ stepId: `${proposalId}-step-1`, instruction: "Gather the required herb.", toolName: toolA.name },
			{ stepId: `${proposalId}-step-2`, instruction: "Prepare the tonic.", toolName: toolB.name },
		],
		gameTime,
		recordedAt,
	};
}

function harness(mode: BehaviorEvolutionMode = "conservative") {
	let currentMode = mode;
	let currentTools: readonly BehaviorToolDescriptor[] = [toolA, toolB];
	const evidence = vi.fn(
		async (_request: BehaviorEvidenceValidationRequest): Promise<BehaviorEvidenceValidationResult> => ({
			accepted: true,
		}),
	);
	const sandbox = vi.fn(
		async (_request: BehaviorSandboxValidationRequest): Promise<BehaviorSandboxValidationResult> => ({
			accepted: true,
			decisionCode: "sandbox-pass",
		}),
	);
	const store = new InMemoryBehaviorStore();
	const service = new BehaviorEvolutionService({
		store,
		limits,
		policy: { resolve: async () => currentMode },
		tools: { list: async () => currentTools },
		evidence: { validate: evidence },
		sandbox: { validate: sandbox },
	});
	return {
		service,
		store,
		evidence,
		sandbox,
		setMode: (value: BehaviorEvolutionMode) => {
			currentMode = value;
		},
		setTools: (value: readonly BehaviorToolDescriptor[]) => {
			currentTools = value;
		},
	};
}

describe("behavior evolution", () => {
	it("validates evidence and a descriptor-only sandbox before conservative host activation", async () => {
		const { service, evidence, sandbox } = harness();
		const created = await service.createProposal(proposalRequest("proposal-1"));
		expect(created.status).toBe("pending");
		expect(created.baseVersion).toBeUndefined();
		expect(created.steps.map((step) => step.toolFingerprint)).toEqual([toolA.fingerprint, toolB.fingerprint]);

		const reviewed = await service.reviewProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: created.revision,
			gameTime,
			recordedAt: laterAt,
		});
		expect(reviewed.proposal.status).toBe("validated");
		expect(reviewed.activation).toBeUndefined();
		expect(evidence).toHaveBeenCalledOnce();
		expect(sandbox).toHaveBeenCalledOnce();
		const sandboxRequest = sandbox.mock.calls[0]?.[0];
		expect(sandboxRequest?.tools).toEqual([toolA, toolB]);
		expect(sandboxRequest?.tools.every((tool) => !("execute" in tool))).toBe(true);

		const activated = await service.activateProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: reviewed.proposal.revision,
			gameTime: laterGameTime,
			recordedAt: laterAt,
		});
		expect(activated.version.version).toBe("1");
		expect(activated.registry.activeVersion).toBe("1");
		expect(activated.proposal.status).toBe("activated");
		expect(activated.change.type).toBe("activate");
	});

	it("auto-activates only validated safe compositions in aggressive mode", async () => {
		const { service } = harness("aggressive");
		const created = await service.createProposal(proposalRequest("proposal-auto"));
		const result = await service.reviewProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: created.revision,
			gameTime: laterGameTime,
			recordedAt: laterAt,
		});
		expect(result.proposal.status).toBe("activated");
		expect(result.activation?.version.version).toBe("1");
	});

	it("persists a sandbox rejection without activating the proposal", async () => {
		const h = harness("aggressive");
		h.sandbox.mockResolvedValueOnce({ accepted: false, decisionCode: "unsafe-sequence" });
		const created = await h.service.createProposal(proposalRequest("proposal-reject"));
		const result = await h.service.reviewProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: created.revision,
			gameTime,
			recordedAt: laterAt,
		});
		expect(result.proposal).toMatchObject({ status: "rejected", decisionCode: "unsafe-sequence" });
		expect(result.activation).toBeUndefined();
	});

	it("records evidence rejection and never invokes the sandbox", async () => {
		const h = harness();
		h.evidence.mockResolvedValueOnce({ accepted: false, decisionCode: "missing-receipt" });
		const created = await h.service.createProposal(proposalRequest("proposal-evidence"));
		const result = await h.service.reviewProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: created.revision,
			gameTime,
			recordedAt: laterAt,
		});
		expect(result.proposal).toMatchObject({ status: "rejected", decisionCode: "missing-receipt" });
		expect(h.sandbox).not.toHaveBeenCalled();
	});

	it("fails closed when a registered tool changes after validation", async () => {
		const h = harness();
		const created = await h.service.createProposal(proposalRequest("proposal-drift"));
		const reviewed = await h.service.reviewProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: created.revision,
			gameTime,
			recordedAt: laterAt,
		});
		h.setTools([{ ...toolA, fingerprint: "c".repeat(64) }, toolB]);
		await expect(
			h.service.activateProposal({
				...partition,
				proposalId: created.proposalId,
				expectedRevision: reviewed.proposal.revision,
				gameTime: laterGameTime,
				recordedAt: laterAt,
			}),
		).rejects.toBeInstanceOf(BehaviorToolUnavailableError);
	});

	it("uses immutable versions and CAS-protected rollback/demotion", async () => {
		const h = harness("aggressive");
		const first = await h.service.createProposal(proposalRequest("proposal-v1"));
		const firstReviewed = await h.service.reviewProposal({
			...partition,
			proposalId: first.proposalId,
			expectedRevision: first.revision,
			gameTime,
			recordedAt: laterAt,
		});
		const second = await h.service.createProposal({
			...proposalRequest("proposal-v2"),
			gameTime: laterGameTime,
			recordedAt: laterAt,
		});
		const secondReviewed = await h.service.reviewProposal({
			...partition,
			proposalId: second.proposalId,
			expectedRevision: second.revision,
			gameTime: laterGameTime,
			recordedAt: "2026-08-29T00:00:02.000Z",
		});
		expect(firstReviewed.activation?.version.version).toBe("1");
		expect(secondReviewed.activation?.version.version).toBe("2");
		expect(second.baseVersion).toBe("1");

		const registry = secondReviewed.activation?.registry;
		expect(registry).toBeDefined();
		const rolledBack = await h.service.transitionRegistry({
			...partition,
			scope: actorScope,
			skillName: "prepare-tonic",
			expectedRevision: registry?.revision as string,
			targetVersion: "1",
			gameTime: laterGameTime,
			recordedAt: "2026-08-29T00:00:03.000Z",
		});
		expect(rolledBack.registry.activeVersion).toBe("1");
		expect(rolledBack.change).toMatchObject({ type: "rollback", fromVersion: "2", toVersion: "1" });

		await expect(
			h.service.transitionRegistry({
				...partition,
				scope: actorScope,
				skillName: "prepare-tonic",
				expectedRevision: registry?.revision as string,
				gameTime: laterGameTime,
				recordedAt: "2026-08-29T00:00:04.000Z",
			}),
		).rejects.toBeInstanceOf(BehaviorRegistryRevisionConflictError);

		const demoted = await h.service.transitionRegistry({
			...partition,
			scope: actorScope,
			skillName: "prepare-tonic",
			expectedRevision: rolledBack.registry.revision,
			gameTime: laterGameTime,
			recordedAt: "2026-08-29T00:00:04.000Z",
		});
		expect(demoted.registry.activeVersion).toBeUndefined();
		expect(demoted.change.type).toBe("demote");
	});

	it("rejects game-time and wall-clock regressions across every mutable boundary", async () => {
		const h = harness();
		const created = await h.service.createProposal(proposalRequest("proposal-time"));
		await expect(
			h.service.reviewProposal({
				...partition,
				proposalId: created.proposalId,
				expectedRevision: created.revision,
				gameTime: { ...gameTime, tick: "39" },
				recordedAt: laterAt,
			}),
		).rejects.toMatchObject({ code: "behavior_time_regression" });

		const reviewed = await h.service.reviewProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: created.revision,
			gameTime,
			recordedAt: laterAt,
		});
		await expect(
			h.service.activateProposal({
				...partition,
				proposalId: created.proposalId,
				expectedRevision: reviewed.proposal.revision,
				gameTime: { ...gameTime, tick: "39" },
				recordedAt: "2026-08-29T00:00:02.000Z",
			}),
		).rejects.toMatchObject({ code: "behavior_time_regression" });

		const activated = await h.service.activateProposal({
			...partition,
			proposalId: created.proposalId,
			expectedRevision: reviewed.proposal.revision,
			gameTime: laterGameTime,
			recordedAt: "2026-08-29T00:00:02.000Z",
		});
		await expect(
			h.service.createProposal({
				...proposalRequest("proposal-old-game-time"),
				recordedAt: "2026-08-29T00:00:03.000Z",
			}),
		).rejects.toMatchObject({ code: "behavior_time_regression" });
		await expect(
			h.service.createProposal({
				...proposalRequest("proposal-old-wall-time"),
				gameTime: laterGameTime,
				recordedAt: laterAt,
			}),
		).rejects.toMatchObject({ code: "behavior_time_regression" });
		await expect(
			h.service.transitionRegistry({
				...partition,
				scope: actorScope,
				skillName: "prepare-tonic",
				expectedRevision: activated.registry.revision,
				gameTime,
				recordedAt: "2026-08-29T00:00:03.000Z",
			}),
		).rejects.toMatchObject({ code: "behavior_time_regression" });
	});

	it("retains active provenance and bounds inactive versions, proposals, and changes", async () => {
		const h = harness("aggressive");
		const first = await h.service.createProposal(proposalRequest("proposal-retain-v1"));
		const firstReview = await h.service.reviewProposal({
			...partition,
			proposalId: first.proposalId,
			expectedRevision: first.revision,
			gameTime,
			recordedAt: laterAt,
		});
		const key = { ...partition, scope: actorScope, skillName: "prepare-tonic" } as const;
		expect(await h.service.purgeInactiveVersions(key, "2026-08-29T00:00:02.000Z", 8)).toEqual([]);
		expect(await h.service.purgeProposals(partition, "2026-08-29T00:00:02.000Z", 8)).toEqual([]);

		const second = await h.service.createProposal({
			...proposalRequest("proposal-retain-v2"),
			gameTime: laterGameTime,
			recordedAt: laterAt,
		});
		const secondReview = await h.service.reviewProposal({
			...partition,
			proposalId: second.proposalId,
			expectedRevision: second.revision,
			gameTime: { ...gameTime, tick: "42" },
			recordedAt: "2026-08-29T00:00:02.000Z",
		});
		expect(secondReview.activation?.version.baseVersion).toBe("1");
		const firstVersion = firstReview.activation?.version.version;
		if (firstVersion === undefined) throw new Error("missing first behavior version");
		const rolledBack = await h.service.transitionRegistry({
			...key,
			expectedRevision: secondReview.activation?.registry.revision as string,
			targetVersion: firstVersion,
			gameTime: { ...gameTime, tick: "43" },
			recordedAt: "2026-08-29T00:00:03.000Z",
		});
		expect(rolledBack.registry.activeVersion).toBe("1");
		expect(await h.service.purgeInactiveVersions(key, "2026-08-29T00:00:04.000Z", 8)).toEqual([2n]);
		expect(await h.service.purgeProposals(partition, "2026-08-29T00:00:04.000Z", 8)).toEqual([2n]);
		expect(await h.service.getProposal({ ...partition, proposalId: first.proposalId })).toBeDefined();
		expect(await h.service.getProposal({ ...partition, proposalId: second.proposalId })).toBeUndefined();
		expect(await h.service.purgeChanges(key, "2026-08-29T00:00:04.000Z", 8)).toEqual([1n, 2n, 3n]);
		expect((await h.service.listChanges({ ...key, limit: 8 })).records).toEqual([]);
	});

	it("rejects stale sibling proposals that were based on the same active version", async () => {
		const h = harness();
		const initial = await h.service.createProposal(proposalRequest("proposal-initial"));
		const initialReview = await h.service.reviewProposal({
			...partition,
			proposalId: initial.proposalId,
			expectedRevision: initial.revision,
			gameTime,
			recordedAt: laterAt,
		});
		await h.service.activateProposal({
			...partition,
			proposalId: initial.proposalId,
			expectedRevision: initialReview.proposal.revision,
			gameTime,
			recordedAt: laterAt,
		});

		const left = await h.service.createProposal({ ...proposalRequest("proposal-left"), recordedAt: laterAt });
		const right = await h.service.createProposal({ ...proposalRequest("proposal-right"), recordedAt: laterAt });
		const reviews = await Promise.all(
			[left, right].map((proposal) =>
				h.service.reviewProposal({
					...partition,
					proposalId: proposal.proposalId,
					expectedRevision: proposal.revision,
					gameTime: laterGameTime,
					recordedAt: "2026-08-29T00:00:02.000Z",
				}),
			),
		);
		const leftReview = reviews[0];
		const rightReview = reviews[1];
		if (leftReview === undefined || rightReview === undefined) throw new Error("missing review result");
		await h.service.activateProposal({
			...partition,
			proposalId: left.proposalId,
			expectedRevision: leftReview.proposal.revision,
			gameTime: laterGameTime,
			recordedAt: "2026-08-29T00:00:03.000Z",
		});
		await expect(
			h.service.activateProposal({
				...partition,
				proposalId: right.proposalId,
				expectedRevision: rightReview.proposal.revision,
				gameTime: laterGameTime,
				recordedAt: "2026-08-29T00:00:03.000Z",
			}),
		).rejects.toBeInstanceOf(BehaviorRegistryRevisionConflictError);
	});

	it("prefers the actor registry over a same-name shared registry", async () => {
		const h = harness("aggressive");
		for (const [id, scope] of [
			["shared-proposal", sharedScope],
			["actor-proposal", actorScope],
		] as const) {
			const proposal = await h.service.createProposal(proposalRequest(id, scope));
			await h.service.reviewProposal({
				...partition,
				proposalId: proposal.proposalId,
				expectedRevision: proposal.revision,
				gameTime,
				recordedAt: laterAt,
			});
		}
		const page = await h.service.listActive({
			...partition,
			scopes: [actorScope, sharedScope],
			limit: 8,
		});
		expect(page.records).toHaveLength(1);
		expect(page.records[0]?.registry.scope).toEqual(actorScope);
	});

	it("exposes only the proposal Tool and active instructions to the existing Pi loop", async () => {
		const h = harness("aggressive");
		let sequence = 0;
		const tools = await createBehaviorEvolutionTools({
			service: h.service,
			readPartition: () => partition,
			readScope: () => actorScope,
			readGameTime: () => gameTime,
			recordedAt: () => recordedAt,
			proposalId: () => "proposal-tool",
			stepId: () => {
				sequence += 1;
				return `generated-step-${sequence}`;
			},
		});
		expect(tools.map((tool) => tool.name)).toEqual(["behavior_skill_propose"]);
		const result = await tools[0]?.execute(
			"call-1",
			{
				skillName: "prepare-tonic",
				description: "Prepare a tonic safely.",
				reflection: {
					summary: "Order mattered in the prior attempt.",
					evidence: [{ kind: "action-receipt", referenceId: "receipt-tool" }],
				},
				steps: [
					{ instruction: "Gather the herb.", toolName: toolA.name },
					{ instruction: "Brew the tonic.", toolName: toolB.name },
				],
			},
			undefined,
		);
		expect(result?.details).toMatchObject({ proposalId: "proposal-tool", status: "pending" });

		const created = await h.service.getProposal({ ...partition, proposalId: "proposal-tool" });
		const reviewed = await h.service.reviewProposal({
			...partition,
			proposalId: "proposal-tool",
			expectedRevision: created?.revision as string,
			gameTime,
			recordedAt: laterAt,
		});
		expect(reviewed.activation).toBeDefined();

		const source = new BehaviorContextSource({
			sourceId: "behavior-context",
			service: h.service,
			resolveView: async () => ({ partition, scopes: [actorScope], limit: 8 }),
		});
		expect(source.provenanceKind).toBe("extension");
		const { references } = await source.load(
			{ sessionId: "session-1", actorId: "npc-1", inputId: "input-1", payload: {} },
			new AbortController().signal,
			contextCoordinate,
		);
		expect(references).toHaveLength(1);
		expect(references[0]?.content).toMatchObject({
			kind: "behavior-skill",
			name: "prepare-tonic",
			steps: [
				{ toolName: toolA.name, instruction: "Gather the herb." },
				{ toolName: toolB.name, instruction: "Brew the tonic." },
			],
		});
		expect(JSON.stringify(references)).not.toContain("Order mattered");
		expect(JSON.stringify(references)).not.toContain("receipt-tool");
	});

	it("hides proposal and active Skill surfaces when policy is off", async () => {
		const h = harness("off");
		await expect(h.service.createProposal(proposalRequest("proposal-off"))).rejects.toBeInstanceOf(
			BehaviorEvolutionDisabledError,
		);
		expect(
			await createBehaviorEvolutionTools({
				service: h.service,
				readPartition: () => partition,
				readScope: () => actorScope,
				readGameTime: () => gameTime,
				recordedAt: () => recordedAt,
				proposalId: () => "unused",
				stepId: () => "unused-step",
			}),
		).toEqual([]);
	});

	it("rejects cross-actor context reads", async () => {
		const h = harness();
		const source = new BehaviorContextSource({
			sourceId: "behavior-context",
			service: h.service,
			resolveView: async () => ({ partition, scopes: [actorScope], limit: 8 }),
		});
		await expect(
			source.load(
				{ sessionId: "session-1", actorId: "npc-2", inputId: "input-1", payload: {} },
				new AbortController().signal,
				{ ...contextCoordinate, actorId: "npc-2" },
			),
		).rejects.toMatchObject({ code: "behavior_actor_scope_mismatch" });
	});

	it("enforces proposal capacity and explicit rejected retention", async () => {
		const h = harness();
		const bounded = new BehaviorEvolutionService({
			store: h.store,
			limits: { ...limits, maxOpenProposalsPerOwnerGeneration: 1 },
			policy: { resolve: async (): Promise<BehaviorEvolutionMode> => "conservative" },
			tools: { list: async () => [toolA, toolB] },
			evidence: { validate: async () => ({ accepted: true }) },
			sandbox: { validate: async () => ({ accepted: false, decisionCode: "rejected" }) },
		});
		const first = await bounded.createProposal(proposalRequest("proposal-capacity-1"));
		await expect(bounded.createProposal(proposalRequest("proposal-capacity-2"))).rejects.toBeInstanceOf(
			BehaviorProposalCapacityError,
		);
		await bounded.reviewProposal({
			...partition,
			proposalId: first.proposalId,
			expectedRevision: first.revision,
			gameTime,
			recordedAt: laterAt,
		});
		await expect(bounded.createProposal(proposalRequest("proposal-capacity-2"))).resolves.toMatchObject({
			status: "pending",
		});
		expect(await bounded.purgeProposals(partition, "2026-08-29T00:00:02.000Z", 8)).toEqual([1n]);
	});

	it("requires every limit field and validates tool descriptor fingerprints", () => {
		for (const key of Object.keys(limits) as (keyof BehaviorLimits)[]) {
			const incomplete = { ...limits } as Partial<BehaviorLimits>;
			delete incomplete[key];
			expect(
				() =>
					new BehaviorEvolutionService({
						store: new InMemoryBehaviorStore(),
						limits: incomplete as BehaviorLimits,
						policy: { resolve: async (): Promise<BehaviorEvolutionMode> => "off" },
						tools: { list: async () => [] },
						evidence: { validate: async () => ({ accepted: true }) },
						sandbox: { validate: async () => ({ accepted: true, decisionCode: "ok" }) },
					}),
			).toThrow(`invalid_limit:${key}`);
		}

		const baseTool = {
			name: "inspect_state",
			label: "Inspect state",
			description: "Read current game state.",
			parameters: Type.Object({ area: Type.String() }, { additionalProperties: false }),
			execute: async () => ({ content: [{ type: "text" as const, text: "ok" }], details: undefined }),
		} satisfies AgentTool;
		const first = createBehaviorToolDescriptors([baseTool], limits);
		const changed = createBehaviorToolDescriptors(
			[{ ...baseTool, parameters: Type.Object({ region: Type.String() }, { additionalProperties: false }) }],
			limits,
		);
		expect(first[0]?.fingerprint).toMatch(/^[a-f0-9]{64}$/u);
		expect(changed[0]?.fingerprint).not.toBe(first[0]?.fingerprint);
		expect(() => createBehaviorToolDescriptors([baseTool], { ...limits, maxToolCatalogBytes: 1 })).toThrow(
			"invalid_behavior_tool_catalog_too_large",
		);
		expect(() => createBehaviorToolDescriptors([baseTool], { ...limits, maxToolSchemaDepth: 1 })).toThrow(
			"invalid_behavior_tool_schema_too_deep",
		);
	});

	it("propagates cancellation before persistence", async () => {
		const h = harness();
		const controller = new AbortController();
		controller.abort(new Error("cancelled"));
		await expect(
			h.service.createProposal({ ...proposalRequest("proposal-cancel"), signal: controller.signal }),
		).rejects.toThrow("cancelled");
		expect(await h.service.getProposal({ ...partition, proposalId: "proposal-cancel" })).toBeUndefined();
	});

	it("rejects unknown reflection and step fields before storage", async () => {
		const h = harness();
		await expect(
			h.service.createProposal({
				...proposalRequest("proposal-unknown"),
				reflection: {
					...proposalRequest("proposal-unknown").reflection,
					hiddenReasoning: "must never be accepted",
				} as never,
			}),
		).rejects.toBeInstanceOf(BehaviorEvolutionValidationError);
	});
});
