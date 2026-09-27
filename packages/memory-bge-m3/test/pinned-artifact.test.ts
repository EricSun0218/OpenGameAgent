import { createHash } from "node:crypto";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { readPinnedArtifact } from "../src/pinned-artifact.js";

describe("readPinnedArtifact", () => {
	it("hashes and returns the same loaded bytes", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-bge-artifact-"));
		try {
			const path = join(directory, "artifact.bin");
			const original = Buffer.from("verified");
			await writeFile(path, original);
			const loaded = await readPinnedArtifact({
				path,
				sha256: createHash("sha256").update(original).digest("hex"),
			});
			await writeFile(path, "replaced");
			expect(loaded).toEqual(original);
			await expect(readPinnedArtifact({ path, sha256: "0".repeat(64) })).rejects.toThrow("invalid_artifact");
		} finally {
			await rm(directory, { recursive: true, force: true });
		}
	});
});
