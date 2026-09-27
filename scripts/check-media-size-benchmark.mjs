import { execFile } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import { withAbortableProgress, withSharpProgress } from "./benchmark-progress.mjs";
import { withCleanupFailure, withOutputFailure } from "./benchmark-report.mjs";

const execFileAsync = promisify(execFile);
const PROFILE_PATH = resolve("scripts/benchmark-profiles/media-size-d3d11-fl10.json");
const RUNNER_PATH = resolve("scripts/benchmark-media-size.mjs");
const REPORT_JSON_MAX_DEPTH = 8;
const SMOKE_PROCESS_TIMEOUT_MS = 5_000;

async function main() {
	const { parseStrictJsonText } = await import("../packages/protocol/dist/index.js");
	const sourceProfile = parseStrictJsonText(await readFile(PROFILE_PATH, "utf8"), 3);
	const valid = smokeProfile(sourceProfile);
	const temporaryRoot = await mkdtemp(join(tmpdir(), "oga-media-size-check-"));
	try {
		const passed = await runCase(temporaryRoot, "valid", valid, parseStrictJsonText);
		assertMatch(passed, {
			status: "passed",
			checks: [
				"attachmentColdReadExact",
				"mediaDigestExact",
				"mediaColdReadExact",
				"mediaColdSelectionExact",
				"providerNotCalledAfterRestart",
				"mediaManifestEnvelopeExact",
				"mediaStoredJobEnvelopeExact",
			],
		});

		await expectConfigFailure(
			temporaryRoot,
			"request-budget",
			withChange(valid, (profile) => {
				profile.mediaLimits.maxRequestBytes = 1;
			}),
			"benchmark_config_request_budget_too_small",
			parseStrictJsonText,
		);
		await expectConfigFailure(
			temporaryRoot,
			"manifest-budget",
			withChange(valid, (profile) => {
				profile.mediaLimits.maxManifestBytes = 1;
			}),
			"benchmark_config_manifest_budget_too_small",
			parseStrictJsonText,
		);
		await expectConfigFailure(
			temporaryRoot,
			"stored-job-budget",
			withChange(valid, (profile) => {
				profile.sqlite.maxStoredJobBytes = 1;
			}),
			"benchmark_config_stored_job_budget_too_small",
			parseStrictJsonText,
		);
		await expectConfigFailure(
			temporaryRoot,
			"resource-materialization",
			withChange(valid, (profile) => {
				profile.media.resourceBytes = Number.MAX_SAFE_INTEGER;
				profile.media.chunkBytes = 1;
				profile.mediaLimits.maxResourceBytes = Number.MAX_SAFE_INTEGER;
				profile.mediaLimits.maxBundleBytes = Number.MAX_SAFE_INTEGER;
			}),
			"benchmark_config_media_not_materializable",
			parseStrictJsonText,
		);
		await expectConfigFailure(
			temporaryRoot,
			"image-materialization",
			withChange(valid, (profile) => {
				profile.image.width = 100_000;
				profile.image.height = 100_000;
				profile.attachmentLimits.maxInputPixels = 10_000_000_000;
				profile.attachmentLimits.maxStoredPixels = 10_000_000_000;
				profile.attachmentLimits.maxInputDimension = 100_000;
				profile.attachmentLimits.maxStoredDimension = 100_000;
			}),
			"benchmark_config_decoded_image_not_materializable",
			parseStrictJsonText,
		);
		await expectConfigFailure(
			temporaryRoot,
			"attachment-materialization",
			withChange(valid, (profile) => {
				profile.image.width = 1;
				profile.image.height = 1;
				profile.attachmentLimits.maxInputPixels = 1;
				profile.attachmentLimits.maxStoredPixels = 1;
				profile.attachmentLimits.maxInputDimension = 1;
				profile.attachmentLimits.maxStoredDimension = 1;
				profile.maxMaterializedBytes = 4;
			}),
			"benchmark_config_attachment_not_materializable",
			parseStrictJsonText,
		);
		assertDoubleFailureReport();
		await assertLateSuccessStillTimesOut();
	} finally {
		await rm(temporaryRoot, { recursive: true, force: true });
	}
	process.stdout.write("Media-size build-artifact checks passed.\n");
}

async function assertLateSuccessStillTimesOut() {
	const abortableCode = "abortable_late_success";
	await expectCode(
		withAbortableProgress(
			(signal) =>
				new Promise((resolvePromise) => {
					signal.addEventListener("abort", () => resolvePromise("late success"), { once: true });
				}),
			1,
			abortableCode,
		),
		abortableCode,
	);

	const sharpCode = "sharp_late_success";
	let resolveSharp;
	const lateSharp = new Promise((resolvePromise) => {
		resolveSharp = resolvePromise;
	});
	await expectCode(
		withSharpProgress({ destroy: () => resolveSharp("late success") }, () => lateSharp, 1, sharpCode),
		sharpCode,
	);
}

