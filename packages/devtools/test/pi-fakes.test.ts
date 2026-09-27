import { describe, expect, it } from "vitest";
import { assistant, messageResponse } from "../src/pi-fakes.js";

describe("Pi stream fakes", () => {
	it("keeps the final response route on every partial event", async () => {
		const message = {
			...assistant([{ type: "text" as const, text: "ready" }]),
			provider: "custom-provider",
			api: "openai-responses" as const,
			model: "custom-model",
			responseModel: "served-model",
			responseId: "final-response-id",
			rawStopReason: "final-provider-reason",
			endTurn: true,
		};
		const partialRoutes: unknown[] = [];
		const partials: unknown[] = [];

		for await (const event of messageResponse(message)) {
			if ("partial" in event) {
				partials.push(event.partial);
				partialRoutes.push({
					provider: event.partial.provider,
					api: event.partial.api,
					model: event.partial.model,
					responseModel: event.partial.responseModel,
				});
			}
		}

		expect(partialRoutes.length).toBeGreaterThan(0);
		expect(partialRoutes).toEqual(
			partialRoutes.map(() => ({
				provider: "custom-provider",
				api: "openai-responses",
				model: "custom-model",
				responseModel: "served-model",
			})),
		);
		for (const partial of partials) {
			expect(partial).not.toHaveProperty("responseId");
			expect(partial).not.toHaveProperty("rawStopReason");
			expect(partial).not.toHaveProperty("endTurn");
		}
	});
});
