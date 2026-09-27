import { execFileSync } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { availableParallelism, cpus, platform, release, totalmem } from "node:os";
import { dirname, resolve } from "node:path";
import { performance } from "node:perf_hooks";

const CONFIG_JSON_MAX_DEPTH = 2;
const PACKAGE_JSON_MAX_DEPTH = 3;
const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const FIXED_TIMESTAMP = "2026-01-01T00:00:00.000Z";
const NUMERIC_EVIDENCE_PATHS = [
	"actorCount",
	"runsPerActor",
	"maxActiveActors",
	"maxQueuedRuns",
	"maxInputBytes",
	"maxInputDepth",
	"progressTimeoutMs",
];

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
		const code = benchmarkErrorCode(error);
		const failure = {
			schemaVersion: 1,
			createdAt: new Date().toISOString(),
			outcome: {
				status: "failed",
				code,
				errorName: error instanceof Error ? error.name : typeof error,
			},
		};
		if (error instanceof BenchmarkOutputError) writeReportToStandardOutput(failure);
		else await writeFailureReport(fallbackOutputPath, failure);
		process.exitCode = 1;
	}
}

async function runBenchmark(config, parseStrictJsonText) {
	const benchmarkStartedAt = performance.now();
	const totalConfiguredRuns = config.actorCount * config.runsPerActor;
	const actorWidth = String(config.actorCount - 1).length;
	const runWidth = String(config.runsPerActor - 1).length;
	const eventCounts = new Map();
	const startedInputsByActor = Array.from({ length: config.actorCount }, () => []);
	const admissionWaiters = [];
	const completionWaiters = [];
	const settlementWaiters = [];
	const memorySnapshots = { before: process.memoryUsage() };
	const durationsMs = {};
	const handles = [];
	const unsubscribe = [];
	let phase = "environment";
	let environment = basicEnvironment();
	let controller;
	let admissionCount = 0;
	let completionEventCount = 0;
	let settledHandleCount = 0;
	let releasedResponses = 0;
	let overflowErrorName = null;
	let overflowRejectedByExpectedError = false;
	try {
		environment = await describeEnvironment(parseStrictJsonText);
		phase = "dependencies";
		const [{ GameRuntime, InMemoryEventStore, RuntimeQueueFullError }, { assistant, TEST_MODEL }, piAi] =
			await Promise.all([
				import("../packages/runtime/dist/index.js"),
				import("../packages/devtools/dist/pi-fakes.js"),
				import("@earendil-works/pi-ai"),
			]);
		let nextEventId = 0;
		let nextRunId = 0;
		const eventStore = new InMemoryEventStore({
			newEventId: () => `event-${++nextEventId}`,
			now: () => FIXED_TIMESTAMP,
		});
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: config.maxActiveActors,
			maxQueuedRuns: config.maxQueuedRuns,
			maxInputBytes: config.maxInputBytes,
			maxInputDepth: config.maxInputDepth,
			newRunId: () => `run-${++nextRunId}`,
		});
		controller = createProviderController({
			assistant,
			createAssistantMessageEventStream: piAi.createAssistantMessageEventStream,
			maxActiveActors: config.maxActiveActors,
			progressTimeoutMs: config.progressTimeoutMs,
		});

		phase = "registration";
		const registrationStartedAt = performance.now();
		await runtime.registerActors(
			Array.from({ length: config.actorCount }, (_, actorIndex) => ({
				...stream(config.sessionId, actorId(actorIndex, actorWidth)),
				model: TEST_MODEL,
				modelStream: controller.streamFor(actorIndex),
				now: () => 1,
			})),
		);
		durationsMs.registration = performance.now() - registrationStartedAt;
		memorySnapshots.afterRegistration = process.memoryUsage();

		for (let actorIndex = 0; actorIndex < config.actorCount; actorIndex += 1) {
			unsubscribe.push(
				runtime.subscribeImmediate(stream(config.sessionId, actorId(actorIndex, actorWidth)), (event) => {
					const type = event.data.type;
					eventCounts.set(type, (eventCounts.get(type) ?? 0) + 1);
					if (type === "run.queued") {
						admissionCount += 1;
						notifyCountWaiters(admissionWaiters, admissionCount);
					}
					if (type === "run.started") startedInputsByActor[actorIndex].push(event.data.inputId);
					if (type === "run.completed") {
						completionEventCount += 1;
						notifyCountWaiters(completionWaiters, completionEventCount);
					}
				}),
			);
		}

		phase = "admission";
		const workloadStartedAt = performance.now();
		for (let actorIndex = 0; actorIndex < config.actorCount; actorIndex += 1) {
			const actor = actorId(actorIndex, actorWidth);
			for (let runIndex = 0; runIndex < config.runsPerActor; runIndex += 1) {
				handles.push(
					runtime.start({
						...stream(config.sessionId, actor),
						inputId: inputId(runIndex, runWidth),
						payload: null,
					}),
				);
			}
		}

		try {
			const overflowHandle = runtime.start({
				...stream(config.sessionId, actorId(0, actorWidth)),
				inputId: "overflow",
				payload: null,
			});
			handles.push(overflowHandle);
		} catch (error) {
			overflowErrorName = error instanceof Error ? error.name : typeof error;
			overflowRejectedByExpectedError = error instanceof RuntimeQueueFullError;
		}
		durationsMs.burstAdmission = performance.now() - workloadStartedAt;
		for (const handle of handles) {
			void handle.completion.then(
				() => {
					settledHandleCount += 1;
					notifyCountWaiters(settlementWaiters, settledHandleCount);
				},
				() => {
					settledHandleCount += 1;
					notifyCountWaiters(settlementWaiters, settledHandleCount);
				},
			);
		}
		await waitForCount(
			admissionWaiters,
			() => admissionCount,
			handles.length,
			config.progressTimeoutMs,
			"admission_progress_timeout",
		);
		durationsMs.durableAdmission = performance.now() - workloadStartedAt;
		memorySnapshots.afterAdmission = process.memoryUsage();

		phase = "drain";
		const drainStartedAt = performance.now();
		while (releasedResponses < handles.length) {
			const waveSize = Math.min(config.maxActiveActors, handles.length - releasedResponses);
			await controller.waitForPending(waveSize);
			releasedResponses += controller.finishPending(waveSize);
		}
		await waitForCount(
			completionWaiters,
			() => completionEventCount,
			handles.length,
			config.progressTimeoutMs,
			"completion_progress_timeout",
		);
		await waitForCount(
			settlementWaiters,
			() => settledHandleCount,
			handles.length,
			config.progressTimeoutMs,
			"handle_settlement_timeout",
		);
		const results = await Promise.all(handles.map((handle) => handle.completion));
		durationsMs.drainAfterDurableAdmission = performance.now() - drainStartedAt;
		durationsMs.workload = performance.now() - workloadStartedAt;
		durationsMs.total = performance.now() - benchmarkStartedAt;
		memorySnapshots.afterExecution = process.memoryUsage();

		phase = "assessment";
		const fairness = inspectRoundRobin(controller.starts, config);
		const fifo = inspectActorFifo(startedInputsByActor, config, runWidth);
		const completedRuns = results.filter((result) => result.status === "completed").length;
		const criticalEventCounts = criticalEvents(eventCounts);
		const checks = {
			burstOverflowRejected: overflowRejectedByExpectedError,
			configuredRunCountExecuted: handles.length === totalConfiguredRuns,
			allConfiguredRunsCompleted: completedRuns === totalConfiguredRuns,
			maxConcurrencyReached: controller.maximumActive === config.maxActiveActors,
			maxConcurrencyRespected: controller.maximumActive <= config.maxActiveActors,
			providerStartCardinality: controller.starts.length === totalConfiguredRuns,
			noPendingProviderResponses: controller.pendingCount === 0,
			perActorFifo: fifo.valid,
			roundRobinFairness: fairness.valid,
			criticalEventCardinality:
				criticalEventCounts.queued === totalConfiguredRuns &&
				criticalEventCounts.started === totalConfiguredRuns &&
				criticalEventCounts.completed === totalConfiguredRuns,
		};
		const passed = Object.values(checks).every(Boolean);
		return {
			schemaVersion: 1,
			createdAt: new Date().toISOString(),
			revision: readRevision(),
			dirtyWorkingTree: hasWorkingTreeChanges(),
			config,
			environment,
			durationsMs,
			throughput: {
				configuredRuns: totalConfiguredRuns,
				runsPerWorkloadSecond: totalConfiguredRuns / (durationsMs.workload / 1_000),
				runsPerTotalSecond: totalConfiguredRuns / (durationsMs.total / 1_000),
			},
			memoryBytes: summarizeMemory(memorySnapshots),
			scheduler: {
				maximumActiveActorsObserved: controller.maximumActive,
				providerStarts: controller.starts.length,
				completedRuns,
				overflowErrorName,
				fairness,
				fifo,
			},
			events: sortedEventCounts(eventCounts),
			outcome: {
				status: passed ? "passed" : "failed",
				...(passed ? {} : { code: "runtime_scheduler_invariant_failed" }),
				checks,
			},
		};
	} catch (error) {
		durationsMs.total = performance.now() - benchmarkStartedAt;
		memorySnapshots.atFailure = process.memoryUsage();
		return {
			schemaVersion: 1,
			createdAt: new Date().toISOString(),
			revision: readRevision(),
			dirtyWorkingTree: hasWorkingTreeChanges(),
			config,
			environment,
			durationsMs,
			memoryBytes: summarizeMemory(memorySnapshots),
			progress: {
				phase,
				configuredRuns: totalConfiguredRuns,
				createdRunHandles: handles.length,
				admittedRuns: admissionCount,
				providerStarts: controller?.starts.length ?? 0,
				maximumActiveActorsObserved: controller?.maximumActive ?? 0,
				pendingProviderResponses: controller?.pendingCount ?? 0,
				releasedProviderResponses: releasedResponses,
				completedRunEvents: completionEventCount,
				settledRunHandles: settledHandleCount,
				overflowErrorName,
			},
			scheduler: {
				fairness: inspectRoundRobin(controller?.starts ?? [], config),
				fifo: inspectActorFifo(startedInputsByActor, config, runWidth),
			},
			events: sortedEventCounts(eventCounts),
			outcome: {
				status: "failed",
				code: benchmarkErrorCode(error),
				errorName: error instanceof Error ? error.name : typeof error,
			},
		};
	} finally {
		for (const stop of unsubscribe) stop();
		controller?.close();
	}
}

