import { createHash } from "node:crypto";
import type { Stats } from "node:fs";
import { lstat, realpath, stat } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { assertOpaqueIdentifier, canonicalizeJsonValue, parseStrictJsonText } from "@opengameagent/protocol";
import { DirectoryAgentPluginError } from "./agent-plugin-errors.js";
import {
	AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES,
	agentPluginPackagePublicationStatePath,
} from "./agent-plugin-package-paths.js";
import {
	AGENT_PLUGIN_MANIFEST_SCHEMA_V1,
	AGENT_PLUGIN_MCP_SCHEMA_V1,
	type AgentPluginStdioCommand,
	type AgentPluginStdioMcpServer,
	type AgentPluginStreamableHttpMcpServer,
	type AgentPluginV1Manifest,
	type DirectoryAgentPluginCatalogOptions,
	type DirectoryAgentPluginCatalogSnapshot,
	type DirectoryAgentPluginDiagnostic,
	type DirectoryAgentPluginInstallation,
	type DirectoryAgentPluginLimits,
	type DirectoryAgentPluginMcpServer,
	type DirectoryAgentPluginSummary,
	isAgentPluginName,
} from "./agent-plugin-types.js";
import { expandAgentPluginValue } from "./agent-plugin-variables.js";
import { BoundedFileReadError, readStableTextFile } from "./bounded-file.js";
import { isAllowedMcpHttpUrl } from "./mcp-url.js";

