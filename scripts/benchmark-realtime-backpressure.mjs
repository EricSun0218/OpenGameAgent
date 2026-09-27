import { Buffer } from "node:buffer";
import { execFileSync } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { availableParallelism, cpus, platform, release, totalmem } from "node:os";
import { dirname, resolve } from "node:path";
import { performance } from "node:perf_hooks";

const CONFIG_JSON_MAX_DEPTH = 2;
const PACKAGE_JSON_MAX_DEPTH = 3;
const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const FIXED_TIMESTAMP = "2026-01-01T00:00:00.000Z";
const MEDIA_TYPE = "audio/pcm";
const TRANSCRIPT = "benchmark";
const TURNS_PER_CYCLE = 4;
const RECOVERY_EVENT_COUNT = 6;
const NUMERIC_EVIDENCE_PATHS = [
	"actorCount",
	"cyclesPerActor",
	"maxActiveActors",
	"maxQueuedRuns",
	"eventBacklogCapacity",
	"textBacklogCapacity",
	"textEventBacklogCapacity",
	"maxTotalTurns",
	"maxSyntheticSegmentsPerWave",
	"progressTimeoutMs",
];
const CONFIG_KEYS = ["profile", "purpose", "sessionId", ...NUMERIC_EVIDENCE_PATHS, "evidence"];

async function main() {
	const arguments_ = process.argv.slice(2);
	const fallbackOutputPath = extractOutputPath(arguments_);
	try {
		const options = parseArguments(arguments_);
		if (options.help) {
			printHelp();
			return;
		}
		if (options.configPath === undefined) throw new BenchmarkConfigError("benchmark_config_required");

		const { parseStrictJsonText } = await import("../packages/protocol/dist/index.js");
		const config = validateConfig(
			parseStrictJsonText(await readFile(resolve(options.configPath), "utf8"), CONFIG_JSON_MAX_DEPTH),
		);
		const report = await runBenchmark(config, parseStrictJsonText);
		try {
			await writeReport(options.outputPath, report);
		} catch (error) {
			if (!(error instanceof BenchmarkOutputError)) throw error;
			writeReportToStandardOutput(withOutputFailure(report, error));
			process.exitCode = 1;
			return;
		}
		if (report.outcome.status === "failed") process.exitCode = 1;
	} catch (error) {
		const failure = minimalFailureReport(error);
		if (error instanceof BenchmarkOutputError) writeReportToStandardOutput(failure);
		else await writeFailureReport(fallbackOutputPath, failure);
		process.exitCode = 1;
	}
}

