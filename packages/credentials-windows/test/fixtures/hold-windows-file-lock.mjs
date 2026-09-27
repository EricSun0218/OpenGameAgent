import { closeSync, openSync } from "node:fs";
import { flockSync } from "fs-ext-extra-prebuilt";

const lockPath = process.argv[2];
if (typeof lockPath !== "string" || lockPath.length === 0) throw new Error("lock path is required");

const descriptor = openSync(lockPath, "a+");
flockSync(descriptor, "exnb");
process.stdout.write("locked\n");
process.stdin.resume();
process.on("exit", () => {
	try {
		closeSync(descriptor);
	} catch {
		// Forced process termination releases the operating-system handle.
	}
});
