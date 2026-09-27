import { generateKeyPairSync, type KeyObject, sign } from "node:crypto";
import { existsSync } from "node:fs";
import {
	access,
	chmod,
	lstat,
	mkdir,
	mkdtemp,
	readdir,
	readFile,
	rename,
	rm,
	stat,
	symlink,
	writeFile,
} from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { canonicalizeJsonValue } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import {
	AGENT_PLUGIN_MANIFEST_SCHEMA_V1,
	AGENT_PLUGIN_PACKAGE_MANIFEST_FILE,
	AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY,
	AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES,
	AGENT_PLUGIN_PACKAGE_SCHEMA_V1,
	AGENT_PLUGIN_PACKAGE_SIGNATURE_DOMAIN_V1,
	AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE,
	type AgentPluginPackageBuildResult,
	AgentPluginPackageError,
	type AgentPluginPackageLimits,
	agentPluginPackagePublicationNextStatePath,
	agentPluginPackagePublicationStatePath,
	buildSignedAgentPluginPackage,
	DirectoryAgentPluginCatalog,
	installSignedAgentPluginPackage,
} from "../src/index.js";

const PACKAGE_LIMITS: AgentPluginPackageLimits = {
	maxTraversalDepth: 8,
	maxDirectories: 32,
	maxEntriesPerDirectory: 64,
	maxVisitedEntries: 128,
	maxFiles: 64,
	maxPortablePathBytes: 512,
	maxPortablePathSegmentBytes: 240,
	maxAddressedPathBytes: 2_048,
	maxFileBytes: 65_536,
	maxTotalFileBytes: 262_144,
	maxPluginManifestBytes: 16_384,
	maxPackageManifestBytes: 65_536,
	maxSignaturesBytes: 16_384,
	maxJsonDepth: 16,
	maxSigners: 8,
	maxTrustedKeys: 8,
	maxSignatures: 8,
	maxKeyIdBytes: 128,
	maxReleaseSequenceBytes: 64,
	maxPluginVersionBytes: 256,
};

const CATALOG_LIMITS = {
	maxInstallations: 4,
	maxInstallationIdBytes: 64,
	maxAddressedPathBytes: 2_048,
	maxManifestBytes: 16_384,
	maxMcpConfigurationBytes: 32_768,
	maxTotalReadBytes: 65_536,
	maxJsonDepth: 16,
	maxDiagnostics: 16,
	maxMcpServersPerPlugin: 8,
	maxServerNameBytes: 128,
	maxExpandedValueBytes: 4_096,
} as const;

const temporaryDirectories: string[] = [];

afterEach(async () => {
	for (const path of temporaryDirectories.splice(0)) {
		await makeRemovable(path);
		await rm(path, { force: true, recursive: true });
	}
});

