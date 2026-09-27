import { BgeM3ModelOutputError } from "./embedding-failure.js";

const NORMALIZATION_EPSILON = 1e-12;

export function normalizeDenseVector(source: Float32Array): Float32Array {
	let squaredMagnitude = 0;
	for (const value of source) {
		if (!Number.isFinite(value)) throw new BgeM3ModelOutputError();
		squaredMagnitude += value * value;
	}
	const magnitude = Math.sqrt(squaredMagnitude);
	if (!Number.isFinite(magnitude)) throw new BgeM3ModelOutputError();
	const denominator = Math.max(magnitude, NORMALIZATION_EPSILON);
	const vector = new Float32Array(source.length);
	for (let index = 0; index < source.length; index += 1) vector[index] = (source[index] as number) / denominator;
	return vector;
}
