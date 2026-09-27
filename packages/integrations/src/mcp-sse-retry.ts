import { GameMcpError } from "./mcp-errors.js";

const UTF8_DECODER = new TextDecoder("utf-8", { ignoreBOM: true });

export function validateMcpSseRetryLine(lineBytes: Uint8Array, maxDelayMs: number, isFirstLine = false): void {
	let end = lineBytes.byteLength;
	if (end > 0 && lineBytes[end - 1] === 10) end -= 1;
	if (end > 0 && lineBytes[end - 1] === 13) end -= 1;
	let line = UTF8_DECODER.decode(lineBytes.subarray(0, end));
	if (isFirstLine && line.startsWith("\uFEFF")) line = line.slice(1);
	const separator = line.indexOf(":");
	const field = separator < 0 ? line : line.slice(0, separator);
	if (field !== "retry") return;
	let value = separator < 0 ? "" : line.slice(separator + 1);
	if (value.startsWith(" ")) value = value.slice(1);
	if (!/^\d+$/u.test(value)) return;
	const delayMs = Number(value);
	if (!Number.isSafeInteger(delayMs) || delayMs > maxDelayMs) {
		throw new GameMcpError("transport_limit_exceeded");
	}
}
