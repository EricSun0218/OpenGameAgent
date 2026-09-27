import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, rmdir, stat, writeFile } from "node:fs/promises";
import { cpus, platform, release, totalmem } from "node:os";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { performance } from "node:perf_hooks";
import { fileURLToPath } from "node:url";

const SCRIPT_DIRECTORY = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(SCRIPT_DIRECTORY, "..");
// The accepted config shape has one root object and only one nested object level.
const CONFIG_JSON_MAX_DEPTH = 2;
// Workspace package manifests used here nest exports conditions three objects deep.
const PACKAGE_JSON_MAX_DEPTH = 3;
const DATASET_VERSION = "unique-token-v1";
const FIXED_TIMESTAMP = "2026-01-01T00:00:00.000Z";
const MEMORY_LIMIT_NAMES = [
	"maxSearchTextBytes",
	"maxContentBytes",
	"maxTagsPerMemory",
	"maxTagBytes",
	"maxRecipientsPerMemory",
	"maxQueryTextBytes",
	"maxSelectorKinds",
	"maxSelectorTags",
	"maxLexicalCandidates",
	"maxVectorCandidates",
	"maxHydrateCandidates",
	"maxRecallResults",
	"maxIndexBatchSize",
	"maxTrackedIndexPartitions",
	"maxExportPageSize",
	"maxPurgeBatchSize",
];
const NUMERIC_EVIDENCE_PATHS = [
	"recordCount",
	"dimensions",
	"warmupQueryCount",
	"queryCount",
	"samplesPerQuery",
	"maxTextsPerRequest",
	"recallAtK",
	"minimumRecallAtK",
	"sqlite.busyTimeoutMs",
	"hnsw.connectivity",
	"hnsw.expansionAdd",
	"hnsw.expansionSearch",
	...MEMORY_LIMIT_NAMES.map((name) => `memoryLimits.${name}`),
];
const RECALL_STAGE_ORDER = ["lexical", "embedding", "vector", "fusion", "hydrate", "rerank"];

async function main() {
	try {
		const options = parseArguments(process.argv.slice(2));
		if (options.help) {
			printHelp();
			return;
		}
		if (options.configPath === undefined) throw new BenchmarkConfigError("benchmark_config_required");

		const configPath = resolve(options.configPath);
		const { parseStrictJsonText } = await import("../packages/protocol/dist/index.js");
		const config = validateConfig(
			parseStrictJsonText(await readFile(configPath, "utf8"), CONFIG_JSON_MAX_DEPTH),
			dirname(configPath),
		);
		const report = await runBenchmark(config, parseStrictJsonText);
		const serialized = `${JSON.stringify(report, undefined, "\t")}\n`;
		if (options.outputPath === undefined) process.stdout.write(serialized);
		else await writeFile(resolve(options.outputPath), serialized, "utf8");
		if (report.outcome.status === "failed") process.exitCode = 1;
	} catch (error) {
		const code =
			error instanceof BenchmarkConfigError || error instanceof BenchmarkInvariantError
				? error.code
				: "memory_recall_benchmark_failed";
		process.stdout.write(`${JSON.stringify({ outcome: { status: "failed", code } }, undefined, "\t")}\n`);
		process.exitCode = 1;
	}
}

