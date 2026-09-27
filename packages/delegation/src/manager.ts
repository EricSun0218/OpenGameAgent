import { createHash } from "node:crypto";
import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { GameRunHandle, NormalizedGameInput } from "@opengameagent/agent";
import { canonicalizeJsonValue, normalizeJsonValue, type JsonValue, type RunCoordinate } from "@opengameagent/protocol";
import type { GameRuntime, RuntimeActorOptions } from "@opengameagent/runtime";
import { Mutex } from "async-mutex";
import { Type } from "typebox";
import { DelegationCapacityError, DelegationError, DelegationLeaseError, DelegationNotFoundError } from "./errors.js";
import type { DelegationService } from "./service.js";
import type {
	DelegatedTaskPayload,
	DelegationDelivery,
	DelegationDraft,
	DelegationKey,
	DelegationMode,
	DelegationOutcomeProjector,
	DelegationPartition,
	DelegationRecord,
	DelegationSequenceKey,
	DelegationTarget,
	DelegationTargetRegistration,
} from "./types.js";
import { byteLength, decimal, identifier, json, partition, timestamp } from "./validation.js";

export interface DelegationManagerOptions {
	readonly service: DelegationService;
	readonly runtime: GameRuntime;
	readonly targets: readonly DelegationTargetRegistration[];
	readonly outcome: DelegationOutcomeProjector;
	readonly workerId: string;
	readonly operationalNow: () => string | Promise<string>;
	readonly leaseExpiresAt: (operationalNow: string) => string | Promise<string>;
	readonly authorizeTarget: (context: {
		readonly input: Readonly<NormalizedGameInput>;
		readonly partition: Readonly<DelegationPartition>;
		readonly target: Readonly<DelegationTarget>;
	}) => boolean;
}

export interface DelegationToolContext {
	readonly input: Readonly<NormalizedGameInput>;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly signal: AbortSignal;
	readonly partition: DelegationPartition;
	readonly projectContext?: (
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
	) => JsonValue | Promise<JsonValue>;
}

interface ActiveRun {
	readonly delivery: DelegationDelivery;
	handle?: GameRunHandle;
	readonly input: DelegatedInput;
}
interface DelegatedInput {
	readonly sessionId: string;
	readonly actorId: string;
	readonly inputId: string;
	readonly payload: DelegatedTaskPayload;
}

const ReadSchema = Type.Object({ delegationId: Type.String() }, { additionalProperties: false });
const CancelSchema = Type.Object(
	{ delegationId: Type.String(), expectedRevision: Type.String() },
	{ additionalProperties: false },
);
const SteerSchema = Type.Object({ delegationId: Type.String(), text: Type.String() }, { additionalProperties: false });

export class DelegationManager {
	readonly service: DelegationService;
	private readonly runtime: GameRuntime;
	private readonly targets: ReadonlyMap<string, DelegationTarget>;
	private readonly active = new Map<string, ActiveRun>();
	private readonly mutex = new Mutex();
	private starting = 0;
	private constructor(
		private readonly options: DelegationManagerOptions,
		targets: ReadonlyMap<string, DelegationTarget>,
	) {
		this.service = options.service;
		this.runtime = options.runtime;
		this.targets = targets;
	}

