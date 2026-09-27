import { raceOperationWithAbort } from "@opengameagent/protocol";
import { LocalSpeechProtocolError } from "./errors.js";

const OWNED_ITERATOR_CLOSED = Object.freeze({ kind: "owned-async-iterator-closed" });

export interface CapturedAsyncIterator<T> {
	next(): PromiseLike<IteratorResult<T>> | IteratorResult<T>;
	close(): PromiseLike<unknown> | unknown;
}

export interface CapturedOwnedAsyncIterable<T> {
	readonly iterable: AsyncIterable<T>;
	close(): void;
}

export function captureAsyncIterator<T>(source: unknown): CapturedAsyncIterator<T> {
	let iterator: AsyncIterator<T> | undefined;
	let closeMethod: unknown;
	try {
		if (!source || (typeof source !== "object" && typeof source !== "function")) throw new Error();
		const create = (source as AsyncIterable<T>)[Symbol.asyncIterator];
		if (typeof create !== "function") throw new Error();
		iterator = Reflect.apply(create, source, []);
		if (!iterator || typeof iterator !== "object") throw new Error();
		closeMethod = iterator.return;
		const nextMethod = iterator.next;
		if (typeof nextMethod !== "function" || (closeMethod !== undefined && typeof closeMethod !== "function")) {
			throw new Error();
		}
		return Object.freeze({
			next: () => Reflect.apply(nextMethod, iterator as AsyncIterator<T>, []),
			close: () =>
				typeof closeMethod === "function" ? Reflect.apply(closeMethod, iterator as AsyncIterator<T>, []) : undefined,
		});
	} catch {
		if (iterator && typeof closeMethod === "function") {
			try {
				void Promise.resolve(Reflect.apply(closeMethod, iterator, [])).catch(() => undefined);
			} catch {
				// Cleanup cannot replace the stable boundary error.
			}
		}
		throw new LocalSpeechProtocolError();
	}
}

export function captureIteratorResult<T>(value: unknown): { readonly done: true } | { readonly done: false; value: T } {
	if (!value || typeof value !== "object") throw new LocalSpeechProtocolError();
	let done: unknown;
	let item: unknown;
	try {
		done = (value as IteratorResult<T>).done;
		if (!done) item = (value as IteratorYieldResult<T>).value;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	return done ? Object.freeze({ done: true }) : Object.freeze({ done: false, value: item as T });
}

export function captureIteratorResultWithAbort<T>(
	value: unknown,
	signal: AbortSignal,
): { readonly done: true } | { readonly done: false; value: T } {
	try {
		const result = captureIteratorResult<T>(value);
		signal.throwIfAborted();
		return result;
	} catch (error) {
		signal.throwIfAborted();
		throw error;
	}
}

export function closeIterator(iterator: CapturedAsyncIterator<unknown>): void {
	detachCleanup(() => iterator.close());
}

export function captureOwnedAsyncIterable<T>(source: unknown): Readonly<CapturedOwnedAsyncIterable<T>> {
	const iterator = captureAsyncIterator<T>(source);
	const lifetime = new AbortController();
	let claimed = false;
	let closed = false;
	const close = () => {
		if (closed) return;
		closed = true;
		lifetime.abort(OWNED_ITERATOR_CLOSED);
		closeIterator(iterator);
	};
	const iterable = Object.freeze({
		[Symbol.asyncIterator]() {
			if (claimed || closed) throw new LocalSpeechProtocolError();
			claimed = true;
			return {
				async next() {
					if (closed) return { done: true as const, value: undefined };
					try {
						const rawResult = await raceOperationWithAbort(() => iterator.next(), lifetime.signal);
						if (closed) return { done: true as const, value: undefined };
						const result = captureIteratorResult<T>(rawResult);
						if (closed) return { done: true as const, value: undefined };
						if (result.done) {
							close();
							return { done: true as const, value: undefined };
						}
						return { done: false as const, value: result.value };
					} catch (error) {
						if (closed) return { done: true as const, value: undefined };
						close();
						throw error;
					}
				},
				async return() {
					close();
					return { done: true as const, value: undefined };
				},
			};
		},
	});
	return Object.freeze({ iterable, close });
}

export function detachCleanup(operation: () => PromiseLike<unknown> | unknown): void {
	try {
		void Promise.resolve(operation()).catch(() => undefined);
	} catch {
		// Cleanup cannot replace the primary result.
	}
}
