import assert from "node:assert/strict";
import { Buffer } from "node:buffer";
import { TEST_MODEL, textResponse } from "@opengameagent/devtools/pi-fakes";
import { GameRuntime, InMemoryEventStore } from "@opengameagent/runtime";

const stream = { sessionId: "local-demo", actorId: "gatekeeper" };
const payload = { instruction: "Greet the arriving player." };
const reply = "Welcome, traveler. The village gates are open.";

// These budgets cover this single-input example; choose explicit host budgets for a real game.
const runtime = new GameRuntime(new InMemoryEventStore(), {
	maxActiveActors: 1,
	maxQueuedRuns: 1,
	maxInputBytes: Buffer.byteLength(JSON.stringify(payload)),
	maxInputDepth: 2,
});

try {
	await runtime.registerActor({
		...stream,
		model: TEST_MODEL,
		modelStream: () => textResponse(reply),
	});
	const run = await runtime.submit({ ...stream, inputId: "arrival-1", payload });
	const result = await run.completion;
	assert.equal(result.status, "completed");
	assert.equal(result.finalOutput, reply);
	console.log(result.finalOutput);
} finally {
	runtime.stopAdmission();
	await runtime.waitForIdle();
}
