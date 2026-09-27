import { type RuntimeHealthSnapshot, RuntimeHealthSnapshotSchema } from "@opengameagent/protocol";
import { Value } from "typebox/value";
import {
	type CapturedProviderProfileConfiguration,
	captureProviderProfileConfiguration,
	type ProviderProfileConfiguration,
	type ProviderProfileSnapshot,
} from "./provider-profiles.js";

export interface ProviderProfileProbeResult {
	readonly acceptedForActivation: boolean;
	readonly health: RuntimeHealthSnapshot;
}

export interface ProviderProfileProbe {
	inspect(
		configuration: CapturedProviderProfileConfiguration,
		signal: AbortSignal,
	): ProviderProfileProbeResult | Promise<ProviderProfileProbeResult>;
}

export interface ProviderProfileLifecycleOptions {
	readonly probe: ProviderProfileProbe;
}

export interface ProviderProfileLifecycleEntrySnapshot {
	readonly revision: string;
	readonly profile: ProviderProfileSnapshot;
}

export interface ProviderProfileLifecycleSnapshot {
	readonly draft?: ProviderProfileLifecycleEntrySnapshot;
	readonly active?: ProviderProfileLifecycleEntrySnapshot;
}

type ProviderProfileLifecycleSubject = "draft" | "active";

export type ProviderProfileLifecycleCommand =
	| Readonly<{ type: "draft"; configuration: Readonly<ProviderProfileConfiguration> }>
	| Readonly<{
			type: "update";
			expectedDraftRevision: string;
			configuration: Readonly<ProviderProfileConfiguration>;
	  }>
	| Readonly<{ type: "validate"; expectedDraftRevision: string }>
	| Readonly<{ type: "test"; expectedDraftRevision: string }>
	| Readonly<{
			type: "activate";
			expectedDraftRevision: string;
			expectedActiveRevision?: string;
	  }>
	| Readonly<{ type: "delete"; expectedDraftRevision: string }>
	| Readonly<{ type: "revoke"; expectedActiveRevision: string }>
	| Readonly<{ type: "health"; expectedActiveRevision: string }>;

export type ProviderProfileLifecycleResult =
	| Readonly<{
			type: "drafted" | "updated" | "activated" | "deleted" | "revoked";
			snapshot: ProviderProfileLifecycleSnapshot;
	  }>
	| Readonly<{
			type: "validated";
			revision: string;
			profile: ProviderProfileSnapshot;
	  }>
	| Readonly<{
			type: "tested";
			revision: string;
			acceptedForActivation: boolean;
			health: RuntimeHealthSnapshot;
	  }>
	| Readonly<{
			type: "health";
			revision: string;
			health: RuntimeHealthSnapshot;
	  }>;

export type ProviderProfileLifecycleErrorCode =
	| "active_missing"
	| "draft_missing"
	| "conflict"
	| "validation_failed"
	| "test_rejected"
	| "inspection_failed"
	| "invalid_command";

export class ProviderProfileLifecycleError extends Error {
	constructor(public readonly code: ProviderProfileLifecycleErrorCode) {
		super(code);
		this.name = "ProviderProfileLifecycleError";
	}
}

interface LifecycleEntry {
	readonly revision: string;
	readonly configuration: CapturedProviderProfileConfiguration;
}

interface LifecycleState {
	readonly draft?: LifecycleEntry;
	readonly active?: LifecycleEntry;
}

export class ProviderProfileLifecycle {
	readonly #inspectProfile: ProviderProfileProbe["inspect"];
	#state: LifecycleState = Object.freeze({});
	#nextRevision = 1n;

	constructor(options: ProviderProfileLifecycleOptions) {
		const probe = options?.probe;
		const inspect = probe?.inspect;
		if (typeof inspect !== "function") {
			throw new TypeError("Provider profile probe is required.");
		}
		this.#inspectProfile = (configuration, signal) => inspect.call(probe, configuration, signal);
	}

