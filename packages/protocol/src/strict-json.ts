import { evaluate, iterator, type NumberNode, type ObjectNode, parse } from "@humanwhocodes/momoa";
import { type JsonValue, normalizeJsonValue } from "./json.js";

export type StrictJsonErrorCode = "invalid_json" | "json_too_deep" | "duplicate_json_key" | "unsafe_json_number";

export class StrictJsonError extends TypeError {
	constructor(public readonly code: StrictJsonErrorCode) {
		super(code);
		this.name = "StrictJsonError";
	}
}

/** Parse detached JSON without duplicate keys or values that JavaScript cannot represent canonically. */
export function parseStrictJsonText(text: string, maxDepth: number): JsonValue {
	if (typeof text !== "string") throw new StrictJsonError("invalid_json");
	if (!Number.isSafeInteger(maxDepth) || maxDepth <= 0) {
		throw new TypeError("maxDepth must be a positive safe integer.");
	}
	try {
		const ast = parse(text, { mode: "json", allowTrailingCommas: false });
		let depth = 0;
		for (const { node, phase } of iterator(ast)) {
			if (node.type === "Object" || node.type === "Array") {
				depth += phase === "enter" ? 1 : -1;
				if (depth > maxDepth) throw new StrictJsonError("json_too_deep");
			}
			if (phase === "enter" && node.type === "Object") assertUniqueKeys(node as ObjectNode);
			if (phase === "enter" && node.type === "Number") assertSafeNumber(node as NumberNode);
		}
		return normalizeJsonValue(evaluate(ast));
	} catch (error) {
		if (error instanceof StrictJsonError) throw error;
		throw new StrictJsonError("invalid_json");
	}
}

function assertSafeNumber(node: NumberNode): void {
	if (
		!Number.isFinite(node.value) ||
		Object.is(node.value, -0) ||
		(Number.isInteger(node.value) && !Number.isSafeInteger(node.value))
	) {
		throw new StrictJsonError("unsafe_json_number");
	}
}

function assertUniqueKeys(node: ObjectNode): void {
	const keys = new Set<string>();
	for (const member of node.members) {
		const key = member.name.type === "String" ? member.name.value : member.name.name;
		if (keys.has(key)) throw new StrictJsonError("duplicate_json_key");
		keys.add(key);
	}
}
