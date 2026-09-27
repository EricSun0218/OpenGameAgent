import { crc32 } from "node:zlib";
import {
	type PngAnimation,
	type PngChunk,
	type PngInspection,
	PngInspectionError,
	type PngInspectionLimits,
} from "./types.js";

const signature = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
const criticalChunkTypes = new Set(["IHDR", "PLTE", "IDAT", "IEND"]);

export function inspectPng(input: Uint8Array, limits: Readonly<PngInspectionLimits>): PngInspection {
	const resolvedLimits = captureLimits(limits);
	if (!(input instanceof Uint8Array)) throw new TypeError("input must be a Uint8Array.");
	if (input.byteLength > resolvedLimits.maxBytes) throw new PngInspectionError("input_too_large");
	if (!hasSignature(input)) throw new PngInspectionError("invalid_signature");

	const chunks: PngChunk[] = [];
	let offset = signature.byteLength;
	let width = 0;
	let height = 0;
	let bitDepth = 0;
	let colorType = 0;
	let interlaceMethod = 0;
	let sawPalette = false;
	let sawImageData = false;
	let imageDataEnded = false;

	for (;;) {
		if (input.byteLength - offset < 12) throw new PngInspectionError("truncated_chunk");
		const dataLength = readUint32(input, offset);
		if (dataLength > 0x7fff_ffff) throw new PngInspectionError("invalid_structure");
		const endOffset = offset + 12 + dataLength;
		if (!Number.isSafeInteger(endOffset) || endOffset > input.byteLength) {
			throw new PngInspectionError("truncated_chunk");
		}
		const type = readChunkType(input, offset + 4);
		const expectedCrc = readUint32(input, offset + 8 + dataLength);
		if (crc32(input.subarray(offset + 4, offset + 8 + dataLength)) >>> 0 !== expectedCrc) {
			throw new PngInspectionError("invalid_crc");
		}
		const chunk = Object.freeze({
			type,
			offset,
			dataOffset: offset + 8,
			dataLength,
			endOffset,
		});
		chunks.push(chunk);

		if (chunks.length === 1) {
			if (type !== "IHDR" || dataLength !== 13) throw new PngInspectionError("invalid_structure");
			const declaredWidth = readPngUint31(input, chunk.dataOffset);
			const declaredHeight = readPngUint31(input, chunk.dataOffset + 4);
			width = declaredWidth ?? 0;
			height = declaredHeight ?? 0;
			bitDepth = input[chunk.dataOffset + 8] ?? -1;
			colorType = input[chunk.dataOffset + 9] ?? -1;
			const compressionMethod = input[chunk.dataOffset + 10];
			const filterMethod = input[chunk.dataOffset + 11];
			interlaceMethod = input[chunk.dataOffset + 12] ?? -1;
			if (
				declaredWidth === undefined ||
				declaredHeight === undefined ||
				width === 0 ||
				height === 0 ||
				!isValidBitDepth(bitDepth, colorType) ||
				compressionMethod !== 0 ||
				filterMethod !== 0 ||
				(interlaceMethod !== 0 && interlaceMethod !== 1)
			) {
				throw new PngInspectionError("invalid_structure");
			}
			offset = endOffset;
			continue;
		}

		if (type === "IHDR") throw new PngInspectionError("invalid_structure");
		if (isCritical(type) && !criticalChunkTypes.has(type)) {
			throw new PngInspectionError("unknown_critical_chunk");
		}
		if (type !== "IDAT" && sawImageData) imageDataEnded = true;

		switch (type) {
			case "PLTE":
				if (
					sawPalette ||
					sawImageData ||
					colorType === 0 ||
					colorType === 4 ||
					dataLength === 0 ||
					dataLength > 768 ||
					dataLength % 3 !== 0 ||
					(colorType === 3 && dataLength / 3 > 2 ** bitDepth)
				) {
					throw new PngInspectionError("invalid_structure");
				}
				sawPalette = true;
				break;
			case "IDAT":
				if (imageDataEnded) throw new PngInspectionError("nonconsecutive_idat");
				if (colorType === 3 && !sawPalette) {
					throw new PngInspectionError("invalid_structure");
				}
				sawImageData = true;
				break;
			case "IEND": {
				if (endOffset !== input.byteLength) throw new PngInspectionError("trailing_data");
				if (dataLength !== 0 || !sawImageData) {
					throw new PngInspectionError("invalid_structure");
				}
				const animation = inspectAnimation(input, chunks, width, height);
				return Object.freeze({
					width,
					height,
					bitDepth,
					colorType,
					interlaceMethod,
					...(animation === undefined ? {} : { animation }),
					chunks: Object.freeze(chunks),
				});
			}
		}
		offset = endOffset;
	}
}