async function runBenchmark(config, parseStrictJsonText) {
	const [{ GameMemory }, { UsearchMemoryIndex }, { SqliteAuthority, SqliteMemoryAuthority }] = await Promise.all([
		import("../packages/memory/dist/index.js"),
		import("../packages/memory-usearch/dist/index.js"),
		import("../packages/storage-sqlite/dist/index.js"),
	]);
	await mkdir(config.sqlite.databaseDirectory, { recursive: true });
	const temporaryRoot = await mkdtemp(join(config.sqlite.databaseDirectory, "opengameagent-memory-recall-"));
	const databasePath = join(temporaryRoot, "memory.sqlite3");
	let authority;
	try {
		authority = openAuthority(SqliteAuthority, databasePath, config.sqlite);
		let store = new SqliteMemoryAuthority(authority, config.memoryLimits);
		const writer = new GameMemory({ authority: store, now: () => FIXED_TIMESTAMP });
		const tokenWidth = String(config.recordCount - 1).length;
		for (let ordinal = 0; ordinal < config.recordCount; ordinal += 1) {
			await writer.remember(createDraft(ordinal, tokenWidth));
		}

		let runtime = createRecallRuntime({ GameMemory, UsearchMemoryIndex }, store, config);
		let startedAt = performance.now();
		const initialRebuild = await runtime.memory.rebuild(memoryPartition());
		const initialRebuildDurationMs = performance.now() - startedAt;
		assertRebuild(initialRebuild, config.recordCount, config.recordCount, "initial_rebuild_invariant_failed");
		runtime.forbidRecallScans();

		const selected = selectQueryOrdinals(config.recordCount, config.warmupQueryCount + config.queryCount, config.seed);
		const warmupOrdinals = selected.slice(0, config.warmupQueryCount);
		const queryOrdinals = selected.slice(config.warmupQueryCount);
		await runWarmup(runtime.memory, warmupOrdinals, tokenWidth, config.recallAtK);
		const beforeRestart = [];
		for (const ordinal of queryOrdinals) {
			beforeRestart.push(
				projectRecall(await recall(runtime.memory, ordinal, tokenWidth, config.recallAtK), memoryId(ordinal)),
			);
		}
		const firstRecallScanCalls = runtime.forbiddenScanCalls();

		authority.close();
		authority = undefined;
		runtime = undefined;

		startedAt = performance.now();
		authority = openAuthority(SqliteAuthority, databasePath, config.sqlite);
		store = new SqliteMemoryAuthority(authority, config.memoryLimits);
		const restartedRuntime = createRecallRuntime({ GameMemory, UsearchMemoryIndex }, store, config);
		const reopenDurationMs = performance.now() - startedAt;
		startedAt = performance.now();
		const restartedRebuild = await restartedRuntime.memory.rebuild(memoryPartition());
		const restartedRebuildDurationMs = performance.now() - startedAt;
		assertRebuild(restartedRebuild, config.recordCount, 0, "restart_rebuild_invariant_failed");
		restartedRuntime.forbidRecallScans();
		await runWarmup(restartedRuntime.memory, warmupOrdinals, tokenWidth, config.recallAtK);

		const stageSamples = [];
		let relevantQueries = 0;
		let measuredQueries = 0;
		let restartEquivalent = true;
		for (let sample = 0; sample < config.samplesPerQuery; sample += 1) {
			for (const [queryIndex, ordinal] of queryOrdinals.entries()) {
				const result = await recall(restartedRuntime.memory, ordinal, tokenWidth, config.recallAtK);
				stageSamples.push(...result.stages);
				measuredQueries += 1;
				if (result.hits.some((hit) => hit.record.memoryId === memoryId(ordinal))) relevantQueries += 1;
				if (sample === 0 && !sameRecall(beforeRestart[queryIndex], projectRecall(result, memoryId(ordinal)))) {
					restartEquivalent = false;
				}
			}
		}
		const recallScanActiveCalls = firstRecallScanCalls + restartedRuntime.forbiddenScanCalls();
		if (!restartEquivalent) throw new BenchmarkInvariantError("restart_results_changed");
		if (recallScanActiveCalls !== 0) throw new BenchmarkInvariantError("recall_used_scan_active");

		authority.close();
		authority = undefined;
		const databaseBytes = await measureDatabaseBytes(databasePath);
		const recallAtK = relevantQueries / measuredQueries;
		const recallAccepted = recallAtK >= config.minimumRecallAtK;
		return {
			config: reportableConfig(config),
			environment: await describeEnvironment(parseStrictJsonText),
			databaseBytes,
			seed: config.seed,
			durationsMs: {
				reopen: reopenDurationMs,
				rebuild: { initial: initialRebuildDurationMs, restarted: restartedRebuildDurationMs },
			},
			stages: { summaryMethod: "nearest-rank", metrics: summarizeStages(stageSamples) },
			recallAtK: {
				k: config.recallAtK,
				minimum: config.minimumRecallAtK,
				relevantQueries,
				measuredQueries,
				value: recallAtK,
			},
			rebuild: { initial: initialRebuild, restarted: restartedRebuild },
			restartEquivalence: { equivalent: restartEquivalent, comparedQueries: config.queryCount },
			outcome: {
				status: recallAccepted ? "passed" : "failed",
				...(recallAccepted ? {} : { code: "recall_below_configured_minimum" }),
				sameOwnerTimelineGeneration: true,
				secondRebuildEmbeddedZero: restartedRebuild.embedded === 0,
				recallScanActiveCalls,
			},
		};
	} finally {
		authority?.close();
		await removeDatabaseFiles(databasePath);
		await rmdir(temporaryRoot);
	}
}

