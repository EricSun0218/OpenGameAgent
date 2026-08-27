import type { GameToolDefinition, JsonValue } from "@opengameagent/protocol";
import type { TSchema } from "typebox";
import { Compile } from "typebox/compile";

const supportedKeywords = new Set([
	"$id",
	"type",
	"title",
	"description",
	"default",
	"examples",
	"properties",
	"required",
	"additionalProperties",
	"items",
	"minItems",
	"maxItems",
	"uniqueItems",
	"minLength",
	"maxLength",
	"pattern",
	"format",
	"minimum",
	"maximum",
	"exclusiveMinimum",
	"exclusiveMaximum",
	"multipleOf",
	"enum",
	"const",
	"anyOf",
	"oneOf",
	"allOf",
]);

const numericKeywords = new Set([
	"minItems",
	"maxItems",
	"minLength",
	"maxLength",
	"minimum",
	"maximum",
	"exclusiveMinimum",
	"exclusiveMaximum",
	"multipleOf",
]);

function inspectSchema(value: JsonValue, depth: number, maximumDepth: number, path: string): void {
	if (depth > maximumDepth) throw new RangeError(`Tool schema exceeds the maximum depth at ${path}.`);
	if (value === null || typeof value !== "object") return;
	if (Array.isArray(value)) {
		for (const [index, item] of value.entries()) inspectSchema(item, depth + 1, maximumDepth, `${path}[${index}]`);
		return;
	}
	for (const [key, item] of Object.entries(value)) {
		if (key === "$ref") throw new Error(`Tool schema references are not supported at ${path}.`);
		const isPropertyName = path.endsWith(".properties");
		if (!isPropertyName && !supportedKeywords.has(key))
			throw new Error(`Unsupported tool schema keyword '${key}' at ${path}.`);
		if (numericKeywords.has(key) && (typeof item !== "number" || !Number.isFinite(item))) {
			throw new TypeError(`Tool schema keyword '${key}' must be a finite number.`);
		}
		inspectSchema(item, depth + 1, maximumDepth, `${path}.${key}`);
	}
}

export function preflightGameToolSchema(definition: GameToolDefinition, maximumDepth = 32): void {
	if (!definition.name || !/^[A-Za-z0-9_.:-]{1,128}$/.test(definition.name)) {
		throw new Error("Tool names must be 1-128 safe identifier characters.");
	}
	inspectSchema(definition.parameters, 0, maximumDepth, definition.name);
	try {
		Compile(definition.parameters as TSchema);
	} catch (error) {
		throw new Error(`Tool '${definition.name}' has an invalid parameter schema.`, { cause: error });
	}
}
