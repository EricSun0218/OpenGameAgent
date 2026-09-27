import { getEventListeners } from "node:events";
import { describe, expect, it, vi } from "vitest";
import { ChangeNotifier } from "../src/change-notifier.js";

describe("ChangeNotifier", () => {
	it("releases timers and abort listeners after repeated idle timeouts", async () => {
		vi.useFakeTimers();
		try {
			const notifier = new ChangeNotifier();
			const controller = new AbortController();
			for (let index = 0; index < 100; index += 1) {
				const waiting = notifier.wait(notifier.snapshot(), 1, controller.signal);
				expect(vi.getTimerCount()).toBe(1);
				expect(getEventListeners(controller.signal, "abort")).toHaveLength(1);
				await vi.advanceTimersByTimeAsync(1);
				await expect(waiting).resolves.toBe(false);
				expect(vi.getTimerCount()).toBe(0);
				expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
			}
		} finally {
			vi.useRealTimers();
		}
	});

	it("releases cancelled registrations and notifies the remaining waiter exactly once", async () => {
		vi.useFakeTimers();
		try {
			const notifier = new ChangeNotifier();
			const remaining = new AbortController();
			const changed = notifier.wait(notifier.snapshot(), 1_000, remaining.signal);
			for (let index = 0; index < 100; index += 1) {
				const controller = new AbortController();
				const reason = new Error("stop-waiting");
				const cancelled = notifier.wait(notifier.snapshot(), 1_000, controller.signal);
				const checked = expect(cancelled).rejects.toBe(reason);
				controller.abort(reason);
				await checked;
				expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
				expect(vi.getTimerCount()).toBe(1);
			}
			notifier.notify();
			await expect(changed).resolves.toBe(true);
			expect(getEventListeners(remaining.signal, "abort")).toHaveLength(0);
			expect(vi.getTimerCount()).toBe(0);
			notifier.notify();
			expect(vi.getTimerCount()).toBe(0);
		} finally {
			vi.useRealTimers();
		}
	});

	it("observes a notification between snapshot and wait without retaining timer or listener", async () => {
		vi.useFakeTimers();
		try {
			const notifier = new ChangeNotifier();
			const controller = new AbortController();
			const revision = notifier.snapshot();
			notifier.notify();
			await expect(notifier.wait(revision, 1_000, controller.signal)).resolves.toBe(true);
			expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
			expect(vi.getTimerCount()).toBe(0);
		} finally {
			vi.useRealTimers();
		}
	});

	it("honors an already-cancelled wait even when its snapshot is stale", () => {
		const notifier = new ChangeNotifier();
		const revision = notifier.snapshot();
		notifier.notify();
		const reason = new Error("cancelled");
		expect(() => notifier.wait(revision, 1_000, AbortSignal.abort(reason))).toThrow(reason);
	});
});
