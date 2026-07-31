using System.Globalization;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class NativeWorldAuthoringFixtureTests
{
    private const string ExpectedPackageDigest =
        "16b721a611043382f878a719e677131d99b953c9e7dd92e158ef9f660deaa800";

    private const string ExpectedCatalogDigest =
        "1299dc0875954a73bb21c4cfb9b6aaec499128a493f752b7f8c94301c1c9cb7f";

    private static readonly string[] SemanticFileNames =
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
    public async Task DiskFixtureCompilesAndRunsDeterministically()
    {
        var fixtureDirectory = Path.Combine(
            FindRepositoryRoot(),
            "fixtures",
            "world-v1",
            "interactive-smoke");
        var source = LoadPackage(
            fixtureDirectory,
            SemanticFileNames);
        var reordered = LoadPackage(
            fixtureDirectory,
            SemanticFileNames.Reverse());

        Assert.Equal(source.PackageDigest, reordered.PackageDigest);
        Assert.Equal(ExpectedPackageDigest, source.PackageDigest);
        Assert.Matches("^[0-9a-f]{64}$", source.PackageDigest);

        var package = Compile(source);
        var reorderedPackage = Compile(reordered);

        Assert.Equal(package.World.Digest, reorderedPackage.World.Digest);
        Assert.Equal(
            package.ClocksDigest,
            reorderedPackage.ClocksDigest);
        Assert.Equal(
            package.NumericsDigest,
            reorderedPackage.NumericsDigest);
        Assert.Equal(
            package.EventsDigest,
            reorderedPackage.EventsDigest);
        Assert.Equal(
            package.InteractionsDigest,
            reorderedPackage.InteractionsDigest);
        Assert.Equal(package.Agents.Digest, reorderedPackage.Agents.Digest);
        Assert.Equal(
            package.Knowledge.Digest,
            reorderedPackage.Knowledge.Digest);
        Assert.Equal(
            package.CatalogDigest,
            reorderedPackage.CatalogDigest);
        Assert.Equal(ExpectedCatalogDigest, package.CatalogDigest);
        Assert.Matches("^[0-9a-f]{64}$", package.CatalogDigest);

        Assert.Equal("interactive-smoke", package.World.WorldId);
        Assert.Single(package.Clocks);
        Assert.Single(package.NumericSchemas);
        Assert.Single(package.Events);
        Assert.Single(package.NativeInteractions);
        Assert.Collection(
            package.Agents.Entries,
            item => Assert.Equal("mira", item.EntryId),
            item => Assert.Equal("ren", item.EntryId));
        Assert.Equal(
            "community-garden",
            Assert.Single(package.Knowledge.Entries).EntryId);

        var runtime = NativeWorldRuntime.CreateInMemory(package);
        var initial = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
        Assert.Equal(
            "0",
            initial.State.GetProperty("time")
                .GetProperty("month")
                .GetString());
        var actor = new GameEntityIdentity("mira", 1);
        var target = new GameEntityIdentity("ren", 1);
        var query = await runtime.QueryInteractionsAsync(
            CreateQuery(initial, actor, target));

        Assert.True(query.Succeeded);
        var available = Assert.Single(query.Value!.Items);
        Assert.Equal(
            "offer-garden-help",
            available.InteractionId);
        Assert.Equal(
            InteractionAvailabilityState.Available,
            available.State);

        using var parameters = JsonDocument.Parse(
            """{"topic":"garden"}""");
        var planned = await runtime.PlanInteractionAsync(
            CreateExecution(
                initial,
                package.CatalogDigest,
                actor,
                target,
                parameters.RootElement));

        Assert.True(planned.Succeeded);
        Assert.NotNull(planned.Value);

        var interaction = await runtime.ExecuteInteractionAsync(
            planned.Value!);

        Assert.True(interaction.Succeeded);
        Assert.True(interaction.Value!.Succeeded);
        var afterInteraction =
            Assert.IsType<WorldAuthoritativeStateSnapshot>(
                await runtime.ReadSnapshotAsync());
        Assert.Equal(
            "1250",
            afterInteraction.State.GetProperty("entities")
                .GetProperty("mira")
                .GetProperty("trust")
                .GetString());
        Assert.True(
            afterInteraction.State.GetProperty("entities")
                .GetProperty("ren")
                .GetProperty("helpReceived")
                .GetBoolean());

        var advance = await runtime.AdvanceClockAsync(
            new WorldAdvanceClockCommand(
                "fixture-advance-month",
                "fixture-advance-month-operation",
                afterInteraction.Coordinate,
                "calendar.month",
                expectedClockTick: 0,
                ticks: 1));

        Assert.True(advance.Succeeded);
        Assert.Equal(1, advance.CompletedTicks);
        Assert.True(Assert.Single(advance.TickResults).Committed);
        var final = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
        Assert.Equal(
            "1",
            final.State.GetProperty("time")
                .GetProperty("month")
                .GetString());
        Assert.Equal(
            "1350",
            final.State.GetProperty("entities")
                .GetProperty("mira")
                .GetProperty("trust")
                .GetString());
        Assert.True(final.Coordinate.IsExactMatch(advance.Coordinate));
    }

    private static InteractionQueryRequest CreateQuery(
        WorldAuthoritativeStateSnapshot snapshot,
        GameEntityIdentity actor,
        GameEntityIdentity target)
    {
        var coordinate = snapshot.Coordinate;
        return new InteractionQueryRequest(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(CultureInfo.InvariantCulture),
            actor,
            "local",
            new[] { target });
    }

    private static InteractionExecutionRequest CreateExecution(
        WorldAuthoritativeStateSnapshot snapshot,
        string catalogDigest,
        GameEntityIdentity actor,
        GameEntityIdentity target,
        JsonElement parameters)
    {
        var coordinate = snapshot.Coordinate;
        return new InteractionExecutionRequest(
            "fixture-offer-help",
            "fixture-offer-help-operation",
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
            parameters);
    }

    private static WorldPackageDefinition LoadPackage(
        string fixtureDirectory,
        IEnumerable<string> fileNames)
    {
        var files = fileNames.Select(
                fileName =>
                    new WorldPackageFile(
                        fileName,
                        "application/json",
                        File.ReadAllBytes(
                            Path.Combine(fixtureDirectory, fileName))))
            .ToArray();
        return new WorldPackageDefinition(
            "interactive-smoke-fixture",
            "1",
            files);
    }

    private static ActivatedWorldPackage Compile(
        WorldPackageDefinition definition)
    {
        var result = new NativeWorldPackageCompiler().Compile(definition);
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(
                    item => item.Code
                            + " "
                            + item.Path
                            + " "
                            + item.Message)));
        return Assert.IsType<ActivatedWorldPackage>(result.Package);
    }

    private static string FindRepositoryRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };
        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(
                Path.GetFullPath(candidate));
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
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for world fixtures.");
    }
}