describe("signed Agent Plugin directory distribution", () => {
	it("derives the fixed publication-state bound from the complete-record grammar", () => {
		const maximumUint64 = (2n ** 64n - 1n).toString();
		const maximumRecord = `complete:${"a".repeat(64)}:${maximumUint64}:${maximumUint64}`;
		expect(Buffer.byteLength(maximumRecord)).toBe(AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES);
		expect(AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES).toBe(115);
	});

	it("ships public schemas aligned with the exact package discriminator and algorithm encodings", async () => {
		const schemaRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "schemas");
		const manifestSchema = JSON.parse(
			await readFile(join(schemaRoot, "agent-plugin-package-manifest-v1.schema.json"), "utf8"),
		) as Record<string, unknown>;
		const signaturesSchema = JSON.parse(
			await readFile(join(schemaRoot, "agent-plugin-package-signatures-v1.schema.json"), "utf8"),
		) as Record<string, unknown>;
		expect(manifestSchema).toMatchObject({
			$id: "urn:opengameagent:agent-plugin-package:manifest:1",
			properties: {
				schema: { const: AGENT_PLUGIN_PACKAGE_SCHEMA_V1 },
				files: {
					minItems: 1,
					minContains: 1,
					maxContains: 1,
					contains: { properties: { path: { const: "plugin.json" } } },
				},
			},
		});
		expect(signaturesSchema).toMatchObject({
			$id: "urn:opengameagent:agent-plugin-package:signatures:1",
			properties: {
				schema: { const: AGENT_PLUGIN_PACKAGE_SCHEMA_V1 },
				signatures: {
					minItems: 1,
					items: { properties: { signature: { pattern: "^[A-Za-z0-9_-]{85}[AQgw]$" } } },
				},
			},
		});
	});

	it("builds a signed complete portable plugin without executing it", async () => {
		const fixture = await createFixture("world.tools", "1.2.3");
		const marker = join(fixture.parent, "executed");
		await writeFile(
			join(fixture.source, "server.mjs"),
			`await import("node:fs/promises").then(fs => fs.writeFile(${JSON.stringify(marker)}, "bad"));\n`,
			"utf8",
		);
		await mkdir(join(fixture.source, "skills", "observe"), { recursive: true });
		await writeFile(
			join(fixture.source, "skills", "observe", "SKILL.md"),
			"---\nname: observe\ndescription: Observe the game.\n---\n",
			"utf8",
		);
		await writeFile(
			join(fixture.source, "mcp.json"),
			JSON.stringify({
				$schema: "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
				mcpServers: {
					local: { type: "stdio", command: "node", args: [`\${PLUGIN_ROOT}/server.mjs`] },
				},
			}),
			"utf8",
		);
		const signed = await build(fixture, "7", ["server.mjs"]);
		expect(signed.pluginName).toBe("world.tools");
		expect(signed.pluginVersion).toBe("1.2.3");
		expect(signed.releaseSequence).toBe("7");
		expect(signed.files.map((file) => file.path)).toEqual([
			"mcp.json",
			"plugin.json",
			"server.mjs",
			"skills/observe/SKILL.md",
		]);
		expect(signed.files.find((file) => file.path === "server.mjs")?.executable).toBe(true);
		expect(await readdir(fixture.packageRoot)).toEqual([
			"io.opengameagent.package",
			"mcp.json",
			"plugin.json",
			"server.mjs",
			"skills",
		]);
		await expect(access(marker)).rejects.toBeDefined();
	});

	it("immutably installs verified files without package metadata, execution, or plugin-data mutation", async () => {
		const fixture = await createFixture("install.complete", "1");
		const marker = join(fixture.parent, "executed");
		await writeFile(
			join(fixture.source, "inert.mjs"),
			`await import("node:fs/promises").then(fs => fs.writeFile(${JSON.stringify(marker)}, "bad"));\n`,
			"utf8",
		);
		await mkdir(fixture.data, { recursive: true });
		await writeFile(join(fixture.data, "keep"), "persistent", "utf8");
		const signed = await build(fixture, "1");
		const installed = await install(fixture, signed);
		expect(installed.packageDigest).toBe(signed.packageDigest);
		expect(installed.rootPath).toBe(join(fixture.store, signed.packageDigest));
		expect(await readdir(installed.rootPath)).toEqual(["inert.mjs", "plugin.json"]);
		await expect(access(join(installed.rootPath, AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY))).rejects.toBeDefined();
		await expect(access(marker)).rejects.toBeDefined();
		expect(await readFile(join(fixture.data, "keep"), "utf8")).toBe("persistent");

		await expect(access(marker)).rejects.toBeDefined();
	});

	it("requires the host threshold from distinct trusted Ed25519 key IDs", async () => {
		const fixture = await createFixture("threshold", "1");
		const second = generateEd25519KeyPair();
		await build(fixture, "1");

		await expect(
			installSignedAgentPluginPackage(
				{
					cwd: fixture.parent,
					packageRoot: fixture.packageRoot,
					storeRoot: fixture.store,
					trustedKeys: [
						{ keyId: fixture.signer.keyId, publicKey: fixture.publicKey },
						{ keyId: "release-b", publicKey: second.publicKey },
					],
					requiredSignatures: 2,
					limits: PACKAGE_LIMITS,
				},
				new AbortController().signal,
			),
		).rejects.toMatchObject({ code: "signature_invalid" });
		const signed = await build(
			fixture,
			"2",
			[],
			[fixture.signer, { keyId: "release-b", privateKey: second.privateKey }],
			fixture.packageRoot,
			{},
			true,
		);

		await expect(
			installSignedAgentPluginPackage(
				{
					cwd: fixture.parent,
					packageRoot: fixture.packageRoot,
					storeRoot: fixture.store,
					trustedKeys: [
						{ keyId: fixture.signer.keyId, publicKey: fixture.publicKey },
						{ keyId: "release-b", publicKey: second.publicKey },
					],
					requiredSignatures: 2,
					limits: PACKAGE_LIMITS,
				},
				new AbortController().signal,
			),
		).resolves.toMatchObject({ packageDigest: signed.packageDigest });

		await expect(
			build(fixture, "2", [], [fixture.signer, { ...fixture.signer }], join(fixture.parent, "duplicate")),
		).rejects.toMatchObject({ code: "invalid_configuration" });

		expect(() =>
			installSignedAgentPluginPackage(
				{
					cwd: fixture.parent,
					packageRoot: fixture.packageRoot,
					storeRoot: join(fixture.parent, "duplicate-key-store"),
					trustedKeys: [
						{ keyId: "alias-a", publicKey: fixture.publicKey },
						{ keyId: "alias-b", publicKey: fixture.publicKey },
					],
					requiredSignatures: 2,
					limits: PACKAGE_LIMITS,
				},
				new AbortController().signal,
			),
		).toThrow(AgentPluginPackageError);

		try {
			installSignedAgentPluginPackage(
				{
					cwd: fixture.parent,
					packageRoot: fixture.packageRoot,
					storeRoot: join(fixture.parent, "fake-key-store"),
					trustedKeys: [
						{
							keyId: "fake",
							publicKey: { type: "public", asymmetricKeyType: "ed25519" } as unknown as KeyObject,
						},
					],
					requiredSignatures: 1,
					limits: PACKAGE_LIMITS,
				},
				new AbortController().signal,
			);
			throw new Error("fake KeyObject accepted");
		} catch (error) {
			expect(error).toMatchObject({ code: "invalid_configuration" });
		}
	});

	it.each(["tampered", "missing", "extra"] as const)("rejects a %s package file set", async (mode) => {
		const fixture = await createFixture(`files-${mode}`, "1");
		await writeFile(join(fixture.source, "payload.txt"), "original", "utf8");
		const signed = await build(fixture, "1");
		if (mode === "tampered") await writeSealedFile(join(fixture.packageRoot, "payload.txt"), "changed!");
		if (mode === "missing") await removeSealedEntry(join(fixture.packageRoot, "payload.txt"));
		if (mode === "extra") await writeSealedFile(join(fixture.packageRoot, "unsigned.txt"), "unsigned");
		await expect(install(fixture, signed)).rejects.toMatchObject({ code: "package_invalid" });
	});

	it("rejects canonical-manifest and signature tampering", async () => {
		const fixture = await createFixture("metadata", "1");
		const signed = await build(fixture, "1");
		const manifestPath = join(
			fixture.packageRoot,
			AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY,
			AGENT_PLUGIN_PACKAGE_MANIFEST_FILE,
		);
		const original = await readFile(manifestPath, "utf8");
		await writeSealedFile(manifestPath, `${original}\n`);
		await expect(install(fixture, signed)).rejects.toMatchObject({ code: "package_invalid" });

		await writeSealedFile(manifestPath, original);
		const signaturesPath = join(
			fixture.packageRoot,
			AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY,
			AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE,
		);
		const signatures = JSON.parse(await readFile(signaturesPath, "utf8")) as {
			schema: string;
			signatures: Array<{ keyId: string; signature: string }>;
		};
		const first = signatures.signatures[0];
		if (first === undefined) throw new Error("fixture signature missing");
		signatures.signatures[0] = { ...first, signature: "A".repeat(86) };
		await writeSealedFile(signaturesPath, canonicalizeJsonValue(signatures));
		await expect(install(fixture, signed)).rejects.toMatchObject({ code: "signature_invalid" });
	});

	it("rejects a validly signed unsafe portable path before touching the store", async () => {
		const fixture = await createFixture("unsafe-path", "1");
		const signed = await build(fixture, "1");
		for (const unsafePath of ["../escape", "bad?.txt", "COM¹.txt", "trailing."]) {
			const unsafeManifest = {
				...signed.manifest,
				files: signed.manifest.files.map((file, index) => (index === 0 ? { ...file, path: unsafePath } : file)),
			};
			await rewriteSignedMetadata(fixture, unsafeManifest);
			await expect(install(fixture, signed)).rejects.toMatchObject({ code: "package_invalid" });
		}
		await expect(access(fixture.store)).rejects.toBeDefined();
	});

	it("rejects a validly signed package identity that disagrees with plugin.json", async () => {
		const fixture = await createFixture("identity", "1");
		const signed = await build(fixture, "1");
		await rewriteSignedMetadata(fixture, {
			...signed.manifest,
			plugin: { name: "different", version: signed.pluginVersion },
		});
		await expect(install(fixture, signed)).rejects.toMatchObject({ code: "package_invalid" });
	});

	it("rejects source and package junctions or symlinks instead of following them", async () => {
		const sourceFixture = await createFixture("source-link", "1");
		const outside = join(sourceFixture.parent, "outside");
		await mkdir(outside);
		await writeFile(join(outside, "outside.txt"), "outside", "utf8");
		await symlink(outside, join(sourceFixture.source, "linked"), process.platform === "win32" ? "junction" : "dir");
		await expect(build(sourceFixture, "1")).rejects.toMatchObject({ code: "source_invalid" });

		const packageFixture = await createFixture("package-link", "1");
		await mkdir(join(packageFixture.source, "nested"));
		await writeFile(join(packageFixture.source, "nested", "inside.txt"), "inside", "utf8");
		const signed = await build(packageFixture, "1");
		await replaceSealedEntryWithSymlink(join(packageFixture.packageRoot, "nested"), outside);
		await expect(install(packageFixture, signed)).rejects.toMatchObject({ code: "package_invalid" });

		const rootFixture = await createFixture("root-link", "1");
		const rootSigned = await build(rootFixture, "1");
		const realPackage = join(rootFixture.parent, "real-package");
		await rename(rootFixture.packageRoot, realPackage);
		await symlink(realPackage, rootFixture.packageRoot, process.platform === "win32" ? "junction" : "dir");
		await expect(install(rootFixture, rootSigned)).rejects.toMatchObject({ code: "package_invalid" });
	});

	it("allows exact idempotent reinstallation only after revalidation", async () => {
		const fixture = await createFixture("idempotent", "1");
		const release = await build(fixture, "1");
		const installed = await install(fixture, release);
		await expect(install(fixture, release, installed)).resolves.toEqual(installed);
	});

	it("installs a higher-sequence release without removing the prior immutable object", async () => {
		const fixture = await createFixture("upgrade", "1");
		await writeFile(join(fixture.source, "payload.txt"), "one", "utf8");
		const releaseOne = await build(fixture, "1");
		const installedOne = await install(fixture, releaseOne);
		await writeFile(join(fixture.source, "payload.txt"), "two", "utf8");
		const secondRoot = join(fixture.parent, "package-two");
		const releaseTwo = await build(fixture, "2", [], [fixture.signer], secondRoot);
		const installedTwo = await install(fixture, releaseTwo, installedOne, secondRoot);
		expect(installedTwo.packageDigest).not.toBe(installedOne.packageDigest);
		await expect(access(installedOne.rootPath)).resolves.toBeUndefined();
	});

	it("refuses lower/equal-sequence replacements and cross-plugin current state", async () => {
		const fixture = await createFixture("ordered", "1");
		const lower = await build(fixture, "1");
		const current = {
			pluginName: lower.pluginName,
			pluginVersion: "current",
			releaseSequence: "2",
			packageDigest: "a".repeat(64),
			rootPath: join(fixture.parent, "current-object"),
		};
		await expect(install(fixture, lower, current)).rejects.toMatchObject({ code: "rollback_rejected" });

		await writeFile(join(fixture.source, "payload.txt"), "different", "utf8");
		const equalRoot = join(fixture.parent, "package-equal");
		const equal = await build(fixture, "2", [], [fixture.signer], equalRoot);
		await expect(install(fixture, equal, current, equalRoot)).rejects.toMatchObject({ code: "rollback_rejected" });

		const foreignFixture = await createFixture("foreign", "1");
		const foreign = await build(foreignFixture, "3");
		await expect(install(foreignFixture, foreign, current)).rejects.toMatchObject({ code: "rollback_rejected" });
	});

	it("does not overwrite a pre-existing corrupt content-addressed destination", async () => {
		const fixture = await createFixture("conflict", "1");
		const signed = await build(fixture, "1");
		const destination = join(fixture.store, signed.packageDigest);
		await mkdir(destination, { recursive: true });
		await writeFile(join(destination, "plugin.json"), "corrupt", "utf8");
		await expect(install(fixture, signed)).rejects.toMatchObject({ code: "installation_conflict" });
		expect(await readFile(join(destination, "plugin.json"), "utf8")).toBe("corrupt");
	});

	it("reserves build destinations without replacing even an empty directory", async () => {
		const fixture = await createFixture("build-conflict", "1");
		await mkdir(fixture.packageRoot);
		await expect(build(fixture, "1")).rejects.toMatchObject({ code: "installation_conflict" });
		expect(await readdir(fixture.packageRoot)).toEqual([]);
	});

	it("keeps completed publication state monotonic when a late builder targets the same root", async () => {
		const fixture = await createFixture("monotonic-build", "1");
		const signed = await build(fixture, "1");
		const statePath = agentPluginPackagePublicationStatePath(fixture.packageRoot);
		const completeState = await readFile(statePath, "utf8");
		expect(completeState).toMatch(new RegExp(`^complete:${signed.packageDigest}:[0-9]+:[0-9]+$`, "u"));
		await expect(build(fixture, "1")).rejects.toMatchObject({ code: "installation_conflict" });
		expect(await readFile(statePath, "utf8")).toBe(completeState);
	});

	it("preserves one completed state across exact idempotent installation", async () => {
		const fixture = await createFixture("monotonic-install", "1");
		const signed = await build(fixture, "1");
		const installed = await install(fixture, signed);
		const statePath = agentPluginPackagePublicationStatePath(installed.rootPath);
		const completeState = await readFile(statePath, "utf8");
		await expect(install(fixture, signed, installed)).resolves.toEqual(installed);
		expect(await readFile(statePath, "utf8")).toBe(completeState);
	});

	it("resumes exact incomplete build and install drafts without deleting by path", async () => {
		const buildFixture = await createFixture("resume-build", "1");
		const referenceRoot = join(buildFixture.parent, "reference-package");
		const reference = await build(buildFixture, "1", [], [buildFixture.signer], referenceRoot);
		await createIncompleteDraft(buildFixture.packageRoot, {
			"plugin.json": await readFile(join(referenceRoot, "plugin.json")),
		});
		const resumed = await build(buildFixture, "1");
		expect(resumed.packageDigest).toBe(reference.packageDigest);
		expect(await readFile(agentPluginPackagePublicationStatePath(buildFixture.packageRoot), "utf8")).toMatch(
			new RegExp(`^complete:${reference.packageDigest}:[0-9]+:[0-9]+$`, "u"),
		);

		const installFixture = await createFixture("resume-install", "1");
		const signed = await build(installFixture, "1");
		await mkdir(installFixture.store);
		const objectRoot = join(installFixture.store, signed.packageDigest);
		await createIncompleteDraft(objectRoot, {
			"plugin.json": await readFile(join(installFixture.packageRoot, "plugin.json")),
		});
		const installed = await install(installFixture, signed);
		expect(installed.rootPath).toBe(objectRoot);
		expect(await readFile(agentPluginPackagePublicationStatePath(objectRoot), "utf8")).toMatch(
			new RegExp(`^complete:${signed.packageDigest}:[0-9]+:[0-9]+$`, "u"),
		);
	});

	it.runIf(process.platform === "win32")("restores readonly mode while resuming exact Windows drafts", async () => {
		const fixture = await createFixture("resume-windows-readonly", "1");
		const referenceRoot = join(fixture.parent, "reference-windows-package");
		await build(fixture, "1", [], [fixture.signer], referenceRoot);
		await createIncompleteDraft(fixture.packageRoot, {
			"plugin.json": await readFile(join(referenceRoot, "plugin.json")),
		});
		const draftFile = join(fixture.packageRoot, "plugin.json");
		await chmod(draftFile, 0o666);
		await build(fixture, "1");
		expect((await stat(draftFile)).mode & 0o222).toBe(0);
	});

	it("leaves a mismatched incomplete draft inert instead of overwriting or recursively deleting it", async () => {
		const fixture = await createFixture("resume-mismatch", "1");
		await createIncompleteDraft(fixture.packageRoot, { "plugin.json": Buffer.from("corrupt", "utf8") });
		await expect(build(fixture, "1")).rejects.toMatchObject({ code: "operation_incomplete" });
		expect(await readFile(join(fixture.packageRoot, "plugin.json"), "utf8")).toBe("corrupt");
		expect(await readFile(agentPluginPackagePublicationStatePath(fixture.packageRoot), "utf8")).toBe("incomplete");
	});

	it("keeps incomplete publications unavailable to the directory catalog", async () => {
		const fixture = await createFixture("draft", "1");
		const draftRoot = join(fixture.parent, "draft-root");
		await mkdir(draftRoot);
		await writeFile(
			join(draftRoot, "plugin.json"),
			JSON.stringify({ $schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1, name: "draft", version: "1" }),
			"utf8",
		);
		await writeFile(agentPluginPackagePublicationStatePath(draftRoot), "incomplete", "utf8");
		const draftAlias = join(fixture.parent, "draft-alias");
		await symlink(draftRoot, draftAlias, process.platform === "win32" ? "junction" : "dir");
		const snapshot = await new DirectoryAgentPluginCatalog({
			cwd: fixture.parent,
			installations: [{ installationId: "draft", rootPath: draftAlias, dataPath: fixture.data }],
			limits: CATALOG_LIMITS,
		}).load(new AbortController().signal);
		expect(snapshot.plugins).toEqual([]);
		expect(snapshot.diagnostics).toEqual([{ installationId: "draft", code: "plugin_unavailable" }]);
	});

	it("loads a managed completed root only while its persistent state matches the exact directory identity", async () => {
		const fixture = await createFixture("managed-catalog", "1");
		const signed = await build(fixture, "1");
		const installed = await install(fixture, signed);
		const catalog = new DirectoryAgentPluginCatalog({
			cwd: fixture.parent,
			installations: [{ installationId: "managed", rootPath: installed.rootPath, dataPath: fixture.data }],
			limits: CATALOG_LIMITS,
		});
		await expect(catalog.load(new AbortController().signal)).resolves.toMatchObject({
			plugins: [{ installationId: "managed" }],
			diagnostics: [],
		});

		const statePath = agentPluginPackagePublicationStatePath(installed.rootPath);
		const state = await readFile(statePath, "utf8");
		const fields = state.split(":");
		const inode = fields[3];
		if (inode === undefined) throw new Error("fixture publication state is incomplete");
		fields[3] = (BigInt(inode) + 1n).toString();
		await chmod(statePath, 0o600);
		await writeFile(statePath, fields.join(":"), "utf8");
		await chmod(statePath, 0o444);
		await expect(catalog.load(new AbortController().signal)).resolves.toMatchObject({
			plugins: [],
			diagnostics: [{ installationId: "managed", code: "plugin_unavailable" }],
		});
	});

	it("rejects UTF-8 BOM ambiguity", async () => {
		const bomFixture = await createFixture("bom", "1");
		const bomSigned = await build(bomFixture, "1");
		const manifestPath = join(
			bomFixture.packageRoot,
			AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY,
			AGENT_PLUGIN_PACKAGE_MANIFEST_FILE,
		);
		const manifest = await readFile(manifestPath);
		await writeSealedFile(manifestPath, Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), manifest]));
		await expect(install(bomFixture, bomSigned)).rejects.toMatchObject({ code: "package_invalid" });
	});

	it("enforces independent host limits during build and verification", async () => {
		const fixture = await createFixture("limits", "1");
		await writeFile(join(fixture.source, "payload.txt"), "payload", "utf8");
		await expect(
			build(fixture, "1", [], [fixture.signer], fixture.packageRoot, { maxFileBytes: 3 }),
		).rejects.toMatchObject({
			code: "package_limit_exceeded",
		});

		const signed = await build(fixture, "1", [], [fixture.signer], fixture.packageRoot, {}, true);
		await expect(
			install(fixture, signed, undefined, fixture.packageRoot, { maxSignaturesBytes: 1 }),
		).rejects.toMatchObject({
			code: "package_limit_exceeded",
		});
		await expect(install(fixture, signed, undefined, fixture.packageRoot, { maxFiles: 1 })).rejects.toMatchObject({
			code: "package_limit_exceeded",
		});

		const addressedFixture = await createFixture("addressed", "1");
		const addressedDestination = join(addressedFixture.parent, "destination-with-a-longer-name");
		const addressedLimit = Math.max(
			Buffer.byteLength(join(addressedFixture.source, "plugin.json")),
			Buffer.byteLength(addressedDestination),
		);
		await expect(
			build(addressedFixture, "1", [], [addressedFixture.signer], addressedDestination, {
				maxAddressedPathBytes: addressedLimit,
			}),
		).rejects.toMatchObject({ code: "package_limit_exceeded" });
		await expect(access(addressedDestination)).rejects.toBeDefined();

		expect(() =>
			buildSignedAgentPluginPackage(
				{
					cwd: fixture.parent,
					sourceRoot: fixture.source,
					packageRoot: join(fixture.parent, "too-many-executables"),
					releaseSequence: "1",
					executablePaths: ["a", "b"],
					signers: [createSigner(fixture.signer)],
					limits: { ...PACKAGE_LIMITS, maxFiles: 1 },
				},
				new AbortController().signal,
			),
		).toThrow(AgentPluginPackageError);
	});

	it("rejects before collecting more directory entries than the host allows", async () => {
		const fixture = await createFixture("directory-limits", "1");
		await writeFile(join(fixture.source, "first.txt"), "first", "utf8");
		await writeFile(join(fixture.source, "second.txt"), "second", "utf8");
		await expect(
			build(fixture, "1", [], [fixture.signer], fixture.packageRoot, { maxEntriesPerDirectory: 2 }),
		).rejects.toMatchObject({ code: "package_limit_exceeded" });
		await expect(
			build(fixture, "1", [], [fixture.signer], join(fixture.parent, "visited-limit"), {
				maxVisitedEntries: 2,
			}),
		).rejects.toMatchObject({ code: "package_limit_exceeded" });
	});

	it("preserves the exact abort reason before publication", async () => {
		const fixture = await createFixture("abort", "1");
		const controller = new AbortController();
		const reason = new Error("caller stopped signing");
		let entered = false;
		const pending = buildSignedAgentPluginPackage(
			{
				cwd: fixture.parent,
				sourceRoot: fixture.source,
				packageRoot: fixture.packageRoot,
				releaseSequence: "1",
				executablePaths: [],
				signers: [
					{
						keyId: "stalled",
						sign: async () => {
							entered = true;
							return await new Promise<Uint8Array>(() => undefined);
						},
					},
				],
				limits: PACKAGE_LIMITS,
			},
			controller.signal,
		);
		await expect.poll(() => entered).toBe(true);
		controller.abort(reason);
		await expect(pending).rejects.toBe(reason);
		await expect(access(fixture.packageRoot)).rejects.toBeDefined();
	});

	it("leaves an exact resumable draft when cancellation follows the first durable publication file", async () => {
		const fixture = await createFixture("abort-draft", "1");
		const controller = new AbortController();
		const reason = new Error("caller stopped after durable file publication");
		const firstPublishedFile = join(fixture.packageRoot, "plugin.json");
		const originalThrowIfAborted = controller.signal.throwIfAborted.bind(controller.signal);
		Object.defineProperty(controller.signal, "throwIfAborted", {
			value: () => {
				if (!controller.signal.aborted && existsSync(firstPublishedFile)) controller.abort(reason);
				originalThrowIfAborted();
			},
		});

		const interrupted = buildSignedAgentPluginPackage(
			{
				cwd: fixture.parent,
				sourceRoot: fixture.source,
				packageRoot: fixture.packageRoot,
				releaseSequence: "1",
				executablePaths: [],
				signers: [createSigner(fixture.signer)],
				limits: PACKAGE_LIMITS,
			},
			controller.signal,
		);
		await expect(interrupted).rejects.toBe(reason);
		expect(await readFile(agentPluginPackagePublicationStatePath(fixture.packageRoot), "utf8")).toBe("incomplete");
		expect(await readFile(firstPublishedFile)).toEqual(await readFile(join(fixture.source, "plugin.json")));
		expect((await stat(firstPublishedFile)).mode & 0o222).toBe(0);

		const resumed = await build(fixture, "1");
		expect(await readFile(agentPluginPackagePublicationStatePath(fixture.packageRoot), "utf8")).toMatch(
			new RegExp(`^complete:${resumed.packageDigest}:[0-9]+:[0-9]+$`, "u"),
		);
	});

	it.runIf(process.platform !== "win32")("applies only explicitly declared executable intent", async () => {
		const fixture = await createFixture("mode", "1");
		await writeFile(join(fixture.source, "run.sh"), "#!/bin/sh\n", { mode: 0o600 });
		await writeFile(join(fixture.source, "plain.txt"), "plain", { mode: 0o777 });
		const signed = await build(fixture, "1", ["run.sh"]);
		const installed = await install(fixture, signed);
		expect((await stat(join(installed.rootPath, "run.sh"))).mode & 0o111).not.toBe(0);
		expect((await stat(join(installed.rootPath, "plain.txt"))).mode & 0o111).toBe(0);
	});

	it("marks installed files read-only on the current platform", async () => {
		const fixture = await createFixture("readonly", "1");
		const signed = await build(fixture, "1");
		const installed = await install(fixture, signed);
		await expect(writeFile(join(installed.rootPath, "plugin.json"), "mutated", "utf8")).rejects.toBeDefined();
	});

	it.runIf(process.platform === "win32")(
		"rejects a completed Windows object whose readonly mode was cleared",
		async () => {
			const fixture = await createFixture("windows-revalidation", "1");
			const signed = await build(fixture, "1");
			const installed = await install(fixture, signed);
			await chmod(join(installed.rootPath, "plugin.json"), 0o666);
			await expect(install(fixture, signed, installed)).rejects.toMatchObject({ code: "installation_conflict" });
		},
	);

	it.runIf(process.platform !== "win32")("rejects executable-intent tampering in the signed package", async () => {
		const fixture = await createFixture("mode-tamper", "1");
		await writeFile(join(fixture.source, "run.sh"), "#!/bin/sh\n", { mode: 0o600 });
		const signed = await build(fixture, "1", ["run.sh"]);
		await chmod(join(fixture.packageRoot, "run.sh"), 0o644);
		await expect(install(fixture, signed)).rejects.toMatchObject({ code: "package_invalid" });
	});

	it("validates and snapshots every host limit instead of adopting mutable defaults", async () => {
		const fixture = await createFixture("configuration", "1");
		for (const name of Object.keys(PACKAGE_LIMITS) as Array<keyof AgentPluginPackageLimits>) {
			expect(() =>
				buildSignedAgentPluginPackage(
					{
						cwd: fixture.parent,
						sourceRoot: fixture.source,
						packageRoot: fixture.packageRoot,
						releaseSequence: "1",
						executablePaths: [],
						signers: [createSigner(fixture.signer)],
						limits: { ...PACKAGE_LIMITS, [name]: 0 },
					},
					new AbortController().signal,
				),
			).toThrow(AgentPluginPackageError);
		}

		const limits = { ...PACKAGE_LIMITS };
		const operation = buildSignedAgentPluginPackage(
			{
				cwd: fixture.parent,
				sourceRoot: fixture.source,
				packageRoot: fixture.packageRoot,
				releaseSequence: "1",
				executablePaths: [],
				signers: [createSigner(fixture.signer)],
				limits,
			},
			new AbortController().signal,
		);
		limits.maxFileBytes = 1;
		await expect(operation).resolves.toMatchObject({ pluginName: "configuration" });
	});
});

