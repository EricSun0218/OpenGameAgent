using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Generation;
using NUnit.Framework;
using UnityEngine;

namespace GameAgent.Unity.Tests
{
    public sealed class UnityDurableGateEditModeTests
    {
        [Test]
        public void DurableToolLoopWritesAnEditModePassMarker()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "game-agent-unity-editmode",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var markerPath = Path.Combine(
                directory,
                "durable-loop.pass.json");
            var root = new GameObject(
                "GameAgentRuntimeDurableEditModeTest");
            var host = root.AddComponent<UnityAgentRuntimeHost>();

            try
            {
                var pending = UnityDurableGateScenario.RunAsync(
                    host,
                    Path.Combine(directory, "runtime.journal"),
                    CancellationToken.None);
                PumpUntilCompleted(host.Dispatcher, pending);
                var result = pending.GetAwaiter().GetResult();
                UnityDurableGateScenario.WritePassMarker(
                    markerPath,
                    "EditMode",
                    result);

                Assert.That(result.Passed, Is.True);
                Assert.That(File.Exists(markerPath), Is.True);
                Assert.That(
                    File.ReadAllText(markerPath).IndexOf(
                        "\"status\":\"passed\"",
                        StringComparison.Ordinal) >= 0,
                    Is.True);
            }
            finally
            {
                host.ShutdownAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                UnityEngine.Object.DestroyImmediate(root);
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void GenerationTypedApiPreservesFloatingPointStructuredInput()
        {
            var root = new GameObject("GameAgentRuntimeGenerationEditModeTest");
            var host = root.AddComponent<UnityAgentRuntimeHost>();
            try
            {
                host.ConfigureGeneration(UnityGenerationGateScenario.CreateRuntime());
                var pending = host.SubmitGenerationAsync(
                    UnityGenerationGateScenario.CreateRequest(
                        "unity-generation-edit"));
                PumpUntilCompleted(host.Dispatcher, pending);
                var result = pending.GetAwaiter().GetResult();

                Assert.That(
                    result.Status,
                    Is.EqualTo(GenerationJobStatuses.Succeeded));
                Assert.That(
                    result.Output.Value.GetProperty("temperature").GetDouble(),
                    Is.EqualTo(3.5d));
            }
            finally
            {
                host.ShutdownAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void PumpUntilCompleted(
            UnityMainThreadDispatcher dispatcher,
            Task pending)
        {
            var timeout = Stopwatch.StartNew();
            while (!pending.IsCompleted)
            {
                dispatcher.Pump(64, 10);
                Thread.Yield();
                if (timeout.Elapsed > TimeSpan.FromSeconds(5))
                {
                    throw new TimeoutException(
                        "The Unity EditMode durable gate timed out.");
                }
            }
        }
    }
}
