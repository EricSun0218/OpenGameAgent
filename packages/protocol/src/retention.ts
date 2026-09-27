import { Buffer } from "node:buffer";
import { isProxy } from "node:util/types";

/** Host-owned budgets for the complete protection list; bytes are the sum of UTF-8 identifier bytes. */
export interface RetentionProtectionBudget {
	readonly maxCount: number;
	readonly maxBytes: number;
}

/** Validate the entire list before allocating its detached snapshot or uniqueness set. */
export function captureRetainedIdentifiers(
	values: unknown,
	budget: RetentionProtectionBudget,
	name: string,
): readonly string[] {
	const maxCount = budget?.maxCount;
	const maxBytes = budget?.maxBytes;
	if (!Number.isSafeInteger(maxCount) || maxCount < 0 || !Number.isSafeInteger(maxBytes) || maxBytes < 0) {
		throw new TypeError("retainBudget must specify non-negative safe integer maxCount and maxBytes.");
	}
	if (!Array.isArray(values) || isProxy(values)) throw new TypeError(`${name} must be an array of strings.`);
	const length = values.length;
	if (length > maxCount) throw new RangeError(`${name} exceeds retainBudget.maxCount.`);
	let remainingBytes = maxBytes;
	for (let index = 0; index < length; index += 1) {
		const entry = Object.getOwnPropertyDescriptor(values, index);
		if (!entry || !("value" in entry) || typeof entry.value !== "string") {
			throw new TypeError(`${name} must contain string data properties.`);
		}
		if (entry.value.length > remainingBytes) throw new RangeError(`${name} exceeds retainBudget.maxBytes.`);
		remainingBytes -= Buffer.byteLength(entry.value, "utf8");
		if (remainingBytes < 0) throw new RangeError(`${name} exceeds retainBudget.maxBytes.`);
	}
	const captured: string[] = [];
	for (let index = 0; index < length; index += 1) captured.push(values[index]);
	if (new Set(captured).size !== length) throw new TypeError(`${name} must be unique.`);
	return Object.freeze(captured);
}
