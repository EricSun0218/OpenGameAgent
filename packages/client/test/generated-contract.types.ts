import type { RuntimeEventReplay } from "../src/generated/types.gen.js";

type ReplayPage = Extract<RuntimeEventReplay, { kind: "page" }>;
type ReplayEvent = ReplayPage["events"][number];
type CompletedItem = Extract<ReplayEvent["data"], { type: "item.completed" }>["item"];
type InternalContextMessage = Extract<CompletedItem, { context: object }>;
type InternalContextContent = InternalContextMessage["content"];

const textOnlyInternalContext: InternalContextContent = [{ type: "text", text: "{}" }];

const validInternalContext: InternalContextContent = [
	{ type: "text", text: "{}" },
	{
		type: "image-reference",
		attachmentId: `img_${"a".repeat(64)}`,
		mediaType: "image/png",
		bytes: 1,
		width: 1,
		height: 1,
	},
];

const twoImageInternalContext: InternalContextContent = [
	{ type: "text", text: "{}" },
	{
		type: "image-reference",
		attachmentId: `img_${"a".repeat(64)}`,
		mediaType: "image/png",
		bytes: 1,
		width: 1,
		height: 1,
	},
	{
		type: "image-reference",
		attachmentId: `img_${"b".repeat(64)}`,
		mediaType: "image/png",
		bytes: 2,
		width: 2,
		height: 2,
	},
];

const namedInternalContext: InternalContextContent = [
	{ type: "text", text: "{}" },
	{
		type: "image-reference",
		attachmentId: `img_${"a".repeat(64)}`,
		mediaType: "image/png",
		bytes: 1,
		width: 1,
		height: 1,
		// @ts-expect-error Internal context references are deliberately name-free.
		name: "display.png",
	},
];

void textOnlyInternalContext;
void validInternalContext;
void twoImageInternalContext;
void namedInternalContext;
