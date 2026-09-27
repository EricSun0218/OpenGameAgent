import { describe, expect, it } from "vitest";
import { BoundedAsyncQueue } from "../src/index.js";

describe("BoundedAsyncQueue", () => {
	it("delivers a long backlog in insertion order", async () => {
		const itemCount = 100_000;
		const queue = new BoundedAsyncQueue<number>(itemCount);
		const expected = Array.from({ length: itemCount }, (_, index) => index);
		for (const value of expected) queue.push(value);

		const actual: (number | undefined)[] = [];
		for (let index = 0; index < itemCount; index += 1) actual.push(await queue.next());

		expect(actual).toEqual(expected);
	});

	it("hands a value directly to a waiting consumer", async () => {
		const queue = new BoundedAsyncQueue<string>(1);
		const pending = queue.next();

		expect(queue.push("ready")).toBe(true);
		await expect(pending).resolves.toBe("ready");
	});

	it("closes on overflow while preserving the admitted backlog", async () => {
		const queue = new BoundedAsyncQueue<string>(2);
		queue.push("first");
		queue.push("second");

		expect({ overflowAccepted: queue.push("third"), didOverflow: queue.didOverflow }).toEqual({
			overflowAccepted: false,
			didOverflow: true,
		});
		await expect(Promise.all([queue.next(), queue.next(), queue.next()])).resolves.toEqual([
			"first",
			"second",
			undefined,
		]);
		expect(queue.push("after-overflow")).toBe(false);
	});

	it("wakes a waiting consumer when manually closed", async () => {
		const queue = new BoundedAsyncQueue<string>(1);
		const pending = queue.next();

		queue.close();

		await expect(pending).resolves.toBeUndefined();
		expect(queue.didOverflow).toBe(false);
	});

	it("rejects a second concurrent consumer", () => {
		const queue = new BoundedAsyncQueue<string>(1);
		void queue.next();

		expect(() => queue.next()).toThrow("BoundedAsyncQueue supports one consumer.");
	});

	it("rejects the undefined value reserved for closed reads", () => {
		const queue = new BoundedAsyncQueue<unknown>(1);

		expect(() => queue.push(undefined)).toThrow(TypeError);
	});

	it.each([-0, 0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1, Number.POSITIVE_INFINITY])(
		"rejects invalid capacity %s",
		(capacity) => {
			expect(() => new BoundedAsyncQueue(capacity)).toThrow(TypeError);
		},
	);
});
