import { crc32, deflateSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import { inspectPng, PngInspectionError } from "../src/index.js";

describe("bounded PNG envelope inspection", () => {
	it("returns ordered chunk offsets and immutable image/APNG facts", () => {
		const input = png([
			chunk("IHDR", ihdr(2, 3)),
			chunk("tEXt", Buffer.from("fixture\0value", "latin1")),
			chunk("IDAT", deflateSync(Buffer.from([0, 0, 0, 0, 0, 0, 0]))),
			chunk("IEND"),
		]);

		const inspected = inspectPng(input, exactLimits(input));

		expect(inspected).toEqual({
			width: 2,
			height: 3,
			bitDepth: 8,
			colorType: 0,
			interlaceMethod: 0,
			chunks: [
				{ type: "IHDR", offset: 8, dataOffset: 16, dataLength: 13, endOffset: 33 },
				{ type: "tEXt", offset: 33, dataOffset: 41, dataLength: 13, endOffset: 58 },
				{
					type: "IDAT",
					offset: 58,
					dataOffset: 66,
					dataLength: input.readUInt32BE(58),
					endOffset: input.byteLength - 12,
				},
				{
					type: "IEND",
					offset: input.byteLength - 12,
					dataOffset: input.byteLength - 4,
					dataLength: 0,
					endOffset: input.byteLength,
				},
			],
		});
	});

	it("validates a two-frame APNG and exposes only its declared animation facts", () => {
		const input = png([
			chunk("IHDR", ihdr(1, 1, 6)),
			chunk("acTL", u32s(2, 0)),
			chunk("fcTL", frameControl(0)),
			chunk("IDAT", deflateSync(Buffer.from([0, 0, 0, 0, 0]))),
			chunk("fcTL", frameControl(1)),
			chunk("fdAT", Buffer.concat([u32s(2), deflateSync(Buffer.from([0, 0, 0, 0, 0]))])),
			chunk("IEND"),
		]);

		expect(inspectPng(input, exactLimits(input)).animation).toEqual({
			status: "valid",
			frameCount: 2,
			playCount: 0,
		});
	});

	it("keeps a valid base PNG when malformed APNG ancillary chunks disable animation", () => {
		const input = png([
			chunk("IHDR", ihdr(1, 1, 6)),
			chunk("acTL", u32s(1, 0)),
			chunk("fcTL", frameControl(1)),
			chunk("IDAT", deflateSync(Buffer.from([0, 0, 0, 0, 0]))),
			chunk("IEND"),
		]);

		expect(inspectPng(input, exactLimits(input)).animation).toEqual({ status: "invalid" });
	});

	it("enforces PNG3's 31-bit ceiling on IHDR and every APNG four-byte integer", () => {
		const invalidHeader = ihdr(1, 1, 6);
		invalidHeader.writeUInt32BE(0x8000_0000, 0);
		const invalidPlayCount = u32s(1, 0x8000_0000);
		const invalidFrame = frameControl(0);
		invalidFrame.writeUInt32BE(0x8000_0000, 4);
		const baseChunks = [
			chunk("IHDR", ihdr(1, 1, 6)),
			chunk("IDAT", deflateSync(Buffer.from([0, 0, 0, 0, 0]))),
			chunk("IEND"),
		];

		const invalidHeaderPng = png([chunk("IHDR", invalidHeader), ...baseChunks.slice(1)]);
		const invalidPlayPng = png([
			baseChunks[0] ?? chunk("IHDR", ihdr(1, 1, 6)),
			chunk("acTL", invalidPlayCount),
			chunk("fcTL", frameControl(0)),
			...baseChunks.slice(1),
		]);
		const invalidFramePng = png([
			baseChunks[0] ?? chunk("IHDR", ihdr(1, 1, 6)),
			chunk("acTL", u32s(1, 0)),
			chunk("fcTL", invalidFrame),
			...baseChunks.slice(1),
		]);

		expect(() => inspectPng(invalidHeaderPng, exactLimits(invalidHeaderPng))).toThrowError(
			new PngInspectionError("invalid_structure"),
		);
		expect(inspectPng(invalidPlayPng, exactLimits(invalidPlayPng)).animation).toEqual({ status: "invalid" });
		expect(inspectPng(invalidFramePng, exactLimits(invalidFramePng)).animation).toEqual({ status: "invalid" });
	});

	it.each([
		["CRC mismatch", () => corruptCrc(validPng()), "invalid_crc"],
		["truncated chunk", () => validPng().subarray(0, validPng().byteLength - 1), "truncated_chunk"],
		["trailing bytes", () => Buffer.concat([validPng(), Buffer.from([0])]), "trailing_data"],
		[
			"non-consecutive image data",
			() =>
				png([chunk("IHDR", ihdr(1, 1)), chunk("IDAT", Buffer.from([1])), chunk("tEXt"), chunk("IDAT"), chunk("IEND")]),
			"nonconsecutive_idat",
		],
		[
			"unknown critical chunk",
			() => png([chunk("IHDR", ihdr(1, 1)), chunk("ABCD"), chunk("IDAT", Buffer.from([1])), chunk("IEND")]),
			"unknown_critical_chunk",
		],
	] as const)("rejects %s", (_name, makeInput, code) => {
		const input = makeInput();
		expect(() => inspectPng(input, exactLimits(input))).toThrowError(new PngInspectionError(code));
	});

	it("enforces the carrier budget and the PNG protocol chunk-length ceiling", () => {
		const input = validPng();
		const impossibleChunk = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), Buffer.alloc(12)]);
		impossibleChunk.writeUInt32BE(0x8000_0000, 8);

		expect(() => inspectPng(input, { maxBytes: input.byteLength - 1 })).toThrowError(
			new PngInspectionError("input_too_large"),
		);
		expect(() => inspectPng(impossibleChunk, exactLimits(impossibleChunk))).toThrowError(
			new PngInspectionError("invalid_structure"),
		);
	});

	it("snapshots the host carrier limit exactly once before reading bytes", () => {
		const input = validPng();
		let reads = 0;
		const limits = {
			get maxBytes() {
				reads += 1;
				return input.byteLength;
			},
		};

		inspectPng(input, limits);

		expect(reads).toBe(1);
	});
});