interface Fixture {
	readonly parent: string;
	readonly source: string;
	readonly packageRoot: string;
	readonly store: string;
	readonly data: string;
	readonly signer: { readonly keyId: string; readonly privateKey: KeyObject };
	readonly publicKey: KeyObject;
}

async function createFixture(name: string, version: string): Promise<Fixture> {
	const here = dirname(fileURLToPath(import.meta.url));
	const parent = await mkdtemp(join(here, ".agent-plugin-package-test-"));
	temporaryDirectories.push(parent);
	const source = join(parent, "source");
	await mkdir(source);
	await writeFile(
		join(source, "plugin.json"),
		JSON.stringify({ $schema: AGENT_PLUGIN_MANIFEST_SCHEMA_V1, name, version }),
		"utf8",
	);
	const keys = generateEd25519KeyPair();
	return {
		parent,
		source,
		packageRoot: join(parent, "package"),
		store: join(parent, "store"),
		data: join(parent, "data"),
		signer: { keyId: "release-a", privateKey: keys.privateKey },
		publicKey: keys.publicKey,
	};
}

function generateEd25519KeyPair(): { readonly publicKey: KeyObject; readonly privateKey: KeyObject } {
	const keys = generateKeyPairSync("ed25519");
	return { publicKey: keys.publicKey as KeyObject, privateKey: keys.privateKey as KeyObject };
}

