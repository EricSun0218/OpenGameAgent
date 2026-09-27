import { access, mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import { GameAgent } from "@opengameagent/agent";
import { eventAudience, PROTOCOL_VERSION } from "@opengameagent/protocol";
import type { CanonicalEvent } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import {
	allowAllTestTools,
	assistant,
	messageResponse,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
} from "../../devtools/src/pi-fakes.js";
import { DirectoryGameSkillSource, type DirectoryGameSkillLimits } from "../src/index.js";

const INPUT = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { mode: "play" },
} as const;

const COORDINATE = {
	sessionId: "session",
	actorId: "actor",
	runId: "run",
	turn: "1",
} as const;

const LIMITS: DirectoryGameSkillLimits = {
	maxRoots: 4,
	maxTraversalDepth: 4,
	maxDirectories: 32,
	maxEntriesPerDirectory: 32,
	maxVisitedEntries: 128,
	maxReadFiles: 64,
	maxSkillFileBytes: 8_192,
	maxIgnoreFileBytes: 1_024,
	maxTotalReadBytes: 32_768,
	maxLoadedSkills: 16,
	maxDiagnostics: 16,
	maxVisibleSkills: 8,
	maxCatalogBytes: 4_096,
	maxAddressedPathBytes: 1_024,
	maxResourcePathBytes: 128,
	maxResourceBytes: 4_096,
	maxToolResultBytes: 8_192,
};

const temporaryRoots: string[] = [];

afterEach(async () => {
	for (const root of temporaryRoots.splice(0)) {
		await rm(root, { recursive: true, force: true });
	}
});