function validPng(): Buffer {
	return png([chunk("IHDR", ihdr(1, 1)), chunk("IDAT", deflateSync(Buffer.from([0, 0]))), chunk("IEND")]);
}

function exactLimits(input: Uint8Array) {
	return { maxBytes: input.byteLength };
}

function png(chunks: readonly Buffer[]): Buffer {
	return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), ...chunks]);
}

function chunk(type: string, data: Uint8Array = new Uint8Array()): Buffer {
	const typeBytes = Buffer.from(type, "ascii");
	const output = Buffer.alloc(12 + data.byteLength);
	output.writeUInt32BE(data.byteLength, 0);
	typeBytes.copy(output, 4);
	Buffer.from(data).copy(output, 8);
	output.writeUInt32BE(crc32(output.subarray(4, 8 + data.byteLength)) >>> 0, 8 + data.byteLength);
	return output;
}

function ihdr(width: number, height: number, colorType = 0): Buffer {
	const output = Buffer.alloc(13);
	output.writeUInt32BE(width, 0);
	output.writeUInt32BE(height, 4);
	output[8] = 8;
	output[9] = colorType;
	return output;
}

function frameControl(sequence: number): Buffer {
	const output = Buffer.alloc(26);
	output.writeUInt32BE(sequence, 0);
	output.writeUInt32BE(1, 4);
	output.writeUInt32BE(1, 8);
	output.writeUInt16BE(1, 20);
	output.writeUInt16BE(10, 22);
	return output;
}

function u32s(...values: readonly number[]): Buffer {
	const output = Buffer.alloc(values.length * 4);
	values.forEach((value, index) => {
		output.writeUInt32BE(value, index * 4);
	});
	return output;
}

function corruptCrc(input: Buffer): Buffer {
	const output = Buffer.from(input);
	output[29] = (output[29] ?? 0) ^ 1;
	return output;
}
