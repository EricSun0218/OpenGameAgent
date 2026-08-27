import { lstat, mkdir, readdir, readFile, realpath, stat } from "node:fs/promises";
import { isAbsolute, relative, resolve } from "node:path";
import {
	connectHttpGameMcp,
	connectStdioGameMcp,
	type GameMcpBridgeOptions,
	type GameMcpServer,
	GameMcpToolBridge,
} from "@opengameagent/mcp";
import { CompositeGameSkillSource, DirectoryGameSkillSource, type GameSkillSource } from "@opengameagent/skills";

const manifestSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
const mcpSchema = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json";
const pluginName = /^(?!.*(?:--|\.\.))[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$/u;
const serverName = /^[A-Za-z0-9_.-]{1,128}$/u;

export type GamePluginDiagnosticSeverity = "info" | "warning" | "error";

export interface GamePluginDiagnostic {
	severity: GamePluginDiagnosticSeverity;
	code: string;
	component: "manifest" | "skills" | "mcp";
	message: string;
}

export interface PortableGamePluginManifest {
	name: string;
	version?: string;
	description?: string;
	author?: Readonly<{ name?: string; email?: string; url?: string }>;
	homepage?: string;
	repository?: string;
	license?: string;
	keywords: readonly string[];
	extensions: ReadonlyMap<string, Readonly<Record<string, unknown>>>;
}

export interface PortableGamePlugin {
	rootDirectory: string;
	dataDirectory?: string;
	manifest: PortableGamePluginManifest;
	diagnostics: readonly GamePluginDiagnostic[];
	skills?: GameSkillSource;
	mcp?: GameMcpToolBridge;
}

export interface PortableGamePluginLoadOptions {
	dataDirectory?: string;
	loadSkills?: boolean;
	loadMcp?: boolean;
	allowStdio?: boolean;
	allowHttp?: boolean;
	baseEnvironment?: Readonly<Record<string, string>>;
	httpHeaders?: Readonly<Record<string, Readonly<Record<string, string>>>>;
	maximumManifestBytes?: number;
	maximumMcpBytes?: number;
	maximumSkills?: number;
	maximumMcpServers?: number;
	maximumArgumentsPerServer?: number;
	maximumEnvironmentVariablesPerServer?: number;
	maximumHeadersPerServer?: number;
	maximumDiagnostics?: number;
	mcpOptions?: Omit<GameMcpBridgeOptions, "servers">;
}

interface Limits {
	maximumManifestBytes: number;
	maximumMcpBytes: number;
	maximumSkills: number;
	maximumMcpServers: number;
	maximumArgumentsPerServer: number;
	maximumEnvironmentVariablesPerServer: number;
	maximumHeadersPerServer: number;
	maximumDiagnostics: number;
}

function boundedInteger(
	value: number | undefined,
	fallback: number,
	minimum: number,
	maximum: number,
	name: string,
): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < minimum || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function objectValue(value: unknown, message: string): Record<string, unknown> {
	if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError(message);
	return value as Record<string, unknown>;
}

function optionalString(value: unknown, name: string, maximum = 65_536): string | undefined {
	if (value === undefined) return undefined;
	if (typeof value !== "string" || value.length > maximum) throw new TypeError(`${name} must be a bounded string.`);
	return value;
}

function isWithin(root: string, target: string): boolean {
	const path = relative(root, target);
	return path === "" || (!path.startsWith("..") && !isAbsolute(path));
}

async function containedRealPath(root: string, target: string, expected: "file" | "directory"): Promise<string> {
	const actual = await realpath(target);
	if (!isWithin(root, actual)) throw new Error("Plugin path escapes its package root.");
	const information = await stat(actual);
	if ((expected === "file" && !information.isFile()) || (expected === "directory" && !information.isDirectory()))
		throw new Error(`Plugin path is not a ${expected}.`);
	return actual;
}

async function readJson(path: string, maximumBytes: number): Promise<unknown> {
	const information = await stat(path);
	if (!information.isFile() || information.size > maximumBytes)
		throw new RangeError("Plugin JSON exceeds its byte limit.");
	const bytes = await readFile(path);
	if (bytes.byteLength > maximumBytes) throw new RangeError("Plugin JSON exceeds its byte limit.");
	let text: string;
	try {
		text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
	} catch {
		throw new Error("Plugin JSON is not valid UTF-8.");
	}
	return JSON.parse(text) as unknown;
}

function diagnosticBuffer(maximum: number) {
	const values: GamePluginDiagnostic[] = [];
	return {
		values,
		add(diagnostic: GamePluginDiagnostic) {
			if (values.length < maximum) values.push(diagnostic);
		},
	};
}

function parseManifest(value: unknown, add: (diagnostic: GamePluginDiagnostic) => void): PortableGamePluginManifest {
	const root = objectValue(value, "Plugin manifest must be an object.");
	const known = new Set([
		"$schema",
		"name",
		"version",
		"description",
		"author",
		"homepage",
		"repository",
		"license",
		"keywords",
		"extensions",
	]);
	for (const key of Object.keys(root)) {
		if (!known.has(key))
			add({
				severity: "warning",
				code: "manifest.unknown-field",
				component: "manifest",
				message: `Unknown manifest field '${key}' was ignored.`,
			});
	}
	if (root["$schema"] !== manifestSchema) throw new Error("Plugin manifest schema is unsupported.");
	if (typeof root["name"] !== "string" || !pluginName.test(root["name"])) throw new Error("Plugin name is invalid.");
	let author: PortableGamePluginManifest["author"];
	if (root["author"] !== undefined) {
		const value = objectValue(root["author"], "Plugin author must be an object.");
		if (Object.keys(value).some((key) => !new Set(["name", "email", "url"]).has(key)))
			throw new Error("Plugin author contains an unknown field.");
		const authorName = optionalString(value["name"], "author.name");
		const authorEmail = optionalString(value["email"], "author.email");
		const authorUrl = optionalString(value["url"], "author.url");
		author = {
			...(authorName === undefined ? {} : { name: authorName }),
			...(authorEmail === undefined ? {} : { email: authorEmail }),
			...(authorUrl === undefined ? {} : { url: authorUrl }),
		};
	}
	let keywords: string[] = [];
	if (root["keywords"] !== undefined) {
		if (!Array.isArray(root["keywords"]) || root["keywords"].length > 1_024)
			throw new Error("Plugin keywords must be a bounded string array.");
		keywords = root["keywords"].map((item) => {
			if (typeof item !== "string" || item.length > 65_536) throw new Error("Plugin keyword is invalid.");
			return item;
		});
	}
	const extensions = new Map<string, Readonly<Record<string, unknown>>>();
	if (root["extensions"] !== undefined) {
		if (root["extensions"] === null || typeof root["extensions"] !== "object" || Array.isArray(root["extensions"])) {
			add({
				severity: "warning",
				code: "manifest.extensions-ignored",
				component: "manifest",
				message: "Non-object manifest extensions were ignored.",
			});
		} else {
			for (const [namespace, entry] of Object.entries(root["extensions"])) {
				if (entry === null || typeof entry !== "object" || Array.isArray(entry))
					throw new Error("Plugin extension values must be objects.");
				extensions.set(namespace, structuredClone(entry as Record<string, unknown>));
			}
		}
	}
	return {
		name: root["name"],
		...(optionalString(root["version"], "version") === undefined ? {} : { version: root["version"] as string }),
		...(optionalString(root["description"], "description") === undefined
			? {}
			: { description: root["description"] as string }),
		...(author === undefined ? {} : { author }),
		...(optionalString(root["homepage"], "homepage") === undefined ? {} : { homepage: root["homepage"] as string }),
		...(optionalString(root["repository"], "repository") === undefined
			? {}
			: { repository: root["repository"] as string }),
		...(optionalString(root["license"], "license") === undefined ? {} : { license: root["license"] as string }),
		keywords,
		extensions,
	};
}

function expandPluginVariables(value: string, root: string, data: string): string {
	return value.replaceAll("${PLUGIN_ROOT}", root).replaceAll("${PLUGIN_DATA}", data);
}

async function resolveConfiguredDirectory(value: string, root: string, data: string): Promise<string> {
	let base: string;
	let suffix: string;
	if (value === "${PLUGIN_ROOT}" || value.startsWith("${PLUGIN_ROOT}/")) {
		base = root;
		suffix = value.slice("${PLUGIN_ROOT}".length).replace(/^\//u, "");
	} else if (value === "${PLUGIN_DATA}" || value.startsWith("${PLUGIN_DATA}/")) {
		base = data;
		suffix = value.slice("${PLUGIN_DATA}".length).replace(/^\//u, "");
	} else if (value.startsWith("./")) {
		base = root;
		suffix = value.slice(2);
	} else {
		throw new Error("Plugin working directory is not rooted in package or data storage.");
	}
	const target = resolve(base, ...suffix.split("/").filter(Boolean));
	if (!isWithin(base, target)) throw new Error("Plugin working directory escapes its root.");
	if (base === data) await mkdir(target, { recursive: true });
	return containedRealPath(base, target, "directory");
}

async function parseMcpServers(
	value: unknown,
	root: string,
	data: string | undefined,
	manifest: PortableGamePluginManifest,
	options: PortableGamePluginLoadOptions,
	limits: Limits,
	add: (diagnostic: GamePluginDiagnostic) => void,
): Promise<readonly GameMcpServer[]> {
	const document = objectValue(value, "MCP configuration must be an object.");
	if (Object.keys(document).some((key) => key !== "$schema" && key !== "mcpServers"))
		throw new Error("MCP configuration contains an unknown top-level field.");
	if (document["$schema"] !== mcpSchema) throw new Error("MCP configuration schema is unsupported.");
	const entries = objectValue(document["mcpServers"], "MCP servers must be an object.");
	if (Object.keys(entries).length > limits.maximumMcpServers)
		throw new RangeError("MCP server count exceeds its limit.");
	const result: GameMcpServer[] = [];
	for (const [id, raw] of Object.entries(entries)) {
		try {
			if (!serverName.test(id)) throw new Error("MCP server id is invalid.");
			const server = objectValue(raw, "MCP server entry must be an object.");
			const type = server["type"];
			if (type === "stdio") {
				if (options.allowStdio === false || data === undefined) throw new Error("MCP stdio is disabled by the host.");
				if (Object.keys(server).some((key) => !new Set(["type", "command", "args", "env", "cwd"]).has(key)))
					throw new Error("MCP stdio entry contains an unknown field.");
				if (typeof server["command"] !== "string" || !server["command"] || server["command"].includes("\0"))
					throw new Error("MCP stdio command is invalid.");
				let command = server["command"];
				if (command.startsWith("./")) command = await containedRealPath(root, resolve(root, command.slice(2)), "file");
				else if (!/^[^\s/\\]+$/u.test(command)) throw new Error("MCP stdio command must be one executable token.");
				const args = server["args"] ?? [];
				if (
					!Array.isArray(args) ||
					args.length > limits.maximumArgumentsPerServer ||
					args.some((item) => typeof item !== "string")
				)
					throw new Error("MCP stdio arguments are invalid.");
				const configuredEnvironment = server["env"] ?? {};
				const environmentObject = objectValue(configuredEnvironment, "MCP stdio environment must be an object.");
				if (Object.keys(environmentObject).length > limits.maximumEnvironmentVariablesPerServer)
					throw new RangeError("MCP stdio environment exceeds its limit.");
				if (
					Object.keys(environmentObject).some(
						(name) => name.toUpperCase() === "PLUGIN_ROOT" || name.toUpperCase() === "PLUGIN_DATA",
					)
				)
					throw new Error("MCP stdio environment overrides a reserved variable.");
				const environment: Record<string, string> = { ...(options.baseEnvironment ?? {}) };
				for (const [name, item] of Object.entries(environmentObject)) {
					if (typeof item !== "string") throw new Error("MCP stdio environment values must be strings.");
					environment[name] = expandPluginVariables(item, root, data);
				}
				environment["PLUGIN_ROOT"] = root;
				environment["PLUGIN_DATA"] = data;
				const cwd =
					server["cwd"] === undefined
						? root
						: typeof server["cwd"] === "string"
							? await resolveConfiguredDirectory(server["cwd"], root, data)
							: (() => {
									throw new Error("MCP stdio working directory is invalid.");
								})();
				result.push({
					id: `${manifest.name}.${id}`,
					toolPrefix: `plugin_${manifest.name.replaceAll(".", "_")}_${id}_`,
					connect: () =>
						connectStdioGameMcp({
							command,
							arguments: (args as string[]).map((item) => expandPluginVariables(item, root, data)),
							environment,
							workingDirectory: cwd,
						}),
				});
				continue;
			}
			if (type === "streamable-http") {
				if (options.allowHttp === false) throw new Error("MCP HTTP is disabled by the host.");
				if (Object.keys(server).some((key) => !new Set(["type", "url", "headers"]).has(key)))
					throw new Error("MCP HTTP entry contains an unknown field.");
				if (typeof server["url"] !== "string") throw new Error("MCP HTTP URL is invalid.");
				const endpoint = new URL(server["url"]);
				if (endpoint.hash) throw new Error("MCP HTTP URL cannot contain a fragment.");
				const configuredHeaders = objectValue(server["headers"] ?? {}, "MCP HTTP headers must be an object.");
				if (Object.keys(configuredHeaders).length > limits.maximumHeadersPerServer)
					throw new RangeError("MCP HTTP headers exceed their limit.");
				const headers = new Map<string, { name: string; value: string }>();
				for (const [name, item] of Object.entries(configuredHeaders)) {
					if (typeof item !== "string") throw new Error("MCP HTTP header values must be strings.");
					const normalized = name.toLowerCase();
					if (headers.has(normalized)) throw new Error("MCP HTTP headers contain a case-insensitive duplicate.");
					headers.set(normalized, { name, value: item });
				}
				for (const [name, item] of Object.entries(options.httpHeaders?.[id] ?? {}))
					headers.set(name.toLowerCase(), { name, value: item });
				const headerObject = Object.fromEntries([...headers.values()].map((item) => [item.name, item.value]));
				result.push({
					id: `${manifest.name}.${id}`,
					toolPrefix: `plugin_${manifest.name.replaceAll(".", "_")}_${id}_`,
					connect: () =>
						connectHttpGameMcp({
							endpoint,
							headers: headerObject,
							allowInsecureLocalhost: endpoint.protocol === "http:",
						}),
				});
				continue;
			}
			if (type === "sse") throw new Error("Legacy SSE MCP is not supported.");
			throw new Error("MCP transport is unsupported.");
		} catch {
			add({
				severity: "error",
				code: "mcp.server-skipped",
				component: "mcp",
				message: `MCP server '${id}' was skipped because its configuration is invalid or disabled.`,
			});
		}
	}
	return result;
}

/** Load one portable Skill/MCP package without executing package-owned code. */
export async function loadPortableGamePlugin(
	directory: string,
	options: PortableGamePluginLoadOptions = {},
): Promise<PortableGamePlugin> {
	if (!directory) throw new TypeError("A plugin directory is required.");
	const limits: Limits = {
		maximumManifestBytes: boundedInteger(
			options.maximumManifestBytes,
			1_000_000,
			2,
			100_000_000,
			"maximumManifestBytes",
		),
		maximumMcpBytes: boundedInteger(options.maximumMcpBytes, 2_000_000, 2, 100_000_000, "maximumMcpBytes"),
		maximumSkills: boundedInteger(options.maximumSkills, 1_000, 0, 100_000, "maximumSkills"),
		maximumMcpServers: boundedInteger(options.maximumMcpServers, 256, 0, 10_000, "maximumMcpServers"),
		maximumArgumentsPerServer: boundedInteger(
			options.maximumArgumentsPerServer,
			1_024,
			0,
			100_000,
			"maximumArgumentsPerServer",
		),
		maximumEnvironmentVariablesPerServer: boundedInteger(
			options.maximumEnvironmentVariablesPerServer,
			1_024,
			0,
			100_000,
			"maximumEnvironmentVariablesPerServer",
		),
		maximumHeadersPerServer: boundedInteger(options.maximumHeadersPerServer, 64, 0, 10_000, "maximumHeadersPerServer"),
		maximumDiagnostics: boundedInteger(options.maximumDiagnostics, 1_024, 0, 100_000, "maximumDiagnostics"),
	};
	const diagnostics = diagnosticBuffer(limits.maximumDiagnostics);
	const root = await realpath(resolve(directory));
	if (!(await stat(root)).isDirectory()) throw new Error("Plugin root is not a directory.");
	const manifestPath = await containedRealPath(root, resolve(root, "plugin.json"), "file");
	const manifest = parseManifest(await readJson(manifestPath, limits.maximumManifestBytes), diagnostics.add);

	let data: string | undefined;
	if (options.dataDirectory !== undefined) {
		const dataRoot = resolve(options.dataDirectory);
		await mkdir(dataRoot, { recursive: true });
		const canonicalDataRoot = await realpath(dataRoot);
		const pluginData = resolve(canonicalDataRoot, manifest.name);
		if (!isWithin(canonicalDataRoot, pluginData)) throw new Error("Plugin data path escapes its root.");
		await mkdir(pluginData, { recursive: true });
		data = await realpath(pluginData);
	}

	let skills: GameSkillSource | undefined;
	if (options.loadSkills !== false) {
		const skillsPath = resolve(root, "skills");
		try {
			const information = await lstat(skillsPath);
			if (!information.isDirectory() || information.isSymbolicLink())
				throw new Error("Skills component is not a real directory.");
			const sources: GameSkillSource[] = [];
			for (const entry of (await readdir(skillsPath, { withFileTypes: true })).sort((left, right) =>
				left.name.localeCompare(right.name),
			)) {
				if (!entry.isDirectory() || entry.isSymbolicLink()) continue;
				if (sources.length >= limits.maximumSkills) throw new RangeError("Plugin skill count exceeds its limit.");
				try {
					const source = await DirectoryGameSkillSource.open(resolve(skillsPath, entry.name), {
						maximumSkills: 1,
						maximumDirectories: 1,
						maximumDepth: 0,
					});
					if ((await source.list()).length === 1) sources.push(source);
				} catch {
					diagnostics.add({
						severity: "error",
						code: "skills.entry-skipped",
						component: "skills",
						message: `Skill '${entry.name}' was skipped because it is invalid.`,
					});
				}
			}
			if (sources.length > 0) skills = new CompositeGameSkillSource(sources, limits.maximumSkills);
		} catch (error) {
			if ((error as NodeJS.ErrnoException).code !== "ENOENT")
				diagnostics.add({
					severity: "error",
					code: "skills.component-disabled",
					component: "skills",
					message: "The Skills component was disabled because its fixed package path is invalid.",
				});
		}
	}

	let mcp: GameMcpToolBridge | undefined;
	if (options.loadMcp !== false) {
		const mcpPath = resolve(root, "mcp.json");
		try {
			const path = await containedRealPath(root, mcpPath, "file");
			const servers = await parseMcpServers(
				await readJson(path, limits.maximumMcpBytes),
				root,
				data,
				manifest,
				options,
				limits,
				diagnostics.add,
			);
			if (servers.length > 0) mcp = new GameMcpToolBridge({ ...options.mcpOptions, servers });
		} catch (error) {
			if ((error as NodeJS.ErrnoException).code !== "ENOENT")
				diagnostics.add({
					severity: "error",
					code: "mcp.component-disabled",
					component: "mcp",
					message: "The MCP component was disabled because its package configuration is invalid.",
				});
		}
	}

	return {
		rootDirectory: root,
		...(data === undefined ? {} : { dataDirectory: data }),
		manifest,
		diagnostics: diagnostics.values,
		...(skills === undefined ? {} : { skills }),
		...(mcp === undefined ? {} : { mcp }),
	};
}
