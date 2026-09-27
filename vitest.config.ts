import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

export default defineConfig({
	resolve: {
		alias: {
			"@opengameagent/actions/runtime-validation": fileURLToPath(
				new URL("./packages/actions/src/runtime-validation.ts", import.meta.url),
			),
			"@opengameagent/agent/runtime-internal": fileURLToPath(
				new URL("./packages/agent/src/runtime-activation.ts", import.meta.url),
			),
			"@opengameagent/extension-kit": fileURLToPath(new URL("./packages/extension-kit/src/index.ts", import.meta.url)),
			"@opengameagent/actions": fileURLToPath(new URL("./packages/actions/src/index.ts", import.meta.url)),
			"@opengameagent/characters": fileURLToPath(new URL("./packages/characters/src/index.ts", import.meta.url)),
			"@opengameagent/realtime": fileURLToPath(new URL("./packages/realtime/src/index.ts", import.meta.url)),
			"@opengameagent/realtime-local": fileURLToPath(
				new URL("./packages/realtime-local/src/index.ts", import.meta.url),
			),
			"@opengameagent/artifacts": fileURLToPath(new URL("./packages/artifacts/src/index.ts", import.meta.url)),
			"@opengameagent/attachments": fileURLToPath(new URL("./packages/attachments/src/index.ts", import.meta.url)),
			"@opengameagent/client": fileURLToPath(new URL("./packages/client/src/index.ts", import.meta.url)),
			"@opengameagent/agent": fileURLToPath(new URL("./packages/agent/src/index.ts", import.meta.url)),
			"@opengameagent/delegation": fileURLToPath(new URL("./packages/delegation/src/index.ts", import.meta.url)),
			"@opengameagent/devtools": fileURLToPath(new URL("./packages/devtools/src/index.ts", import.meta.url)),
			"@opengameagent/protocol": fileURLToPath(new URL("./packages/protocol/src/index.ts", import.meta.url)),
			"@opengameagent/runtime": fileURLToPath(new URL("./packages/runtime/src/index.ts", import.meta.url)),
			"@opengameagent/server": fileURLToPath(new URL("./packages/server/src/index.ts", import.meta.url)),
			"@opengameagent/health": fileURLToPath(new URL("./packages/health/src/index.ts", import.meta.url)),
			"@opengameagent/memory": fileURLToPath(new URL("./packages/memory/src/index.ts", import.meta.url)),
			"@opengameagent/memory-ai-sdk": fileURLToPath(new URL("./packages/memory-ai-sdk/src/index.ts", import.meta.url)),
			"@opengameagent/memory-bge-m3": fileURLToPath(new URL("./packages/memory-bge-m3/src/index.ts", import.meta.url)),
			"@opengameagent/memory-agent": fileURLToPath(new URL("./packages/memory-agent/src/index.ts", import.meta.url)),
			"@opengameagent/extensions": fileURLToPath(new URL("./packages/extensions/src/index.ts", import.meta.url)),
			"@opengameagent/integrations": fileURLToPath(new URL("./packages/integrations/src/index.ts", import.meta.url)),
			"@opengameagent/credentials-windows": fileURLToPath(
				new URL("./packages/credentials-windows/src/index.ts", import.meta.url),
			),
			"@opengameagent/memory-usearch": fileURLToPath(
				new URL("./packages/memory-usearch/src/index.ts", import.meta.url),
			),
			"@opengameagent/media": fileURLToPath(new URL("./packages/media/src/index.ts", import.meta.url)),
			"@opengameagent/observations": fileURLToPath(new URL("./packages/observations/src/index.ts", import.meta.url)),
			"@opengameagent/storage-sqlite": fileURLToPath(
				new URL("./packages/storage-sqlite/src/index.ts", import.meta.url),
			),
		},
	},
	test: {
		include: ["packages/*/test/**/*.test.ts"],
		maxWorkers: "50%",
		passWithNoTests: false,
	},
});
