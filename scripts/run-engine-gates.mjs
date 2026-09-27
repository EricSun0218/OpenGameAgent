import { execFileSync, spawnSync } from "node:child_process";
import { cpSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join, resolve } from "node:path";

const selected = new Set(process.argv.slice(2));
if (selected.size === 0) selected.add("all");
const includes = (name) => selected.has("all") || selected.has(name);

if (includes("godot")) {
	const staged = stageEngine("godot");
	try {
		run("dotnet", [
			"run",
			"--project",
			join(
				staged.root,
				"csharp",
				"test",
				"OpenGameAgent.Client.Conformance",
				"OpenGameAgent.Client.Conformance.csproj",
			),
			"--configuration",
			"Release",
		]);
		run("dotnet", [
			"build",
			join(staged.root, "OpenGameAgentGodot", "OpenGameAgentGodot.csproj"),
			"--configuration",
			"Debug",
		]);
		run("dotnet", [
			"build",
			join(staged.root, "OpenGameAgentGodot.Conformance", "OpenGameAgentGodot.Conformance.csproj"),
			"--configuration",
			"Debug",
		]);
		const godot =
			process.env.OGA_GODOT_EDITOR ||
			findCommand(process.platform === "win32" ? ["godot_console.exe", "godot.exe"] : ["godot"]);
		if (!godot) throw new Error("Godot gate requires OGA_GODOT_EDITOR or a godot executable on PATH.");
		const project = join(staged.root, "OpenGameAgentGodot.Conformance");
		run(godot, ["--headless", "--editor", "--path", project, "--quit"]);
		run(godot, ["--headless", "--path", project, "--scene", "res://action_pump_conformance.tscn"]);
	} finally {
		staged.dispose();
	}
}

if (includes("unity")) {
	const configured = process.env.OGA_UNITY_EDITORS?.split(delimiter)
		.map((entry) => entry.trim())
		.filter(Boolean);
	const fallback =
		process.env.OGA_UNITY_EDITOR || findCommand(process.platform === "win32" ? ["Unity.exe"] : ["Unity"]);
	const editors = [...new Set(configured && configured.length > 0 ? configured : fallback ? [fallback] : [])];
	if (editors.length === 0) {
		throw new Error("Unity gate requires OGA_UNITY_EDITORS, OGA_UNITY_EDITOR, or a Unity executable on PATH.");
	}
	for (const editor of editors) {
		assertUnity6(editor);
		runUnity(editor);
	}
}

console.log(`Engine integration gates passed: ${[...selected].join(", ")}.`);

function run(command, args) {
	execFileSync(command, args, { cwd: process.cwd(), stdio: "inherit" });
}

function findCommand(names) {
	for (const name of names) {
		const lookup = spawnSync(process.platform === "win32" ? "where.exe" : "which", [name], {
			encoding: "utf8",
		});
		if (lookup.status === 0) {
			const first = lookup.stdout
				.split(/\r?\n/u)
				.map((line) => line.trim())
				.find(Boolean);
			if (first) return first;
		}
	}
	return undefined;
}

function runUnity(unity) {
	const staged = stageEngine("unity");
	const log = join(staged.temporary, "unity.log");
	const project = join(staged.root, "TestProject");
	try {
		let result;
		if (process.platform === "win32") {
			result = spawnSync(
				"powershell.exe",
				[
					"-NoProfile",
					"-NonInteractive",
					"-ExecutionPolicy",
					"Bypass",
					"-File",
					resolve("scripts/run-unity-gate.ps1"),
					"-EditorPath",
					unity,
					"-ProjectPath",
					project,
					"-LogPath",
					log,
				],
				{
					cwd: process.cwd(),
					stdio: "inherit",
				},
			);
		} else {
			result = spawnSync(
				unity,
				[
					"-batchmode",
					"-nographics",
					"-projectPath",
					project,
					"-executeMethod",
					"OpenGameAgentUnityGate.Run",
					"-logFile",
					log,
				],
				{ cwd: process.cwd(), stdio: "inherit" },
			);
		}

		if (result.error) throw result.error;
		if (result.status !== 0) {
			let details = "";
			try {
				details = readFileSync(log, "utf8");
			} catch {
				// The process can fail before Unity creates its log.
			}
			throw new Error(`Unity gate failed with exit code ${result.status}.\n${details}`);
		}
	} finally {
		staged.dispose();
	}
}

function stageEngine(name) {
	const temporary = mkdtempSync(join(tmpdir(), `opengameagent-${name}-gate-`));
	const root = join(temporary, name);
	cpSync(resolve("engines", name), root, {
		recursive: true,
		filter: (source) =>
			![".godot", "Library", "Logs", "Temp", "UserSettings", "bin", "obj"].includes(
				source.split(/[\\/]/u).at(-1) ?? "",
			),
	});
	return {
		dispose: () => rmSync(temporary, { recursive: true, force: true }),
		root,
		temporary,
	};
}

function assertUnity6(unity) {
	const result = spawnSync(unity, ["-version"], { cwd: process.cwd(), encoding: "utf8" });
	if (result.error) throw result.error;
	const version = result.stdout.trim();
	if (result.status !== 0 || !/^6000\./u.test(version)) {
		throw new Error(
			`Unity gate requires a Unity 6 editor (6000.x); configured editor reported '${version || "unknown"}'.`,
		);
	}
}
