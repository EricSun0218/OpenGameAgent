import { decode, encode } from "cbor-x";
import type { JsonValue } from "./runtime.js";

export interface RuntimeWireLimits {
	maximumBytes: number;
	maximumDepth: number;
	maximumCollectionItems: number;
	maximumStringCharacters: number;
}

export const defaultRuntimeWireLimits: RuntimeWireLimits = {
	maximumBytes: 1024 * 1024,
	maximumDepth: 32,
	maximumCollectionItems: 4096,
	maximumStringCharacters: 256 * 1024,
};

function validateValue(value: unknown, limits: RuntimeWireLimits, depth = 0): asserts value is JsonValue {
	if (depth > limits.maximumDepth) {
		throw new RangeError("Runtime frame exceeds the maximum nesting depth.");
	}
	if (value === null || typeof value === "boolean") return;
	if (typeof value === "number") {
		if (!Number.isFinite(value)) throw new TypeError("Runtime frames require finite JSON numbers.");
		return;
	}
	if (typeof value === "string") {
		if (value.length > limits.maximumStringCharacters) {
			throw new RangeError("Runtime frame contains an oversized string.");
		}
		return;
	}
	if (Array.isArray(value)) {
		if (value.length > limits.maximumCollectionItems) {
			throw new RangeError("Runtime frame contains an oversized collection.");
		}
		for (const item of value) validateValue(item, limits, depth + 1);
		return;
	}
	if (typeof value === "object") {
		const entries = Object.entries(value);
		if (entries.length > limits.maximumCollectionItems) {
			throw new RangeError("Runtime frame contains an oversized object.");
		}
		for (const [key, item] of entries) {
			if (key.length > limits.maximumStringCharacters) {
				throw new RangeError("Runtime frame contains an oversized property name.");
			}
			validateValue(item, limits, depth + 1);
		}
		return;
	}
	throw new TypeError("Runtime frames may only contain JSON-compatible values.");
}

export function encodeRuntimeFrame(value: JsonValue, limits = defaultRuntimeWireLimits): Uint8Array {
	validateValue(value, limits);
	const bytes = encode(value);
	if (bytes.byteLength > limits.maximumBytes) throw new RangeError("Runtime frame exceeds the maximum byte size.");
	return bytes;
}

export function decodeRuntimeFrame(bytes: Uint8Array, limits = defaultRuntimeWireLimits): JsonValue {
	if (bytes.byteLength > limits.maximumBytes) throw new RangeError("Runtime frame exceeds the maximum byte size.");
	const value: unknown = decode(bytes);
	validateValue(value, limits);
	return value;
}