	static async create(options: DelegationManagerOptions): Promise<DelegationManager> {
		const limits = options.service.limits;
		identifier(options.workerId, limits, "worker_id");
		if (options.targets.length > limits.maxTargets) throw new DelegationCapacityError();
		if (options.runtime.limits.maxActiveActors <= limits.maxDepth) {
			throw new DelegationError("runtime_actor_capacity_below_delegation_depth");
		}
		const targets = new Map<string, DelegationTarget>();
		if (options.targets.length === 0) throw new DelegationError("delegation_targets_required");
		let manager: DelegationManager | undefined;
		const actors: RuntimeActorOptions[] = [];
		for (const registration of options.targets) {
			const target = normalizeTarget(registration, options.service);
			if (targets.has(target.delegateId)) throw new DelegationError("duplicate_delegate_id");
			targets.set(target.delegateId, target);
			const existingGuard = registration.actor.beforeToolExecution;
			const actor: RuntimeActorOptions = {
				...registration.actor,
				sessionId: target.sessionId,
				actorId: target.actorId,
				shouldStopAfterTurn: (_input, coordinate) => BigInt(coordinate.turn) >= BigInt(target.maxTurns),
				beforeToolExecution: async (input, execution, signal) => {
					if (!manager) throw new DelegationLeaseError();
					await manager.authorizeToolExecution(input, execution, signal);
					await existingGuard?.(input, execution, signal);
				},
			};
			actors.push(actor);
		}
		if (byteLength(delegateDescription([...targets.keys()])) > limits.maxToolDescriptionBytes) {
			throw new DelegationError("delegation_tool_description_too_large");
		}
		await options.runtime.registerActors(actors);
		manager = new DelegationManager(options, targets);
		return manager;
	}

	createTools(context: DelegationToolContext): readonly AgentTool[] {
		const part = partition(context.partition, this.service.limits);
		const authorized = new Map(
			[...this.targets].filter(([, target]) =>
				this.options.authorizeTarget({ input: context.input, partition: part, target }),
			),
		);
		if (authorized.size === 0) return [];
		return [
			this.delegateTool(context, part, authorized),
			this.readTool(context, part),
			this.cancelTool(context, part),
			this.controlTool(context, part, "steer"),
			this.controlTool(context, part, "follow-up"),
		];
	}

