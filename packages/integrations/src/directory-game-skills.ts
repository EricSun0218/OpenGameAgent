import { createHash } from "node:crypto";
import { realpath } from "node:fs/promises";
import { basename, dirname, isAbsolute, relative, resolve, sep } from "node:path";
import {
	type AgentTool,
	formatSkillInvocation,
	loadSourcedSkills,
	type Skill,
	type SkillDiagnostic,
} from "@earendil-works/pi-agent-core";
import type { GameRequestContribution, GameRequestSource, NormalizedGameInput } from "@opengameagent/agent";
import type { RunCoordinate } from "@opengameagent/protocol";
import { assertOpaqueIdentifier, canonicalizeJsonValue } from "@opengameagent/protocol";
import { Type } from "typebox";
import { parse as parseYaml } from "yaml";
import { BoundedFileReadError, readStableTextFile } from "./bounded-file.js";
import { createBoundedSkillExecutionEnv, type ResolvedDirectoryGameSkillRoot } from "./bounded-skill-env.js";
import { DirectoryGameSkillError } from "./errors.js";
import type {
	DirectoryGameSkillCatalogSnapshot,
	DirectoryGameSkillDiagnostic,
	DirectoryGameSkillLimits,
	DirectoryGameSkillRoot,
	DirectoryGameSkillSelector,
	DirectoryGameSkillSourceOptions,
	DirectoryGameSkillSummary,
	ExplicitGameSkillInvocation,
} from "./types.js";

const encoder = new TextEncoder();
const skillParameters = Type.Object(
	{
		name: Type.String({
			maxLength: 64,
			description: "Exact skill name from the current available game skills catalog.",
		}),
		resource: Type.Optional(
			Type.String({ description: "Optional forward-slash relative text resource path inside the selected skill." }),
		),
	},
	{ additionalProperties: false },
);

interface LoadedDirectoryGameSkill {
	readonly skill: Skill;
	readonly rootId: string;
	readonly addressedDirectory: string;
	readonly canonicalDirectory: string;
	readonly contentFingerprint: string;
}

interface LoadedDirectoryGameSkillCatalog {
	readonly revision: string;
	readonly skills: readonly LoadedDirectoryGameSkill[];
	readonly diagnostics: readonly DirectoryGameSkillDiagnostic[];
}

export interface DirectoryGameSkillToolDetails {
	readonly kind: "instructions" | "resource";
	readonly name: string;
	readonly revision: string;
	readonly resource?: string;
}

/**
 * Read-only, per-request Agent Skills projection for a Pi-backed GameAgent.
 *
 * Discovery and frontmatter parsing are delegated to Pi. This adapter adds
 * host-required resource budgets, root confinement, deterministic collision
 * precedence, per-input Tool-aware selection, opaque model locators, and an
 * exact immutable body snapshot for the generated read Tool.
 */
export class DirectoryGameSkillSource implements GameRequestSource {
	readonly provenanceKind = "skill" as const;
	readonly sourceId: string;
	private readonly cwd: string;
	private readonly roots: readonly DirectoryGameSkillRoot[];
	private readonly limits: DirectoryGameSkillLimits;
	private readonly toolName: string;
	private readonly select: DirectoryGameSkillSelector | undefined;
	private readonly catalogRevision: (() => string) | undefined;
	private cachedCatalog: Readonly<{ key: string; catalog: LoadedDirectoryGameSkillCatalog }> | undefined;

	constructor(options: DirectoryGameSkillSourceOptions) {
		validateOptions(options);
		this.cwd = options.cwd;
		this.sourceId = options.sourceId;
		this.roots = options.roots.map((root) => ({ ...root }));
		this.limits = { ...options.limits };
		this.toolName = options.toolName ?? "skill";
		this.select = options.select;
		this.catalogRevision = options.catalogRevision;
	}

	async collect(
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
		visibleToolNames: readonly string[],
	): Promise<GameRequestContribution> {
		signal.throwIfAborted();
		const catalog = await this.loadCatalog(signal);
		const eligible = catalog.skills.filter((entry) => entry.skill.disableModelInvocation !== true);
		const selected = await this.selectSkills(eligible, input, coordinate, visibleToolNames, signal);
		if (selected.length === 0) return {};
		if (selected.length > this.limits.maxVisibleSkills) {
			throw new DirectoryGameSkillError("catalog_limit_exceeded");
		}
		for (const entry of selected) this.formatInvocation(entry);

		const revision = selectedRevision(selected);
		const content = {
			catalogVersion: "1",
			kind: "available-game-skills",
			loadTool: this.toolName,
			skills: selected.map((entry) => ({
				name: entry.skill.name,
				description: entry.skill.description,
			})),
		};
		const catalogText = canonicalizeJsonValue(content);
		if (encoder.encode(catalogText).byteLength > this.limits.maxCatalogBytes) {
			throw new DirectoryGameSkillError("catalog_limit_exceeded");
		}

		const selectedByName = new Map(selected.map((entry) => [entry.skill.name, entry]));
		return {
			context: { references: [{ referenceId: "catalog", revision, content }] },
			tools: [this.createReadTool(selectedByName, revision)],
		};
	}