function openAuthority(SqliteAuthority, databasePath, sqlite) {
	return new SqliteAuthority({
		path: databasePath,
		busyTimeoutMs: sqlite.busyTimeoutMs,
		verifyOnOpen: sqlite.verifyOnOpen,
		ownership: sqlite.ownership,
		now: () => FIXED_TIMESTAMP,
	});
}

function createRecallRuntime(dependencies, store, config) {
	let scansAllowed = true;
	let forbiddenScanCalls = 0;
	const scanActive = store.scanActive.bind(store);
	store.scanActive = (...arguments_) => {
		if (!scansAllowed) {
			forbiddenScanCalls += 1;
			throw new BenchmarkInvariantError("recall_used_scan_active");
		}
		return scanActive(...arguments_);
	};
	const embeddingProvider = new DeterministicEmbeddingProvider(config);
	const vectorIndex = new dependencies.UsearchMemoryIndex({
		modelId: embeddingProvider.modelId,
		dimensions: embeddingProvider.dimensions,
		connectivity: config.hnsw.connectivity,
		expansionAdd: config.hnsw.expansionAdd,
		expansionSearch: config.hnsw.expansionSearch,
	});
	return {
		memory: new dependencies.GameMemory({
			authority: store,
			embeddingProvider,
			vectorIndex,
			now: () => FIXED_TIMESTAMP,
		}),
		forbidRecallScans: () => {
			scansAllowed = false;
		},
		forbiddenScanCalls: () => forbiddenScanCalls,
	};
}

class DeterministicEmbeddingProvider {
	constructor(config) {
		this.modelId = config.modelId;
		this.dimensions = config.dimensions;
		this.maxTextsPerRequest = config.maxTextsPerRequest;
		this.seed = config.seed;
	}

	async embed(texts, signal) {
		if (signal?.aborted) throw signal.reason;
		if (!Array.isArray(texts) || texts.length > this.maxTextsPerRequest) {
			throw new BenchmarkInvariantError("embedding_batch_exceeded_config");
		}
		const vectors = [];
		for (const text of texts) {
			if (signal?.aborted) throw signal.reason;
			vectors.push(createDeterministicVector(this.seed, text, this.dimensions));
		}
		return vectors;
	}
}

function createDeterministicVector(seed, text, dimensions) {
	const identity = syntheticEmbeddingIdentity(text);
	let state = createHash("sha256").update(seed).update("\0").update(identity).digest().readUInt32LE(0) | 1;
	const vector = new Float32Array(dimensions);
	let squaredMagnitude = 0;
	for (let index = 0; index < dimensions; index += 1) {
		// Marsaglia xorshift32 supplies a stable synthetic fixture, not a production embedding model.
		state ^= state << 13;
		state ^= state >>> 17;
		state ^= state << 5;
		const value = (state >>> 0) / 0xffff_ffff - 0.5;
		vector[index] = value;
		squaredMagnitude += value * value;
	}
	const magnitude = Math.sqrt(squaredMagnitude);
	for (let index = 0; index < dimensions; index += 1) vector[index] = vector[index] / magnitude;
	return vector;
}

function syntheticEmbeddingIdentity(text) {
	const matched = /^benchmark(?:memory|query)(\d+)$/u.exec(text);
	if (matched?.[1] === undefined) throw new BenchmarkInvariantError("invalid_synthetic_embedding_text");
	return `ordinal:${matched[1].replace(/^0+(?=\d)/u, "")}`;
}

