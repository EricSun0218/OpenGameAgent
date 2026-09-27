import { execFileSync, spawn } from "node:child_process";
import { readFile, stat, writeFile } from "node:fs/promises";
import { cpus, freemem, platform, release, totalmem } from "node:os";
import { dirname, isAbsolute, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { memoryUsage, resourceUsage } from "node:process";
import { performance } from "node:perf_hooks";

const CHILD_FLAG = "--bge-m3-case-child";
const RESULT_PREFIX = "OGA_BGE_M3_BENCHMARK_RESULT ";
const BGE_M3_DIMENSIONS = 1024;
const BGE_M3_MAX_TOKENS = 8192;
const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;

if (process.argv.includes(CHILD_FLAG)) {
	await runChild();
} else {
	await runParent();
}

async function runParent() {
	const options = parseArguments(process.argv.slice(2));
	if (options.help) {
		printHelp();
		return;
	}
	if (options.configPath === undefined) throw new Error("benchmark_config_required");

	const configPath = resolve(options.configPath);
	const config = validateConfig(JSON.parse(await readFile(configPath, "utf8")), dirname(configPath));
	const tokenizer = await loadTokenizer(config.artifacts.tokenizer.path, config.artifacts.tokenizerConfig.path);
	const texts = new Map(
		config.cases.map(({ tokenCount }) => [tokenCount, createExactTokenText(tokenizer, tokenCount)]),
	);
	const artifacts = await describeArtifacts(config.artifacts);
	const runtimePackages = await readRuntimePackages();
	const samples = [];

	for (const benchmarkCase of config.cases) {
		const text = texts.get(benchmarkCase.tokenCount);
		if (text === undefined) throw new Error("benchmark_text_generation_failed");
		for (let run = 1; run <= config.runs; run += 1) {
			const freeMemoryBeforeBytes = freemem();
			const result = await runCase({
				artifacts: config.artifacts,
				benchmarkCase,
				text,
				warmIterations: config.warmIterations,
				rssSampleIntervalMs: config.rssSampleIntervalMs,
				requestTimeoutMs: config.requestTimeoutMs,
				caseTimeoutMs: config.caseTimeoutMs,
				observeWindowsProcessMemory: config.observeWindowsProcessMemory,
			});
			samples.push({
				...result,
				run,
				textBytes: Buffer.byteLength(text, "utf8"),
				derivedBytes: deriveCaseBytes(benchmarkCase),
				freeMemoryBeforeBytes,
				freeMemoryAfterBytes: freemem(),
			});
		}
	}

	const output = {
		schemaVersion: 3,
		createdAt: new Date().toISOString(),
		revision: readRevision(),
		dirtyWorkingTree: hasWorkingTreeChanges(),
		host: {
			platform: platform(),
			release: release(),
			architecture: process.arch,
			cpu: cpus()[0]?.model ?? "unknown",
			logicalCpuCount: cpus().length,
			totalMemoryBytes: totalmem(),
			nodeVersion: process.version,
		},
		artifacts,
		runtimePackages,
		settings: {
			runs: config.runs,
			warmIterations: config.warmIterations,
			rssSampleIntervalMs: config.rssSampleIntervalMs,
			requestTimeoutMs: config.requestTimeoutMs,
			caseTimeoutMs: config.caseTimeoutMs,
			observeWindowsProcessMemory: config.observeWindowsProcessMemory,
			cases: config.cases,
		},
		samples,
	};
	const serialized = `${JSON.stringify(output, undefined, 2)}\n`;
	if (options.outputPath === undefined) {
		process.stdout.write(serialized);
	} else {
		await writeFile(resolve(options.outputPath), serialized, "utf8");
	}
}

async function runChild() {
	let input;
	let provider;
	let sampler;
	let baseline;
	let sampledMaxRssSnapshot;
	let phase = "setup";
	let phaseStart = performance.now();
	let coldFirstEmbedLatencyMs;
	const warmLatencyMs = [];
	let outputBytes;
	try {
		input = JSON.parse(await readStandardInput());
		const { BgeM3EmbeddingProvider } = await import("../packages/memory-bge-m3/dist/index.js");
		const texts = Array.from({ length: input.benchmarkCase.batchSize }, () => input.text);
		const textBytes = Buffer.byteLength(input.text, "utf8");
		provider = new BgeM3EmbeddingProvider({
			artifacts: input.artifacts,
			limits: {
				maxTextsPerBatch: input.benchmarkCase.batchSize,
				maxTextBytes: Math.max(1, textBytes),
				maxTokens: input.benchmarkCase.tokenCount,
				requestTimeoutMs: input.requestTimeoutMs,
				maxPendingRequests: 1,
			},
		});
		baseline = captureMemory();
		sampledMaxRssSnapshot = baseline;
		sampler = setInterval(() => {
			const current = captureMemory();
			if (current.wholeProcessRssBytes > sampledMaxRssSnapshot.wholeProcessRssBytes) {
				sampledMaxRssSnapshot = current;
			}
		}, input.rssSampleIntervalMs);
		sampler.unref();

		phase = "cold";
		phaseStart = performance.now();
		const coldStart = phaseStart;
		const coldVectors = await provider.embed(texts);
		const coldElapsedMs = performance.now() - coldStart;
		outputBytes = measureOutput(coldVectors, input.benchmarkCase.batchSize);
		coldFirstEmbedLatencyMs = coldElapsedMs;
		const afterCold = captureMemory();
		if (afterCold.wholeProcessRssBytes > sampledMaxRssSnapshot.wholeProcessRssBytes) {
			sampledMaxRssSnapshot = afterCold;
		}
		phase = "warm";
		for (let iteration = 0; iteration < input.warmIterations; iteration += 1) {
			phaseStart = performance.now();
			const warmStart = phaseStart;
			const vectors = await provider.embed(texts);
			const warmElapsedMs = performance.now() - warmStart;
			const warmOutputBytes = measureOutput(vectors, input.benchmarkCase.batchSize);
			warmLatencyMs.push(warmElapsedMs);
			outputBytes = warmOutputBytes;
			const current = captureMemory();
			if (current.wholeProcessRssBytes > sampledMaxRssSnapshot.wholeProcessRssBytes) {
				sampledMaxRssSnapshot = current;
			}
		}
		const afterWarm = captureMemory();
		phase = "dispose";
		phaseStart = performance.now();
		await provider.dispose();
		const afterDispose = captureMemory();
		emitChildResult({
			case: input.benchmarkCase,
			textBytes,
			outputBytes,
			coldFirstEmbedLatencyMs,
			warmLatencyMs,
			memory: createMemoryReport(baseline, sampledMaxRssSnapshot, { afterCold, afterWarm, afterDispose }),
		});
	} catch (error) {
		const code = typeof error === "object" && error !== null && "code" in error ? String(error.code) : "case_failed";
		const failedPhaseElapsedMs = performance.now() - phaseStart;
		const afterFailure = baseline === undefined ? undefined : captureMemory();
		let cleanupError;
		try {
			if (provider !== undefined) await provider.dispose();
		} catch {
			cleanupError = "dispose_failed";
		}
		const afterDispose = baseline === undefined ? undefined : captureMemory();
		emitChildResult({
			...(input?.benchmarkCase === undefined ? {} : { case: input.benchmarkCase }),
			error: code,
			failedPhase: phase,
			failedPhaseElapsedMs,
			...(cleanupError === undefined ? {} : { cleanupError }),
			completedWarmIterations: warmLatencyMs.length,
			...(coldFirstEmbedLatencyMs === undefined ? {} : { coldFirstEmbedLatencyMs }),
			warmLatencyMs,
			...(outputBytes === undefined ? {} : { outputBytes }),
			...(baseline === undefined ||
			sampledMaxRssSnapshot === undefined ||
			afterFailure === undefined ||
			afterDispose === undefined
				? {}
				: {
						memory: createMemoryReport(baseline, sampledMaxRssSnapshot, { afterFailure, afterDispose }),
					}),
		});
		process.exitCode = 1;
	} finally {
		if (sampler !== undefined) clearInterval(sampler);
	}
}

function runCase(input) {
	return new Promise((resolveCase) => {
		const child = spawn(process.execPath, [fileURLToPath(import.meta.url), CHILD_FLAG], {
			cwd: process.cwd(),
			stdio: ["pipe", "pipe", "pipe"],
			windowsHide: true,
		});
		let stdout = "";
		let settled = false;
		let caseTimedOut = false;
		const processObserver = observeProcessMemory(
			child.pid,
			input.rssSampleIntervalMs,
			input.observeWindowsProcessMemory,
		);
		const caseTimer = setTimeout(() => {
			if (child.exitCode === null && child.signalCode === null) caseTimedOut = child.kill();
		}, input.caseTimeoutMs);
		const finish = (result) => {
			if (settled) return;
			settled = true;
			clearTimeout(caseTimer);
			const observedProcessMemory = processObserver.stop();
			resolveCase({
				...result,
				...(observedProcessMemory === undefined ? {} : { observedProcessMemory }),
			});
		};
		child.stdout.setEncoding("utf8");
		child.stdout.on("data", (chunk) => {
			stdout += chunk;
		});
		child.stderr.resume();
		child.stdin.on("error", () => undefined);
		child.on("error", () => finish({ case: input.benchmarkCase, error: "child_spawn_failed" }));
		child.on("close", (code, signal) => {
			if (caseTimedOut) {
				finish({
					case: input.benchmarkCase,
					error: "case_timeout",
					exitCode: code,
					signal,
				});
				return;
			}
			const line = stdout.split(/\r?\n/u).findLast((candidate) => candidate.startsWith(RESULT_PREFIX));
			if (line === undefined) {
				finish({
					case: input.benchmarkCase,
					error: "child_exit",
					exitCode: code,
					signal,
				});
				return;
			}
			try {
				const result = JSON.parse(line.slice(RESULT_PREFIX.length));
				if (code !== 0 || result.error !== undefined) {
					finish({
						...result,
						case: input.benchmarkCase,
						error: result.error ?? "child_exit",
						exitCode: code,
						signal,
					});
					return;
				}
				finish(result);
			} catch {
				finish({
					case: input.benchmarkCase,
					error: "invalid_child_result",
					exitCode: code,
					signal,
				});
			}
		});
		child.stdin.end(JSON.stringify(input));
	});
}

function observeProcessMemory(processId, intervalMs, enabled) {
	if (!enabled) return { stop: () => undefined };
	if (process.platform !== "win32" || processId === undefined) {
		return { stop: () => ({ sampleCount: 0, unavailable: "unsupported_platform" }) };
	}
	let sampleCount = 0;
	let sampledMaxWorkingSetBytes = 0;
	let sampledMaxPrivateBytes = 0;
	let observerFailed = false;
	let stopped = false;
	let bufferedOutput = "";
	const script = `
$TargetProcessId = ${processId}
$IntervalMilliseconds = ${intervalMs}
while ($true) {
	$target = Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
	if ($null -eq $target) { break }
	[Console]::Out.WriteLine("$($target.WorkingSet64),$($target.PrivateMemorySize64)")
	Start-Sleep -Milliseconds $IntervalMilliseconds
}
`;
	const observer = spawn("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], {
		stdio: ["ignore", "pipe", "ignore"],
		windowsHide: true,
	});
	const acceptLines = (flush) => {
		const lines = bufferedOutput.split(/\r?\n/u);
		bufferedOutput = flush ? "" : (lines.pop() ?? "");
		for (const line of lines) {
			if (line.length === 0) continue;
			const [workingSetText, privateBytesText] = line.split(",");
			const workingSetBytes = Number(workingSetText);
			const privateBytes = Number(privateBytesText);
			if (!Number.isSafeInteger(workingSetBytes) || !Number.isSafeInteger(privateBytes)) {
				observerFailed = true;
				continue;
			}
			sampleCount += 1;
			sampledMaxWorkingSetBytes = Math.max(sampledMaxWorkingSetBytes, workingSetBytes);
			sampledMaxPrivateBytes = Math.max(sampledMaxPrivateBytes, privateBytes);
		}
	};
	observer.stdout.setEncoding("utf8");
	observer.stdout.on("data", (chunk) => {
		bufferedOutput += chunk;
		acceptLines(false);
	});
	observer.on("error", () => {
		observerFailed = true;
	});
	return {
		stop: () => {
			if (!stopped) {
				stopped = true;
				observer.kill();
				acceptLines(true);
			}
			if (sampleCount === 0) {
				return { sampleCount, unavailable: observerFailed ? "observer_failed" : "no_samples" };
			}
			return {
				sampleCount,
				sampledMaxWorkingSetBytes,
				sampledMaxPrivateBytes,
				...(observerFailed ? { observerError: "observer_failed" } : {}),
			};
		},
	};
}

