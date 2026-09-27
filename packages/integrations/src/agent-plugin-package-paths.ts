import { createHash } from "node:crypto";
import { dirname, join, resolve } from "node:path";
import { AGENT_PLUGIN_PACKAGE_PUBLICATION_PREFIX } from "./agent-plugin-package-types.js";

/** `complete:` + SHA-256 + separators + two maximum unsigned 64-bit decimal filesystem IDs. */
export const AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES = 9 + 64 + 2 + 20 + 20;

/** Resolve the host-owned sibling publication-state path for one package or installed root. */
export function agentPluginPackagePublicationStatePath(destination: string): string {
	const addressed = resolve(destination);
	const identityKey = process.platform === "win32" ? addressed.toLowerCase() : addressed;
	const digest = createHash("sha256").update(identityKey, "utf8").digest("hex");
	return join(dirname(addressed), `${AGENT_PLUGIN_PACKAGE_PUBLICATION_PREFIX}${digest}`);
}

/** Resolve the crash-recovery candidate used for the atomic publication-state transition. */
export function agentPluginPackagePublicationNextStatePath(destination: string): string {
	return `${agentPluginPackagePublicationStatePath(destination)}.next`;
}
