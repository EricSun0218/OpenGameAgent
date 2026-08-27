export class AsyncQueue<T> implements AsyncIterable<T> {
	private readonly values: T[] = [];
	private readonly waiters: Array<(result: IteratorResult<T>) => void> = [];
	private closed = false;

	push(value: T): void {
		if (this.closed) return;
		const waiter = this.waiters.shift();
		if (waiter) waiter({ done: false, value });
		else this.values.push(value);
	}

	end(): void {
		if (this.closed) return;
		this.closed = true;
		for (const waiter of this.waiters.splice(0)) waiter({ done: true, value: undefined });
	}

	async *[Symbol.asyncIterator](): AsyncIterator<T> {
		while (true) {
			const value = this.values.shift();
			if (value !== undefined) {
				yield value;
				continue;
			}
			if (this.closed) return;
			const next = await new Promise<IteratorResult<T>>((resolve) => this.waiters.push(resolve));
			if (next.done) return;
			yield next.value;
		}
	}
}
