import { mkdir, realpath, stat } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import type { OAuthClientProvider } from "@modelcontextprotocol/sdk/client/auth.js";
import { assertOpaqueIdentifier } from "@opengameagent/protocol";
import type { DirectoryAgentPluginMcpServer } from "./agent-plugin-types.js";
import { expandAgentPluginValue } from "./agent-plugin-variables.js";
import { GameMcpError } from "./mcp-errors.js";
import {
	createGameMcpStdioTransportFactory,
	createGameMcpStreamableHttpTransportFactory,
	type GameMcpHttpReconnectionPolicy,
} from "./mcp-transports.js";
import type { GameMcpServerDefinition } from "./mcp-types.js";

const encoder = new TextEncoder();
const pluginDataVariable = "\u0024{PLUGIN_DATA}";

interface AgentPluginMcpAdmissionBase {
	readonly serverId: string;
	readonly description: string;
	readonly requirement: "required" | "optional";
}

export interface AgentPluginStdioMcpAdmission extends AgentPluginMcpAdmissionBase {
	readonly transport: {
		readonly type: "stdio";
		/** Host-selected ambient environment. Package `env` entries overlay it. */
		readonly baseEnv: Readonly<Record<string, string>>;
		readonly stderr: "inherit" | "ignore";
		readonly maxMessageBytes: number;
	};
}

export interface AgentPluginStreamableHttpMcpAdmission extends AgentPluginMcpAdmissionBase {
	readonly transport: {
		readonly type: "streamable-http";
		/** Host headers replace package headers case-insensitively. */
		readonly headers?: ConstructorParameters<typeof Headers>[0];
		readonly authProvider?: OAuthClientProvider;
		readonly fetch?: typeof fetch;
		readonly maxHttpResponseBytes: number;
		readonly maxSseEventBytes: number;
		readonly reconnection: GameMcpHttpReconnectionPolicy;
	};
}

export type AgentPluginMcpAdmission = AgentPluginStdioMcpAdmission | AgentPluginStreamableHttpMcpAdmission;

/** Convert one validated declaration into a connection only after explicit host admission. */
export function createAgentPluginMcpServerDefinition(
	server: Readonly<DirectoryAgentPluginMcpServer>,
	admission: Readonly<AgentPluginMcpAdmission>,
): GameMcpServerDefinition {
	validateAdmission(server, admission);
	if (server.configuration.type === "stdio" && admission.transport.type === "stdio") {
		const transport = admission.transport;
		const baseEnv = { ...transport.baseEnv };
		const configuration = server.configuration;
		return {
			serverId: admission.serverId,
			description: admission.description,
			requirement: admission.requirement,
			createTransport: async (signal) => {
				signal.throwIfAborted();
				const pluginRootIdentity = await resolveStableDirectoryIdentity(server.pluginAddressedRoot, signal);
				assertPluginRootIdentity(server, pluginRootIdentity);
				const pluginRoot = pluginRootIdentity.canonicalPath;
				await mkdir(server.pluginDataPath, { recursive: true });
				signal.throwIfAborted();
				const pluginData = await resolveStableDirectory(server.pluginDataPath, signal);
				if (pathsOverlap(pluginRoot, pluginData)) throw new GameMcpError("transport_rejected");

				let command: string;
				if (configuration.command.kind === "bare") {
					command = configuration.command.value;
				} else {
					const addressed = resolveForwardPath(pluginRoot, configuration.command.relativePath.slice(2));
					command = await resolveStableFile(addressed, pluginRoot, signal);
					if (!samePath(command, configuration.command.canonicalPath)) {
						throw new GameMcpError("transport_rejected");
					}
				}

				const args = configuration.args.map((value) =>
					expandBounded(value, pluginRoot, pluginData, server.maxExpandedValueBytes),
				);
				const packageEnv = Object.fromEntries(
					Object.entries(configuration.env).map(([key, value]) => [
						key,
						expandBounded(value, pluginRoot, pluginData, server.maxExpandedValueBytes),
					]),
				);
				const env = mergeEnvironment(baseEnv, packageEnv, pluginRoot, pluginData);
				const cwd = await resolveCwd(configuration.cwd, pluginRoot, pluginData, server.maxExpandedValueBytes, signal);
				assertPluginRootIdentity(server, await resolveStableDirectoryIdentity(server.pluginAddressedRoot, signal));
				return createGameMcpStdioTransportFactory({
					command,
					args,
					cwd,
					env,
					stderr: transport.stderr,
					maxMessageBytes: transport.maxMessageBytes,
				})(signal);
			},
		};
	}

	if (server.configuration.type === "streamable-http" && admission.transport.type === "streamable-http") {
		const packageHeaders = new Headers(server.configuration.headers);
		const hostHeaders =
			admission.transport.headers === undefined ? undefined : new Headers(admission.transport.headers);
		if (hostHeaders) {
			hostHeaders.forEach((value, key) => {
				packageHeaders.set(key, value);
			});
		}
		const factory = createGameMcpStreamableHttpTransportFactory({
			url: server.configuration.url,
			headers: packageHeaders,
			...(admission.transport.authProvider === undefined ? {} : { authProvider: admission.transport.authProvider }),
			...(admission.transport.fetch === undefined ? {} : { fetch: admission.transport.fetch }),
			maxHttpResponseBytes: admission.transport.maxHttpResponseBytes,
			maxSseEventBytes: admission.transport.maxSseEventBytes,
			reconnection: { ...admission.transport.reconnection },
		});
		return {
			serverId: admission.serverId,
			description: admission.description,
			requirement: admission.requirement,
			createTransport: factory,
		};
	}
	throw new GameMcpError("invalid_configuration");
}