async function runBenchmark(config, parseStrictJsonText) {
	const benchmarkStartedAt = performance.now();
	const totalCycles = safeProduct(config.actorCount, config.cyclesPerActor, "benchmark_config_workload_overflow");
	const totalTurns = safeProduct(totalCycles, TURNS_PER_CYCLE, "benchmark_config_workload_overflow");
	const textSegmentsPerFailure = safeAdd(config.textBacklogCapacity, 1, "benchmark_config_text_segments_overflow");
	const counters = {
		eventBackpressureTurns: 0,
		textBackpressureTurns: 0,
		eventRecoveryTurns: 0,
		textRecoveryTurns: 0,
		eventAcceptedEvents: 0,
		textVisibleDeltas: 0,
		recoveryAudioChunks: 0,
		providerStarts: 0,
		stalledSynthesisStarts: 0,
		stalledSynthesisReturns: 0,
		lateSynthesisResolutions: 0,
		heldRecoverySynthesisStarts: 0,
	};
	const runtimeEventCounts = new Map();
	const memorySnapshots = { before: process.memoryUsage() };
	const durationsMs = {};
	let phase = "environment";
	let environment = basicEnvironment();
	let stopRuntimeListener;
	let synthesisController;
	let derivedLimits;
	const activeTurns = new Set();
	try {
		environment = await describeEnvironment(parseStrictJsonText);
		phase = "dependencies";
		const [{ GameRuntime, InMemoryEventStore }, { GameRealtimeVoiceService }, devtools] = await Promise.all([
			import("../packages/runtime/dist/index.js"),
			import("../packages/realtime/dist/index.js"),
			import("../packages/devtools/dist/pi-fakes.js"),
		]);

		let nextEventId = 0;
		let nextRunId = 0;
		const runtime = new GameRuntime(
			new InMemoryEventStore({
				newEventId: () => `event-${++nextEventId}`,
				now: () => FIXED_TIMESTAMP,
			}),
			{
				maxActiveActors: config.maxActiveActors,
				maxQueuedRuns: config.maxQueuedRuns,
				maxInputBytes: 4,
				maxInputDepth: 1,
				newRunId: () => `run-${++nextRunId}`,
			},
		);
		const actorWidth = String(config.actorCount - 1).length;
		const providerCallCounts = Array.from({ length: config.actorCount }, () => 0);

		phase = "registration";
		const registrationStartedAt = performance.now();
		await runtime.registerActors(
			Array.from({ length: config.actorCount }, (_, actorIndex) => ({
				sessionId: config.sessionId,
				actorId: actorId(actorIndex, actorWidth),
				model: devtools.TEST_MODEL,
				modelStream: () => {
					const call = providerCallCounts[actorIndex]++;
					counters.providerStarts += 1;
					const kind = call % 4;
					if (kind === 0) {
						return devtools.textResponse(
							"x".repeat(config.eventBacklogCapacity),
							Array.from({ length: config.eventBacklogCapacity }, () => "x"),
						);
					}
					if (kind === 2) {
						return devtools.textResponse(
							"x".repeat(config.textBacklogCapacity + 1),
							Array.from({ length: config.textBacklogCapacity + 1 }, () => "x"),
						);
					}
					return devtools.textResponse("r");
				},
				now: () => 1,
			})),
		);
		durationsMs.registration = performance.now() - registrationStartedAt;
		memorySnapshots.afterRegistration = process.memoryUsage();

		for (let actorIndex = 0; actorIndex < config.actorCount; actorIndex += 1) {
			const unsubscribe = runtime.subscribeImmediate(
				{ sessionId: config.sessionId, actorId: actorId(actorIndex, actorWidth) },
				(event) => runtimeEventCounts.set(event.data.type, (runtimeEventCounts.get(event.data.type) ?? 0) + 1),
			);
			const previous = stopRuntimeListener;
			stopRuntimeListener = () => {
				previous?.();
				unsubscribe();
			};
		}

		const identifiers = benchmarkIdentifiers(config, actorWidth);
		const commonLimits = derivedCommonLimits(config, identifiers);
		const eventLimits = {
			...commonLimits,
			maxPendingTextSegments: commonLimits.maxAssistantTextEvents,
			maxEventBacklog: config.eventBacklogCapacity,
		};
		const textLimits = {
			...commonLimits,
			maxPendingTextSegments: config.textBacklogCapacity,
			maxEventBacklog: config.textEventBacklogCapacity,
		};
		derivedLimits = { eventService: eventLimits, textService: textLimits };
		const eventService = new GameRealtimeVoiceService({
			runtime,
			recognizer: finalRecognizer(),
			synthesizer: drainingSynthesizer(),
			projectInput: ({ coordinate }) => ({ inputId: coordinate.utteranceId, payload: null }),
			limits: eventLimits,
		});
		synthesisController = createSynthesisController(counters, config.progressTimeoutMs);
		const textService = new GameRealtimeVoiceService({
			runtime,
			recognizer: finalRecognizer(),
			synthesizer: synthesisController.synthesizer,
			projectInput: ({ coordinate }) => ({ inputId: coordinate.utteranceId, payload: null }),
			limits: textLimits,
		});

		phase = "workload";
		const workloadStartedAt = performance.now();
		for (let cycle = 0; cycle < config.cyclesPerActor; cycle += 1) {
			const ordinalBase = cycle * TURNS_PER_CYCLE;
			const eventFailure = startWave(eventService, config, actorWidth, ordinalBase, "drain", activeTurns);
			const eventFailureResults = await settleCompletions(eventFailure, config.progressTimeoutMs, "event_failure");
			const eventFailureEvents = await collectWave(eventFailure, config.progressTimeoutMs, "event_failure_events");
			for (let index = 0; index < eventFailure.length; index += 1) {
				assertFailure(eventFailureResults[index], "event_backpressure", "event_failure_result_mismatch");
				const events = eventFailureEvents[index];
				if (events.length !== config.eventBacklogCapacity) {
					throw new BenchmarkInvariantError("event_backlog_prefix_mismatch");
				}
				if (events.some((event) => event.type.startsWith("assistant.audio"))) {
					throw new BenchmarkInvariantError("event_backpressure_audio_leak");
				}
				counters.eventBackpressureTurns += 1;
				counters.eventAcceptedEvents += events.length;
			}

			const eventRecovery = startWave(eventService, config, actorWidth, ordinalBase + 1, "drain", activeTurns);
			const eventRecoveryCollections = eventRecovery.map((entry) => collectEvents(entry.handle.events));
			const eventRecoveryEventsOutcome = captureOutcome(
				settlePromises(eventRecoveryCollections, config.progressTimeoutMs, "event_recovery_events"),
			);
			const eventRecoveryResults = await settleCompletions(eventRecovery, config.progressTimeoutMs, "event_recovery");
			const eventRecoveryEvents = unwrapOutcome(await eventRecoveryEventsOutcome);
			for (let index = 0; index < eventRecovery.length; index += 1) {
				assertRecovery(eventRecoveryResults[index], eventRecoveryEvents[index]);
				counters.eventRecoveryTurns += 1;
				counters.recoveryAudioChunks += 1;
			}

			const textFailure = startWave(textService, config, actorWidth, ordinalBase + 2, "stall", activeTurns);
			const textFailureCollections = textFailure.map((entry) => collectEvents(entry.handle.events));
			const textFailureEventsOutcome = captureOutcome(
				settlePromises(textFailureCollections, config.progressTimeoutMs, "text_failure_events"),
			);
			const textFailureResults = await settleCompletions(textFailure, config.progressTimeoutMs, "text_failure");
			const textFailureEvents = unwrapOutcome(await textFailureEventsOutcome);
			for (let index = 0; index < textFailure.length; index += 1) {
				assertFailure(textFailureResults[index], "text_backpressure", "text_failure_result_mismatch");
				const events = textFailureEvents[index];
				const visibleDeltas = events.filter((event) => event.type === "assistant.text.delta").length;
				if (visibleDeltas !== textSegmentsPerFailure) {
					throw new BenchmarkInvariantError("text_backlog_boundary_mismatch");
				}
				if (events.some((event) => event.type.startsWith("assistant.audio"))) {
					throw new BenchmarkInvariantError("text_backpressure_audio_leak");
				}
				counters.textBackpressureTurns += 1;
				counters.textVisibleDeltas += visibleDeltas;
			}

			const expectedRecoveryStarts = (cycle + 1) * config.actorCount;
			const textRecovery = startWave(textService, config, actorWidth, ordinalBase + 3, "recovery-hold", activeTurns);
			const textRecoveryCollections = textRecovery.map((entry) => collectEvents(entry.handle.events));
			const textRecoveryEventsOutcome = captureOutcome(
				settlePromises(textRecoveryCollections, config.progressTimeoutMs, "text_recovery_events"),
			);
			const textRecoveryOutcome = captureOutcome(
				settleCompletions(textRecovery, config.progressTimeoutMs, "text_recovery"),
			);
			await synthesisController.waitForRecoveryStarts(expectedRecoveryStarts);
			synthesisController.releaseLate(config.actorCount);
			await Promise.resolve();
			synthesisController.releaseRecoveries(config.actorCount);
			const textRecoveryResults = unwrapOutcome(await textRecoveryOutcome);
			const textRecoveryEvents = unwrapOutcome(await textRecoveryEventsOutcome);
			for (let index = 0; index < textRecovery.length; index += 1) {
				assertRecovery(textRecoveryResults[index], textRecoveryEvents[index]);
				counters.textRecoveryTurns += 1;
				counters.recoveryAudioChunks += 1;
			}
		}
		durationsMs.workload = performance.now() - workloadStartedAt;
		memorySnapshots.afterWorkload = process.memoryUsage();

		phase = "verification";
		const expectedRuntimeEvents = totalTurns;
		const checks = {
			exactEventFailures: counters.eventBackpressureTurns === totalCycles,
			exactTextFailures: counters.textBackpressureTurns === totalCycles,
			exactEventRecoveries: counters.eventRecoveryTurns === totalCycles,
			exactTextRecoveries: counters.textRecoveryTurns === totalCycles,
			exactEventPrefixes:
				counters.eventAcceptedEvents ===
				safeProduct(totalCycles, config.eventBacklogCapacity, "benchmark_config_counter_overflow"),
			exactTextBoundary:
				counters.textVisibleDeltas ===
				safeProduct(totalCycles, textSegmentsPerFailure, "benchmark_config_counter_overflow"),
			exactProviderStarts: counters.providerStarts === totalTurns,
			exactStalledSynthesisStarts: counters.stalledSynthesisStarts === totalCycles,
			exactStalledSynthesisReturns: counters.stalledSynthesisReturns === totalCycles,
			exactLateTailResolutions: counters.lateSynthesisResolutions === totalCycles,
			exactHeldRecoverySynthesisStarts: counters.heldRecoverySynthesisStarts === totalCycles,
			exactRecoveryAudio:
				counters.recoveryAudioChunks === safeProduct(totalCycles, 2, "benchmark_config_counter_overflow"),
			exactRuntimeQueued: runtimeEventCounts.get("run.queued") === expectedRuntimeEvents,
			exactRuntimeStarted: runtimeEventCounts.get("run.started") === expectedRuntimeEvents,
			exactRuntimeCompleted: runtimeEventCounts.get("run.completed") === expectedRuntimeEvents,
			exactPerActorProviderStarts: providerCallCounts.every(
				(count) => count === safeProduct(config.cyclesPerActor, TURNS_PER_CYCLE, "benchmark_config_counter_overflow"),
			),
		};
		const failedChecks = Object.entries(checks)
			.filter(([, passed]) => !passed)
			.map(([name]) => name);
		durationsMs.total = performance.now() - benchmarkStartedAt;
		const report = {
			schemaVersion: 1,
			createdAt: new Date().toISOString(),
			profile: config.profile,
			purpose: config.purpose,
			outcome:
				failedChecks.length === 0
					? { status: "passed" }
					: { status: "failed", code: "benchmark_acceptance_failed", failedChecks },
			configuration: config,
			derivedLimits,
			environment,
			workload: { totalCycles, totalTurns },
			counters,
			runtimeEventCounts: Object.fromEntries(
				[...runtimeEventCounts.entries()].sort(([left], [right]) => left.localeCompare(right)),
			),
			checks,
			durationsMs,
			throughput: {
				turnsPerSecond: (totalTurns * 1000) / durationsMs.workload,
				cyclesPerSecond: (totalCycles * 1000) / durationsMs.workload,
			},
			memory: summarizeMemory(memorySnapshots),
			notes: [
				"Backlog capacities are explicit benchmark workload inputs and are not runtime or product defaults.",
				"Durations and throughput are observations from this host, not latency or capacity SLAs.",
				"Every failed turn is followed by a held same-channel recovery turn; the old stalled-provider tail is resolved while that newer turn is active.",
			],
		};
		return report;
	} catch (error) {
		durationsMs.total = performance.now() - benchmarkStartedAt;
		return {
			schemaVersion: 1,
			createdAt: new Date().toISOString(),
			profile: config.profile,
			purpose: config.purpose,
			outcome: {
				status: "failed",
				code: benchmarkErrorCode(error),
				errorName: error instanceof Error ? error.name : typeof error,
				phase,
			},
			configuration: config,
			derivedLimits,
			environment,
			counters,
			runtimeEventCounts: Object.fromEntries(runtimeEventCounts),
			durationsMs,
			memory: summarizeMemory({ ...memorySnapshots, onFailure: process.memoryUsage() }),
		};
	} finally {
		synthesisController?.close();
		await cleanupActiveTurns(activeTurns, config.progressTimeoutMs);
		stopRuntimeListener?.();
	}
}

