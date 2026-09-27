import type { RankedMemoryCandidate } from "./types.js";

// The original Reciprocal Rank Fusion evaluation fixed this constant during its pilot study.
export const RECIPROCAL_RANK_FUSION_CONSTANT = 60;

export function fuseMemoryRanks(
	rankings: readonly (readonly RankedMemoryCandidate[])[],
): readonly RankedMemoryCandidate[] {
	const scores = new Map<bigint, number>();
	for (const ranking of rankings) {
		for (const [index, candidate] of ranking.entries()) {
			const contribution = 1 / (RECIPROCAL_RANK_FUSION_CONSTANT + index + 1);
			scores.set(candidate.memoryKey, (scores.get(candidate.memoryKey) ?? 0) + contribution);
		}
	}
	return [...scores]
		.map(([memoryKey, score]) => ({ memoryKey, score }))
		.sort((left, right) => right.score - left.score || (left.memoryKey < right.memoryKey ? -1 : 1));
}