function validateAdmission(
	server: Readonly<DirectoryAgentPluginMcpServer>,
	admission: Readonly<AgentPluginMcpAdmission>,
): void {
	if (
		typeof server !== "object" ||
		server === null ||
		typeof server.pluginAddressedRoot !== "string" ||
		typeof server.pluginRootIdentity !== "object" ||
		server.pluginRootIdentity === null ||
		typeof server.pluginRootIdentity.canonicalPath !== "string" ||
		typeof server.pluginRootIdentity.device !== "string" ||
		!/^[0-9]+$/u.test(server.pluginRootIdentity.device) ||
		typeof server.pluginRootIdentity.inode !== "string" ||
		!/^[0-9]+$/u.test(server.pluginRootIdentity.inode) ||
		typeof server.pluginDataPath !== "string" ||
		!isPositiveSafeInteger(server.maxExpandedValueBytes) ||
		typeof admission !== "object" ||
		admission === null ||
		typeof admission.serverId !== "string" ||
		typeof admission.description !== "string" ||
		(admission.requirement !== "required" && admission.requirement !== "optional") ||
		typeof admission.transport !== "object" ||
		admission.transport === null ||
		server.configuration.type !== admission.transport.type
	) {
		throw new GameMcpError("invalid_configuration");
	}
	try {
		assertOpaqueIdentifier(admission.serverId);
	} catch {
		throw new GameMcpError("invalid_configuration");
	}
	if (admission.transport.type === "stdio") {
		if (
			!isRecordOfStrings(admission.transport.baseEnv) ||
			(admission.transport.stderr !== "inherit" && admission.transport.stderr !== "ignore") ||
			!isPositiveSafeInteger(admission.transport.maxMessageBytes)
		) {
			throw new GameMcpError("invalid_configuration");
		}
	}
}

function mergeEnvironment(
	base: Readonly<Record<string, string>>,
	configured: Readonly<Record<string, string>>,
	pluginRoot: string,
	pluginData: string,
): Record<string, string> {
	const result = Object.assign(Object.create(null) as Record<string, string>, base);
	for (const [key, value] of Object.entries(configured)) setEnvironmentValue(result, key, value);
	setEnvironmentValue(result, "PLUGIN_ROOT", pluginRoot);
	setEnvironmentValue(result, "PLUGIN_DATA", pluginData);
	return result;
}

function setEnvironmentValue(target: Record<string, string>, key: string, value: string): void {
	if (process.platform === "win32") {
		const normalized = key.toLowerCase();
		for (const existing of Object.keys(target)) {
			if (existing.toLowerCase() === normalized) delete target[existing];
		}
	}
	target[key] = value;
}

