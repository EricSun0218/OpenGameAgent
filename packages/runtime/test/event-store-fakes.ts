import { InMemoryEventStore } from "../src/index.js";

export class FailingRunCompletionStore extends InMemoryEventStore {
	blockedRunId: string | undefined;

	constructor(private readonly failureMessage: string) {
		super();
	}

	override append(...parameters: Parameters<InMemoryEventStore["append"]>) {
		if (parameters[2].runId === this.blockedRunId && parameters[3].type === "run.completed") {
			throw new Error(this.failureMessage);
		}
		return super.append(...parameters);
	}
}
