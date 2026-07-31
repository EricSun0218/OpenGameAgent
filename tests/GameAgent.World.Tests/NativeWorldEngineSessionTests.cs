using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class NativeWorldEngineSessionTests
{
    private static readonly string[] FixtureFiles =
    {
        "world.json",
        "clocks.json",
        "numerics.json",
        "events.json",
        "interactions.json",
        "agents.json",
        "knowledge.json"
    };

    [Fact]
    public async Task PackageSaveAndInteractionUseOneAtomicGeneration()
    {
        var archive = FixtureArchive();
        await using var session = new NativeWorldEngineSession();

        var loaded = await session.LoadPackageAsync(archive);

        Assert.True(loaded.Activated);
        Assert.Equal(1, loaded.Generation);
        Assert.Equal(
            loaded.Definition.PackageDigest,
            session.Status.ActivePackageDigest);
        var initial = Require(await session.ReadSnapshotAsync());
        var actor = new GameEntityIdentity("mira", 1);
        var target = new GameEntityIdentity("ren", 1);
        var planned = await session.PlanInteractionAsync(
            Interaction(initial, loaded.Package!.CatalogDigest, actor, target));
        Assert.True(planned.Succeeded);
        var admitted = Assert.IsType<NativeWorldEnginePlannedInteraction>(
            planned.Value);
        var beforeInteraction = await session.CaptureSaveBytesAsync();

        var executed = await session.ExecuteInteractionAsync(admitted);

        Assert.True(executed.Succeeded);
        Assert.True(executed.Value!.Succeeded);
        var afterInteraction = Require(await session.ReadSnapshotAsync());
        Assert.Equal(
            "1250",
            afterInteraction.State.GetProperty("entities")
                .GetProperty("mira")
                .GetProperty("trust")
                .GetString());

        var restored = await session.LoadSaveAsync(beforeInteraction);

        Assert.Equal(2, restored.Generation);
        var restoredSnapshot = Require(await session.ReadSnapshotAsync());
        Assert.True(
            initial.Coordinate.IsExactMatch(restoredSnapshot.Coordinate));
        Assert.Equal(initial.StateDigest, restoredSnapshot.StateDigest);
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.ExecuteInteractionAsync(admitted));

        var currentPlan = await session.PlanInteractionAsync(
            Interaction(
                restoredSnapshot,
                loaded.Package.CatalogDigest,
                actor,
                target,
                commandSuffix: "after-load"));
        var currentExecution = await session.ExecuteInteractionAsync(
            Assert.IsType<NativeWorldEnginePlannedInteraction>(
                currentPlan.Value));
        Assert.True(currentExecution.Succeeded);
    }

    [Fact]
    public async Task InvalidReplacementLeavesLiveGenerationUntouched()
    {
        await using var session = new NativeWorldEngineSession();
        var loaded = await session.LoadPackageAsync(FixtureArchive());
        var before = Require(await session.ReadSnapshotAsync());
        var invalid = new WorldPackageDefinition(
            "invalid",
            "1",
            new[]
            {
                new WorldPackageFile(
                    "world.json",
                    "application/json",
                    Encoding.UTF8.GetBytes(
                        """{"contract":"wrong"}"""))
            });

        var rejected = await session.LoadPackageAsync(Archive(invalid));

        Assert.False(rejected.Activated);
        Assert.Contains(
            rejected.Diagnostics,
            item => item.Severity
                    == WorldSemanticDiagnosticSeverity.Error);
        Assert.Equal(loaded.Generation, session.Status.Generation);
        var after = Require(await session.ReadSnapshotAsync());
        Assert.True(before.Coordinate.IsExactMatch(after.Coordinate));
        Assert.Equal(before.StateDigest, after.StateDigest);
    }

    [Fact]
    public async Task GenerationSwapDrainsOldOperationsAndPausesAdmission()
    {
        await using var session = new NativeWorldEngineSession();
        await session.LoadPackageAsync(FixtureArchive());
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = session.RunAsync(
                "held-authority",
                authoritative: true,
                async (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return true;
                })
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = session.LoadPackageAsync(
                FixtureArchive(),
                timelineId: "replacement")
            .AsTask();
        await WaitUntilAsync(
            () => !session.Status.IsAcceptingOperations,
            TimeSpan.FromSeconds(5));
        Assert.False(replacement.IsCompleted);

        release.TrySetResult();
        Assert.True(await running);
        var loaded = await replacement.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, loaded.Generation);
        var snapshot = Require(await session.ReadSnapshotAsync());
        Assert.Equal("replacement", snapshot.Coordinate.TimelineId);
    }

    [Fact]
    public async Task ControlledShutdownReportsUnsettledAuthority()
    {
        var session = new NativeWorldEngineSession();
        await session.LoadPackageAsync(FixtureArchive());
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = session.RunAsync(
                "authority-that-must-settle",
                authoritative: true,
                async (_, cancellationToken) =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return true;
                })
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        var incomplete =
            await Assert.ThrowsAsync<
                NativeWorldEngineShutdownIncompleteException>(
                async () => await session.ShutdownAsync(timeout.Token));

        Assert.Contains(
            "authority-that-must-settle",
            incomplete.OutstandingOperationIds);
        Assert.Contains(
            "authority-that-must-settle",
            incomplete.AuthoritativeOperationIds);
        Assert.Equal(
            NativeWorldEngineSessionState.Stopping,
            session.Status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.ReadSnapshotAsync());

        release.TrySetResult();
        Assert.True(await running);
        var report = await session.ShutdownAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, report.SettledOperationCount);
        Assert.Equal(
            NativeWorldEngineSessionState.Stopped,
            session.Status.State);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task EmergencyDisposeReturnsWithoutUncooperativeOperation()
    {
        var session = new NativeWorldEngineSession();
        await session.LoadPackageAsync(FixtureArchive());
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = session.RunAsync(
                "detached-authority",
                authoritative: true,
                async (_, cancellationToken) =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return true;
                })
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await session.DisposeAsync();

        Assert.Equal(
            NativeWorldEngineSessionState.Disposed,
            session.Status.State);
        Assert.False(running.IsCompleted);
        release.TrySetResult();
        Assert.True(await running.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static InteractionExecutionRequest Interaction(
        WorldAuthoritativeStateSnapshot snapshot,
        string catalogDigest,
        GameEntityIdentity actor,
        GameEntityIdentity target,
        string commandSuffix = "initial")
    {
        var coordinate = snapshot.Coordinate;
        return new InteractionExecutionRequest(
            "session-interaction-" + commandSuffix,
            "session-interaction-operation-" + commandSuffix,
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(CultureInfo.InvariantCulture),
            catalogDigest,
            "offer-garden-help",
            "1",
            actor,
            new[] { target },
            "local",
            Json("""{"topic":"garden"}"""));
    }

    private static byte[] FixtureArchive()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "fixtures",
            "world-v1",
            "interactive-smoke");
        var definition = new WorldPackageDefinition(
            "interactive-smoke-fixture",
            "1",
            FixtureFiles.Select(
                fileName => new WorldPackageFile(
                    fileName,
                    "application/json",
                    File.ReadAllBytes(
                        Path.Combine(directory, fileName)))));
        return Archive(definition);
    }

    private static byte[] Archive(WorldPackageDefinition definition)
    {
        using var stream = new MemoryStream();
        WorldPackageArchive.Write(stream, definition);
        return stream.ToArray();
    }

    private static WorldAuthoritativeStateSnapshot Require(
        WorldAuthoritativeStateSnapshot? snapshot)
    {
        return snapshot
               ?? throw new InvalidOperationException(
                   "The session returned no authoritative state.");
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "GameAgentRuntime.sln"))
                && Directory.Exists(
                    Path.Combine(
                        directory.FullName,
                        "fixtures",
                        "world-v1",
                        "interactive-smoke")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the interactive-world fixture.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow - started >= timeout)
            {
                throw new TimeoutException(
                    "The expected session state was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
