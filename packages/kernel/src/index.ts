export type {
	GameAgentEvent,
	GameAgentKernelPort,
	GameControlResult,
	GameKernelRunRequest,
	GameRunCoordinate,
	GameSessionKey,
} from "@opengameagent/protocol";

export function sameGameSession(
	left: import("@opengameagent/protocol").GameSessionKey,
	right: import("@opengameagent/protocol").GameSessionKey,
): boolean {
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

export * from "./conversation-store.js";