	/** Load a host-explicit skill, including one hidden from model invocation. */
	async loadExplicit(name: string, signal: AbortSignal): Promise<ExplicitGameSkillInvocation> {
		signal.throwIfAborted();
		if (!isSkillName(name)) throw new DirectoryGameSkillError("skill_unavailable");
		const catalog = await this.loadCatalog(signal);
		const entry = catalog.skills.find((candidate) => candidate.skill.name === name);
		if (!entry) throw new DirectoryGameSkillError("skill_unavailable");
		const text = this.formatInvocation(entry);
		return { name, revision: catalog.revision, text };
	}

	/** Return a path-free host diagnostic view of the latest complete scan. */
	async inspect(signal: AbortSignal): Promise<DirectoryGameSkillCatalogSnapshot> {
		const catalog = await this.loadCatalog(signal);
		return {
			revision: catalog.revision,
			skills: catalog.skills.map(toSummary),
			diagnostics: catalog.diagnostics.map((diagnostic) => ({ ...diagnostic })),
		};
	}

	private async loadCatalog(signal: AbortSignal): Promise<LoadedDirectoryGameSkillCatalog> {
		signal.throwIfAborted();
		const cacheKey = this.readCatalogRevision();
		if (cacheKey !== undefined && this.cachedCatalog?.key === cacheKey) return this.cachedCatalog.catalog;
		const catalog = await this.scanCatalog(signal);
		if (cacheKey !== undefined) this.cachedCatalog = Object.freeze({ key: cacheKey, catalog });
		return catalog;
	}

	private async scanCatalog(signal: AbortSignal): Promise<LoadedDirectoryGameSkillCatalog> {
		const execution = await createBoundedSkillExecutionEnv(this.cwd, this.roots, this.limits, signal);
		try {
			const loaded = await loadSourcedSkills(
				execution.env,
				execution.roots.map((root) => ({ path: root.addressedPath, source: root.rootId })),
				undefined,
				execution.context,
			);
			execution.env.throwIfFatal();
			signal.throwIfAborted();
			if (loaded.diagnostics.length > this.limits.maxDiagnostics) {
				throw new DirectoryGameSkillError("catalog_limit_exceeded");
			}
			if (loaded.diagnostics.some((diagnostic) => isIncompleteDiagnostic(diagnostic.code))) {
				throw new DirectoryGameSkillError("catalog_incomplete");
			}

			const invalidPaths = loaded.diagnostics
				.filter((diagnostic) => diagnostic.code === "parse_failed" || diagnostic.code === "invalid_metadata")
				.map((diagnostic) => diagnostic.path);
			const diagnostics = loaded.diagnostics.map((diagnostic) => this.sanitizeDiagnostic(diagnostic, execution.roots));
			const candidates: LoadedDirectoryGameSkill[] = [];
			for (const loadedSkill of loaded.skills) {
				signal.throwIfAborted();
				if (invalidPaths.some((path) => samePath(path, loadedSkill.skill.filePath))) continue;
				if (basename(loadedSkill.skill.filePath) !== "SKILL.md") {
					diagnostics.push({ code: "flat_skill_ignored", rootId: loadedSkill.source });
					continue;
				}
				const root = rootForPath(execution.roots, loadedSkill.skill.filePath);
				if (!root || root.rootId !== loadedSkill.source || root.canonicalPath === undefined) {
					throw new DirectoryGameSkillError("catalog_incomplete");
				}
				const addressedDirectory = dirname(resolve(loadedSkill.skill.filePath));
				const canonicalResult = await execution.base.canonicalPath(addressedDirectory, execution.context);
				if (!canonicalResult.ok) throw new DirectoryGameSkillError("catalog_incomplete");
				const canonicalDirectory = resolve(canonicalResult.value);
				if (!isContained(root.canonicalPath, canonicalDirectory)) {
					throw new DirectoryGameSkillError("root_escape");
				}
				const canonicalSkillFile = resolve(canonicalDirectory, "SKILL.md");
				const raw = execution.env.readCachedText(canonicalSkillFile);
				if (raw === undefined || !hasStrictInvocationMetadata(raw, loadedSkill.skill)) {
					diagnostics.push({ code: "invalid_metadata", rootId: root.rootId });
					continue;
				}
				candidates.push({
					skill: { ...loadedSkill.skill },
					rootId: root.rootId,
					addressedDirectory,
					canonicalDirectory,
					contentFingerprint: sha256(loadedSkill.skill.content),
				});
			}
			if (candidates.length > this.limits.maxLoadedSkills) {
				throw new DirectoryGameSkillError("catalog_limit_exceeded");
			}

			const winners: LoadedDirectoryGameSkill[] = [];
			const seen = new Set<string>();
			for (const candidate of candidates) {
				if (seen.has(candidate.skill.name)) {
					diagnostics.push({ code: "shadowed_skill", rootId: candidate.rootId });
					continue;
				}
				seen.add(candidate.skill.name);
				winners.push(candidate);
			}
			if (diagnostics.length > this.limits.maxDiagnostics) {
				throw new DirectoryGameSkillError("catalog_limit_exceeded");
			}
			return {
				revision: selectedRevision(winners),
				skills: winners,
				diagnostics,
			};
		} finally {
			await execution.env.cleanup(execution.context);
		}
	}

