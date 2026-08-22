using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class ExtensionDevelopmentKitTests
{
    [Fact]
    public void ManifestParserValidatesIdentityPermissionsDependenciesAndUnknownFields()
    {
        var manifest = GameExtensionDevelopmentManifest.Parse(
            """
            {
              "schemaVersion":"1",
              "id":"sample.extension",
              "version":"1.2.3-beta.1",
              "permissions":["tools.register"],
              "dependencies":[{"id":"base.extension","minimumVersion":"2.0.0"}]
            }
            """);

        Assert.Equal("sample.extension", manifest.Id);
        Assert.Equal("1.2.3-beta.1", manifest.Version);
        Assert.Equal(GameExtensionPermissions.ToolsRegister, Assert.Single(manifest.Permissions));
        Assert.Equal("base.extension", Assert.Single(manifest.Dependencies).Id);

        Assert.Throws<FormatException>(() => GameExtensionDevelopmentManifest.Parse(
            """{"schemaVersion":"1","id":"x","version":"1.0.0","unknown":true}"""));
        Assert.Throws<ArgumentException>(() => GameExtensionDevelopmentManifest.Parse(
            """{"schemaVersion":"1","id":"x","version":"1.0.0","permissions":["process.execute"]}"""));
        Assert.Throws<ArgumentException>(() => GameExtensionDevelopmentManifest.Parse(
            """{"schemaVersion":"1","id":"x","version":"01.0.0"}"""));
        Assert.Throws<ArgumentException>(() => GameExtensionDevelopmentManifest.Parse(
            """{"schemaVersion":"1","id":"x","version":"1.0.0-01"}"""));
        Assert.Throws<ArgumentException>(() => GameExtensionDevelopmentManifest.Parse(
            """{"schemaVersion":"1","id":"x","version":"1.0.0+"}"""));

        var withMetadata = GameExtensionDevelopmentManifest.Parse(
            """{"schemaVersion":"1","id":"x","version":"1.0.0+engine.7"}""");
        Assert.Equal("1.0.0+engine.7", withMetadata.Version);
    }

    [Fact]
    public async Task ConformanceRunsARealRuntimeAndAcceptsDeclaredResources()
    {
        var extension = ToolExtension("sample.extension", "1.0.0");
        var manifest = new GameExtensionDevelopmentManifest(
            "sample.extension",
            "1.0.0",
            new[] { GameExtensionPermissions.ToolsRegister });

        var report = await GameExtensionConformance.RunAsync(
            extension,
            manifest,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(1, report.ModelRequestCount);
        Assert.Equal(GameAgentExtensionResourceKind.Tool, Assert.Single(report.Resources).Kind);
    }

    [Fact]
    public async Task ConformanceFailsBeforeModelForUndeclaredOrHostDeniedPermissions()
    {
        var undeclared = await GameExtensionConformance.RunAsync(
            ToolExtension("sample.extension", "1.0.0"),
            new GameExtensionDevelopmentManifest("sample.extension", "1.0.0"),
            cancellationToken: TestContext.Current.CancellationToken);
        var denied = await GameExtensionConformance.RunAsync(
            ToolExtension("sample.extension", "1.0.0"),
            new GameExtensionDevelopmentManifest(
                "sample.extension",
                "1.0.0",
                new[] { GameExtensionPermissions.ToolsRegister }),
            new GameExtensionConformanceOptions { AllowedPermissions = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        Assert.False(undeclared.Passed);
        Assert.Contains(undeclared.Diagnostics, value => value.Code == "extension.permission-undeclared");
        Assert.Equal(0, undeclared.ModelRequestCount);
        Assert.False(denied.Passed);
        Assert.Contains(denied.Diagnostics, value => value.Code == "extension.permission-denied");
        Assert.Empty(denied.Resources);
        Assert.Equal(0, denied.ModelRequestCount);
    }

    [Fact]
    public async Task ConformanceReportsMissingAndOldDependenciesBeforeConfiguration()
    {
        var extension = new CountingExtension("dependent", "1.0.0");
        var manifest = new GameExtensionDevelopmentManifest(
            "dependent",
            "1.0.0",
            dependencies: new[] { new GameExtensionDependency("base", "2.0.0") });

        var missing = await GameExtensionConformance.RunAsync(
            extension,
            manifest,
            cancellationToken: TestContext.Current.CancellationToken);
        var old = await GameExtensionConformance.RunAsync(
            new CountingExtension("dependent", "1.0.0"),
            manifest,
            new GameExtensionConformanceOptions
            {
                AvailableExtensions = new[] { new GameAgentExtensionDescriptor("base", "1.9.9") },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(missing.Diagnostics, value => value.Code == "extension.dependency-missing");
        Assert.Equal(0, extension.ConfigureCount);
        Assert.Contains(old.Diagnostics, value => value.Code == "extension.dependency-version");

        var prerelease = await GameExtensionConformance.RunAsync(
            new CountingExtension("dependent", "1.0.0"),
            new GameExtensionDevelopmentManifest(
                "dependent",
                "1.0.0",
                dependencies: new[] { new GameExtensionDependency("base", "2.0.0-beta.2") }),
            new GameExtensionConformanceOptions
            {
                AvailableExtensions = new[] { new GameAgentExtensionDescriptor("base", "2.0.0-beta.10") },
            },
            TestContext.Current.CancellationToken);
        Assert.True(prerelease.Passed);
    }

    [Fact]
    public async Task ConformanceClassifiesConfigurationAndLifecycleFailures()
    {
        var configuration = await GameExtensionConformance.RunAsync(
            new ThrowingExtension(throwDuringConfiguration: true),
            new GameExtensionDevelopmentManifest("throwing", "1.0.0"),
            cancellationToken: TestContext.Current.CancellationToken);
        var lifecycle = await GameExtensionConformance.RunAsync(
            new ThrowingExtension(throwDuringConfiguration: false),
            new GameExtensionDevelopmentManifest(
                "throwing",
                "1.0.0",
                new[] { GameExtensionPermissions.EventsSubscribe }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(configuration.Diagnostics, value => value.Code == "extension.configure");
        Assert.Contains(lifecycle.Diagnostics, value => value.Code == "extension.runtime-diagnostic");
    }

    private static IGameAgentExtension ToolExtension(string id, string version) =>
        new DelegateGameAgentExtension(
            new GameAgentExtensionDescriptor(id, version),
            api => api.RegisterTool(new AgentTool(
                new ToolDefinition(
                    "inspect_state",
                    "Read a bounded test state.",
                    "{\"type\":\"object\",\"additionalProperties\":false}"),
                (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                    new AgentContent[] { new TextContent("ok") })),
                ToolRisk.ReadOnly)));

    private sealed class CountingExtension : IGameAgentExtension
    {
        public CountingExtension(string id, string version)
        {
            Descriptor = new GameAgentExtensionDescriptor(id, version);
        }

        public GameAgentExtensionDescriptor Descriptor { get; }
        public int ConfigureCount { get; private set; }

        public void Configure(GameAgentExtensionApi api) => ConfigureCount++;
    }

    private sealed class ThrowingExtension : IGameAgentExtension
    {
        private readonly bool _throwDuringConfiguration;

        public ThrowingExtension(bool throwDuringConfiguration)
        {
            _throwDuringConfiguration = throwDuringConfiguration;
        }

        public GameAgentExtensionDescriptor Descriptor { get; } = new("throwing", "1.0.0");

        public void Configure(GameAgentExtensionApi api)
        {
            if (_throwDuringConfiguration)
            {
                throw new InvalidOperationException("configuration detail");
            }

            api.On(GameAgentExtensionEvents.InputReceived, (_, _, _) =>
                throw new InvalidOperationException("lifecycle detail"));
        }
    }
}