function createProviderController(dependencies) {
	let active = 0;
	let maximumActive = 0;
	let closed = false;
	const starts = [];
	const pending = [];
	const pendingWaiters = [];
	return {
		starts,
		get maximumActive() {
			return maximumActive;
		},
		get pendingCount() {
			return pending.length;
		},
		streamFor: (actorIndex) => {
			let callOrdinal = 0;
			return (_model, _context, options) => {
				const ordinal = callOrdinal++;
				const response = dependencies.createAssistantMessageEventStream();
				active += 1;
				maximumActive = Math.max(maximumActive, active);
				starts.push({ actorIndex, ordinal });
				response.push({ type: "start", partial: dependencies.assistant([], "pending") });
				let finished = false;
				const settle = (kind) => {
					if (finished) return;
					finished = true;
					active -= 1;
					if (kind === "done") {
						response.push({
							type: "done",
							reason: "stop",
							message: dependencies.assistant([{ type: "text", text: "done" }]),
						});
					} else {
						const aborted = { ...dependencies.assistant([], "aborted"), errorMessage: "benchmark closed" };
						response.push({ type: "error", reason: "aborted", error: aborted });
					}
				};
				const entry = { finish: () => settle("done"), abort: () => settle("aborted") };
				if (closed) entry.abort();
				else {
					pending.push(entry);
					notifyCountWaiters(pendingWaiters, pending.length);
					if (options?.signal?.aborted) entry.abort();
					else options?.signal?.addEventListener("abort", entry.abort, { once: true });
				}
				return response;
			};
		},
		waitForPending: (count) =>
			waitForCount(
				pendingWaiters,
				() => pending.length,
				count,
				dependencies.progressTimeoutMs,
				"provider_progress_timeout",
			),
		finishPending: (count) => {
			if (pending.length < count) throw new BenchmarkInvariantError("provider_wave_not_ready");
			const wave = pending.splice(0, count);
			for (const entry of wave) entry.finish();
			return wave.length;
		},
		close: () => {
			if (closed) return;
			closed = true;
			const abandoned = pending.splice(0);
			for (const entry of abandoned) entry.abort();
		},
	};
}

