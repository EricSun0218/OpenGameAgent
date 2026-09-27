import { spawn, spawnSync } from "node:child_process";
import { once } from "node:events";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const testPrefix = "OpenGameAgent.RuntimeClient";
const expectedTests = [
	"OpenGameAgent.RuntimeClient.ActionPump",
	"OpenGameAgent.RuntimeClient.Transport",
	"OpenGameAgent.RuntimeClient.Validation",
];
const engineRootArgument = process.argv.find((argument) => argument.startsWith("--engine-root="));
const engineRoot = resolve(engineRootArgument?.slice("--engine-root=".length) ?? process.env.UNREAL_ENGINE_ROOT ?? "");
if (!engineRootArgument && !process.env.UNREAL_ENGINE_ROOT) {
	throw new Error("Pass --engine-root=<Unreal Engine root> or set UNREAL_ENGINE_ROOT.");
}
const timeoutArgument = process.argv.find((argument) => argument.startsWith("--timeout-ms="));
const timeoutText = timeoutArgument?.slice("--timeout-ms=".length) ?? process.env.OGA_UNREAL_GATE_TIMEOUT_MS;
const gateTimeoutMilliseconds = Number(timeoutText);
if (
	!Number.isSafeInteger(gateTimeoutMilliseconds) ||
	gateTimeoutMilliseconds <= 0 ||
	gateTimeoutMilliseconds > 2_147_483_647
) {
	throw new Error("Pass --timeout-ms=<positive integer no greater than 2147483647> or set OGA_UNREAL_GATE_TIMEOUT_MS.");
}