const encoder = new TextEncoder();
const pluginRootVariable = "\u0024{PLUGIN_ROOT}";
const pluginDataVariable = "\u0024{PLUGIN_DATA}";
const manifestFields = new Set([
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
const authorFields = new Set(["name", "email", "url"]);
const stdioFields = new Set(["type", "command", "args", "env", "cwd"]);
const httpFields = new Set(["type", "url", "headers"]);

interface ResolvedInstallation extends DirectoryAgentPluginInstallation {
	readonly rootPath: string;
	readonly dataPath: string;
}

interface ScanBudget {
	readBytes: number;
}

interface RootIdentity {
	readonly device: string;
	readonly inode: string;
}

interface LoadedPlugin {
	readonly summary: DirectoryAgentPluginSummary;
	readonly skillRoot?: { readonly rootId: string; readonly path: string };
	readonly mcpServers: readonly DirectoryAgentPluginMcpServer[];
	readonly revisionInput: Record<string, unknown>;
}

/** A bounded, non-executing Agent Plugins 1.0 directory loader. */
export class DirectoryAgentPluginCatalog {
	private readonly installations: readonly ResolvedInstallation[];
	private readonly limits: DirectoryAgentPluginLimits;

	constructor(options: DirectoryAgentPluginCatalogOptions) {
		validateOptions(options);
		this.installations = options.installations.map((installation) => ({
			...installation,
			rootPath: resolve(options.cwd, installation.rootPath),
			dataPath: resolve(options.cwd, installation.dataPath),
		}));
		this.limits = { ...options.limits };
	}

	async load(signal: AbortSignal): Promise<DirectoryAgentPluginCatalogSnapshot> {
		signal.throwIfAborted();
		const diagnostics: DirectoryAgentPluginDiagnostic[] = [];
		const loaded: LoadedPlugin[] = [];
		const budget: ScanBudget = { readBytes: 0 };
		const canonicalRoots: string[] = [];

		for (const installation of this.installations) {
			signal.throwIfAborted();
			let canonicalRoot: string;
			let rootIdentity: RootIdentity;
			try {
				canonicalRoot = resolve(await realpath(installation.rootPath));
				const info = await stat(canonicalRoot, { bigint: true });
				signal.throwIfAborted();
				if (!info.isDirectory()) throw new Error("not_directory");
				rootIdentity = { device: String(info.dev), inode: String(info.ino) };
			} catch {
				signal.throwIfAborted();
				pushDiagnostic(diagnostics, this.limits, {
					installationId: installation.installationId,
					code: "plugin_unavailable",
				});
				continue;
			}
			if (canonicalRoots.some((root) => isContained(root, canonicalRoot) || isContained(canonicalRoot, root))) {
				throw new DirectoryAgentPluginError("invalid_configuration");
			}
			canonicalRoots.push(canonicalRoot);
			const publicationState = await readPublicationState(canonicalRoot, signal);
			if (!publicationStateMatchesRoot(publicationState, rootIdentity)) {
				pushDiagnostic(diagnostics, this.limits, {
					installationId: installation.installationId,
					code: "plugin_unavailable",
				});
				continue;
			}
			const plugin = await this.loadPlugin(installation, canonicalRoot, rootIdentity, budget, diagnostics, signal);
			const publicationStateAfter = await readPublicationState(canonicalRoot, signal);
			if (plugin && publicationStateAfter === publicationState) loaded.push(plugin);
			else if (plugin) {
				pushDiagnostic(diagnostics, this.limits, {
					installationId: installation.installationId,
					code: "plugin_unavailable",
				});
			}
		}

		const plugins = loaded.map((plugin) => plugin.summary);
		const skillRoots = loaded.flatMap((plugin) => (plugin.skillRoot ? [plugin.skillRoot] : []));
		const mcpServers = loaded.flatMap((plugin) => plugin.mcpServers);
		const revision = sha256(
			canonicalizeJsonValue({
				plugins: loaded.map((plugin) => plugin.revisionInput),
				diagnostics,
			}),
		);
		return deepFreeze({ revision, plugins, skillRoots, mcpServers, diagnostics });
	}

	private async loadPlugin(
		installation: ResolvedInstallation,
		canonicalRoot: string,
		rootIdentity: RootIdentity,
		budget: ScanBudget,
		diagnostics: DirectoryAgentPluginDiagnostic[],
		signal: AbortSignal,
	): Promise<LoadedPlugin | undefined> {
		const manifestPath = resolve(installation.rootPath, "plugin.json");
		const manifestText = await this.readComponent(
			manifestPath,
			canonicalRoot,
			this.limits.maxManifestBytes,
			budget,
			signal,
		);
		if (manifestText === undefined) {
			pushDiagnostic(diagnostics, this.limits, {
				installationId: installation.installationId,
				code: "manifest_invalid",
			});
			return;
		}

		let manifestValue: unknown;
		try {
			manifestValue = parseStrictJsonText(manifestText, this.limits.maxJsonDepth);
		} catch {
			pushDiagnostic(diagnostics, this.limits, {
				installationId: installation.installationId,
				code: "manifest_invalid",
			});
			return;
		}
		const manifest = parseManifest(manifestValue, installation.installationId, diagnostics, this.limits);
		if (manifest === "unsupported") return;
		if (manifest === undefined) return;

		const skillsPath = resolve(installation.rootPath, "skills");
		const skillRoot = await this.discoverSkills(
			installation.installationId,
			skillsPath,
			canonicalRoot,
			diagnostics,
			signal,
		);
		const mcp = await this.discoverMcp(
			installation,
			canonicalRoot,
			rootIdentity,
			manifest,
			budget,
			diagnostics,
			signal,
		);
		const mcpServers = mcp?.servers ?? [];
		const summary: DirectoryAgentPluginSummary = {
			installationId: installation.installationId,
			manifest,
			hasSkills: skillRoot !== undefined,
			mcpServerNames: mcpServers.map((server) => server.serverName),
		};
		return {
			summary,
			...(skillRoot === undefined ? {} : { skillRoot }),
			mcpServers,
			revisionInput: {
				installationId: installation.installationId,
				canonicalRoot,
				dataPath: installation.dataPath,
				manifest,
				hasSkills: skillRoot !== undefined,
				mcp: mcp?.revisionInput ?? null,
			},
		};
	}

	private async discoverSkills(
		installationId: string,
		addressedPath: string,
		canonicalRoot: string,
		diagnostics: DirectoryAgentPluginDiagnostic[],
		signal: AbortSignal,
	): Promise<{ readonly rootId: string; readonly path: string } | undefined> {
		signal.throwIfAborted();
		let info: Stats;
		try {
			info = await stat(addressedPath);
			signal.throwIfAborted();
		} catch (error) {
			signal.throwIfAborted();
			if (isMissing(error)) return;
			pushDiagnostic(diagnostics, this.limits, { installationId, code: "skills_invalid" });
			return;
		}
		if (!info.isDirectory()) {
			pushDiagnostic(diagnostics, this.limits, { installationId, code: "skills_invalid" });
			return;
		}
		try {
			const canonical = resolve(await realpath(addressedPath));
			signal.throwIfAborted();
			if (!isContained(canonicalRoot, canonical)) throw new Error("root_escape");
		} catch {
			signal.throwIfAborted();
			pushDiagnostic(diagnostics, this.limits, { installationId, code: "skills_invalid" });
			return;
		}
		return { rootId: installationId, path: addressedPath };
	}

	private async discoverMcp(
		installation: ResolvedInstallation,
		canonicalRoot: string,
		rootIdentity: RootIdentity,
		manifest: AgentPluginV1Manifest,
		budget: ScanBudget,
		diagnostics: DirectoryAgentPluginDiagnostic[],
		signal: AbortSignal,
	): Promise<
		{ readonly servers: readonly DirectoryAgentPluginMcpServer[]; readonly revisionInput: unknown } | undefined
	> {
		const path = resolve(installation.rootPath, "mcp.json");
		let exists = true;
		try {
			const info = await stat(path);
			if (!info.isFile()) throw new Error("not_file");
		} catch (error) {
			signal.throwIfAborted();
			exists = false;
			if (!isMissing(error)) {
				pushDiagnostic(diagnostics, this.limits, {
					installationId: installation.installationId,
					code: "mcp_configuration_invalid",
				});
			}
		}
		if (!exists) return;

		const text = await this.readComponent(path, canonicalRoot, this.limits.maxMcpConfigurationBytes, budget, signal);
		if (text === undefined) {
			pushDiagnostic(diagnostics, this.limits, {
				installationId: installation.installationId,
				code: "mcp_configuration_invalid",
			});
			return;
		}
		let value: unknown;
		try {
			value = parseStrictJsonText(text, this.limits.maxJsonDepth);
		} catch {
			pushDiagnostic(diagnostics, this.limits, {
				installationId: installation.installationId,
				code: "mcp_configuration_invalid",
			});
			return;
		}
		if (
			!isRecord(value) ||
			Object.keys(value).some((key) => key !== "$schema" && key !== "mcpServers") ||
			value.$schema !== AGENT_PLUGIN_MCP_SCHEMA_V1 ||
			manifest.schema !== AGENT_PLUGIN_MANIFEST_SCHEMA_V1 ||
			!isRecord(value.mcpServers)
		) {
			pushDiagnostic(diagnostics, this.limits, {
				installationId: installation.installationId,
				code: "mcp_configuration_invalid",
			});
			return;
		}
		const entries = Object.entries(value.mcpServers).sort(([left], [right]) => compareStrings(left, right));
		if (entries.length > this.limits.maxMcpServersPerPlugin) {
			throw new DirectoryAgentPluginError("catalog_limit_exceeded");
		}
		const servers: DirectoryAgentPluginMcpServer[] = [];
		const revisionServers: unknown[] = [];
		for (const [serverName, raw] of entries) {
			signal.throwIfAborted();
			const validName = isValidServerName(serverName, this.limits.maxServerNameBytes);
			if (!validName || !isRecord(raw) || typeof raw.type !== "string") {
				pushDiagnostic(diagnostics, this.limits, {
					installationId: installation.installationId,
					code: "mcp_server_invalid",
					...(isSafeDiagnosticName(serverName) ? { serverName } : {}),
				});
				continue;
			}
			if (raw.type === "sse") {
				pushDiagnostic(diagnostics, this.limits, {
					installationId: installation.installationId,
					code: parseRemoteServer(raw, "sse") === undefined ? "mcp_server_invalid" : "mcp_transport_unsupported",
					serverName,
				});
				continue;
			}
			const configuration =
				raw.type === "stdio"
					? await parseStdioServer(raw, canonicalRoot, installation.dataPath, this.limits, signal)
					: raw.type === "streamable-http"
						? parseRemoteServer(raw, "streamable-http")
						: undefined;
			if (configuration === undefined) {
				pushDiagnostic(diagnostics, this.limits, {
					installationId: installation.installationId,
					code: "mcp_server_invalid",
					serverName,
				});
				continue;
			}
			const server: DirectoryAgentPluginMcpServer = {
				installationId: installation.installationId,
				pluginName: manifest.name,
				serverName,
				pluginAddressedRoot: installation.rootPath,
				pluginRootIdentity: {
					canonicalPath: canonicalRoot,
					device: rootIdentity.device,
					inode: rootIdentity.inode,
				},
				pluginDataPath: installation.dataPath,
				maxExpandedValueBytes: this.limits.maxExpandedValueBytes,
				configuration,
			};
			servers.push(server);
			revisionServers.push(server);
		}
		return { servers, revisionInput: revisionServers };
	}

	private async readComponent(
		addressedPath: string,
		canonicalRoot: string,
		maximum: number,
		budget: ScanBudget,
		signal: AbortSignal,
	): Promise<string | undefined> {
		try {
			const file = await readStableTextFile(addressedPath, maximum, signal);
			if (!isContained(canonicalRoot, file.canonicalPath)) return;
			const total = budget.readBytes + file.bytes;
			if (!Number.isSafeInteger(total) || total > this.limits.maxTotalReadBytes) {
				throw new DirectoryAgentPluginError("catalog_limit_exceeded");
			}
			budget.readBytes = total;
			return file.text;
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof DirectoryAgentPluginError) throw error;
			if (error instanceof BoundedFileReadError) return;
			throw new DirectoryAgentPluginError("catalog_incomplete");
		}
	}
}

function parseManifest(
	value: unknown,
	installationId: string,
	diagnostics: DirectoryAgentPluginDiagnostic[],
	limits: DirectoryAgentPluginLimits,
): AgentPluginV1Manifest | "unsupported" | undefined {
	if (!isRecord(value)) return invalidManifest(installationId, diagnostics, limits);
	for (const field of Object.keys(value)) {
		if (!manifestFields.has(field)) {
			pushDiagnostic(diagnostics, limits, { installationId, code: "manifest_field_ignored" });
		}
	}
	if (typeof value.$schema !== "string") {
		return invalidManifest(installationId, diagnostics, limits);
	}
	if (value.$schema !== AGENT_PLUGIN_MANIFEST_SCHEMA_V1) {
		pushDiagnostic(diagnostics, limits, { installationId, code: "manifest_schema_unsupported" });
		return "unsupported";
	}
	if (value.extensions !== undefined && !isRecord(value.extensions)) {
		pushDiagnostic(diagnostics, limits, { installationId, code: "manifest_extensions_ignored" });
	}
	if (typeof value.name !== "string" || !isAgentPluginName(value.name)) {
		return invalidManifest(installationId, diagnostics, limits);
	}
	for (const field of ["version", "description", "homepage", "repository", "license"] as const) {
		if (value[field] !== undefined && typeof value[field] !== "string") {
			return invalidManifest(installationId, diagnostics, limits);
		}
	}
	if (
		value.keywords !== undefined &&
		(!Array.isArray(value.keywords) || value.keywords.some((item) => typeof item !== "string"))
	) {
		return invalidManifest(installationId, diagnostics, limits);
	}
	let author: AgentPluginV1Manifest["author"];
	if (value.author !== undefined) {
		if (
			!isRecord(value.author) ||
			Object.keys(value.author).some((field) => !authorFields.has(field)) ||
			Object.values(value.author).some((field) => typeof field !== "string")
		) {
			return invalidManifest(installationId, diagnostics, limits);
		}
		author = {
			...(typeof value.author.name === "string" ? { name: value.author.name } : {}),
			...(typeof value.author.email === "string" ? { email: value.author.email } : {}),
			...(typeof value.author.url === "string" ? { url: value.author.url } : {}),
		};
	}
	return {
		schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1,
		name: value.name,
		...(typeof value.version === "string" ? { version: value.version } : {}),
		...(typeof value.description === "string" ? { description: value.description } : {}),
		...(author === undefined ? {} : { author }),
		...(typeof value.homepage === "string" ? { homepage: value.homepage } : {}),
		...(typeof value.repository === "string" ? { repository: value.repository } : {}),
		...(typeof value.license === "string" ? { license: value.license } : {}),
		keywords: value.keywords === undefined ? [] : [...value.keywords],
	};
}

function invalidManifest(
	installationId: string,
	diagnostics: DirectoryAgentPluginDiagnostic[],
	limits: DirectoryAgentPluginLimits,
): undefined {
	pushDiagnostic(diagnostics, limits, { installationId, code: "manifest_invalid" });
}

async function parseStdioServer(
	value: Record<string, unknown>,
	pluginRoot: string,
	pluginDataPath: string,
	limits: DirectoryAgentPluginLimits,
	signal: AbortSignal,
): Promise<AgentPluginStdioMcpServer | undefined> {
	if (
		Object.keys(value).some((field) => !stdioFields.has(field)) ||
		typeof value.command !== "string" ||
		value.command.length === 0 ||
		hasNul(value.command) ||
		(value.args !== undefined &&
			(!Array.isArray(value.args) || value.args.some((item) => typeof item !== "string" || hasNul(item)))) ||
		(value.env !== undefined &&
			(!isRecord(value.env) ||
				Object.entries(value.env).some(
					([key, item]) =>
						key.length === 0 || key.includes("=") || hasNul(key) || typeof item !== "string" || hasNul(item),
				) ||
				Object.hasOwn(value.env, "PLUGIN_ROOT") ||
				Object.hasOwn(value.env, "PLUGIN_DATA") ||
				hasEquivalentEnvironmentNames(Object.keys(value.env)))) ||
		(value.cwd !== undefined && (typeof value.cwd !== "string" || hasNul(value.cwd)))
	) {
		return;
	}
	const command = await parseCommand(value.command, pluginRoot, signal);
	if (!command) return;
	const args = value.args === undefined ? [] : ([...value.args] as string[]);
	const env = value.env === undefined ? {} : ({ ...value.env } as Record<string, string>);
	for (const item of [...args, ...Object.values(env), ...(typeof value.cwd === "string" ? [value.cwd] : [])]) {
		const expanded = expandAgentPluginValue(item, pluginRoot, pluginDataPath);
		if (encoder.encode(expanded).byteLength > limits.maxExpandedValueBytes) return;
	}
	if (typeof value.cwd === "string" && !(await isValidCwdTemplate(value.cwd, pluginRoot, pluginDataPath, signal))) {
		return;
	}
	return {
		type: "stdio",
		command,
		args,
		env,
		...(typeof value.cwd === "string" ? { cwd: value.cwd } : {}),
	};
}

async function parseCommand(
	value: string,
	pluginRoot: string,
	signal: AbortSignal,
): Promise<AgentPluginStdioCommand | undefined> {
	if (!value.startsWith("./")) {
		if (value.includes("/") || value.includes("\\") || hasControl(value)) return;
		return { kind: "bare", value };
	}
	if (value.includes("\\") || value.includes(":")) return;
	const addressed = resolveForwardPath(pluginRoot, value.slice(2));
	if (!isContained(pluginRoot, addressed)) return;
	try {
		const canonicalPath = resolve(await realpath(addressed));
		const info = await stat(canonicalPath);
		signal.throwIfAborted();
		if (!info.isFile() || !isContained(pluginRoot, canonicalPath)) return;
		return { kind: "plugin-relative", relativePath: value, canonicalPath };
	} catch {
		signal.throwIfAborted();
		return;
	}
}

function parseRemoteServer(
	value: Record<string, unknown>,
	type: "streamable-http" | "sse",
): AgentPluginStreamableHttpMcpServer | undefined {
	if (
		Object.keys(value).some((field) => !httpFields.has(field)) ||
		value.type !== type ||
		typeof value.url !== "string" ||
		value.url.length === 0 ||
		hasNul(value.url) ||
		(value.headers !== undefined &&
			(!isRecord(value.headers) || Object.values(value.headers).some((header) => typeof header !== "string")))
	) {
		return;
	}
	let url: URL;
	try {
		url = new URL(value.url);
	} catch {
		return;
	}
	if (!isAllowedMcpHttpUrl(url)) return;
	const headers = value.headers === undefined ? {} : ({ ...value.headers } as Record<string, string>);
	const seen = new Set<string>();
	for (const [name, headerValue] of Object.entries(headers)) {
		const normalized = name.toLowerCase();
		if (seen.has(normalized)) return;
		seen.add(normalized);
		try {
			new Headers([[name, headerValue]]);
		} catch {
			return;
		}
	}
	return { type: "streamable-http", url: url.href, headers };
}

async function isValidCwdTemplate(
	value: string,
	pluginRoot: string,
	pluginDataPath: string,
	signal: AbortSignal,
): Promise<boolean> {
	let addressed: string;
	let boundary: string;
	let requireExisting: boolean;
	if (value.startsWith("./")) {
		if (!isPortablePathText(value)) return false;
		addressed = resolveForwardPath(pluginRoot, value.slice(2));
		boundary = pluginRoot;
		requireExisting = true;
	} else if (value === pluginRootVariable || value.startsWith(`${pluginRootVariable}/`)) {
		if (!isPortablePathText(value.slice(pluginRootVariable.length))) return false;
		addressed = resolveForwardPath(pluginRoot, value.slice(pluginRootVariable.length + 1));
		boundary = pluginRoot;
		requireExisting = true;
	} else if (value === pluginDataVariable || value.startsWith(`${pluginDataVariable}/`)) {
		if (!isPortablePathText(value.slice(pluginDataVariable.length))) return false;
		addressed = resolveForwardPath(pluginDataPath, value.slice(pluginDataVariable.length + 1));
		boundary = pluginDataPath;
		requireExisting = false;
	} else {
		return false;
	}
	if (!isContained(boundary, addressed)) return false;
	try {
		const canonicalBoundary = resolve(await realpath(boundary));
		const canonical = resolve(await realpath(addressed));
		const info = await stat(canonical);
		signal.throwIfAborted();
		return info.isDirectory() && isContained(canonicalBoundary, canonical);
	} catch {
		signal.throwIfAborted();
		return !requireExisting;
	}
}

function resolveForwardPath(base: string, suffix: string): string {
	return resolve(base, ...suffix.split("/").filter((segment) => segment.length > 0));
}

function validateOptions(options: DirectoryAgentPluginCatalogOptions): void {
	const expectedLimits: readonly (keyof DirectoryAgentPluginLimits)[] = [
		"maxInstallations",
		"maxInstallationIdBytes",
		"maxAddressedPathBytes",
		"maxManifestBytes",
		"maxMcpConfigurationBytes",
		"maxTotalReadBytes",
		"maxJsonDepth",
		"maxDiagnostics",
		"maxMcpServersPerPlugin",
		"maxServerNameBytes",
		"maxExpandedValueBytes",
	];
	if (
		!isRecord(options) ||
		typeof options.cwd !== "string" ||
		options.cwd.length === 0 ||
		!Array.isArray(options.installations) ||
		!isRecord(options.limits) ||
		Object.keys(options.limits).length !== expectedLimits.length ||
		expectedLimits.some((field) => !Number.isSafeInteger(options.limits[field]) || options.limits[field] <= 0) ||
		options.installations.length > options.limits.maxInstallations
	) {
		throw new DirectoryAgentPluginError("invalid_configuration");
	}
	const ids = new Set<string>();
	const roots: string[] = [];
	const dataPaths: string[] = [];
	for (const installation of options.installations) {
		if (
			!isRecord(installation) ||
			typeof installation.installationId !== "string" ||
			typeof installation.rootPath !== "string" ||
			typeof installation.dataPath !== "string" ||
			installation.rootPath.length === 0 ||
			installation.dataPath.length === 0 ||
			ids.has(installation.installationId)
		) {
			throw new DirectoryAgentPluginError("invalid_configuration");
		}
		try {
			assertOpaqueIdentifier(installation.installationId);
		} catch {
			throw new DirectoryAgentPluginError("invalid_configuration");
		}
		if (encoder.encode(installation.installationId).byteLength > options.limits.maxInstallationIdBytes) {
			throw new DirectoryAgentPluginError("invalid_configuration");
		}
		const root = resolve(options.cwd, installation.rootPath);
		const data = resolve(options.cwd, installation.dataPath);
		if (
			encoder.encode(root).byteLength > options.limits.maxAddressedPathBytes ||
			encoder.encode(data).byteLength > options.limits.maxAddressedPathBytes ||
			roots.some((other) => isContained(other, root) || isContained(root, other)) ||
			dataPaths.some((other) => isContained(other, data) || isContained(data, other)) ||
			roots.some((other) => isContained(other, data) || isContained(data, other)) ||
			dataPaths.some((other) => isContained(other, root) || isContained(root, other)) ||
			isContained(root, data) ||
			isContained(data, root)
		) {
			throw new DirectoryAgentPluginError("invalid_configuration");
		}
		ids.add(installation.installationId);
		roots.push(root);
		dataPaths.push(data);
	}
}

async function readPublicationState(root: string, signal: AbortSignal): Promise<string> {
	const statePath = agentPluginPackagePublicationStatePath(root);
	let stateInfo: Stats;
	try {
		stateInfo = await lstat(statePath);
		signal.throwIfAborted();
	} catch (error) {
		signal.throwIfAborted();
		if (!isMissing(error)) return "unavailable";
		const identity = await readStableRootIdentity(root, signal);
		if (identity === undefined) return "unavailable";
		try {
			await lstat(statePath);
			return "unavailable";
		} catch (stateError) {
			signal.throwIfAborted();
			return isMissing(stateError) ? `legacy:${identity.device}:${identity.inode}` : "unavailable";
		}
	}
	if (
		!stateInfo.isFile() ||
		stateInfo.isSymbolicLink() ||
		(process.platform !== "win32" && (stateInfo.mode & 0o777) !== 0o444)
	) {
		return "unavailable";
	}
	let text: string;
	try {
		text = (await readStableTextFile(statePath, AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES, signal)).text;
	} catch {
		signal.throwIfAborted();
		return "unavailable";
	}
	const match = /^complete:([0-9a-f]{64}):([0-9]+):([0-9]+)$/u.exec(text);
	if (match === null) return "unavailable";
	const identity = await readStableRootIdentity(root, signal);
	if (identity === undefined || match[2] !== identity.device || match[3] !== identity.inode) {
		return "unavailable";
	}
	return `managed:${text}`;
}

async function readStableRootIdentity(root: string, signal: AbortSignal): Promise<RootIdentity | undefined> {
	try {
		const canonical = resolve(await realpath(root));
		const before = await stat(root, { bigint: true });
		const canonicalAfter = resolve(await realpath(root));
		const after = await stat(root, { bigint: true });
		signal.throwIfAborted();
		if (
			!before.isDirectory() ||
			!after.isDirectory() ||
			canonicalAfter !== canonical ||
			before.dev !== after.dev ||
			before.ino !== after.ino
		) {
			return;
		}
		return { device: String(before.dev), inode: String(before.ino) };
	} catch {
		signal.throwIfAborted();
		return;
	}
}

function publicationStateMatchesRoot(state: string, identity: RootIdentity): boolean {
	if (state === `legacy:${identity.device}:${identity.inode}`) return true;
	const match = /^managed:complete:[0-9a-f]{64}:([0-9]+):([0-9]+)$/u.exec(state);
	return match !== null && match[1] === identity.device && match[2] === identity.inode;
}

function pushDiagnostic(
	diagnostics: DirectoryAgentPluginDiagnostic[],
	limits: DirectoryAgentPluginLimits,
	diagnostic: DirectoryAgentPluginDiagnostic,
): void {
	if (diagnostics.length >= limits.maxDiagnostics) throw new DirectoryAgentPluginError("catalog_limit_exceeded");
	diagnostics.push(diagnostic);
}

function isValidServerName(value: string, maximumBytes: number): boolean {
	return encoder.encode(value).byteLength <= maximumBytes;
}

function isSafeDiagnosticName(value: string): boolean {
	return value.length > 0 && !hasControl(value);
}

function isPortablePathText(value: string): boolean {
	return !value.includes("\\") && !value.includes(":") && !hasNul(value);
}

function hasEquivalentEnvironmentNames(names: readonly string[]): boolean {
	if (process.platform !== "win32") return false;
	const normalized = names.map((name) => name.toLowerCase());
	return new Set(normalized).size !== normalized.length;
}

function hasControl(value: string): boolean {
	return [...value].some((character) => {
		const code = character.codePointAt(0) ?? 0;
		return code <= 31 || (code >= 127 && code <= 159);
	});
}

function hasNul(value: string): boolean {
	return value.includes("\u0000");
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isContained(base: string, target: string): boolean {
	const child = relative(resolve(base), resolve(target));
	return child === "" || (child !== ".." && !child.startsWith(`..${sep}`) && !isAbsolute(child));
}

function isMissing(error: unknown): boolean {
	return isRecord(error) && error.code === "ENOENT";
}

function sha256(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

function compareStrings(left: string, right: string): number {
	return left < right ? -1 : left > right ? 1 : 0;
}

function deepFreeze<T>(value: T): T {
	if (typeof value !== "object" || value === null || Object.isFrozen(value)) return value;
	Object.freeze(value);
	for (const child of Array.isArray(value) ? value : Object.values(value)) deepFreeze(child);
	return value;
}
