import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { GameRequestContribution, GameRequestSource, GameToolResultTransformer } from "@opengameagent/agent";
import { canonicalizeJsonValue } from "@opengameagent/protocol";
import { Type } from "typebox";
import { waitForAbort } from "./abort.js";
import { ArtifactReadError, ArtifactValidationError } from "./artifact-errors.js";
import type {
	ArtifactLimits,
	ArtifactPage,
	ArtifactRecord,
	ArtifactServiceOptions,
	ArtifactToolDetails,
	ArtifactWriteRequest,
} from "./artifact-types.js";
import {
	assertArtifactLimitsEqual,
	byteLength,
	createArtifactRecord,
	digest,
	freezeArtifactLimits,
	requiredArtifactReadToolResultBytes,
	sameArtifactBinding,
	validateArtifactBinding,
	validateArtifactIdentifier,
	validateArtifactRecord,
} from "./artifact-validation.js";

const TOOL_RESULT_CONTENT_TYPE = "text/plain";

export class GameArtifactService implements GameRequestSource {
	readonly provenanceKind = "artifact" as const;
	readonly limits: Readonly<ArtifactLimits>;
	readonly readToolName: string;
	readonly requiredToolResultBytes: number;
	readonly sourceId: string;
	readonly transformToolResultTextBytes: number;
	private readonly now: () => string;
	private readonly parameters: ReturnType<typeof artifactReadParameters>;
	private readonly store: ArtifactServiceOptions["store"];

	constructor(options: ArtifactServiceOptions) {
		this.limits = freezeArtifactLimits(options.limits);
		if (
			typeof options.store !== "object" ||
			options.store === null ||
			typeof options.store.put !== "function" ||
			typeof options.store.read !== "function" ||
			typeof options.store.purge !== "function"
		) {
			throw new ArtifactValidationError("store must implement the artifact store contract.");
		}
		assertArtifactLimitsEqual(this.limits, options.store.limits);
		if (typeof options.now !== "function") throw new ArtifactValidationError("now must be a canonical clock function.");
		this.store = options.store;
		this.now = options.now;
		this.requiredToolResultBytes = requiredArtifactReadToolResultBytes(this.limits);
		this.transformToolResultTextBytes = this.limits.maxArtifactBytes;
		this.sourceId = validateArtifactIdentifier(options.sourceId ?? "artifacts", this.limits, "sourceId");
		this.readToolName = validateArtifactIdentifier(
			options.readToolName ?? "read_game_artifact",
			this.limits,
			"readToolName",
		);
		this.parameters = artifactReadParameters(this.limits);
		const maximumContentType = `a/${"b".repeat(this.limits.maxContentTypeBytes - 2)}`;
		this.assertModelResultFits(notFoundText());
		this.assertModelResultFits(unavailableText("artifact_storage_failed"));
		this.assertModelResultFits(
			referenceText({
				artifactId: "0".repeat(64),
				contentType: maximumContentType,
				byteLength: this.limits.maxArtifactBytes,
				readToolName: this.readToolName,
			}),
		);
		this.assertModelResultFits(
			canonicalizeJsonValue({
				status: "ok",
				artifactId: "0".repeat(64),
				contentType: maximumContentType,
				offsetBytes: this.limits.maxArtifactBytes,
				content: "🙂",
				contentBytes: 4,
				totalBytes: this.limits.maxArtifactBytes,
				nextOffsetBytes: this.limits.maxArtifactBytes,
			}),
		);
	}

	collect(
		input: Parameters<GameRequestSource["collect"]>[0],
		signal: Parameters<GameRequestSource["collect"]>[1],
		coordinate: Parameters<GameRequestSource["collect"]>[2],
	): Readonly<GameRequestContribution> {
		signal.throwIfAborted();
		validateArtifactBinding(input, coordinate, this.limits);
		const transformToolResult: GameToolResultTransformer = async (result, context, executionSignal) => {
			if (context.toolName === this.readToolName) return undefined;
			return await this.projectToolResult(
				result,
				{
					input: context.input,
					coordinate: context.coordinate,
					toolCallId: context.toolCallId,
					toolName: context.toolName,
					purpose: "tool_result",
					requestFingerprint: digest({ parameters: context.arguments }),
				},
				executionSignal,
			);
		};
		return Object.freeze({
			tools: Object.freeze([this.createReadTool(input, signal)]),
			transformToolResult,
			requiredToolResultBytes: this.requiredToolResultBytes,
			transformToolResultTextBytes: this.transformToolResultTextBytes,
		});
	}

