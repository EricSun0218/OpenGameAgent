import type {
	GameConversationMessage,
	GameConversationSnapshot,
	GameConversationStore,
	GameSessionKey,
} from "@opengameagent/protocol";

function key(session: GameSessionKey): string {
	return JSON.stringify([
		session.worldId,
		session.saveId,
		session.timelineId,
		session.generation,
		session.ownerId,
		session.sessionId,
		session.actorId,
	]);
}

export class InMemoryGameConversationStore implements GameConversationStore {
	private readonly snapshots = new Map<string, GameConversationSnapshot>();

	async read(session: GameSessionKey, signal?: AbortSignal): Promise<GameConversationSnapshot> {
		signal?.throwIfAborted();
		return structuredClone(this.snapshots.get(key(session)) ?? { revision: 0, messages: [] });
	}

	async save(
		session: GameSessionKey,
		expectedRevision: number,
		messages: readonly GameConversationMessage[],
		signal?: AbortSignal,
	): Promise<GameConversationSnapshot> {
		signal?.throwIfAborted();
		const storageKey = key(session);
		const current = this.snapshots.get(storageKey) ?? { revision: 0, messages: [] };
		if (current.revision !== expectedRevision) throw new Error("Conversation revision conflict.");
		const snapshot: GameConversationSnapshot = { revision: current.revision + 1, messages: structuredClone(messages) };
		this.snapshots.set(storageKey, snapshot);
		return structuredClone(snapshot);
	}
}