function inspectRoundRobin(starts, config) {
	const configuredStarts = starts.slice(0, config.actorCount * config.runsPerActor);
	let violatedRounds = 0;
	let firstViolatedRound = null;
	for (let round = 0; round < config.runsPerActor; round += 1) {
		const actors = new Set(
			configuredStarts
				.slice(round * config.actorCount, (round + 1) * config.actorCount)
				.map((start) => start.actorIndex),
		);
		if (actors.size === config.actorCount) continue;
		violatedRounds += 1;
		if (firstViolatedRound === null) firstViolatedRound = round;
	}
	return {
		valid: configuredStarts.length === config.actorCount * config.runsPerActor && violatedRounds === 0,
		inspectedRounds: config.runsPerActor,
		violatedRounds,
		firstViolatedRound,
	};
}

function inspectActorFifo(startedInputsByActor, config, runWidth) {
	let violatedActors = 0;
	let firstViolatedActor = null;
	let minimumStarts = Number.POSITIVE_INFINITY;
	let maximumStarts = 0;
	for (const [actorIndex, inputs] of startedInputsByActor.entries()) {
		minimumStarts = Math.min(minimumStarts, inputs.length);
		maximumStarts = Math.max(maximumStarts, inputs.length);
		const expected = Array.from({ length: config.runsPerActor }, (_, runIndex) => inputId(runIndex, runWidth));
		if (inputs.length === expected.length && inputs.every((input, index) => input === expected[index])) continue;
		violatedActors += 1;
		if (firstViolatedActor === null) firstViolatedActor = actorIndex;
	}
	return {
		valid: violatedActors === 0,
		inspectedActors: config.actorCount,
		violatedActors,
		firstViolatedActor,
		minimumStarts,
		maximumStarts,
	};
}