function startWave(service, config, actorWidth, ordinal, voiceProfileId, activeTurns) {
	return Array.from({ length: config.actorCount }, (_, actorIndex) => {
		const actor = actorId(actorIndex, actorWidth);
		const epoch = String(ordinal + 1);
		const utteranceId = `u-${epoch}-${actor}`;
		const handle = service.start({
			coordinate: {
				sessionId: config.sessionId,
				actorId: actor,
				voiceSessionId: `voice-${actor}`,
				epoch,
				utteranceId,
			},
			inputFormat: { mediaType: MEDIA_TYPE, sampleRateHz: 1, channelCount: 1 },
			outputFormat: { mediaType: MEDIA_TYPE, sampleRateHz: 1, channelCount: 1 },
			voiceProfileId,
			audio: oneByteAudio(),
		});
		const entry = { service, handle };
		activeTurns.add(entry);
		void handle.completion.finally(() => activeTurns.delete(entry)).catch(() => undefined);
		return entry;
	});
}

async function cleanupActiveTurns(activeTurns, timeoutMs) {
	const snapshot = [...activeTurns];
	if (snapshot.length === 0) return;
	try {
		await settlePromises(
			snapshot.map((entry) =>
				entry.service.interrupt(entry.handle.coordinate, new BenchmarkProgressError("benchmark_cleanup")),
			),
			timeoutMs,
			"cleanup_interrupt",
		);
	} catch {
		// Best-effort cleanup must not replace the benchmark's primary report.
	}
	try {
		await settlePromises(
			snapshot.map((entry) => entry.handle.completion),
			timeoutMs,
			"cleanup_completion",
		);
	} catch {
		// A non-cooperative host dependency must not keep the benchmark process alive.
	}
}

