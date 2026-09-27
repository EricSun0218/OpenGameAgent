import { type ChildProcess, spawn } from "node:child_process";
import { once } from "node:events";
import { access, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	createProviderEndpointHealthDependency,
	OwnedLocalModelProcessError,
	OwnedLocalModelProcessLauncher,
	type OwnedLocalModelProcessSpec,
	type OwnedLocalModelProcessStdio,
} from "../src/index.js";

const FIXTURE_PATH = fileURLToPath(new URL("./fixtures/owned-local-model-process.mjs", import.meta.url));
const launchers = new Set<OwnedLocalModelProcessLauncher>();
const externalProcesses = new Set<ChildProcess>();
const temporaryDirectories = new Set<string>();

afterEach(async () => {
	vi.unstubAllEnvs();
	await Promise.allSettled([...launchers].map((launcher) => launcher.close()));
	launchers.clear();
	for (const child of externalProcesses) {
		if (child.exitCode === null && child.signalCode === null) child.kill();
		if (child.exitCode === null && child.signalCode === null) await once(child, "close").catch(() => undefined);
	}
	externalProcesses.clear();
	await Promise.all([...temporaryDirectories].map((directory) => rm(directory, { recursive: true, force: true })));
	temporaryDirectories.clear();
});

describe("owned local-model process", () => {
	it("requires an explicit positive host process capacity", () => {
		for (const maxOwnedProcesses of [undefined, true, 0, -1, 1.5, Number.NaN, Number.POSITIVE_INFINITY]) {
			expect(
				() =>
					new OwnedLocalModelProcessLauncher({
						maxOwnedProcesses,
					} as unknown as ConstructorParameters<typeof OwnedLocalModelProcessLauncher>[0]),
			).toThrow(new OwnedLocalModelProcessError("invalid_configuration"));
		}
	});

	it("requires an explicit host shutdown policy before spawning", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const base = processSpec(["record", join(directory, "record.json"), "0"]);
		for (const forceKillAfterDelayMs of [undefined, true, 0, -1, Number.NaN, Number.POSITIVE_INFINITY, 2_147_483_648]) {
			await expect(
				launcher.startOwned({ ...base, forceKillAfterDelayMs } as unknown as OwnedLocalModelProcessSpec),
			).rejects.toMatchObject({ code: "invalid_configuration" });
		}
		await expect(access(join(directory, "record.json"))).rejects.toThrow();
	});

	it("snapshots argv, cwd, environment, and stdio while keeping shell expansion disabled", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const outputPath = join(directory, "record.json");
		const argumentsList = [FIXTURE_PATH, "record", outputPath, "0", "literal;$HOME", "$(not-a-command)"];
		const environment = { OGA_PROCESS_TEST_VALUE: "captured" };
		const stdio: {
			stdin: OwnedLocalModelProcessStdio;
			stdout: OwnedLocalModelProcessStdio;
			stderr: OwnedLocalModelProcessStdio;
		} = {
			stdin: "ignore",
			stdout: "ignore",
			stderr: "ignore",
		};
		const mutable = {
			executable: process.execPath,
			arguments: argumentsList,
			cwd: directory,
			env: environment,
			stdio,
			forceKillAfterDelayMs: false as const,
		};

		const started = launcher.startOwned(mutable);
		argumentsList[4] = "changed";
		environment.OGA_PROCESS_TEST_VALUE = "changed";
		stdio.stdout = "inherit";
		mutable.cwd = dirname(directory);
		const handle = await started;
		await expect(handle.exited).resolves.toEqual({ exitCode: 0, signal: null });

		const record = JSON.parse(await readFile(outputPath, "utf8")) as {
			readonly cwd: string;
			readonly arguments: readonly string[];
			readonly environment: string | null;
		};
		expect(record).toMatchObject({
			cwd: directory,
			arguments: ["literal;$HOME", "$(not-a-command)"],
			environment: "captured",
		});
	});

	it("uses the explicit child environment without inheriting ambient values", async () => {
		vi.stubEnv("OGA_PROCESS_TEST_VALUE", "ambient-value");
		const launcher = trackedLauncher(2);
		const directory = await temporaryDirectory();
		const emptyOutputPath = join(directory, "empty-environment.json");
		const explicitOutputPath = join(directory, "explicit-environment.json");
		await expect(
			launcher.startOwned({
				...processSpec(["record", join(directory, "missing-environment.json"), "0"]),
				env: undefined,
			} as unknown as OwnedLocalModelProcessSpec),
		).rejects.toEqual(new OwnedLocalModelProcessError("invalid_configuration"));

		const emptyEnvironment = await launcher.startOwned({
			...processSpec(["record", emptyOutputPath, "0"]),
			env: {},
		});
		const explicitEnvironment = await launcher.startOwned({
			...processSpec(["record", explicitOutputPath, "0"]),
			env: { OGA_PROCESS_TEST_VALUE: "explicit-value" },
		});
		await Promise.all([emptyEnvironment.exited, explicitEnvironment.exited]);

		const emptyRecord = JSON.parse(await readFile(emptyOutputPath, "utf8")) as { readonly environment: string | null };
		const explicitRecord = JSON.parse(await readFile(explicitOutputPath, "utf8")) as {
			readonly environment: string | null;
		};
		expect(emptyRecord.environment).toBeNull();
		expect(explicitRecord.environment).toBe("explicit-value");
	});

	it("reports spawn failure without publishing a process handle", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const missingWorkingDirectory = join(directory, "missing-working-directory");
		await expect(launcher.startOwned({ ...processSpec([]), cwd: missingWorkingDirectory })).rejects.toEqual(
			new OwnedLocalModelProcessError("spawn_failed"),
		);
	});

	it("publishes a successfully spawned process even when it exits nonzero immediately", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const handle = await launcher.startOwned(processSpec(["record", join(directory, "record.json"), "17"]));
		expect(handle.pid).toBeGreaterThan(0);
		expect(handle.exited).toBe(handle.exited);
		const exited = await handle.exited;
		expect(exited).toEqual({ exitCode: 17, signal: null });
		expect(Object.isFrozen(exited)).toBe(true);
		expect(Object.isFrozen(handle)).toBe(true);
		expect(handle).not.toHaveProperty("subprocess");
		expect(handle).not.toHaveProperty("nodeChildProcess");
		expect(handle).not.toHaveProperty("kill");
		expect(handle).not.toHaveProperty("attach");
		expect(handle).not.toHaveProperty("stopPid");
		expect(handle).not.toHaveProperty("restart");
		expect(handle).not.toHaveProperty("waitUntilReady");
	});

	it("performs zero spawn for a pre-aborted start and preserves the exact reason", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const outputPath = join(directory, "record.json");
		const controller = new AbortController();
		const reason = new Error("host cancelled start");
		controller.abort(reason);

		await expect(launcher.startOwned(processSpec(["record", outputPath, "0"]), controller.signal)).rejects.toBe(reason);
		await expect(access(outputPath)).rejects.toThrow();
	});

	it("terminates a start cancelled before handle publication and preserves the exact reason", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const outputPath = join(directory, "pid.txt");
		const controller = new AbortController();
		const reason = new Error("cancel before publication");

		const pending = launcher.startOwned(processSpec(["stay", outputPath]), controller.signal);
		controller.abort(reason);
		await expect(pending).rejects.toBe(reason);
		const pid = await readOptionalPid(outputPath);
		if (pid !== undefined) await waitUntil(() => !isProcessAlive(pid));
	});

	it("disconnects the start signal after publication", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const outputPath = join(directory, "pid.txt");
		const controller = new AbortController();
		const handle = await launcher.startOwned(processSpec(["stay", outputPath]), controller.signal);
		controller.abort(new Error("late start cancellation"));
		const pid = await waitForPid(outputPath);
		expect(isProcessAlive(pid)).toBe(true);
		await handle.stop();
		await handle.exited;
		expect(isProcessAlive(pid)).toBe(false);
	});

	it("starts one shared stop operation even when the caller wait is already aborted", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const handle = await launcher.startOwned(processSpec(["stay", join(directory, "pid.txt")]));
		const controller = new AbortController();
		const reason = new Error("stop waiter cancelled");
		controller.abort(reason);

		await expect(handle.stop(controller.signal)).rejects.toBe(reason);
		await handle.exited;
		await expect(Promise.all([handle.stop(), handle.stop()])).resolves.toEqual([undefined, undefined]);
	});

	it("closes every published handle once and rejects later starts", async () => {
		const launcher = trackedLauncher(2);
		const directory = await temporaryDirectory();
		const [first, second] = await Promise.all([
			launcher.startOwned(processSpec(["stay", join(directory, "first.txt")])),
			launcher.startOwned(processSpec(["stay", join(directory, "second.txt")])),
		]);
		const firstClose = launcher.close();
		const secondClose = launcher.close();
		expect(secondClose).toBe(firstClose);
		await firstClose;
		await Promise.all([first.exited, second.exited]);
		await expect(launcher.startOwned(processSpec(["stay", join(directory, "third.txt")]))).rejects.toEqual(
			new OwnedLocalModelProcessError("launcher_closed"),
		);
	});

	it("rejects excess starts without queuing and releases capacity after settlement", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const blockedOutputPath = join(directory, "blocked.json");
		const admittedOutputPath = join(directory, "admitted.json");
		const first = await launcher.startOwned(processSpec(["stay", join(directory, "first.txt")]));

		await expect(launcher.startOwned(processSpec(["record", blockedOutputPath, "0"]))).rejects.toEqual(
			new OwnedLocalModelProcessError("capacity_exhausted"),
		);
		await expect(access(blockedOutputPath)).rejects.toThrow();
		await first.stop();

		const admitted = await launcher.startOwned(processSpec(["record", admittedOutputPath, "0"]));
		await expect(admitted.exited).resolves.toEqual({ exitCode: 0, signal: null });
		await expect(access(admittedOutputPath)).resolves.toBeUndefined();
	});

	it("closes a publication-pending start without leaving its process alive", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const outputPath = join(directory, "pid.txt");
		const pending = launcher.startOwned(processSpec(["stay", outputPath]));
		const closing = launcher.close();

		await expect(pending).rejects.toEqual(new OwnedLocalModelProcessError("launcher_closed"));
		await closing;
		const pid = await readOptionalPid(outputPath);
		if (pid !== undefined) await waitUntil(() => !isProcessAlive(pid));
	});

	it("stops an ordinary descendant tree but never touches an external process", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const treePath = join(directory, "tree.json");
		const childPath = join(directory, "child.txt");
		const owned = await launcher.startOwned(processSpec(["tree", treePath, childPath]));
		const tree = await waitForJson<{ readonly rootPid: number; readonly childPid: number }>(treePath);

		const externalPath = join(directory, "external.txt");
		const external = spawn(process.execPath, [FIXTURE_PATH, "stay", externalPath], { stdio: "ignore" });
		externalProcesses.add(external);
		const externalPid = await waitForPid(externalPath);
		await launcher.close();
		await owned.exited;
		await waitUntil(() => !isProcessAlive(tree.rootPid) && !isProcessAlive(tree.childPid));
		expect(isProcessAlive(externalPid)).toBe(true);
	});

	it("keeps process liveness separate from endpoint readiness", async () => {
		const launcher = trackedLauncher();
		const directory = await temporaryDirectory();
		const handle = await launcher.startOwned(processSpec(["stay", join(directory, "pid.txt")]));
		const dependency = createProviderEndpointHealthDependency({
			id: "local-model",
			kind: "local-endpoint",
			requirement: "optional",
			protocol: "llama.cpp",
			healthUrl: "http://127.0.0.1:8080/health",
			maxResponseBytes: 1_024,
			fetch: async () =>
				new Response(JSON.stringify({ error: { code: 503, message: "Loading model", type: "unavailable_error" } }), {
					status: 503,
					headers: { "content-type": "application/json" },
				}),
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({ state: "available" });
		expect(isProcessAlive(handle.pid)).toBe(true);
		await handle.stop();
	});
});

