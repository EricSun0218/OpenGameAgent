import { type JsonValue, parseStrictJsonText, StrictJsonError } from "@opengameagent/protocol";

export class RequestBodyError extends TypeError {
	constructor(
		public readonly code:
			| "body_too_large"
			| "invalid_json"
			| "json_too_deep"
			| "duplicate_json_key"
			| "unsafe_json_number",
	) {
		super(code);
		this.name = "RequestBodyError";
	}
}

export async function readBoundedJson(request: Request, maxBytes: number, maxDepth: number): Promise<JsonValue> {
	assertPositiveSafeInteger(maxBytes, "maxBytes");
	assertPositiveSafeInteger(maxDepth, "maxDepth");
	const declaredLength = request.headers.get("content-length");
	if (declaredLength !== null) {
		const parsedLength = Number(declaredLength);
		if (!Number.isSafeInteger(parsedLength) || parsedLength < 0 || parsedLength > maxBytes) {
			throw new RequestBodyError("body_too_large");
		}
	}
	if (!request.body) throw new RequestBodyError("invalid_json");

	const reader = request.body.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	while (true) {
		const result = await reader.read();
		if (result.done) break;
		total += result.value.byteLength;
		if (total > maxBytes) {
			await reader.cancel();
			throw new RequestBodyError("body_too_large");
		}
		chunks.push(result.value);
	}
	const bytes = new Uint8Array(total);
	let offset = 0;
	for (const chunk of chunks) {
		bytes.set(chunk, offset);
		offset += chunk.byteLength;
	}

	let text: string;
	try {
		text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
	} catch {
		throw new RequestBodyError("invalid_json");
	}

	try {
		return parseStrictJsonText(text, maxDepth);
	} catch (error) {
		if (error instanceof StrictJsonError) throw new RequestBodyError(error.code);
		throw error;
	}
}

function assertPositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
}