async function settleCompletions(entries, timeoutMs, phase) {
	return settlePromises(
		entries.map((entry) => entry.handle.completion),
		timeoutMs,
		phase,
	);
}

function captureOutcome(promise) {
	return Promise.resolve(promise).then(
		(value) => ({ status: "fulfilled", value }),
		(error) => ({ status: "rejected", error }),
	);
}

function unwrapOutcome(outcome) {
	if (outcome.status === "rejected") throw outcome.error;
	return outcome.value;
}

async function collectWave(entries, timeoutMs, phase) {
	return settlePromises(
		entries.map((entry) => collectEvents(entry.handle.events)),
		timeoutMs,
		phase,
	);
}

async function settlePromises(promises, timeoutMs, phase) {
	if (promises.length === 0) return [];
	return new Promise((resolvePromise, rejectPromise) => {
		const results = Array(promises.length);
		let settled = 0;
		let timer;
		let finished = false;
		const fail = (error) => {
			if (finished) return;
			finished = true;
			clearTimeout(timer);
			rejectPromise(error);
		};
		const arm = () => {
			if (finished) return;
			clearTimeout(timer);
			timer = setTimeout(() => fail(new BenchmarkProgressError(`${phase}_progress_timeout`)), timeoutMs);
		};
		arm();
		for (const [index, promise] of promises.entries()) {
			Promise.resolve(promise).then(
				(value) => {
					if (finished) return;
					results[index] = value;
					settled += 1;
					if (settled === promises.length) {
						finished = true;
						clearTimeout(timer);
						resolvePromise(results);
					} else arm();
				},
				(error) => fail(error),
			);
		}
	});
}

