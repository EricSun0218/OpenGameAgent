import { access, mkdir, mkdtemp, rename, rm, symlink, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { type CallToolResult, LATEST_PROTOCOL_VERSION } from "@modelcontextprotocol/sdk/types.js";
import { afterEach, describe, expect, it } from "vitest";
import {
	AGENT_PLUGIN_MANIFEST_SCHEMA_V1,
	AGENT_PLUGIN_MCP_SCHEMA_V1,
	createAgentPluginMcpServerDefinition,
	DirectoryAgentPluginCatalog,
	DirectoryAgentPluginError,
	type DirectoryAgentPluginLimits,
	type DirectoryGameSkillLimits,
	DirectoryGameSkillSource,
} from "../src/index.js";

const PLUGIN_LIMITS: DirectoryAgentPluginLimits = {
	maxInstallations: 8,
	maxInstallationIdBytes: 64,
	maxAddressedPathBytes: 1_024,
	maxManifestBytes: 16_384,
	maxMcpConfigurationBytes: 32_768,
	maxTotalReadBytes: 98_304,
	maxJsonDepth: 16,
	maxDiagnostics: 32,
	maxMcpServersPerPlugin: 16,
	maxServerNameBytes: 128,
	maxExpandedValueBytes: 4_096,
};
const pluginRootVariable = "\u0024{PLUGIN_ROOT}";
const pluginDataVariable = "\u0024{PLUGIN_DATA}";

const SKILL_LIMITS: DirectoryGameSkillLimits = {
	maxRoots: 8,
	maxTraversalDepth: 8,
	maxDirectories: 32,
	maxEntriesPerDirectory: 32,
	maxVisitedEntries: 64,
	maxReadFiles: 16,
	maxSkillFileBytes: 8_192,
	maxIgnoreFileBytes: 1_024,
	maxTotalReadBytes: 32_768,
	maxLoadedSkills: 16,
	maxDiagnostics: 16,
	maxVisibleSkills: 16,
	maxCatalogBytes: 16_384,
	maxAddressedPathBytes: 1_024,
	maxResourcePathBytes: 512,
	maxResourceBytes: 8_192,
	maxToolResultBytes: 16_384,
};

const temporaryDirectories: string[] = [];
const clients: Client[] = [];

afterEach(async () => {
	for (const client of clients.splice(0)) await client.close().catch(() => undefined);
	for (const path of temporaryDirectories.splice(0)) await rm(path, { force: true, recursive: true });
});

describe("DirectoryAgentPluginCatalog", () => {
	it("loads the published portable format without executing it, then requires explicit host admission", async () => {
		const paths = await pluginPaths("portable");
		const packageEnvironment = Object.assign(Object.create(null) as Record<string, string>, {
			HOST_VALUE: "package",
			ROOT_ECHO: pluginRootVariable,
			DATA_ECHO: pluginDataVariable,
		});
		Object.defineProperty(packageEnvironment, "__proto__", {
			value: "literal-proto",
			enumerable: true,
			configurable: true,
			writable: true,
		});
		await mkdir(join(paths.root, "skills", "observe"), { recursive: true });
		await writeFile(
			join(paths.root, "skills", "observe", "SKILL.md"),
			"---\nname: observe\ndescription: Observe the current game state.\n---\n\nUse read-only tools first.\n",
			"utf8",
		);
		await writeFile(join(paths.root, "server.mjs"), serverFixture(), "utf8");
		await writeManifest(paths.root, {
			$schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1,
			name: "world.tools",
			version: "release-2026-08",
			description: "Portable world inspection",
			author: { name: "Example" },
			keywords: ["world", "inspection"],
		});
		await writeFile(
			join(paths.root, "mcp.json"),
			JSON.stringify({
				$schema: AGENT_PLUGIN_MCP_SCHEMA_V1,
				mcpServers: {
					local: {
						type: "stdio",
						command: "node",
						args: [`${pluginRootVariable}/server.mjs`, pluginDataVariable],
						env: packageEnvironment,
						cwd: pluginDataVariable,
					},
					remote: { type: "streamable-http", url: "http://127.0.0.2:4318/mcp" },
				},
			}),
			"utf8",
		);

		const catalog = createCatalog(paths);
		const snapshot = await catalog.load(new AbortController().signal);
		expect(snapshot.plugins).toEqual([
			expect.objectContaining({
				installationId: "portable",
				hasSkills: true,
				mcpServerNames: ["local", "remote"],
				manifest: expect.objectContaining({ name: "world.tools", version: "release-2026-08" }),
			}),
		]);
		expect(snapshot.diagnostics).toEqual([]);
		expect(Object.isFrozen(snapshot)).toBe(true);
		expect(Object.isFrozen(snapshot.mcpServers[0]?.configuration)).toBe(true);
		await expect(access(join(paths.data, "started"))).rejects.toBeDefined();

		const skills = new DirectoryGameSkillSource({
			cwd: paths.parent,
			sourceId: "plugin-skills",
			roots: snapshot.skillRoots,
			limits: SKILL_LIMITS,
		});
		await expect(skills.inspect(new AbortController().signal)).resolves.toMatchObject({
			skills: [{ name: "observe", rootId: "portable" }],
		});

		const local = snapshot.mcpServers.find((server) => server.serverName === "local");
		if (!local) throw new Error("local server was not discovered");
		const definition = createAgentPluginMcpServerDefinition(local, {
			serverId: "portable-local",
			description: "Inspect the fixture process",
			requirement: "required",
			transport: {
				type: "stdio",
				baseEnv: {
					...stringEnvironment(),
					HOST_VALUE: "host",
					PLUGIN_ROOT: "must-be-replaced",
					PLUGIN_DATA: "must-be-replaced",
				},
				stderr: "ignore",
				maxMessageBytes: 1_048_576,
			},
		});
		const client = new Client({ name: "plugin-test", version: "1" });
		clients.push(client);
		await client.connect(await definition.createTransport(new AbortController().signal));
		const result = (await client.callTool({ name: "inspect", arguments: {} })) as CallToolResult;
		const text = result.content[0]?.type === "text" ? result.content[0].text : "";
		const observed = JSON.parse(text) as Record<string, unknown>;
		expect(observed).toMatchObject({
			cwd: paths.data,
			argument: paths.data,
			hostValue: "package",
			pluginRoot: local.pluginRootIdentity.canonicalPath,
			rootEcho: local.pluginRootIdentity.canonicalPath,
			protoEcho: "literal-proto",
		});
		expect(observed.pluginData).toBe(observed.dataEcho);
		await expect(access(join(paths.data, "started"))).resolves.toBeUndefined();
	});

	it("keeps manifest, component, and server failure boundaries independent", async () => {
		const first = await pluginPaths("first");
		await mkdir(join(first.root, "skills", "safe"), { recursive: true });
		await writeFile(
			join(first.root, "skills", "safe", "SKILL.md"),
			"---\nname: safe\ndescription: Safe instructions.\n---\n",
			"utf8",
		);
		await writeManifest(first.root, {
			$schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1,
			name: "first",
			future: true,
			extensions: false,
		});
		await writeFile(
			join(first.root, "mcp.json"),
			JSON.stringify({
				$schema: AGENT_PLUGIN_MCP_SCHEMA_V1,
				mcpServers: {
					valid: { type: "streamable-http", url: "https://example.com/mcp" },
					legacy: { type: "sse", url: "https://example.com/events" },
					broken: { type: "stdio", command: "node", extra: true },
					insecure: { type: "streamable-http", url: "http://example.com/mcp" },
				},
			}),
			"utf8",
		);

		const second = await pluginPaths("second");
		await writeManifest(second.root, {
			$schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1,
			name: "second",
			description: 42,
		});

		const snapshot = await new DirectoryAgentPluginCatalog({
			cwd: first.parent,
			installations: [first.installation, second.installation],
			limits: PLUGIN_LIMITS,
		}).load(new AbortController().signal);

		expect(snapshot.plugins.map((plugin) => plugin.installationId)).toEqual(["first"]);
		expect(snapshot.skillRoots).toHaveLength(1);
		expect(snapshot.mcpServers.map((server) => server.serverName)).toEqual(["valid"]);
		expect(snapshot.diagnostics.map((diagnostic) => diagnostic.code)).toEqual([
			"manifest_field_ignored",
			"manifest_extensions_ignored",
			"mcp_server_invalid",
			"mcp_server_invalid",
			"mcp_transport_unsupported",
			"manifest_invalid",
		]);
	});

	it("rejects escaped fixed locations and plugin-root working directories", async () => {
		const paths = await pluginPaths("escape");
		const outside = join(paths.parent, "outside");
		await mkdir(outside, { recursive: true });
		await symlink(outside, join(paths.root, "skills"), process.platform === "win32" ? "junction" : "dir");
		await symlink(outside, join(paths.root, "escape"), process.platform === "win32" ? "junction" : "dir");
		await writeManifest(paths.root, { $schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1, name: "escape" });
		await writeFile(
			join(paths.root, "mcp.json"),
			JSON.stringify({
				$schema: AGENT_PLUGIN_MCP_SCHEMA_V1,
				mcpServers: { escaped: { type: "stdio", command: "node", cwd: "./escape" } },
			}),
			"utf8",
		);

		const snapshot = await createCatalog(paths).load(new AbortController().signal);
		expect(snapshot.skillRoots).toEqual([]);
		expect(snapshot.mcpServers).toEqual([]);
		expect(snapshot.diagnostics).toEqual([
			{ installationId: "escape", code: "skills_invalid" },
			{ installationId: "escape", code: "mcp_server_invalid", serverName: "escaped" },
		]);
	});

	it("enforces strict JSON and per-file versus complete-catalog budgets", async () => {
		const paths = await pluginPaths("limits");
		const manifest = `{"$schema":"${AGENT_PLUGIN_MANIFEST_SCHEMA_V1}","name":"limits"}`;
		await writeFile(join(paths.root, "plugin.json"), manifest, "utf8");
		await writeFile(
			join(paths.root, "mcp.json"),
			JSON.stringify({ $schema: AGENT_PLUGIN_MCP_SCHEMA_V1, mcpServers: {} }),
			"utf8",
		);

		const tooSmall = createCatalog(paths, { ...PLUGIN_LIMITS, maxManifestBytes: 1 });
		await expect(tooSmall.load(new AbortController().signal)).resolves.toMatchObject({
			plugins: [],
			diagnostics: [{ installationId: "limits", code: "manifest_invalid" }],
		});

		const totalBound = createCatalog(paths, {
			...PLUGIN_LIMITS,
			maxTotalReadBytes: Buffer.byteLength(manifest),
		});
		await expect(totalBound.load(new AbortController().signal)).rejects.toMatchObject({
			code: "catalog_limit_exceeded",
		});

		await writeFile(
			join(paths.root, "plugin.json"),
			`{"$schema":"${AGENT_PLUGIN_MANIFEST_SCHEMA_V1}","name":"first","name":"second"}`,
			"utf8",
		);
		await expect(createCatalog(paths).load(new AbortController().signal)).resolves.toMatchObject({
			plugins: [],
			diagnostics: [{ installationId: "limits", code: "manifest_invalid" }],
		});
	});

	it("distinguishes malformed manifests from unsupported versions and disables only invalid MCP configuration", async () => {
		const missingSchema = await pluginPaths("missing-schema");
		await writeManifest(missingSchema.root, { name: "missing-schema" });
		const draftSchema = await pluginPaths("draft-schema");
		await writeManifest(draftSchema.root, {
			$schema: "https://agent-plugins.org/schemas/1.1.0/plugin.schema.json",
			name: "draft-schema",
		});
		const isolated = await pluginPaths("isolated");
		await mkdir(join(isolated.root, "skills", "still-available"), { recursive: true });
		await writeFile(
			join(isolated.root, "skills", "still-available", "SKILL.md"),
			"---\nname: still-available\ndescription: Survives an invalid MCP component.\n---\n",
			"utf8",
		);
		await writeManifest(isolated.root, { $schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1, name: "isolated" });
		await writeFile(
			join(isolated.root, "mcp.json"),
			JSON.stringify({
				$schema: AGENT_PLUGIN_MCP_SCHEMA_V1,
				mcpServers: {},
				unexpected: true,
			}),
			"utf8",
		);

		const snapshot = await new DirectoryAgentPluginCatalog({
			cwd: isolated.parent,
			installations: [missingSchema.installation, draftSchema.installation, isolated.installation],
			limits: PLUGIN_LIMITS,
		}).load(new AbortController().signal);

		expect(snapshot.plugins.map((plugin) => plugin.installationId)).toEqual(["isolated"]);
		expect(snapshot.skillRoots).toEqual([{ rootId: "isolated", path: join(isolated.root, "skills") }]);
		expect(snapshot.mcpServers).toEqual([]);
		expect(snapshot.diagnostics).toEqual([
			{ installationId: "missing-schema", code: "manifest_invalid" },
			{ installationId: "draft-schema", code: "manifest_schema_unsupported" },
			{ installationId: "isolated", code: "mcp_configuration_invalid" },
		]);
	});

	it("fences a host-admitted server when the installed root is retargeted", async () => {
		const base = await pluginPaths("retarget");
		const firstRoot = join(base.parent, "first-target");
		const secondRoot = join(base.parent, "second-target");
		await rm(base.root, { force: true, recursive: true });
		for (const root of [firstRoot, secondRoot]) {
			await mkdir(root, { recursive: true });
			await writeManifest(root, { $schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1, name: "retarget" });
			await writeFile(
				join(root, "mcp.json"),
				JSON.stringify({
					$schema: AGENT_PLUGIN_MCP_SCHEMA_V1,
					mcpServers: { local: { type: "stdio", command: "node" } },
				}),
				"utf8",
			);
		}
		await symlink(firstRoot, base.root, process.platform === "win32" ? "junction" : "dir");
		const snapshot = await createCatalog(base).load(new AbortController().signal);
		const server = snapshot.mcpServers[0];
		if (!server) throw new Error("server was not discovered");
		const definition = createAgentPluginMcpServerDefinition(server, {
			serverId: "retarget-local",
			description: "Retarget test",
			requirement: "required",
			transport: {
				type: "stdio",
				baseEnv: stringEnvironment(),
				stderr: "ignore",
				maxMessageBytes: 1_048_576,
			},
		});

		const movedFirstRoot = join(base.parent, "moved-first-target");
		await rename(firstRoot, movedFirstRoot);
		await rename(secondRoot, firstRoot);
		await expect(definition.createTransport(new AbortController().signal)).rejects.toMatchObject({
			code: "transport_rejected",
		});
		await expect(access(base.data)).rejects.toBeDefined();
	});

	it("gives host HTTP headers precedence over visible package headers", async () => {
		const paths = await pluginPaths("http-headers");
		await writeManifest(paths.root, { $schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1, name: "http-headers" });
		await writeFile(
			join(paths.root, "mcp.json"),
			JSON.stringify({
				$schema: AGENT_PLUGIN_MCP_SCHEMA_V1,
				mcpServers: {
					remote: {
						type: "streamable-http",
						url: "https://example.com/mcp",
						headers: { "X-Tenant": "package", "X-Package": "yes" },
					},
				},
			}),
			"utf8",
		);
		const snapshot = await createCatalog(paths).load(new AbortController().signal);
		const server = snapshot.mcpServers[0];
		if (!server) throw new Error("server was not discovered");
		const requestHeaders: Headers[] = [];
		const boundedFetch: typeof fetch = async (input, init) => {
			const request = new Request(input, init);
			requestHeaders.push(new Headers(request.headers));
			const text = await request.text();
			const body = text.length > 0 ? (JSON.parse(text) as Record<string, unknown>) : {};
			if (body.method === "initialize") {
				return new Response(
					JSON.stringify({
						jsonrpc: "2.0",
						id: body.id,
						result: {
							protocolVersion: LATEST_PROTOCOL_VERSION,
							capabilities: {},
							serverInfo: { name: "fixture", version: "1" },
						},
					}),
					{ status: 200, headers: { "content-type": "application/json" } },
				);
			}
			return new Response(null, { status: 202 });
		};
		const definition = createAgentPluginMcpServerDefinition(server, {
			serverId: "http-headers-remote",
			description: "Header precedence test",
			requirement: "optional",
			transport: {
				type: "streamable-http",
				headers: { "X-Tenant": "host", "X-Host": "yes" },
				fetch: boundedFetch,
				maxHttpResponseBytes: 65_536,
				maxSseEventBytes: 65_536,
				reconnection: { initialDelayMs: 1, maxDelayMs: 1, delayGrowFactor: 1, maxAttempts: 0 },
			},
		});
		const client = new Client({ name: "plugin-http-test", version: "1" });
		clients.push(client);
		await client.connect(await definition.createTransport(new AbortController().signal));
		expect(requestHeaders[0]?.get("x-tenant")).toBe("host");
		expect(requestHeaders[0]?.get("x-package")).toBe("yes");
		expect(requestHeaders[0]?.get("x-host")).toBe("yes");
	});

	it("rejects ambiguous installation identities and overlapping package/data roots", async () => {
		const paths = await pluginPaths("config");
		expect(
			() =>
				new DirectoryAgentPluginCatalog({
					cwd: paths.parent,
					installations: [paths.installation, { ...paths.installation }],
					limits: PLUGIN_LIMITS,
				}),
		).toThrow(DirectoryAgentPluginError);
		expect(
			() =>
				new DirectoryAgentPluginCatalog({
					cwd: paths.parent,
					installations: [{ ...paths.installation, dataPath: join(paths.root, "data") }],
					limits: PLUGIN_LIMITS,
				}),
		).toThrow(DirectoryAgentPluginError);
	});
});

interface TestPluginPaths {
	readonly parent: string;
	readonly root: string;
	readonly data: string;
	readonly installation: { readonly installationId: string; readonly rootPath: string; readonly dataPath: string };
}

async function pluginPaths(installationId: string): Promise<TestPluginPaths> {
	const packageRoot = dirname(fileURLToPath(import.meta.url));
	const parent = await mkdtemp(join(packageRoot, ".agent-plugin-test-"));
	temporaryDirectories.push(parent);
	const root = join(parent, `${installationId}-plugin`);
	const data = join(parent, `${installationId}-data`);
	await mkdir(root, { recursive: true });
	return {
		parent,
		root,
		data,
		installation: { installationId, rootPath: root, dataPath: data },
	};
}

function createCatalog(
	paths: TestPluginPaths,
	limits: DirectoryAgentPluginLimits = PLUGIN_LIMITS,
): DirectoryAgentPluginCatalog {
	return new DirectoryAgentPluginCatalog({
		cwd: paths.parent,
		installations: [paths.installation],
		limits,
	});
}

async function writeManifest(root: string, value: Record<string, unknown>): Promise<void> {
	await writeFile(join(root, "plugin.json"), JSON.stringify(value), "utf8");
}

function stringEnvironment(): Record<string, string> {
	return Object.fromEntries(
		Object.entries(process.env).filter((entry): entry is [string, string] => entry[1] !== undefined),
	);
}

function serverFixture(): string {
	return `
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
writeFileSync(join(process.env.PLUGIN_DATA, "started"), "started");
const server = new Server({ name: "fixture", version: "1" }, { capabilities: { tools: {} } });
server.setRequestHandler(ListToolsRequestSchema, () => ({ tools: [{ name: "inspect", inputSchema: { type: "object", properties: {}, additionalProperties: false } }] }));
server.setRequestHandler(CallToolRequestSchema, () => ({ content: [{ type: "text", text: JSON.stringify({
  cwd: process.cwd(),
  argument: process.argv[2],
  hostValue: process.env.HOST_VALUE,
  pluginRoot: process.env.PLUGIN_ROOT,
  pluginData: process.env.PLUGIN_DATA,
  rootEcho: process.env.ROOT_ECHO,
  dataEcho: process.env.DATA_ECHO,
  protoEcho: process.env.__proto__
}) }] }));
await server.connect(new StdioServerTransport());
`;
}
