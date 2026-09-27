import { Buffer } from "node:buffer";

export function decodeCanonicalBase64(value: unknown): Buffer | undefined {
	if (typeof value !== "string" || value.length === 0) return undefined;
	const decoded = Buffer.from(value, "base64");
	return decoded.byteLength > 0 && decoded.toString("base64") === value ? decoded : undefined;
}
