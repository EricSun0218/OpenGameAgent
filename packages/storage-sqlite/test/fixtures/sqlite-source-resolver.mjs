import { existsSync } from "node:fs";
import { registerHooks } from "node:module";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const workspaceRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../../..");
const protocolSourceUrl = pathToFileURL(join(workspaceRoot, "packages/protocol/src/index.ts")).href;
const sourceRootUrls = [
	pathToFileURL(`${join(workspaceRoot, "packages/storage-sqlite/src")}${sep}`).href,
	pathToFileURL(`${join(workspaceRoot, "packages/protocol/src")}${sep}`).href,
];

registerHooks({
	resolve(specifier, context, nextResolve) {
		if (specifier === "@opengameagent/protocol") {
			return { url: protocolSourceUrl, shortCircuit: true };
		}
		if (
			context.parentURL &&
			sourceRootUrls.some((sourceRootUrl) => context.parentURL?.startsWith(sourceRootUrl)) &&
			specifier.startsWith(".") &&
			specifier.endsWith(".js")
		) {
			const sourceUrl = new URL(`${specifier.slice(0, -3)}.ts`, context.parentURL);
			if (
				sourceRootUrls.some((sourceRootUrl) => sourceUrl.href.startsWith(sourceRootUrl)) &&
				existsSync(fileURLToPath(sourceUrl))
			) {
				return { url: sourceUrl.href, shortCircuit: true };
			}
		}
		return nextResolve(specifier, context);
	},
});