describe("DirectoryGameSkillSource", () => {
	it("uses Pi discovery and invocation while applying ordered collisions, Tool-aware selection, and path hiding", async () => {
		const workspace = await tempRoot();
		const project = join(workspace, "project-skills");
		const user = join(workspace, "user-skills");
		await writeSkill(project, "gather-and-build", "Project procedure", "Project instructions.", {
			"references/checklist.md": "Check inventory, then build.",
		});
		await writeSkill(user, "gather-and-build", "Shadowed procedure", "Shadowed instructions.");
		await writeSkill(user, "private-audit", "Host-only audit", "Private instructions.", undefined, true);
		await writeSkill(user, "direct-chat", "Direct conversation", "Talk naturally.");

		const selections: unknown[] = [];
		const source = createSource(
			workspace,
			[
				{ rootId: "project", path: project },
				{ rootId: "user", path: user },
			],
			{
				select: (request) => {
					selections.push(request);
					return request.visibleToolNames.includes("gather") ? ["gather-and-build"] : ["direct-chat"];
				},
			},
		);

		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, Object.freeze(["gather"]));
		const serialized = JSON.stringify(contribution.context?.references);
		expect(serialized).toContain("gather-and-build");
		expect(serialized).not.toContain("private-audit");
		expect(serialized).not.toContain(workspace);
		expect(selections).toMatchObject([
			{
				visibleToolNames: ["gather"],
				candidates: [
					{ name: "gather-and-build", rootId: "project" },
					{ name: "direct-chat", rootId: "user" },
				],
			},
		]);

		const instructions = await executeSkillTool(contribution.tools?.[0], { name: "gather-and-build" });
		expect(instructions.content[0]).toMatchObject({ type: "text" });
		expect(textOf(instructions)).toContain("Project instructions.");
		expect(textOf(instructions)).toContain("skill://gather-and-build/SKILL.md");
		expect(textOf(instructions)).not.toContain(workspace);

		const resource = await executeSkillTool(contribution.tools?.[0], {
			name: "gather-and-build",
			resource: "references/checklist.md",
		});
		expect(textOf(resource)).toContain("Check inventory, then build.");
		expect(textOf(resource)).not.toContain(workspace);

		const inspected = await source.inspect(freshSignal());
		expect(inspected.skills).toMatchObject([
			{ name: "gather-and-build", rootId: "project", modelInvocable: true },
			{ name: "direct-chat", rootId: "user", modelInvocable: true },
			{ name: "private-audit", rootId: "user", modelInvocable: false },
		]);
		expect(inspected.diagnostics).toContainEqual({ code: "shadowed_skill", rootId: "user" });
		expect(JSON.stringify(inspected)).not.toContain(workspace);

		const explicit = await source.loadExplicit("private-audit", freshSignal());
		expect(explicit.text).toContain("Private instructions.");
		expect(explicit.text).not.toContain(workspace);
	});

	it("pins the exact Pi body snapshot for a provider request and refreshes only at the next request", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		const skillFile = await writeSkill(root, "changing", "Changing skill", "Version one.");
		const source = createSource(workspace, [{ rootId: "root", path: root }]);

		const first = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		await writeFile(skillFile, skillDocument("changing", "Changing skill", "Version two."), "utf8");
		const pinned = await executeSkillTool(first.tools?.[0], { name: "changing" });
		expect(textOf(pinned)).toContain("Version one.");
		expect(textOf(pinned)).not.toContain("Version two.");

		const second = await source.collect(INPUT, freshSignal(), { ...COORDINATE, turn: "2" }, []);
		expect(second.context?.references[0]?.revision).not.toBe(first.context?.references[0]?.revision);
		expect(textOf(await executeSkillTool(second.tools?.[0], { name: "changing" }))).toContain("Version two.");
	});

	it("reuses a complete catalog for one host revision and rescans only after the revision changes", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		await writeSkill(root, "stable", "Stable skill", "Version one.");
		let revision = "skills-1";
		const source = createSource(workspace, [{ rootId: "root", path: root }], {
			catalogRevision: () => revision,
		});

		const first = await source.collect(INPUT, freshSignal(), COORDINATE, []);
		await rm(root, { recursive: true, force: true });
		const cached = await source.collect(INPUT, freshSignal(), { ...COORDINATE, turn: "2" }, []);
		expect(cached.context?.references).toEqual(first.context?.references);

		await writeSkill(root, "stable", "Stable skill", "Version two.");
		revision = "skills-2";
		const refreshed = await source.collect(INPUT, freshSignal(), { ...COORDINATE, turn: "3" }, []);
		expect(refreshed.context?.references[0]?.revision).not.toBe(first.context?.references[0]?.revision);
		expect(textOf(await executeSkillTool(refreshed.tools?.[0], { name: "stable" }))).toContain("Version two.");
	});

	it("rejects resource traversal, non-text data, oversize files, and symlink escape", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		const outside = join(workspace, "outside.txt");
		await writeFile(outside, "outside secret", "utf8");
		const skillDirectory = join(root, "reader");
		await writeSkill(root, "reader", "Read resources", "Read only when needed.", {
			"references/valid.md": "inside",
			"references/binary.bin": new Uint8Array([0xff, 0xfe, 0xfd]),
			"references/large.md": "x".repeat(64),
		});
		await symlink(outside, join(skillDirectory, "references", "escape.md"));
		const source = createSource(workspace, [{ rootId: "root", path: root }], {
			limits: { ...LIMITS, maxResourceBytes: 16 },
		});
		const contribution = await source.collect(INPUT, freshSignal(), COORDINATE, []);

		await expect(
			executeSkillTool(contribution.tools?.[0], { name: "reader", resource: "../outside.txt" }),
		).rejects.toMatchObject({ code: "invalid_resource_path" });
		await expect(
			executeSkillTool(contribution.tools?.[0], { name: "reader", resource: "references/valid.md:stream" }),
		).rejects.toMatchObject({ code: "invalid_resource_path" });
		await expect(
			executeSkillTool(contribution.tools?.[0], { name: "reader", resource: "references/escape.md" }),
		).rejects.toMatchObject({ code: "root_escape" });
		await expect(
			executeSkillTool(contribution.tools?.[0], { name: "reader", resource: "references/binary.bin" }),
		).rejects.toMatchObject({ code: "resource_not_text" });
		await expect(
			executeSkillTool(contribution.tools?.[0], { name: "reader", resource: "references/large.md" }),
		).rejects.toMatchObject({ code: "resource_limit_exceeded" });
	});

	it("fails the scan when a discovered directory symlink leaves its configured root", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		const outside = join(workspace, "outside");
		await writeSkill(outside, "escaped", "Escaped", "Must not load.");
		await mkdir(root, { recursive: true });
		await symlink(outside, join(root, "linked"), process.platform === "win32" ? "junction" : "dir");
		const source = createSource(workspace, [{ rootId: "root", path: root }]);

		await expect(source.inspect(freshSignal())).rejects.toMatchObject({ code: "root_escape" });
	});

	it("bounds discovery before publication and keeps malformed diagnostics path-free", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		await writeSkill(root, "valid", "Valid", "Valid body.");
		await mkdir(join(root, "broken"), { recursive: true });
		await writeFile(join(root, "broken", "SKILL.md"), "---\nname: BROKEN\ndescription: Broken\n---\nBody", "utf8");
		await mkdir(join(root, "permissive-policy"), { recursive: true });
		await writeFile(
			join(root, "permissive-policy", "SKILL.md"),
			'---\nname: permissive-policy\ndescription: Must not become visible\ndisable-model-invocation: "true"\n---\nBody',
			"utf8",
		);
		await mkdir(join(root, "missing-name"), { recursive: true });
		await writeFile(join(root, "missing-name", "SKILL.md"), "---\ndescription: A name is required\n---\nBody", "utf8");

		const source = createSource(workspace, [{ rootId: "root", path: root }]);
		const inspected = await source.inspect(freshSignal());
		expect(inspected.skills.map((entry) => entry.name)).toEqual(["valid"]);
		expect(inspected.diagnostics.filter((entry) => entry.code === "invalid_metadata")).toHaveLength(4);
		expect(JSON.stringify(inspected)).not.toContain(workspace);

		const bounded = createSource(workspace, [{ rootId: "root", path: root }], {
			limits: { ...LIMITS, maxEntriesPerDirectory: 3 },
		});
		await expect(bounded.inspect(freshSignal())).rejects.toMatchObject({ code: "catalog_limit_exceeded" });
	});

	it("rejects invalid selector output and visible-catalog overflow", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		await writeSkill(root, "one", "One", "One.");
		await writeSkill(root, "two", "Two", "Two.");

		const invalid = createSource(workspace, [{ rootId: "root", path: root }], {
			select: () => ["unknown"],
		});
		await expect(invalid.collect(INPUT, freshSignal(), COORDINATE, [])).rejects.toMatchObject({
			code: "invalid_selection",
		});

		const bounded = createSource(workspace, [{ rootId: "root", path: root }], {
			limits: { ...LIMITS, maxVisibleSkills: 1 },
		});
		await expect(bounded.collect(INPUT, freshSignal(), COORDINATE, [])).rejects.toMatchObject({
			code: "catalog_limit_exceeded",
		});
	});

	it("rejects overlapping configured roots before discovery can assign ambiguous authority", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		const nested = join(root, "nested");
		await mkdir(nested, { recursive: true });
		const source = createSource(workspace, [
			{ rootId: "outer", path: root },
			{ rootId: "inner", path: nested },
		]);
		expect(source.provenanceKind).toBe("skill");

		await expect(source.inspect(freshSignal())).rejects.toMatchObject({ code: "invalid_configuration" });
	});

	it("enforces root and traversal budgets at their owning boundaries", async () => {
		const { root, workspace } = await budgetFixture();
		expect(
			() =>
				new DirectoryGameSkillSource({
					cwd: workspace,
					sourceId: "skills",
					roots: [
						{ rootId: "one", path: root },
						{ rootId: "two", path: join(workspace, "other") },
					],
					limits: { ...LIMITS, maxRoots: 1 },
				}),
		).toThrow(expect.objectContaining({ code: "invalid_configuration" }));

		await mkdir(join(root, "nested", "deep"), { recursive: true });
		await writeFile(join(root, "nested", "deep", "SKILL.md"), skillDocument("deep", "Deep", "Deep body."), "utf8");
		await expect(
			createSource(workspace, [{ rootId: "root", path: root }], {
				limits: { ...LIMITS, maxTraversalDepth: 1 },
			}).inspect(freshSignal()),
		).rejects.toMatchObject({ code: "catalog_limit_exceeded" });
	});

	it.each([
		["maxDirectories", { ...LIMITS, maxDirectories: 1 }],
		["maxEntriesPerDirectory", { ...LIMITS, maxEntriesPerDirectory: 1 }],
		["maxVisitedEntries", { ...LIMITS, maxVisitedEntries: 1 }],
		["maxReadFiles", { ...LIMITS, maxReadFiles: 1 }],
		["maxSkillFileBytes", { ...LIMITS, maxSkillFileBytes: 1 }],
		["maxTotalReadBytes", { ...LIMITS, maxTotalReadBytes: 1 }],
		["maxLoadedSkills", { ...LIMITS, maxLoadedSkills: 1 }],
		["maxAddressedPathBytes", { ...LIMITS, maxAddressedPathBytes: 1 }],
	] satisfies readonly (readonly [string, DirectoryGameSkillLimits])[])(
		"enforces the host-required %s discovery budget",
		async (_name, limits) => {
			const { root, workspace } = await budgetFixture();
			await expect(
				createSource(workspace, [{ rootId: "root", path: root }], { limits }).inspect(freshSignal()),
			).rejects.toMatchObject({ code: "catalog_limit_exceeded" });
		},
	);

	it("enforces ignore-file and diagnostic budgets at their owning boundaries", async () => {
		const { root, workspace } = await budgetFixture();
		await writeFile(join(root, ".gitignore"), "ignored-pattern", "utf8");
		await expect(
			createSource(workspace, [{ rootId: "root", path: root }], {
				limits: { ...LIMITS, maxIgnoreFileBytes: 1 },
			}).inspect(freshSignal()),
		).rejects.toMatchObject({ code: "catalog_limit_exceeded" });

		const brokenRoot = join(workspace, "broken-skills");
		await mkdir(join(brokenRoot, "broken"), { recursive: true });
		await writeFile(
			join(brokenRoot, "broken", "SKILL.md"),
			"---\nname: BROKEN\ndescription: Broken\n---\nBody",
			"utf8",
		);
		await expect(
			createSource(workspace, [{ rootId: "broken", path: brokenRoot }], {
				limits: { ...LIMITS, maxDiagnostics: 1 },
			}).inspect(freshSignal()),
		).rejects.toMatchObject({ code: "catalog_limit_exceeded" });
	});

	it("enforces catalog and Tool-result budgets at their owning boundaries", async () => {
		const { root, workspace } = await budgetFixture();
		await expect(
			createSource(workspace, [{ rootId: "root", path: root }], {
				limits: { ...LIMITS, maxCatalogBytes: 1 },
			}).collect(INPUT, freshSignal(), COORDINATE, []),
		).rejects.toMatchObject({ code: "catalog_limit_exceeded" });
		await expect(
			createSource(workspace, [{ rootId: "root", path: root }], {
				limits: { ...LIMITS, maxToolResultBytes: 1 },
			}).collect(INPUT, freshSignal(), COORDINATE, []),
		).rejects.toMatchObject({ code: "tool_result_limit_exceeded" });
	});

	it("enforces the addressed resource-path budget at Tool execution", async () => {
		const { root, workspace } = await budgetFixture();
		const resourceSource = createSource(workspace, [{ rootId: "root", path: root }], {
			limits: { ...LIMITS, maxResourcePathBytes: 3 },
			select: () => ["one"],
		});
		const resourceContribution = await resourceSource.collect(INPUT, freshSignal(), COORDINATE, []);
		await expect(
			executeSkillTool(resourceContribution.tools?.[0], { name: "one", resource: "references/guide.md" }),
		).rejects.toMatchObject({ code: "invalid_resource_path" });
	});

	it("does not execute bundled code during discovery", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		const marker = join(workspace, "executed.txt");
		await writeSkill(root, "with-script", "Contains a script", "The host may inspect scripts separately.", {
			"scripts/run.mjs": `await import("node:fs/promises").then(fs => fs.writeFile(${JSON.stringify(marker)}, "bad"));`,
		});
		const source = createSource(workspace, [{ rootId: "root", path: root }]);

		await expect(source.inspect(freshSignal())).resolves.toMatchObject({ skills: [{ name: "with-script" }] });
		await expect(access(marker)).rejects.toBeDefined();
	});

	it("propagates cancellation before publishing a catalog", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		await writeSkill(root, "cancelled", "Cancelled", "Never publish.");
		const source = createSource(workspace, [{ rootId: "root", path: root }]);
		const controller = new AbortController();
		controller.abort(new Error("cancelled"));

		await expect(source.inspect(controller.signal)).rejects.toThrow("cancelled");
	});

	it("runs through the real Pi Agent loop with a fresh Tool-aware catalog on every provider request", async () => {
		const workspace = await tempRoot();
		const root = join(workspace, "skills");
		await writeSkill(root, "gather-guide", "Guide gathering", "Use the gather Tool after checking capacity.");
		const selections: string[][] = [];
		const source = createSource(workspace, [{ rootId: "root", path: root }], {
			select: (request) => {
				selections.push([...request.visibleToolNames]);
				return request.visibleToolNames.includes("gather") ? ["gather-guide"] : [];
			},
		});
		const contexts: Context[] = [];
		let request = 0;
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			request += 1;
			return request === 1
				? messageResponse(
						assistant(
							[{ type: "toolCall", id: "load-guide", name: "skill", arguments: { name: "gather-guide" } }],
							"toolUse",
						),
					)
				: textResponse("done");
		};
		const events: CanonicalEvent[] = [];
		let sequence = 0;
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: testModel(),
			modelStream,
			toolSource: () => [tool("gather", async () => ({ content: [], details: {} }))],
			requestSources: [source],
			toolPolicy: allowAllTestTools,
			toolLimits: TEST_TOOL_LIMITS,
			contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 8_192 },
			emitEvent: async (coordinate, data) => {
				sequence += 1;
				const ordinal = String(sequence);
				const event: CanonicalEvent = {
					protocolVersion: PROTOCOL_VERSION,
					eventId: `event-${ordinal}`,
					sequence: ordinal,
					revision: ordinal,
					occurredAt: "2026-08-29T00:00:00.000Z",
					coordinate: structuredClone(coordinate),
					audience: eventAudience(data),
					data: structuredClone(data),
				};
				events.push(event);
				return event;
			},
		});

		await expect(agent.start(INPUT, "run").completion).resolves.toEqual({
			runId: "run",
			status: "completed",
			finalOutput: "done",
		});
		expect(selections).toEqual([["gather"], ["gather"]]);
		expect(contexts).toHaveLength(2);
		expect(contexts.every((context) => context.tools?.map((entry) => entry.name).join(",") === "gather,skill")).toBe(
			true,
		);
		expect(JSON.stringify(contexts)).not.toContain(workspace);
		expect(contexts[1]?.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				content: [expect.objectContaining({ text: expect.stringContaining("Use the gather Tool") })],
			}),
		);
		expect(events.some((event) => event.data.type === "run.completed")).toBe(true);
	});
});