function assertFailure(result, failure, code) {
	if (result?.status !== "failed" || result.failure !== failure) throw new BenchmarkInvariantError(code);
}

function assertRecovery(result, events) {
	if (result?.status !== "completed") throw new BenchmarkInvariantError("recovery_result_mismatch");
	const audio = events.filter((event) => event.type === "assistant.audio.chunk");
	if (audio.length !== 1 || audio[0].chunk.bytes.length !== 1 || audio[0].chunk.bytes[0] !== 7) {
		throw new BenchmarkInvariantError("recovery_audio_mismatch");
	}
}

function finalRecognizer() {
	return {
		async *recognize(request) {
			for await (const _chunk of request.audio) {
				// consume
			}
			yield { type: "transcript.final", text: TRANSCRIPT };
		},
	};
}

function drainingSynthesizer() {
	return {
		async *synthesize(request) {
			for await (const _text of request.text) {
				// consume
			}
			yield { bytes: Uint8Array.of(7) };
		},
	};
}

function createSynthesisController(counters, progressTimeoutMs) {
	const late = [];
	const recoveryGates = [];
	const recoveryWaiters = [];
	let closed = false;
	const notifyRecoveryStarts = () => {
		for (let index = recoveryWaiters.length - 1; index >= 0; index -= 1) {
			const waiter = recoveryWaiters[index];
			if (counters.heldRecoverySynthesisStarts < waiter.target) continue;
			recoveryWaiters.splice(index, 1);
			clearTimeout(waiter.timer);
			waiter.resolve();
		}
	};
	return {
		synthesizer: {
			synthesize(request) {
				if (request.voiceProfileId === "stall") {
					counters.stalledSynthesisStarts += 1;
					return {
						[Symbol.asyncIterator]() {
							return {
								next: () =>
									new Promise((resolvePromise) => {
										late.push(resolvePromise);
									}),
								return: async () => {
									counters.stalledSynthesisReturns += 1;
									return { done: true, value: undefined };
								},
							};
						},
					};
				}
				if (request.voiceProfileId !== "recovery-hold") return drainingSynthesizer().synthesize(request);
				return (async function* () {
					const gate = new Promise((resolvePromise) => recoveryGates.push(resolvePromise));
					counters.heldRecoverySynthesisStarts += 1;
					notifyRecoveryStarts();
					await gate;
					for await (const _text of request.text) {
						// consume
					}
					yield { bytes: Uint8Array.of(7) };
				})();
			},
		},
		waitForRecoveryStarts(target) {
			if (counters.heldRecoverySynthesisStarts >= target) return Promise.resolve();
			return new Promise((resolvePromise, rejectPromise) => {
				const waiter = { target, resolve: resolvePromise, timer: undefined };
				waiter.timer = setTimeout(() => {
					const index = recoveryWaiters.indexOf(waiter);
					if (index >= 0) recoveryWaiters.splice(index, 1);
					rejectPromise(new BenchmarkProgressError("recovery_synthesis_progress_timeout"));
				}, progressTimeoutMs);
				recoveryWaiters.push(waiter);
			});
		},
		releaseLate(count) {
			if (late.length !== count) throw new BenchmarkInvariantError("stalled_synthesis_tail_mismatch");
			for (const pending of late.splice(0)) {
				pending({ done: false, value: { bytes: Uint8Array.of(9) } });
				counters.lateSynthesisResolutions += 1;
			}
		},
		releaseRecoveries(count) {
			if (recoveryGates.length !== count) throw new BenchmarkInvariantError("recovery_synthesis_gate_mismatch");
			for (const releaseGate of recoveryGates.splice(0)) releaseGate();
		},
		close() {
			if (closed) return;
			closed = true;
			for (const pending of late.splice(0)) pending({ done: true, value: undefined });
			for (const releaseGate of recoveryGates.splice(0)) releaseGate();
			for (const waiter of recoveryWaiters.splice(0)) {
				clearTimeout(waiter.timer);
				waiter.resolve();
			}
		},
	};
}

