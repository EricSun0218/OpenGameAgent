import type { GameActionIntent, GameActionJournalEntry, GameActionReceipt } from "@opengameagent/protocol";
import type { GameActionJournal } from "./journal.js";

export interface GameActionExecutor {
	execute(intent: GameActionIntent, signal: AbortSignal): Promise<GameActionReceipt>;
}

export type GameActionDispatchResult =
	| { kind: "terminal"; entry: GameActionJournalEntry }
	| { kind: "reconcile"; entry: GameActionJournalEntry; blockingOperationId?: string };

export class DurableGameActionDispatcher {
	constructor(
		private readonly journal: GameActionJournal,
		private readonly executor: GameActionExecutor,
	) {}

	async dispatch(intent: GameActionIntent, signal?: AbortSignal): Promise<GameActionDispatchResult> {
		await this.journal.prepare(intent, signal);
		const claim = await this.journal.claimDispatch(intent.operationId, signal);
		if (claim.kind === "terminal") return { kind: "terminal", entry: claim.entry };
		if (claim.kind === "reconcile") return { kind: "reconcile", entry: claim.entry };
		if (claim.kind === "blocked") {
			return { kind: "reconcile", entry: claim.entry, blockingOperationId: claim.blockingOperationId };
		}

		const dispatchSignal = signal ?? new AbortController().signal;
		try {
			const receipt = await this.executor.execute(intent, dispatchSignal);
			const entry = await this.journal.submitReceipt(receipt);
			return { kind: "terminal", entry };
		} catch (error) {
			await this.journal.markUncertain(intent.operationId);
			throw error;
		}
	}

	reconcile(receipt: GameActionReceipt, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		return this.journal.submitReceipt(receipt, signal);
	}
}
