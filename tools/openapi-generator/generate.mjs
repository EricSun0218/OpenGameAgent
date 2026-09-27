import { createClient } from "@hey-api/openapi-ts";
import { readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const toolDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(toolDirectory, "../..");

await createClient({
	input: resolve(repositoryRoot, "protocol/runtime-v1.openapi.json"),
	output: {
		path: resolve(repositoryRoot, "packages/client/src/generated"),
		clean: true,
		postProcess: ["biome:format"],
		tsConfigPath: resolve(repositoryRoot, "packages/client/tsconfig.json"),
	},
	plugins: ["@hey-api/client-fetch", "@hey-api/typescript", "@hey-api/sdk"],
});

const queryKeySerializer = resolve(repositoryRoot, "packages/client/src/generated/core/queryKeySerializer.gen.ts");
const generated = await readFile(queryKeySerializer, "utf8");
const hardened = generated
	.replace(
		"const result: Record<string, JsonValue> = {};",
		"const result = Object.create(null) as Record<string, JsonValue>;",
	)
	.replace("const existing = result[key];", "const existing = Object.hasOwn(result, key) ? result[key] : undefined;");
if (hardened === generated) throw new Error("The generated query-key serializer hardening patch was not applied.");
await writeFile(queryKeySerializer, hardened, "utf8");

const generatedTypesPath = resolve(repositoryRoot, "packages/client/src/generated/types.gen.ts");
const generatedTypes = await readFile(generatedTypesPath, "utf8");
const lines = generatedTypes.split("\n");
const contextTuple = [
	"{",
	'type: "text";',
	"text: string;",
	"},",
	"{",
	'type: "image-reference";',
	"attachmentId: string;",
	'mediaType: "image/png" | "image/jpeg" | "image/webp" | "image/gif";',
	"bytes: number;",
	"width: number;",
	"height: number;",
	"},",
	"];",
];
let contextTupleCount = 0;
for (let index = 0; index < lines.length - contextTuple.length; index += 1) {
	if (lines[index]?.trim() !== "content: [") continue;
	const candidate = lines.slice(index + 1, index + 1 + contextTuple.length).map((line) => line.trim());
	if (!candidate.every((line, candidateIndex) => line === contextTuple[candidateIndex])) continue;
	const itemIndent = lines[index + 5]?.match(/^\s*/u)?.[0];
	if (itemIndent === undefined) throw new Error("The generated internal context tuple indentation was unavailable.");
	lines[index + 5] = `${itemIndent}...Array<{`;
	lines[index + 12] = `${itemIndent}}>,`;
	contextTupleCount += 1;
}
if (contextTupleCount === 0) {
	throw new Error("The generated internal context tuple patch was not applied.");
}
await writeFile(generatedTypesPath, lines.join("\n"), "utf8");
