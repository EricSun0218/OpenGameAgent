import { Buffer, constants as bufferConstants } from "node:buffer";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { availableParallelism, cpus, platform, release, tmpdir, totalmem } from "node:os";
import { dirname, join, resolve } from "node:path";
import { performance } from "node:perf_hooks";
import sharp from "sharp";
import { BenchmarkProgressError, withAbortableProgress, withSharpProgress } from "./benchmark-progress.mjs";
import { withCleanupFailure, withOutputFailure } from "./benchmark-report.mjs";

const CONFIG_JSON_MAX_DEPTH = 3;
const PACKAGE_JSON_MAX_DEPTH = 3;
const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const SQLITE_INT_MAX = 2_147_483_647;
const SOURCE_NAME = "benchmark-source.png";
const JOB_ID = "media-size-job";
const ADAPTER_ID = "benchmark-adapter";
const MEDIA_TYPE = "application/octet-stream";
const FIXED_TIME = 1_800_000_000_000;
const IMAGE_KEYS = ["width", "height", "channels"];
const MEDIA_KEYS = ["resourceBytes", "chunkBytes"];
const ATTACHMENT_LIMIT_KEYS = [
	"maxInputBytes",
	"maxInputPixels",
	"maxInputDimension",
	"maxStoredBytes",
	"maxStoredPixels",
	"maxStoredDimension",
	"maxReadBytes",
	"maxNameBytes",
	"maxImagesPerBatch",
	"maxBatchInputBytes",
	"maxBatchStoredBytes",
];
const MEDIA_LIMIT_KEYS = [
	"maxIdentifierBytes",
	"maxAdapterIdBytes",
	"maxAdapters",
	"maxRequestBytes",
	"maxJsonDepth",
	"maxProviderHandleBytes",
	"maxAttemptsPerJob",
	"maxOutputsPerAttempt",
	"maxResourceBytes",
	"maxBundleBytes",
	"maxManifestBytes",
	"maxMediaTypeBytes",
];
const SQLITE_KEYS = ["busyTimeoutMs", "verifyOnOpen", "ownership", "maxStoredJobBytes"];
const ROOT_KEYS = [
	"profile",
	"purpose",
	"image",
	"media",
	"progressTimeoutMs",
	"maxMaterializedBytes",
	"attachmentLimits",
	"mediaLimits",
	"sqlite",
	"evidence",
];
const NUMERIC_EVIDENCE_PATHS = [
	...IMAGE_KEYS.map((name) => `image.${name}`),
	...MEDIA_KEYS.map((name) => `media.${name}`),
	"progressTimeoutMs",
	"maxMaterializedBytes",
	...ATTACHMENT_LIMIT_KEYS.map((name) => `attachmentLimits.${name}`),
	...MEDIA_LIMIT_KEYS.map((name) => `mediaLimits.${name}`),
	"sqlite.busyTimeoutMs",
	"sqlite.maxStoredJobBytes",
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
		const failure = minimalFailureReport(error);
		if (error instanceof BenchmarkOutputError) writeReportToStandardOutput(failure);
		else await writeFailureReport(fallbackOutputPath, failure);
		process.exitCode = 1;
	}
}

