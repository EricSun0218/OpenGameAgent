export class MailboxValidationError extends TypeError {
	constructor(readonly code: string) {
		super(code);
		this.name = "MailboxValidationError";
	}
}

export class MailboxConflictError extends Error {
	constructor() {
		super("mailbox_conflict");
		this.name = "MailboxConflictError";
	}
}

export class MailboxNotFoundError extends Error {
	constructor() {
		super("mailbox_message_not_found");
		this.name = "MailboxNotFoundError";
	}
}

export class MailboxRevisionConflictError extends Error {
	constructor() {
		super("mailbox_revision_conflict");
		this.name = "MailboxRevisionConflictError";
	}
}

export class MailboxTerminalError extends Error {
	constructor() {
		super("mailbox_message_terminal");
		this.name = "MailboxTerminalError";
	}
}

export class MailboxCapacityError extends Error {
	constructor(readonly code: "timeline_generation" | "recipient" | "active_deliveries") {
		super(`mailbox_capacity:${code}`);
		this.name = "MailboxCapacityError";
	}
}

export class MailboxLeaseConflictError extends Error {
	constructor() {
		super("mailbox_lease_conflict");
		this.name = "MailboxLeaseConflictError";
	}
}

export class MailboxOrderConflictError extends Error {
	constructor() {
		super("mailbox_order_conflict");
		this.name = "MailboxOrderConflictError";
	}
}
