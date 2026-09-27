export type ImageAttachmentErrorCode =
	| "invalid_configuration"
	| "invalid_input"
	| "input_too_large"
	| "decode_failed"
	| "media_type_mismatch"
	| "animated_image_unsupported"
	| "normalized_image_too_large"
	| "storage_missing"
	| "storage_unavailable"
	| "storage_corrupt"
	| "storage_changed";

export class ImageAttachmentError extends Error {
	constructor(public readonly code: ImageAttachmentErrorCode) {
		super(code);
		this.name = "ImageAttachmentError";
	}
}