async function loadTokenizer(tokenizerPath, tokenizerConfigPath) {
	const { Tokenizer } = await import("@huggingface/tokenizers");
	const [tokenizerJson, tokenizerConfig] = await Promise.all([
		readFile(tokenizerPath, "utf8"),
		readFile(tokenizerConfigPath, "utf8"),
	]);
	return new Tokenizer(JSON.parse(tokenizerJson), JSON.parse(tokenizerConfig));
}

function createExactTokenText(tokenizer, tokenCount) {
	const bodyTokenCount = tokenCount - 2;
	if (bodyTokenCount === 0) {
		const text = "";
		if (hasExactTokenContract(tokenizer, text, tokenCount)) return text;
		throw new Error("benchmark_text_generation_failed");
	}
	const candidates = [...tokenizer.get_vocab().entries()]
		.filter(([token]) => /^▁[A-Za-z]+$/u.test(token))
		.sort((left, right) => left[1] - right[1]);
	for (const [, tokenId] of candidates) {
		const probe = tokenizer.decode(Array.from({ length: Math.min(bodyTokenCount, 30) }, () => tokenId));
		if (tokenizer.encode(probe, { add_special_tokens: true }).ids.length !== Math.min(bodyTokenCount, 30) + 2) {
			continue;
		}
		const text = tokenizer.decode(Array.from({ length: bodyTokenCount }, () => tokenId));
		if (hasExactTokenContract(tokenizer, text, tokenCount)) return text;
	}
	throw new Error("benchmark_text_generation_failed");
}