async function runBenchmark(config, parseStrictJsonText) {
	const benchmarkStartedAt = performance.now();
	const durationsMs = {};
	const memorySnapshots = { before: process.memoryUsage() };
	const counters = { providerStarts: 0, restartProviderStarts: 0, emittedChunks: 0 };
	let phase = "environment";
	let environment = basicEnvironment();
	let authority;
	let temporaryRoot;
	let report;
	try {
		environment = await describeEnvironment(parseStrictJsonText);
		phase = "dependencies";
		const [
			{ LocalImageAttachmentStore },
			{ LocalGeneratedResourceStore, MediaJobService, MediaCallAdmissionGroup },
			storage,
		] = await Promise.all([
			import("../packages/attachments/dist/index.js"),
			import("../packages/media/dist/index.js"),
			import("../packages/storage-sqlite/dist/index.js"),
		]);
		temporaryRoot = await mkdtemp(join(tmpdir(), "oga-media-size-"));
		const attachmentRoot = join(temporaryRoot, "attachments");
		const resourceRoot = join(temporaryRoot, "resources");
		const databasePath = join(temporaryRoot, "authority.sqlite");

		phase = "image_generation";
		const imageGenerationStartedAt = performance.now();
		const sourcePipeline = sharp({
			create: {
				width: config.image.width,
				height: config.image.height,
				channels: config.image.channels,
				background: config.image.channels === 4 ? { r: 23, g: 67, b: 149, alpha: 0.5 } : { r: 23, g: 67, b: 149 },
			},
		}).png({ compressionLevel: 9, adaptiveFiltering: false, palette: false });
		const source = await withSharpProgress(
			sourcePipeline,
			() => sourcePipeline.toBuffer(),
			config.progressTimeoutMs,
			"image_generation_progress_timeout",
		);
		if (source.byteLength > config.attachmentLimits.maxInputBytes || source.byteLength > config.maxMaterializedBytes) {
			throw new BenchmarkInvariantError("generated_image_exceeds_input_budget");
		}
		durationsMs.imageGeneration = performance.now() - imageGenerationStartedAt;
		memorySnapshots.afterImageGeneration = process.memoryUsage();

		phase = "attachment_normalization";
		const attachmentStartedAt = performance.now();
		const attachmentStore = await LocalImageAttachmentStore.open({
			rootDirectory: attachmentRoot,
			limits: config.attachmentLimits,
		});
		const admitted = await withAbortableProgress(
			(signal) =>
				attachmentStore.admit([{ encoding: "bytes", data: source, mediaType: "image/png", name: SOURCE_NAME }], signal),
			config.progressTimeoutMs,
			"attachment_admission_progress_timeout",
		);
		const attachment = admitted[0];
		if (!attachment || admitted.length !== 1) throw new BenchmarkInvariantError("attachment_cardinality_mismatch");
		const warmAttachmentBytes = await withAbortableProgress(
			(signal) => attachmentStore.read(attachment, signal),
			config.progressTimeoutMs,
			"attachment_warm_read_progress_timeout",
		);
		if (warmAttachmentBytes.byteLength > config.maxMaterializedBytes) {
			throw new BenchmarkInvariantError("warm_attachment_exceeds_materialization_budget");
		}
		const attachmentDigest = createHash("sha256").update(warmAttachmentBytes).digest("hex");
		const reopenedAttachments = await LocalImageAttachmentStore.open({
			rootDirectory: attachmentRoot,
			limits: config.attachmentLimits,
		});
		const coldAttachmentBytes = await withAbortableProgress(
			(signal) => reopenedAttachments.read(attachment, signal),
			config.progressTimeoutMs,
			"attachment_cold_read_progress_timeout",
		);
		if (coldAttachmentBytes.byteLength > config.maxMaterializedBytes) {
			throw new BenchmarkInvariantError("cold_attachment_exceeds_materialization_budget");
		}
		durationsMs.attachmentNormalizationAndColdRead = performance.now() - attachmentStartedAt;
		memorySnapshots.afterAttachment = process.memoryUsage();

		phase = "image_decode_verification";
		const decodeStartedAt = performance.now();
		const decodePipeline = sharp(coldAttachmentBytes, {
			failOn: "warning",
			limitInputPixels: config.attachmentLimits.maxStoredPixels,
		}).raw();
		let decoded = await withSharpProgress(
			decodePipeline,
			() => decodePipeline.toBuffer({ resolveWithObject: true }),
			config.progressTimeoutMs,
			"image_decode_progress_timeout",
		);
		const decodedBytes = decoded.data.byteLength;
		const decodedInfo = { ...decoded.info };
		decoded = undefined;
		durationsMs.imageDecodeVerification = performance.now() - decodeStartedAt;
		memorySnapshots.afterImageDecode = process.memoryUsage();

		phase = "media_submission";
		const resourceStore = await LocalGeneratedResourceStore.open({ rootDirectory: resourceRoot });
		authority = new storage.SqliteAuthority({
			path: databasePath,
			busyTimeoutMs: config.sqlite.busyTimeoutMs,
			verifyOnOpen: config.sqlite.verifyOnOpen,
			ownership: config.sqlite.ownership,
		});
		const mediaStore = new storage.SqliteMediaJobStore(authority, {
			limits: config.mediaLimits,
			maxStoredJobBytes: config.sqlite.maxStoredJobBytes,
		});
		const expectedHash = createHash("sha256");
		const provider = {
			start: async (_request, context) => {
				counters.providerStarts += 1;
				return {
					status: "succeeded",
					resources: [
						{
							ordinal: 0,
							kind: "file",
							mediaType: MEDIA_TYPE,
							declaredBytes: config.media.resourceBytes,
							bytes: deterministicResourceStream(config, context.signal, expectedHash, counters),
						},
					],
				};
			},
		};
		const mediaService = new MediaJobService({
			adapters: [
				{
					adapterId: ADAPTER_ID,
					provider,
					admission: new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 0 }),
				},
			],
			store: mediaStore,
			resources: resourceStore,
			limits: config.mediaLimits,
			newId: idFactory(),
			now: incrementalClock(),
		});
		const mediaStartedAt = performance.now();
		const submitted = await withAbortableProgress(
			(signal) =>
				mediaService.submit(ADAPTER_ID, { jobId: JOB_ID, kind: "file", input: { profile: config.profile } }, signal),
			config.progressTimeoutMs,
			"media_submission_progress_timeout",
		);
		const warmBundle = await mediaService.readBundle(JOB_ID, submitted.bundleId ?? "missing");
		const warmResource = warmBundle?.resources[0];
		if (!warmBundle || !warmResource) throw new BenchmarkInvariantError("media_bundle_missing");
		const warmStoredJob = await mediaStore.read(JOB_ID);
		if (!warmStoredJob) throw new BenchmarkInvariantError("media_job_missing");
		const manifestBytes = jsonBytes(warmBundle);
		const storedJobBytes = jsonBytes(warmStoredJob);
		const expectedMediaDigest = expectedHash.digest("hex");
		durationsMs.mediaSubmission = performance.now() - mediaStartedAt;
		memorySnapshots.afterMediaSubmission = process.memoryUsage();

		phase = "cold_restart";
		const restartStartedAt = performance.now();
		authority.close();
		authority = undefined;
		const reopenedResources = await LocalGeneratedResourceStore.open({ rootDirectory: resourceRoot });
		authority = new storage.SqliteAuthority({
			path: databasePath,
			busyTimeoutMs: config.sqlite.busyTimeoutMs,
			verifyOnOpen: config.sqlite.verifyOnOpen,
			ownership: config.sqlite.ownership,
		});
		const reopenedStore = new storage.SqliteMediaJobStore(authority, {
			limits: config.mediaLimits,
			maxStoredJobBytes: config.sqlite.maxStoredJobBytes,
		});
		const restartProvider = {
			start: async () => {
				counters.restartProviderStarts += 1;
				throw new BenchmarkInvariantError("restart_provider_called");
			},
		};
		const restartedService = new MediaJobService({
			adapters: [
				{
					adapterId: ADAPTER_ID,
					provider: restartProvider,
					admission: new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 0 }),
				},
			],
			store: reopenedStore,
			resources: reopenedResources,
			limits: config.mediaLimits,
			newId: idFactory(),
			now: incrementalClock(),
		});
		const coldJob = await restartedService.read(JOB_ID);
		const coldBundle = await restartedService.readBundle(JOB_ID, warmBundle.bundleId);
		const selector = {
			jobId: JOB_ID,
			attemptId: warmBundle.attemptId,
			bundleId: warmBundle.bundleId,
			ordinal: warmResource.ordinal,
			resourceId: warmResource.resourceId,
		};
		const coldSelection = await restartedService.selectResource(selector);
		const coldMediaBytes = coldSelection
			? await withAbortableProgress(
					(signal) =>
						reopenedResources.read(coldSelection.resource, {
							maxBytes: config.mediaLimits.maxResourceBytes,
							maxMediaTypeBytes: config.mediaLimits.maxMediaTypeBytes,
							signal,
						}),
					config.progressTimeoutMs,
					"media_cold_read_progress_timeout",
				)
			: new Uint8Array();
		const coldMediaDigest =
			coldSelection === undefined ? undefined : createHash("sha256").update(coldMediaBytes).digest("hex");
		durationsMs.coldRestartAndRead = performance.now() - restartStartedAt;
		memorySnapshots.afterColdRestartRead = process.memoryUsage();

		phase = "verification";
		const expectedPixels = safeProduct(
			config.image.width,
			config.image.height,
			"benchmark_config_image_pixels_overflow",
		);
		const expectedDecodedBytes = safeProduct(
			expectedPixels,
			config.image.channels,
			"benchmark_config_decoded_bytes_overflow",
		);
		const expectedChunks = config.media.resourceBytes / config.media.chunkBytes;
		const checks = {
			imageDimensionsExact:
				attachment.width === config.image.width &&
				attachment.height === config.image.height &&
				decodedInfo.width === config.image.width &&
				decodedInfo.height === config.image.height,
			imageChannelsExact: decodedInfo.channels === config.image.channels,
			imageDecodedBytesExact: decodedBytes === expectedDecodedBytes,
			attachmentIdentityExact: attachment.attachmentId === `img_${attachmentDigest}`,
			attachmentColdReadExact: Buffer.from(coldAttachmentBytes).equals(Buffer.from(warmAttachmentBytes)),
			mediaStatusSucceeded: submitted.status === "succeeded" && coldJob?.status === "succeeded",
			mediaBundleExact:
				warmBundle.resources.length === 1 &&
				warmBundle.disposition === "complete" &&
				warmResource.byteLength === config.media.resourceBytes,
			mediaManifestEnvelopeExact: manifestBytes === config.derivedMinimums.manifestBytes,
			mediaStoredJobEnvelopeExact: storedJobBytes === config.derivedMinimums.storedJobBytes,
			mediaChunkCountExact: counters.emittedChunks === expectedChunks,
			mediaDigestExact:
				warmResource.sha256 === expectedMediaDigest &&
				warmResource.resourceId === expectedMediaDigest &&
				coldMediaDigest === expectedMediaDigest,
			mediaColdJobExact: coldJob?.bundleId === warmBundle.bundleId && coldJob.revision === submitted.revision,
			mediaColdBundleExact: JSON.stringify(coldBundle) === JSON.stringify(warmBundle),
			mediaColdSelectionExact:
				JSON.stringify(coldSelection) ===
				JSON.stringify({
					jobId: JOB_ID,
					attemptId: warmBundle.attemptId,
					bundleId: warmBundle.bundleId,
					bundleDisposition: warmBundle.disposition,
					resource: warmResource,
				}),
			mediaColdReadExact: coldMediaBytes.byteLength === config.media.resourceBytes,
			providerCallCountExact: counters.providerStarts === 1,
			providerNotCalledAfterRestart: counters.restartProviderStarts === 0,
		};
		const failedChecks = Object.entries(checks)
			.filter(([, passed]) => !passed)
			.map(([name]) => name);
		durationsMs.total = performance.now() - benchmarkStartedAt;
		report = {
			schemaVersion: 1,
			createdAt: new Date().toISOString(),
			profile: config.profile,
			purpose: config.purpose,
			outcome:
				failedChecks.length === 0
					? { status: "passed" }
					: { status: "failed", code: "benchmark_acceptance_failed", failedChecks },
			configuration: config,
			environment,
			workload: {
				image: {
					width: config.image.width,
					height: config.image.height,
					channels: config.image.channels,
					pixels: expectedPixels,
					decodedBytes,
					sourceBytes: source.byteLength,
					canonicalBytes: coldAttachmentBytes.byteLength,
				},
				media: {
					resourceBytes: config.media.resourceBytes,
					chunkBytes: config.media.chunkBytes,
					emittedChunks: counters.emittedChunks,
					manifestBytes,
					storedJobBytes,
				},
			},
			identities: {
				attachmentId: attachment.attachmentId,
				mediaResourceId: warmResource.resourceId,
				mediaSha256: warmResource.sha256,
			},
			counters,
			checks,
			durationsMs,
			memory: summarizeMemory(memorySnapshots),
			notes: [
				"All capacities and timeouts are explicit acceptance-profile inputs, not framework or product defaults.",
				"Durations and memory are observations from this host, not performance or capacity SLAs.",
				"The image path performs full normalization and a separate exact raw decode; the media path streams into CAS and verifies SQLite metadata plus CAS bytes after a cold reopen.",
			],
		};
	} catch (error) {
		durationsMs.total = performance.now() - benchmarkStartedAt;
		report = {
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
			environment,
			counters,
			durationsMs,
			memory: summarizeMemory({ ...memorySnapshots, onFailure: process.memoryUsage() }),
		};
	} finally {
		const cleanupFailures = [];
		try {
			authority?.close();
		} catch (error) {
			cleanupFailures.push(cleanupFailure("sqlite_close", error));
		}
		if (temporaryRoot !== undefined) {
			try {
				await rm(temporaryRoot, { recursive: true, force: true });
			} catch (error) {
				cleanupFailures.push(cleanupFailure("temporary_root_remove", error));
			}
		}
		if (cleanupFailures.length > 0) report = withCleanupFailure(report, cleanupFailures);
	}
	return report;
}