const uat = join(engineRoot, "Engine", "Build", "BatchFiles", "RunUAT.bat");
const editor = join(engineRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
for (const executable of [uat, editor]) {
	if (!existsSync(executable)) throw new Error(`Unreal executable not found: ${executable}`);
}

const repositoryRoot = resolve(import.meta.dirname, "..");
const pluginDescriptor = join(repositoryRoot, "engines", "unreal", "OpenGameAgent", "OpenGameAgent.uplugin");
const temporaryRoot = mkdtempSync(join(tmpdir(), "opengameagent-unreal-gate-"));
const packageRoot = join(temporaryRoot, "package");
const projectRoot = join(temporaryRoot, "host");
const projectFile = join(projectRoot, "OpenGameAgentGate.uproject");
const reportRoot = join(temporaryRoot, "report");
const logFile = join(temporaryRoot, "OpenGameAgentGate.log");
const gateStateFile = join(temporaryRoot, "http-gate-state.json");

function run(executable, args, options = {}) {
	const result = spawnSync(executable, args, {
		cwd: repositoryRoot,
		encoding: "utf8",
		stdio: "inherit",
		timeout: gateTimeoutMilliseconds,
		...options,
	});
	if (result.error) throw result.error;
	if (result.status !== 0) {
		throw new Error(`${executable} exited with status ${result.status}. Gate artifacts: ${temporaryRoot}`);
	}
}

function startGateServer() {
	const child = spawn(
		process.execPath,
		[join(repositoryRoot, "scripts", "unreal-http-gate-server.mjs"), `--state=${gateStateFile}`],
		{
			cwd: repositoryRoot,
			stdio: ["ignore", "pipe", "inherit"],
			windowsHide: true,
		},
	);
	return new Promise((resolveReady, rejectReady) => {
		let output = "";
		let settled = false;
		const health = { error: null, postReadyExit: null };
		const timer = setTimeout(() => {
			if (!settled) {
				settled = true;
				child.kill();
				rejectReady(new Error("Unreal HTTP gate server readiness exceeded the configured timeout."));
			}
		}, gateTimeoutMilliseconds);
		child.stdout.setEncoding("utf8");
		child.stdout.on("data", (chunk) => {
			if (settled) return;
			output += chunk;
			const lineEnd = output.indexOf("\n");
			if (lineEnd < 0) return;
			settled = true;
			clearTimeout(timer);
			resolveReady({ child, health, ...JSON.parse(output.slice(0, lineEnd)) });
		});
		child.once("error", (error) => {
			if (!settled) {
				settled = true;
				clearTimeout(timer);
				rejectReady(error);
			} else {
				health.error = error;
			}
		});
		child.once("exit", (code) => {
			if (!settled) {
				settled = true;
				clearTimeout(timer);
				rejectReady(new Error(`Unreal HTTP gate server exited before readiness (${String(code)}).`));
			} else {
				health.postReadyExit = code;
			}
		});
	});
}

function assertGateServerState(server) {
	if (server.child.exitCode !== null || server.child.signalCode !== null || server.health.error) {
		throw new Error(`Unreal HTTP gate server did not remain healthy. Gate artifacts: ${temporaryRoot}`);
	}
	const state = JSON.parse(readFileSync(gateStateFile, "utf8"));
	if (state.serverError) {
		throw new Error(`Unreal HTTP gate server failed: ${state.serverError}. Gate artifacts: ${temporaryRoot}`);
	}
	if (state.targetConnections !== 0 || state.targetBytes !== 0) {
		throw new Error(`Unreal transport followed a redirect. Gate artifacts: ${temporaryRoot}`);
	}
	const expectedPaths = [
		"/attachment",
		"/error-chunked",
		"/error-length",
		"/redirect",
		"/runtime/v1/actions/get",
		...Array(2).fill("/runtime/v1/actions/next"),
		...Array(4).fill("/runtime/v1/actions/receipt"),
		...Array(3).fill("/runtime/v1/actions/reconcile"),
		...Array(4).fill("/runtime/v1/actions/recovery"),
		"/runtime/v1/attachments/read",
		...Array(3).fill("/runtime/v1/deferred/abandon"),
		...Array(2).fill("/runtime/v1/deferred/read"),
		...Array(7).fill("/runtime/v1/deferred/resume"),
	];
	const actualPaths = state.sourceRequests.map((request) => request.path).sort();
	if (JSON.stringify(actualPaths) !== JSON.stringify(expectedPaths)) {
		throw new Error(`Unreal transport gate requests were incomplete: ${JSON.stringify(actualPaths)}.`);
	}
	for (const request of state.sourceRequests) {
		if (request.method !== "POST" || request.authorization !== "Bearer gate-token") {
			throw new Error(`Unreal transport changed the authenticated POST request for ${request.path}.`);
		}
		if (request.path.startsWith("/runtime/v1/actions/")) {
			const body = JSON.parse(request.body);
			if (body.sessionId !== "gate-session") {
				throw new Error(`Unreal action request changed its session selector for ${request.path}.`);
			}
			if (request.path === "/runtime/v1/actions/next") {
				if (body.actorId !== "gate-actor" && body.actorId !== "gate-exhaust-actor") {
					throw new Error("Unreal next-action request changed its actor selector.");
				}
			} else if (request.path === "/runtime/v1/actions/recovery") {
				if (
					(body.actorId !== "gate-actor" &&
						body.actorId !== "gate-exhaust-actor" &&
						body.actorId !== "gate-invalid-recovery-actor" &&
						body.actorId !== "gate-invalid-pending-recovery-actor") ||
					Object.keys(body).length !== 2
				) {
					throw new Error("Unreal recovery-only request changed its actor selector.");
				}
			} else if (request.path === "/runtime/v1/actions/get") {
				if (body.actorId !== "gate-actor" || body.operationId !== "operation-network") {
					throw new Error("Unreal authoritative action read changed its exact identity.");
				}
			} else if (request.path === "/runtime/v1/actions/receipt") {
				const expected =
					body.actorId === "gate-actor"
						? { operationId: "operation-network", claimToken: "claim-1" }
						: body.actorId === "gate-exhaust-actor"
							? { operationId: "operation-exhaust", claimToken: "claim-exhaust" }
							: null;
				if (
					!expected ||
					body.operationId !== expected.operationId ||
					body.claimToken !== expected.claimToken ||
					body.attempt !== "1" ||
					body.outcome !== "succeeded"
				) {
					throw new Error("Unreal receipt changed its captured claim fence or outcome.");
				}
			} else if (
				body.actorId !== "gate-actor" ||
				body.operationId !== "operation-network" ||
				body.claimToken !== "claim-2" ||
				body.attempt !== "2" ||
				body.resolution !== "applied"
			) {
				throw new Error("Unreal reconciliation changed its conflict-read claim fence or resolution.");
			}
		} else if (request.path === "/runtime/v1/attachments/read") {
			const body = JSON.parse(request.body);
			if (
				body.sessionId !== "gate-session" ||
				body.actorId !== "gate-actor" ||
				body.reference?.attachmentId !== "img_4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6"
			) {
				throw new Error("Unreal public attachment request changed its protocol selectors.");
			}
		} else if (request.path.startsWith("/runtime/v1/deferred/")) {
			const body = JSON.parse(request.body);
			if (body.sessionId !== "gate-session" || body.actorId !== "gate-actor") {
				throw new Error(`Unreal deferred request changed its actor selector for ${request.path}.`);
			}
		} else if (request.body !== '{"gate":true}') {
			throw new Error(`Unreal direct transport fixture body changed for ${request.path}.`);
		}
	}
	const operationCounts = {};
	for (const request of state.sourceRequests.filter((request) => request.path === "/runtime/v1/deferred/resume")) {
		const operationId = JSON.parse(request.body).operationId;
		operationCounts[operationId] = (operationCounts[operationId] ?? 0) + 1;
	}
	if (
		JSON.stringify(operationCounts) !==
		JSON.stringify({ operation: 2, conflict: 1, missing: 1, malformed: 1, partial: 1, redirect: 1 })
	) {
		throw new Error(`Unreal deferred resume request counts changed: ${JSON.stringify(operationCounts)}.`);
	}
	const actionSequence = state.sourceRequests
		.filter((request) => request.path.startsWith("/runtime/v1/actions/"))
		.map((request) => {
			const body = JSON.parse(request.body);
			return [request.path, body.actorId, body.claimToken ?? null];
		});
	const expectedActionSequence = [
		["/runtime/v1/actions/recovery", "gate-actor", null],
		["/runtime/v1/actions/next", "gate-actor", null],
		["/runtime/v1/actions/receipt", "gate-actor", "claim-1"],
		["/runtime/v1/actions/get", "gate-actor", null],
		...Array(3).fill(["/runtime/v1/actions/reconcile", "gate-actor", "claim-2"]),
		["/runtime/v1/actions/recovery", "gate-exhaust-actor", null],
		["/runtime/v1/actions/next", "gate-exhaust-actor", null],
		...Array(3).fill(["/runtime/v1/actions/receipt", "gate-exhaust-actor", "claim-exhaust"]),
		["/runtime/v1/actions/recovery", "gate-invalid-recovery-actor", null],
		["/runtime/v1/actions/recovery", "gate-invalid-pending-recovery-actor", null],
	];
	if (JSON.stringify(actionSequence) !== JSON.stringify(expectedActionSequence)) {
		throw new Error(`Unreal action network sequence changed: ${JSON.stringify(actionSequence)}.`);
	}
}

async function stopGateServer(child) {
	if (child.exitCode !== null || child.signalCode !== null) return;
	child.kill();
	let timer;
	try {
		await Promise.race([
			once(child, "exit"),
			new Promise((_, reject) => {
				timer = setTimeout(
					() => reject(new Error("Unreal HTTP gate server shutdown exceeded the configured timeout.")),
					gateTimeoutMilliseconds,
				);
			}),
		]);
	} finally {
		clearTimeout(timer);
	}
}

let succeeded = false;
let gateServer;
try {
	run(
		"cmd.exe",
		["/d", "/c", "call", uat, "BuildPlugin", `-Plugin=${pluginDescriptor}`, `-Package=${packageRoot}`, "-Rocket"],
		{
			windowsHide: true,
		},
	);

	mkdirSync(join(projectRoot, "Plugins"), { recursive: true });
	cpSync(packageRoot, join(projectRoot, "Plugins", "OpenGameAgent"), {
		recursive: true,
	});
	writeFileSync(
		projectFile,
		`${JSON.stringify(
			{
				FileVersion: 3,
				EngineAssociation: "5.8",
				Category: "",
				Description: "OpenGameAgent Unreal engine gate host.",
				Plugins: [{ Name: "OpenGameAgent", Enabled: true }],
			},
			null,
			"\t",
		)}\n`,
		"utf8",
	);
	gateServer = await startGateServer();

	run(
		editor,
		[
			projectFile,
			"-unattended",
			"-nop4",
			"-NullRHI",
			"-nosplash",
			`-ExecCmds=Automation RunTests ${testPrefix}; Quit`,
			"-TestExit=Automation Test Queue Empty",
			`-ReportExportPath=${reportRoot}`,
			`-abslog=${logFile}`,
		],
		{
			env: {
				...process.env,
				OGA_UNREAL_TEST_BASE_URL: gateServer.baseUrl,
				OGA_UNREAL_TEST_TIMEOUT_MS: String(gateTimeoutMilliseconds),
			},
		},
	);

	const reportPath = join(reportRoot, "index.json");
	if (!existsSync(reportPath)) {
		throw new Error(`Unreal did not write the automation report. Gate artifacts: ${temporaryRoot}`);
	}
	const report = JSON.parse(readFileSync(reportPath, "utf8").replace(/^\uFEFF/, ""));
	const tests = Array.isArray(report.tests) ? report.tests : Array.isArray(report.Tests) ? report.Tests : [];
	const failed = report.failed ?? report.Failed;
	for (const testName of expectedTests) {
		const test = tests.find((candidate) => (candidate.fullTestPath ?? candidate.FullTestPath) === testName);
		const state = test?.state ?? test?.State;
		if (!test || state !== "Success" || failed !== 0) {
			throw new Error(
				`Unreal automation test did not pass (${testName}, state=${String(state)}, failed=${String(failed)}). Gate artifacts: ${temporaryRoot}`,
			);
		}
	}
	assertGateServerState(gateServer);

	succeeded = true;
	console.log(`Unreal engine gate passed: ${expectedTests.join(", ")} (${engineRoot}).`);
} finally {
	if (gateServer) await stopGateServer(gateServer.child);
	if (succeeded) rmSync(temporaryRoot, { recursive: true, force: true });
}
