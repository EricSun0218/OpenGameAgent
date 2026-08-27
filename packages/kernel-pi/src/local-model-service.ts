import { type ChildProcess, spawn } from "node:child_process";
import type { LocalGameModelBackend } from "./local-models.js";

export type LocalGameModelServiceState = "stopped" | "starting" | "ready" | "failed";

export interface LocalGameModelDescriptor {
	id: string;
	state?: "unloaded" | "loading" | "loaded" | "downloading" | "sleeping";
	input?: readonly ("text" | "image")[];
	contextWindow?: number;
}

export interface LocalGameModelCatalogOptions {
	backend: LocalGameModelBackend;
	endpoint?: string;
	timeoutMilliseconds?: number;
	maximumResponseBytes?: number;
	maximumModels?: number;
	headers?: Readonly<Record<string, string>>;
	fetch?: typeof globalThis.fetch;
}

export interface LocalGameModelProcessController {
	start(signal?: AbortSignal): Promise<void>;
	stop(signal?: AbortSignal): Promise<void>;
}

export interface SpawnedGameLocalModelProcessOptions {
	executable: string;
	arguments?: readonly string[];
	workingDirectory?: string;
	environment?: Readonly<Record<string, string>>;
	shutdownTimeoutMilliseconds?: number;
}

export interface LocalGameModelServiceOptions extends LocalGameModelCatalogOptions {
	process?: LocalGameModelProcessController;
	startupTimeoutMilliseconds?: number;
	pollIntervalMilliseconds?: number;
}

export interface LocalGameModelServiceSnapshot {
	state: LocalGameModelServiceState;
	models: readonly LocalGameModelDescriptor[];
	startedProcess: boolean;
	failure?: "unreachable" | "invalid-response" | "capacity" | "cancelled" | "process";
}

const defaultEndpoints: Record<LocalGameModelBackend, string> = {
	ollama: "http://127.0.0.1:11434/v1",
	"lm-studio": "http://127.0.0.1:1234/v1",
	localai: "http://127.0.0.1:8080/v1",
	"llama.cpp": "http://127.0.0.1:8080/v1",
	vllm: "http://127.0.0.1:8000/v1",
};

