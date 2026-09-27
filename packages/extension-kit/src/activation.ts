import type { GameContextSource, GameRequestSource, GameToolSource, NormalizedGameInput } from "@opengameagent/agent";
import { assertOpaqueIdentifier, type RunCoordinate } from "@opengameagent/protocol";
import type { RuntimeActorOptions } from "@opengameagent/runtime";
import {
	diagnoseGameExtensionSet,
	type GameExtensionSetLimits,
	type GameExtensionSetReport,
} from "./dependency-diagnostics.js";
import type { GameExtensionManifest, GameExtensionPermission } from "./manifest.js";

export interface GameExtensionDescriptor {
	readonly id: string;
	readonly version: string;
}

export type GameExtensionCleanup = () => void | Promise<void>;

export interface GameExtensionRegistrationApi {
	registerContextSource(source: GameContextSource): void;
	registerRequestSource(source: GameRequestSource): void;
	registerToolSource(resourceId: string, source: GameToolSource): void;
}

export interface GameExtensionModule {
	readonly descriptor: GameExtensionDescriptor;
	configure(api: GameExtensionRegistrationApi): GameExtensionCleanup | undefined;
}

export interface GameExtensionEntry {
	readonly manifest: GameExtensionManifest;
	readonly module: GameExtensionModule;
}

export interface GameExtensionActivationLimits {
	readonly maxResourcesPerExtension: number;
	readonly maxResourceIdBytes: number;
}

export interface ActivateGameExtensionsOptions {
	readonly entries: readonly GameExtensionEntry[];
	readonly allowedPermissions: readonly GameExtensionPermission[];
	readonly setLimits: GameExtensionSetLimits;
	readonly activationLimits: GameExtensionActivationLimits;
	readonly signal?: AbortSignal;
}

export interface GameExtensionResource {
	readonly extensionId: string;
	readonly resourceId: string;
	readonly permission: GameExtensionPermission;
}

export type GameExtensionActivationErrorCode =
	| "extension_set_invalid"
	| "descriptor_mismatch"
	| "permission_undeclared"
	| "resource_invalid"
	| "resource_conflict"
	| "resource_limit_exceeded"
	| "registration_closed"
	| "configure_failed"
	| "extension_inactive"
	| "dispose_failed";

export class GameExtensionActivationError extends Error {
	constructor(
		public readonly code: GameExtensionActivationErrorCode,
		public readonly extensionId?: string,
		public readonly setReport?: GameExtensionSetReport,
	) {
		super(code);
		this.name = "GameExtensionActivationError";
	}
}

interface CapturedToolSource {
	readonly resourceId: string;
	readonly load: GameToolSource;
}

export interface ActivatedGameExtensionSet {
	readonly order: readonly string[];
	readonly resources: readonly GameExtensionResource[];
	composeActorOptions(base: RuntimeActorOptions): RuntimeActorOptions;
	dispose(): Promise<void>;
}

const encoder = new TextEncoder();

