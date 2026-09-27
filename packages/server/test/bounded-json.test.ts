import { describe, expect, it } from "vitest";
import { RequestBodyError, readBoundedJson } from "../src/index.js";

function request(body: string): Request {
	return new Request("https://example.invalid", { method: "POST", body });
}

describe("bounded JSON transport parsing", () => {
	it("rejects duplicate object keys before evaluation", async () => {
		await expect(readBoundedJson(request('{"same":1,"same":2}'), 100, 10)).rejects.toMatchObject({
			code: "duplicate_json_key",
		});
	});

	it("enforces explicit byte and nesting budgets", async () => {
		await expect(readBoundedJson(request('{"value":"long"}'), 4, 10)).rejects.toBeInstanceOf(RequestBodyError);
		await expect(readBoundedJson(request('{"outer":{"inner":true}}'), 100, 1)).rejects.toMatchObject({
			code: "json_too_deep",
		});
	});

	it("returns detached JSON under finite IEEE-754 semantics and rejects ambiguous negative zero", async () => {
		await expect(readBoundedJson(request('{"ok":[true,null]}'), 100, 10)).resolves.toEqual({ ok: [true, null] });
		await expect(readBoundedJson(request('{"value":-0}'), 100, 10)).rejects.toMatchObject({
			code: "unsafe_json_number",
		});
		await expect(readBoundedJson(request('{"value":0.100000000000000005}'), 100, 10)).resolves.toEqual({ value: 0.1 });
	});

	it("rejects integer lexemes outside JavaScript's exact range before evaluation", async () => {
		for (const body of [
			'{"value":9007199254740993}',
			'{"value":-9007199254740993}',
			'{"value":9.007199254740993e15}',
		]) {
			await expect(readBoundedJson(request(body), 100, 10)).rejects.toMatchObject({
				code: "unsafe_json_number",
			});
		}
		await expect(readBoundedJson(request('{"value":9007199254740991}'), 100, 10)).resolves.toEqual({
			value: Number.MAX_SAFE_INTEGER,
		});
	});
});
