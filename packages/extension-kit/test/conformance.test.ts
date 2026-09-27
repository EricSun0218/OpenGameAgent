import type { AgentTool } from "@earendil-works/pi-agent-core";
import type { GameContextSource, GameRequestSource, GameToolSource } from "@opengameagent/agent";
import { Type } from "typebox";
import { describe, expect, it } from "vitest";
import {
	activateGameExtensions,
	type GameExtensionEntry,
	parseGameExtensionManifest,
	runGameExtensionConformance,
} from "../src/index.js";

const MANIFEST_LIMITS = {
	maxManifestBytes: 4_096,
	maxJsonDepth: 4,
	maxIdentifierBytes: 128,
	maxVersionBytes: 64,
	maxVersionRangeBytes: 128,
	maxDependencies: 8,
} as const;
const SET_LIMITS = { maxExtensions: 8, maxDiagnostics: 16 } as const;
const ACTIVATION_LIMITS = { maxResourcesPerExtension: 8, maxResourceIdBytes: 128 } as const;
const RUNTIME_LIMITS = { maxActiveActors: 1, maxQueuedRuns: 1, maxInputBytes: 4_096, maxInputDepth: 8 } as const;
const CONTEXT_LIMITS = { maxReferencesPerSnapshot: 8, maxSnapshotBytes: 8_192 } as const;
const TOOL_LIMITS = {
	maxTools: 8,
	maxCatalogBytes: 32_768,
	maxSchemaDepth: 8,
	maxRequestSources: 8,
	maxPolicyRevisionBytes: 128,
	maxArgumentsBytes: 4_096,
	maxArgumentsDepth: 8,
	maxResultContentParts: 8,
	maxResultBytes: 8_192,
	maxResultDepth: 8,
	maxResultUpdates: 8,
} as const;

function manifest(
	id: string,
	version: string,
	permissions: readonly string[],
	dependencies: readonly { readonly id: string; readonly versionRange: string }[] = [],
) {
	return parseGameExtensionManifest(
		JSON.stringify({ schemaVersion: "1", id, version, permissions, dependencies }),
		MANIFEST_LIMITS,
	);
}

function tool(name: string): AgentTool {
	return {
		name,
		label: name,
		description: `${name} fixture`,
		parameters: Type.Object({}),
		execute: async () => ({ content: [{ type: "text", text: "ok" }], details: {} }),
	};
}

describe("game extension activation", () => {
	it("configures dependency-first contributions and composes them onto host actor options", async () => {
		const calls: string[] = [];
		const hostToolSource: GameToolSource = async () => [tool("host_tool")];
		const shared: GameExtensionEntry = {
			manifest: manifest("studio.shared", "2.0.0", ["request.contribute"]),
			module: {
				descriptor: { id: "studio.shared", version: "2.0.0" },
				configure(api) {
					const source: GameRequestSource = {
						sourceId: "studio.shared.request",
						collect: async () => {
							calls.push("shared.request");
							return {};
						},
					};
					api.registerRequestSource(source);
					return () => {
						calls.push("shared.dispose");
					};
				},
			},
		};
		const world: GameExtensionEntry = {
			manifest: manifest(
				"studio.world",
				"1.0.0",
				["context.contribute", "tools.contribute"],
				[{ id: "studio.shared", versionRange: "^2.0.0" }],
			),
			module: {
				descriptor: { id: "studio.world", version: "1.0.0" },
				configure(api) {
					const context: GameContextSource = {
						sourceId: "studio.world.context",
						load: async () => {
							calls.push("world.context");
							return { references: [{ referenceId: "world", revision: "1", content: { weather: "rain" } }] };
						},
					};
					api.registerContextSource(context);
					api.registerToolSource("studio.world.tools", async () => {
						calls.push("world.tools");
						return [tool("world_tool")];
					});
					return () => {
						calls.push("world.dispose");
					};
				},
			},
		};

		const active = await activateGameExtensions({
			entries: [world, shared],
			allowedPermissions: ["context.contribute", "request.contribute", "tools.contribute"],
			setLimits: SET_LIMITS,
			activationLimits: ACTIVATION_LIMITS,
		});
		const actor = active.composeActorOptions({
			sessionId: "session",
			actorId: "actor",
			model: {} as never,
			modelStream: (() => undefined) as never,
			toolSource: hostToolSource,
		});

		expect(active.order).toEqual(["studio.shared", "studio.world"]);
		expect(active.resources).toEqual([
			{ extensionId: "studio.shared", resourceId: "studio.shared.request", permission: "request.contribute" },
			{ extensionId: "studio.world", resourceId: "studio.world.context", permission: "context.contribute" },
			{ extensionId: "studio.world", resourceId: "studio.world.tools", permission: "tools.contribute" },
		]);
		expect(actor.contextSources?.map((source) => source.sourceId)).toEqual(["studio.world.context"]);
		expect(actor.requestSources?.map((source) => source.sourceId)).toEqual(["studio.shared.request"]);
		expect(
			(await actor.toolSource?.({} as never, new AbortController().signal, {} as never))?.map((item) => item.name),
		).toEqual(["host_tool", "world_tool"]);

		await active.dispose();
		expect(calls.slice(-2)).toEqual(["world.dispose", "shared.dispose"]);
	});

	it("rejects descriptor mismatches, undeclared resources, and retained registration APIs", async () => {
		let retained: Parameters<NonNullable<GameExtensionEntry["module"]["configure"]>>[0] | undefined;
		const entry: GameExtensionEntry = {
			manifest: manifest("studio.context", "1.0.0", ["context.contribute"]),
			module: {
				descriptor: { id: "studio.context", version: "1.0.0" },
				configure(api) {
					retained = api;
				},
			},
		};
		const active = await activateGameExtensions({
			entries: [entry],
			allowedPermissions: ["context.contribute"],
			setLimits: SET_LIMITS,
			activationLimits: ACTIVATION_LIMITS,
		});
		expect(() => retained?.registerToolSource("late", async () => [])).toThrowError(
			expect.objectContaining({ code: "registration_closed" }),
		);
		await active.dispose();

		await expect(
			activateGameExtensions({
				entries: [{ ...entry, module: { ...entry.module, descriptor: { id: "studio.other", version: "1.0.0" } } }],
				allowedPermissions: ["context.contribute"],
				setLimits: SET_LIMITS,
				activationLimits: ACTIVATION_LIMITS,
			}),
		).rejects.toMatchObject({ code: "descriptor_mismatch", extensionId: "studio.context" });

		await expect(
			activateGameExtensions({
				entries: [
					{
						...entry,
						module: {
							...entry.module,
							configure(api) {
								api.registerToolSource("undeclared", async () => []);
							},
						},
					},
				],
				allowedPermissions: ["context.contribute", "tools.contribute"],
				setLimits: SET_LIMITS,
				activationLimits: ACTIVATION_LIMITS,
			}),
		).rejects.toMatchObject({ code: "permission_undeclared", extensionId: "studio.context" });
	});

	it("rolls back configured modules in reverse order when later configuration fails", async () => {
		const cleanup: string[] = [];
		const first: GameExtensionEntry = {
			manifest: manifest("studio.first", "1.0.0", []),
			module: {
				descriptor: { id: "studio.first", version: "1.0.0" },
				configure: () => () => {
					cleanup.push("first");
				},
			},
		};
		const second: GameExtensionEntry = {
			manifest: manifest("studio.second", "1.0.0", [], [{ id: "studio.first", versionRange: "1.x" }]),
			module: {
				descriptor: { id: "studio.second", version: "1.0.0" },
				configure: () => {
					throw new Error("private failure");
				},
			},
		};

		await expect(
			activateGameExtensions({
				entries: [second, first],
				allowedPermissions: [],
				setLimits: SET_LIMITS,
				activationLimits: ACTIVATION_LIMITS,
			}),
		).rejects.toMatchObject({ code: "configure_failed", extensionId: "studio.second" });
		expect(cleanup).toEqual(["first"]);
	});
});

