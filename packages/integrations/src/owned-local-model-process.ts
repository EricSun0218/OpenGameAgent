import type { ChildProcess } from "node:child_process";
import { execa } from "execa";

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;

export type OwnedLocalModelProcessErrorCode =
	| "capacity_exhausted"
	| "invalid_configuration"
	| "launcher_closed"
	| "spawn_failed";

export class OwnedLocalModelProcessError extends Error {
	constructor(public readonly code: OwnedLocalModelProcessErrorCode) {
		super(code);
		this.name = "OwnedLocalModelProcessError";
	}
}

export type OwnedLocalModelProcessStdio = "ignore" | "inherit";

export interface OwnedLocalModelProcessSpec {
	readonly executable: string;
	readonly arguments: readonly string[];
	readonly cwd: string;
	readonly env: Readonly<Record<string, string>>;
	readonly stdio: Readonly<{
		readonly stdin: OwnedLocalModelProcessStdio;
		readonly stdout: OwnedLocalModelProcessStdio;
		readonly stderr: OwnedLocalModelProcessStdio;
	}>;
	readonly forceKillAfterDelayMs: number | false;
}

export interface OwnedLocalModelProcessLauncherOptions {
	readonly maxOwnedProcesses: number;
}

export interface OwnedLocalModelProcessExit {
	readonly exitCode: number | null;
	readonly signal: NodeJS.Signals | null;
}

export interface OwnedLocalModelProcess extends AsyncDisposable {
	readonly pid: number;
	readonly exited: Promise<Readonly<OwnedLocalModelProcessExit>>;
	stop(signal?: AbortSignal): Promise<void>;
}

interface CapturedProcessSpec {
	readonly executable: string;
	readonly arguments: readonly string[];
	readonly cwd: string;
	readonly env: Readonly<Record<string, string>>;
	readonly stdio: Readonly<{
		readonly stdin: OwnedLocalModelProcessStdio;
		readonly stdout: OwnedLocalModelProcessStdio;
		readonly stderr: OwnedLocalModelProcessStdio;
	}>;
	readonly forceKillAfterDelayMs: number | false;
}

interface PendingStart {
	readonly controller: AbortController;
	readonly settled: Promise<void>;
	finish(): void;
}

type OwnedExecaSubprocess = ReturnType<typeof spawnOwnedProcess>;

export class OwnedLocalModelProcessLauncher implements AsyncDisposable {
	readonly #maxOwnedProcesses: number;
	readonly #pendingStarts = new Set<PendingStart>();
	readonly #handles = new Set<OwnedLocalModelProcessHandle>();
	#accepting = true;
	#closePromise: Promise<void> | undefined;

	constructor(options: OwnedLocalModelProcessLauncherOptions) {
		if (
			!options ||
			typeof options !== "object" ||
			!Number.isSafeInteger(options.maxOwnedProcesses) ||
			options.maxOwnedProcesses <= 0
		) {
			throw new OwnedLocalModelProcessError("invalid_configuration");
		}
		this.#maxOwnedProcesses = options.maxOwnedProcesses;
	}

