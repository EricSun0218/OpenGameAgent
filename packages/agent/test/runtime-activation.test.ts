import { describe, expect, it } from "vitest";
import { TEST_MODEL, textResponse } from "../../devtools/src/pi-fakes.js";
import { createRuntimeGameAgentActivation } from "../src/runtime-activation.js";

describe("runtime-owned GameAgent activation", () => {
	it("reports the one release that detaches its runtime listeners", () => {
		const activation = createRuntimeGameAgentActivation({
			sessionId: "session",
			actorId: "actor",
			model: TEST_MODEL,
			modelStream: () => textResponse("unused"),
			emitEvent: async () => {
				throw new Error("No event should be emitted by an idle activation.");
			},
		});

		expect(activation.release()).toBe(true);
		expect(activation.release()).toBe(false);
	});
});
