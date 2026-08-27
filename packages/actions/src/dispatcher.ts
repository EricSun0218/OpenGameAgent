import type { GameActionIntent, GameActionJournalEntry, GameActionReceipt } from "@opengameagent/protocol";
import type { GameActionJournal } from "./journal.js";

export interface GameActionExecutor {
	execute(intent: GameActionIntent, signal: AbortSignal): Promise<GameActionReceipt>;
}

export type GameActionDispatchResult =
	| { kind: "terminal"; entry: GameActionJournalEntry }
	| { kind: "reconcile"; entry: GameActionJournalEntry; blockingOperationId?: string };

export type GameActionDispatchDisposition =
	| "executed"
	| "duplicate-prevented"
	| "reconcile-required"
	| "conflict-blocked"
	| "uncertain"
	| "reconciled"
	| "failed-before-dispatch";

export interface GameActionDispatchObservation {
	schemaVersion: 1;
	session: GameActionIntent["session"];
	inputId: string;
	runId: string;
	turn: number;
	operationId: string;
	action: string;
	startedAt: number;
	durationMilliseconds: number;
	frameworkMilliseconds: number;
	hostMilliseconds: number;
	disposition: GameActionDispatchDisposition;
	terminalStatus?: GameActionReceipt["status"];
	blockingOperationId?: string;
	errorCategory?: string;
}

export interface GameActionObserver {
	observeAction(observation: GameActionDispatchObservation): void;
}

export interface DurableGameActionDispatcherOptions {
	observer?: GameActionObserver;
}

export class DurableGameActionDispatcher {
	constructor(
		private readonly journal: GameActionJournal,
		private readonly executor: GameActionExecutor,
		private readonly options: DurableGameActionDispatcherOptions = {},
	) {}

	async dispatch(intent: GameActionIntent, signal?: AbortSignal): Promise<GameActionDispatchResult> {
		const startedAt = Date.now();
		const monotonicStartedAt = performance.now();
		let hostMilliseconds = 0;
		let hostStartedAt: number | undefined;
		let claimedForDispatch = false;
		const dispatchSignal = signal ?? new AbortController().signal;
		try {
			await this.journal.prepare(intent, signal);
			const claim = await this.journal.claimDispatch(intent.operationId, signal);
			if (claim.kind === "terminal") {
				this.observe(intent, startedAt, monotonicStartedAt, hostMilliseconds, {
					disposition: "duplicate-prevented",
					...(claim.entry.receipt === undefined ? {} : { terminalStatus: claim.entry.receipt.status }),
				});
				return { kind: "terminal", entry: claim.entry };
			}
			if (claim.kind === "reconcile") {
				this.observe(intent, startedAt, monotonicStartedAt, hostMilliseconds, {
					disposition: "reconcile-required",
				});
				return { kind: "reconcile", entry: claim.entry };
			}
			if (claim.kind === "blocked") {
				this.observe(intent, startedAt, monotonicStartedAt, hostMilliseconds, {
					disposition: "conflict-blocked",
					blockingOperationId: claim.blockingOperationId,
				});
				return { kind: "reconcile", entry: claim.entry, blockingOperationId: claim.blockingOperationId };
			}

			claimedForDispatch = true;
			hostStartedAt = performance.now();
			const receipt = await this.executor.execute(intent, dispatchSignal);
			hostMilliseconds = performance.now() - hostStartedAt;
			const entry = await this.journal.submitReceipt(receipt);
			this.observe(intent, startedAt, monotonicStartedAt, hostMilliseconds, {
				disposition: "executed",
				terminalStatus: receipt.status,
			});
			return { kind: "terminal", entry };
		} catch (error) {
			if (hostStartedAt !== undefined && hostMilliseconds === 0) hostMilliseconds = performance.now() - hostStartedAt;
			if (claimedForDispatch) {
				try {
					await this.journal.markUncertain(intent.operationId);
				} catch (journalError) {
					this.observe(intent, startedAt, monotonicStartedAt, hostMilliseconds, {
						disposition: "uncertain",
						errorCategory: safeErrorCategory(journalError),
					});
					throw journalError;
				}
				this.observe(intent, startedAt, monotonicStartedAt, hostMilliseconds, {
					disposition: "uncertain",
					errorCategory: safeErrorCategory(error),
				});
			} else {
				this.observe(intent, startedAt, monotonicStartedAt, hostMilliseconds, {
					disposition: "failed-before-dispatch",
					errorCategory: safeErrorCategory(error),
				});
			}
			throw error;
		}
	}

	async reconcile(receipt: GameActionReceipt, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		const startedAt = Date.now();
		const monotonicStartedAt = performance.now();
		const entry = await this.journal.submitReceipt(receipt, signal);
		this.observe(entry.intent, startedAt, monotonicStartedAt, 0, {
			disposition: "reconciled",
			terminalStatus: receipt.status,
		});
		return entry;
	}

	private observe(
		intent: GameActionIntent,
		startedAt: number,
		monotonicStartedAt: number,
		hostMilliseconds: number,
		result: Pick<GameActionDispatchObservation, "disposition"> &
			Partial<Pick<GameActionDispatchObservation, "terminalStatus" | "blockingOperationId" | "errorCategory">>,
	): void {
		const durationMilliseconds = performance.now() - monotonicStartedAt;
		try {
			this.options.observer?.observeAction({
				schemaVersion: 1,
				session: structuredClone(intent.session),
				inputId: intent.inputId,
				runId: intent.runId,
				turn: intent.turn,
				operationId: intent.operationId,
				action: intent.action,
				startedAt,
				durationMilliseconds,
				frameworkMilliseconds: Math.max(0, durationMilliseconds - hostMilliseconds),
				hostMilliseconds,
				...result,
			});
		} catch {
			// Observation is intentionally isolated from durable dispatch semantics.
		}
	}
}

function safeErrorCategory(error: unknown): string {
	if (error instanceof DOMException && error.name === "AbortError") return "aborted";
	if (error instanceof Error && /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/u.test(error.name)) return error.name;
	return "unknown";
}