describe("real-runtime extension conformance", () => {
	it("invokes contributed context, request, and Tool sources through one real Pi-backed runtime request", async () => {
		const calls = { context: 0, request: 0, tools: 0, dispose: 0 };
		const entry: GameExtensionEntry = {
			manifest: manifest("studio.conformance", "1.0.0", [
				"context.contribute",
				"request.contribute",
				"tools.contribute",
			]),
			module: {
				descriptor: { id: "studio.conformance", version: "1.0.0" },
				configure(api) {
					api.registerContextSource({
						sourceId: "conformance.context",
						load: async () => {
							calls.context += 1;
							return { references: [{ referenceId: "state", revision: "1", content: { value: true } }] };
						},
					});
					api.registerRequestSource({
						sourceId: "conformance.request",
						collect: async () => {
							calls.request += 1;
							return {};
						},
					});
					api.registerToolSource("conformance.tools", async () => {
						calls.tools += 1;
						return [tool("inspect_world")];
					});
					return () => {
						calls.dispose += 1;
					};
				},
			},
		};

		const report = await runGameExtensionConformance({
			entries: [entry],
			allowedPermissions: ["context.contribute", "request.contribute", "tools.contribute"],
			setLimits: SET_LIMITS,
			activationLimits: ACTIVATION_LIMITS,
			runtimeLimits: RUNTIME_LIMITS,
			contextLimits: CONTEXT_LIMITS,
			toolLimits: TOOL_LIMITS,
			input: { instruction: "inspect" },
		});

		expect(report).toMatchObject({ passed: true, providerRequestCount: 1, diagnostics: [] });
		expect(report.resources).toHaveLength(3);
		expect(calls).toEqual({ context: 1, request: 1, tools: 1, dispose: 1 });
	});

	it("reports configuration failures without making a provider request", async () => {
		const entry: GameExtensionEntry = {
			manifest: manifest("studio.failure", "1.0.0", []),
			module: {
				descriptor: { id: "studio.failure", version: "2.0.0" },
				configure: () => undefined,
			},
		};

		await expect(
			runGameExtensionConformance({
				entries: [entry],
				allowedPermissions: [],
				setLimits: SET_LIMITS,
				activationLimits: ACTIVATION_LIMITS,
				runtimeLimits: RUNTIME_LIMITS,
				contextLimits: CONTEXT_LIMITS,
				toolLimits: TOOL_LIMITS,
				input: {},
			}),
		).resolves.toMatchObject({
			passed: false,
			providerRequestCount: 0,
			diagnostics: [{ code: "descriptor_mismatch", extensionId: "studio.failure" }],
		});
	});
});