function createDraft(ordinal, tokenWidth) {
	return {
		memoryId: memoryId(ordinal),
		partition: { ownerId: "benchmark-owner", scope: "benchmark" },
		kind: "benchmark-memory",
		tags: [],
		gameTime: { timelineId: "benchmark-timeline", generation: "1", tick: String(ordinal + 1) },
		importance: 0,
		searchText: memoryToken(ordinal, tokenWidth),
		content: { ordinal },
		audience: { visibility: "owner" },
	};
}

function memoryPartition() {
	return { ownerId: "benchmark-owner", timelineId: "benchmark-timeline", generation: "1" };
}

function memoryId(ordinal) {
	return `benchmark-memory-${ordinal}`;
}

function memoryToken(ordinal, width) {
	return `benchmarkmemory${String(ordinal).padStart(width, "0")}`;
}

function queryToken(ordinal, width) {
	return `benchmarkquery${String(ordinal).padStart(width, "0")}`;
}

function recall(memory, ordinal, tokenWidth, resultLimit) {
	return memory.recall({
		query: queryToken(ordinal, tokenWidth),
		selector: memoryPartition(),
		viewer: { kind: "owner", ownerId: "benchmark-owner" },
		resultLimit,
		vectorFailure: "fail",
	});
}

async function runWarmup(memory, ordinals, tokenWidth, resultLimit) {
	for (const ordinal of ordinals) await recall(memory, ordinal, tokenWidth, resultLimit);
}

function projectRecall(result, relevantMemoryId) {
	return {
		vectorStatus: result.vectorStatus,
		hitCount: result.hits.length,
		relevantMemoryId: result.hits.find((hit) => hit.record.memoryId === relevantMemoryId)?.record.memoryId,
	};
}

function sameRecall(left, right) {
	return (
		left.vectorStatus === right.vectorStatus &&
		left.hitCount === right.hitCount &&
		left.relevantMemoryId === right.relevantMemoryId
	);
}

function selectQueryOrdinals(recordCount, selectedCount, seed) {
	const ordinals = Array.from({ length: recordCount }, (_, index) => index);
	let state = createHash("sha256").update(seed).update("\0query-selection").digest().readUInt32LE(0) | 1;
	for (let index = ordinals.length - 1; index > 0; index -= 1) {
		state ^= state << 13;
		state ^= state >>> 17;
		state ^= state << 5;
		const target = (state >>> 0) % (index + 1);
		[ordinals[index], ordinals[target]] = [ordinals[target], ordinals[index]];
	}
	return ordinals.slice(0, selectedCount);
}

function summarizeStages(samples) {
	const grouped = new Map();
	for (const sample of samples) {
		const group = grouped.get(sample.stage) ?? { durationMs: [], inputCount: [], outputCount: [], outcomeCounts: {} };
		group.durationMs.push(sample.durationMs);
		group.inputCount.push(sample.inputCount);
		group.outputCount.push(sample.outputCount);
		group.outcomeCounts[sample.outcome] = (group.outcomeCounts[sample.outcome] ?? 0) + 1;
		grouped.set(sample.stage, group);
	}
	return RECALL_STAGE_ORDER.flatMap((stage) => {
		const group = grouped.get(stage);
		return group === undefined
			? []
			: [
					{
						stage,
						samples: group.durationMs.length,
						durationMs: summarizeNumbers(group.durationMs),
						inputCount: summarizeNumbers(group.inputCount),
						outputCount: summarizeNumbers(group.outputCount),
						outcomeCounts: group.outcomeCounts,
					},
				];
	});
}

function summarizeNumbers(values) {
	const sorted = values.toSorted((left, right) => left - right);
	return { median: nearestRank(sorted, 0.5), p95: nearestRank(sorted, 0.95), max: sorted.at(-1) };
}

function nearestRank(sorted, quantile) {
	return sorted[Math.ceil(sorted.length * quantile) - 1];
}

function assertRebuild(result, indexed, embedded, code) {
	if (result.indexed !== indexed || result.embedded !== embedded) throw new BenchmarkInvariantError(code);
}

async function measureDatabaseBytes(databasePath) {
	const main = await fileSize(databasePath);
	const writeAheadLog = await fileSize(`${databasePath}-wal`);
	const sharedMemory = await fileSize(`${databasePath}-shm`);
	return { main, writeAheadLog, sharedMemory, total: main + writeAheadLog + sharedMemory };
}

