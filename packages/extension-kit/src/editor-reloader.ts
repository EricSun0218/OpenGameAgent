import { assertOpaqueIdentifier } from "@opengameagent/protocol";
import { Mutex } from "async-mutex";

export interface EditorGameExtensionGeneration {
	readonly generationId: string;
	stopAndDrain(signal: AbortSignal): void | Promise<void>;
	dispose(): void | Promise<void>;
}

export type EditorGameExtensionGenerationFactory = (
	signal: AbortSignal,
) => EditorGameExtensionGeneration | Promise<EditorGameExtensionGeneration>;

export interface EditorGameExtensionReloaderLimits {
	readonly maxGenerationIdBytes: number;
}

export interface EditorGameExtensionReloaderOptions {
	readonly environment: "editor";
	readonly limits: EditorGameExtensionReloaderLimits;
}

export interface EditorGameExtensionGenerationSnapshot {
	readonly generationId: string;
}

export type EditorGameExtensionReloaderErrorCode =
	| "editor_only"
	| "closed"
	| "already_active"
	| "no_current_generation"
	| "generation_invalid"
	| "generation_conflict"
	| "build_failed"
	| "stop_failed"
	| "dispose_failed"
	| "lifecycle_faulted";

export class EditorGameExtensionReloaderError extends Error {
	constructor(public readonly code: EditorGameExtensionReloaderErrorCode) {
		super(code);
		this.name = "EditorGameExtensionReloaderError";
	}
}

interface CapturedGeneration {
	readonly generationId: string;
	readonly stopAndDrain: EditorGameExtensionGeneration["stopAndDrain"];
	readonly dispose: EditorGameExtensionGeneration["dispose"];
}

const encoder = new TextEncoder();

/**
 * Serializes stop-the-world host generation replacement in an editor process.
 * It never discovers, imports, unloads, or executes extension package code.
 */
export class EditorGameExtensionReloader {
	private readonly serial = new Mutex();
	private readonly limits: Readonly<EditorGameExtensionReloaderLimits>;
	private current: CapturedGeneration | undefined;
	private faulted = false;
	private closed = false;

	constructor(options: EditorGameExtensionReloaderOptions) {
		if (options?.environment !== "editor") throw new EditorGameExtensionReloaderError("editor_only");
		if (
			!options.limits ||
			!Number.isSafeInteger(options.limits.maxGenerationIdBytes) ||
			options.limits.maxGenerationIdBytes <= 0
		) {
			throw new RangeError("maxGenerationIdBytes must be a positive safe integer.");
		}
		this.limits = Object.freeze({ ...options.limits });
	}

	get currentGenerationId(): string | undefined {
		return this.current?.generationId;
	}

	activate(
		factory: EditorGameExtensionGenerationFactory,
		signal: AbortSignal = new AbortController().signal,
	): Promise<EditorGameExtensionGenerationSnapshot> {
		return this.serial.runExclusive(async () => {
			this.ensureUsable();
			if (this.current) throw new EditorGameExtensionReloaderError("already_active");
			const next = await this.build(factory, signal);
			this.current = next;
			return snapshot(next);
		});
	}

	reload(
		factory: EditorGameExtensionGenerationFactory,
		signal: AbortSignal = new AbortController().signal,
	): Promise<EditorGameExtensionGenerationSnapshot> {
		return this.serial.runExclusive(async () => {
			this.ensureUsable();
			const previous = this.current;
			if (!previous) throw new EditorGameExtensionReloaderError("no_current_generation");
			await this.stop(previous, signal);
			await this.disposeCurrent(previous, signal);
			this.current = undefined;
			signal.throwIfAborted();
			const next = await this.build(factory, signal, previous.generationId);
			this.current = next;
			return snapshot(next);
		});
	}

	close(signal: AbortSignal = new AbortController().signal): Promise<void> {
		return this.serial.runExclusive(async () => {
			if (this.closed) return;
			if (this.faulted) throw new EditorGameExtensionReloaderError("lifecycle_faulted");
			const current = this.current;
			if (current) {
				await this.stop(current, signal);
				await this.disposeCurrent(current, signal);
				this.current = undefined;
			}
			this.closed = true;
			signal.throwIfAborted();
		});
	}

	private ensureUsable(): void {
		if (this.closed) throw new EditorGameExtensionReloaderError("closed");
		if (this.faulted) throw new EditorGameExtensionReloaderError("lifecycle_faulted");
	}

	private async stop(generation: CapturedGeneration, signal: AbortSignal): Promise<void> {
		signal.throwIfAborted();
		try {
			await generation.stopAndDrain(signal);
		} catch {
			this.faulted = true;
			if (signal.aborted) throw signal.reason;
			throw new EditorGameExtensionReloaderError("stop_failed");
		}
	}

	private async disposeCurrent(generation: CapturedGeneration, signal: AbortSignal): Promise<void> {
		try {
			await generation.dispose();
		} catch {
			this.faulted = true;
			if (signal.aborted) throw signal.reason;
			throw new EditorGameExtensionReloaderError("dispose_failed");
		}
	}

	private async build(
		factory: EditorGameExtensionGenerationFactory,
		signal: AbortSignal,
		previousGenerationId?: string,
	): Promise<CapturedGeneration> {
		if (typeof factory !== "function") throw new TypeError("An editor generation factory is required.");
		signal.throwIfAborted();
		let candidate: EditorGameExtensionGeneration;
		try {
			candidate = await factory(signal);
		} catch {
			if (signal.aborted) throw signal.reason;
			throw new EditorGameExtensionReloaderError("build_failed");
		}
		let captured: CapturedGeneration;
		try {
			captured = this.captureGeneration(candidate);
			if (captured.generationId === previousGenerationId) {
				throw new EditorGameExtensionReloaderError("generation_conflict");
			}
		} catch (error) {
			await bestEffortDispose(candidate);
			throw error;
		}
		if (signal.aborted) {
			await bestEffortDispose(captured);
			throw signal.reason;
		}
		return captured;
	}

	private captureGeneration(value: EditorGameExtensionGeneration): CapturedGeneration {
		if (!value || typeof value !== "object") throw new EditorGameExtensionReloaderError("generation_invalid");
		const { generationId, stopAndDrain, dispose } = value;
		try {
			assertOpaqueIdentifier(generationId);
		} catch {
			throw new EditorGameExtensionReloaderError("generation_invalid");
		}
		if (
			encoder.encode(generationId).byteLength > this.limits.maxGenerationIdBytes ||
			typeof stopAndDrain !== "function" ||
			typeof dispose !== "function"
		) {
			throw new EditorGameExtensionReloaderError("generation_invalid");
		}
		return Object.freeze({
			generationId,
			stopAndDrain: stopAndDrain.bind(value),
			dispose: dispose.bind(value),
		});
	}
}

function snapshot(generation: CapturedGeneration): EditorGameExtensionGenerationSnapshot {
	return Object.freeze({ generationId: generation.generationId });
}

async function bestEffortDispose(value: Partial<EditorGameExtensionGeneration> | undefined): Promise<void> {
	try {
		if (value && typeof value.dispose === "function") await value.dispose();
	} catch {
		// The original validation or cancellation result remains authoritative.
	}
}
