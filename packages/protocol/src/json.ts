import { isProxy } from "node:util/types";

export type JsonPrimitive = boolean | number | string | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };

export class InvalidJsonValueError extends TypeError {
	constructor() {
		super("Value must be representable as JSON without unsupported coercion.");
		this.name = "InvalidJsonValueError";
	}
}

export class JsonValueLimitError extends RangeError {
	constructor(readonly code: "json_too_deep" | "json_too_large") {
		super(
			code === "json_too_deep"
				? "JSON value exceeds the configured depth."
				: "JSON value exceeds the configured byte budget.",
		);
		this.name = "JsonValueLimitError";
	}
}

export interface JsonValueLimits {
	readonly maxDepth: number;
	readonly maxBytes: number;
}

/**
 * Return a detached JSON value under JavaScript's finite-number semantics or fail before provider or persistence
 * boundaries. Unsupported values and non-finite numbers are rejected instead of being silently coerced.
 */
export function normalizeJsonValue(value: unknown): JsonValue {
	return normalizeJson(value);
}

/**
 * Detach and validate an in-process JSON value against host-owned admission budgets before it is copied into
 * runtime queues or transcripts. Depth is checked during the iterative walk; bytes cover the complete canonical
 * UTF-8 representation.
 */
export function normalizeJsonValueWithin(value: unknown, limits: JsonValueLimits): JsonValue {
	const resolvedLimits = captureJsonValueLimits(limits);
	return normalizeJson(value, resolvedLimits.maxDepth, resolvedLimits.maxBytes);
}

/** Normalize under host admission budgets and return canonical JSON without a second validation copy. */
export function canonicalizeJsonValueWithin(value: unknown, limits: JsonValueLimits): string {
	const resolvedLimits = captureJsonValueLimits(limits);
	return serializeCanonicalJsonValue(normalizeJson(value, resolvedLimits.maxDepth, resolvedLimits.maxBytes));
}

function normalizeJson(value: unknown, maxDepth?: number, maxBytes?: number): JsonValue {
	let canonicalBytes = 0;
	const active = new WeakSet<object>();
	const addBytes = (bytes: number) => {
		canonicalBytes += bytes;
		if (maxBytes !== undefined && canonicalBytes > maxBytes) throw new JsonValueLimitError("json_too_large");
	};
	type Frame =
		| {
				type: "array";
				source: unknown[];
				target: JsonValue[];
				depth: number;
				index: number;
				length: number;
		  }
		| {
				type: "object";
				source: Record<string, unknown>;
				target: { [key: string]: JsonValue };
				depth: number;
				index: number;
				keys: string[];
		  };
	const capture = (source: unknown, depth: number): { value: JsonValue; frame?: Frame } => {
		if (maxDepth !== undefined && depth > maxDepth) throw new JsonValueLimitError("json_too_deep");
		if (source === null || typeof source === "string" || typeof source === "boolean") {
			addBytes(primitiveCanonicalBytes(source));
			return { value: source };
		}
		if (typeof source === "number") {
			if (!Number.isFinite(source) || Object.is(source, -0)) {
				throw new InvalidJsonValueError();
			}
			addBytes(primitiveCanonicalBytes(source));
			return { value: source };
		}
		if (typeof source !== "object" || isProxy(source) || active.has(source)) {
			throw new InvalidJsonValueError();
		}
		active.add(source);

		if (Array.isArray(source)) {
			const target: JsonValue[] = [];
			const length = source.length;
			addBytes(2 + Math.max(0, length - 1));
			if (length === 0) {
				active.delete(source);
				return { value: target };
			}
			return { value: target, frame: { type: "array", source, target, depth, index: 0, length } };
		}

		const prototype = Object.getPrototypeOf(source);
		if (prototype !== Object.prototype && prototype !== null) {
			throw new InvalidJsonValueError();
		}
		const record = source as Record<string, unknown>;
		const target: { [key: string]: JsonValue } = {};
		const keys = Object.keys(record);
		addBytes(2 + Math.max(0, keys.length - 1));
		for (const key of keys) addBytes(jsonStringBytes(key) + 1);
		if (keys.length === 0) {
			active.delete(source);
			return { value: target };
		}
		return { value: target, frame: { type: "object", source: record, target, depth, index: 0, keys } };
	};

	const root = capture(value, 1);
	const frames: Frame[] = root.frame ? [root.frame] : [];
	while (frames.length > 0) {
		const frame = frames.at(-1);
		if (!frame) break;
		const length = frame.type === "array" ? frame.length : frame.keys.length;
		if (frame.index >= length) {
			active.delete(frame.source);
			frames.pop();
			continue;
		}
		const index = frame.index;
		frame.index += 1;
		const key = frame.type === "array" ? index : frame.keys[index];
		if (key === undefined) throw new InvalidJsonValueError();
		const descriptor = Object.getOwnPropertyDescriptor(frame.source, key);
		if (!descriptor || !("value" in descriptor)) throw new InvalidJsonValueError();
		const child = capture(descriptor.value, frame.depth + 1);
		if (frame.type === "array") frame.target[index] = child.value;
		else defineJsonProperty(frame.target, key as string, child.value);
		if (child.frame) frames.push(child.frame);
	}
	return root.value;
}

