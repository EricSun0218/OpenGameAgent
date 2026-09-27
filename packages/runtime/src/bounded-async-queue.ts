import { Deque } from "@datastructures-js/deque";

/** A bounded FIFO handoff with one asynchronous consumer. Overflow closes admission but preserves the accepted prefix. */
export class BoundedAsyncQueue<T> {
	private readonly values = new Deque<T>();
	private waiter: ((value: T | undefined) => void) | undefined;
	private closed = false;
	private overflowed = false;

	constructor(private readonly capacity: number) {
		if (!Number.isSafeInteger(capacity) || Object.is(capacity, -0) || capacity <= 0) {
			throw new TypeError("Queue capacity must be a positive safe integer.");
		}
	}

	push(value: T): boolean {
		if (value === undefined) throw new TypeError("BoundedAsyncQueue does not admit undefined values.");
		if (this.closed) return false;
		if (this.waiter) {
			const waiter = this.waiter;
			this.waiter = undefined;
			waiter(value);
			return true;
		}
		if (this.values.size() >= this.capacity) {
			this.overflowed = true;
			this.close();
			return false;
		}
		this.values.pushBack(value);
		return true;
	}

	next(): Promise<T | undefined> {
		if (this.values.size() > 0) return Promise.resolve(this.values.popFront() as T);
		if (this.closed) return Promise.resolve(undefined);
		if (this.waiter) throw new Error("BoundedAsyncQueue supports one consumer.");
		return new Promise<T | undefined>((resolve) => {
			this.waiter = resolve;
		});
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		const waiter = this.waiter;
		this.waiter = undefined;
		waiter?.(undefined);
	}

	get didOverflow(): boolean {
		return this.overflowed;
	}
}