function hasExactTokenContract(tokenizer, text, tokenCount) {
	const ids = tokenizer.encode(text, { add_special_tokens: true }).ids;
	return ids.length === tokenCount && ids[0] === 0 && ids.at(-1) === 2;
}

async function describeArtifacts(artifacts) {
	return Object.fromEntries(
		await Promise.all(
			Object.entries(artifacts).map(async ([name, artifact]) => [
				name,
				{ sha256: artifact.sha256, sizeBytes: (await stat(artifact.path)).size },
			]),
		),
	);
}

async function readRuntimePackages() {
	const packageJson = JSON.parse(await readFile(resolve("packages/memory-bge-m3/package.json"), "utf8"));
	return {
		memoryBgeM3: packageJson.version,
		onnxruntimeWeb: packageJson.dependencies["onnxruntime-web"],
		huggingfaceTokenizers: packageJson.dependencies["@huggingface/tokenizers"],
	};
}

function validateConfig(value, configDirectory) {
	if (typeof value !== "object" || value === null || Array.isArray(value)) throw new Error("invalid_benchmark_config");
	const artifacts = Object.fromEntries(
		["model", "tokenizer", "tokenizerConfig", "modelConfig"].map((name) => {
			const artifact = value.artifacts?.[name];
			if (
				typeof artifact?.path !== "string" ||
				artifact.path.length === 0 ||
				typeof artifact.sha256 !== "string" ||
				!/^[a-f\d]{64}$/iu.test(artifact.sha256)
			) {
				throw new Error("invalid_benchmark_config");
			}
			return [
				name,
				{
					path: isAbsolute(artifact.path) ? resolve(artifact.path) : resolve(configDirectory, artifact.path),
					sha256: artifact.sha256.toLowerCase(),
				},
			];
		}),
	);
	for (const field of ["runs", "rssSampleIntervalMs", "requestTimeoutMs", "caseTimeoutMs"]) {
		assertPositiveInteger(value[field]);
	}
	if (typeof value.observeWindowsProcessMemory !== "boolean") throw new Error("invalid_benchmark_config");
	assertNonNegativeInteger(value.warmIterations);
	if (
		value.rssSampleIntervalMs > NODE_MAX_TIMER_DELAY_MS ||
		value.requestTimeoutMs > NODE_MAX_TIMER_DELAY_MS ||
		value.caseTimeoutMs > NODE_MAX_TIMER_DELAY_MS
	) {
		throw new Error("invalid_benchmark_config");
	}
	if (!Array.isArray(value.cases) || value.cases.length === 0) throw new Error("invalid_benchmark_config");
	const cases = value.cases.map((benchmarkCase) => {
		assertPositiveInteger(benchmarkCase?.batchSize);
		assertPositiveInteger(benchmarkCase?.tokenCount);
		if (benchmarkCase.tokenCount < 2 || benchmarkCase.tokenCount > BGE_M3_MAX_TOKENS) {
			throw new Error("invalid_benchmark_config");
		}
		return { batchSize: benchmarkCase.batchSize, tokenCount: benchmarkCase.tokenCount };
	});
	return {
		artifacts,
		runs: value.runs,
		warmIterations: value.warmIterations,
		rssSampleIntervalMs: value.rssSampleIntervalMs,
		requestTimeoutMs: value.requestTimeoutMs,
		caseTimeoutMs: value.caseTimeoutMs,
		observeWindowsProcessMemory: value.observeWindowsProcessMemory,
		cases,
	};
}

