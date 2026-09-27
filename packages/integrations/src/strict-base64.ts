import { Buffer } from "node:buffer";

const BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const BASE64_BODY = /^[A-Za-z0-9+/]*={0,2}$/u;

export interface InspectedCanonicalBase64 {
	readonly data: string;
	readonly byteLength: number;
}

export function inspectCanonicalBase64(value: unknown): InspectedCanonicalBase64 | undefined {
	if (typeof value !== "string" || value.length === 0 || !BASE64_BODY.test(value)) return undefined;
	const padding = value.endsWith("==") ? 2 : value.endsWith("=") ? 1 : 0;
	const unpaddedLength = value.length - padding;
	if (unpaddedLength === 0 || (padding > 0 && value.length % 4 !== 0)) return undefined;
	const remainder = unpaddedLength % 4;
	if (remainder === 1 || (padding === 2 && remainder !== 2) || (padding === 1 && remainder !== 3)) {
		return undefined;
	}
	const final = BASE64_ALPHABET.indexOf(value[unpaddedLength - 1] ?? "");
	if (final < 0 || (remainder === 2 && (final & 0x0f) !== 0) || (remainder === 3 && (final & 0x03) !== 0)) {
		return undefined;
	}
	const byteLength = Math.floor(unpaddedLength / 4) * 3 + (remainder === 2 ? 1 : remainder === 3 ? 2 : 0);
	if (!Number.isSafeInteger(byteLength) || byteLength <= 0) return undefined;
	return { data: value, byteLength };
}

export async function* decodeCanonicalBase64(value: Readonly<InspectedCanonicalBase64>): AsyncIterable<Uint8Array> {
	yield Buffer.from(value.data, "base64");
}