function primitiveCanonicalBytes(value: JsonPrimitive): number {
	if (typeof value === "string") return jsonStringBytes(value);
	if (value === null) return 4;
	if (typeof value === "boolean") return value ? 4 : 5;
	return JSON.stringify(value).length;
}

function jsonStringBytes(value: string): number {
	let bytes = 2;
	for (let index = 0; index < value.length; index += 1) {
		const unit = value.charCodeAt(index);
		if (
			unit === 0x22 ||
			unit === 0x5c ||
			unit === 0x08 ||
			unit === 0x09 ||
			unit === 0x0a ||
			unit === 0x0c ||
			unit === 0x0d
		) {
			bytes += 2;
			continue;
		}
		if (unit <= 0x1f || (unit >= 0xd800 && unit <= 0xdfff && !isSurrogatePair(value, index))) {
			bytes += 6;
			continue;
		}
		if (unit <= 0x7f) bytes += 1;
		else if (unit <= 0x7ff) bytes += 2;
		else if (unit >= 0xd800 && unit <= 0xdbff) {
			bytes += 4;
			index += 1;
		} else bytes += 3;
	}
	return bytes;
}

function isSurrogatePair(value: string, index: number): boolean {
	const unit = value.charCodeAt(index);
	if (unit >= 0xd800 && unit <= 0xdbff) {
		const next = value.charCodeAt(index + 1);
		return next >= 0xdc00 && next <= 0xdfff;
	}
	if (unit >= 0xdc00 && unit <= 0xdfff) {
		const previous = value.charCodeAt(index - 1);
		return previous >= 0xd800 && previous <= 0xdbff;
	}
	return false;
}

function defineJsonProperty(target: { [key: string]: JsonValue }, key: string, value: JsonValue): void {
	Object.defineProperty(target, key, {
		value,
		enumerable: true,
		configurable: true,
		writable: true,
	});
}

export function canonicalizeJsonValue(value: unknown): string {
	return serializeCanonicalJsonValue(normalizeJsonValue(value));
}

function serializeCanonicalJsonValue(value: JsonValue): string {
	type Task = { readonly kind: "value"; readonly value: JsonValue } | { readonly kind: "text"; readonly text: string };
	const chunks: string[] = [];
	const tasks: Task[] = [{ kind: "value", value }];
	while (tasks.length > 0) {
		const task = tasks.pop();
		if (!task) break;
		if (task.kind === "text") {
			chunks.push(task.text);
			continue;
		}
		const current = task.value;
		if (current === null || typeof current !== "object") {
			chunks.push(JSON.stringify(current));
			continue;
		}
		if (Array.isArray(current)) {
			tasks.push({ kind: "text", text: "]" });
			for (let index = current.length - 1; index >= 0; index -= 1) {
				tasks.push({ kind: "value", value: current[index] as JsonValue });
				if (index > 0) tasks.push({ kind: "text", text: "," });
			}
			tasks.push({ kind: "text", text: "[" });
			continue;
		}
		const keys = Object.keys(current).sort();
		tasks.push({ kind: "text", text: "}" });
		for (let index = keys.length - 1; index >= 0; index -= 1) {
			const key = keys[index] as string;
			tasks.push({ kind: "value", value: current[key] as JsonValue });
			tasks.push({ kind: "text", text: ":" });
			tasks.push({ kind: "text", text: JSON.stringify(key) });
			if (index > 0) tasks.push({ kind: "text", text: "," });
		}
		tasks.push({ kind: "text", text: "{" });
	}
	return chunks.join("");
}

function assertPositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value < 1) throw new RangeError(`${name} must be a positive safe integer.`);
}

function captureJsonValueLimits(limits: JsonValueLimits): JsonValueLimits {
	const resolved = { maxDepth: limits.maxDepth, maxBytes: limits.maxBytes };
	assertPositiveSafeInteger(resolved.maxDepth, "maxDepth");
	assertPositiveSafeInteger(resolved.maxBytes, "maxBytes");
	return resolved;
}