async function* oneByteAudio() {
	yield { bytes: Uint8Array.of(1) };
}

async function collectEvents(events) {
	const collected = [];
	for await (const event of events) collected.push(event);
	return collected;
}

function benchmarkIdentifiers(config, actorWidth) {
	const lastActor = actorId(config.actorCount - 1, actorWidth);
	const lastEpoch = String(safeProduct(config.cyclesPerActor, TURNS_PER_CYCLE, "benchmark_config_identifier_overflow"));
	return [
		config.sessionId,
		lastActor,
		`voice-${lastActor}`,
		lastEpoch,
		`u-${lastEpoch}-${lastActor}`,
		"stall",
		"drain",
	];
}

function derivedCommonLimits(config, identifiers) {
	const textSegmentsPerFailure = safeAdd(config.textBacklogCapacity, 1, "benchmark_config_text_segments_overflow");
	return {
		maxIdentifierBytes: Math.max(...identifiers.map((value) => Buffer.byteLength(value))),
		maxMediaTypeBytes: Buffer.byteLength(MEDIA_TYPE),
		maxInputChunks: 1,
		maxInputChunkBytes: 1,
		maxInputAudioBytes: 1,
		maxRecognitionEvents: 1,
		maxRecognitionTextBytes: Buffer.byteLength(TRANSCRIPT),
		maxTranscriptBytes: Buffer.byteLength(TRANSCRIPT),
		maxAssistantTextEvents: Math.max(config.eventBacklogCapacity, textSegmentsPerFailure),
		maxAssistantTextBytes: Math.max(config.eventBacklogCapacity, textSegmentsPerFailure),
		maxOutputChunks: 1,
		maxOutputChunkBytes: 1,
		maxOutputAudioBytes: 1,
	};
}

function actorId(index, width) {
	return `actor-${String(index).padStart(width, "0")}`;
}

function validateConfig(value) {
	if (!isPlainObject(value)) throw new BenchmarkConfigError("benchmark_config_invalid");
	assertExactKeys(value, CONFIG_KEYS, "benchmark_config_keys_invalid");
	const profile = requiredText(value.profile, "profile");
	const purpose = requiredText(value.purpose, "purpose");
	const sessionId = requiredText(value.sessionId, "sessionId");
	const actorCount = requiredPositiveInteger(value.actorCount, "actorCount");
	const cyclesPerActor = requiredPositiveInteger(value.cyclesPerActor, "cyclesPerActor");
	const maxActiveActors = requiredPositiveInteger(value.maxActiveActors, "maxActiveActors");
	const maxQueuedRuns = requiredPositiveInteger(value.maxQueuedRuns, "maxQueuedRuns");
	const eventBacklogCapacity = requiredPositiveInteger(value.eventBacklogCapacity, "eventBacklogCapacity");
	const textBacklogCapacity = requiredPositiveInteger(value.textBacklogCapacity, "textBacklogCapacity");
	const textEventBacklogCapacity = requiredPositiveInteger(value.textEventBacklogCapacity, "textEventBacklogCapacity");
	const maxTotalTurns = requiredPositiveInteger(value.maxTotalTurns, "maxTotalTurns");
	const maxSyntheticSegmentsPerWave = requiredPositiveInteger(
		value.maxSyntheticSegmentsPerWave,
		"maxSyntheticSegmentsPerWave",
	);
	const progressTimeoutMs = requiredPositiveInteger(value.progressTimeoutMs, "progressTimeoutMs");
	if (maxActiveActors !== actorCount)
		throw new BenchmarkConfigError("benchmark_config_active_actor_derivation_mismatch");
	if (maxQueuedRuns !== actorCount) throw new BenchmarkConfigError("benchmark_config_queue_derivation_mismatch");
	if (eventBacklogCapacity < RECOVERY_EVENT_COUNT) {
		throw new BenchmarkConfigError("benchmark_config_event_backlog_too_small");
	}
	if (progressTimeoutMs > NODE_MAX_TIMER_DELAY_MS) throw new BenchmarkConfigError("benchmark_config_timeout_invalid");
	const totalCycles = safeProduct(actorCount, cyclesPerActor, "benchmark_config_workload_overflow");
	const totalTurns = safeProduct(totalCycles, TURNS_PER_CYCLE, "benchmark_config_workload_overflow");
	if (maxTotalTurns !== totalTurns) throw new BenchmarkConfigError("benchmark_config_total_turns_derivation_mismatch");
	const textSegmentsPerFailure = safeAdd(textBacklogCapacity, 1, "benchmark_config_text_segments_overflow");
	const expectedTextEventBacklog = Math.max(
		safeAdd(textBacklogCapacity, 4, "benchmark_config_text_event_backlog_overflow"),
		RECOVERY_EVENT_COUNT,
	);
	if (textEventBacklogCapacity !== expectedTextEventBacklog) {
		throw new BenchmarkConfigError("benchmark_config_text_event_backlog_derivation_mismatch");
	}
	const expectedSegmentsPerWave = safeProduct(
		actorCount,
		Math.max(eventBacklogCapacity, textSegmentsPerFailure),
		"benchmark_config_synthetic_segments_overflow",
	);
	if (maxSyntheticSegmentsPerWave !== expectedSegmentsPerWave) {
		throw new BenchmarkConfigError("benchmark_config_synthetic_segments_derivation_mismatch");
	}
	safeProduct(totalCycles, eventBacklogCapacity, "benchmark_config_counter_overflow");
	safeProduct(totalCycles, textSegmentsPerFailure, "benchmark_config_counter_overflow");
	const evidence = captureEvidence(value.evidence);
	return Object.freeze({
		profile,
		purpose,
		sessionId,
		actorCount,
		cyclesPerActor,
		maxActiveActors,
		maxQueuedRuns,
		eventBacklogCapacity,
		textBacklogCapacity,
		textEventBacklogCapacity,
		maxTotalTurns,
		maxSyntheticSegmentsPerWave,
		progressTimeoutMs,
		evidence,
	});
}