	async storeText(request: Readonly<ArtifactWriteRequest>, signal?: AbortSignal): Promise<ArtifactRecord> {
		signal?.throwIfAborted();
		const record = createArtifactRecord(request, this.now(), this.limits);
		const operation = Promise.resolve().then(() => this.store.put(record, signal));
		const result = signal ? await waitForAbort(operation, signal) : await operation;
		signal?.throwIfAborted();
		if (
			typeof result !== "object" ||
			result === null ||
			(result.status !== "created" && result.status !== "existing")
		) {
			throw new ArtifactValidationError("Artifact store returned an invalid put result.");
		}
		const stored = validateArtifactRecord(result.record, this.limits);
		if (!sameArtifactBinding(record, stored)) {
			throw new ArtifactValidationError("Artifact store returned a record with different bound content.");
		}
		return stored;
	}

	createReferenceText(recordValue: Readonly<ArtifactRecord>): string {
		const record = validateArtifactRecord(recordValue, this.limits);
		const text = referenceText({
			artifactId: record.artifactId,
			contentType: record.contentType,
			byteLength: record.byteLength,
			readToolName: this.readToolName,
		});
		this.assertModelResultFits(text);
		return text;
	}

	private async projectToolResult(
		result: Readonly<AgentToolResult<unknown>>,
		origin: Omit<ArtifactWriteRequest, "contentType" | "content">,
		signal: AbortSignal,
	): Promise<AgentToolResult<unknown>["content"] | undefined> {
		const text = allTextContent(result.content);
		if (text === undefined) return undefined;
		const totalBytes = byteLength(text);
		if (totalBytes <= this.limits.maxInlineToolResultBytes) return undefined;
		if (totalBytes > this.limits.maxArtifactBytes) {
			return this.replacementContent(unavailableText("artifact_too_large"));
		}

		let record: ArtifactRecord;
		try {
			record = await this.storeText({ ...origin, contentType: TOOL_RESULT_CONTENT_TYPE, content: text }, signal);
		} catch (error) {
			if (signal.aborted) throw signal.reason ?? error;
			return this.replacementContent(unavailableText("artifact_storage_failed"));
		}
		return this.replacementContent(this.createReferenceText(record));
	}

	private replacementContent(text: string): AgentToolResult<unknown>["content"] {
		this.assertModelResultFits(text);
		return [{ type: "text", text }];
	}

	private createReadTool(
		input: Parameters<GameRequestSource["collect"]>[0],
		requestSignal: AbortSignal,
	): AgentTool<typeof this.parameters, ArtifactToolDetails> {
		const stream = { sessionId: input.sessionId, actorId: input.actorId };
		return {
			name: this.readToolName,
			label: "Read game artifact",
			description:
				"Read one bounded UTF-8 byte page from an immutable artifact created for this exact game session and actor.",
			parameters: this.parameters,
			executionMode: "parallel",
			execute: async (toolCallId, parameters, executionSignal) => {
				const signal = executionSignal ?? requestSignal;
				signal.throwIfAborted();
				validateArtifactIdentifier(toolCallId, this.limits, "toolCallId");
				const request = {
					artifactId: parameters.artifactId,
					offsetBytes: parameters.offsetBytes ?? 0,
					maxBytes: parameters.maxBytes ?? this.limits.maxReadBytes,
				};
				let record: ArtifactRecord | undefined;
				try {
					record = await waitForAbort(
						Promise.resolve().then(() => this.store.read(stream, request.artifactId, signal)),
						signal,
					);
					if (record) record = validateArtifactRecord(record, this.limits);
				} catch (error) {
					if (signal.aborted) throw signal.reason ?? error;
					throw new ArtifactReadError({ cause: error });
				}
				if (!record || record.sessionId !== stream.sessionId || record.actorId !== stream.actorId) {
					const text = notFoundText();
					this.assertModelResultFits(text);
					return {
						content: [{ type: "text", text }],
						details: { artifactStatus: "not_found" },
					};
				}
				const page = fitArtifactPage(record, request.offsetBytes, request.maxBytes, this.limits.maxModelResultBytes);
				const text = canonicalizeJsonValue({ status: "ok", ...page });
				this.assertModelResultFits(text);
				return {
					content: [{ type: "text", text }],
					details: { artifactId: record.artifactId, artifactStatus: "read" },
				};
			},
		};
	}