	async run(key: DelegationKey, signal: AbortSignal): Promise<DelegationRecord> {
		signal.throwIfAborted();
		await this.mutex.runExclusive(() => {
			if (this.active.size + this.starting >= this.service.limits.maxActiveRuns) {
				throw new DelegationCapacityError();
			}
			this.starting += 1;
		});
		let reserved = true;
		let delivery: DelegationDelivery | undefined;
		let input: DelegatedInput | undefined;
		let handle: GameRunHandle | undefined;
		try {
			const now = timestamp(await this.options.operationalNow());
			delivery = await this.service.claim({
				...key,
				leaseOwnerId: this.options.workerId,
				operationalNow: now,
				leaseExpiresAt: timestamp(await this.options.leaseExpiresAt(now)),
			});
			const registeredTarget = this.targets.get(delivery.record.target.delegateId);
			if (!registeredTarget || !sameTarget(registeredTarget, delivery.record.target)) {
				return await this.service.fail(
					sequence(delivery.record),
					delivery.leaseToken,
					"target_configuration_changed",
					timestamp(await this.options.operationalNow()),
				);
			}
			input = toInput(delivery.record);
			const active: ActiveRun = { delivery, input };
			await this.mutex.runExclusive(() => {
				this.starting -= 1;
				reserved = false;
				this.active.set(activeKey(key), active);
			});
			await this.service.assertLease(
				sequence(delivery.record),
				delivery.leaseToken,
				timestamp(await this.options.operationalNow()),
			);
			const pending = this.runtime.readDeferred(registeredTarget);
			if (pending && pending.input.inputId !== input.inputId) {
				return await this.service.abandon(
					sequence(delivery.record),
					delivery.leaseToken,
					timestamp(await this.options.operationalNow()),
				);
			}
			const submitted = pending
				? await this.runtime.resumeDeferred(registeredTarget, pending.operationId)
				: await this.runtime.submit(input);
			handle = submitted;
			await this.mutex.runExclusive(() => {
				if (this.active.get(activeKey(key)) !== active) throw new DelegationLeaseError();
				active.handle = submitted;
			});
			await this.service.bindRun(
				sequence(delivery.record),
				delivery.leaseToken,
				submitted.runId,
				timestamp(await this.options.operationalNow()),
			);
			const result = await waitForAbort(submitted.completion, signal);
			if (result.status === "deferred") {
				return await this.service.abandon(
					sequence(delivery.record),
					delivery.leaseToken,
					timestamp(await this.options.operationalNow()),
				);
			}
			if (result.status !== "completed")
				return await this.service.fail(
					sequence(delivery.record),
					delivery.leaseToken,
					result.status === "aborted" ? "child_aborted" : "child_failed",
					timestamp(await this.options.operationalNow()),
				);
			let output: JsonValue;
			try {
				if (result.runId !== submitted.runId || typeof result.finalOutput !== "string") {
					throw new TypeError("The completed child run has no exact final output.");
				}
				const childFinalOutput = json(result.finalOutput, this.service.limits.maxResultBytes, this.service.limits);
				if (typeof childFinalOutput !== "string" || childFinalOutput.trim().length === 0) {
					throw new TypeError("The completed child run has no exact final output.");
				}
				output = await this.options.outcome({
					record: delivery.record,
					childCoordinate: { ...submitted.initialCoordinate },
					childFinalOutput,
					signal,
				});
			} catch (error) {
				if (signal.aborted) throw signal.reason ?? error;
				return await this.service.fail(
					sequence(delivery.record),
					delivery.leaseToken,
					"result_unavailable",
					timestamp(await this.options.operationalNow()),
				);
			}
			return await this.service.complete(
				sequence(delivery.record),
				delivery.leaseToken,
				json(output, this.service.limits.maxResultBytes, this.service.limits),
				timestamp(await this.options.operationalNow()),
			);
		} catch (error) {
			if (signal.aborted) {
				try {
					const current = await this.service.require(key);
					if (current.status === "pending" || current.status === "running") {
						await this.cancel(key, current.revision);
					}
				} catch {}
				throw signal.reason ?? error;
			}
			if (error instanceof DelegationLeaseError) throw error;
			if (handle === undefined && delivery !== undefined) {
				try {
					await this.service.abandon(
						sequence(delivery.record),
						delivery.leaseToken,
						timestamp(await this.options.operationalNow()),
					);
				} catch {}
			} else if (delivery !== undefined) {
				try {
					return await this.service.fail(
						sequence(delivery.record),
						delivery.leaseToken,
						"executor_failed",
						timestamp(await this.options.operationalNow()),
					);
				} catch {}
			}
			throw error;
		} finally {
			await this.mutex.runExclusive(() => {
				if (reserved) this.starting -= 1;
				this.active.delete(activeKey(key));
			});
		}
	}

	readonly authorizeToolExecution = async (
		input: Readonly<NormalizedGameInput>,
		_execution: unknown,
		signal: AbortSignal,
	): Promise<void> => {
		signal.throwIfAborted();
		const active = await this.mutex.runExclusive(() =>
			[...this.active.values()].find((candidate) => sameInput(candidate.input, input)),
		);
		if (!active) throw new DelegationLeaseError();
		await this.service.assertLease(
			sequence(active.delivery.record),
			active.delivery.leaseToken,
			timestamp(await this.options.operationalNow()),
		);
	};

	async cancel(key: DelegationKey, revision: string): Promise<DelegationRecord> {
		const record = await this.service.cancel(key, revision, timestamp(await this.options.operationalNow()));
		const active = await this.mutex.runExclusive(() => this.active.get(activeKey(key)));
		if (active?.handle) {
			const coordinate =
				this.runtime.readActiveCoordinate({ sessionId: active.input.sessionId, actorId: active.input.actorId }) ??
				active.handle.initialCoordinate;
			await this.runtime.control(coordinate, { type: "abort" });
		}
		return record;
	}

	async control(key: DelegationKey, type: "steer" | "follow-up", text: string): Promise<boolean> {
		if (typeof text !== "string" || text.length === 0 || byteLength(text) > this.service.limits.maxControlTextBytes) {
			throw new DelegationError("invalid_control_text");
		}
		const active = await this.mutex.runExclusive(() => this.active.get(activeKey(key)));
		if (!active?.handle) return false;
		const coordinate = this.runtime.readActiveCoordinate({
			sessionId: active.input.sessionId,
			actorId: active.input.actorId,
		});
		if (!coordinate) return false;
		return (await this.runtime.control(coordinate, { type, text })).accepted;
	}