function captureEvidence(value) {
	if (!isPlainObject(value)) throw new BenchmarkConfigError("benchmark_config_evidence_required");
	assertExactKeys(value, NUMERIC_EVIDENCE_PATHS, "benchmark_config_evidence_keys_invalid");
	const evidence = {};
	for (const path of NUMERIC_EVIDENCE_PATHS) evidence[path] = requiredText(value[path], `evidence.${path}`);
	return Object.freeze(evidence);
}

function requiredText(value, path) {
	if (typeof value !== "string" || value.trim().length === 0) {
		throw new BenchmarkConfigError(`benchmark_config_${path.replaceAll(".", "_")}_invalid`);
	}
	return value.trim();
}

function requiredPositiveInteger(value, path) {
	if (!Number.isSafeInteger(value) || Object.is(value, -0) || value <= 0) {
		throw new BenchmarkConfigError(`benchmark_config_${path}_invalid`);
	}
	return value;
}

function safeProduct(left, right, code) {
	const product = left * right;
	if (!Number.isSafeInteger(product)) throw new BenchmarkConfigError(code);
	return product;
}

function safeAdd(left, right, code) {
	const sum = left + right;
	if (!Number.isSafeInteger(sum)) throw new BenchmarkConfigError(code);
	return sum;
}

function assertExactKeys(value, expected, code) {
	const actual = Object.keys(value).sort();
	const required = [...expected].sort();
	if (actual.length !== required.length || actual.some((key, index) => key !== required[index])) {
		throw new BenchmarkConfigError(code);
	}
}

function isPlainObject(value) {
	if (!value || typeof value !== "object" || Array.isArray(value)) return false;
	const prototype = Object.getPrototypeOf(value);
	return prototype === Object.prototype || prototype === null;
}

async function describeEnvironment(parseStrictJsonText) {
	const [runtimePackage, realtimePackage, agentPackage] = await Promise.all([
		readPackage("packages/runtime/package.json", parseStrictJsonText),
		readPackage("packages/realtime/package.json", parseStrictJsonText),
		readPackage("packages/agent/package.json", parseStrictJsonText),
	]);
	return {
		...basicEnvironment(),
		git: describeGit(),
		packages: {
			runtime: runtimePackage.version,
			realtime: realtimePackage.version,
			agent: agentPackage.version,
			piAgentCore: agentPackage.dependencies["@earendil-works/pi-agent-core"],
			piAi: agentPackage.dependencies["@earendil-works/pi-ai"],
		},
	};
}

function basicEnvironment() {
	const processors = cpus();
	return {
		nodeVersion: process.version,
		platform: platform(),
		release: release(),
		architecture: process.arch,
		cpu: processors[0]?.model ?? "unknown",
		logicalCpuCount: processors.length,
		availableParallelism: availableParallelism(),
		totalMemoryBytes: totalmem(),
	};
}

