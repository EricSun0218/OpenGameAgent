import { describe, expect, it } from "vitest";
import { PendingRequestQueue } from "../src/pending-request-queue.js";

describe("PendingRequestQueue", () => {
	it("dequeues pending requests in submission order", () => {
		const queue = new PendingRequestQueue<object>();
		const first = {};
		const second = {};

		queue.enqueue(first);
		queue.enqueue(second);

		expect(queue.size).toBe(2);
		expect(queue.dequeue()).toBe(first);
		expect(queue.dequeue()).toBe(second);
		expect(queue.dequeue()).toBeUndefined();
		expect(queue.size).toBe(0);
	});

	it("removes only the exact cancelled request without reordering its peers", () => {
		const queue = new PendingRequestQueue<object>();
		const first = { text: "same" };
		const cancelled = { text: "same" };
		const third = { text: "same" };

		queue.enqueue(first);
		queue.enqueue(cancelled);
		queue.enqueue(third);

		expect(queue.remove(cancelled)).toBe(true);
		expect(queue.remove(cancelled)).toBe(false);
		expect(queue.dequeue()).toBe(first);
		expect(queue.dequeue()).toBe(third);
		expect(queue.size).toBe(0);
	});

	it("drains every pending request in submission order", () => {
		const queue = new PendingRequestQueue<object>();
		const first = {};
		const second = {};
		queue.enqueue(first);
		queue.enqueue(second);

		expect([...queue.drain()]).toEqual([first, second]);
		expect(queue.size).toBe(0);
	});

	it("drains the benchmarked large FIFO workload without quadratic head removal", () => {
		const queue = new PendingRequestQueue<{ readonly sequence: number }>();
		const benchmarkDepth = 250_000;
		for (let sequence = 0; sequence < benchmarkDepth; sequence += 1) queue.enqueue({ sequence });

		let ordered = true;
		for (let sequence = 0; sequence < benchmarkDepth; sequence += 1) {
			if (queue.dequeue()?.sequence !== sequence) ordered = false;
		}

		expect(ordered).toBe(true);
		expect(queue.size).toBe(0);
	});
});
