import { assertOpaqueIdentifier, type CanonicalEvent, type StreamKey } from "@opengameagent/protocol";

export interface RuntimePrincipal {
	principalId: string;
}

export interface AuthorizedRuntimeStream {
	stream: StreamKey;
	isOwner: boolean;
	recipientIds: readonly string[];
	allowInternal: boolean;
	allowActionExchange?: boolean;
	allowApprovals?: boolean;
}

export function validateAuthorizedStream(access: AuthorizedRuntimeStream): void {
	if (
		!access?.stream ||
		!Array.isArray(access.recipientIds) ||
		typeof access.isOwner !== "boolean" ||
		typeof access.allowInternal !== "boolean" ||
		(access.allowActionExchange !== undefined && typeof access.allowActionExchange !== "boolean") ||
		(access.allowApprovals !== undefined && typeof access.allowApprovals !== "boolean")
	) {
		throw new TypeError("Authorized Runtime access is invalid.");
	}
	assertOpaqueIdentifier(access.stream.sessionId);
	assertOpaqueIdentifier(access.stream.actorId);
	const seen = new Set<string>();
	for (const recipientId of access.recipientIds) {
		assertOpaqueIdentifier(recipientId);
		if (seen.has(recipientId)) throw new TypeError("Authorized Runtime recipients must be unique.");
		seen.add(recipientId);
	}
}

export function projectEventForAudience(
	event: Readonly<CanonicalEvent>,
	access: Readonly<AuthorizedRuntimeStream>,
): CanonicalEvent | undefined {
	switch (event.audience.visibility) {
		case "public":
			return structuredClone(event);
		case "owner":
			return access.isOwner ? structuredClone(event) : undefined;
		case "internal":
			return access.allowInternal ? structuredClone(event) : undefined;
		case "recipient": {
			const allowed = new Set(access.recipientIds);
			const visibleRecipients = event.audience.recipientIds.filter((recipientId) => allowed.has(recipientId));
			if (visibleRecipients.length === 0) return undefined;
			const audience = { visibility: "recipient" as const, recipientIds: visibleRecipients };
			const projected: CanonicalEvent = {
				...structuredClone(event),
				audience,
			};
			switch (projected.data.type) {
				case "item.started":
				case "item.completed":
					projected.data.item.audience = audience;
					break;
				case "item.delta":
				case "item.updated":
					projected.data.audience = audience;
					break;
			}
			return projected;
		}
	}
}
