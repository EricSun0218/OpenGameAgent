export const LIMIT_EVIDENCE_KINDS = ["protocol", "reference", "benchmark", "derived", "host-required"] as const;
export type LimitEvidenceKind = (typeof LIMIT_EVIDENCE_KINDS)[number];

export type LimitUnit = "count" | "bytes" | "milliseconds" | "depth" | "port";
export type LimitMode = "fixed" | "defaulted" | "host-required" | "removed";

export interface LimitDefinition {
	readonly id: string;
	readonly unit: LimitUnit;
	readonly evidence: LimitEvidenceKind;
	readonly mode: LimitMode;
	readonly integer: boolean;
	readonly minimum?: number;
	readonly maximum?: number;
	readonly fixedValue?: number;
	readonly defaultValue?: number;
}

export const P0_LIMIT_DEFINITIONS = [
	definition("runtime.maxActiveActors", "count", "host-required", "host-required", 1),
	definition("runtime.maxQueuedRuns", "count", "host-required", "host-required", 1),
	definition("runtime.maxInputBytes", "bytes", "host-required", "host-required", 1),
	definition("runtime.maxInputDepth", "depth", "host-required", "host-required", 1),
	definition("server.maxRequestBytes", "bytes", "host-required", "host-required", 1),
	definition("server.maxResponseBytes", "bytes", "host-required", "host-required", 1),
	definition("server.maxJsonDepth", "depth", "host-required", "host-required", 1),
	definition("server.maxEventPageSize", "count", "host-required", "host-required", 1),
	definition("server.maxSseConnections", "count", "host-required", "host-required", 1),
	definition("server.maxSseEventBytes", "bytes", "host-required", "host-required", 1),
	definition("server.maxSseBacklog", "count", "host-required", "host-required", 1),
	definition("server.maxActionClaimCandidates", "count", "host-required", "host-required", 1),
	definition("server.actionClaimLeaseMs", "milliseconds", "host-required", "host-required", 1),
	definition("server.actionStreamWaitMs", "milliseconds", "host-required", "host-required", 1, 2_147_483_647),
	definition("server.maxApprovalPageSize", "count", "host-required", "host-required", 1),
	definition("server.port", "port", "protocol", "host-required", 0, 65_535),
	definition("client.maxHttpResponseBytes", "bytes", "host-required", "host-required", 1),
	definition("client.maxSseEventBytes", "bytes", "host-required", "host-required", 1),
	definition("client.sseDefaultDelayMs", "milliseconds", "host-required", "host-required", 1, 2_147_483_647),
	definition("client.sseMaximumDelayMs", "milliseconds", "host-required", "host-required", 1, 2_147_483_647),
	definition("client.sseMaximumAttempts", "count", "host-required", "host-required", 1),
	definition("sqlite.busyTimeoutMs", "milliseconds", "protocol", "host-required", 0, 2_147_483_647),
] as const satisfies readonly LimitDefinition[];

export type LimitValues = Readonly<Record<string, number>>;

export class LimitRegistry {
	private readonly definitions = new Map<string, LimitDefinition>();

	constructor(definitions: readonly LimitDefinition[]) {
		for (const entry of definitions) {
			validateDefinition(entry);
			if (this.definitions.has(entry.id)) throw new TypeError(`Duplicate limit definition: ${entry.id}`);
			this.definitions.set(entry.id, Object.freeze({ ...entry }));
		}
	}

	list(): readonly LimitDefinition[] {
		return [...this.definitions.values()].map((entry) => ({ ...entry }));
	}

	resolve(hostValues: LimitValues): LimitValues {
		for (const id of Object.keys(hostValues)) {
			if (!this.definitions.has(id)) throw new TypeError(`Unknown limit: ${id}`);
		}

		const resolved: Record<string, number> = {};
		for (const entry of this.definitions.values()) {
			const hostValue = hostValues[entry.id];
			switch (entry.mode) {
				case "fixed": {
					if (hostValue !== undefined && hostValue !== entry.fixedValue) {
						throw new RangeError(`${entry.id} is fixed by its protocol.`);
					}
					resolved[entry.id] = entry.fixedValue as number;
					break;
				}
				case "defaulted": {
					const value = hostValue ?? entry.defaultValue;
					validateValue(entry, value);
					resolved[entry.id] = value as number;
					break;
				}
				case "host-required": {
					if (hostValue === undefined) throw new TypeError(`The host must configure ${entry.id}.`);
					validateValue(entry, hostValue);
					resolved[entry.id] = hostValue;
					break;
				}
				case "removed": {
					if (hostValue !== undefined) throw new TypeError(`${entry.id} was intentionally removed.`);
					break;
				}
			}
		}
		return Object.freeze(resolved);
	}
}

function definition(
	id: string,
	unit: LimitUnit,
	evidence: LimitEvidenceKind,
	mode: LimitMode,
	minimum?: number,
	maximum?: number,
	defaultValue?: number,
): LimitDefinition {
	return {
		id,
		unit,
		evidence,
		mode,
		integer: true,
		...(minimum === undefined ? {} : { minimum }),
		...(maximum === undefined ? {} : { maximum }),
		...(defaultValue === undefined ? {} : { defaultValue }),
	};
}

function validateDefinition(entry: LimitDefinition): void {
	if (!/^[a-z][a-zA-Z0-9]*(?:\.[a-z][a-zA-Z0-9]*)+$/u.test(entry.id)) {
		throw new TypeError("Limit identifiers must be stable dotted names.");
	}
	if (!LIMIT_EVIDENCE_KINDS.includes(entry.evidence)) throw new TypeError(`Invalid limit evidence: ${entry.id}`);
	if (entry.minimum !== undefined) validateNumber(entry, entry.minimum);
	if (entry.maximum !== undefined) validateNumber(entry, entry.maximum);
	if (entry.minimum !== undefined && entry.maximum !== undefined && entry.minimum > entry.maximum) {
		throw new RangeError(`Invalid limit range: ${entry.id}`);
	}

	if (entry.mode === "fixed") {
		if (entry.fixedValue === undefined || entry.defaultValue !== undefined)
			throw new TypeError(`Invalid fixed limit: ${entry.id}`);
		validateValue(entry, entry.fixedValue);
	} else if (entry.mode === "defaulted") {
		if (entry.defaultValue === undefined || entry.fixedValue !== undefined) {
			throw new TypeError(`Invalid defaulted limit: ${entry.id}`);
		}
		validateValue(entry, entry.defaultValue);
	} else if (entry.fixedValue !== undefined || entry.defaultValue !== undefined) {
		throw new TypeError(`Unexpected limit value: ${entry.id}`);
	}
}

function validateValue(entry: LimitDefinition, value: number | undefined): void {
	if (value === undefined) throw new TypeError(`Missing limit value: ${entry.id}`);
	validateNumber(entry, value);
	if (entry.minimum !== undefined && value < entry.minimum) throw new RangeError(`${entry.id} is below its minimum.`);
	if (entry.maximum !== undefined && value > entry.maximum) throw new RangeError(`${entry.id} exceeds its maximum.`);
}

function validateNumber(entry: LimitDefinition, value: number): void {
	const valid = entry.integer ? Number.isSafeInteger(value) : Number.isFinite(value);
	if (!valid) throw new RangeError(`${entry.id} must be ${entry.integer ? "a safe integer" : "finite"}.`);
}