async function* deterministicResourceStream(config, signal, expectedHash, counters) {
	const chunk = deterministicChunk(config.media.chunkBytes);
	const chunks = config.media.resourceBytes / config.media.chunkBytes;
	for (let index = 0; index < chunks; index += 1) {
		signal.throwIfAborted();
		expectedHash.update(chunk);
		counters.emittedChunks += 1;
		yield chunk;
	}
}

function idFactory() {
	const counts = { attempt: 0, submission: 0, bundle: 0 };
	return (kind) => `media-size-${kind}-${++counts[kind]}`;
}

function incrementalClock() {
	let now = FIXED_TIME;
	return () => now++;
}

function deriveMinimums(profile, media) {
	const digest = "0".repeat(64);
	const resource = {
		resourceId: digest,
		kind: "file",
		mediaType: MEDIA_TYPE,
		byteLength: media.resourceBytes,
		sha256: digest,
		ordinal: 0,
	};
	const bundle = {
		bundleId: "media-size-bundle-1",
		jobId: JOB_ID,
		attemptId: "media-size-attempt-1",
		disposition: "complete",
		resources: [resource],
		failures: [],
	};
	const recordedAt = new Date(FIXED_TIME).toISOString();
	const updatedAt = new Date(FIXED_TIME + 1).toISOString();
	const job = {
		jobId: JOB_ID,
		adapterId: ADAPTER_ID,
		request: { kind: "file", input: { profile } },
		revision: "2",
		createdAt: recordedAt,
		updatedAt,
		currentAttemptId: "media-size-attempt-1",
		attempts: [
			{
				attemptId: "media-size-attempt-1",
				ordinal: 0,
				submissionKey: "media-size-submission-1",
				status: "succeeded",
				disposition: "active",
				cancellation: "not_requested",
				startedAt: recordedAt,
				updatedAt,
				outputSettlements: [{ ordinal: 0, status: "published", resource }],
				bundleId: "media-size-bundle-1",
			},
		],
		bundles: [bundle],
	};
	return Object.freeze({
		requestBytes: jsonBytes({ profile }),
		requestDepth: 2,
		manifestBytes: jsonBytes(bundle),
		manifestDepth: 4,
		storedJobBytes: jsonBytes(job),
	});
}