	private readCatalogRevision(): string | undefined {
		if (!this.catalogRevision) return undefined;
		try {
			const revision = this.catalogRevision();
			assertOpaqueIdentifier(revision);
			return revision;
		} catch {
			throw new DirectoryGameSkillError("invalid_configuration");
		}
	}

	private async selectSkills(
		eligible: readonly LoadedDirectoryGameSkill[],
		input: Readonly<NormalizedGameInput>,
		coordinate: Readonly<RunCoordinate>,
		visibleToolNames: readonly string[],
		signal: AbortSignal,
	): Promise<readonly LoadedDirectoryGameSkill[]> {
		if (!this.select) return eligible;
		const candidates = Object.freeze(eligible.map((entry) => Object.freeze(toSummary(entry))));
		const selectedNames = await this.select(
			{
				input,
				coordinate,
				visibleToolNames: Object.freeze([...visibleToolNames]),
				candidates,
			},
			signal,
		);
		signal.throwIfAborted();
		if (!Array.isArray(selectedNames)) throw new DirectoryGameSkillError("invalid_selection");
		const byName = new Map(eligible.map((entry) => [entry.skill.name, entry]));
		const selected: LoadedDirectoryGameSkill[] = [];
		const seen = new Set<string>();
		for (const name of selectedNames) {
			if (typeof name !== "string" || seen.has(name)) {
				throw new DirectoryGameSkillError("invalid_selection");
			}
			const entry = byName.get(name);
			if (!entry) throw new DirectoryGameSkillError("invalid_selection");
			seen.add(name);
			selected.push(entry);
		}
		return selected;
	}

	private createReadTool(
		selected: ReadonlyMap<string, LoadedDirectoryGameSkill>,
		revision: string,
	): AgentTool<typeof skillParameters, DirectoryGameSkillToolDetails> {
		return {
			name: this.toolName,
			label: "Load game skill",
			description:
				"Load the full instructions or one referenced text resource for an available game skill. Use only exact names from the current catalog and load instructions before following them.",
			parameters: skillParameters,
			executionMode: "parallel",
			execute: async (_toolCallId, parameters, signal) => {
				const executionSignal = signal ?? new AbortController().signal;
				executionSignal.throwIfAborted();
				const entry = selected.get(parameters.name);
				if (!entry) throw new DirectoryGameSkillError("skill_unavailable");
				if (parameters.resource === undefined) {
					return {
						content: [{ type: "text", text: this.formatInvocation(entry) }],
						details: { kind: "instructions", name: entry.skill.name, revision },
					};
				}
				const text = await this.readResource(entry, parameters.resource, executionSignal);
				const rendered = `<skill_resource skill="${escapeAttribute(entry.skill.name)}" path="${escapeAttribute(
					parameters.resource,
				)}">\n${text}\n</skill_resource>`;
				this.assertToolResult(rendered);
				return {
					content: [{ type: "text", text: rendered }],
					details: {
						kind: "resource",
						name: entry.skill.name,
						revision,
						resource: parameters.resource,
					},
				};
			},
		};
	}