function parseArguments(args) {
	let configPath;
	let outputPath;
	let help = false;
	for (let index = 0; index < args.length; index += 1) {
		const argument = args[index];
		if (argument === CHILD_FLAG) continue;
		if (argument === "--help" || argument === "-h") {
			help = true;
			continue;
		}
		if (argument === "--config") {
			configPath = args[++index];
			continue;
		}
		if (argument === "--output") {
			outputPath = args[++index];
			continue;
		}
		throw new Error("unknown_benchmark_argument");
	}
	return { configPath, outputPath, help };
}

function assertPositiveInteger(value) {
	if (!Number.isSafeInteger(value) || value < 1) throw new Error("invalid_benchmark_config");
}

function assertNonNegativeInteger(value) {
	if (!Number.isSafeInteger(value) || value < 0) throw new Error("invalid_benchmark_config");
}

function deriveCaseBytes(benchmarkCase) {
	const elements = BigInt(benchmarkCase.batchSize) * BigInt(benchmarkCase.tokenCount);
	const inputTensorBytes = elements * BigInt(BigInt64Array.BYTES_PER_ELEMENT) * 2n;
	const modelOutputTensorBytes = elements * BigInt(BGE_M3_DIMENSIONS) * BigInt(Float32Array.BYTES_PER_ELEMENT);
	const returnedVectorBytes =
		BigInt(benchmarkCase.batchSize) * BigInt(BGE_M3_DIMENSIONS) * BigInt(Float32Array.BYTES_PER_ELEMENT);
	const knownOrtCopyFloorBytes = 2n * (inputTensorBytes + modelOutputTensorBytes);
	return {
		paddedTokenElements: elements.toString(),
		inputTensorBytes: inputTensorBytes.toString(),
		modelOutputTensorBytes: modelOutputTensorBytes.toString(),
		knownOrtCopyFloorBytes: knownOrtCopyFloorBytes.toString(),
		returnedVectorBytes: returnedVectorBytes.toString(),
	};
}