function boundedInteger(value: number | undefined, fallback: number, minimum: number, maximum: number, name: string) {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < minimum || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function validateIdentifier(value: unknown): string {
	if (typeof value !== "string" || value.length < 1 || value.length > 512) {
		throw new TypeError("Local model id is invalid.");
	}
	for (const character of value) {
		const code = character.codePointAt(0) ?? 0;
		if (code < 32 || code === 127) throw new TypeError("Local model id is invalid.");
	}
	return value;
}

function containsControlCharacter(value: string): boolean {
	for (const character of value) {
		const code = character.codePointAt(0) ?? 0;
		if (code < 32 || code === 127) return true;
	}
	return false;
}

function validateEndpoint(value: string): URL {
	const url = new URL(value);
	const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (!loopback || (url.protocol !== "http:" && url.protocol !== "https:")) {
		throw new TypeError("Local model services require a loopback HTTP endpoint.");
	}
	if (url.username || url.password || url.hash || url.search)
		throw new TypeError("The local model endpoint is invalid.");
	url.pathname = url.pathname.replace(/\/+$/u, "");
	return url;
}

function validateHeaders(value: Readonly<Record<string, string>> | undefined): Readonly<Record<string, string>> {
	const normalized: Record<string, string> = {};
	const entries = Object.entries(value ?? {});
	if (entries.length > 32) throw new RangeError("Local model discovery has too many headers.");
	for (const [name, header] of entries) {
		if (
			!/^[!#$%&'*+\-.^_`|~0-9A-Za-z]{1,128}$/u.test(name) ||
			containsControlCharacter(header) ||
			header.length > 8192
		) {
			throw new TypeError("Local model discovery header is invalid.");
		}
		if (["host", "content-length", "connection"].includes(name.toLowerCase())) {
			throw new TypeError("Local model discovery cannot control transport headers.");
		}
		normalized[name] = header;
	}
	return normalized;
}

function endpointFor(base: URL, backend: LocalGameModelBackend, operation: "list" | "load" | "unload"): URL {
	const url = new URL(base);
	const basePath = url.pathname.replace(/\/+$/u, "").replace(/\/v1$/u, "");
	if (backend === "ollama") {
		if (operation !== "list") throw new Error("This local model backend does not expose managed load/unload.");
		url.pathname = `${basePath}/api/tags`;
		return url;
	}
	if (backend === "llama.cpp" && operation !== "list") {
		url.pathname = `${basePath}/models/${operation}`;
		return url;
	}
	url.pathname = backend === "llama.cpp" ? `${basePath}/models` : `${basePath}/v1/models`;
	return url;
}

async function readBoundedJson(response: Response, maximumBytes: number): Promise<unknown> {
	if (!response.body) throw new TypeError("Local model service returned no response body.");
	const declared = Number(response.headers.get("content-length"));
	if (Number.isFinite(declared) && declared > maximumBytes) throw new RangeError("Local model response is too large.");
	const reader = response.body.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	while (true) {
		const chunk = await reader.read();
		if (chunk.done) break;
		total += chunk.value.byteLength;
		if (total > maximumBytes) {
			await reader.cancel();
			throw new RangeError("Local model response is too large.");
		}
		chunks.push(chunk.value);
	}
	const bytes = new Uint8Array(total);
	let offset = 0;
	for (const chunk of chunks) {
		bytes.set(chunk, offset);
		offset += chunk.byteLength;
	}
	try {
		return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
	} catch {
		throw new TypeError("Local model service returned invalid JSON.");
	}
}

function parseModels(
	payload: unknown,
	backend: LocalGameModelBackend,
	maximumModels: number,
): LocalGameModelDescriptor[] {
	if (payload === null || typeof payload !== "object") throw new TypeError("Local model catalog is invalid.");
	const record = payload as Record<string, unknown>;
	const values = backend === "ollama" ? record["models"] : record["data"];
	if (!Array.isArray(values)) throw new TypeError("Local model catalog is invalid.");
	if (values.length > maximumModels) throw new RangeError("Local model catalog exceeds its configured capacity.");
	const result = values.map((value): LocalGameModelDescriptor => {
		if (value === null || typeof value !== "object") throw new TypeError("Local model catalog entry is invalid.");
		const item = value as Record<string, unknown>;
		const id = validateIdentifier(backend === "ollama" ? (item["name"] ?? item["model"]) : item["id"]);
		if (backend !== "llama.cpp") return { id };
		const status = item["status"];
		const state =
			status !== null && typeof status === "object" ? (status as Record<string, unknown>)["value"] : undefined;
		const allowedStates = new Set(["unloaded", "loading", "loaded", "downloading", "sleeping"]);
		if (state !== undefined && (typeof state !== "string" || !allowedStates.has(state))) {
			throw new TypeError("Local model state is invalid.");
		}
		const architecture = item["architecture"];
		const modalities =
			architecture !== null && typeof architecture === "object"
				? (architecture as Record<string, unknown>)["input_modalities"]
				: undefined;
		const input = Array.isArray(modalities)
			? modalities.filter((candidate): candidate is "text" | "image" => candidate === "text" || candidate === "image")
			: undefined;
		const meta = item["meta"];
		const contextWindow =
			meta !== null && typeof meta === "object" ? (meta as Record<string, unknown>)["n_ctx"] : undefined;
		const checkedState =
			typeof state === "string" && allowedStates.has(state)
				? (state as NonNullable<LocalGameModelDescriptor["state"]>)
				: undefined;
		return {
			id,
			...(checkedState === undefined ? {} : { state: checkedState }),
			...(input === undefined || input.length === 0 ? {} : { input: [...new Set(input)] }),
			...(typeof contextWindow !== "number" || !Number.isSafeInteger(contextWindow) || contextWindow < 1
				? {}
				: { contextWindow }),
		};
	});
	const ids = new Set<string>();
	for (const model of result) {
		if (ids.has(model.id)) throw new TypeError("Local model catalog contains duplicate ids.");
		ids.add(model.id);
	}
	return result.sort((left, right) => left.id.localeCompare(right.id));
}

export class LocalGameModelCatalogClient {
	private readonly endpoint: URL;
	private readonly fetcher: typeof globalThis.fetch;
	private readonly timeoutMilliseconds: number;
	private readonly maximumResponseBytes: number;
	private readonly maximumModels: number;
	private readonly headers: Readonly<Record<string, string>>;

	constructor(private readonly options: LocalGameModelCatalogOptions) {
		this.endpoint = validateEndpoint(options.endpoint ?? defaultEndpoints[options.backend]);
		this.fetcher = options.fetch ?? globalThis.fetch;
		this.timeoutMilliseconds = boundedInteger(options.timeoutMilliseconds, 5000, 100, 60_000, "timeoutMilliseconds");
		this.maximumResponseBytes = boundedInteger(
			options.maximumResponseBytes,
			2 * 1024 * 1024,
			1024,
			16 * 1024 * 1024,
			"maximumResponseBytes",
		);
		this.maximumModels = boundedInteger(options.maximumModels, 1024, 1, 10_000, "maximumModels");
		this.headers = validateHeaders(options.headers);
	}

	async list(signal?: AbortSignal): Promise<readonly LocalGameModelDescriptor[]> {
		const payload = await this.request("list", undefined, signal);
		return parseModels(payload, this.options.backend, this.maximumModels);
	}

	async load(model: string, signal?: AbortSignal): Promise<void> {
		validateIdentifier(model);
		if (this.options.backend !== "llama.cpp")
			throw new Error("This local model backend does not support managed load.");
		await this.request("load", { model }, signal);
	}

	async unload(model: string, signal?: AbortSignal): Promise<void> {
		validateIdentifier(model);
		if (this.options.backend !== "llama.cpp")
			throw new Error("This local model backend does not support managed unload.");
		await this.request("unload", { model }, signal);
	}

	private async request(operation: "list" | "load" | "unload", body: object | undefined, signal?: AbortSignal) {
		signal?.throwIfAborted();
		const timeout = AbortSignal.timeout(this.timeoutMilliseconds);
		const combined = signal ? AbortSignal.any([signal, timeout]) : timeout;
		let response: Response;
		try {
			response = await this.fetcher(endpointFor(this.endpoint, this.options.backend, operation), {
				method: body === undefined ? "GET" : "POST",
				redirect: "error",
				headers: body === undefined ? this.headers : { ...this.headers, "content-type": "application/json" },
				...(body === undefined ? {} : { body: JSON.stringify(body) }),
				signal: combined,
			});
		} catch {
			if (signal?.aborted) throw signal.reason ?? new Error("Local model operation was cancelled.");
			throw new Error("Local model service is unreachable.");
		}
		if (!response.ok) throw new Error(`Local model service returned HTTP ${response.status}.`);
		if (operation !== "list") {
			await response.body?.cancel();
			return {};
		}
		return await readBoundedJson(response, this.maximumResponseBytes);
	}
}

export class SpawnedGameLocalModelProcess implements LocalGameModelProcessController {
	private process: ChildProcess | undefined;
	private readonly shutdownTimeoutMilliseconds: number;

	constructor(private readonly options: SpawnedGameLocalModelProcessOptions) {
		if (!options.executable || options.executable.includes("\0"))
			throw new TypeError("A local model executable is required.");
		if ((options.arguments?.length ?? 0) > 128 || options.arguments?.some((value) => value.length > 16_384)) {
			throw new RangeError("Local model process arguments are invalid.");
		}
		this.shutdownTimeoutMilliseconds = boundedInteger(
			options.shutdownTimeoutMilliseconds,
			5000,
			100,
			60_000,
			"shutdownTimeoutMilliseconds",
		);
	}

	async start(signal?: AbortSignal): Promise<void> {
		signal?.throwIfAborted();
		if (this.process && this.process.exitCode === null) return;
		const child = spawn(this.options.executable, [...(this.options.arguments ?? [])], {
			cwd: this.options.workingDirectory,
			env: this.options.environment === undefined ? process.env : { ...process.env, ...this.options.environment },
			stdio: "ignore",
			shell: false,
			windowsHide: true,
		});
		this.process = child;
		await new Promise<void>((resolve, reject) => {
			const cleanup = () => {
				signal?.removeEventListener("abort", abort);
				child.removeListener("error", error);
				child.removeListener("spawn", spawnEvent);
			};
			const abort = () => {
				cleanup();
				reject(signal?.reason ?? new Error("Local model process start was cancelled."));
			};
			const error = () => {
				cleanup();
				reject(new Error("Local model process failed to start."));
			};
			const spawnEvent = () => {
				cleanup();
				resolve();
			};
			signal?.addEventListener("abort", abort, { once: true });
			child.once("error", error);
			child.once("spawn", spawnEvent);
		}).catch(async (error) => {
			child.kill();
			if (this.process === child) this.process = undefined;
			throw error;
		});
	}

	async stop(signal?: AbortSignal): Promise<void> {
		signal?.throwIfAborted();
		const child = this.process;
		if (!child) return;
		this.process = undefined;
		if (child.exitCode !== null) return;
		child.kill("SIGTERM");
		await new Promise<void>((resolve, reject) => {
			const timeout = setTimeout(() => {
				if (child.exitCode === null) child.kill("SIGKILL");
				resolve();
			}, this.shutdownTimeoutMilliseconds);
			const abort = () => {
				clearTimeout(timeout);
				reject(signal?.reason ?? new Error("Local model process stop was cancelled."));
			};
			child.once("exit", () => {
				clearTimeout(timeout);
				signal?.removeEventListener("abort", abort);
				resolve();
			});
			signal?.addEventListener("abort", abort, { once: true });
		});
	}
}

function wait(milliseconds: number, signal?: AbortSignal): Promise<void> {
	return new Promise((resolve, reject) => {
		if (signal?.aborted) {
			reject(signal.reason ?? new Error("Local model startup was cancelled."));
			return;
		}
		const abort = () => {
			clearTimeout(timer);
			reject(signal?.reason ?? new Error("Local model startup was cancelled."));
		};
		const timer = setTimeout(() => {
			signal?.removeEventListener("abort", abort);
			resolve();
		}, milliseconds);
		signal?.addEventListener("abort", abort, { once: true });
	});
}

type LocalGameModelFailure = NonNullable<LocalGameModelServiceSnapshot["failure"]>;

function failure(error: unknown, signal?: AbortSignal): LocalGameModelFailure {
	if (signal?.aborted) return "cancelled";
	if (error instanceof RangeError) return "capacity";
	if (error instanceof TypeError) return "invalid-response";
	return "unreachable";
}

export class LocalGameModelService implements AsyncDisposable {
	private readonly catalog: LocalGameModelCatalogClient;
	private readonly startupTimeoutMilliseconds: number;
	private readonly pollIntervalMilliseconds: number;
	private snapshotValue: LocalGameModelServiceSnapshot = { state: "stopped", models: [], startedProcess: false };
	private operation: Promise<LocalGameModelServiceSnapshot> | undefined;

	constructor(private readonly options: LocalGameModelServiceOptions) {
		this.catalog = new LocalGameModelCatalogClient(options);
		this.startupTimeoutMilliseconds = boundedInteger(
			options.startupTimeoutMilliseconds,
			30_000,
			100,
			120_000,
			"startupTimeoutMilliseconds",
		);
		this.pollIntervalMilliseconds = boundedInteger(
			options.pollIntervalMilliseconds,
			250,
			10,
			5000,
			"pollIntervalMilliseconds",
		);
	}

	snapshot(): LocalGameModelServiceSnapshot {
		return structuredClone(this.snapshotValue);
	}

	async start(signal?: AbortSignal): Promise<LocalGameModelServiceSnapshot> {
		if (this.snapshotValue.state === "ready") return this.snapshot();
		if (this.operation) return await this.operation;
		this.operation = this.startCore(signal).finally(() => {
			this.operation = undefined;
		});
		return await this.operation;
	}

	async refresh(signal?: AbortSignal): Promise<LocalGameModelServiceSnapshot> {
		try {
			const models = await this.catalog.list(signal);
			this.snapshotValue = { state: "ready", models, startedProcess: this.snapshotValue.startedProcess };
		} catch (error) {
			this.snapshotValue = { ...this.snapshotValue, state: "failed", models: [], failure: failure(error, signal) };
		}
		return this.snapshot();
	}

	async stop(signal?: AbortSignal): Promise<void> {
		if (this.snapshotValue.startedProcess) await this.options.process?.stop(signal);
		this.snapshotValue = { state: "stopped", models: [], startedProcess: false };
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.stop();
	}

	private async startCore(signal?: AbortSignal): Promise<LocalGameModelServiceSnapshot> {
		this.snapshotValue = { state: "starting", models: [], startedProcess: false };
		const initial = await this.refresh(signal);
		if (initial.state === "ready") return initial;
		if (!this.options.process) return initial;
		try {
			await this.options.process.start(signal);
			this.snapshotValue = { state: "starting", models: [], startedProcess: true };
		} catch {
			this.snapshotValue = { state: "failed", models: [], startedProcess: false, failure: "process" };
			return this.snapshot();
		}
		try {
			const deadline = Date.now() + this.startupTimeoutMilliseconds;
			while (Date.now() < deadline) {
				const current = await this.refresh(signal);
				if (current.state === "ready") return current;
				await wait(this.pollIntervalMilliseconds, signal);
			}
		} catch {
			await this.options.process.stop().catch(() => undefined);
			this.snapshotValue = { state: "failed", models: [], startedProcess: false, failure: "cancelled" };
			return this.snapshot();
		}
		await this.options.process.stop().catch(() => undefined);
		this.snapshotValue = { state: "failed", models: [], startedProcess: false, failure: "unreachable" };
		return this.snapshot();
	}
}
