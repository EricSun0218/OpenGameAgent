export class SignalValidationError extends TypeError {
	constructor(readonly code: string) {
		super(code);
		this.name = "SignalValidationError";
	}
}

export class SignalConflictError extends Error {
	constructor() {
		super("signal_conflict");
		this.name = "SignalConflictError";
	}
}

export class SignalWaitConflictError extends Error {
	constructor() {
		super("signal_wait_conflict");
		this.name = "SignalWaitConflictError";
	}
}

export class SignalWaitNotFoundError extends Error {
	constructor() {
		super("signal_wait_not_found");
		this.name = "SignalWaitNotFoundError";
	}
}

export class SignalWaitRevisionConflictError extends Error {
	constructor() {
		super("signal_wait_revision_conflict");
		this.name = "SignalWaitRevisionConflictError";
	}
}

export class SignalWaitTerminalError extends Error {
	constructor() {
		super("signal_wait_terminal");
		this.name = "SignalWaitTerminalError";
	}
}

export class SignalWaitFanoutError extends Error {
	constructor() {
		super("signal_wait_fanout");
		this.name = "SignalWaitFanoutError";
	}
}
