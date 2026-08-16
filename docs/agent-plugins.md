# Agent Plugins 1.0.0

`OpenGameAgent.Plugins` loads the portable portion of an Agent Plugins 1.0.0 package without changing the agent kernel. A loaded package is an ordinary `IGameAgentExtension`: skills register through the existing skill-provider API and MCP servers register through `McpToolConnectorExtension`.

## Supported package layout

```text
my-plugin/
├── plugin.json
├── skills/
│   └── build/
│       └── SKILL.md
├── mcp.json
└── org.example.client/
```

The loader supports:

- the closed `plugin.json` 1.0.0 manifest and its required canonical `$schema`;
- the specification's non-fatal handling for unknown manifest fields and a non-object `extensions` field;
- immediate-child `skills/*/SKILL.md` discovery using the Agent Skills-compatible loader;
- MCP `stdio` and `streamable-http` transports;
- client-owned HTTP headers that override package headers case-insensitively;
- `${PLUGIN_ROOT}` and `${PLUGIN_DATA}` in stdio arguments, environment values, and working directories;
- bounded diagnostics and component-level failure isolation;
- opaque manifest extension objects and safe top-level client extension directories.

Legacy HTTP+SSE is optional in Agent Plugins 1.0.0 and is not implemented. Its entries are diagnosed and skipped without disabling skills or other MCP servers.

## Load and compose

From a source checkout, reference the optional adapter project alongside the core runtime. A matching versioned package artifact is also available on the GitHub Releases page.

```xml
<ItemGroup>
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Plugins/OpenGameAgent.Plugins.csproj" />
</ItemGroup>
```

```csharp
using OpenGameAgent.Plugins;

var package = AgentPluginLoader.Load(
    @"C:\plugins\world-tools",
    new AgentPluginLoadOptions
    {
        PluginDataDirectory = @"C:\game-data\plugins\world-tools",
        McpServerHeaders = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["remote-world-api"] = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + shortLivedToken,
            },
        },
    });

await using var runtime = new GameAgentBuilder(provider, model)
    .UseExtension(package)
    .Build();
```

The runtime owns the package after `UseExtension`. Disposing the runtime closes MCP clients and any default HTTP transport created by the loader.

`PluginDataDirectory` is required for stdio servers because Agent Plugins reserves `PLUGIN_DATA` as client-managed writable storage. When it is absent, only affected stdio entries are skipped; skills and remote MCP entries still load.

## Security and ownership

- Plugin content is untrusted. Loading a skill grants instructions, not tool permission.
- The loader rejects package reparse points and paths that escape the package root. This is stricter than accepting an internal symbolic link and keeps behavior deterministic across Godot, Unity, and server hosts.
- Plugin-relative commands must begin with `./`; bare commands use the platform executable search behavior and are launched as one executable token, never as a shell command.
- Non-loopback HTTP MCP endpoints require HTTPS. The default HTTP client rejects redirects and does not keep cookies.
- Package headers are visible configuration, not a secret store. Supply credentials through `McpServerHeaders` or a client-owned `HttpClient`; client values take precedence.
- If a client-owned `HttpClient` is supplied, the game owns its redirect, authentication, timeout, and disposal policy.
- OpenGameAgent does not dynamically load assemblies declared by a plugin. Game-specific executable extensions remain explicit, compiled `IGameAgentExtension` registrations.

## Portable and client-specific boundaries

Agent Plugins 1.0.0 standardizes skills and MCP server configuration. It does not standardize plugin installation, marketplaces, permissions, sandboxing, OAuth, signatures, dependencies, hooks, or game runtime APIs. Unknown manifest extension objects are retained as bounded JSON but receive no behavior automatically. Top-level client extension directories are exposed through `ClientExtensionDirectories` and `GetClientExtensionDirectory`; the game decides whether it implements a namespace.

The authoritative external specification and schemas are at [agent-plugins.org](https://agent-plugins.org/specification). The loader selects its locally implemented 1.0.0 rules and never downloads a schema while loading a package.
