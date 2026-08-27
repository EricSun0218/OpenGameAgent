import type { GameActionIntent, GameActionJournalEntry, GameActionReceipt } from "@opengameagent/protocol";

export type GameActionClaim =
	| { kind: "dispatch"; entry: GameActionJournalEntry }
	| { kind: "reconcile"; entry: GameActionJournalEntry }
	| { kind: "blocked"; entry: GameActionJournalEntry; blockingOperationId: string }
	| { kind: "terminal"; entry: GameActionJournalEntry };

export interface GameActionJournal {
	prepare(intent: GameActionIntent, signal?: AbortSignal): Promise<GameActionJournalEntry>;
	claimDispatch(operationId: string, signal?: AbortSignal): Promise<GameActionClaim>;
	markUncertain(operationId: string, signal?: AbortSignal): Promise<GameActionJournalEntry>;
	submitReceipt(receipt: GameActionReceipt, signal?: AbortSignal): Promise<GameActionJournalEntry>;
	read(operationId: string, signal?: AbortSignal): Promise<GameActionJournalEntry | undefined>;
}

function sameIntent(left: GameActionIntent, right: GameActionIntent): boolean {
	return JSON.stringify(left) === JSON.stringify(right);
}

function sameSession(left: GameActionIntent["session"], right: GameActionIntent["session"]): boolean {
	return (
		left.worldId === right.worldId &&
		left.saveId === right.saveId &&
		left.timelineId === right.timelineId &&
		left.generation === right.generation &&
		left.ownerId === right.ownerId &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId
	);
}

function clone<T>(value: T): T {
	return structuredClone(value);
}

export class InMemoryGameActionJournal implements GameActionJournal {
	private readonly entries = new Map<string, GameActionJournalEntry>();
	private gate: Promise<void> = Promise.resolve();

	async prepare(intent: GameActionIntent, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		return this.exclusive(() => {
			signal?.throwIfAborted();
			const current = this.entries.get(intent.operationId);
			if (current) {
				if (!sameIntent(current.intent, intent))
					throw new Error("Operation ID already identifies a different action intent.");
				return clone(current);
			}
			const entry: GameActionJournalEntry = {
				intent: clone(intent),
				status: "prepared",
				attempt: 0,
				preparedAt: Date.now(),
			};
			this.entries.set(intent.operationId, entry);
			return clone(entry);
		});
	}

	async claimDispatch(operationId: string, signal?: AbortSignal): Promise<GameActionClaim> {
		return this.exclusive(() => {
			signal?.throwIfAborted();
			const entry = this.require(operationId);
			if (entry.status === "dispatched" || entry.status === "uncertain")
				return { kind: "reconcile", entry: clone(entry) };
			if (entry.status !== "prepared") return { kind: "terminal", entry: clone(entry) };
			const conflictKey = entry.intent.conflictKey;
			if (conflictKey) {
				const blocker = [...this.entries.values()]
					.filter(
						(candidate) =>
							candidate.intent.operationId !== operationId &&
							candidate.intent.conflictKey === conflictKey &&
							candidate.intent.session.worldId === entry.intent.session.worldId &&
							candidate.intent.session.saveId === entry.intent.session.saveId &&
							candidate.intent.session.timelineId === entry.intent.session.timelineId &&
							candidate.intent.session.generation === entry.intent.session.generation &&
							(candidate.status === "dispatched" || candidate.status === "uncertain"),
					)
					.sort(
						(left, right) =>
							left.preparedAt - right.preparedAt || left.intent.operationId.localeCompare(right.intent.operationId),
					)[0];
				if (blocker) return { kind: "blocked", entry: clone(entry), blockingOperationId: blocker.intent.operationId };
			}
			entry.status = "dispatched";
			entry.attempt += 1;
			entry.dispatchedAt = Date.now();
			return { kind: "dispatch", entry: clone(entry) };
		});
	}

	async markUncertain(operationId: string, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		return this.exclusive(() => {
			signal?.throwIfAborted();
			const entry = this.require(operationId);
			if (entry.status === "dispatched") entry.status = "uncertain";
			else if (entry.status !== "uncertain") throw new Error("Only a dispatched action can become uncertain.");
			return clone(entry);
		});
	}

	async submitReceipt(receipt: GameActionReceipt, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		return this.exclusive(() => {
			signal?.throwIfAborted();
			const entry = this.require(receipt.operationId);
			if (!sameSession(entry.intent.session, receipt.session))
				throw new Error("Action receipt session does not match its intent.");
			if (entry.intent.action !== receipt.action) throw new Error("Action receipt name does not match its intent.");
			if (entry.intent.expectedRevision !== receipt.expectedRevision) {
				throw new Error("Action receipt expected revision does not match its intent.");
			}
			if (entry.receipt) {
				if (JSON.stringify(entry.receipt) !== JSON.stringify(receipt))
					throw new Error("Conflicting receipt for a terminal action.");
				return clone(entry);
			}
			if (entry.status !== "dispatched" && entry.status !== "uncertain") {
				throw new Error("A receipt is only valid after dispatch.");
			}
			entry.status = receipt.status;
			entry.receipt = clone(receipt);
			return clone(entry);
		});
	}

	async read(operationId: string, signal?: AbortSignal): Promise<GameActionJournalEntry | undefined> {
		return this.exclusive(() => {
			signal?.throwIfAborted();
			const entry = this.entries.get(operationId);
			return entry ? clone(entry) : undefined;
		});
	}

	private require(operationId: string): GameActionJournalEntry {
		const entry = this.entries.get(operationId);
		if (!entry) throw new Error(`Unknown action operation '${operationId}'.`);
		return entry;
	}

	private async exclusive<T>(action: () => T): Promise<T> {
		const previous = this.gate;
		let release = () => {};
		this.gate = new Promise<void>((resolve) => {
			release = resolve;
		});
		await previous;
		try {
			return action();
		} finally {
			release();
		}
	}
}