export async function activateGameExtensions(
	options: ActivateGameExtensionsOptions,
): Promise<ActivatedGameExtensionSet> {
	if (!options || !Array.isArray(options.entries)) throw new TypeError("Extension activation options are required.");
	const limits = captureActivationLimits(options.activationLimits);
	const signal = options.signal ?? new AbortController().signal;
	signal.throwIfAborted();
	const setReport = diagnoseGameExtensionSet({
		manifests: options.entries.map((entry) => entry.manifest),
		allowedPermissions: options.allowedPermissions,
		limits: options.setLimits,
	});
	if (!setReport.passed) throw new GameExtensionActivationError("extension_set_invalid", undefined, setReport);

	const entries = new Map<string, GameExtensionEntry>();
	for (const entry of options.entries) {
		if (!entry || typeof entry !== "object" || !entry.module || typeof entry.module !== "object") {
			throw new TypeError("Extension entries are invalid.");
		}
		entries.set(entry.manifest.id, entry);
	}
	const contextSources: GameContextSource[] = [];
	const requestSources: GameRequestSource[] = [];
	const toolSources: CapturedToolSource[] = [];
	const resources: GameExtensionResource[] = [];
	const resourceIds = new Set<string>();
	const cleanups: Array<{ readonly extensionId: string; readonly cleanup: GameExtensionCleanup }> = [];
	let active = true;

	try {
		for (const extensionId of setReport.order) {
			signal.throwIfAborted();
			const entry = entries.get(extensionId);
			if (!entry) throw new GameExtensionActivationError("configure_failed", extensionId);
			const descriptor = entry.module.descriptor;
			if (
				!descriptor ||
				typeof descriptor !== "object" ||
				descriptor.id !== entry.manifest.id ||
				descriptor.version !== entry.manifest.version
			) {
				throw new GameExtensionActivationError("descriptor_mismatch", extensionId);
			}
			if (typeof entry.module.configure !== "function") {
				throw new GameExtensionActivationError("configure_failed", extensionId);
			}

			let registrationOpen = true;
			let resourceCount = 0;
			const stagedContextSources: GameContextSource[] = [];
			const stagedRequestSources: GameRequestSource[] = [];
			const stagedToolSources: CapturedToolSource[] = [];
			const stagedResources: GameExtensionResource[] = [];
			const register = (permission: GameExtensionPermission, resourceId: string) => {
				if (!registrationOpen) throw new GameExtensionActivationError("registration_closed", extensionId);
				if (!entry.manifest.permissions.includes(permission)) {
					throw new GameExtensionActivationError("permission_undeclared", extensionId);
				}
				assertResourceId(resourceId, limits, extensionId);
				if (resourceIds.has(resourceId)) throw new GameExtensionActivationError("resource_conflict", extensionId);
				if (resourceCount >= limits.maxResourcesPerExtension) {
					throw new GameExtensionActivationError("resource_limit_exceeded", extensionId);
				}
				resourceCount += 1;
				resourceIds.add(resourceId);
				stagedResources.push(Object.freeze({ extensionId, resourceId, permission }));
			};
			const api: GameExtensionRegistrationApi = Object.freeze({
				registerContextSource(source: GameContextSource) {
					const captured = captureContextSource(source, extensionId, () => active);
					register("context.contribute", captured.sourceId);
					stagedContextSources.push(captured);
				},
				registerRequestSource(source: GameRequestSource) {
					const captured = captureRequestSource(source, extensionId, () => active);
					register("request.contribute", captured.sourceId);
					stagedRequestSources.push(captured);
				},
				registerToolSource(resourceId: string, source: GameToolSource) {
					if (typeof source !== "function") throw new GameExtensionActivationError("resource_invalid", extensionId);
					register("tools.contribute", resourceId);
					stagedToolSources.push({ resourceId, load: captureToolSource(source, extensionId, () => active) });
				},
			});

			let cleanup: GameExtensionCleanup | undefined;
			try {
				cleanup = entry.module.configure(api);
			} catch (error) {
				if (error instanceof GameExtensionActivationError) throw error;
				throw new GameExtensionActivationError("configure_failed", extensionId);
			} finally {
				registrationOpen = false;
			}
			if (cleanup !== undefined && typeof cleanup !== "function") {
				throw new GameExtensionActivationError("configure_failed", extensionId);
			}
			contextSources.push(...stagedContextSources);
			requestSources.push(...stagedRequestSources);
			toolSources.push(...stagedToolSources);
			resources.push(...stagedResources);
			if (cleanup) cleanups.push({ extensionId, cleanup });
		}
	} catch (error) {
		active = false;
		await disposeCleanups(cleanups);
		throw error;
	}

	const frozenOrder = Object.freeze([...setReport.order]);
	const frozenResources = Object.freeze([...resources]);
	const frozenContextSources = Object.freeze([...contextSources]);
	const frozenRequestSources = Object.freeze([...requestSources]);
	const frozenToolSources = Object.freeze([...toolSources]);
	let disposed = false;
	return Object.freeze({
		order: frozenOrder,
		resources: frozenResources,
		composeActorOptions(base: RuntimeActorOptions): RuntimeActorOptions {
			if (!active) throw new GameExtensionActivationError("extension_inactive");
			if (!base || typeof base !== "object") throw new TypeError("Base actor options are required.");
			const baseContextSources = base.contextSources ?? [];
			const baseRequestSources = base.requestSources ?? [];
			const baseToolSource = base.toolSource;
			return {
				...base,
				...(baseContextSources.length > 0 || frozenContextSources.length > 0
					? { contextSources: Object.freeze([...baseContextSources, ...frozenContextSources]) }
					: {}),
				...(baseRequestSources.length > 0 || frozenRequestSources.length > 0
					? { requestSources: Object.freeze([...baseRequestSources, ...frozenRequestSources]) }
					: {}),
				...(baseToolSource || frozenToolSources.length > 0
					? { toolSource: composeToolSources(baseToolSource, frozenToolSources, () => active) }
					: {}),
			};
		},
		async dispose(): Promise<void> {
			if (disposed) return;
			disposed = true;
			active = false;
			if (!(await disposeCleanups(cleanups))) throw new GameExtensionActivationError("dispose_failed");
		},
	}) satisfies ActivatedGameExtensionSet;
}