async function describeEnvironment(parseStrictJsonText) {
	const [runtimePackage, agentPackage, devtoolsPackage] = await Promise.all([
		readPackage("packages/runtime/package.json", parseStrictJsonText),
		readPackage("packages/agent/package.json", parseStrictJsonText),
		readPackage("packages/devtools/package.json", parseStrictJsonText),
	]);
	return {
		...basicEnvironment(),
		packages: {
			runtime: runtimePackage.version,
			agent: agentPackage.version,
			devtools: devtoolsPackage.version,
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

async function readPackage(path, parseStrictJsonText) {
	return parseStrictJsonText(await readFile(resolve(path), "utf8"), PACKAGE_JSON_MAX_DEPTH);
}

function summarizeMemory(snapshots) {
	const report = {};
	for (const [phase, snapshot] of Object.entries(snapshots)) {
		report[phase] = { rss: snapshot.rss, heapUsed: snapshot.heapUsed, external: snapshot.external };
	}
	return report;
}

function criticalEvents(eventCounts) {
	return {
		queued: eventCounts.get("run.queued") ?? 0,
		started: eventCounts.get("run.started") ?? 0,
		completed: eventCounts.get("run.completed") ?? 0,
	};
}

function sortedEventCounts(eventCounts) {
	return Object.fromEntries([...eventCounts.entries()].sort(([left], [right]) => left.localeCompare(right)));
}

function validateConfig(value) {
	assertRecord(value, "benchmark_config_invalid");
	assertExactKeys(value, ["profile", "purpose", "sessionId", ...NUMERIC_EVIDENCE_PATHS, "evidence"]);
	assertNonEmptyString(value.profile, "profile");
	assertNonEmptyString(value.purpose, "purpose");
	assertNonEmptyString(value.sessionId, "sessionId");
	for (const path of NUMERIC_EVIDENCE_PATHS) assertPositiveSafeInteger(value[path], path);
	if (value.progressTimeoutMs > NODE_MAX_TIMER_DELAY_MS) {
		throw new BenchmarkConfigError("progress_timeout_ms_exceeds_node_timer_limit");
	}
	if (value.maxActiveActors > value.actorCount) throw new BenchmarkConfigError("max_active_actors_exceeds_actor_count");
	const totalRuns = value.actorCount * value.runsPerActor;
	if (!Number.isSafeInteger(totalRuns) || value.maxQueuedRuns !== totalRuns) {
		throw new BenchmarkConfigError("max_queued_runs_must_equal_profile_run_count");
	}
	if (value.actorCount % value.maxActiveActors !== 0) {
		throw new BenchmarkConfigError("actor_count_must_fill_complete_concurrency_waves");
	}
	if (value.maxInputBytes !== Buffer.byteLength(JSON.stringify(null), "utf8")) {
		throw new BenchmarkConfigError("max_input_bytes_must_match_null_payload");
	}
	if (value.maxInputDepth !== 1) throw new BenchmarkConfigError("max_input_depth_must_be_positive_minimum");
	assertRecord(value.evidence, "numeric_evidence_invalid");
	assertExactKeys(value.evidence, NUMERIC_EVIDENCE_PATHS);
	for (const path of NUMERIC_EVIDENCE_PATHS) assertNonEmptyString(value.evidence[path], `evidence.${path}`);
	return Object.freeze({
		profile: value.profile.trim(),
		purpose: value.purpose.trim(),
		sessionId: value.sessionId.trim(),
		actorCount: value.actorCount,
		runsPerActor: value.runsPerActor,
		maxActiveActors: value.maxActiveActors,
		maxQueuedRuns: value.maxQueuedRuns,
		maxInputBytes: value.maxInputBytes,
		maxInputDepth: value.maxInputDepth,
		progressTimeoutMs: value.progressTimeoutMs,
		evidence: Object.freeze(
			Object.fromEntries(Object.entries(value.evidence).map(([key, evidence]) => [key, evidence.trim()])),
		),
	});
}

function parseArguments(arguments_) {
	const options = { help: false, configPath: undefined, outputPath: undefined };
	for (let index = 0; index < arguments_.length; index += 1) {
		const argument = arguments_[index];
		if (argument === "--help" || argument === "-h") options.help = true;
		else if (argument === "--config") options.configPath = requiredArgument(arguments_, ++index, "--config");
		else if (argument === "--output") options.outputPath = requiredArgument(arguments_, ++index, "--output");
		else throw new BenchmarkConfigError("benchmark_argument_invalid");
	}
	return options;
}

function requiredArgument(arguments_, index, option) {
	const value = arguments_[index];
	if (value === undefined || value.startsWith("--")) {
		throw new BenchmarkConfigError(`${option.slice(2)}_argument_required`);
	}
	return value;
}

function extractOutputPath(arguments_) {
	const index = arguments_.lastIndexOf("--output");
	const value = index < 0 ? undefined : arguments_[index + 1];
	return value === undefined || value.startsWith("--") ? undefined : value;
}

async function writeReport(outputPath, report) {
	const serialized = `${JSON.stringify(report, undefined, "\t")}\n`;
	if (outputPath === undefined) process.stdout.write(serialized);
	else {
		try {
			const resolved = resolve(outputPath);
			await mkdir(dirname(resolved), { recursive: true });
			await writeFile(resolved, serialized, "utf8");
		} catch (error) {
			throw new BenchmarkOutputError(error);
		}
	}
}

async function writeFailureReport(outputPath, report) {
	try {
		await writeReport(outputPath, report);
	} catch (error) {
		writeReportToStandardOutput(error instanceof BenchmarkOutputError ? withOutputFailure(report, error) : report);
	}
}

function writeReportToStandardOutput(report) {
	process.stdout.write(`${JSON.stringify(report, undefined, "\t")}\n`);
}

function withOutputFailure(report, error) {
	return {
		...report,
		benchmarkOutcome: report.outcome,
		output: { status: "failed", code: error.code, errorName: error.name },
		outcome: { status: "failed", code: error.code },
	};
}

function printHelp() {
	process.stdout.write(
		"Usage: node scripts/benchmark-runtime-scheduler.mjs --config <profile.json> [--output <report.json>]\n",
	);
}

function stream(sessionId, actorIdValue) {
	return { sessionId, actorId: actorIdValue };
}

function actorId(index, width) {
	return `actor-${String(index).padStart(width, "0")}`;
}

function inputId(index, width) {
	return `input-${String(index).padStart(width, "0")}`;
}

function waitForCount(waiters, readCount, target, timeoutMs, timeoutCode) {
	if (readCount() >= target) return Promise.resolve();
	return new Promise((resolvePromise, rejectPromise) => {
		let timer;
		const waiter = {
			target,
			notify: (count) => {
				clearTimeout(timer);
				if (count >= target) {
					removeWaiter(waiters, waiter);
					resolvePromise();
					return;
				}
				timer = setTimeout(() => {
					removeWaiter(waiters, waiter);
					rejectPromise(new BenchmarkInvariantError(timeoutCode));
				}, timeoutMs);
			},
		};
		waiters.push(waiter);
		waiter.notify(readCount());
	});
}

function notifyCountWaiters(waiters, count) {
	for (const waiter of [...waiters]) waiter.notify(count);
}

function removeWaiter(waiters, waiter) {
	const index = waiters.indexOf(waiter);
	if (index >= 0) waiters.splice(index, 1);
}

function readRevision() {
	return execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim();
}

function hasWorkingTreeChanges() {
	return execFileSync("git", ["status", "--porcelain"], { encoding: "utf8" }).length > 0;
}

function benchmarkErrorCode(error) {
	return error instanceof BenchmarkConfigError ||
		error instanceof BenchmarkInvariantError ||
		error instanceof BenchmarkOutputError
		? error.code
		: "runtime_scheduler_benchmark_failed";
}

function assertRecord(value, code) {
	if (value === null || typeof value !== "object" || Array.isArray(value)) throw new BenchmarkConfigError(code);
}

function assertExactKeys(value, expected) {
	const actual = Object.keys(value).sort();
	const wanted = [...expected].sort();
	if (actual.length !== wanted.length || actual.some((key, index) => key !== wanted[index])) {
		throw new BenchmarkConfigError("benchmark_config_keys_invalid");
	}
}

function assertNonEmptyString(value, name) {
	if (typeof value !== "string" || value.trim().length === 0) {
		throw new BenchmarkConfigError(`${name.replaceAll(".", "_")}_invalid`);
	}
}

function assertPositiveSafeInteger(value, name) {
	if (!Number.isSafeInteger(value) || value <= 0) {
		throw new BenchmarkConfigError(`${name.replaceAll(".", "_")}_invalid`);
	}
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

class BenchmarkOutputError extends Error {
	constructor(cause) {
		super("benchmark_report_write_failed", { cause });
		this.name = "BenchmarkOutputError";
		this.code = "benchmark_report_write_failed";
	}
}

await main();
