import { createHash } from "node:crypto";
import type { GameActionIdentity, JsonValue } from "@opengameagent/protocol";

function canonicalize(value: JsonValue): string {
	if (value === null || typeof value !== "object") return JSON.stringify(value);
	if (Array.isArray(value)) return `[${value.map(canonicalize).join(",")}]`;
	return `{${Object.keys(value)
		.sort()
		.map((key) => `${JSON.stringify(key)}:${canonicalize(value[key] ?? null)}`)
		.join(",")}}`;
}

export function createGameActionOperationId(identity: GameActionIdentity): string {
	if (!Number.isInteger(identity.turn) || identity.turn < 1) throw new RangeError("turn must be a positive integer.");
	if (!Number.isInteger(identity.toolCallIndex) || identity.toolCallIndex < 0) {
		throw new RangeError("toolCallIndex must be a non-negative integer.");
	}
	const canonical = canonicalize({
		version: 2,
		worldId: identity.session.worldId,
		saveId: identity.session.saveId,
		timelineId: identity.session.timelineId,
		generation: identity.session.generation,
		ownerId: identity.session.ownerId,
		sessionId: identity.session.sessionId,
		actorId: identity.session.actorId,
		inputId: identity.inputId,
		runId: identity.runId,
		turn: identity.turn,
		toolCallIndex: identity.toolCallIndex,
		action: identity.action,
	});
	return `oga2_${createHash("sha256").update(canonical).digest("base64url")}`;
}
