import type { GameAgentEvent, GameSessionKey } from "./runtime.js";

export interface GameEventViewer {
	principalId?: string;
	internal?: boolean;
	recipientIds?: ReadonlySet<string>;
}

function mayView(event: GameAgentEvent, session: GameSessionKey, viewer: GameEventViewer): boolean {
	switch (event.audience.visibility) {
		case "internal":
			return viewer.internal === true;
		case "owner":
			return viewer.internal === true || viewer.principalId === session.ownerId;
		case "public":
			return true;
		case "recipient":
			return viewer.internal === true || viewer.recipientIds?.has(event.audience.recipientId) === true;
	}
}

export function projectGameAgentEvent(
	event: GameAgentEvent,
	session: GameSessionKey,
	viewer: GameEventViewer,
): GameAgentEvent | undefined {
	if (!mayView(event, session, viewer)) return undefined;
	if (viewer.internal === true) return structuredClone(event);
	switch (event.type) {
		case "tool.started":
			return { ...event, call: { ...event.call, arguments: {} } };
		case "tool.progress":
			return { ...event, update: null };
		case "tool.completed":
			return {
				...event,
				result: { content: [], isError: event.result.isError === true },
			};
		default:
			return structuredClone(event);
	}
}
