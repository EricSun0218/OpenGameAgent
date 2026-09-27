import { describe, expect, it, vi } from "vitest";
import { raceOperationWithAbort } from "../src/index.js";

describe("raceOperationWithAbort", () => {
	it("does not invoke an operation after the caller already aborted", async () => {
		const controller = new AbortController();
		const reason = new Error("already stopped");
		const operation = vi.fn(() => "unused");
		controller.abort(reason);

		try {
			raceOperationWithAbort(operation, controller.signal);
			throw new Error("expected synchronous abort");
		} catch (error) {
			expect(error).toBe(reason);
		}
		expect(operation).not.toHaveBeenCalled();
	});

	it("rejects a stalled operation with the exact caller reason", async () => {
		const controller = new AbortController();
		const reason = new Error("stop waiting");
		const pending = raceOperationWithAbort(() => new Promise<never>(() => undefined), controller.signal);
		controller.abort(reason);

		await expect(pending).rejects.toBe(reason);
	});

	it("preserves synchronous failure and successful settlement", async () => {
		const failure = new Error("failed now");
		await expect(
			raceOperationWithAbort(() => {
				throw failure;
			}, new AbortController().signal),
		).rejects.toBe(failure);
		await expect(raceOperationWithAbort(() => 7, new AbortController().signal)).resolves.toBe(7);
	});
});