	async renew(key: DelegationKey, leaseExpiresAt: string): Promise<DelegationDelivery> {
		const active = await this.mutex.runExclusive(() => this.active.get(activeKey(key)));
		if (!active) throw new DelegationLeaseError();
		return await this.service.renew(
			sequence(active.delivery.record),
			active.delivery.leaseToken,
			timestamp(await this.options.operationalNow()),
			leaseExpiresAt,
		);
	}

	async recover(part: DelegationPartition, signal: AbortSignal): Promise<readonly DelegationRecord[]> {
		const page = await this.service.list({
			...partition(part, this.service.limits),
			statuses: ["pending", "running"],
			limit: this.service.limits.maxRecoveryBatchSize,
		});
		const settled: DelegationRecord[] = [];
		for (const record of page.records) {
			signal.throwIfAborted();
			try {
				settled.push(await this.run({ ...record.partition, delegationId: record.delegationId }, signal));
			} catch (error) {
				if (!(error instanceof DelegationLeaseError)) throw error;
			}
		}
		return settled;
	}

	private delegateTool(
		context: DelegationToolContext,
		part: DelegationPartition,
		authorized: ReadonlyMap<string, DelegationTarget>,
	): AgentTool {
		const targetIds = [...authorized.keys()];
		const schema = Type.Object(
			{
				delegateId: Type.Union(targetIds.map((id) => Type.Literal(id))),
				mode: Type.Union([Type.Literal("foreground"), Type.Literal("background")]),
				task: Type.Unknown(),
			},
			{ additionalProperties: false },
		);
		const description = delegateDescription(targetIds);
		if (byteLength(description) > this.service.limits.maxToolDescriptionBytes) {
			throw new DelegationError("delegation_tool_description_too_large");
		}
		const tool: AgentTool<typeof schema> = {
			name: "delegate_game_task",
			label: "Delegate game task",
			description,
			parameters: schema,
			executionMode: "sequential",
			execute: async (toolCallId, parameters, executionSignal) => {
				const signal = executionSignal ?? context.signal;
				signal.throwIfAborted();
				const target = authorized.get(parameters.delegateId);
				if (!target) throw new DelegationError("delegate_not_registered");
				if (
					parameters.mode === "foreground" &&
					target.sessionId === context.input.sessionId &&
					target.actorId === context.input.actorId
				) {
					throw new DelegationError("foreground_self_delegation_deadlock");
				}
				const id = digest({ input: context.input.inputId, coordinate: context.coordinate, toolCallId });
				const lineage = readLineage(context.input.payload);
				const depth = (lineage?.depth ?? 0n) + 1n;
				if (depth > BigInt(this.service.limits.maxDepth)) throw new DelegationCapacityError();
				const projected = context.projectContext ? await context.projectContext(context.input, signal) : undefined;
				const draft: DelegationDraft = {
					delegationId: id,
					partition: part,
					parent: {
						sessionId: context.input.sessionId,
						actorId: context.input.actorId,
						inputId: context.input.inputId,
						runId: context.coordinate.runId,
						turn: decimal(context.coordinate.turn, this.service.limits, "turn"),
						toolCallId: identifier(toolCallId, this.service.limits, "tool_call_id"),
					},
					target,
					mode: parameters.mode as DelegationMode,
					task: json(parameters.task, this.service.limits.maxTaskBytes, this.service.limits),
					...(projected === undefined
						? {}
						: { context: json(projected, this.service.limits.maxContextBytes, this.service.limits) }),
					rootDelegationId: lineage?.rootDelegationId ?? id,
					...(lineage === undefined ? {} : { parentDelegationId: lineage.delegationId }),
					depth: depth.toString(),
					childInputId: digest({ partition: part, delegationId: id }),
					createdAt: timestamp(await this.options.operationalNow()),
				};
				const created = await this.service.create(draft);
				const key = { ...part, delegationId: created.delegationId };
				if (created.mode === "background") {
					void this.run(key, new AbortController().signal).catch(() => undefined);
					return this.modelResult(created);
				}
				return this.modelResult(await this.run(key, signal));
			},
		};
		return tool;
	}
	private readTool(context: DelegationToolContext, part: DelegationPartition): AgentTool<typeof ReadSchema> {
		return {
			name: "read_delegated_task",
			label: "Read delegated task",
			description: "Read one delegated task in the current game timeline.",
			parameters: ReadSchema,
			executionMode: "parallel",
			execute: async (_id, p) => this.modelResult(await this.requireAuthorizedRecord(context, part, p.delegationId)),
		};
	}
	private cancelTool(context: DelegationToolContext, part: DelegationPartition): AgentTool<typeof CancelSchema> {
		return {
			name: "cancel_delegated_task",
			label: "Cancel delegated task",
			description: "Cancel one nonterminal delegated task using its exact revision.",
			parameters: CancelSchema,
			executionMode: "sequential",
			execute: async (_id, p) => {
				await this.requireAuthorizedRecord(context, part, p.delegationId);
				return this.modelResult(await this.cancel({ ...part, delegationId: p.delegationId }, p.expectedRevision));
			},
		};
	}
	private controlTool(
		context: DelegationToolContext,
		part: DelegationPartition,
		type: "steer" | "follow-up",
	): AgentTool<typeof SteerSchema> {
		return {
			name: type === "steer" ? "steer_delegated_task" : "follow_up_delegated_task",
			label: type === "steer" ? "Steer delegated task" : "Follow up delegated task",
			description:
				type === "steer"
					? "Steer an active local delegated task. Durable but inactive tasks are not retargeted."
					: "Queue a follow-up for an active local delegated task after its current work drains.",
			parameters: SteerSchema,
			executionMode: "sequential",
			execute: async (_id, p) => {
				await this.requireAuthorizedRecord(context, part, p.delegationId);
				return {
					content: [
						{
							type: "text",
							text: JSON.stringify({
								accepted: await this.control({ ...part, delegationId: p.delegationId }, type, p.text),
							}),
						},
					],
					details: {},
				};
			},
		};
	}