async function fileSize(path) {
	try {
		return (await stat(path)).size;
	} catch (error) {
		if (error?.code === "ENOENT") return 0;
		throw error;
	}
}

async function removeDatabaseFiles(databasePath) {
	await Promise.all([
		rm(databasePath, { force: true }),
		rm(`${databasePath}-wal`, { force: true }),
		rm(`${databasePath}-shm`, { force: true }),
	]);
}

async function describeEnvironment(parseStrictJsonText) {
	const [memoryPackage, sqlitePackage, usearchPackage] = await Promise.all([
		readPackage("packages/memory/package.json", parseStrictJsonText),
		readPackage("packages/storage-sqlite/package.json", parseStrictJsonText),
		readPackage("packages/memory-usearch/package.json", parseStrictJsonText),
	]);
	return {
		platform: platform(),
		release: release(),
		architecture: process.arch,
		cpu: cpus()[0]?.model ?? "unknown",
		logicalCpuCount: cpus().length,
		totalMemoryBytes: totalmem(),
		nodeVersion: process.version,
		packages: {
			memory: memoryPackage.version,
			betterSqlite3: sqlitePackage.dependencies["better-sqlite3"],
			memoryUsearch: usearchPackage.version,
			usearch: usearchPackage.dependencies.usearch,
		},
	};
}

async function readPackage(relativePath, parseStrictJsonText) {
	return parseStrictJsonText(await readFile(resolve(REPOSITORY_ROOT, relativePath), "utf8"), PACKAGE_JSON_MAX_DEPTH);
}

function reportableConfig(config) {
	return {
		profile: config.profile,
		purpose: config.purpose,
		datasetVersion: config.datasetVersion,
		modelId: config.modelId,
		recordCount: config.recordCount,
		dimensions: config.dimensions,
		warmupQueryCount: config.warmupQueryCount,
		queryCount: config.queryCount,
		samplesPerQuery: config.samplesPerQuery,
		maxTextsPerRequest: config.maxTextsPerRequest,
		recallAtK: config.recallAtK,
		minimumRecallAtK: config.minimumRecallAtK,
		hnsw: config.hnsw,
		sqlite: {
			busyTimeoutMs: config.sqlite.busyTimeoutMs,
			verifyOnOpen: config.sqlite.verifyOnOpen,
			ownership: config.sqlite.ownership,
			databaseDirectory: "configured-but-not-reported",
		},
		memoryLimits: config.memoryLimits,
		evidence: config.evidence,
	};
}

function validateConfig(value, configDirectory) {
	const root = requireObject(value, "root");
	const sqliteValue = requireObject(root.sqlite, "sqlite");
	const hnswValue = requireObject(root.hnsw, "hnsw");
	const memoryLimitsValue = requireObject(root.memoryLimits, "memoryLimits");
	const evidenceValue = requireObject(root.evidence, "evidence");
	const memoryLimits = Object.fromEntries(
		MEMORY_LIMIT_NAMES.map((name) => [name, requirePositiveInteger(memoryLimitsValue[name], `memoryLimits.${name}`)]),
	);
	const evidence = Object.fromEntries(
		NUMERIC_EVIDENCE_PATHS.map((path) => [path, requireString(evidenceValue[path], `evidence.${path}`)]),
	);
	const config = {
		profile: requireString(root.profile, "profile"),
		purpose: requireString(root.purpose, "purpose"),
		datasetVersion: requireString(root.datasetVersion, "datasetVersion"),
		modelId: requireString(root.modelId, "modelId"),
		seed: requireString(root.seed, "seed"),
		recordCount: requirePositiveInteger(root.recordCount, "recordCount"),
		dimensions: requirePositiveInteger(root.dimensions, "dimensions"),
		warmupQueryCount: requireNonNegativeInteger(root.warmupQueryCount, "warmupQueryCount"),
		queryCount: requirePositiveInteger(root.queryCount, "queryCount"),
		samplesPerQuery: requirePositiveInteger(root.samplesPerQuery, "samplesPerQuery"),
		maxTextsPerRequest: requirePositiveInteger(root.maxTextsPerRequest, "maxTextsPerRequest"),
		recallAtK: requirePositiveInteger(root.recallAtK, "recallAtK"),
		minimumRecallAtK: requirePositiveUnitInterval(root.minimumRecallAtK, "minimumRecallAtK"),
		hnsw: {
			connectivity: requireConnectivity(hnswValue.connectivity, "hnsw.connectivity"),
			expansionAdd: requireNonNegativeInteger(hnswValue.expansionAdd, "hnsw.expansionAdd"),
			expansionSearch: requireNonNegativeInteger(hnswValue.expansionSearch, "hnsw.expansionSearch"),
		},
		sqlite: {
			databaseDirectory: resolveConfiguredDirectory(
				requireString(sqliteValue.databaseDirectory, "sqlite.databaseDirectory"),
				configDirectory,
			),
			busyTimeoutMs: requireNonNegativeInteger(sqliteValue.busyTimeoutMs, "sqlite.busyTimeoutMs"),
			verifyOnOpen: requireEnum(sqliteValue.verifyOnOpen, ["full", "quick"], "sqlite.verifyOnOpen"),
			ownership: requireEnum(sqliteValue.ownership, ["shared", "exclusive"], "sqlite.ownership"),
		},
		memoryLimits,
		evidence,
	};
	if (config.datasetVersion !== DATASET_VERSION) invalidConfig("datasetVersion");
	if (config.warmupQueryCount + config.queryCount > config.recordCount) invalidConfig("query_selection_count");
	if (config.recallAtK > config.memoryLimits.maxRecallResults) invalidConfig("recallAtK");
	return config;
}

