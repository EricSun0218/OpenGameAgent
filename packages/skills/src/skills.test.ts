import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameInput, GameToolExecutionContext } from "@opengameagent/protocol";
import { preflightGameToolSchema } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { createGameSkillExtension, DirectoryGameSkillSource } from "./skills.js";

const directories: string[] = [];
const input: GameInput = {
	id: "input",
	type: "npc.command",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 10 },
	content: [{ type: "text", text: "build" }],
};

function executionContext(): GameToolExecutionContext {
	return { input, runId: "run-1", turn: 1, toolCallIndex: 0, signal: new AbortController().signal };
}

async function root(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-skills-"));
	directories.push(directory);
	return directory;
}

async function skill(
	rootDirectory: string,
	id: string,
	metadata: string,
	instructions: string,
	resource?: { name: string; text: string },
): Promise<void> {
	const directory = join(rootDirectory, id);
	await mkdir(directory, { recursive: true });
	const name = /^name:/m.test(metadata) ? "" : `name: ${id}\n`;
	await writeFile(
		join(directory, "SKILL.md"),
		`---\n${name}description: ${id} description\n${metadata}---\n${instructions}`,
	);
	if (resource) await writeFile(join(directory, resource.name), resource.text);
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("DirectoryGameSkillSource", () => {
	it("loads bounded Agent Skills with stable digests and safe relative resources", async () => {
		const directory = await root();
		await skill(
			directory,
			"build-shelter",
			"input-types:\n  - npc.command\ntools:\n  - build\npriority: 10\nversion: '2'\n",
			"Build in verified stages.\nRead notes.txt first.",
			{ name: "notes.txt", text: "Use a stable foundation." },
		);
		const source = await DirectoryGameSkillSource.open(directory);
		const first = await source.list();
		expect(first).toHaveLength(1);
		expect(first[0]).toMatchObject({ id: "build-shelter", requiredTools: ["build"], version: "2" });
		expect(first[0]?.digest).toHaveLength(43);
		expect(await source.readResource("build-shelter", "notes.txt")).toBe("Use a stable foundation.");
		await expect(source.readResource("build-shelter", "../secret.txt")).rejects.toThrow("invalid");

		await source.reload();
		expect((await source.list())[0]?.digest).toBe(first[0]?.digest);
	});

	it("fails closed on duplicate identities and malformed metadata", async () => {
		const directory = await root();
		await skill(directory, "one", "", "One.");
		await skill(directory, "two", "name: one\n", "Two.");
		await expect(DirectoryGameSkillSource.open(directory)).rejects.toThrow("Duplicate skill id");

		const malformed = await root();
		await skill(malformed, "bad", "description: [not, a, string]\n", "Bad.");
		await expect(DirectoryGameSkillSource.open(malformed)).rejects.toThrow();
	});
});

describe("GameSkillExtension", () => {
	it("advertises only tool-compatible skills and progressively loads their instructions", async () => {
		const directory = await root();
		await skill(directory, "build-shelter", "tools:\n  - build\n", "Build carefully.");
		await skill(directory, "private-skill", "disable-model-invocation: true\n", "Private instructions.");
		const source = await DirectoryGameSkillSource.open(directory);
		const extension = createGameSkillExtension({ source });
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		expect(tools).toHaveLength(1);
		expect(() =>
			preflightGameToolSchema(tools[0]?.definition as NonNullable<(typeof tools)[0]>["definition"]),
		).not.toThrow();

		const missing = await extension.postToolContextProvider.provide(
			input,
			[tools[0]?.definition as NonNullable<(typeof tools)[0]>["definition"]],
			new AbortController().signal,
		);
		expect(missing).toBeUndefined();
		await expect(
			tools[0]?.execute(
				{ id: "call", name: "load_game_skill", arguments: { id: "build-shelter" } },
				executionContext(),
			),
		).rejects.toThrow("not advertised");

		const segment = await extension.postToolContextProvider.provide(
			input,
			[
				tools[0]?.definition as NonNullable<(typeof tools)[0]>["definition"],
				{ name: "build", label: "Build", description: "Build", parameters: { type: "object" } },
			],
			new AbortController().signal,
		);
		expect(JSON.stringify(segment?.value)).toContain("build-shelter");
		expect(JSON.stringify(segment?.value)).not.toContain("Build carefully");
		expect(JSON.stringify(segment?.value)).not.toContain("private-skill");
		const loaded = await tools[0]?.execute(
			{ id: "call", name: "load_game_skill", arguments: { id: "build-shelter" } },
			executionContext(),
		);
		expect(loaded?.content).toEqual([{ type: "text", text: "Build carefully." }]);
	});

	it("allows a host to explicitly select a non-model-invocable skill", async () => {
		const directory = await root();
		await skill(directory, "private-skill", "disable-model-invocation: true\n", "Private instructions.");
		const source = await DirectoryGameSkillSource.open(directory);
		const extension = createGameSkillExtension({ source, explicitSkillIds: () => ["private-skill"] });
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		const segment = await extension.postToolContextProvider.provide(
			input,
			tools.map((tool) => tool.definition),
			new AbortController().signal,
		);
		expect(JSON.stringify(segment?.value)).toContain("private-skill");
	});
});
