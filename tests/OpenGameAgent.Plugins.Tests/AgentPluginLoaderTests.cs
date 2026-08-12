using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using OpenGameAgent.Plugins;
using Xunit;

namespace OpenGameAgent.Plugins.Tests;

public sealed class AgentPluginLoaderTests
{
    [Fact]
    public async Task LoadsPortableSkillsStdioMcpAndClientExtensions()
    {
        using var fixture = new PluginFixture();
        fixture.Write(
            "plugin.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
              "name":"game-tools",
              "version":"1.2.3",
              "description":"Portable game tools",
              "keywords":["game","tools"],
              "extensions":{"org.opengameagent":{"profile":"safe"}},
              "futureField":true
            }
            """);
        fixture.Write(
            "skills/greet/SKILL.md",
            """
            ---
            name: greet
            description: Greet a game actor.
            ---

            Greet the current actor using the supplied game context.
            """);
        fixture.Write("org.opengameagent/settings.json", "{}");
        fixture.Write(
            "mcp.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
              "mcpServers":{
                "local-tools":{
                  "type":"stdio",
                  "command":"node",
                  "args":["${PLUGIN_ROOT}/server.js","${PLUGIN_DATA}/state","${UNKNOWN}"],
                  "env":{"CONFIG":"${PLUGIN_ROOT}/config.json"},
                  "cwd":"${PLUGIN_DATA}/work"
                }
              }
            }
            """);

        var data = Path.Combine(fixture.Parent, "data-${PLUGIN_ROOT}");
        var package = AgentPluginLoader.Load(
            fixture.Root,
            new AgentPluginLoadOptions { PluginDataDirectory = data });

        Assert.Equal("game-tools", package.Manifest.Name);
        Assert.Equal("1.2.3", package.Manifest.Version);
        Assert.Equal(new[] { "game", "tools" }, package.Manifest.Keywords);
        Assert.Equal("{\"profile\":\"safe\"}", package.Manifest.Extensions["org.opengameagent"]);
        Assert.Equal(Path.Combine(fixture.Root, "org.opengameagent"), package.GetClientExtensionDirectory("org.opengameagent"));
        Assert.Single(package.Skills);
        Assert.Equal("greet", package.Skills[0].SkillId);
        Assert.Single(package.McpServers);
        Assert.Contains(package.Diagnostics, value => value.Code == "manifest.unknown-field");

        var mcp = Assert.Single(package.McpConfigurations);
        Assert.Equal("node", mcp.Command);
        Assert.Equal(fixture.Root + "/server.js", mcp.Arguments[0]);
        Assert.Equal(data + "/state", mcp.Arguments[1]);
        Assert.Equal("${UNKNOWN}", mcp.Arguments[2]);
        Assert.Equal(Path.Combine(data, "work"), mcp.WorkingDirectory);
        Assert.Equal(fixture.Root, mcp.Environment["PLUGIN_ROOT"]);
        Assert.Equal(data, mcp.Environment["PLUGIN_DATA"]);
        Assert.Equal(fixture.Root + "/config.json", mcp.Environment["CONFIG"]);
        Assert.Contains("${PLUGIN_ROOT}", mcp.Arguments[1], StringComparison.Ordinal);

