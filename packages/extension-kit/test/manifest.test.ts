import { describe, expect, it } from "vitest";
import {
	diagnoseGameExtensionSet,
	GAME_EXTENSION_MANIFEST_SCHEMA_VERSION,
	type GameExtensionManifestLimits,
	parseGameExtensionManifest,
} from "../src/index.js";

const encoder = new TextEncoder();
const validManifest = {
	schemaVersion: "1",
	id: "studio.world-context",
	version: "1.2.3",
	permissions: ["context.contribute", "tools.contribute"],
	dependencies: [{ id: "studio.shared", versionRange: "^2.0.0" }],
} as const;
const validText = JSON.stringify(validManifest);

const LIMITS: GameExtensionManifestLimits = {
	maxManifestBytes: encoder.encode(validText).byteLength,
	maxJsonDepth: 4,
	maxIdentifierBytes: encoder.encode("studio.world-context").byteLength,
	maxVersionBytes: encoder.encode("1.2.3-alpha.1+build.7").byteLength,
	maxVersionRangeBytes: encoder.encode(">=1.2.3-alpha.1 <3.0.0").byteLength,
	maxDependencies: validManifest.dependencies.length,
};

describe("game extension development manifest", () => {
	it("strictly parses and detaches a bounded manifest", () => {
		const manifest = parseGameExtensionManifest(validText, LIMITS);

		expect(manifest).toEqual(validManifest);
		expect(manifest.schemaVersion).toBe(GAME_EXTENSION_MANIFEST_SCHEMA_VERSION);
		expect(Object.isFrozen(manifest)).toBe(true);
		expect(Object.isFrozen(manifest.permissions)).toBe(true);
		expect(Object.isFrozen(manifest.dependencies)).toBe(true);
		expect(Object.isFrozen(manifest.dependencies[0])).toBe(true);
	});

	it("rejects bytes, depth, duplicate keys, unknown fields, and unsupported schemas", () => {
		expect(() => parseGameExtensionManifest(`${validText} `, LIMITS)).toThrowError(
			expect.objectContaining({ code: "manifest_limit_exceeded" }),
		);
		expect(() =>
			parseGameExtensionManifest(
				'{"schemaVersion":"1","id":"studio.a","id":"studio.b","version":"1.0.0","permissions":[],"dependencies":[]}',
				{ ...LIMITS, maxManifestBytes: 1_024 },
			),
		).toThrowError(expect.objectContaining({ code: "manifest_invalid_json" }));
		expect(() =>
			parseGameExtensionManifest(JSON.stringify({ ...validManifest, extra: true }), {
				...LIMITS,
				maxManifestBytes: 1_024,
			}),
		).toThrowError(expect.objectContaining({ code: "manifest_invalid" }));
		expect(() =>
			parseGameExtensionManifest(JSON.stringify({ ...validManifest, schemaVersion: "2" }), {
				...LIMITS,
				maxManifestBytes: 1_024,
			}),
		).toThrowError(expect.objectContaining({ code: "manifest_schema_unsupported" }));
		expect(() =>
			parseGameExtensionManifest(
				JSON.stringify({
					...validManifest,
					dependencies: [{ id: "studio.shared", versionRange: [[["1.0.0"]]] }],
				}),
				{ ...LIMITS, maxManifestBytes: 1_024, maxJsonDepth: 3 },
			),
		).toThrowError(expect.objectContaining({ code: "manifest_limit_exceeded" }));
	});

	it("uses strict SemVer and a closed permission vocabulary", () => {
		for (const patch of [
			{ version: "v1.2.3" },
			{ version: "1.2" },
			{ permissions: ["context.contribute", "context.contribute"] },
			{ permissions: ["game.write"] },
			{ dependencies: [{ id: "studio.world-context", versionRange: "^1.0.0" }] },
			{ dependencies: [{ id: "studio.shared", versionRange: "not-a-range" }] },
			{
				dependencies: [
					{ id: "studio.shared", versionRange: "^1.0.0" },
					{ id: "studio.shared", versionRange: "^2.0.0" },
				],
			},
		]) {
			expect(() =>
				parseGameExtensionManifest(JSON.stringify({ ...validManifest, ...patch }), {
					...LIMITS,
					maxManifestBytes: 2_048,
					maxDependencies: 2,
				}),
			).toThrowError(expect.objectContaining({ code: "manifest_invalid" }));
		}
	});
});

describe("game extension dependency diagnostics", () => {
	it("returns a deterministic dependency-first order for an allowed set", () => {
		const shared = parseGameExtensionManifest(
			JSON.stringify({
				schemaVersion: "1",
				id: "studio.shared",
				version: "2.4.0",
				permissions: ["request.contribute"],
				dependencies: [],
			}),
			{ ...LIMITS, maxManifestBytes: 1_024 },
		);
		const world = parseGameExtensionManifest(validText, LIMITS);

		expect(
			diagnoseGameExtensionSet({
				manifests: [world, shared],
				allowedPermissions: ["tools.contribute", "request.contribute", "context.contribute"],
				limits: { maxExtensions: 2, maxDiagnostics: 1 },
			}),
		).toEqual({ passed: true, order: ["studio.shared", "studio.world-context"], diagnostics: [] });
	});

	it("diagnoses denied permissions, missing or incompatible dependencies, duplicates, and cycles", () => {
		const manifest = (value: Record<string, unknown>) =>
			parseGameExtensionManifest(JSON.stringify(value), {
				...LIMITS,
				maxManifestBytes: 2_048,
				maxDependencies: 2,
			});
		const shared = manifest({
			schemaVersion: "1",
			id: "studio.shared",
			version: "1.9.0",
			permissions: [],
			dependencies: [{ id: "studio.cycle", versionRange: "1.x" }],
		});
		const cycle = manifest({
			schemaVersion: "1",
			id: "studio.cycle",
			version: "1.0.0",
			permissions: [],
			dependencies: [{ id: "studio.shared", versionRange: "1.x" }],
		});
		const world = parseGameExtensionManifest(validText, LIMITS);

		const report = diagnoseGameExtensionSet({
			manifests: [world, shared, cycle, shared],
			allowedPermissions: ["context.contribute"],
			limits: { maxExtensions: 4, maxDiagnostics: 8 },
		});

		expect(report.passed).toBe(false);
		expect(report.order).toEqual([]);
		expect(report.diagnostics).toEqual(
			expect.arrayContaining([
				{ code: "extension_duplicate", extensionId: "studio.shared" },
				{ code: "permission_denied", extensionId: "studio.world-context", permission: "tools.contribute" },
				{
					code: "dependency_version_mismatch",
					extensionId: "studio.world-context",
					dependencyId: "studio.shared",
					requiredVersionRange: "^2.0.0",
					availableVersion: "1.9.0",
				},
				{ code: "dependency_cycle", extensionId: "studio.cycle" },
				{ code: "dependency_cycle", extensionId: "studio.shared" },
			]),
		);
	});

	it("fails closed instead of truncating diagnostics", () => {
		const world = parseGameExtensionManifest(validText, LIMITS);
		expect(() =>
			diagnoseGameExtensionSet({
				manifests: [world],
				allowedPermissions: [],
				limits: { maxExtensions: 1, maxDiagnostics: 1 },
			}),
		).toThrowError(expect.objectContaining({ code: "diagnostic_limit_exceeded" }));
	});
});
