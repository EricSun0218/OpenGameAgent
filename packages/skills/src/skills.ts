import { createHash } from "node:crypto";
import { lstat, readdir, readFile, realpath, stat } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import type { GameInput, GameTool, JsonValue } from "@opengameagent/protocol";
import type { GamePostToolContextProvider, GameToolProvider } from "@opengameagent/runtime";
import { parseDocument } from "yaml";

export interface GameSkill {
	id: string;
	name: string;
	description: string;
	instructions: string;
	inputTypes: readonly string[];
	requiredTools: readonly string[];
	priority: number;
	version: string;
	digest: string;
	disableModelInvocation: boolean;
}

export interface GameSkillSource {
	list(signal?: AbortSignal): Promise<readonly GameSkill[]>;
	readResource?(skillId: string, relativePath: string, signal?: AbortSignal): Promise<string>;
}

export interface DirectoryGameSkillSourceOptions {
	maximumSkills?: number;
	maximumDirectories?: number;
	maximumDepth?: number;
	maximumSkillBytes?: number;
	maximumResourceBytes?: number;
}

export interface GameSkillSelectionContext {
	input: GameInput;
	availableTools: readonly string[];
	candidates: readonly GameSkill[];
}

export interface GameSkillExtensionOptions {
	source: GameSkillSource;
	select?: (context: GameSkillSelectionContext, signal: AbortSignal) => Promise<readonly string[]> | readonly string[];
	explicitSkillIds?: (input: GameInput, signal: AbortSignal) => Promise<readonly string[]> | readonly string[];
	maximumSelectedSkills?: number;
	maximumAdvertisedCharacters?: number;
	maximumLoadedCharacters?: number;
	contextName?: string;
	contextPriority?: number;
}

export interface GameSkillExtensionResources {
	postToolContextProvider: GamePostToolContextProvider;
	toolProvider: GameToolProvider;
}

interface SkillLocation {
	skill: GameSkill;
	directory: string;
}

interface SkillMetadata {
	name?: unknown;
	description?: unknown;
	"disable-model-invocation"?: unknown;
	"input-types"?: unknown;
	tools?: unknown;
	priority?: unknown;
	version?: unknown;
}

const portableId = /^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/;