async function resolveCwd(
	template: string | undefined,
	pluginRoot: string,
	pluginData: string,
	maximum: number,
	signal: AbortSignal,
): Promise<string> {
	if (template === undefined) return pluginRoot;
	const expanded = template.startsWith("./")
		? resolveForwardPath(pluginRoot, template.slice(2))
		: expandBounded(template, pluginRoot, pluginData, maximum);
	if (encoder.encode(expanded).byteLength > maximum) throw new GameMcpError("transport_rejected");
	const boundary = template.startsWith(pluginDataVariable) ? pluginData : pluginRoot;
	const canonical = await resolveStableDirectory(expanded, signal);
	if (!isContained(boundary, canonical)) throw new GameMcpError("transport_rejected");
	return canonical;
}

async function resolveStableDirectory(path: string, signal: AbortSignal): Promise<string> {
	return (await resolveStableDirectoryIdentity(path, signal)).canonicalPath;
}

async function resolveStableDirectoryIdentity(
	path: string,
	signal: AbortSignal,
): Promise<{ readonly canonicalPath: string; readonly device: string; readonly inode: string }> {
	signal.throwIfAborted();
	try {
		const canonical = resolve(await realpath(path));
		const info = await stat(path, { bigint: true });
		const canonicalAfter = resolve(await realpath(path));
		const infoAfter = await stat(path, { bigint: true });
		signal.throwIfAborted();
		if (
			!info.isDirectory() ||
			!infoAfter.isDirectory() ||
			canonicalAfter !== canonical ||
			infoAfter.dev !== info.dev ||
			infoAfter.ino !== info.ino
		) {
			throw new Error("not_stable_directory");
		}
		return { canonicalPath: canonical, device: String(info.dev), inode: String(info.ino) };
	} catch {
		signal.throwIfAborted();
		throw new GameMcpError("transport_rejected");
	}
}

function assertPluginRootIdentity(
	server: Readonly<DirectoryAgentPluginMcpServer>,
	identity: { readonly canonicalPath: string; readonly device: string; readonly inode: string },
): void {
	if (
		!samePath(identity.canonicalPath, server.pluginRootIdentity.canonicalPath) ||
		identity.device !== server.pluginRootIdentity.device ||
		identity.inode !== server.pluginRootIdentity.inode
	) {
		throw new GameMcpError("transport_rejected");
	}
}

async function resolveStableFile(path: string, boundary: string, signal: AbortSignal): Promise<string> {
	signal.throwIfAborted();
	try {
		const canonical = resolve(await realpath(path));
		const info = await stat(canonical);
		signal.throwIfAborted();
		if (!info.isFile() || !isContained(boundary, canonical)) throw new Error("invalid_file");
		return canonical;
	} catch {
		signal.throwIfAborted();
		throw new GameMcpError("transport_rejected");
	}
}

function expandBounded(value: string, pluginRoot: string, pluginData: string, maximum: number): string {
	const expanded = expandAgentPluginValue(value, pluginRoot, pluginData);
	if (encoder.encode(expanded).byteLength > maximum) throw new GameMcpError("transport_rejected");
	return expanded;
}

function resolveForwardPath(base: string, suffix: string): string {
	return resolve(base, ...suffix.split("/").filter((segment) => segment.length > 0));
}

function isRecordOfStrings(value: unknown): value is Record<string, string> {
	return (
		typeof value === "object" &&
		value !== null &&
		!Array.isArray(value) &&
		Object.entries(value).every(
			([key, entry]) =>
				key.length > 0 &&
				!key.includes("=") &&
				!key.includes("\u0000") &&
				typeof entry === "string" &&
				!entry.includes("\u0000"),
		)
	);
}

function pathsOverlap(left: string, right: string): boolean {
	return isContained(left, right) || isContained(right, left);
}

function isContained(base: string, target: string): boolean {
	const child = relative(resolve(base), resolve(target));
	return child === "" || (child !== ".." && !child.startsWith(`..${sep}`) && !isAbsolute(child));
}

function samePath(left: string, right: string): boolean {
	return relative(left, right) === "" && relative(right, left) === "";
}

function isPositiveSafeInteger(value: number): boolean {
	return Number.isSafeInteger(value) && value > 0;
}