	private formatInvocation(entry: LoadedDirectoryGameSkill): string {
		const text = formatSkillInvocation({
			...entry.skill,
			filePath: `skill://${entry.skill.name}/SKILL.md`,
		});
		this.assertToolResult(text);
		return text;
	}

	private async readResource(entry: LoadedDirectoryGameSkill, resource: string, signal: AbortSignal): Promise<string> {
		const segments = validateResourcePath(resource, this.limits.maxResourcePathBytes);
		const addressed = resolve(entry.addressedDirectory, ...segments);
		if (!isContained(entry.addressedDirectory, addressed)) {
			throw new DirectoryGameSkillError("invalid_resource_path");
		}
		signal.throwIfAborted();
		let canonical: string;
		try {
			canonical = resolve(await realpath(addressed));
		} catch {
			throw new DirectoryGameSkillError("resource_unavailable");
		}
		signal.throwIfAborted();
		if (!isContained(entry.canonicalDirectory, canonical)) {
			throw new DirectoryGameSkillError("root_escape");
		}
		let text: string;
		try {
			const stable = await readStableTextFile(addressed, this.limits.maxResourceBytes, signal);
			if (!samePath(stable.canonicalPath, canonical)) throw new DirectoryGameSkillError("root_escape");
			text = stable.text;
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof DirectoryGameSkillError) throw error;
			if (error instanceof BoundedFileReadError) {
				if (error.code === "not_file") throw new DirectoryGameSkillError("resource_not_file");
				if (error.code === "limit_exceeded") throw new DirectoryGameSkillError("resource_limit_exceeded");
				if (error.code === "not_utf8") throw new DirectoryGameSkillError("resource_not_text");
				if (error.code === "changed") throw new DirectoryGameSkillError("root_escape");
			}
			throw new DirectoryGameSkillError("resource_unavailable");
		}
		return text;
	}

	private sanitizeDiagnostic(
		diagnostic: SkillDiagnostic & { source: string },
		roots: readonly ResolvedDirectoryGameSkillRoot[],
	): DirectoryGameSkillDiagnostic {
		const root = rootForPath(roots, diagnostic.path);
		if (!root || root.rootId !== diagnostic.source) {
			throw new DirectoryGameSkillError("catalog_incomplete");
		}
		return { code: diagnostic.code, rootId: root.rootId };
	}

	private assertToolResult(value: string): void {
		if (encoder.encode(value).byteLength > this.limits.maxToolResultBytes) {
			throw new DirectoryGameSkillError("tool_result_limit_exceeded");
		}
	}
}

function validateOptions(options: DirectoryGameSkillSourceOptions): void {
	if (typeof options !== "object" || options === null || Array.isArray(options)) {
		throw new DirectoryGameSkillError("invalid_configuration");
	}
	if (typeof options.cwd !== "string" || options.cwd.length === 0 || !Array.isArray(options.roots)) {
		throw new DirectoryGameSkillError("invalid_configuration");
	}
	assertOpaqueIdentifier(options.sourceId);
	assertOpaqueIdentifier(options.toolName ?? "skill");
	if (typeof options.limits !== "object" || options.limits === null || Array.isArray(options.limits)) {
		throw new DirectoryGameSkillError("invalid_configuration");
	}
	const fields = Object.entries(options.limits) as [keyof DirectoryGameSkillLimits, number][];
	const expectedFields: readonly (keyof DirectoryGameSkillLimits)[] = [
		"maxRoots",
		"maxTraversalDepth",
		"maxDirectories",
		"maxEntriesPerDirectory",
		"maxVisitedEntries",
		"maxReadFiles",
		"maxSkillFileBytes",
		"maxIgnoreFileBytes",
		"maxTotalReadBytes",
		"maxLoadedSkills",
		"maxDiagnostics",
		"maxVisibleSkills",
		"maxCatalogBytes",
		"maxAddressedPathBytes",
		"maxResourcePathBytes",
		"maxResourceBytes",
		"maxToolResultBytes",
	];
	if (fields.length !== expectedFields.length) throw new DirectoryGameSkillError("invalid_configuration");
	for (const field of expectedFields) {
		const value = options.limits[field];
		const minimum = field === "maxTraversalDepth" ? 0 : 1;
		if (!Number.isSafeInteger(value) || value < minimum) {
			throw new DirectoryGameSkillError("invalid_configuration");
		}
	}
	if (options.roots.length === 0 || options.roots.length > options.limits.maxRoots) {
		throw new DirectoryGameSkillError("invalid_configuration");
	}
	const rootIds = new Set<string>();
	for (const root of options.roots) {
		if (typeof root !== "object" || root === null || Array.isArray(root) || typeof root.path !== "string") {
			throw new DirectoryGameSkillError("invalid_configuration");
		}
		assertOpaqueIdentifier(root.rootId);
		if (root.path.length === 0 || rootIds.has(root.rootId)) {
			throw new DirectoryGameSkillError("invalid_configuration");
		}
		rootIds.add(root.rootId);
	}
	if (options.select !== undefined && typeof options.select !== "function") {
		throw new DirectoryGameSkillError("invalid_configuration");
	}
	if (options.catalogRevision !== undefined && typeof options.catalogRevision !== "function") {
		throw new DirectoryGameSkillError("invalid_configuration");
	}
}

