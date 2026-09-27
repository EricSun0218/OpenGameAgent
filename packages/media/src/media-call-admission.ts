import { MediaJobAdmissionFullError } from "./errors.js";

export interface MediaCallAdmissionLimits {
	readonly maxConcurrentCalls: number;
	readonly maxQueuedCalls: number;
}

export interface MediaCallReservation {
	/** False means the waiting reservation was cancelled before it acquired capacity. */
	readonly ready: Promise<boolean>;
	release(): void;
}

/** Share one instance across adapter registrations that share a provider's host call budget. */
export class MediaCallAdmissionGroup {
	private readonly maxConcurrentCalls: number;
	private readonly maxQueuedCalls: number;
	private activeCalls = 0;
	private readonly waiting = new Map<MediaCallReservation, () => void>();

	constructor(limits: Readonly<MediaCallAdmissionLimits>) {
		const maxConcurrentCalls = limits?.maxConcurrentCalls;
		const maxQueuedCalls = limits?.maxQueuedCalls;
		if (
			!Number.isSafeInteger(maxConcurrentCalls) ||
			maxConcurrentCalls < 1 ||
			!Number.isSafeInteger(maxQueuedCalls) ||
			maxQueuedCalls < 0
		) {
			throw new RangeError("Media admission requires explicit positive concurrent and nonnegative queued call limits.");
		}
		this.maxConcurrentCalls = maxConcurrentCalls;
		this.maxQueuedCalls = maxQueuedCalls;
	}

	reserve(signal: AbortSignal): MediaCallReservation {
		signal.throwIfAborted();
		if (this.activeCalls === this.maxConcurrentCalls && this.waiting.size >= this.maxQueuedCalls) {
			throw new MediaJobAdmissionFullError();
		}
		let state: "waiting" | "active" | "released" = "waiting";
		let resolveReady!: (admitted: boolean) => void;
		const ready = new Promise<boolean>((resolve) => {
			resolveReady = resolve;
		});
		const onAbort = () => {
			if (state === "waiting") reservation.release();
		};
		const activate = () => {
			state = "active";
			this.activeCalls++;
			signal.removeEventListener("abort", onAbort);
			resolveReady(true);
		};
		const reservation: MediaCallReservation = {
			ready,
			release: () => {
				if (state === "released") return;
				if (state === "active") this.activeCalls--;
				else this.waiting.delete(reservation);
				state = "released";
				signal.removeEventListener("abort", onAbort);
				resolveReady(false);
				const next = this.waiting.entries().next().value;
				if (next && this.activeCalls < this.maxConcurrentCalls) {
					this.waiting.delete(next[0]);
					next[1]();
				}
			},
		};
		if (this.activeCalls < this.maxConcurrentCalls) activate();
		else {
			this.waiting.set(reservation, activate);
			signal.addEventListener("abort", onAbort, { once: true });
			if (signal.aborted) onAbort();
		}
		return reservation;
	}
}