function trackedLauncher(maxOwnedProcesses = 1): OwnedLocalModelProcessLauncher {
	const launcher = new OwnedLocalModelProcessLauncher({ maxOwnedProcesses });
	launchers.add(launcher);
	return launcher;
}

function processSpec(parameters: readonly string[]): OwnedLocalModelProcessSpec {
	return {
		executable: process.execPath,
		arguments: [FIXTURE_PATH, ...parameters],
		cwd: dirname(FIXTURE_PATH),
		env: {},
		stdio: { stdin: "ignore", stdout: "ignore", stderr: "ignore" },
		forceKillAfterDelayMs: 1_000,
	};
}

async function temporaryDirectory(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-owned-process-test-"));
	temporaryDirectories.add(directory);
	return directory;
}

async function readOptionalPid(path: string): Promise<number | undefined> {
	try {
		return Number(await readFile(path, "utf8"));
	} catch {
		return undefined;
	}
}

async function waitForPid(path: string): Promise<number> {
	return waitForJson(path, (text) => Number(text));
}

async function waitForJson<T>(path: string, parse: (text: string) => T = JSON.parse): Promise<T> {
	let value: T | undefined;
	await waitUntil(async () => {
		try {
			value = parse(await readFile(path, "utf8"));
			return true;
		} catch {
			return false;
		}
	});
	return value as T;
}

async function waitUntil(predicate: () => boolean | Promise<boolean>): Promise<void> {
	const deadline = Date.now() + 5_000;
	while (!(await predicate())) {
		if (Date.now() >= deadline) throw new Error("Timed out waiting for owned-process test state.");
		await new Promise((resolve) => setTimeout(resolve, 10));
	}
}

function isProcessAlive(pid: number): boolean {
	try {
		process.kill(pid, 0);
		return true;
	} catch {
		return false;
	}
}
