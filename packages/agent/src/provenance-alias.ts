import { createHash } from "node:crypto";

declare const gameProvenanceAliasBrand: unique symbol;

/** A validated deterministic alias used only for local provenance correlation. */
export type GameProvenanceAlias = string & { readonly [gameProvenanceAliasBrand]: true };

export type GameProvenanceAliasDomain =
	| "scope"
	| "stream"
	| "run"
	| "provider"
	| "api"
	| "model"
	| "source"
	| "reference"
	| "revision"
	| "tool"
	| "host-tool-owner"
	| "event"
	| "record";

/** Create a deterministic local correlation alias. This is pseudonymous, not anonymous. */
export function createGameProvenanceAlias(domain: GameProvenanceAliasDomain, value: string): GameProvenanceAlias {
	if (typeof value !== "string" || value.length === 0) {
		throw new TypeError("A provenance alias input must be a non-empty string.");
	}
	return `pv_${createHash("sha256").update(domain).update("\0").update(value).digest("hex")}` as GameProvenanceAlias;
}

export function isGameProvenanceAlias(value: unknown): value is GameProvenanceAlias {
	return typeof value === "string" && /^pv_[0-9a-f]{64}$/u.test(value);
}