	private async requireAuthorizedRecord(
		context: DelegationToolContext,
		part: DelegationPartition,
		delegationId: string,
	): Promise<DelegationRecord> {
		let record: DelegationRecord;
		try {
			record = await this.service.require({ ...part, delegationId });
		} catch (error) {
			if (error instanceof DelegationNotFoundError) throw new DelegationError("delegation_not_authorized");
			throw error;
		}
		const target = this.targets.get(record.target.delegateId);
		if (
			!target ||
			!sameTarget(target, record.target) ||
			!this.options.authorizeTarget({ input: context.input, partition: part, target })
		) {
			throw new DelegationError("delegation_not_authorized");
		}
		return record;
	}

	private modelResult(
		record: DelegationRecord,
	): AgentToolResult<{ delegationId: string; status: string; revision: string }> {
		const value = {
			delegationId: record.delegationId,
			status: record.status,
			revision: record.revision,
			...(record.result === undefined ? {} : { result: record.result }),
			...(record.failureCode === undefined ? {} : { failureCode: record.failureCode }),
		};
		const text = JSON.stringify(value);
		if (byteLength(text) > this.service.limits.maxModelResultBytes) {
			const bounded = JSON.stringify({
				delegationId: record.delegationId,
				status: record.status,
				revision: record.revision,
				resultAvailableToHost: record.result !== undefined,
			});
			if (byteLength(bounded) > this.service.limits.maxModelResultBytes) {
				throw new DelegationError("delegation_model_result_budget_too_small");
			}
			return {
				content: [{ type: "text", text: bounded }],
				details: { delegationId: record.delegationId, status: record.status, revision: record.revision },
			};
		}
		return {
			content: [{ type: "text", text }],
			details: { delegationId: record.delegationId, status: record.status, revision: record.revision },
		};
	}
}