function deterministicChunk(byteLength) {
	const chunk = Buffer.allocUnsafe(byteLength);
	for (let index = 0; index < chunk.byteLength; index += 1) chunk[index] = (index * 31 + 17) % 251;
	return chunk;
}

function jsonBytes(value) {
	return Buffer.byteLength(JSON.stringify(value));
}

function validateConfig(value) {
	if (!isPlainObject(value)) throw new BenchmarkConfigError("benchmark_config_invalid");
	assertExactKeys(value, ROOT_KEYS, "benchmark_config_keys_invalid");
	const profile = requiredText(value.profile, "profile");
	const purpose = requiredText(value.purpose, "purpose");
	const image = numericObject(value.image, IMAGE_KEYS, "image");
	if (image.channels !== 3 && image.channels !== 4)
		throw new BenchmarkConfigError("benchmark_config_image_channels_invalid");
	const media = numericObject(value.media, MEDIA_KEYS, "media");
	const progressTimeoutMs = requiredPositiveInteger(value.progressTimeoutMs, "progressTimeoutMs");
	if (progressTimeoutMs > NODE_MAX_TIMER_DELAY_MS) throw new BenchmarkConfigError("benchmark_config_timeout_invalid");
	const maxMaterializedBytes = requiredPositiveInteger(value.maxMaterializedBytes, "maxMaterializedBytes");
	const attachmentLimits = numericObject(value.attachmentLimits, ATTACHMENT_LIMIT_KEYS, "attachmentLimits");
	const mediaLimits = numericObject(value.mediaLimits, MEDIA_LIMIT_KEYS, "mediaLimits");
	const sqlite = captureSqlite(value.sqlite);
	const evidence = captureEvidence(value.evidence);
	const pixels = safeProduct(image.width, image.height, "benchmark_config_image_pixels_overflow");
	const decodedBytes = safeProduct(pixels, image.channels, "benchmark_config_decoded_bytes_overflow");
	if (decodedBytes > bufferConstants.MAX_LENGTH || decodedBytes > maxMaterializedBytes) {
		throw new BenchmarkConfigError("benchmark_config_decoded_image_not_materializable");
	}
	if (
		image.width > attachmentLimits.maxInputDimension ||
		image.height > attachmentLimits.maxInputDimension ||
		image.width > attachmentLimits.maxStoredDimension ||
		image.height > attachmentLimits.maxStoredDimension ||
		pixels > attachmentLimits.maxInputPixels ||
		pixels > attachmentLimits.maxStoredPixels
	) {
		throw new BenchmarkConfigError("benchmark_config_image_limits_too_small");
	}
	if (
		attachmentLimits.maxImagesPerBatch < 1 ||
		attachmentLimits.maxBatchInputBytes < attachmentLimits.maxInputBytes ||
		attachmentLimits.maxBatchStoredBytes < attachmentLimits.maxStoredBytes ||
		attachmentLimits.maxReadBytes < attachmentLimits.maxStoredBytes ||
		Buffer.byteLength(SOURCE_NAME) > attachmentLimits.maxNameBytes
	) {
		throw new BenchmarkConfigError("benchmark_config_attachment_limits_inconsistent");
	}
	if (
		attachmentLimits.maxInputBytes > maxMaterializedBytes ||
		attachmentLimits.maxStoredBytes > maxMaterializedBytes ||
		attachmentLimits.maxReadBytes > maxMaterializedBytes
	) {
		throw new BenchmarkConfigError("benchmark_config_attachment_not_materializable");
	}
	if (
		media.resourceBytes > mediaLimits.maxResourceBytes ||
		media.resourceBytes > mediaLimits.maxBundleBytes ||
		media.chunkBytes > media.resourceBytes ||
		media.resourceBytes % media.chunkBytes !== 0 ||
		mediaLimits.maxAttemptsPerJob < 1 ||
		mediaLimits.maxOutputsPerAttempt < 1
	) {
		throw new BenchmarkConfigError("benchmark_config_media_limits_inconsistent");
	}
	if (
		media.resourceBytes > bufferConstants.MAX_LENGTH ||
		media.chunkBytes > bufferConstants.MAX_LENGTH ||
		media.resourceBytes > maxMaterializedBytes ||
		media.chunkBytes > maxMaterializedBytes
	) {
		throw new BenchmarkConfigError("benchmark_config_media_not_materializable");
	}
	for (const identifier of [JOB_ID, "media-size-attempt-1", "media-size-submission-1", "media-size-bundle-1"]) {
		if (Buffer.byteLength(identifier) > mediaLimits.maxIdentifierBytes) {
			throw new BenchmarkConfigError("benchmark_config_identifier_limit_too_small");
		}
	}
	if (Buffer.byteLength(ADAPTER_ID) > mediaLimits.maxAdapterIdBytes) {
		throw new BenchmarkConfigError("benchmark_config_adapter_limit_too_small");
	}
	if (Buffer.byteLength(MEDIA_TYPE) > mediaLimits.maxMediaTypeBytes) {
		throw new BenchmarkConfigError("benchmark_config_media_type_limit_too_small");
	}
	const derivedMinimums = deriveMinimums(profile, media);
	if (mediaLimits.maxRequestBytes < derivedMinimums.requestBytes) {
		throw new BenchmarkConfigError("benchmark_config_request_budget_too_small");
	}
	if (mediaLimits.maxJsonDepth < derivedMinimums.manifestDepth) {
		throw new BenchmarkConfigError("benchmark_config_json_depth_too_small");
	}
	if (mediaLimits.maxManifestBytes < derivedMinimums.manifestBytes) {
		throw new BenchmarkConfigError("benchmark_config_manifest_budget_too_small");
	}
	if (sqlite.maxStoredJobBytes < derivedMinimums.storedJobBytes) {
		throw new BenchmarkConfigError("benchmark_config_stored_job_budget_too_small");
	}
	return Object.freeze({
		profile,
		purpose,
		image: Object.freeze(image),
		media: Object.freeze(media),
		progressTimeoutMs,
		maxMaterializedBytes,
		attachmentLimits: Object.freeze(attachmentLimits),
		mediaLimits: Object.freeze(mediaLimits),
		sqlite: Object.freeze(sqlite),
		evidence,
		derivedMinimums,
	});
}