	private assertModelResultFits(text: string): void {
		if (byteLength(text) > this.limits.maxModelResultBytes) {
			throw new ArtifactValidationError("The model-visible artifact result exceeds its configured byte limit.");
		}
	}
}

function artifactReadParameters(limits: Readonly<ArtifactLimits>) {
	return Type.Object(
		{
			artifactId: Type.String({ pattern: "^[0-9a-f]{64}$" }),
			offsetBytes: Type.Optional(Type.Integer({ minimum: 0, maximum: limits.maxArtifactBytes })),
			maxBytes: Type.Optional(Type.Integer({ minimum: 4, maximum: limits.maxReadBytes })),
		},
		{ additionalProperties: false },
	);
}

function fitArtifactPage(
	record: Readonly<ArtifactRecord>,
	offsetBytes: number,
	requestedBytes: number,
	maxResultBytes: number,
): ArtifactPage {
	const bytes = Buffer.from(record.content, "utf8");
	if (offsetBytes > bytes.length || (offsetBytes < bytes.length && isContinuation(bytes[offsetBytes]))) {
		throw new ArtifactValidationError("offsetBytes must be a UTF-8 boundary within the artifact.");
	}
	if (offsetBytes === bytes.length) {
		return {
			artifactId: record.artifactId,
			contentType: record.contentType,
			offsetBytes,
			content: "",
			contentBytes: 0,
			totalBytes: record.byteLength,
		};
	}

	let firstEnd = offsetBytes + 1;
	while (firstEnd < bytes.length && isContinuation(bytes[firstEnd])) firstEnd += 1;
	let low = firstEnd - offsetBytes;
	let high = Math.min(requestedBytes, bytes.length - offsetBytes);
	if (low > high) {
		throw new ArtifactValidationError("maxBytes cannot represent the next UTF-8 code point.");
	}
	let best: ArtifactPage | undefined;
	while (low <= high) {
		const budget = Math.floor((low + high) / 2);
		const page = pageAtBudget(record, bytes, offsetBytes, budget);
		const text = canonicalizeJsonValue({ status: "ok", ...page });
		if (page.contentBytes > 0 && byteLength(text) <= maxResultBytes) {
			best = page;
			low = budget + 1;
		} else {
			high = budget - 1;
		}
	}
	if (!best) {
		throw new ArtifactValidationError("maxModelResultBytes cannot represent one readable artifact code point.");
	}
	return best;
}

function pageAtBudget(
	record: Readonly<ArtifactRecord>,
	bytes: Buffer,
	offsetBytes: number,
	budget: number,
): ArtifactPage {
	let end = Math.min(bytes.length, offsetBytes + budget);
	while (end > offsetBytes && end < bytes.length && isContinuation(bytes[end])) end -= 1;
	const content = bytes.subarray(offsetBytes, end).toString("utf8");
	return {
		artifactId: record.artifactId,
		contentType: record.contentType,
		offsetBytes,
		content,
		contentBytes: end - offsetBytes,
		totalBytes: record.byteLength,
		...(end < bytes.length ? { nextOffsetBytes: end } : {}),
	};
}

function isContinuation(value: number | undefined): boolean {
	return value !== undefined && (value & 0xc0) === 0x80;
}

function allTextContent(value: Readonly<AgentToolResult<unknown>["content"]>): string | undefined {
	let text = "";
	for (const item of value) {
		if (item.type !== "text" || typeof item.text !== "string") return undefined;
		text += item.text;
	}
	return text;
}

function referenceText(value: {
	artifactId: string;
	contentType: string;
	byteLength: number;
	readToolName: string;
}): string {
	return canonicalizeJsonValue({
		status: "stored",
		artifactId: value.artifactId,
		contentType: value.contentType,
		totalBytes: value.byteLength,
		readTool: value.readToolName,
	});
}

function unavailableText(reason: "artifact_storage_failed" | "artifact_too_large"): string {
	return canonicalizeJsonValue({ status: "output_unavailable", reason, doNotRetry: true });
}

function notFoundText(): string {
	return canonicalizeJsonValue({ status: "not_found" });
}
