export function waitForAbort<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
	if (signal.aborted) return Promise.reject(signal.reason);
	return new Promise<T>((resolve, reject) => {
		const aborted = () => {
			cleanup();
			reject(signal.reason);
		};
		const cleanup = () => signal.removeEventListener("abort", aborted);
		signal.addEventListener("abort", aborted, { once: true });
		operation.then(
			(value) => {
				cleanup();
				resolve(value);
			},
			(error: unknown) => {
				cleanup();
				reject(error);
			},
		);
	});
}
