interface PendingLease {
	key: string;
	resolve: (release: () => void) => void;
	reject: (error: Error) => void;
	signal?: AbortSignal;
	abort?: () => void;
}

export interface ActorSchedulerOptions {
	maximumConcurrentActors: number;
	maximumQueuedRuns: number;
}

export class ActorScheduler {
	private readonly activeKeys = new Set<string>();
	private readonly queue: PendingLease[] = [];

	constructor(private readonly options: ActorSchedulerOptions) {
		if (!Number.isInteger(options.maximumConcurrentActors) || options.maximumConcurrentActors < 1) {
			throw new RangeError("maximumConcurrentActors must be a positive integer.");
		}
		if (!Number.isInteger(options.maximumQueuedRuns) || options.maximumQueuedRuns < 0) {
			throw new RangeError("maximumQueuedRuns must be a non-negative integer.");
		}
	}

	acquire(key: string, signal?: AbortSignal): Promise<() => void> {
		signal?.throwIfAborted();
		if (this.queue.length >= this.options.maximumQueuedRuns && !this.canAcquire(key)) {
			return Promise.reject(new Error("The bounded actor run queue is full."));
		}
		return new Promise<() => void>((resolve, reject) => {
			const pending: PendingLease = { key, resolve, reject, ...(signal === undefined ? {} : { signal }) };
			if (signal) {
				pending.abort = () => {
					const index = this.queue.indexOf(pending);
					if (index >= 0) this.queue.splice(index, 1);
					reject(new DOMException("The actor run was cancelled while queued.", "AbortError"));
				};
				signal.addEventListener("abort", pending.abort, { once: true });
			}
			this.queue.push(pending);
			this.drain();
		});
	}

	get activeActorCount(): number {
		return this.activeKeys.size;
	}

	get queuedRunCount(): number {
		return this.queue.length;
	}

	private canAcquire(key: string): boolean {
		return this.activeKeys.size < this.options.maximumConcurrentActors && !this.activeKeys.has(key);
	}

	private drain(): void {
		for (let index = 0; index < this.queue.length && this.activeKeys.size < this.options.maximumConcurrentActors; ) {
			const pending = this.queue[index];
			if (!pending) return;
			if (pending.signal?.aborted) {
				this.queue.splice(index, 1);
				continue;
			}
			if (!this.canAcquire(pending.key)) {
				index += 1;
				continue;
			}
			this.queue.splice(index, 1);
			if (pending.abort && pending.signal) pending.signal.removeEventListener("abort", pending.abort);
			this.activeKeys.add(pending.key);
			let released = false;
			pending.resolve(() => {
				if (released) return;
				released = true;
				this.activeKeys.delete(pending.key);
				this.drain();
			});
		}
	}
}
