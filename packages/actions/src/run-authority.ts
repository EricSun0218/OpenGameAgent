import { RunAuthorityClosedError, type RunCoordinate } from "@opengameagent/protocol";

export interface SynchronousActionRunAuthority {
	assertRunAuthorityOpenSynchronously(coordinate: RunCoordinate): void;
}

/** Closure is the only error that permits retiring work; corrupt authority must fail the mutation. */
export function isActionRunAuthorityOpen(assertOpen: () => void): boolean {
	try {
		const result: unknown = assertOpen();
		if (result !== undefined) throw new TypeError("Action run authority checks must complete synchronously.");
		return true;
	} catch (error) {
		if (error instanceof RunAuthorityClosedError) return false;
		throw error;
	}
}
