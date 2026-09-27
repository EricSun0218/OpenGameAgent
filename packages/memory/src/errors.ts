export class MemoryConflictError extends Error {
	constructor(public readonly memoryId: string) {
		super("A memory with this identity already has different authoritative content.");
		this.name = "MemoryConflictError";
	}
}

export class MemoryNotFoundError extends Error {
	constructor(public readonly memoryId: string) {
		super("The requested memory does not exist.");
		this.name = "MemoryNotFoundError";
	}
}

export class MemoryRevisionConflictError extends Error {
	constructor(public readonly memoryId: string) {
		super("The memory revision no longer matches the requested mutation.");
		this.name = "MemoryRevisionConflictError";
	}
}

export class MemoryRecallUnavailableError extends Error {
	constructor() {
		super("Vector recall failed and the caller required fail-closed behavior.");
		this.name = "MemoryRecallUnavailableError";
	}
}

export class MemoryRerankerError extends Error {
	constructor() {
		super("The memory reranker returned an invalid ordering.");
		this.name = "MemoryRerankerError";
	}
}