function captureSqlite(value) {
	if (!isPlainObject(value)) throw new BenchmarkConfigError("benchmark_config_sqlite_invalid");
	assertExactKeys(value, SQLITE_KEYS, "benchmark_config_sqlite_keys_invalid");
	const busyTimeoutMs = requiredPositiveInteger(value.busyTimeoutMs, "sqlite.busyTimeoutMs");
	const maxStoredJobBytes = requiredPositiveInteger(value.maxStoredJobBytes, "sqlite.maxStoredJobBytes");
	if (busyTimeoutMs > SQLITE_INT_MAX) throw new BenchmarkConfigError("benchmark_config_sqlite_timeout_invalid");
	if (value.verifyOnOpen !== "full" || value.ownership !== "exclusive") {
		throw new BenchmarkConfigError("benchmark_config_sqlite_recovery_mode_invalid");
	}
	return { busyTimeoutMs, verifyOnOpen: value.verifyOnOpen, ownership: value.ownership, maxStoredJobBytes };
}

function numericObject(value, keys, path) {
	if (!isPlainObject(value)) throw new BenchmarkConfigError(`benchmark_config_${path}_invalid`);
	assertExactKeys(value, keys, `benchmark_config_${path}_keys_invalid`);
	return Object.fromEntries(keys.map((name) => [name, requiredPositiveInteger(value[name], `${path}.${name}`)]));
}

