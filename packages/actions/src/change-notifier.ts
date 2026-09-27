const MAX_TIMER_DELAY_MS = 2_147_483_647;

export class ChangeNotifier {
	private revision = 0n;
	private readonly waiters = new Set<() => void>();

	snapshot(): bigint {
		return this.revision;
	}

	notify(): void {
		this.revision += 1n;
		for (const wake of [...this.waiters]) wake();
	}

	wait(afterRevision: bigint, timeoutMs: number, signal?: AbortSignal): Promise<boolean> {
		if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0 || timeoutMs > MAX_TIMER_DELAY_MS) {
			throw new TypeError("timeoutMs must be a positive supported timer delay.");
		}
		signal?.throwIfAborted();
		if (afterRevision !== this.revision) return Promise.resolve(true);
		return new Promise<boolean>((resolve, reject) => {
			let settled = false;
			const finish = (complete: () => void) => {
				if (settled) return;
				settled = true;
				clearTimeout(timer);
				this.waiters.delete(onChange);
				signal?.removeEventListener("abort", onAbort);
				complete();
			};
			const onAbort = () => finish(() => reject(signal?.reason));
			const onChange = () => finish(() => resolve(true));
			const timer = setTimeout(() => finish(() => resolve(false)), timeoutMs);
			this.waiters.add(onChange);
			signal?.addEventListener("abort", onAbort, { once: true });
			if (signal?.aborted) onAbort();
		});
	}
}