async function tempRoot(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "oga-skills-test-"));
	temporaryRoots.push(root);
	return root;
}

async function budgetFixture(): Promise<{ readonly root: string; readonly workspace: string }> {
	const workspace = await tempRoot();
	const root = join(workspace, "skills");
	await writeSkill(root, "one", "One", "One body.", { "references/guide.md": "guide" });
	await writeSkill(root, "two", "Two", "Two body.");
	return { root, workspace };
}

async function writeSkill(
	root: string,
	name: string,
	description: string,
	body: string,
	resources?: Readonly<Record<string, string | Uint8Array>>,
	disableModelInvocation = false,
): Promise<string> {
	const directory = join(root, name);
	await mkdir(directory, { recursive: true });
	const path = join(directory, "SKILL.md");
	await writeFile(path, skillDocument(name, description, body, disableModelInvocation), "utf8");
	for (const [relativePath, content] of Object.entries(resources ?? {})) {
		const target = join(directory, ...relativePath.split("/"));
		await mkdir(dirname(target), { recursive: true });
		await writeFile(target, content);
	}
	return path;
}

function skillDocument(name: string, description: string, body: string, disabled = false): string {
	return `---\nname: ${name}\ndescription: ${description}\n${disabled ? "disable-model-invocation: true\n" : ""}---\n${body}\n`;
}

