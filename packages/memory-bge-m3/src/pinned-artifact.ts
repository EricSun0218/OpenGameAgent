import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

export class PinnedArtifactError extends Error {
	override readonly name = "PinnedArtifactError";

	constructor() {
		super("invalid_artifact");
	}
}

export async function readPinnedArtifact(artifact: {
	readonly path: string;
	readonly sha256: string;
}): Promise<Buffer> {
	try {
		const bytes = await readFile(artifact.path);
		if (createHash("sha256").update(bytes).digest("hex") !== artifact.sha256) throw new Error();
		return bytes;
	} catch {
		throw new PinnedArtifactError();
	}
}