function toSummary(entry: LoadedDirectoryGameSkill): DirectoryGameSkillSummary {
	return {
		name: entry.skill.name,
		description: entry.skill.description,
		rootId: entry.rootId,
		modelInvocable: entry.skill.disableModelInvocation !== true,
	};
}

function selectedRevision(entries: readonly LoadedDirectoryGameSkill[]): string {
	return sha256(
		canonicalizeJsonValue(
			entries.map((entry) => ({
				name: entry.skill.name,
				description: entry.skill.description,
				rootId: entry.rootId,
				modelInvocable: entry.skill.disableModelInvocation !== true,
				contentFingerprint: entry.contentFingerprint,
			})),
		),
	);
}

function rootForPath(
	roots: readonly ResolvedDirectoryGameSkillRoot[],
	path: string,
): ResolvedDirectoryGameSkillRoot | undefined {
	const absolute = resolve(path);
	return roots.find(
		(root) =>
			isContained(root.addressedPath, absolute) ||
			(root.canonicalPath !== undefined && isContained(root.canonicalPath, absolute)),
	);
}

function validateResourcePath(resource: string, maxBytes: number): readonly string[] {
	if (
		typeof resource !== "string" ||
		resource.length === 0 ||
		encoder.encode(resource).byteLength > maxBytes ||
		resource.includes("\\") ||
		resource.includes(":") ||
		resource.includes("\u0000") ||
		isAbsolute(resource) ||
		[...resource].some((character) => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127)
	) {
		throw new DirectoryGameSkillError("invalid_resource_path");
	}
	const segments = resource.split("/");
	if (segments.some((segment) => segment === "" || segment === "." || segment === "..")) {
		throw new DirectoryGameSkillError("invalid_resource_path");
	}
	return segments;
}

function hasStrictInvocationMetadata(raw: string, skill: Skill): boolean {
	const normalized = raw.replaceAll("\r\n", "\n").replaceAll("\r", "\n");
	if (!normalized.startsWith("---\n")) return false;
	let end = normalized.indexOf("\n---\n", 4);
	if (end < 0 && normalized.endsWith("\n---")) end = normalized.length - 4;
	if (end < 0) return false;
	let parsed: unknown;
	try {
		parsed = parseYaml(normalized.slice(4, end));
	} catch {
		return false;
	}
	if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return false;
	const metadata = parsed as Record<string, unknown>;
	if (metadata.name !== skill.name || metadata.description !== skill.description) return false;
	if (
		Object.hasOwn(metadata, "disableModelInvocation") ||
		Object.hasOwn(metadata, "modelInvocable") ||
		Object.hasOwn(metadata, "userInvocable")
	) {
		return false;
	}
	if (
		Object.hasOwn(metadata, "disable-model-invocation") &&
		typeof metadata["disable-model-invocation"] !== "boolean"
	) {
		return false;
	}
	if ((metadata["disable-model-invocation"] === true) !== (skill.disableModelInvocation === true)) return false;
	return true;
}

function isSkillName(name: string): boolean {
	return /^[a-z0-9]+(?:-[a-z0-9]+)*$/u.test(name) && name.length <= 64;
}

function isIncompleteDiagnostic(code: SkillDiagnostic["code"]): boolean {
	return code === "file_info_failed" || code === "list_failed" || code === "read_failed";
}

function isContained(base: string, target: string): boolean {
	const child = relative(base, target);
	return child === "" || (child !== ".." && !child.startsWith(`..${sep}`) && !isAbsolute(child));
}

function samePath(left: string, right: string): boolean {
	return relative(left, right) === "" && relative(right, left) === "";
}

function sha256(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

function escapeAttribute(value: string): string {
	return value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;");
}