function createSource(
	cwd: string,
	roots: readonly { rootId: string; path: string }[],
	overrides: {
		limits?: DirectoryGameSkillLimits;
		select?: ConstructorParameters<typeof DirectoryGameSkillSource>[0]["select"];
		catalogRevision?: ConstructorParameters<typeof DirectoryGameSkillSource>[0]["catalogRevision"];
	} = {},
): DirectoryGameSkillSource {
	return new DirectoryGameSkillSource({
		cwd,
		sourceId: "skills",
		roots,
		limits: overrides.limits ?? LIMITS,
		...(overrides.select === undefined ? {} : { select: overrides.select }),
		...(overrides.catalogRevision === undefined ? {} : { catalogRevision: overrides.catalogRevision }),
	});
}

async function executeSkillTool(toolEntry: AgentTool | undefined, parameters: { name: string; resource?: string }) {
	if (!toolEntry) throw new Error("Expected the generated skill Tool.");
	return await toolEntry.execute("call", parameters as never, freshSignal());
}

function textOf(result: Awaited<ReturnType<typeof executeSkillTool>>): string {
	const block = result.content[0];
	return block?.type === "text" ? block.text : "";
}

function freshSignal(): AbortSignal {
	return new AbortController().signal;
}

function copyContext(context: Context): Context {
	return {
		...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
		messages: structuredClone(context.messages),
		...(context.tools === undefined ? {} : { tools: [...context.tools] }),
	};
}

function testModel() {
	return {
		id: "test-model",
		name: "Test model",
		api: "openai-responses" as const,
		provider: "openai",
		baseUrl: "https://example.invalid",
		reasoning: true,
		input: ["text"] as ("text" | "image")[],
		cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		contextWindow: 1,
		maxTokens: 1,
	};
}