function requireObject(value, field) {
	if (typeof value !== "object" || value === null || Array.isArray(value)) invalidConfig(field);
	return value;
}

function requireString(value, field) {
	if (typeof value !== "string" || value.trim().length === 0) invalidConfig(field);
	return value;
}

function requirePositiveInteger(value, field) {
	if (!Number.isSafeInteger(value) || value < 1) invalidConfig(field);
	return value;
}

function requireNonNegativeInteger(value, field) {
	if (!Number.isSafeInteger(value) || value < 0) invalidConfig(field);
	return value;
}

function requireConnectivity(value, field) {
	const connectivity = requireNonNegativeInteger(value, field);
	if (connectivity === 1) invalidConfig(field);
	return connectivity;
}

function requirePositiveUnitInterval(value, field) {
	if (typeof value !== "number" || !Number.isFinite(value) || value <= 0 || value > 1) invalidConfig(field);
	return value;
}

function requireEnum(value, values, field) {
	if (!values.includes(value)) invalidConfig(field);
	return value;
}

function resolveConfiguredDirectory(value, configDirectory) {
	return isAbsolute(value) ? resolve(value) : resolve(configDirectory, value);
}

function invalidConfig(field) {
	throw new BenchmarkConfigError(`invalid_benchmark_config:${field}`);
}

function parseArguments(arguments_) {
	let configPath;
	let outputPath;
	let help = false;
	for (let index = 0; index < arguments_.length; index += 1) {
		const argument = arguments_[index];
		if (argument === "--help" || argument === "-h") {
			help = true;
			continue;
		}
		if (argument === "--config") {
			configPath = arguments_[++index];
			continue;
		}
		if (argument === "--output") {
			outputPath = arguments_[++index];
			continue;
		}
		throw new BenchmarkConfigError("unknown_benchmark_argument");
	}
	return { configPath, outputPath, help };
}

function printHelp() {
	process.stdout.write(
		"Usage: node scripts/benchmark-memory-recall.mjs --config <path> [--output <path>]\n\n" +
			"The strict-JSON config must explicitly provide profile, purpose, datasetVersion=unique-token-v1, " +
			"modelId, a string seed, recordCount, dimensions, warmupQueryCount, queryCount, samplesPerQuery, " +
			"maxTextsPerRequest, recallAtK, minimumRecallAtK, HNSW connectivity/expansionAdd/expansionSearch, SQLite " +
			"databaseDirectory/busyTimeoutMs/verifyOnOpen/ownership, every " +
			"MemoryLimits field, and a non-empty evidence string for every numeric setting. There are no numeric defaults. " +
			"Build the workspace before running the benchmark.\n",
	);
}

class BenchmarkConfigError extends TypeError {
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

await main();