function createSigner(signer: Fixture["signer"]) {
	return {
		keyId: signer.keyId,
		sign: (message: Uint8Array) => new Uint8Array(sign(null, message, signer.privateKey)),
	};
}

async function build(
	fixture: Fixture,
	releaseSequence: string,
	executablePaths: readonly string[] = [],
	signers: readonly Fixture["signer"][] = [fixture.signer],
	packageRoot = fixture.packageRoot,
	limitOverrides: Partial<AgentPluginPackageLimits> = {},
	reuseRoot = false,
): Promise<AgentPluginPackageBuildResult> {
	if (reuseRoot) {
		const publicationState = agentPluginPackagePublicationStatePath(packageRoot);
		await chmod(publicationState, 0o600).catch(() => undefined);
		await rm(publicationState, { force: true });
		const nextPublicationState = agentPluginPackagePublicationNextStatePath(packageRoot);
		await chmod(nextPublicationState, 0o600).catch(() => undefined);
		await rm(nextPublicationState, { force: true });
		await makeRemovable(packageRoot);
		await rm(packageRoot, { force: true, recursive: true });
	}
	return await buildSignedAgentPluginPackage(
		{
			cwd: fixture.parent,
			sourceRoot: fixture.source,
			packageRoot,
			releaseSequence,
			executablePaths,
			signers: signers.map(createSigner),
			limits: { ...PACKAGE_LIMITS, ...limitOverrides },
		},
		new AbortController().signal,
	);
}

