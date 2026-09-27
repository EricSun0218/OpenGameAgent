import { satisfies } from "semver";
import { GAME_EXTENSION_PERMISSIONS, type GameExtensionManifest, type GameExtensionPermission } from "./manifest.js";

export interface GameExtensionSetLimits {
	readonly maxExtensions: number;
	readonly maxDiagnostics: number;
}

export type GameExtensionDiagnostic =
	| { readonly code: "extension_duplicate"; readonly extensionId: string }
	| {
			readonly code: "permission_denied";
			readonly extensionId: string;
			readonly permission: GameExtensionPermission;
	  }
	| {
			readonly code: "dependency_missing";
			readonly extensionId: string;
			readonly dependencyId: string;
			readonly requiredVersionRange: string;
	  }
	| {
			readonly code: "dependency_version_mismatch";
			readonly extensionId: string;
			readonly dependencyId: string;
			readonly requiredVersionRange: string;
			readonly availableVersion: string;
	  }
	| { readonly code: "dependency_cycle"; readonly extensionId: string };

export interface GameExtensionSetReport {
	readonly passed: boolean;
	readonly order: readonly string[];
	readonly diagnostics: readonly GameExtensionDiagnostic[];
}

export type GameExtensionSetErrorCode = "extension_limit_exceeded" | "diagnostic_limit_exceeded";

export class GameExtensionSetError extends RangeError {
	constructor(public readonly code: GameExtensionSetErrorCode) {
		super(code);
		this.name = "GameExtensionSetError";
	}
}

export interface DiagnoseGameExtensionSetOptions {
	readonly manifests: readonly GameExtensionManifest[];
	readonly allowedPermissions: readonly GameExtensionPermission[];
	readonly limits: GameExtensionSetLimits;
}

export function diagnoseGameExtensionSet(options: DiagnoseGameExtensionSetOptions): GameExtensionSetReport {
	if (!options || !Array.isArray(options.manifests) || !Array.isArray(options.allowedPermissions)) {
		throw new TypeError("Extension set options are required.");
	}
	const limits = captureSetLimits(options.limits);
	if (options.manifests.length > limits.maxExtensions) throw new GameExtensionSetError("extension_limit_exceeded");
	const allowed = captureAllowedPermissions(options.allowedPermissions);
	const diagnostics: GameExtensionDiagnostic[] = [];
	const push = (diagnostic: GameExtensionDiagnostic) => {
		if (diagnostics.length >= limits.maxDiagnostics) throw new GameExtensionSetError("diagnostic_limit_exceeded");
		diagnostics.push(Object.freeze(diagnostic));
	};

	const grouped = new Map<string, GameExtensionManifest[]>();
	for (const manifest of options.manifests) {
		if (!manifest || typeof manifest !== "object") throw new TypeError("Extension manifests are invalid.");
		const group = grouped.get(manifest.id) ?? [];
		group.push(manifest);
		grouped.set(manifest.id, group);
	}
	const ids = [...grouped.keys()].sort(compareStrings);
	for (const id of ids) {
		const group = grouped.get(id);
		if (group && group.length > 1) push({ code: "extension_duplicate", extensionId: id });
	}

	const byId = new Map<string, GameExtensionManifest>();
	for (const id of ids) {
		const manifest = grouped.get(id)?.[0];
		if (manifest) byId.set(id, manifest);
	}
	for (const id of ids) {
		const manifest = byId.get(id);
		if (!manifest) continue;
		for (const permission of manifest.permissions) {
			if (!allowed.has(permission)) push({ code: "permission_denied", extensionId: id, permission });
		}
		for (const dependency of [...manifest.dependencies].sort((left, right) => compareStrings(left.id, right.id))) {
			const available = byId.get(dependency.id);
			if (!available) {
				push({
					code: "dependency_missing",
					extensionId: id,
					dependencyId: dependency.id,
					requiredVersionRange: dependency.versionRange,
				});
				continue;
			}
			if (!satisfies(available.version, dependency.versionRange, { loose: false, includePrerelease: false })) {
				push({
					code: "dependency_version_mismatch",
					extensionId: id,
					dependencyId: dependency.id,
					requiredVersionRange: dependency.versionRange,
					availableVersion: available.version,
				});
			}
		}
	}

	for (const id of findCycleMembers(byId, ids)) push({ code: "dependency_cycle", extensionId: id });
	if (diagnostics.length > 0) {
		return Object.freeze({ passed: false, order: Object.freeze([]), diagnostics: Object.freeze(diagnostics) });
	}
	return Object.freeze({
		passed: true,
		order: Object.freeze(topologicalOrder(byId, ids)),
		diagnostics: Object.freeze([]),
	});
}