function cloneSkill(skill: GameSkill): GameSkill {
	return structuredClone(skill);
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

function stringList(value: unknown, name: string, maximum: number): string[] {
	if (value === undefined) return [];
	if (!Array.isArray(value) || value.length > maximum || value.some((item) => typeof item !== "string"))
		throw new TypeError(`${name} must be a bounded string array.`);
	const result = [...new Set(value as string[])];
	for (const item of result) {
		if (item.length < 1 || item.length > 192) throw new RangeError(`${name} contains an invalid identifier.`);
	}
	return result.sort();
}

function parseSkill(text: string, directoryName: string): GameSkill {
	const normalized = text.replaceAll("\r\n", "\n");
	if (!normalized.startsWith("---\n")) throw new Error("Skill file requires YAML front matter.");
	const end = normalized.indexOf("\n---\n", 4);
	if (end < 0) throw new Error("Skill file has unterminated YAML front matter.");
	const metadataText = normalized.slice(4, end);
	const instructions = normalized.slice(end + 5).trim();
	const document = parseDocument(metadataText, { uniqueKeys: true });
	if (document.errors.length > 0) throw new Error("Skill file contains invalid YAML front matter.");
	const metadata = document.toJS({ maxAliasCount: 0 }) as SkillMetadata | null;
	if (!metadata || typeof metadata !== "object" || Array.isArray(metadata))
		throw new Error("Skill metadata must be an object.");
	const name = metadata.name === undefined ? directoryName : metadata.name;
	if (typeof name !== "string" || !portableId.test(name)) throw new Error("Skill name is not a portable identifier.");
	if (typeof metadata.description !== "string" || metadata.description.trim().length < 1)
		throw new Error("Skill description is required.");
	if (metadata.description.length > 1_024) throw new RangeError("Skill description is too long.");
	if (instructions.length < 1) throw new Error("Skill instructions are required.");
	if (metadata["disable-model-invocation"] !== undefined && typeof metadata["disable-model-invocation"] !== "boolean")
		throw new TypeError("disable-model-invocation must be boolean.");
	const priority = metadata.priority ?? 0;
	if (typeof priority !== "number" || !Number.isInteger(priority) || priority < -1_000_000 || priority > 1_000_000)
		throw new RangeError("Skill priority is invalid.");
	const version = metadata.version ?? "1";
	if (typeof version !== "string" || version.length < 1 || version.length > 128)
		throw new RangeError("Skill version is invalid.");
	const inputTypes = stringList(metadata["input-types"], "input-types", 128);
	const requiredTools = stringList(metadata.tools, "tools", 128);
	const digest = createHash("sha256")
		.update(
			JSON.stringify({
				name,
				description: metadata.description,
				instructions,
				inputTypes,
				requiredTools,
				priority,
				version,
				disableModelInvocation: metadata["disable-model-invocation"] === true,
			}),
		)
		.digest("base64url");
	return {
		id: name,
		name,
		description: metadata.description,
		instructions,
		inputTypes,
		requiredTools,
		priority,
		version,
		digest,
		disableModelInvocation: metadata["disable-model-invocation"] === true,
	};
}

async function readUtf8Bounded(path: string, maximumBytes: number, signal?: AbortSignal): Promise<string> {
	signal?.throwIfAborted();
	const information = await stat(path);
	if (!information.isFile() || information.size > maximumBytes)
		throw new RangeError("Skill resource exceeds its byte limit.");
	const bytes = await readFile(path, signal === undefined ? undefined : { signal });
	if (bytes.byteLength > maximumBytes) throw new RangeError("Skill resource exceeds its byte limit.");
	try {
		return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
	} catch {
		throw new Error("Skill resource is not valid UTF-8 text.");
	}
}

export class InMemoryGameSkillSource implements GameSkillSource {
	private readonly skills: readonly GameSkill[];
	constructor(skills: readonly GameSkill[], maximumSkills = 10_000) {
		if (!Number.isInteger(maximumSkills) || maximumSkills < 0 || maximumSkills > 100_000)
			throw new RangeError("maximumSkills is invalid.");
		if (skills.length > maximumSkills) throw new RangeError("Too many skills were registered.");
		const ids = new Set<string>();
		for (const skill of skills) {
			if (!portableId.test(skill.id) || ids.has(skill.id))
				throw new Error("Skill ids must be unique portable identifiers.");
			ids.add(skill.id);
		}
		this.skills = skills.map(cloneSkill);
	}
	async list(signal?: AbortSignal): Promise<readonly GameSkill[]> {
		signal?.throwIfAborted();
		return this.skills.map(cloneSkill);
	}
}

export class DirectoryGameSkillSource implements GameSkillSource {
	private readonly root: string;
	private readonly maximumSkills: number;
	private readonly maximumDirectories: number;
	private readonly maximumDepth: number;
	private readonly maximumSkillBytes: number;
	private readonly maximumResourceBytes: number;
	private locations = new Map<string, SkillLocation>();

	private constructor(root: string, options: DirectoryGameSkillSourceOptions) {
		this.root = root;
		this.maximumSkills = boundedInteger(options.maximumSkills, 1_000, 0, 100_000, "maximumSkills");
		this.maximumDirectories = boundedInteger(options.maximumDirectories, 10_000, 1, 1_000_000, "maximumDirectories");
		this.maximumDepth = boundedInteger(options.maximumDepth, 32, 0, 128, "maximumDepth");
		this.maximumSkillBytes = boundedInteger(
			options.maximumSkillBytes,
			1_100_000,
			1_024,
			100_000_000,
			"maximumSkillBytes",
		);
		this.maximumResourceBytes = boundedInteger(
			options.maximumResourceBytes,
			1_000_000,
			1_024,
			100_000_000,
			"maximumResourceBytes",
		);
	}

	static async open(
		directory: string,
		options: DirectoryGameSkillSourceOptions = {},
		signal?: AbortSignal,
	): Promise<DirectoryGameSkillSource> {
		if (!directory) throw new TypeError("A skill directory is required.");
		const root = await realpath(resolve(directory));
		const information = await lstat(root);
		if (!information.isDirectory() || information.isSymbolicLink())
			throw new Error("Skill root must be a real directory.");
		const source = new DirectoryGameSkillSource(root, options);
		await source.reload(signal);
		return source;
	}

	async reload(signal?: AbortSignal): Promise<void> {
		const pending: Array<{ directory: string; depth: number }> = [{ directory: this.root, depth: 0 }];
		const discovered: string[] = [];
		let scanned = 0;
		while (pending.length > 0) {
			signal?.throwIfAborted();
			const current = pending.pop();
			if (!current) break;
			scanned += 1;
			if (scanned > this.maximumDirectories) throw new RangeError("Skill directory scan exceeds its configured limit.");
			const entries = (await readdir(current.directory, { withFileTypes: true })).sort((left, right) =>
				left.name.localeCompare(right.name),
			);
			const descriptor = entries.find((entry) => entry.name === "SKILL.md");
			if (descriptor) {
				if (!descriptor.isFile() || descriptor.isSymbolicLink()) throw new Error("SKILL.md cannot be a symbolic link.");
				discovered.push(resolve(current.directory, descriptor.name));
				if (discovered.length > this.maximumSkills) throw new RangeError("Skill count exceeds its configured limit.");
				continue;
			}
			if (current.depth >= this.maximumDepth) continue;
			for (const entry of entries.toReversed()) {
				if (entry.name.startsWith(".") || entry.name === "node_modules") continue;
				if (entry.isSymbolicLink()) throw new Error("Skill directories cannot contain symbolic links.");
				if (entry.isDirectory())
					pending.push({ directory: resolve(current.directory, entry.name), depth: current.depth + 1 });
			}
		}

		const next = new Map<string, SkillLocation>();
		for (const path of discovered.sort()) {
			signal?.throwIfAborted();
			const text = await readUtf8Bounded(path, this.maximumSkillBytes, signal);
			const directory = resolve(path, "..");
			const skill = parseSkill(text, directory.split(sep).at(-1) ?? "");
			if (next.has(skill.id)) throw new Error(`Duplicate skill id '${skill.id}'.`);
			next.set(skill.id, { skill, directory });
		}
		this.locations = next;
	}

	async list(signal?: AbortSignal): Promise<readonly GameSkill[]> {
		signal?.throwIfAborted();
		return [...this.locations.values()]
			.map((location) => cloneSkill(location.skill))
			.sort((left, right) => right.priority - left.priority || left.id.localeCompare(right.id));
	}

	async readResource(skillId: string, relativePath: string, signal?: AbortSignal): Promise<string> {
		signal?.throwIfAborted();
		const location = this.locations.get(skillId);
		if (!location) throw new Error("Skill is not available.");
		if (
			relativePath.length < 1 ||
			relativePath.length > 1_024 ||
			isAbsolute(relativePath) ||
			relativePath.includes("\\") ||
			relativePath.split("/").some((segment) => segment === "" || segment === "." || segment === "..")
		)
			throw new TypeError("Skill resource path is invalid.");
		const target = resolve(location.directory, ...relativePath.split("/"));
		const pathFromRoot = relative(location.directory, target);
		if (pathFromRoot.startsWith("..") || isAbsolute(pathFromRoot))
			throw new Error("Skill resource escapes its directory.");
		let current = location.directory;
		for (const segment of relativePath.split("/")) {
			current = resolve(current, segment);
			const information = await lstat(current);
			if (information.isSymbolicLink()) throw new Error("Skill resources cannot traverse symbolic links.");
		}
		return readUtf8Bounded(target, this.maximumResourceBytes, signal);
	}
}

function loadTool(
	input: GameInput,
	options: GameSkillExtensionOptions,
	selected: WeakMap<GameInput, Map<string, GameSkill>>,
): GameTool {
	return {
		definition: {
			name: "load_game_skill",
			label: "Load game skill",
			description: "Load trusted instructions or a referenced text resource for one advertised game skill.",
			parameters: {
				type: "object",
				properties: {
					id: { type: "string", minLength: 1, maxLength: 64 },
					resource: { type: "string", minLength: 1, maxLength: 1024 },
				},
				required: ["id"],
				additionalProperties: false,
			},
		},
		async execute(call, context) {
			const { signal } = context;
			const id = call.arguments["id"];
			const resource = call.arguments["resource"];
			if (typeof id !== "string" || (resource !== undefined && typeof resource !== "string"))
				throw new TypeError("Skill load arguments are invalid.");
			const skill = selected.get(input)?.get(id);
			if (!skill) throw new Error("Skill was not advertised for this input.");
			const text =
				resource === undefined ? skill.instructions : await options.source.readResource?.(id, resource, signal);
			if (text === undefined) throw new Error("This skill source does not expose referenced resources.");
			if (text.length > (options.maximumLoadedCharacters ?? 1_000_000))
				throw new RangeError("Loaded skill content exceeds its configured character limit.");
			return {
				content: [{ type: "text", text }],
				details: { skillId: skill.id, version: skill.version, digest: skill.digest, resource: resource ?? null },
			};
		},
	};
}

export function createGameSkillExtension(options: GameSkillExtensionOptions): GameSkillExtensionResources {
	if (!options.source) throw new TypeError("A skill source is required.");
	const maximumSelectedSkills = boundedInteger(options.maximumSelectedSkills, 32, 0, 1_000, "maximumSelectedSkills");
	const maximumAdvertisedCharacters = boundedInteger(
		options.maximumAdvertisedCharacters,
		64_000,
		0,
		10_000_000,
		"maximumAdvertisedCharacters",
	);
	boundedInteger(options.maximumLoadedCharacters, 1_000_000, 1, 100_000_000, "maximumLoadedCharacters");
	const selected = new WeakMap<GameInput, Map<string, GameSkill>>();
	return {
		toolProvider: {
			async provide(input) {
				return [loadTool(input, options, selected)];
			},
		},
		postToolContextProvider: {
			async provide(input, definitions, signal) {
				const inventory = await options.source.list(signal);
				const toolNames = definitions.map((definition) => definition.name);
				const tools = new Set(toolNames);
				const eligible = inventory.filter(
					(skill) =>
						!skill.disableModelInvocation &&
						(skill.inputTypes.length === 0 || skill.inputTypes.includes(input.type)) &&
						skill.requiredTools.every((tool) => tools.has(tool)),
				);
				const requested = options.select
					? await options.select({ input, availableTools: toolNames, candidates: eligible.map(cloneSkill) }, signal)
					: eligible.map((skill) => skill.id);
				const explicit = (await options.explicitSkillIds?.(input, signal)) ?? [];
				const byId = new Map(inventory.map((skill) => [skill.id, skill]));
				const chosen = new Map<string, GameSkill>();
				for (const id of [...requested, ...explicit]) {
					if (chosen.has(id)) continue;
					const skill = byId.get(id);
					if (!skill) throw new Error(`Selected skill '${id}' does not exist.`);
					if (!skill.requiredTools.every((tool) => tools.has(tool)))
						throw new Error(`Selected skill '${id}' requires an unavailable tool.`);
					chosen.set(id, skill);
					if (chosen.size > maximumSelectedSkills)
						throw new RangeError("Selected skill count exceeds its configured limit.");
				}
				const ordered = [...chosen.values()].sort(
					(left, right) => right.priority - left.priority || left.id.localeCompare(right.id),
				);
				const advertised = ordered.map((skill) => ({
					id: skill.id,
					name: skill.name,
					description: skill.description,
					version: skill.version,
					digest: skill.digest,
				}));
				const value: JsonValue = {
					usage: "Call load_game_skill only when a listed skill matches the current task.",
					skills: advertised,
				};
				if (JSON.stringify(value).length > maximumAdvertisedCharacters)
					throw new RangeError("Advertised skill metadata exceeds its configured character limit.");
				selected.set(input, new Map(ordered.map((skill) => [skill.id, skill])));
				if (ordered.length === 0) return undefined;
				return { name: options.contextName ?? "game-skills", priority: options.contextPriority ?? 40, value };
			},
		},
	};
}
