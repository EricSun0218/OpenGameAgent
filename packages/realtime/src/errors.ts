import type { RealtimeVoiceFailureKind } from "./types.js";

export class RealtimeVoiceConfigurationError extends TypeError {
	constructor() {
		super("invalid_realtime_voice_configuration");
		this.name = "RealtimeVoiceConfigurationError";
	}
}

export class RealtimeVoiceBusyError extends Error {
	constructor() {
		super("A realtime voice channel already has an active utterance.");
		this.name = "RealtimeVoiceBusyError";
	}
}

export class RealtimeVoiceFailure extends Error {
	constructor(public readonly code: RealtimeVoiceFailureKind) {
		super(code);
		this.name = "RealtimeVoiceFailure";
	}
}