	async startOwned(spec: OwnedLocalModelProcessSpec, signal?: AbortSignal): Promise<OwnedLocalModelProcess> {
		signal?.throwIfAborted();
		if (!this.#accepting) throw new OwnedLocalModelProcessError("launcher_closed");
		const captured = captureSpec(spec);
		if (this.#pendingStarts.size + this.#handles.size >= this.#maxOwnedProcesses) {
			throw new OwnedLocalModelProcessError("capacity_exhausted");
		}
		const lifetime = new AbortController();
		const pending = createPendingStart(lifetime);
		this.#pendingStarts.add(pending);
		const onCallerAbort = () => lifetime.abort(signal?.reason);
		signal?.addEventListener("abort", onCallerAbort, { once: true });

		let spawned:
			| {
					readonly subprocess: OwnedExecaSubprocess;
					readonly exited: Promise<Readonly<OwnedLocalModelProcessExit>>;
			  }
			| undefined;
		try {
			const subprocess = spawnOwnedProcess(captured, lifetime.signal);
			const exited = settleProcess(subprocess);
			spawned = { subprocess, exited };
			await waitForSpawn(subprocess.nodeChildProcess);
			signal?.throwIfAborted();
			if (!this.#accepting) throw new OwnedLocalModelProcessError("launcher_closed");
			const pid = subprocess.pid;
			if (pid === undefined) throw new OwnedLocalModelProcessError("spawn_failed");
			signal?.removeEventListener("abort", onCallerAbort);
			const handle = new OwnedLocalModelProcessHandle(pid, subprocess, exited);
			this.#handles.add(handle);
			void exited.then(() => this.#handles.delete(handle));
			return handle;
		} catch {
			lifetime.abort();
			if (spawned !== undefined) await spawned.exited;
			signal?.throwIfAborted();
			if (!this.#accepting) throw new OwnedLocalModelProcessError("launcher_closed");
			throw new OwnedLocalModelProcessError("spawn_failed");
		} finally {
			signal?.removeEventListener("abort", onCallerAbort);
			this.#pendingStarts.delete(pending);
			pending.finish();
		}
	}

	close(): Promise<void> {
		if (this.#closePromise !== undefined) return this.#closePromise;
		this.#accepting = false;
		this.#closePromise = this.#closeOwnedProcesses();
		return this.#closePromise;
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.close();
	}

	async #closeOwnedProcesses(): Promise<void> {
		const pending = [...this.#pendingStarts];
		const reason = new OwnedLocalModelProcessError("launcher_closed");
		for (const start of pending) start.controller.abort(reason);
		await Promise.all(pending.map((start) => start.settled));
		await Promise.all([...this.#handles].map((handle) => handle.stop()));
	}
}

class OwnedLocalModelProcessHandle implements OwnedLocalModelProcess {
	readonly #subprocess: OwnedExecaSubprocess;
	#stopPromise: Promise<void> | undefined;

	constructor(
		readonly pid: number,
		subprocess: OwnedExecaSubprocess,
		readonly exited: Promise<Readonly<OwnedLocalModelProcessExit>>,
	) {
		this.#subprocess = subprocess;
		Object.freeze(this);
	}

	stop(signal?: AbortSignal): Promise<void> {
		this.#stopPromise ??= this.#beginStop();
		return waitForSettlement(this.#stopPromise, signal);
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.stop();
	}

	async #beginStop(): Promise<void> {
		this.#subprocess.kill();
		await this.exited;
	}
}

function captureSpec(spec: OwnedLocalModelProcessSpec): CapturedProcessSpec {
	if (!spec || typeof spec !== "object") throw new OwnedLocalModelProcessError("invalid_configuration");
	const executable = captureSpawnString(spec.executable, false);
	const cwd = captureSpawnString(spec.cwd, false);
	const sourceArguments = spec.arguments;
	if (!Array.isArray(sourceArguments)) throw new OwnedLocalModelProcessError("invalid_configuration");
	const argumentCount = sourceArguments.length;
	const argumentsCopy = new Array<string>(argumentCount);
	for (let index = 0; index < argumentCount; index += 1) {
		argumentsCopy[index] = captureSpawnString(sourceArguments[index], true);
	}

	const sourceEnvironment = spec.env;
	if (!sourceEnvironment || typeof sourceEnvironment !== "object" || Array.isArray(sourceEnvironment)) {
		throw new OwnedLocalModelProcessError("invalid_configuration");
	}
	const environmentCopy = Object.create(null) as Record<string, string>;
	for (const [key, value] of Object.entries(sourceEnvironment)) {
		environmentCopy[captureSpawnString(key, true)] = captureSpawnString(value, true);
	}
	const env = Object.freeze(environmentCopy);

	const sourceStdio = spec.stdio;
	if (!sourceStdio || typeof sourceStdio !== "object") {
		throw new OwnedLocalModelProcessError("invalid_configuration");
	}
	const stdio = Object.freeze({
		stdin: captureStdio(sourceStdio.stdin),
		stdout: captureStdio(sourceStdio.stdout),
		stderr: captureStdio(sourceStdio.stderr),
	});
	const forceKillAfterDelayMs = spec.forceKillAfterDelayMs;
	if (
		forceKillAfterDelayMs !== false &&
		(!Number.isSafeInteger(forceKillAfterDelayMs) ||
			forceKillAfterDelayMs <= 0 ||
			forceKillAfterDelayMs > NODE_MAX_TIMER_DELAY_MS)
	) {
		throw new OwnedLocalModelProcessError("invalid_configuration");
	}

	return Object.freeze({
		executable,
		arguments: Object.freeze(argumentsCopy),
		cwd,
		env,
		stdio,
		forceKillAfterDelayMs,
	});
}

function captureSpawnString(value: unknown, allowEmpty: boolean): string {
	if (typeof value !== "string" || (!allowEmpty && value.length === 0) || value.includes("\0")) {
		throw new OwnedLocalModelProcessError("invalid_configuration");
	}
	return value;
}

function captureStdio(value: unknown): OwnedLocalModelProcessStdio {
	if (value !== "ignore" && value !== "inherit") {
		throw new OwnedLocalModelProcessError("invalid_configuration");
	}
	return value;
}

function createPendingStart(controller: AbortController): PendingStart {
	let finish: () => void = () => undefined;
	const settled = new Promise<void>((resolve) => {
		finish = resolve;
	});
	return { controller, settled, finish };
}

function waitForSpawn(child: ChildProcess): Promise<void> {
	return new Promise((resolve, reject) => {
		const onSpawn = () => {
			child.off("error", onError);
			resolve();
		};
		const onError = (error: Error) => {
			child.off("spawn", onSpawn);
			reject(error);
		};
		child.once("spawn", onSpawn);
		child.once("error", onError);
	});
}

function spawnOwnedProcess(spec: CapturedProcessSpec, signal: AbortSignal) {
	return execa(spec.executable, spec.arguments, {
		cwd: spec.cwd,
		env: spec.env,
		extendEnv: false,
		stdin: spec.stdio.stdin,
		stdout: spec.stdio.stdout,
		stderr: spec.stdio.stderr,
		shell: false,
		buffer: false,
		cleanup: true,
		killDescendants: true,
		forceKillAfterDelay: spec.forceKillAfterDelayMs,
		cancelSignal: signal,
		reject: false,
	});
}

function settleProcess(subprocess: OwnedExecaSubprocess): Promise<Readonly<OwnedLocalModelProcessExit>> {
	const readExit = () =>
		Object.freeze({
			exitCode: subprocess.nodeChildProcess.exitCode,
			signal: subprocess.nodeChildProcess.signalCode,
		});
	return subprocess.then(readExit, readExit);
}

function waitForSettlement(settlement: Promise<void>, signal?: AbortSignal): Promise<void> {
	if (signal === undefined) return settlement;
	if (signal.aborted) return Promise.reject(signal.reason);
	return new Promise<void>((resolve, reject) => {
		const onAbort = () => {
			cleanup();
			reject(signal.reason);
		};
		const cleanup = () => signal.removeEventListener("abort", onAbort);
		signal.addEventListener("abort", onAbort, { once: true });
		settlement.then(
			() => {
				cleanup();
				resolve();
			},
			(error) => {
				cleanup();
				reject(error);
			},
		);
	});
}