async function install(
	fixture: Fixture,
	_signed: AgentPluginPackageBuildResult,
	currentInstallation?: Awaited<ReturnType<typeof installSignedAgentPluginPackage>>,
	packageRoot = fixture.packageRoot,
	limitOverrides: Partial<AgentPluginPackageLimits> = {},
) {
	return await installSignedAgentPluginPackage(
		{
			cwd: fixture.parent,
			packageRoot,
			storeRoot: fixture.store,
			trustedKeys: [{ keyId: fixture.signer.keyId, publicKey: fixture.publicKey }],
			requiredSignatures: 1,
			...(currentInstallation === undefined ? {} : { currentInstallation }),
			limits: { ...PACKAGE_LIMITS, ...limitOverrides },
		},
		new AbortController().signal,
	);
}

async function rewriteSignedMetadata(fixture: Fixture, manifest: unknown): Promise<void> {
	const manifestText = canonicalizeJsonValue(manifest);
	const payload = Buffer.concat([
		Buffer.from(AGENT_PLUGIN_PACKAGE_SIGNATURE_DOMAIN_V1, "utf8"),
		Buffer.from(manifestText, "utf8"),
	]);
	const signature = sign(null, payload, fixture.signer.privateKey).toString("base64url");
	const metadataRoot = join(fixture.packageRoot, AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY);
	await writeSealedFile(join(metadataRoot, AGENT_PLUGIN_PACKAGE_MANIFEST_FILE), manifestText);
	await writeSealedFile(
		join(metadataRoot, AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE),
		canonicalizeJsonValue({
			schema: AGENT_PLUGIN_PACKAGE_SCHEMA_V1,
			signatures: [{ keyId: fixture.signer.keyId, signature }],
		}),
	);
}

