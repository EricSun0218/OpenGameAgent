import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const root = readJson("package.json");
const failures = [];
const nodeVersion = readFileSync(".nvmrc", "utf8").trim();
const nodeMajor = nodeVersion.split(".", 1)[0];
const licenseNotice = readFileSync("LICENSE", "utf8").replaceAll("\r\n", "\n");

assertEqual("root private", root.private, true);
assertEqual("root type", root.type, "module");
assertEqual("root license", root.license, "MIT");
assertEqual("root Node engine", root.engines?.node, `${nodeMajor}.x`);
assertMatch("root package manager", root.packageManager, /^npm@\d+\.\d+\.\d+$/u);

if (!Array.isArray(root.workspaces) || root.workspaces.length !== 1 || root.workspaces[0] !== "packages/*") {
	failures.push("root workspaces: expected only packages/*");
}

const packageDirectories = readdirSync("packages", { withFileTypes: true })
	.filter((entry) => entry.isDirectory() && existsSync(join("packages", entry.name, "package.json")))
	.map((entry) => entry.name)
	.sort();
const packageNames = new Set();

for (const directory of packageDirectories) {
	const prefix = `packages/${directory}`;
	const manifest = readJson(`${prefix}/package.json`);
	assertEqual(`${prefix} name`, manifest.name, `@opengameagent/${directory}`);
	assertEqual(`${prefix} version`, manifest.version, root.version);
	assertEqual(`${prefix} private`, manifest.private, true);
	assertEqual(`${prefix} type`, manifest.type, root.type);
	assertEqual(`${prefix} license`, manifest.license, root.license);
	assertEqual(`${prefix} Node engine`, manifest.engines?.node, root.engines?.node);
	assertEqual(`${prefix} main`, manifest.main, "./dist/index.js");
	assertEqual(`${prefix} types`, manifest.types, "./dist/index.d.ts");
	assertEqual(`${prefix} root export import`, manifest.exports?.["."]?.import, manifest.main);
	assertEqual(`${prefix} root export types`, manifest.exports?.["."]?.types, manifest.types);
	assertEqual(`${prefix} package.json export`, manifest.exports?.["./package.json"], "./package.json");

	if (packageNames.has(manifest.name)) failures.push(`${prefix} name: duplicate package name ${manifest.name}`);
	packageNames.add(manifest.name);
	if (typeof manifest.description !== "string" || manifest.description.trim() === "") {
		failures.push(`${prefix} description: expected a non-empty developer-facing description`);
	}
	if (!existsSync(`${prefix}/README.md`)) failures.push(`${prefix}: missing README.md`);
	assertEqual(
		`${prefix} license notice`,
		readFileSync(`${prefix}/LICENSE`, "utf8").replaceAll("\r\n", "\n"),
		licenseNotice,
	);
	for (const requiredFile of ["dist/**/*.js", "dist/**/*.d.ts", "dist/**/*.d.ts.map", "README.md", "LICENSE"]) {
		if (!manifest.files?.includes(requiredFile)) failures.push(`${prefix} files: missing ${requiredFile}`);
	}

	for (const field of ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"]) {
		for (const [dependency, version] of Object.entries(manifest[field] ?? {})) {
			if (dependency.startsWith("@opengameagent/") && version !== root.version) {
				failures.push(`${prefix} ${field}.${dependency}: expected ${root.version}, received ${String(version)}`);
			}
		}
	}
}

if (failures.length > 0) {
	throw new Error(`Package metadata check failed:\n${failures.join("\n")}`);
}

console.log(
	`Package metadata check passed for ${packageDirectories.length} MIT-licensed source workspaces on Node ${root.engines.node}.`,
);

function readJson(path) {
	return JSON.parse(readFileSync(path, "utf8"));
}

function assertEqual(label, actual, expected) {
	if (actual !== expected) failures.push(`${label}: expected ${String(expected)}, received ${String(actual)}`);
}

function assertMatch(label, actual, expected) {
	if (typeof actual !== "string" || !expected.test(actual)) {
		failures.push(`${label}: received ${String(actual)}`);
	}
}
