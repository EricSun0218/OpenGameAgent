import { execFileSync } from "node:child_process";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { resolve } from "node:path";

const targets = [
	resolve("protocol/runtime-v1.openapi.json"),
	resolve("packages/client/src/generated"),
	resolve("engines/godot/csharp/src/OpenGameAgent.Protocol/Generated"),
	resolve("engines/unity/Packages/com.opengameagent.client/Runtime/Generated"),
];
const before = snapshot(targets);
const npmEntryPoint = process.env.npm_execpath;
if (npmEntryPoint) {
	execFileSync(process.execPath, [npmEntryPoint, "run", "generate"], { cwd: process.cwd(), stdio: "inherit" });
} else {
	execFileSync("npm", ["run", "generate"], { cwd: process.cwd(), stdio: "inherit" });
}
const after = snapshot(targets);

const allPaths = new Set([...before.keys(), ...after.keys()]);
const changed = [...allPaths].filter((path) => before.get(path) !== after.get(path)).sort();
if (changed.length > 0) {
	throw new Error(`Generated Runtime Protocol artifacts were stale:\n${changed.join("\n")}`);
}
assertUniqueSchemaIdentifiers(JSON.parse(readFileSync(targets[0], "utf8")));
for (const path of [
	resolve("engines/godot/csharp/src/OpenGameAgent.Protocol/Generated/RuntimeProtocol.g.cs"),
	resolve("engines/unity/Packages/com.opengameagent.client/Runtime/Generated/RuntimeProtocol.g.cs"),
]) {
	const source = readFileSync(path, "utf8");
	if (/\bpublic enum Action\b/u.test(source) || !/\bpublic enum ToolRepeatPolicyAction\b/u.test(source)) {
		throw new Error(`Generated Runtime Protocol enum naming is unsafe: ${path}`);
	}
}
console.log(`Generated Runtime Protocol artifacts are current (${after.size} files).`);

function assertUniqueSchemaIdentifiers(document) {
	const seen = new Set();
	const pending = [document];
	while (pending.length > 0) {
		const value = pending.pop();
		if (typeof value !== "object" || value === null) continue;
		if (typeof value.$id === "string") {
			if (seen.has(value.$id)) throw new Error(`Duplicate JSON Schema $id in generated OpenAPI: ${value.$id}`);
			seen.add(value.$id);
		}
		for (const child of Array.isArray(value) ? value : Object.values(value)) pending.push(child);
	}
}

function snapshot(paths) {
	const files = new Map();
	for (const path of paths) collect(path, files);
	return files;
}

function collect(path, files) {
	const stat = statSync(path);
	if (stat.isFile()) {
		files.set(path, readFileSync(path, "utf8"));
		return;
	}
	for (const entry of readdirSync(path, { withFileTypes: true })) {
		collect(resolve(path, entry.name), files);
	}
}