function captureActivationLimits(limits: GameExtensionActivationLimits): Readonly<GameExtensionActivationLimits> {
	if (!limits || typeof limits !== "object") throw new TypeError("Extension activation limits are required.");
	const captured = { ...limits };
	for (const [name, value] of Object.entries(captured)) {
		if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${name} must be a positive safe integer.`);
	}
	return Object.freeze(captured);
}

function assertResourceId(
	resourceId: string,
	limits: Readonly<GameExtensionActivationLimits>,
	extensionId: string,
): void {
	try {
		assertOpaqueIdentifier(resourceId);
	} catch {
		throw new GameExtensionActivationError("resource_invalid", extensionId);
	}
	if (encoder.encode(resourceId).byteLength > limits.maxResourceIdBytes) {
		throw new GameExtensionActivationError("resource_limit_exceeded", extensionId);
	}
}

function captureContextSource(
	source: GameContextSource,
	extensionId: string,
	isActive: () => boolean,
): GameContextSource {
	if (
		!source ||
		typeof source !== "object" ||
		typeof source.sourceId !== "string" ||
		typeof source.load !== "function"
	) {
		throw new GameExtensionActivationError("resource_invalid", extensionId);
	}
	const load = source.load.bind(source);
	return Object.freeze({
		sourceId: source.sourceId,
		...(source.provenanceKind === undefined ? {} : { provenanceKind: source.provenanceKind }),
		load: (...args: Parameters<GameContextSource["load"]>) => {
			assertActive(isActive, extensionId);
			return load(...args);
		},
	});
}

function captureRequestSource(
	source: GameRequestSource,
	extensionId: string,
	isActive: () => boolean,
): GameRequestSource {
	if (
		!source ||
		typeof source !== "object" ||
		typeof source.sourceId !== "string" ||
		typeof source.collect !== "function"
	) {
		throw new GameExtensionActivationError("resource_invalid", extensionId);
	}
	const collect = source.collect.bind(source);
	return Object.freeze({
		sourceId: source.sourceId,
		...(source.provenanceKind === undefined ? {} : { provenanceKind: source.provenanceKind }),
		collect: (...args: Parameters<GameRequestSource["collect"]>) => {
			assertActive(isActive, extensionId);
			return collect(...args);
		},
	});
}

function captureToolSource(source: GameToolSource, extensionId: string, isActive: () => boolean): GameToolSource {
	return (input: Readonly<NormalizedGameInput>, signal: AbortSignal, coordinate: Readonly<RunCoordinate>) => {
		assertActive(isActive, extensionId);
		return source(input, signal, coordinate);
	};
}

function composeToolSources(
	base: GameToolSource | undefined,
	extensions: readonly CapturedToolSource[],
	isActive: () => boolean,
): GameToolSource {
	return async (input, signal, coordinate) => {
		assertActive(isActive);
		signal.throwIfAborted();
		const tools = [];
		if (base) {
			const loaded = await base(input, signal, coordinate);
			signal.throwIfAborted();
			if (!Array.isArray(loaded)) throw new TypeError("The host Tool source returned an invalid value.");
			tools.push(...loaded);
		}
		for (const source of extensions) {
			signal.throwIfAborted();
			const loaded = await source.load(input, signal, coordinate);
			signal.throwIfAborted();
			if (!Array.isArray(loaded))
				throw new TypeError(`Extension Tool source ${source.resourceId} returned an invalid value.`);
			tools.push(...loaded);
		}
		return Object.freeze(tools);
	};
}

function assertActive(isActive: () => boolean, extensionId?: string): void {
	if (!isActive()) throw new GameExtensionActivationError("extension_inactive", extensionId);
}

async function disposeCleanups(
	cleanups: readonly { readonly extensionId: string; readonly cleanup: GameExtensionCleanup }[],
): Promise<boolean> {
	let passed = true;
	for (let index = cleanups.length - 1; index >= 0; index -= 1) {
		try {
			await cleanups[index]?.cleanup();
		} catch {
			passed = false;
		}
	}
	return passed;
}
