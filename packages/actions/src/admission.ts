import { type ActionApprovalConsumeInput, type ActionApprovalRecord, assertActionApprovalRecord } from "./approvals.js";
import type { ActionAuthority, ActionRecord, PreparedActionIntent } from "./types.js";

export interface ApprovedActionAdmissionInput
	extends Omit<ActionApprovalConsumeInput, "consumedAt" | "currentAuthority"> {
	readonly currentAuthority: Readonly<ActionAuthority>;
	readonly intent: Readonly<PreparedActionIntent>;
}

export interface ApprovedActionAdmissionResult {
	approval: ActionApprovalRecord;
	action?: ActionRecord;
}

/** Atomically consumes a one-time approval and admits its exact content-bound action. */
export interface ApprovedActionAdmission {
	admit(input: ApprovedActionAdmissionInput): Promise<ApprovedActionAdmissionResult>;
}

/** Return the approved revision required by admission, including after an idempotent consumed result is observed. */
export function approvalRevisionForAdmission(approval: ActionApprovalRecord): string {
	assertActionApprovalRecord(approval);
	if (approval.status === "approved") return approval.revision;
	if (approval.status !== "consumed") throw new TypeError("The action approval is not admissible.");
	const consumedRevision = BigInt(approval.revision);
	if (consumedRevision <= 1n) throw new TypeError("The consumed approval revision has no approved predecessor.");
	return (consumedRevision - 1n).toString();
}
