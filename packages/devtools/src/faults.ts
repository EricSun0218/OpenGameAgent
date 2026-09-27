import { setTimeout as delay } from "node:timers/promises";

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;

export const FAULT_POINTS = [
	"provider.before-stream",
	"provider.mid-stream",
	"tool.before-execute",
	"tool.after-execute",
	"storage.before-commit",
	"storage.after-commit",
	"action.before-dispatch",
	"action.after-dispatch",
	"transport.before-send",
	"transport.after-send",
] as const;
export type FaultPoint = (typeof FAULT_POINTS)[number];

export type FaultAction =
	| { readonly kind: "throw" }
	| { readonly kind: "abort" }
	| { readonly kind: "delay"; readonly milliseconds: number };

export interface FaultRule {
	readonly point: FaultPoint;
	readonly occurrence: number;
	readonly action: FaultAction;
}

export class DeterministicFaultInjector {
	private readonly rules = new Map<string, FaultAction>();
	private readonly occurrences = new Map<FaultPoint, number>();

	constructor(rules: readonly FaultRule[]) {
		for (const rule of rules) {
			const point = rule.point;
			const occurrence = rule.occurrence;
			if (!Number.isSafeInteger(occurrence) || occurrence <= 0) {
				throw new RangeError("Fault occurrence must be a positive safe integer.");
			}
			const action = prepareFaultAction(rule.action);
			const key = ruleKey(point, occurrence);
			if (this.rules.has(key)) throw new TypeError("A fault point occurrence can have only one action.");
			this.rules.set(key, action);
		}
	}

	async checkpoint(point: FaultPoint, signal?: AbortSignal): Promise<void> {
		if (signal?.aborted) throw signal.reason;
		const occurrence = (this.occurrences.get(point) ?? 0) + 1;
		this.occurrences.set(point, occurrence);
		const action = this.rules.get(ruleKey(point, occurrence));
		if (!action) return;

		switch (action.kind) {
			case "throw":
				throw new InjectedFaultError(point, occurrence);
			case "abort":
				throw new DOMException("The conformance fixture injected an abort.", "AbortError");
			case "delay":
				await delay(action.milliseconds, undefined, signal ? { signal } : undefined);
		}
	}
}

export class InjectedFaultError extends Error {
	constructor(
		readonly point: FaultPoint,
		readonly occurrence: number,
	) {
		super(`The conformance fixture injected a fault at ${point}.`);
		this.name = "InjectedFaultError";
	}
}

export interface Deferred<T> {
	readonly promise: Promise<T>;
	resolve(value: T): void;
	reject(reason?: unknown): void;
}

export function deferred<T>(): Deferred<T> {
	let resolvePromise: ((value: T) => void) | undefined;
	let rejectPromise: ((reason?: unknown) => void) | undefined;
	const promise = new Promise<T>((resolve, reject) => {
		resolvePromise = resolve;
		rejectPromise = reject;
	});
	return {
		promise,
		resolve: (value) => resolvePromise?.(value),
		reject: (reason) => rejectPromise?.(reason),
	};
}

function ruleKey(point: FaultPoint, occurrence: number): string {
	return `${point}\u0000${occurrence}`;
}

function prepareFaultAction(action: FaultAction): FaultAction {
	const kind = action.kind;
	switch (kind) {
		case "throw":
			return Object.freeze({ kind });
		case "abort":
			return Object.freeze({ kind });
		case "delay": {
			const milliseconds = action.milliseconds;
			if (!Number.isSafeInteger(milliseconds) || milliseconds < 0 || milliseconds > NODE_MAX_TIMER_DELAY_MS) {
				throw new RangeError(`Fault delay must be between 0 and ${NODE_MAX_TIMER_DELAY_MS} milliseconds.`);
			}
			return Object.freeze({ kind, milliseconds });
		}
		default:
			throw new TypeError("Fault action kind is not supported.");
	}
}
