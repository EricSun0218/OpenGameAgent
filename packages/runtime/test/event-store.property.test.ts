import fc from "fast-check";
import { describe, expect, it } from "vitest";
import { InMemoryEventStore } from "../src/index.js";

const pageScenario = fc.array(fc.constant(null)).chain((events) =>
	fc.integer({ min: 1, max: Math.max(1, events.length) }).map((pageSize) => ({
		eventCount: events.length,
		pageSize,
	})),
);

describe("event cursor properties", () => {
	it("reads every revision exactly once and finishes at the authority head", async () => {
		await fc.assert(
			fc.asyncProperty(pageScenario, async ({ eventCount, pageSize }) => {
				let nextEventId = 0;
				const store = new InMemoryEventStore({
					newEventId: () => `event-${++nextEventId}`,
					now: () => "2026-09-01T00:00:00.000Z",
				});
				const stream = { sessionId: "session", actorId: "actor" };
				let expectedRevision: "empty" | string = "empty";
				for (let index = 0; index < eventCount; index += 1) {
					const appended = await store.append(
						stream,
						expectedRevision,
						{ ...stream, runId: "run", turn: index.toString() },
						{ type: "turn.started" },
					);
					expectedRevision = appended.revision;
				}

				const revisions: string[] = [];
				let cursor = "0";
				let pages = 0;
				for (;;) {
					const page = await store.read(stream, { afterRevision: cursor, limit: pageSize });
					pages += 1;
					revisions.push(...page.events.map((event) => event.revision));
					cursor = page.cursor;
					if (!page.hasMore) break;
					if (pages > eventCount + 1) throw new Error("Cursor pagination did not make progress.");
				}

				expect({ revisions, cursor, pages }).toEqual({
					revisions: Array.from({ length: eventCount }, (_, index) => (index + 1).toString()),
					cursor: eventCount.toString(),
					pages: Math.max(1, Math.ceil(eventCount / pageSize)),
				});
			}),
		);
	});
});
