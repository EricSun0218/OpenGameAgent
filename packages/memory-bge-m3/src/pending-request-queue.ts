import Queue from "yocto-queue";

export class PendingRequestQueue<Request extends object> {
	private requests = new Queue<Request>();

	get size(): number {
		return this.requests.size;
	}

	enqueue(request: Request): void {
		this.requests.enqueue(request);
	}

	dequeue(): Request | undefined {
		return this.requests.dequeue();
	}

	drain(): IterableIterator<Request> {
		return this.requests.drain();
	}

	remove(request: Request): boolean {
		const retained = new Queue<Request>();
		let removed = false;
		for (const queued of this.requests.drain()) {
			if (!removed && queued === request) {
				removed = true;
				continue;
			}
			retained.enqueue(queued);
		}
		this.requests = retained;
		return removed;
	}
}
