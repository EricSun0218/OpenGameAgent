import { describe, expect, it } from "vitest";
import { normalizeDenseVector } from "../src/dense-normalization.js";
import { BgeM3ModelOutputError, classifyBgeM3RequestFailure } from "../src/embedding-failure.js";

describe("normalizeDenseVector", () => {
	it("matches epsilon normalization for finite zero and non-zero vectors", () => {
		expect(Array.from(normalizeDenseVector(new Float32Array([0, 0, 0])))).toEqual([0, 0, 0]);
		const normalized = normalizeDenseVector(new Float32Array([3, 4]));
		expect(normalized[0]).toBeCloseTo(0.6);
		expect(normalized[1]).toBeCloseTo(0.8);
	});

	it("rejects non-finite model output", () => {
		expect(() => normalizeDenseVector(new Float32Array([Number.NaN]))).toThrow("invalid_embedding");
	});

	it("keeps model-output failures distinct from worker infrastructure failures", () => {
		expect(classifyBgeM3RequestFailure(new BgeM3ModelOutputError())).toBe("invalid_embedding");
		expect(classifyBgeM3RequestFailure(new Error("runtime"))).toBe("worker_failure");
	});
});