	/** Returns the opaque active capture used by the next Pi request. */
	configuration(): CapturedProviderProfileConfiguration {
		const active = this.#state.active;
		if (!active) throw new ProviderProfileLifecycleError("active_missing");
		return active.configuration;
	}

	snapshot(): ProviderProfileLifecycleSnapshot {
		return snapshotState(this.#state);
	}

	async execute(
		command: ProviderProfileLifecycleCommand,
		signal: AbortSignal = new AbortController().signal,
	): Promise<ProviderProfileLifecycleResult> {
		signal.throwIfAborted();
		switch (command.type) {
			case "draft":
				return this.#draft(command.configuration);
			case "update":
				return this.#update(command.expectedDraftRevision, command.configuration);
			case "validate":
				return this.#validate(command.expectedDraftRevision);
			case "test":
				return this.#test(command.expectedDraftRevision, signal);
			case "activate":
				return this.#activate(command.expectedDraftRevision, command.expectedActiveRevision, signal);
			case "delete":
				return this.#delete(command.expectedDraftRevision);
			case "revoke":
				return this.#revoke(command.expectedActiveRevision);
			case "health":
				return this.#health(command.expectedActiveRevision, signal);
			default:
				throw new ProviderProfileLifecycleError("invalid_command");
		}
	}

	#draft(configuration: Readonly<ProviderProfileConfiguration>): ProviderProfileLifecycleResult {
		if (this.#state.draft) throw new ProviderProfileLifecycleError("conflict");
		const captured = captureConfiguration(configuration);
		if (this.#state.draft) throw new ProviderProfileLifecycleError("conflict");
		this.#state = Object.freeze({ ...this.#state, draft: this.#entry(captured) });
		return mutationResult("drafted", this.#state);
	}

	#update(
		expectedRevision: string,
		configuration: Readonly<ProviderProfileConfiguration>,
	): ProviderProfileLifecycleResult {
		const draft = requireEntry(this.#state, "draft", expectedRevision);
		const captured = captureConfiguration(configuration);
		if (this.#state.draft !== draft) throw new ProviderProfileLifecycleError("conflict");
		this.#state = Object.freeze({ ...this.#state, draft: this.#entry(captured) });
		return mutationResult("updated", this.#state);
	}

	#validate(expectedRevision: string): ProviderProfileLifecycleResult {
		const draft = requireEntry(this.#state, "draft", expectedRevision);
		return Object.freeze({
			type: "validated",
			revision: draft.revision,
			profile: draft.configuration.snapshot,
		});
	}

	async #test(expectedRevision: string, signal: AbortSignal): Promise<ProviderProfileLifecycleResult> {
		const entry = requireEntry(this.#state, "draft", expectedRevision);
		const result = await this.#inspect(entry.configuration, signal);
		// Observation results describe the captured revision even if a writer changed the slot while the probe ran.
		return Object.freeze({
			type: "tested",
			revision: entry.revision,
			acceptedForActivation: result.acceptedForActivation === true,
			health: result.health,
		});
	}

	async #activate(
		expectedDraftRevision: string,
		expectedActiveRevision: string | undefined,
		signal: AbortSignal,
	): Promise<ProviderProfileLifecycleResult> {
		const draft = requireEntry(this.#state, "draft", expectedDraftRevision);
		const active = this.#state.active;
		if (active?.revision !== expectedActiveRevision) throw new ProviderProfileLifecycleError("conflict");
		const result = await this.#inspect(draft.configuration, signal);
		if (this.#state.draft !== draft || this.#state.active !== active) {
			throw new ProviderProfileLifecycleError("conflict");
		}
		if (result.acceptedForActivation !== true) throw new ProviderProfileLifecycleError("test_rejected");
		this.#state = Object.freeze({ active: this.#entry(draft.configuration) });
		return mutationResult("activated", this.#state);
	}

	#delete(expectedRevision: string): ProviderProfileLifecycleResult {
		requireEntry(this.#state, "draft", expectedRevision);
		const { draft: _draft, ...remaining } = this.#state;
		this.#state = Object.freeze(remaining);
		return mutationResult("deleted", this.#state);
	}

	#revoke(expectedRevision: string): ProviderProfileLifecycleResult {
		requireEntry(this.#state, "active", expectedRevision);
		const { active: _active, ...remaining } = this.#state;
		this.#state = Object.freeze(remaining);
		return mutationResult("revoked", this.#state);
	}

	async #health(expectedRevision: string, signal: AbortSignal): Promise<ProviderProfileLifecycleResult> {
		const entry = requireEntry(this.#state, "active", expectedRevision);
		const result = await this.#inspect(entry.configuration, signal);
		// Health is likewise revision-bound and never persisted as lifecycle state.
		return Object.freeze({
			type: "health",
			revision: entry.revision,
			health: result.health,
		});
	}

	async #inspect(
		configuration: CapturedProviderProfileConfiguration,
		signal: AbortSignal,
	): Promise<ProviderProfileProbeResult> {
		signal.throwIfAborted();
		try {
			const result = await this.#inspectProfile(configuration, signal);
			signal.throwIfAborted();
			if (typeof result !== "object" || result === null) {
				throw new ProviderProfileLifecycleError("inspection_failed");
			}
			const acceptedForActivation = result.acceptedForActivation;
			if (typeof acceptedForActivation !== "boolean") {
				throw new ProviderProfileLifecycleError("inspection_failed");
			}
			const health = captureHealthSnapshot(result.health);
			return Object.freeze({ acceptedForActivation, health });
		} catch {
			if (signal.aborted) throw signal.reason;
			throw new ProviderProfileLifecycleError("inspection_failed");
		}
	}

	#entry(configuration: CapturedProviderProfileConfiguration): LifecycleEntry {
		const revision = this.#nextRevision.toString();
		this.#nextRevision += 1n;
		return Object.freeze({ revision, configuration });
	}
}

function captureHealthSnapshot(value: unknown): RuntimeHealthSnapshot {
	const captured = structuredClone(value);
	if (!Value.Check(RuntimeHealthSnapshotSchema, captured)) {
		throw new ProviderProfileLifecycleError("inspection_failed");
	}
	for (const dependency of captured.dependencies) Object.freeze(dependency);
	Object.freeze(captured.dependencies);
	return Object.freeze(captured);
}

function captureConfiguration(
	configuration: Readonly<ProviderProfileConfiguration>,
): CapturedProviderProfileConfiguration {
	try {
		return captureProviderProfileConfiguration(configuration);
	} catch {
		throw new ProviderProfileLifecycleError("validation_failed");
	}
}

function requireEntry(
	state: LifecycleState,
	subject: ProviderProfileLifecycleSubject,
	expectedRevision: string,
): LifecycleEntry {
	const entry = state[subject];
	if (!entry) throw new ProviderProfileLifecycleError(subject === "draft" ? "draft_missing" : "active_missing");
	if (entry.revision !== expectedRevision) throw new ProviderProfileLifecycleError("conflict");
	return entry;
}

function snapshotState(state: LifecycleState): ProviderProfileLifecycleSnapshot {
	return Object.freeze({
		...(state.draft ? { draft: snapshotEntry(state.draft) } : {}),
		...(state.active ? { active: snapshotEntry(state.active) } : {}),
	});
}

function snapshotEntry(entry: LifecycleEntry): ProviderProfileLifecycleEntrySnapshot {
	return Object.freeze({ revision: entry.revision, profile: entry.configuration.snapshot });
}

function mutationResult(
	type: Extract<ProviderProfileLifecycleResult, { snapshot: unknown }>["type"],
	state: LifecycleState,
): ProviderProfileLifecycleResult {
	return Object.freeze({ type, snapshot: snapshotState(state) });
}