async function expectCode(promise, expectedCode) {
	try {
		await promise;
	} catch (error) {
		if (error?.code === expectedCode) return;
		throw error;
	}
	throw new Error(`${expectedCode} unexpectedly fulfilled.`);
}

function assertDoubleFailureReport() {
	const initial = { schemaVersion: 1, outcome: { status: "passed" } };
	const cleanup = withCleanupFailure(initial, [{ operation: "test_cleanup", errorName: "Error" }]);
	const output = withOutputFailure(cleanup, { causeName: "Error" });
	if (
		output.outcome.code !== "benchmark_report_write_failed" ||
		output.benchmarkOutcome.status !== "passed" ||
		output.cleanupOutcome?.code !== "benchmark_cleanup_failed" ||
		output.cleanup?.failures?.[0]?.operation !== "test_cleanup"
	) {
		throw new Error("The combined cleanup/output failure report lost earlier evidence.");
	}
}

function smokeProfile(source) {
	const profile = structuredClone(source);
	profile.profile = "build-artifact-smoke";
	profile.purpose = "Exercise the compiled benchmark surfaces with deterministic small inputs.";
	profile.image = { width: 2, height: 2, channels: 4 };
	profile.media = { resourceBytes: 1_024, chunkBytes: 256 };
	profile.progressTimeoutMs = SMOKE_PROCESS_TIMEOUT_MS;
	profile.maxMaterializedBytes = 4_096;
	profile.attachmentLimits = {
		maxInputBytes: 4_096,
		maxInputPixels: 4,
		maxInputDimension: 2,
		maxStoredBytes: 4_096,
		maxStoredPixels: 4,
		maxStoredDimension: 2,
		maxReadBytes: 4_096,
		maxNameBytes: 32,
		maxImagesPerBatch: 1,
		maxBatchInputBytes: 4_096,
		maxBatchStoredBytes: 4_096,
	};
	profile.mediaLimits = {
		maxIdentifierBytes: 64,
		maxAdapterIdBytes: 64,
		maxAdapters: 1,
		maxRequestBytes: 256,
		maxJsonDepth: 4,
		maxProviderHandleBytes: 256,
		maxAttemptsPerJob: 1,
		maxOutputsPerAttempt: 1,
		maxResourceBytes: 1_024,
		maxBundleBytes: 1_024,
		maxManifestBytes: 4_096,
		maxMediaTypeBytes: 64,
	};
	profile.sqlite = {
		busyTimeoutMs: 1_000,
		verifyOnOpen: "full",
		ownership: "exclusive",
		maxStoredJobBytes: 16_384,
	};
	profile.evidence = Object.fromEntries(
		Object.keys(profile.evidence).map((path) => [path, "Explicit build-artifact check input."]),
	);
	return profile;
}

async function runCase(temporaryRoot, name, profile, parseStrictJsonText) {
	const configPath = join(temporaryRoot, `${name}.json`);
	const outputPath = join(temporaryRoot, `${name}.report.json`);
	await writeFile(configPath, `${JSON.stringify(profile, null, 2)}\n`);
	let processError;
	try {
		await execFileAsync(process.execPath, [RUNNER_PATH, "--config", configPath, "--output", outputPath], {
			cwd: resolve("."),
			timeout: SMOKE_PROCESS_TIMEOUT_MS,
			killSignal: "SIGKILL",
			windowsHide: true,
		});
	} catch (error) {
		processError = error;
	}
	const report = parseStrictJsonText(await readFile(outputPath, "utf8"), REPORT_JSON_MAX_DEPTH);
	return { processError, report };
}

async function expectConfigFailure(temporaryRoot, name, profile, expectedCode, parseStrictJsonText) {
	const result = await runCase(temporaryRoot, name, profile, parseStrictJsonText);
	if (!result.processError) throw new Error(`${name} unexpectedly succeeded.`);
	if (result.processError.killed !== false || result.processError.signal !== null || result.processError.code !== 1) {
		throw new Error(`${name} did not terminate through the expected structured exit.`);
	}
	if (result.report?.outcome?.status !== "failed" || result.report.outcome.code !== expectedCode) {
		throw new Error(`${name} returned the wrong structured failure.`);
	}
}

function assertMatch(result, expected) {
	if (result.processError)
		throw new Error("The valid media-size smoke workload failed.", { cause: result.processError });
	if (result.report?.outcome?.status !== expected.status)
		throw new Error("The valid media-size outcome was not passed.");
	for (const check of expected.checks) {
		if (result.report?.checks?.[check] !== true) throw new Error(`The valid media-size check failed: ${check}`);
	}
}

function withChange(source, change) {
	const changed = structuredClone(source);
	change(changed);
	return changed;
}

await main();
