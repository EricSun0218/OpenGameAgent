export class ScheduleValidationError extends TypeError {
	constructor(readonly code: string) {
		super(code);
		this.name = "ScheduleValidationError";
	}
}

export class ScheduleConflictError extends Error {
	constructor() {
		super("schedule_conflict");
		this.name = "ScheduleConflictError";
	}
}

export class ScheduleNotFoundError extends Error {
	constructor() {
		super("schedule_not_found");
		this.name = "ScheduleNotFoundError";
	}
}

export class ScheduleRevisionConflictError extends Error {
	constructor() {
		super("schedule_revision_conflict");
		this.name = "ScheduleRevisionConflictError";
	}
}

export class ScheduleTerminalError extends Error {
	constructor() {
		super("schedule_terminal");
		this.name = "ScheduleTerminalError";
	}
}

export class ScheduleCapacityError extends Error {
	constructor(readonly code: "active_schedules" | "pending_occurrences") {
		super(`schedule_capacity:${code}`);
		this.name = "ScheduleCapacityError";
	}
}

export class ScheduleAdvanceConflictError extends Error {
	constructor() {
		super("schedule_advance_conflict");
		this.name = "ScheduleAdvanceConflictError";
	}
}

export class ScheduleClockConflictError extends Error {
	constructor() {
		super("schedule_clock_conflict");
		this.name = "ScheduleClockConflictError";
	}
}

export class ScheduleMissedOccurrenceError extends Error {
	constructor() {
		super("schedule_missed_occurrence");
		this.name = "ScheduleMissedOccurrenceError";
	}
}

export class ScheduleAdvanceOverflowError extends Error {
	constructor() {
		super("schedule_advance_overflow");
		this.name = "ScheduleAdvanceOverflowError";
	}
}

export class ScheduleOccurrenceNotFoundError extends Error {
	constructor() {
		super("schedule_occurrence_not_found");
		this.name = "ScheduleOccurrenceNotFoundError";
	}
}

export class ScheduleOccurrenceLeaseConflictError extends Error {
	constructor() {
		super("schedule_occurrence_lease_conflict");
		this.name = "ScheduleOccurrenceLeaseConflictError";
	}
}
