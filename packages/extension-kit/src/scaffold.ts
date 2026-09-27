import { lstat, mkdir, open, realpath, rmdir, unlink } from "node:fs/promises";
import { basename, dirname, join, resolve } from "node:path";
import { valid } from "semver";
import validatePackageName from "validate-npm-package-name";
import {
	GAME_EXTENSION_MANIFEST_SCHEMA_VERSION,
	type GameExtensionDependency,
	type GameExtensionManifestLimits,
	type GameExtensionPermission,
	parseGameExtensionManifest,
} from "./manifest.js";

export interface GameExtensionScaffoldOptions {
	readonly id: string;
	readonly version: string;
	readonly packageName: string;
	readonly extensionKitVersion: string;
	readonly typescriptVersion: string;
	readonly permissions: readonly GameExtensionPermission[];
	readonly dependencies: readonly GameExtensionDependency[];
	readonly manifestLimits: GameExtensionManifestLimits;
}

export interface GameExtensionScaffoldFile {
	readonly path: "README.md" | "extension.json" | "package.json" | "src/index.ts" | "tsconfig.json";
	readonly content: string;
}

export type GameExtensionScaffoldErrorCode =
	| "scaffold_invalid"
	| "destination_invalid"
	| "target_exists"
	| "scaffold_incomplete";

export class GameExtensionScaffoldError extends Error {
	constructor(public readonly code: GameExtensionScaffoldErrorCode) {
		super(code);
		this.name = "GameExtensionScaffoldError";
	}
}

export interface WrittenGameExtensionScaffold {
	readonly rootPath: string;
	readonly files: readonly GameExtensionScaffoldFile[];
}

export function renderGameExtensionScaffold(
	options: GameExtensionScaffoldOptions,
): readonly GameExtensionScaffoldFile[] {
	if (!options || typeof options !== "object") throw new GameExtensionScaffoldError("scaffold_invalid");
	const packageName = validatePackageName(options.packageName);
	if (!packageName.validForNewPackages) throw new GameExtensionScaffoldError("scaffold_invalid");
	if (
		valid(options.extensionKitVersion) !== options.extensionKitVersion ||
		valid(options.typescriptVersion) !== options.typescriptVersion
	) {
		throw new GameExtensionScaffoldError("scaffold_invalid");
	}
	const manifest = parseGameExtensionManifest(
		JSON.stringify({
			schemaVersion: GAME_EXTENSION_MANIFEST_SCHEMA_VERSION,
			id: options.id,
			version: options.version,
			permissions: options.permissions,
			dependencies: options.dependencies,
		}),
		options.manifestLimits,
	);
	const manifestText = `${JSON.stringify(manifest, null, "\t")}\n`;
	parseGameExtensionManifest(manifestText, options.manifestLimits);
	const files: GameExtensionScaffoldFile[] = [
		{
			path: "README.md",
			content: `# ${manifest.id}\n\nA statically linked OpenGameAgent extension for this game. The game host owns permissions, authoritative state, and runtime construction.\n`,
		},
		{
			path: "extension.json",
			content: manifestText,
		},
		{
			path: "package.json",
			content: `${JSON.stringify(
				{
					name: options.packageName,
					version: manifest.version,
					private: true,
					type: "module",
					main: "./dist/index.js",
					types: "./dist/index.d.ts",
					exports: {
						".": { types: "./dist/index.d.ts", import: "./dist/index.js" },
					},
					files: ["dist/**/*.js", "dist/**/*.d.ts", "dist/**/*.d.ts.map", "extension.json", "README.md"],
					scripts: { build: "tsc -p tsconfig.json --pretty false" },
					dependencies: { "@opengameagent/extension-kit": options.extensionKitVersion },
					devDependencies: { typescript: options.typescriptVersion },
				},
				null,
				"\t",
			)}\n`,
		},
		{
			path: "src/index.ts",
			content: `import type { GameExtensionModule } from "@opengameagent/extension-kit";\n\nexport const extension: GameExtensionModule = {\n\tdescriptor: { id: ${JSON.stringify(manifest.id)}, version: ${JSON.stringify(manifest.version)} },\n\tconfigure(api) {\n\t\t// Register only resources declared by extension.json. The game host keeps state-write authority.\n\t\tvoid api;\n\t},\n};\n\nexport default extension;\n`,
		},
		{
			path: "tsconfig.json",
			content: `${JSON.stringify(
				{
					compilerOptions: {
						composite: true,
						declaration: true,
						declarationMap: true,
						exactOptionalPropertyTypes: true,
						forceConsistentCasingInFileNames: true,
						lib: ["ES2024"],
						module: "NodeNext",
						moduleResolution: "NodeNext",
						noEmitOnError: true,
						outDir: "dist",
						rootDir: "src",
						skipLibCheck: true,
						strict: true,
						target: "ES2024",
						verbatimModuleSyntax: true,
					},
					include: ["src/**/*.ts"],
				},
				null,
				"\t",
			)}\n`,
		},
	];
	return Object.freeze(files.map((file) => Object.freeze(file)));
}