function inspectAnimation(
	input: Uint8Array,
	chunks: readonly PngChunk[],
	canvasWidth: number,
	canvasHeight: number,
): PngAnimation | undefined {
	const animationChunks = chunks.filter(
		(chunk) => chunk.type === "acTL" || chunk.type === "fcTL" || chunk.type === "fdAT",
	);
	if (animationChunks.length === 0) return undefined;
	const controlChunks = animationChunks.filter((chunk) => chunk.type === "acTL");
	if (controlChunks.length !== 1) return invalidAnimation();
	const control = controlChunks[0];
	if (control === undefined) return invalidAnimation();
	const firstImageDataIndex = chunks.findIndex((chunk) => chunk.type === "IDAT");
	const controlIndex = chunks.indexOf(control);
	if (control.dataLength !== 8 || controlIndex > firstImageDataIndex) return invalidAnimation();
	const frameCount = readPngUint31(input, control.dataOffset);
	const playCount = readPngUint31(input, control.dataOffset + 4);
	if (frameCount === undefined || playCount === undefined || frameCount === 0) return invalidAnimation();

	let expectedSequence = 0;
	let frames = 0;
	let sawImageData = false;
	let currentFrameHasData = false;
	let currentFrameUsesImageData = false;
	for (const chunk of chunks) {
		switch (chunk.type) {
			case "IDAT":
				sawImageData = true;
				if (currentFrameUsesImageData) currentFrameHasData = true;
				break;
			case "fcTL":
				if (
					chunk.dataLength !== 26 ||
					readPngUint31(input, chunk.dataOffset) !== expectedSequence ||
					(frames > 0 && !currentFrameHasData)
				) {
					return invalidAnimation();
				}
				expectedSequence += 1;
				frames += 1;
				currentFrameHasData = false;
				currentFrameUsesImageData = !sawImageData;
				if (!isValidFrameControl(input, chunk.dataOffset, canvasWidth, canvasHeight, currentFrameUsesImageData)) {
					return invalidAnimation();
				}
				break;
			case "fdAT":
				if (
					!sawImageData ||
					frames === 0 ||
					currentFrameUsesImageData ||
					chunk.dataLength < 4 ||
					readPngUint31(input, chunk.dataOffset) !== expectedSequence
				) {
					return invalidAnimation();
				}
				expectedSequence += 1;
				currentFrameHasData = true;
				break;
		}
	}
	if (frames !== frameCount || !currentFrameHasData) return invalidAnimation();
	return Object.freeze({ status: "valid", frameCount, playCount });
}

function invalidAnimation(): PngAnimation {
	return Object.freeze({ status: "invalid" });
}

function captureLimits(limits: Readonly<PngInspectionLimits>): PngInspectionLimits {
	const resolved = { maxBytes: limits.maxBytes };
	if (!Number.isSafeInteger(resolved.maxBytes) || resolved.maxBytes < 1) {
		throw new RangeError("maxBytes must be a positive safe integer.");
	}
	return resolved;
}

function hasSignature(input: Uint8Array): boolean {
	if (input.byteLength < signature.byteLength) return false;
	for (let index = 0; index < signature.byteLength; index += 1) {
		if (input[index] !== signature[index]) return false;
	}
	return true;
}

function readChunkType(input: Uint8Array, offset: number): string {
	let type = "";
	for (let index = 0; index < 4; index += 1) {
		const value = input[offset + index];
		if (value === undefined || !isAsciiLetter(value) || (index === 2 && value >= 0x61)) {
			throw new PngInspectionError("invalid_chunk_type");
		}
		type += String.fromCharCode(value);
	}
	return type;
}

function isAsciiLetter(value: number): boolean {
	return (value >= 0x41 && value <= 0x5a) || (value >= 0x61 && value <= 0x7a);
}

function isCritical(type: string): boolean {
	const first = type.charCodeAt(0);
	return first >= 0x41 && first <= 0x5a;
}

function readUint32(input: Uint8Array, offset: number): number {
	return (
		((input[offset] ?? 0) * 0x1_00_00_00 +
			(input[offset + 1] ?? 0) * 0x1_00_00 +
			(input[offset + 2] ?? 0) * 0x1_00 +
			(input[offset + 3] ?? 0)) >>>
		0
	);
}

function readPngUint31(input: Uint8Array, offset: number): number | undefined {
	const value = readUint32(input, offset);
	return value <= 0x7fff_ffff ? value : undefined;
}

function isValidBitDepth(bitDepth: number, colorType: number): boolean {
	switch (colorType) {
		case 0:
			return bitDepth === 1 || bitDepth === 2 || bitDepth === 4 || bitDepth === 8 || bitDepth === 16;
		case 2:
		case 4:
		case 6:
			return bitDepth === 8 || bitDepth === 16;
		case 3:
			return bitDepth === 1 || bitDepth === 2 || bitDepth === 4 || bitDepth === 8;
		default:
			return false;
	}
}

function isValidFrameControl(
	input: Uint8Array,
	offset: number,
	canvasWidth: number,
	canvasHeight: number,
	isDefaultImageFrame: boolean,
): boolean {
	const width = readPngUint31(input, offset + 4);
	const height = readPngUint31(input, offset + 8);
	const xOffset = readPngUint31(input, offset + 12);
	const yOffset = readPngUint31(input, offset + 16);
	const disposeOperation = input[offset + 24];
	const blendOperation = input[offset + 25];
	return (
		width !== undefined &&
		height !== undefined &&
		xOffset !== undefined &&
		yOffset !== undefined &&
		width > 0 &&
		height > 0 &&
		width <= canvasWidth &&
		height <= canvasHeight &&
		xOffset <= canvasWidth - width &&
		yOffset <= canvasHeight - height &&
		(disposeOperation === 0 || disposeOperation === 1 || disposeOperation === 2) &&
		(blendOperation === 0 || blendOperation === 1) &&
		(!isDefaultImageFrame || (width === canvasWidth && height === canvasHeight && xOffset === 0 && yOffset === 0))
	);
}