async function writeSealedFile(path: string, contents: string | Uint8Array): Promise<void> {
	const parent = dirname(path);
	if (process.platform !== "win32") await chmod(parent, 0o755);
	try {
		await chmod(path, 0o600).catch(() => undefined);
		await writeFile(path, contents);
		await chmod(path, 0o444);
	} finally {
		if (process.platform !== "win32") await chmod(parent, 0o555).catch(() => undefined);
	}
}

async function createIncompleteDraft(root: string, files: Readonly<Record<string, Uint8Array>>): Promise<void> {
	await mkdir(root, { mode: 0o755 });
	if (process.platform !== "win32") await chmod(root, 0o755);
	const statePath = agentPluginPackagePublicationStatePath(root);
	await writeFile(statePath, "incomplete");
	await chmod(statePath, 0o444);
	for (const [portablePath, bytes] of Object.entries(files)) {
		const target = join(root, ...portablePath.split("/"));
		await mkdir(dirname(target), { recursive: true, mode: 0o755 });
		await writeFile(target, bytes);
		await chmod(target, 0o444);
	}
}

async function removeSealedEntry(path: string): Promise<void> {
	const parent = dirname(path);
	if (process.platform !== "win32") await chmod(parent, 0o755);
	try {
		await makeRemovable(path);
		await rm(path, { force: true, recursive: true });
	} finally {
		if (process.platform !== "win32") await chmod(parent, 0o555).catch(() => undefined);
	}
}

async function replaceSealedEntryWithSymlink(path: string, target: string): Promise<void> {
	const parent = dirname(path);
	if (process.platform !== "win32") await chmod(parent, 0o755);
	try {
		await makeRemovable(path);
		await rm(path, { force: true, recursive: true });
		await symlink(target, path, process.platform === "win32" ? "junction" : "dir");
	} finally {
		if (process.platform !== "win32") await chmod(parent, 0o555).catch(() => undefined);
	}
}

async function makeRemovable(path: string): Promise<void> {
	if (process.platform === "win32") return;
	let info: Awaited<ReturnType<typeof lstat>>;
	try {
		info = await lstat(path);
	} catch {
		return;
	}
	if (info.isSymbolicLink()) return;
	if (!info.isDirectory()) {
		await chmod(path, 0o600).catch(() => undefined);
		return;
	}
	await chmod(path, 0o700).catch(() => undefined);
	for (const entry of await readdir(path)) await makeRemovable(join(path, entry));
}