function describeGit() {
	try {
		return {
			revision: execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim(),
			dirty: execFileSync("git", ["status", "--porcelain"], { encoding: "utf8" }).trim().length > 0,
		};
	} catch {
		return { revision: null, dirty: null };
	}
}

async function readPackage(path, parseStrictJsonText) {
	return parseStrictJsonText(await readFile(resolve(path), "utf8"), PACKAGE_JSON_MAX_DEPTH);
}

function summarizeMemory(snapshots) {
	const report = {};
	for (const [name, snapshot] of Object.entries(snapshots)) {
		report[name] = { rss: snapshot.rss, heapUsed: snapshot.heapUsed, external: snapshot.external };
	}
	return report;
}

function parseArguments(arguments_) {
	let configPath;
	let outputPath;
	for (let index = 0; index < arguments_.length; index += 1) {
		const argument = arguments_[index];
		if (argument === "--help" || argument === "-h") return { help: true };
		if (argument === "--config") {
			if (configPath !== undefined || index + 1 >= arguments_.length)
				throw new BenchmarkConfigError("benchmark_arguments_invalid");
			configPath = captureOptionValue(arguments_[++index]);
			continue;
		}
		if (argument === "--output") {
			if (outputPath !== undefined || index + 1 >= arguments_.length)
				throw new BenchmarkConfigError("benchmark_arguments_invalid");
			outputPath = captureOptionValue(arguments_[++index]);
			continue;
		}
		throw new BenchmarkConfigError("benchmark_arguments_invalid");
	}
	return { help: false, configPath, outputPath };
}

function extractOutputPath(arguments_) {
	const index = arguments_.indexOf("--output");
	if (index < 0 || index + 1 >= arguments_.length) return undefined;
	const value = arguments_[index + 1];
	return value.length > 0 && !value.startsWith("--") ? value : undefined;
}

function captureOptionValue(value) {
	if (value.length === 0 || value.startsWith("--")) throw new BenchmarkConfigError("benchmark_arguments_invalid");
	return value;
}

async function writeReport(outputPath, report) {
	const serialized = `${JSON.stringify(report, null, 2)}\n`;
	if (outputPath === undefined) {
		process.stdout.write(serialized);
		return;
	}
	try {
		const absolutePath = resolve(outputPath);
		await mkdir(dirname(absolutePath), { recursive: true });
		await writeFile(absolutePath, serialized, { encoding: "utf8", flag: "w" });
	} catch (error) {
		throw new BenchmarkOutputError(error);
	}
}

async function writeFailureReport(outputPath, report) {
	try {
		await writeReport(outputPath, report);
	} catch (error) {
		if (error instanceof BenchmarkOutputError) writeReportToStandardOutput(withOutputFailure(report, error));
		else throw error;
	}
}

function withOutputFailure(report, error) {
	return {
		...report,
		benchmarkOutcome: report.outcome,
		outcome: { status: "failed", code: "benchmark_report_write_failed", errorName: error.causeName },
		output: { status: "failed", code: "benchmark_report_write_failed", errorName: error.causeName },
	};
}

function minimalFailureReport(error) {
	return {
		schemaVersion: 1,
		createdAt: new Date().toISOString(),
		outcome: {
			status: "failed",
			code: benchmarkErrorCode(error),
			errorName: error instanceof Error ? error.name : typeof error,
		},
	};
}

function writeReportToStandardOutput(report) {
	process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
}

function benchmarkErrorCode(error) {
	if (
		error instanceof BenchmarkConfigError ||
		error instanceof BenchmarkInvariantError ||
		error instanceof BenchmarkProgressError
	) {
		return error.code;
	}
	if (error instanceof BenchmarkOutputError) return "benchmark_report_write_failed";
	return "benchmark_execution_failed";
}

function printHelp() {
	process.stdout.write(
		"Usage: node scripts/benchmark-realtime-backpressure.mjs --config <profile.json> [--output <report.json>]\n",
	);
}

class BenchmarkConfigError extends Error {
	constructor(code) {
		super(code);
		this.name = "BenchmarkConfigError";
		this.code = code;
	}
}

class BenchmarkInvariantError extends Error {
	constructor(code) {
		super(code);
		this.name = "BenchmarkInvariantError";
		this.code = code;
	}
}

class BenchmarkProgressError extends Error {
	constructor(code) {
		super(code);
		this.name = "BenchmarkProgressError";
		this.code = code;
	}
}

class BenchmarkOutputError extends Error {
	constructor(cause) {
		super("benchmark_report_write_failed");
		this.name = "BenchmarkOutputError";
		this.causeName = cause instanceof Error ? cause.name : typeof cause;
	}
}

await main();
