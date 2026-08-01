using System;
using System.Collections;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameAgent.Unity.Tests
{
    public sealed class UnityHostPlayModeTests
    {
        [UnityTest]
        public IEnumerator HostSurvivesAFrameAndPumpsPostedWork()
        {
            Assert.That(Application.isPlaying, Is.True);
            var root = new GameObject("GameAgentRuntimeTest");
            var host = root.AddComponent<UnityAgentRuntimeHost>();
            var frameProbe = root.AddComponent<UnityFrameProbe>();
            var invoked = false;

            yield return new WaitForSecondsRealtime(0.01f);
            Assert.That(frameProbe.WasUpdated, Is.True);
            Assert.That(host.Dispatcher.TryPost(() => invoked = true), Is.True);
            yield return new WaitForSecondsRealtime(0.01f);
            Assert.That(invoked, Is.True);

            UnityEngine.Object.Destroy(root);
            yield return new WaitForSecondsRealtime(0.01f);
        }

        [UnityTest]
        public IEnumerator DurableToolLoopWritesAPlayerCompatiblePassMarker()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "game-agent-unity-playmode",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var markerPath = Path.Combine(
                directory,
                "durable-loop.pass.json");
            var root = new GameObject(
                "GameAgentRuntimeDurablePlayModeTest");
            var host = root.AddComponent<UnityAgentRuntimeHost>();
            var pending = UnityDurableGateScenario.RunAsync(
                host,
                Path.Combine(directory, "runtime.journal"),
                CancellationToken.None);

            var framesRemaining = 600;
            while (!pending.IsCompleted && framesRemaining-- > 0)
            {
                yield return new WaitForSecondsRealtime(0.01f);
            }

            Assert.That(pending.IsCompleted, Is.True);
            var result = pending.GetAwaiter().GetResult();
            UnityDurableGateScenario.WritePassMarker(
                markerPath,
                "PlayMode",
                result);
            Assert.That(result.Passed, Is.True);
            Assert.That(File.Exists(markerPath), Is.True);
            Assert.That(
                File.ReadAllText(markerPath).IndexOf(
                    "\"status\":\"passed\"",
                    StringComparison.Ordinal) >= 0,
                Is.True);

            var shutdown = host.ShutdownAsync(CancellationToken.None);
            while (!shutdown.IsCompleted)
            {
                yield return new WaitForSecondsRealtime(0.01f);
            }
            shutdown.GetAwaiter().GetResult();

            UnityEngine.Object.Destroy(root);
            yield return new WaitForSecondsRealtime(0.01f);
            Directory.Delete(directory, true);
        }
    }
}
