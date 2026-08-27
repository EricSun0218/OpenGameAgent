import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { loadPortableGamePlugin } from "./plugins.js";

const roots: string[] = [];

async function pluginRoot(manifest: Record<string, unknown>): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "oga-plugin-"));
	roots.push(root);
	await writeFile(join(root, "plugin.json"), JSON.stringify(manifest), "utf8");
	return root;
}

async function addSkill(root: string, directory: string, name = directory): Promise<void> {
	const path = join(root, "skills", directory);
	await mkdir(path, { recursive: true });
	await writeFile(
		join(path, "SKILL.md"),
		`---\nname: ${name}\ndescription: A portable test skill.\ntools:\n  - inspect_world\n---\n\nInspect the trusted world snapshot.`,
		"utf8",
	);
}

const schema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
const mcpSchema = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json";

afterEach(async () => {
	await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

describe("loadPortableGamePlugin", () => {
	it("loads immediate Skills and an authorized MCP component without connecting during discovery", async () => {
		const root = await pluginRoot({
			$schema: schema,
			name: "world-tools",
			version: "1.2.3",
			extra: "ignored",
		});
		await addSkill(root, "inspect");
		await writeFile(
			join(root, "mcp.json"),
			JSON.stringify({
				$schema: mcpSchema,
				mcpServers: {
					world: { type: "streamable-http", url: "https://example.invalid/mcp" },
				},
			}),
			"utf8",
		);
		const loaded = await loadPortableGamePlugin(root);
		expect(loaded.manifest.name).toBe("world-tools");
		expect((await loaded.skills?.list())?.map((skill) => skill.id)).toEqual(["inspect"]);
		expect(loaded.mcp).toBeDefined();
		expect(loaded.diagnostics).toEqual([
			expect.objectContaining({ code: "manifest.unknown-field", component: "manifest" }),
		]);
	});

	it("rejects a malformed manifest before discovering any component", async () => {
		const root = await pluginRoot({ $schema: schema, name: "Bad Name" });
		await addSkill(root, "hidden");
		await expect(loadPortableGamePlugin(root)).rejects.toThrow("Plugin name is invalid");
	});

	it("isolates invalid Skills and MCP entries while retaining valid siblings", async () => {
		const root = await pluginRoot({ $schema: schema, name: "mixed" });
		await addSkill(root, "good");
		await addSkill(root, "bad", "NOT PORTABLE");
		await writeFile(
			join(root, "mcp.json"),
			JSON.stringify({
				$schema: mcpSchema,
				mcpServers: {
					valid: { type: "streamable-http", url: "https://example.invalid/mcp" },
					legacy: { type: "sse", url: "https://example.invalid/sse" },
					broken: { type: "stdio", command: "node", env: { PLUGIN_DATA: "override" } },
				},
			}),
			"utf8",
		);
		const loaded = await loadPortableGamePlugin(root, { dataDirectory: join(root, "host-data") });
		expect((await loaded.skills?.list())?.map((skill) => skill.id)).toEqual(["good"]);
		expect(loaded.mcp).toBeDefined();
		expect(loaded.diagnostics.map((item) => item.code)).toEqual([
			"skills.entry-skipped",
			"mcp.server-skipped",
			"mcp.server-skipped",
		]);
	});

	it("keeps Skills available when top-level MCP configuration is invalid", async () => {
		const root = await pluginRoot({ $schema: schema, name: "skills-only" });
		await addSkill(root, "inspect");
		await writeFile(join(root, "mcp.json"), JSON.stringify({ $schema: "unsupported", mcpServers: {} }), "utf8");
		const loaded = await loadPortableGamePlugin(root);
		expect((await loaded.skills?.list())?.map((skill) => skill.id)).toEqual(["inspect"]);
		expect(loaded.mcp).toBeUndefined();
		expect(loaded.diagnostics).toEqual([expect.objectContaining({ code: "mcp.component-disabled", component: "mcp" })]);
	});

	it("requires host-owned data storage before enabling process plugins", async () => {
		const root = await pluginRoot({ $schema: schema, name: "process-tools" });
		await writeFile(
			join(root, "mcp.json"),
			JSON.stringify({
				$schema: mcpSchema,
				mcpServers: {
					local: {
						type: "stdio",
						command: "node",
						args: ["${PLUGIN_ROOT}/server.mjs", "${PLUGIN_DATA}/state"],
					},
				},
			}),
			"utf8",
		);
		const loaded = await loadPortableGamePlugin(root);
		expect(loaded.mcp).toBeUndefined();
		expect(loaded.diagnostics).toEqual([expect.objectContaining({ code: "mcp.server-skipped" })]);
	});

	it("ignores non-object extension metadata without blocking portable components", async () => {
		const root = await pluginRoot({ $schema: schema, name: "metadata", extensions: "not-an-object" });
		await addSkill(root, "inspect");
		const loaded = await loadPortableGamePlugin(root);
		expect((await loaded.skills?.list())?.length).toBe(1);
		expect(loaded.manifest.extensions.size).toBe(0);
		expect(loaded.diagnostics[0]?.code).toBe("manifest.extensions-ignored");
	});
});