function captureEvidence(value) {
	if (!isPlainObject(value)) throw new BenchmarkConfigError("benchmark_config_evidence_required");
	assertExactKeys(value, NUMERIC_EVIDENCE_PATHS, "benchmark_config_evidence_keys_invalid");
	return Object.freeze(
		Object.fromEntries(NUMERIC_EVIDENCE_PATHS.map((path) => [path, requiredText(value[path], `evidence.${path}`)])),
	);
}

function requiredText(value, path) {
	if (typeof value !== "string" || value.trim().length === 0) {
		throw new BenchmarkConfigError(`benchmark_config_${path.replaceAll(".", "_")}_invalid`);
	}
	return value.trim();
}

function requiredPositiveInteger(value, path) {
	if (!Number.isSafeInteger(value) || Object.is(value, -0) || value <= 0) {
		throw new BenchmarkConfigError(`benchmark_config_${path.replaceAll(".", "_")}_invalid`);
	}
	return value;
}

function safeProduct(left, right, code) {
	const product = left * right;
	if (!Number.isSafeInteger(product)) throw new BenchmarkConfigError(code);
	return product;
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
	const [attachments, media, resources, sqlite] = await Promise.all([
		readPackage("packages/attachments/package.json", parseStrictJsonText),
		readPackage("packages/media/package.json", parseStrictJsonText),
		readPackage("packages/resources/package.json", parseStrictJsonText),
		readPackage("packages/storage-sqlite/package.json", parseStrictJsonText),
	]);
	return {
		...basicEnvironment(),
		git: describeGit(),
		packages: {
			attachments: attachments.version,
			media: media.version,
			resources: resources.version,
			storageSqlite: sqlite.version,
			sharp: sharp.versions.sharp,
			libvips: sharp.versions.vips,
			betterSqlite3: sqlite.dependencies["better-sqlite3"],
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
	return Object.fromEntries(
		Object.entries(snapshots).map(([name, snapshot]) => [
			name,
			{ rss: snapshot.rss, heapUsed: snapshot.heapUsed, external: snapshot.external },
		]),
	);
}

function parseArguments(arguments_) {
	let configPath;
	let outputPath;
	for (let index = 0; index < arguments_.length; index += 1) {
		const argument = arguments_[index];
		if (argument === "--help" || argument === "-h") return { help: true };
		if (argument === "--config") {
			if (configPath !== undefined || index + 1 >= arguments_.length) {
				throw new BenchmarkConfigError("benchmark_arguments_invalid");
			}
			configPath = captureOptionValue(arguments_[++index]);
			continue;
		}
		if (argument === "--output") {
			if (outputPath !== undefined || index + 1 >= arguments_.length) {
				throw new BenchmarkConfigError("benchmark_arguments_invalid");
			}
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

function cleanupFailure(operation, error) {
	return Object.freeze({ operation, errorName: error instanceof Error ? error.name : typeof error });
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
		"Usage: node scripts/benchmark-media-size.mjs --config <profile.json> [--output <report.json>]\n",
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

class BenchmarkOutputError extends Error {
	constructor(cause) {
		super("benchmark_report_write_failed");
		this.name = "BenchmarkOutputError";
		this.causeName = cause instanceof Error ? cause.name : typeof cause;
	}
}

await main();