function captureSetLimits(limits: GameExtensionSetLimits): Readonly<GameExtensionSetLimits> {
	if (!limits || typeof limits !== "object") throw new TypeError("Extension set limits are required.");
	const captured = { ...limits };
	for (const [name, value] of Object.entries(captured)) {
		if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${name} must be a positive safe integer.`);
	}
	return Object.freeze(captured);
}

function captureAllowedPermissions(values: readonly GameExtensionPermission[]): ReadonlySet<GameExtensionPermission> {
	const known = new Set<string>(GAME_EXTENSION_PERMISSIONS);
	const captured = new Set<GameExtensionPermission>();
	for (const permission of values) {
		if (!known.has(permission) || captured.has(permission))
			throw new TypeError("Allowed extension permissions are invalid.");
		captured.add(permission);
	}
	return captured;
}

function findCycleMembers(byId: ReadonlyMap<string, GameExtensionManifest>, ids: readonly string[]): readonly string[] {
	const state = new Map<string, 0 | 1 | 2>();
	const cycleMembers = new Set<string>();
	type Frame = { readonly id: string; readonly dependencies: readonly string[]; index: number };
	for (const root of ids) {
		if (state.get(root)) continue;
		const path: string[] = [];
		const pathIndex = new Map<string, number>();
		const stack: Frame[] = [frame(root, byId)];
		state.set(root, 1);
		pathIndex.set(root, 0);
		path.push(root);
		while (stack.length > 0) {
			const current = stack.at(-1);
			if (!current) break;
			const dependency = current.dependencies[current.index++];
			if (dependency === undefined) {
				state.set(current.id, 2);
				stack.pop();
				pathIndex.delete(current.id);
				path.pop();
				continue;
			}
			const dependencyState = state.get(dependency) ?? 0;
			if (dependencyState === 1) {
				const start = pathIndex.get(dependency);
				if (start !== undefined)
					for (let index = start; index < path.length; index += 1) cycleMembers.add(path[index] as string);
				continue;
			}
			if (dependencyState === 2) continue;
			state.set(dependency, 1);
			pathIndex.set(dependency, path.length);
			path.push(dependency);
			stack.push(frame(dependency, byId));
		}
	}
	return [...cycleMembers].sort(compareStrings);
}

function frame(id: string, byId: ReadonlyMap<string, GameExtensionManifest>) {
	const dependencies = (byId.get(id)?.dependencies ?? [])
		.map((dependency) => dependency.id)
		.filter((dependencyId) => byId.has(dependencyId))
		.sort(compareStrings);
	return { id, dependencies, index: 0 };
}

function topologicalOrder(byId: ReadonlyMap<string, GameExtensionManifest>, ids: readonly string[]): string[] {
	const indegree = new Map<string, number>();
	const dependents = new Map<string, string[]>();
	for (const id of ids) {
		const dependencies = byId.get(id)?.dependencies ?? [];
		indegree.set(id, dependencies.length);
		for (const dependency of dependencies) {
			const list = dependents.get(dependency.id) ?? [];
			list.push(id);
			dependents.set(dependency.id, list);
		}
	}
	const ready = ids.filter((id) => indegree.get(id) === 0).sort(compareStrings);
	const order: string[] = [];
	while (ready.length > 0) {
		const id = ready.shift();
		if (!id) break;
		order.push(id);
		for (const dependent of (dependents.get(id) ?? []).sort(compareStrings)) {
			const remaining = (indegree.get(dependent) ?? 0) - 1;
			indegree.set(dependent, remaining);
			if (remaining === 0) insertSorted(ready, dependent);
		}
	}
	return order;
}

function insertSorted(values: string[], value: string): void {
	let index = 0;
	while (index < values.length && compareStrings(values[index] as string, value) < 0) index += 1;
	values.splice(index, 0, value);
}

function compareStrings(left: string, right: string): number {
	return left < right ? -1 : left > right ? 1 : 0;
}
