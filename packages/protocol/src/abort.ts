/**
 * Invoke one operation and settle with either its result or the caller's exact abort reason.
 *
 * This only races observation. The operation must receive and honor the same signal when it owns cancellable work.
 */
export function raceOperationWithAbort<T>(operation: () => PromiseLike<T> | T, signal: AbortSignal): Promise<T> {
	signal.throwIfAborted();
	return new Promise((resolve, reject) => {
		let settled = false;
		const succeed = (value: T) => {
			if (settled) return;
			settled = true;
			signal.removeEventListener("abort", onAbort);
			resolve(value);
		};
		const fail = (error: unknown) => {
			if (settled) return;
			settled = true;
			signal.removeEventListener("abort", onAbort);
			reject(error);
		};
		const onAbort = () => fail(signal.reason);
		signal.addEventListener("abort", onAbort, { once: true });
		if (signal.aborted) {
			onAbort();
			return;
		}
		let pending: PromiseLike<T> | T;
		try {
			pending = operation();
		} catch (error) {
			fail(error);
			return;
		}
		Promise.resolve(pending).then(succeed, fail);
	});
}
