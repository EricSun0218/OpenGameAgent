import { type AssistantMessage, createAssistantMessageEventStream, type Model } from "@earendil-works/pi-ai";
import type { GameContextLimits, GameToolPolicy, GameToolSnapshotLimits } from "@opengameagent/agent";
import type { JsonValue } from "@opengameagent/protocol";
import { GameRuntime, type GameRuntimeLimits, InMemoryEventStore } from "@opengameagent/runtime";
import {
	type ActivateGameExtensionsOptions,
	activateGameExtensions,
	GameExtensionActivationError,
	type GameExtensionActivationErrorCode,
	type GameExtensionResource,
} from "./activation.js";
import type { GameExtensionDiagnostic } from "./dependency-diagnostics.js";

export interface RunGameExtensionConformanceOptions extends Omit<ActivateGameExtensionsOptions, "signal"> {
	readonly runtimeLimits: GameRuntimeLimits;
	readonly contextLimits: GameContextLimits;
	readonly toolLimits: GameToolSnapshotLimits;
	readonly input: JsonValue;
}

export type GameExtensionConformanceDiagnostic =
	| GameExtensionDiagnostic
	| {
			readonly code: Exclude<GameExtensionActivationErrorCode, "extension_set_invalid">;
			readonly extensionId?: string;
	  }
	| { readonly code: "runtime_failed" };

export interface GameExtensionConformanceReport {
	readonly passed: boolean;
	readonly providerRequestCount: number;
	readonly resources: readonly GameExtensionResource[];
	readonly diagnostics: readonly GameExtensionConformanceDiagnostic[];
}

const fixtureModel: Model<"openai-responses"> = Object.freeze({
	id: "extension-conformance-model",
	name: "Extension conformance model",
	api: "openai-responses",
	provider: "opengameagent-conformance",
	baseUrl: "https://example.invalid",
	reasoning: false,
	input: ["text"] as Array<"text">,
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 1,
	maxTokens: 1,
});

const allowAllTools: GameToolPolicy = (_input, candidates) => ({
	revision: "extension-conformance-policy",
	visibleToolNames: candidates.map((candidate) => candidate.name),
});

export async function runGameExtensionConformance(
	options: RunGameExtensionConformanceOptions,
	signal: AbortSignal = new AbortController().signal,
): Promise<GameExtensionConformanceReport> {
	signal.throwIfAborted();
	let providerRequestCount = 0;
	let active: Awaited<ReturnType<typeof activateGameExtensions>>;
	try {
		active = await activateGameExtensions({
			entries: options.entries,
			allowedPermissions: options.allowedPermissions,
			setLimits: options.setLimits,
			activationLimits: options.activationLimits,
			signal,
		});
	} catch (error) {
		if (error instanceof GameExtensionActivationError) {
			const diagnostics = error.setReport
				? error.setReport.diagnostics
				: [
						Object.freeze({
							code: error.code as Exclude<GameExtensionActivationErrorCode, "extension_set_invalid">,
							...(error.extensionId ? { extensionId: error.extensionId } : {}),
						}),
					];
			return report(false, providerRequestCount, [], diagnostics);
		}
		throw error;
	}

	const diagnostics: GameExtensionConformanceDiagnostic[] = [];
	try {
		let eventId = 0;
		let agentId = 0;
		const runtime = new GameRuntime(
			new InMemoryEventStore({
				newEventId: () => `extension-conformance-event-${++eventId}`,
				now: () => "2000-01-01T00:00:00.000Z",
			}),
			{ ...options.runtimeLimits, newRunId: () => "extension-conformance-run" },
		);
		await runtime.registerActor(
			active.composeActorOptions({
				sessionId: "extension-conformance-session",
				actorId: "extension-conformance-actor",
				model: fixtureModel,
				modelStream: (_model, _context) => {
					providerRequestCount += 1;
					return fakeTextResponse();
				},
				contextLimits: options.contextLimits,
				toolPolicy: allowAllTools,
				toolLimits: options.toolLimits,
				newId: () => `extension-conformance-agent-${++agentId}`,
				now: () => 1,
			}),
		);
		signal.throwIfAborted();
		const handle = runtime.start({
			sessionId: "extension-conformance-session",
			actorId: "extension-conformance-actor",
			inputId: "extension-conformance-input",
			payload: structuredClone(options.input),
		});
		const abort = () => {
			void runtime.abortRun(handle.initialCoordinate, signal.reason);
		};
		if (signal.aborted) abort();
		else signal.addEventListener("abort", abort, { once: true });
		let result: Awaited<typeof handle.completion>;
		try {
			result = await handle.completion;
		} finally {
			signal.removeEventListener("abort", abort);
		}
		signal.throwIfAborted();
		if (result.status !== "completed") diagnostics.push(Object.freeze({ code: "runtime_failed" }));
	} catch {
		if (signal.aborted) {
			try {
				await active.dispose();
			} catch {
				// Caller cancellation remains authoritative over cleanup failure.
			}
			throw signal.reason;
		}
		diagnostics.push(Object.freeze({ code: "runtime_failed" }));
	}

	try {
		await active.dispose();
	} catch (error) {
		if (error instanceof GameExtensionActivationError) {
			diagnostics.push(Object.freeze({ code: error.code === "extension_set_invalid" ? "dispose_failed" : error.code }));
		} else {
			diagnostics.push(Object.freeze({ code: "dispose_failed" }));
		}
	}
	return report(diagnostics.length === 0, providerRequestCount, active.resources, diagnostics);
}

function report(
	passed: boolean,
	providerRequestCount: number,
	resources: readonly GameExtensionResource[],
	diagnostics: readonly GameExtensionConformanceDiagnostic[],
): GameExtensionConformanceReport {
	return Object.freeze({
		passed,
		providerRequestCount,
		resources: Object.freeze([...resources]),
		diagnostics: Object.freeze([...diagnostics]),
	});
}

function fakeTextResponse() {
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		const message = fakeMessage([{ type: "text", text: "extension conformance" }], "stop");
		stream.push({ type: "start", partial: fakeMessage([], "pending") });
		stream.push({ type: "text_start", contentIndex: 0, partial: fakeMessage([{ type: "text", text: "" }], "pending") });
		stream.push({
			type: "text_delta",
			contentIndex: 0,
			delta: "extension conformance",
			partial: fakeMessage([{ type: "text", text: "extension conformance" }], "pending"),
		});
		stream.push({ type: "text_end", contentIndex: 0, content: "extension conformance", partial: message });
		stream.push({ type: "done", reason: "stop", message });
	});
	return stream;
}

function fakeMessage(
	content: AssistantMessage["content"],
	stopReason: AssistantMessage["stopReason"],
): AssistantMessage {
	return {
		role: "assistant",
		content,
		api: fixtureModel.api,
		provider: fixtureModel.provider,
		model: fixtureModel.id,
		usage: {
			input: 0,
			output: 0,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: 0,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason,
		timestamp: 1,
	};
}