function captureMemory() {
	const usage = memoryUsage();
	return {
		wholeProcessRssBytes: usage.rss,
		mainThreadHeapTotalBytes: usage.heapTotal,
		mainThreadHeapUsedBytes: usage.heapUsed,
		mainThreadExternalBytes: usage.external,
		mainThreadArrayBuffersBytes: usage.arrayBuffers,
	};
}

function createMemoryReport(baseline, sampledMaxRssSnapshot, phases) {
	let maximumObservedRssSnapshot = sampledMaxRssSnapshot;
	for (const snapshot of [baseline, ...Object.values(phases)]) {
		if (snapshot.wholeProcessRssBytes > maximumObservedRssSnapshot.wholeProcessRssBytes) {
			maximumObservedRssSnapshot = snapshot;
		}
	}
	return {
		baseline,
		maximumObservedRssSnapshot,
		cumulativeMaxRssBytes: resourceUsage().maxRSS * 1024,
		...phases,
	};
}

function emitChildResult(result) {
	process.stdout.write(`${RESULT_PREFIX}${JSON.stringify(result)}\n`);
}

function measureOutput(vectors, expectedBatchSize) {
	if (
		vectors.length !== expectedBatchSize ||
		vectors.some((vector) => !(vector instanceof Float32Array) || vector.length !== BGE_M3_DIMENSIONS)
	) {
		throw new Error("invalid_benchmark_output");
	}
	return vectors.reduce((total, vector) => total + vector.byteLength, 0);
}

function readRevision() {
	return execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim();
}

function hasWorkingTreeChanges() {
	return execFileSync("git", ["status", "--porcelain"], { encoding: "utf8" }).length > 0;
}

async function readStandardInput() {
	let input = "";
	for await (const chunk of process.stdin) input += chunk;
	return input;
}

function printHelp() {
	process.stdout.write(`Usage: node scripts/benchmark-bge-m3.mjs --config <path> [--output <path>]\n\n`);
	process.stdout.write(
		"The JSON config must provide pinned artifacts, runs, warmIterations, rssSampleIntervalMs, " +
			"requestTimeoutMs, caseTimeoutMs, observeWindowsProcessMemory, and non-empty cases of { batchSize, " +
			"tokenCount }. No numeric benchmark setting has a built-in default. Each run executes in a fresh child process.\n",
	);
}
