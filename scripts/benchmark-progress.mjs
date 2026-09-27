export class BenchmarkProgressError extends Error {
	constructor(code) {
		super(code);
		this.name = "BenchmarkProgressError";
		this.code = code;
	}
}

export async function withAbortableProgress(operation, timeoutMs, code) {
	const controller = new AbortController();
	const timeout = new BenchmarkProgressError(code);
	let timedOut = false;
	const timer = setTimeout(() => {
		timedOut = true;
		controller.abort(timeout);
	}, timeoutMs);
	try {
		const value = await operation(controller.signal);
		if (timedOut) throw timeout;
		return value;
	} catch (error) {
		if (timedOut) throw timeout;
		throw error;
	} finally {
		clearTimeout(timer);
	}
}

export async function withSharpProgress(pipeline, operation, timeoutMs, code) {
	const timeout = new BenchmarkProgressError(code);
	let timedOut = false;
	const timer = setTimeout(() => {
		timedOut = true;
		pipeline.destroy(timeout);
	}, timeoutMs);
	try {
		const value = await operation();
		if (timedOut) throw timeout;
		return value;
	} catch (error) {
		if (timedOut) throw timeout;
		throw error;
	} finally {
		clearTimeout(timer);
	}
}
