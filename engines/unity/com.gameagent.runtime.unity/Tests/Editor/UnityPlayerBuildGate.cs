using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GameAgent.Protocol;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameAgent.Unity.Tests
{
    public static class UnityPlayerBuildGate
    {
        public static void BuildWindowsMono()
        {
            BuildWindows(ScriptingImplementation.Mono2x, "Mono");
        }

        public static void BuildWindowsIl2Cpp()
        {
            BuildWindows(ScriptingImplementation.IL2CPP, "IL2CPP");
        }

        private static void BuildWindows(
            ScriptingImplementation backend,
            string backendName)
        {
            const string scenePath =
                "Assets/GameAgentUnityBuildGate.unity";
            var previousBackend = PlayerSettings.GetScriptingBackend(
                BuildTargetGroup.Standalone);
            var previousSceneSetup =
                EditorSceneManager.GetSceneManagerSetup();
            var createdScene = false;

            try
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath)
                    != null)
                {
                    throw new InvalidOperationException(
                        "Unity Player gate scene path is already in use: "
                        + scenePath);
                }

                PlayerSettings.SetScriptingBackend(
                    BuildTargetGroup.Standalone,
                    backend);
                var scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Single);
                var root = new GameObject(
                    "GameAgentUnityDurablePlayerGate");
                root.AddComponent<UnityDurablePlayerGateBootstrap>();
                createdScene = true;
                if (!EditorSceneManager.SaveScene(scene, scenePath))
                {
                    throw new InvalidOperationException(
                        "Unity Player gate scene could not be saved.");
                }
                var outputDirectory = Path.GetFullPath(
                    Path.Combine(
                        "Builds",
                        "GameAgentUnity",
                        backendName));
                Directory.CreateDirectory(outputDirectory);
                var report = BuildPipeline.BuildPlayer(
                    new BuildPlayerOptions
                    {
                        scenes = new[] { scenePath },
                        locationPathName = Path.Combine(
                            outputDirectory,
                            "GameAgentUnityGate.exe"),
                        target = BuildTarget.StandaloneWindows64,
                        options = BuildOptions.Development
                                  | BuildOptions.IncludeTestAssemblies
                    });

                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Unity " + backendName + " build failed: "
                        + report.summary.result + ", errors="
                        + report.summary.totalErrors);
                }

                RunPlayerGate(
                    Path.Combine(
                        outputDirectory,
                        "GameAgentUnityGate.exe"),
                    outputDirectory,
                    backendName);
            }
            finally
            {
                try
                {
                    if (createdScene)
                    {
                        AssetDatabase.DeleteAsset(scenePath);
                    }
                }
                finally
                {
                    try
                    {
                        EditorSceneManager.RestoreSceneManagerSetup(
                            previousSceneSetup);
                    }
                    finally
                    {
                        PlayerSettings.SetScriptingBackend(
                            BuildTargetGroup.Standalone,
                            previousBackend);
                    }
                }
            }
        }

        private static void RunPlayerGate(
            string playerPath,
            string outputDirectory,
            string backendName)
        {
            var markerPath = Path.Combine(
                outputDirectory,
                "durable-loop.pass.json");
            var logPath = Path.Combine(
                outputDirectory,
                "durable-loop.player.log");
            DeleteIfPresent(markerPath);
            DeleteIfPresent(markerPath + ".journal");
            DeleteIfPresent(logPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = playerPath,
                Arguments =
                    "-batchmode -nographics -logFile "
                    + QuoteArgument(logPath)
                    + " -gameAgentGateMarker "
                    + QuoteArgument(markerPath)
                    + " -gameAgentGateBackend "
                    + QuoteArgument(backendName),
                WorkingDirectory = outputDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "Unity " + backendName
                        + " Player could not be started.");
                }

                if (!process.WaitForExit(120_000))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new TimeoutException(
                        "Unity " + backendName
                        + " Player gate timed out. "
                        + ReadLogTail(logPath));
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "Unity " + backendName
                        + " Player gate failed with exit code "
                        + process.ExitCode + ". "
                        + ReadMarkerOrLog(markerPath, logPath));
                }
            }

            AssertPassMarker(markerPath, backendName);
        }

        private static void AssertPassMarker(
            string markerPath,
            string backendName)
        {
            if (!File.Exists(markerPath))
            {
                throw new InvalidOperationException(
                    "Unity " + backendName
                    + " Player did not write its durable pass marker.");
            }

            var marker = ProtocolJson.ParseElement(
                File.ReadAllText(markerPath));
            if (marker.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Unity Player pass marker is not a JSON object.");
            }

            RequireString(
                marker,
                "schema",
                UnityDurableGateScenario.MarkerSchema);
            RequireString(marker, "status", "passed");
            RequireString(marker, "backend", backendName);
            RequireString(
                marker,
                "runId",
                UnityDurableGateScenario.RunId);
            RequireString(marker, "state", RunStates.Completed);
            RequireNumber(marker, "providerCalls", 2);
            RequireNumber(marker, "actionCalls", 1);
            RequireTrue(marker, "mainThreadReceipt");
            RequireTrue(marker, "actionRequested");
            RequireTrue(marker, "actionReceived");
            RequireTrue(marker, "protocolRoundTrip");
            RequireTrue(marker, "structuredContext");
            RequireTrue(marker, "toolResultFeedback");
            RequireTrue(marker, "transcriptToolResult");
        }

        private static void RequireString(
            JsonElement marker,
            string propertyName,
            string expected)
        {
            JsonElement value;
            if (!marker.TryGetProperty(propertyName, out value)
                || value.ValueKind != JsonValueKind.String
                || !string.Equals(
                    value.GetString(),
                    expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Unity Player marker property '"
                    + propertyName + "' is invalid.");
            }
        }

        private static void RequireNumber(
            JsonElement marker,
            string propertyName,
            int expected)
        {
            JsonElement value;
            int actual;
            if (!marker.TryGetProperty(propertyName, out value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out actual)
                || actual != expected)
            {
                throw new InvalidOperationException(
                    "Unity Player marker property '"
                    + propertyName + "' is invalid.");
            }
        }

        private static void RequireTrue(
            JsonElement marker,
            string propertyName)
        {
            JsonElement value;
            if (!marker.TryGetProperty(propertyName, out value)
                || value.ValueKind != JsonValueKind.True)
            {
                throw new InvalidOperationException(
                    "Unity Player marker property '"
                    + propertyName + "' is invalid.");
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\""
                   + value.Replace("\"", "\\\"")
                   + "\"";
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static string ReadMarkerOrLog(
            string markerPath,
            string logPath)
        {
            if (File.Exists(markerPath))
            {
                return "Marker: " + File.ReadAllText(markerPath);
            }

            return ReadLogTail(logPath);
        }

        private static string ReadLogTail(string logPath)
        {
            if (!File.Exists(logPath))
            {
                return "No Player log was produced.";
            }

            var log = File.ReadAllText(logPath);
            const int maximum = 4000;
            return log.Length <= maximum
                ? log
                : log.Substring(log.Length - maximum, maximum);
        }
    }
}