function normalizeTarget(input: DelegationTargetRegistration, service: DelegationService): DelegationTarget {
	const l = service.limits;
	if (!Number.isSafeInteger(input.maxTurns) || input.maxTurns < 1) throw new DelegationError("invalid_max_turns");
	return {
		delegateId: identifier(input.delegateId, l, "delegate_id"),
		sessionId: identifier(input.sessionId, l, "session_id"),
		actorId: identifier(input.actorId, l, "actor_id"),
		maxTurns: input.maxTurns,
	};
}

function delegateDescription(targetIds: readonly string[]): string {
	return `Delegate one bounded task to a fixed host-registered game agent. Choose foreground to wait or background to continue. Delegates: ${targetIds.join(", ")}`;
}
function toInput(record: DelegationRecord): DelegatedInput {
	return {
		sessionId: record.target.sessionId,
		actorId: record.target.actorId,
		inputId: record.childInputId,
		payload: {
			type: "delegation.task",
			delegation: {
				delegationId: record.delegationId,
				rootDelegationId: record.rootDelegationId,
				...(record.parentDelegationId === undefined ? {} : { parentDelegationId: record.parentDelegationId }),
				depth: record.depth,
				parent: {
					sessionId: record.parent.sessionId,
					actorId: record.parent.actorId,
					inputId: record.parent.inputId,
					runId: record.parent.runId,
					turn: record.parent.turn,
				},
			},
			task: normalizeJsonValue(record.task),
			...(record.context === undefined ? {} : { context: normalizeJsonValue(record.context) }),
		},
	};
}
function readLineage(value: JsonValue): { delegationId: string; rootDelegationId: string; depth: bigint } | undefined {
	if (
		!value ||
		typeof value !== "object" ||
		Array.isArray(value) ||
		value.type !== "delegation.task" ||
		!value.delegation ||
		typeof value.delegation !== "object" ||
		Array.isArray(value.delegation)
	)
		return undefined;
	const d = value.delegation as Record<string, JsonValue>;
	if (
		typeof d.delegationId !== "string" ||
		typeof d.rootDelegationId !== "string" ||
		typeof d.depth !== "string" ||
		!/^(?:0|[1-9][0-9]*)$/u.test(d.depth)
	)
		return undefined;
	return { delegationId: d.delegationId, rootDelegationId: d.rootDelegationId, depth: BigInt(d.depth) };
}
function sequence(record: DelegationRecord): DelegationSequenceKey {
	return { ...record.partition, delegationKey: record.delegationKey };
}
function activeKey(key: DelegationKey): string {
	return JSON.stringify([key.ownerId, key.timelineId, key.generation, key.delegationId]);
}
function sameInput(a: DelegatedInput, b: Readonly<NormalizedGameInput>): boolean {
	return a.sessionId === b.sessionId && a.actorId === b.actorId && a.inputId === b.inputId;
}

function sameTarget(left: DelegationTarget, right: DelegationTarget): boolean {
	return (
		left.delegateId === right.delegateId &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId &&
		left.maxTurns === right.maxTurns
	);
}
function digest(value: unknown): string {
	return createHash("sha256").update(canonicalizeJsonValue(value)).digest("hex");
}
function waitForAbort<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
	if (signal.aborted) return Promise.reject(signal.reason);
	return new Promise((resolve, reject) => {
		const abort = () => {
			cleanup();
			reject(signal.reason);
		};
		const cleanup = () => signal.removeEventListener("abort", abort);
		signal.addEventListener("abort", abort, { once: true });
		promise.then(
			(v) => {
				cleanup();
				resolve(v);
			},
			(e) => {
				cleanup();
				reject(e);
			},
		);
	});
}
