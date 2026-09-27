import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";

const publicFiles = execFileSync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], {
	encoding: "utf8",
})
	.split("\0")
	.filter(Boolean);

const forbiddenPathSegments = ["/.codex/", "/private/", "/source-snapshots/", "/research/"];
const forbiddenContent = [["C:", "Users", ""].join("\\"), ["source", "snapshots"].join("-")];
const failures = [];

for (const path of publicFiles) {
	const normalized = `/${path.replaceAll("\\", "/").toLowerCase()}`;
	const unrealModulePrivate = normalized.startsWith("/engines/unreal/opengameagent/source/opengameagent/private/");
	if (
		forbiddenPathSegments.some(
			(segment) => normalized.includes(segment) && !(segment === "/private/" && unrealModulePrivate),
		)
	) {
		failures.push(`${path}: forbidden public path`);
		continue;
	}

	let content;
	try {
		content = readFileSync(path, "utf8");
	} catch {
		continue;
	}

	if (path !== "scripts/check-public-tree.mjs" && forbiddenContent.some((marker) => content.includes(marker))) {
		failures.push(`${path}: private workspace reference`);
	}
}

if (failures.length > 0) {
	throw new Error(`Public tree check failed:\n${failures.join("\n")}`);
}

console.log(`Public tree check passed for ${publicFiles.length} tracked or untracked files.`);
