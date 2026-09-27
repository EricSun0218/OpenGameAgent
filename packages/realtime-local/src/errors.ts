export class LocalSpeechConfigurationError extends TypeError {
	constructor() {
		super("invalid_local_speech_configuration");
		this.name = "LocalSpeechConfigurationError";
	}
}

export class LocalSpeechProtocolError extends Error {
	constructor() {
		super("invalid_local_speech_payload");
		this.name = "LocalSpeechProtocolError";
	}
}

export class LocalSpeechCapacityError extends Error {
	constructor() {
		super("local_speech_capacity_exceeded");
		this.name = "LocalSpeechCapacityError";
	}
}

export class LocalSpeechTimeoutError extends Error {
	constructor() {
		super("local_speech_provider_timeout");
		this.name = "LocalSpeechTimeoutError";
	}
}

export class LocalSpeechProviderError extends Error {
	constructor() {
		super("local_speech_provider_failed");
		this.name = "LocalSpeechProviderError";
	}
}

export class LocalSpeechBargeInError extends Error {
	constructor() {
		super("local_speech_barge_in_coordinate_mismatch");
		this.name = "LocalSpeechBargeInError";
	}
}
