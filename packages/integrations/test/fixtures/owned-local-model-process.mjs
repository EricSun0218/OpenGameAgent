import { spawn } from "node:child_process";
import { access, readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const [mode, ...parameters] = process.argv.slice(2);

switch (mode) {
	case "record": {
		const [outputPath, exitCodeText, ...argumentsSeen] = parameters;
		await writeFile(
			outputPath,
			JSON.stringify({
				pid: process.pid,
				cwd: process.cwd(),
				arguments: argumentsSeen,
				environment: process.env.OGA_PROCESS_TEST_VALUE ?? null,
			}),
		);
		process.exitCode = Number(exitCodeText);
		break;
	}
	case "stay": {
		const [outputPath] = parameters;
		await writeFile(outputPath, String(process.pid));
		setInterval(() => undefined, 1_000);
		break;
	}
	case "tree": {
		const [outputPath, childOutputPath] = parameters;
		spawn(process.execPath, [fileURLToPath(import.meta.url), "stay", childOutputPath], {
			stdio: "ignore",
		});
		for (let attempt = 0; attempt < 200; attempt += 1) {
			try {
				await access(childOutputPath);
				break;
			} catch {
				await new Promise((resolve) => setTimeout(resolve, 10));
			}
		}
		const childPid = Number(await readFile(childOutputPath, "utf8"));
		await writeFile(outputPath, JSON.stringify({ rootPid: process.pid, childPid }));
		setInterval(() => undefined, 1_000);
		break;
	}
	default:
		throw new Error("Unknown owned-process test fixture mode.");
}
