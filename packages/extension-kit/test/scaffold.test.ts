import { execFile } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { isAbsolute, join, relative, resolve } from "node:path";
import { promisify } from "node:util";
import { afterEach, describe, expect, it } from "vitest";
import { parseGameExtensionManifest, renderGameExtensionScaffold, writeGameExtensionScaffold } from "../src/index.js";

const execute = promisify(execFile);
const roots: string[] = [];
const manifestLimits = {
	maxManifestBytes: 4_096,
	maxJsonDepth: 4,
	maxIdentifierBytes: 128,
	maxVersionBytes: 64,
	maxVersionRangeBytes: 128,
	maxDependencies: 8,
} as const;
const options = {
	id: "studio.world-context",
	version: "1.0.0",
	packageName: "@studio/world-context-extension",
	extensionKitVersion: "0.1.0-alpha.0",
	typescriptVersion: "7.0.2",
	permissions: ["context.contribute"] as const,
	dependencies: [{ id: "studio.shared", versionRange: "^2.0.0" }],
	manifestLimits,
};

afterEach(async () => {
	for (const root of roots.splice(0)) {
		const resolved = resolve(root);
		const workspaceRelative = relative(resolve(process.cwd()), resolved);
		if (workspaceRelative.startsWith("..") || isAbsolute(workspaceRelative))
			throw new Error("Refusing to remove an unexpected test root.");
		await rm(resolved, { recursive: true, force: true });
	}
});

describe("game extension scaffold", () => {
	it("renders a strict manifest and a buildable typed package without executable loading", async () => {
		const files = renderGameExtensionScaffold(options);

		expect(files.map((file) => file.path)).toEqual([
			"README.md",
			"extension.json",
			"package.json",
			"src/index.ts",
			"tsconfig.json",
		]);
		const manifestFile = files.find((file) => file.path === "extension.json");
		expect(parseGameExtensionManifest(manifestFile?.content ?? "", manifestLimits)).toMatchObject({
			id: options.id,
			version: options.version,
			permissions: options.permissions,
		});
		const packageFile = JSON.parse(files.find((file) => file.path === "package.json")?.content ?? "null");
		expect(packageFile).toMatchObject({
			name: options.packageName,
			version: options.version,
			private: true,
			type: "module",
			dependencies: { "@opengameagent/extension-kit": options.extensionKitVersion },
			devDependencies: { typescript: options.typescriptVersion },
		});
		expect(files.map((file) => file.content).join("\n")).not.toMatch(/dynamic import|eval\(|character card|tavern/i);

		const root = await mkdtemp(join(process.cwd(), ".oga-extension-scaffold-test-"));
		roots.push(root);
		const destination = join(root, "generated");
		await writeGameExtensionScaffold(destination, options);
		const overlay = join(destination, "tsconfig.conformance.json");
		await writeFile(
			overlay,
			JSON.stringify({
				extends: "./tsconfig.json",
				compilerOptions: {
					composite: false,
					noEmit: true,
					rootDir: resolve(process.cwd()),
					paths: {
						"@opengameagent/extension-kit": [resolve(process.cwd(), "packages/extension-kit/src/index.ts")],
					},
				},
			}),
			"utf8",
		);
		await expect(
			execute(process.execPath, [resolve("node_modules/typescript/bin/tsc"), "-p", overlay, "--pretty", "false"]),
		).resolves.toMatchObject({ stderr: "" });
	});

	it("publishes the complete fixed file set through an exclusive destination and never overwrites", async () => {
		const root = await mkdtemp(join(process.cwd(), ".oga-extension-scaffold-test-"));
		roots.push(root);
		const destination = join(root, "new-extension");
		const result = await writeGameExtensionScaffold(destination, options);
		const expected = renderGameExtensionScaffold(options);

		expect(result.rootPath).toBe(destination);
		for (const file of expected) {
			await expect(readFile(join(destination, ...file.path.split("/")), "utf8")).resolves.toBe(file.content);
		}

		const existing = join(root, "existing");
		await mkdir(existing);
		await writeFile(join(existing, "keep.txt"), "keep", "utf8");
		await expect(writeGameExtensionScaffold(existing, options)).rejects.toMatchObject({ code: "target_exists" });
		await expect(readFile(join(existing, "keep.txt"), "utf8")).resolves.toBe("keep");

		const contested = join(root, "contested");
		const concurrent = await Promise.allSettled([
			writeGameExtensionScaffold(contested, options),
			writeGameExtensionScaffold(contested, options),
		]);
		expect(concurrent.filter((result) => result.status === "fulfilled")).toHaveLength(1);
		expect(concurrent.filter((result) => result.status === "rejected")).toMatchObject([
			{ reason: { code: "target_exists" } },
		]);
	});

	it("rejects invalid npm names and dependency versions before touching disk", async () => {
		expect(() => renderGameExtensionScaffold({ ...options, packageName: "HTTP" })).toThrowError(
			expect.objectContaining({ code: "scaffold_invalid" }),
		);
		expect(() => renderGameExtensionScaffold({ ...options, extensionKitVersion: "latest" })).toThrowError(
			expect.objectContaining({ code: "scaffold_invalid" }),
		);
		const compactManifest = JSON.stringify({
			schemaVersion: "1",
			id: options.id,
			version: options.version,
			permissions: options.permissions,
			dependencies: options.dependencies,
		});
		expect(() =>
			renderGameExtensionScaffold({
				...options,
				manifestLimits: {
					...manifestLimits,
					maxManifestBytes: new TextEncoder().encode(compactManifest).byteLength,
				},
			}),
		).toThrowError(expect.objectContaining({ code: "manifest_limit_exceeded" }));

		const root = await mkdtemp(join(process.cwd(), ".oga-extension-scaffold-test-"));
		roots.push(root);
		const destination = join(root, "untouched");
		await expect(
			writeGameExtensionScaffold(destination, { ...options, typescriptVersion: "next" }),
		).rejects.toMatchObject({ code: "scaffold_invalid" });
		await expect(readFile(destination, "utf8")).rejects.toMatchObject({ code: "ENOENT" });
	});
});
