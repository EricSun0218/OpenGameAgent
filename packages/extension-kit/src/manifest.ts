import { type JsonValue, parseStrictJsonText, StrictJsonError } from "@opengameagent/protocol";
import { valid, validRange } from "semver";

export const GAME_EXTENSION_MANIFEST_SCHEMA_VERSION = "1" as const;

export const GAME_EXTENSION_PERMISSIONS = ["context.contribute", "request.contribute", "tools.contribute"] as const;

export type GameExtensionPermission = (typeof GAME_EXTENSION_PERMISSIONS)[number];

export interface GameExtensionDependency {
	readonly id: string;
	readonly versionRange: string;
}

export interface GameExtensionManifest {
	readonly schemaVersion: typeof GAME_EXTENSION_MANIFEST_SCHEMA_VERSION;
	readonly id: string;
	readonly version: string;
	readonly permissions: readonly GameExtensionPermission[];
	readonly dependencies: readonly GameExtensionDependency[];
}

export interface GameExtensionManifestLimits {
	readonly maxManifestBytes: number;
	readonly maxJsonDepth: number;
	readonly maxIdentifierBytes: number;
	readonly maxVersionBytes: number;
	readonly maxVersionRangeBytes: number;
	readonly maxDependencies: number;
}

export type GameExtensionManifestErrorCode =
	| "manifest_invalid_json"
	| "manifest_limit_exceeded"
	| "manifest_schema_unsupported"
	| "manifest_invalid";

export class GameExtensionManifestError extends TypeError {
	constructor(public readonly code: GameExtensionManifestErrorCode) {
		super(code);
		this.name = "GameExtensionManifestError";
	}
}

const encoder = new TextEncoder();
const fields = new Set(["schemaVersion", "id", "version", "permissions", "dependencies"]);
const dependencyFields = new Set(["id", "versionRange"]);
const permissions = new Set<string>(GAME_EXTENSION_PERMISSIONS);

export function parseGameExtensionManifest(text: string, limits: GameExtensionManifestLimits): GameExtensionManifest {
	const capturedLimits = captureManifestLimits(limits);
	if (typeof text !== "string" || text.length === 0) throw new GameExtensionManifestError("manifest_invalid_json");
	if (encoder.encode(text).byteLength > capturedLimits.maxManifestBytes) {
		throw new GameExtensionManifestError("manifest_limit_exceeded");
	}

	let value: JsonValue;
	try {
		value = parseStrictJsonText(text, capturedLimits.maxJsonDepth);
	} catch (error) {
		if (error instanceof StrictJsonError && error.code === "json_too_deep") {
			throw new GameExtensionManifestError("manifest_limit_exceeded");
		}
		throw new GameExtensionManifestError("manifest_invalid_json");
	}
	if (!isRecord(value) || Object.keys(value).some((field) => !fields.has(field))) {
		throw new GameExtensionManifestError("manifest_invalid");
	}
	if (value.schemaVersion !== GAME_EXTENSION_MANIFEST_SCHEMA_VERSION) {
		throw new GameExtensionManifestError("manifest_schema_unsupported");
	}
	if (!isExtensionIdentifier(value.id, capturedLimits.maxIdentifierBytes)) {
		throw new GameExtensionManifestError("manifest_invalid");
	}
	if (!isBoundedString(value.version, capturedLimits.maxVersionBytes) || valid(value.version) !== value.version) {
		throw new GameExtensionManifestError("manifest_invalid");
	}
	if (!Array.isArray(value.permissions) || !Array.isArray(value.dependencies)) {
		throw new GameExtensionManifestError("manifest_invalid");
	}
	if (value.dependencies.length > capturedLimits.maxDependencies) {
		throw new GameExtensionManifestError("manifest_limit_exceeded");
	}

	const seenPermissions = new Set<string>();
	const capturedPermissions: GameExtensionPermission[] = [];
	for (const permission of value.permissions) {
		if (typeof permission !== "string" || !permissions.has(permission) || seenPermissions.has(permission)) {
			throw new GameExtensionManifestError("manifest_invalid");
		}
		seenPermissions.add(permission);
		capturedPermissions.push(permission as GameExtensionPermission);
	}

	const seenDependencies = new Set<string>();
	const capturedDependencies: GameExtensionDependency[] = [];
	for (const dependency of value.dependencies) {
		if (
			!isRecord(dependency) ||
			Object.keys(dependency).length !== dependencyFields.size ||
			Object.keys(dependency).some((field) => !dependencyFields.has(field)) ||
			!isExtensionIdentifier(dependency.id, capturedLimits.maxIdentifierBytes) ||
			dependency.id === value.id ||
			seenDependencies.has(dependency.id) ||
			!isBoundedString(dependency.versionRange, capturedLimits.maxVersionRangeBytes) ||
			dependency.versionRange.trim() !== dependency.versionRange ||
			validRange(dependency.versionRange) === null
		) {
			throw new GameExtensionManifestError("manifest_invalid");
		}
		seenDependencies.add(dependency.id);
		capturedDependencies.push(Object.freeze({ id: dependency.id, versionRange: dependency.versionRange }));
	}

	return Object.freeze({
		schemaVersion: GAME_EXTENSION_MANIFEST_SCHEMA_VERSION,
		id: value.id,
		version: value.version,
		permissions: Object.freeze(capturedPermissions),
		dependencies: Object.freeze(capturedDependencies),
	});
}

function captureManifestLimits(limits: GameExtensionManifestLimits): Readonly<GameExtensionManifestLimits> {
	if (!limits || typeof limits !== "object") throw new TypeError("Extension manifest limits are required.");
	const captured = { ...limits };
	for (const [name, value] of Object.entries(captured)) {
		if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${name} must be a positive safe integer.`);
	}
	return Object.freeze(captured);
}

function isExtensionIdentifier(value: unknown, maxBytes: number): value is string {
	return (
		typeof value === "string" &&
		encoder.encode(value).byteLength <= maxBytes &&
		/^(?!.*(?:--|\.\.))[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/u.test(value)
	);
}

function isBoundedString(value: unknown, maxBytes: number): value is string {
	return typeof value === "string" && value.length > 0 && encoder.encode(value).byteLength <= maxBytes;
}

function isRecord(value: unknown): value is Record<string, JsonValue> {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}