export async function writeGameExtensionScaffold(
	destination: string,
	options: GameExtensionScaffoldOptions,
	signal: AbortSignal = new AbortController().signal,
): Promise<WrittenGameExtensionScaffold> {
	const files = renderGameExtensionScaffold(options);
	signal.throwIfAborted();
	if (typeof destination !== "string" || destination.length === 0 || destination.includes("\0")) {
		throw new GameExtensionScaffoldError("destination_invalid");
	}
	const addressedTarget = resolve(destination);
	const targetName = basename(addressedTarget);
	if (targetName.length === 0 || targetName === "." || targetName === "..") {
		throw new GameExtensionScaffoldError("destination_invalid");
	}
	let canonicalParent: string;
	try {
		canonicalParent = await realpath(dirname(addressedTarget));
		const parentInfo = await lstat(canonicalParent);
		if (!parentInfo.isDirectory()) throw new Error("not_directory");
	} catch {
		throw new GameExtensionScaffoldError("destination_invalid");
	}
	const target = join(canonicalParent, targetName);
	const incompleteMarker = join(target, ".oga-extension-scaffold-incomplete");
	const writtenPaths: string[] = [];
	let targetCreated = false;
	let sourceDirectoryCreated = false;
	try {
		await mkdir(target, { mode: 0o700 });
		targetCreated = true;
		await writeNewTextFile(incompleteMarker, "OpenGameAgent scaffold publication is incomplete.\n", writtenPaths);
		await mkdir(join(target, "src"), { mode: 0o700 });
		sourceDirectoryCreated = true;
		for (const file of files) {
			signal.throwIfAborted();
			await writeNewTextFile(join(target, ...file.path.split("/")), file.content, writtenPaths);
		}
		signal.throwIfAborted();
		await unlink(incompleteMarker);
		writtenPaths.splice(writtenPaths.indexOf(incompleteMarker), 1);
		return Object.freeze({ rootPath: target, files });
	} catch (error) {
		const cleanupComplete = targetCreated
			? await cleanIncompleteTarget(target, writtenPaths, sourceDirectoryCreated)
			: true;
		if (!cleanupComplete) throw new GameExtensionScaffoldError("scaffold_incomplete");
		if (signal.aborted) throw signal.reason;
		if (!targetCreated && isAlreadyExists(error)) throw new GameExtensionScaffoldError("target_exists");
		if (error instanceof GameExtensionScaffoldError) throw error;
		throw new GameExtensionScaffoldError("scaffold_incomplete");
	}
}

async function writeNewTextFile(path: string, content: string, writtenPaths: string[]): Promise<void> {
	const handle = await open(path, "wx", 0o600);
	writtenPaths.push(path);
	try {
		await handle.writeFile(content, "utf8");
	} finally {
		await handle.close();
	}
}

async function cleanIncompleteTarget(
	target: string,
	writtenPaths: readonly string[],
	sourceDirectoryCreated: boolean,
): Promise<boolean> {
	let complete = true;
	for (const path of [...writtenPaths].reverse()) {
		try {
			await unlink(path);
		} catch (error) {
			if (!isNodeError(error, "ENOENT")) complete = false;
		}
	}
	if (sourceDirectoryCreated) {
		try {
			await rmdir(join(target, "src"));
		} catch (error) {
			if (!isNodeError(error, "ENOENT")) complete = false;
		}
	}
	try {
		await rmdir(target);
	} catch (error) {
		if (!isNodeError(error, "ENOENT")) complete = false;
	}
	return complete;
}

function isAlreadyExists(error: unknown): boolean {
	return isNodeError(error, "EEXIST") || isNodeError(error, "ENOTEMPTY");
}

function isNodeError(error: unknown, code: string): error is NodeJS.ErrnoException {
	return error instanceof Error && "code" in error && (error as NodeJS.ErrnoException).code === code;
}
