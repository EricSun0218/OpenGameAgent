export interface PngInspectionLimits {
	readonly maxBytes: number;
}

export interface PngChunk {
	readonly type: string;
	readonly offset: number;
	readonly dataOffset: number;
	readonly dataLength: number;
	readonly endOffset: number;
}

export interface ValidPngAnimation {
	readonly status: "valid";
	readonly frameCount: number;
	readonly playCount: number;
}

export interface InvalidPngAnimation {
	readonly status: "invalid";
}

export type PngAnimation = ValidPngAnimation | InvalidPngAnimation;

export interface PngInspection {
	readonly width: number;
	readonly height: number;
	readonly bitDepth: number;
	readonly colorType: number;
	readonly interlaceMethod: number;
	readonly animation?: PngAnimation;
	readonly chunks: readonly PngChunk[];
}

export type PngInspectionErrorCode =
	| "input_too_large"
	| "invalid_signature"
	| "truncated_chunk"
	| "invalid_chunk_type"
	| "invalid_crc"
	| "trailing_data"
	| "unknown_critical_chunk"
	| "nonconsecutive_idat"
	| "invalid_structure";

export class PngInspectionError extends Error {
	constructor(public readonly code: PngInspectionErrorCode) {
		super(code);
		this.name = "PngInspectionError";
	}
}
