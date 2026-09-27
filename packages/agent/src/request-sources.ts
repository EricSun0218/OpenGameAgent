import { assertOpaqueIdentifier } from "@opengameagent/protocol";
import { captureGameContextContribution } from "./context-snapshot.js";
import type { GameContextSource, GameRequestContribution, GameRequestSource } from "./types.js";
import { modelContextSourceKind } from "./model-context-provenance.js";

export class InvalidRequestSourceError extends TypeError {
	constructor(message: string, options?: ErrorOptions) {
		super(message, options);
		this.name = "InvalidRequestSourceError";
	}
}

export function captureRequestSourceArray(
	sources: readonly GameRequestSource[] | undefined,
	maximum: number | undefined,
): readonly GameRequestSource[] {
	if (sources === undefined) return Object.freeze([]);
	if (!Array.isArray(sources)) {
		throw new InvalidRequestSourceError("Request sources must be an array.");
	}
	const length: unknown = sources.length;
	if (!Number.isSafeInteger(length) || (length as number) < 0) {
		throw new InvalidRequestSourceError("Request source array length must be a non-negative safe integer.");
	}
	if ((length as number) === 0) return Object.freeze([]);
	if (typeof maximum !== "number" || !Number.isSafeInteger(maximum) || maximum < 1) {
		throw new InvalidRequestSourceError("Request sources require an explicit positive source limit.");
	}
	if ((length as number) > maximum) {
		throw new InvalidRequestSourceError("The request source limit was exceeded.");
	}
	const captured: GameRequestSource[] = [];
	for (let index = 0; index < (length as number); index += 1) {
		captured.push(sources[index] as GameRequestSource);
	}
	return Object.freeze(captured);
}

export function validateRequestSources(
	sources: readonly GameRequestSource[] | undefined,
	contextSources: readonly GameContextSource[],
	maxSources: number | undefined,
): readonly GameRequestSource[] {
	const sourceSnapshot = captureRequestSourceArray(sources, maxSources);
	const sourceCount = sourceSnapshot.length;
	if (sourceCount === 0) return Object.freeze([]);

	const sourceIds = new Set(contextSources.map((source) => source.sourceId));
	const captured: GameRequestSource[] = [];
	for (const source of sourceSnapshot) {
		if (typeof source !== "object" || source === null || Array.isArray(source)) {
			throw new InvalidRequestSourceError("Every request source must be an object.");
		}
		const sourceId: unknown = source.sourceId;
		const collect: unknown = source.collect;
		if (typeof sourceId !== "string") throw new InvalidRequestSourceError("Every request source needs an identity.");
		assertOpaqueIdentifier(sourceId);
		if (sourceIds.has(sourceId)) {
			throw new InvalidRequestSourceError(`Request source identities must be unique: ${sourceId}`);
		}
		if (typeof collect !== "function") {
			throw new InvalidRequestSourceError(`Request source ${sourceId} must have a collect function.`);
		}
		sourceIds.add(sourceId);
		const boundCollect = collect.bind(source) as GameRequestSource["collect"];
		const capturedSource: GameRequestSource = Object.freeze({
			sourceId,
			provenanceKind: modelContextSourceKind(source.provenanceKind, "extension"),
			collect: (...args: Parameters<GameRequestSource["collect"]>) => boundCollect(...args),
		});
		captured.push(capturedSource);
	}
	return Object.freeze(captured);
}

export function captureRequestContribution(sourceId: string, value: unknown): Readonly<GameRequestContribution> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) {
		throw new InvalidRequestSourceError(`Request source ${sourceId} returned an invalid contribution.`);
	}
	const contribution = value as GameRequestContribution;
	const context: unknown = contribution.context;
	const tools: unknown = contribution.tools;
	const transformToolResult: unknown = contribution.transformToolResult;
	const requiredToolResultBytes: unknown = contribution.requiredToolResultBytes;
	const transformToolResultTextBytes: unknown = contribution.transformToolResultTextBytes;
	let capturedContext: ReturnType<typeof captureGameContextContribution> | undefined;
	try {
		capturedContext = context === undefined ? undefined : captureGameContextContribution(context);
	} catch (error) {
		throw new InvalidRequestSourceError(`Request source ${sourceId} returned invalid context.`, { cause: error });
	}
	if (tools !== undefined && !Array.isArray(tools)) {
		throw new InvalidRequestSourceError(`Request source ${sourceId} returned invalid Tools.`);
	}
	if (transformToolResult !== undefined && typeof transformToolResult !== "function") {
		throw new InvalidRequestSourceError(`Request source ${sourceId} returned an invalid Tool result transformer.`);
	}
	if (
		requiredToolResultBytes !== undefined &&
		(!Number.isSafeInteger(requiredToolResultBytes) || (requiredToolResultBytes as number) < 1)
	) {
		throw new InvalidRequestSourceError(`Request source ${sourceId} returned an invalid Tool result admission.`);
	}
	if (
		transformToolResultTextBytes !== undefined &&
		(!Number.isSafeInteger(transformToolResultTextBytes) || (transformToolResultTextBytes as number) < 1)
	) {
		throw new InvalidRequestSourceError(`Request source ${sourceId} returned an invalid transform input admission.`);
	}
	if (transformToolResultTextBytes !== undefined && typeof transformToolResult !== "function") {
		throw new InvalidRequestSourceError(`Request source ${sourceId} declared transform input without a transformer.`);
	}
	return Object.freeze({
		...(capturedContext === undefined ? {} : { context: capturedContext }),
		...(tools === undefined ? {} : { tools }),
		...(typeof transformToolResult === "function"
			? {
					transformToolResult: transformToolResult.bind(value) as NonNullable<
						GameRequestContribution["transformToolResult"]
					>,
				}
			: {}),
		...(requiredToolResultBytes === undefined ? {} : { requiredToolResultBytes: requiredToolResultBytes as number }),
		...(transformToolResultTextBytes === undefined
			? {}
			: { transformToolResultTextBytes: transformToolResultTextBytes as number }),
	}) satisfies Readonly<GameRequestContribution>;
}
