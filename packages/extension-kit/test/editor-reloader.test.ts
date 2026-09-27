import { describe, expect, it } from "vitest";
import { type EditorGameExtensionGeneration, EditorGameExtensionReloader } from "../src/index.js";

const limits = { maxGenerationIdBytes: 128 } as const;

function deferred() {
	let resolvePromise: (() => void) | undefined;
	const promise = new Promise<void>((resolve) => {
		resolvePromise = resolve;
	});
	return { promise, resolve: () => resolvePromise?.() };
}

function generation(id: string, calls: string[]): EditorGameExtensionGeneration {
	return {
		generationId: id,
		stopAndDrain: async () => {
			calls.push(`${id}:stop`);
		},
		dispose: async () => {
			calls.push(`${id}:dispose`);
		},
	};
}

describe("editor-only extension generation reloader", () => {
	it("stops and disposes the current host generation before constructing its replacement", async () => {
		const calls: string[] = [];
		const stopEntered = deferred();
		const stopRelease = deferred();
		const controlled = new EditorGameExtensionReloader({ environment: "editor", limits });
		await controlled.activate(async () => ({
			generationId: "controlled-1",
			stopAndDrain: async () => {
				calls.push("controlled-1:stop");
				stopEntered.resolve();
				await stopRelease.promise;
			},
			dispose: async () => {
				calls.push("controlled-1:dispose");
			},
		}));
		const controlledReplacement = controlled.reload(async () => {
			calls.push("controlled-2:build");
			return generation("controlled-2", calls);
		});
		await stopEntered.promise;

		expect(controlled.currentGenerationId).toBe("controlled-1");
		expect(calls).not.toContain("controlled-2:build");
		stopRelease.resolve();
		await expect(controlledReplacement).resolves.toEqual({ generationId: "controlled-2" });
		expect(calls.indexOf("controlled-1:dispose")).toBeLessThan(calls.indexOf("controlled-2:build"));

		await controlled.close();
	});

	it("serializes a lifecycle stress burst and stops and disposes every generation exactly once", async () => {
		const calls: string[] = [];
		const reloader = new EditorGameExtensionReloader({ environment: "editor", limits });
		const ids = Array.from({ length: 128 }, (_, index) => `generation-${index}`);
		await reloader.activate(async () => generation(ids[0] as string, calls));
		await Promise.all(ids.slice(1).map(async (id) => reloader.reload(async () => generation(id, calls))));
		await reloader.close();

		for (const id of ids) {
			expect(calls.filter((call) => call === `${id}:stop`)).toHaveLength(1);
			expect(calls.filter((call) => call === `${id}:dispose`)).toHaveLength(1);
		}
		expect(reloader.currentGenerationId).toBeUndefined();
	});

	it("disposes a late replacement and preserves the exact caller cancellation reason", async () => {
		const calls: string[] = [];
		const buildEntered = deferred();
		const buildRelease = deferred();
		const reloader = new EditorGameExtensionReloader({ environment: "editor", limits });
		await reloader.activate(async () => generation("generation-old", calls));
		const controller = new AbortController();
		const reason = { code: "editor_cancelled" };
		const reload = reloader.reload(async () => {
			buildEntered.resolve();
			await buildRelease.promise;
			return generation("generation-late", calls);
		}, controller.signal);
		await buildEntered.promise;
		controller.abort(reason);
		buildRelease.resolve();

		await expect(reload).rejects.toBe(reason);
		expect(calls).toEqual(["generation-old:stop", "generation-old:dispose", "generation-late:dispose"]);
		expect(reloader.currentGenerationId).toBeUndefined();
	});

	it("clears a generation that finished disposal while its reload caller was cancelled", async () => {
		const disposeEntered = deferred();
		const disposeRelease = deferred();
		const reloader = new EditorGameExtensionReloader({ environment: "editor", limits });
		await reloader.activate(async () => ({
			generationId: "generation-disposing",
			stopAndDrain: async () => undefined,
			dispose: async () => {
				disposeEntered.resolve();
				await disposeRelease.promise;
			},
		}));
		const controller = new AbortController();
		const reason = new Error("cancelled while disposing");
		let replacementBuilt = false;
		const reload = reloader.reload(async () => {
			replacementBuilt = true;
			return generation("replacement", []);
		}, controller.signal);
		await disposeEntered.promise;
		controller.abort(reason);
		disposeRelease.resolve();

		await expect(reload).rejects.toBe(reason);
		expect(reloader.currentGenerationId).toBeUndefined();
		expect(replacementBuilt).toBe(false);
		await expect(reloader.activate(async () => generation("recovered", []))).resolves.toEqual({
			generationId: "recovered",
		});
		await reloader.close();
	});

	it("fails closed after a lifecycle fault and never constructs a competing generation", async () => {
		let builds = 0;
		const reloader = new EditorGameExtensionReloader({ environment: "editor", limits });
		await reloader.activate(async () => ({
			generationId: "faulted-generation",
			stopAndDrain: async () => {
				throw new Error("stop failed");
			},
			dispose: async () => undefined,
		}));

		await expect(
			reloader.reload(async () => {
				builds += 1;
				return generation("must-not-build", []);
			}),
		).rejects.toMatchObject({ code: "stop_failed" });
		expect(builds).toBe(0);
		await expect(reloader.reload(async () => generation("still-must-not-build", []))).rejects.toMatchObject({
			code: "lifecycle_faulted",
		});
	});

	it("rejects non-editor construction and operations after close", async () => {
		expect(
			() =>
				new EditorGameExtensionReloader({
					environment: "runtime" as "editor",
					limits,
				}),
		).toThrowError(expect.objectContaining({ code: "editor_only" }));
		const reloader = new EditorGameExtensionReloader({ environment: "editor", limits });
		await reloader.activate(async () => generation("generation", []));
		await reloader.close();
		await expect(reloader.activate(async () => generation("other", []))).rejects.toMatchObject({ code: "closed" });
		await expect(reloader.reload(async () => generation("other", []))).rejects.toMatchObject({ code: "closed" });
	});
});