        await using var runtime = new GameAgentBuilder(new EmptyProvider(), "test")
            .UseExtension(package)
            .Build();
        Assert.Contains(runtime.ExtensionResources, value => value.Kind == GameAgentExtensionResourceKind.SkillProvider);
        Assert.Contains(runtime.ExtensionResources, value => value.Kind == GameAgentExtensionResourceKind.ToolProvider);
    }

    [Fact]
    public async Task InvalidMcpComponentDoesNotDisableValidSkills()
    {
        using var fixture = PluginFixture.Minimal("partial-plugin");
        fixture.Write(
            "skills/valid/SKILL.md",
            """
            ---
            name: valid
            description: A valid skill.
            ---
            Do valid work.
            """);
        fixture.Write(
            "mcp.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
              "mcpServers":{},
              "unknown":true
            }
            """);

        await using var package = AgentPluginLoader.Load(fixture.Root);

        Assert.Single(package.Skills);
        Assert.Empty(package.McpServers);
        Assert.Contains(package.Diagnostics, value => value.Code == "mcp.invalid-component");
    }

    [Fact]
    public async Task DiscoversOnlyImmediateSkillChildrenAndSkipsInvalidSkills()
    {
        using var fixture = PluginFixture.Minimal("skill-layout");
        fixture.Write(
            "skills/valid/SKILL.md",
            """
            ---
            name: valid
            description: A valid skill.
            ---
            Valid instructions.
            """);
        fixture.Write(
            "skills/bad/SKILL.md",
            """
            ---
            name: bad
            ---
            Missing description.
            """);
        fixture.Write(
            "skills/container/nested/SKILL.md",
            """
            ---
            name: nested
            description: Must not be recursively discovered.
            ---
            Nested instructions.
            """);
        fixture.Write(
            "skills/duplicate/SKILL.md",
            """
            ---
            name: valid
            description: Duplicate portable name.
            ---
            Duplicate instructions.
            """);

        await using var package = AgentPluginLoader.Load(fixture.Root);

        var skill = Assert.Single(package.Skills);
        Assert.Equal("valid", skill.SkillId);
        Assert.DoesNotContain(package.Skills, value => value.SkillId == "nested");
        Assert.Contains(package.Diagnostics, value => value.Code == "skills.duplicate-name");
        Assert.Contains(package.Diagnostics, value => value.Component == "skills");
    }

    [Theory]
    [InlineData("{\"name\":\"missing-schema\"}")]
    [InlineData("{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"Uppercase\"}")]
    [InlineData("{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"bad-author\",\"author\":{\"company\":\"x\"}}")]
    [InlineData("{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"bad-extension\",\"extensions\":{\"com.example\":true}}")]
    [InlineData("{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"duplicate\",\"name\":\"duplicate\"}")]
    public void RejectsInvalidCoreManifests(string manifest)
    {
        using var fixture = new PluginFixture();
        fixture.Write("plugin.json", manifest);

        Assert.Throws<AgentPluginLoadException>(() => AgentPluginLoader.Load(fixture.Root));
    }

    [Fact]
    public async Task IgnoresANonObjectExtensionsFieldAsRequiredByTheFailureBoundary()
    {
        using var fixture = new PluginFixture();
        fixture.Write(
            "plugin.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
              "name":"ignored-extensions",
              "extensions":"not-an-object"
            }
            """);

        await using var package = AgentPluginLoader.Load(fixture.Root);

        Assert.Empty(package.Manifest.Extensions);
        Assert.Contains(package.Diagnostics, value => value.Code == "manifest.invalid-extensions");
    }

    [Fact]
    public async Task DoesNotValidateContentsOfUnknownManifestAndClientExtensionObjects()
    {
        using var fixture = new PluginFixture();
        fixture.Write(
            "plugin.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
              "name":"opaque-extensions",
              "future":{"same":1,"same":2},
              "extensions":{"ANY opaque namespace":{"same":1,"same":2}}
            }
            """);

        await using var package = AgentPluginLoader.Load(fixture.Root);

        Assert.Contains("\"same\":1,\"same\":2", package.Manifest.Extensions["ANY opaque namespace"]);
        Assert.Contains(package.Diagnostics, value => value.Code == "manifest.unknown-field");
    }

    [Fact]
    public async Task LoadsStreamableHttpWithClientHeaderPrecedenceAndSkipsLegacySse()
    {
        using var fixture = PluginFixture.Minimal("remote-tools");
        fixture.Write(
            "mcp.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
              "mcpServers":{
                "remote":{
                  "type":"streamable-http",
                  "url":"https://example.test/mcp",
                  "headers":{"X-Tenant":"package","X-Public":"visible"}
                },
                "legacy":{"type":"sse","url":"https://example.test/sse"}
              }
            }
            """);
        var options = new AgentPluginLoadOptions
        {
            McpServerHeaders = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["remote"] = new Dictionary<string, string>
                {
                    ["x-tenant"] = "client",
                    ["Authorization"] = "Bearer client-owned",
                },
            },
        };

        await using var package = AgentPluginLoader.Load(fixture.Root, options);

        var server = Assert.Single(package.McpServers);
        Assert.Equal("remote", server.Id);
        Assert.Equal(AgentPluginMcpTransport.StreamableHttp, server.Transport);
        var configuration = Assert.Single(package.McpConfigurations);
        Assert.Equal("client", configuration.Headers["X-Tenant"]);
        Assert.Equal("visible", configuration.Headers["X-Public"]);
        Assert.Equal("Bearer client-owned", configuration.Headers["Authorization"]);
        Assert.Contains(package.Diagnostics, value => value.Code == "mcp.unsupported-transport" && value.Component == "legacy");
    }

    [Fact]
    public async Task EnforcesRemoteHttpAndPackagePathSafetyPerServer()
    {
        using var fixture = PluginFixture.Minimal("safe-tools");
        fixture.Write(
            "mcp.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
              "mcpServers":{
                "public-http":{"type":"streamable-http","url":"http://example.test/mcp"},
                "loopback":{"type":"streamable-http","url":"http://127.0.0.1:9123/mcp"},
                "escape":{"type":"stdio","command":"./../outside.exe"}
              }
            }
            """);

        await using var package = AgentPluginLoader.Load(
            fixture.Root,
            new AgentPluginLoadOptions { PluginDataDirectory = fixture.Data });

        var server = Assert.Single(package.McpServers);
        Assert.Equal("loopback", server.Id);
        Assert.Equal(2, package.Diagnostics.Count(value => value.Code == "mcp.invalid-server"));
    }

    [Fact]
    public async Task MissingPluginDataSkipsOnlyStdioServers()
    {
        using var fixture = PluginFixture.Minimal("mixed-tools");
        fixture.Write(
            "mcp.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
              "mcpServers":{
                "local":{"type":"stdio","command":"node"},
                "remote":{"type":"streamable-http","url":"https://example.test/mcp"}
              }
            }
            """);

        await using var package = AgentPluginLoader.Load(fixture.Root);

        var server = Assert.Single(package.McpServers);
        Assert.Equal("remote", server.Id);
        Assert.Contains(package.Diagnostics, value => value.Component == "local" && value.Code == "mcp.invalid-server");
    }

    [Fact]
    public async Task DuplicateCaseInsensitiveHeadersInvalidateOnlyTheirServer()
    {
        using var fixture = PluginFixture.Minimal("header-tools");
        fixture.Write(
            "mcp.json",
            """
            {
              "$schema":"https://agent-plugins.org/schemas/1.0.0/mcp.schema.json",
              "mcpServers":{
                "bad":{"type":"streamable-http","url":"https://example.test/mcp","headers":{"X-Key":"a","x-key":"b"}},
                "good":{"type":"streamable-http","url":"https://example.test/good"}
              }
            }
            """);

        await using var package = AgentPluginLoader.Load(fixture.Root);

        Assert.Equal("good", Assert.Single(package.McpServers).Id);
        Assert.Contains(package.Diagnostics, value => value.Component == "bad" && value.Code == "mcp.invalid-server");
    }

    [Theory]
    [InlineData("skills", "skills.invalid-location")]
    [InlineData("mcp.json", "mcp.invalid-location")]
    public async Task WrongFixedComponentKindsAreDiagnosedWithoutRejectingThePlugin(
        string componentPath,
        string expectedDiagnostic)
    {
        using var fixture = PluginFixture.Minimal("wrong-component-kind");
        if (string.Equals(componentPath, "skills", StringComparison.Ordinal))
        {
            fixture.Write(componentPath, "not a directory");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, componentPath));
        }

        await using var package = AgentPluginLoader.Load(fixture.Root);

        Assert.Empty(package.Skills);
        Assert.Empty(package.McpServers);
        Assert.Contains(package.Diagnostics, value => value.Code == expectedDiagnostic);
    }

    private sealed class EmptyProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class PluginFixture : IDisposable
    {
        public PluginFixture()
        {
            Parent = Path.Combine(Path.GetTempPath(), "oga-plugin-tests-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Parent, "plugin");
            Data = Path.Combine(Parent, "data");
            Directory.CreateDirectory(Root);
        }

        public string Parent { get; }

        public string Root { get; }

        public string Data { get; }

        public static PluginFixture Minimal(string name)
        {
            var fixture = new PluginFixture();
            fixture.Write(
                "plugin.json",
                $$"""
                {
                  "$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
                  "name":"{{name}}"
                }
                """);
            return fixture;
        }

        public void Write(string relativePath, string value)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }

        public void Dispose()
        {
            if (Directory.Exists(Parent))
            {
                Directory.Delete(Parent, recursive: true);
            }
        }
    }
}
