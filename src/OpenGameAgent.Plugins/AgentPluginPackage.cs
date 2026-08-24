using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Connectors.Mcp;

namespace OpenGameAgent.Plugins;

/// <summary>
/// A loaded Agent Plugins 1.0.0 package. Add this object to a GameAgentBuilder as a normal
/// extension; the runtime then owns its MCP connections and transport lifetime.
/// </summary>
public sealed class AgentPluginPackage : IGameAgentExtension, IAsyncDisposable
{
    private readonly InMemoryGameSkillSource _skillSource;
    private readonly McpToolConnectorExtension? _mcpExtension;
    private readonly HttpClient? _ownedHttpClient;
    private int _configured;
    private int _disposed;

    internal AgentPluginPackage(
        string rootDirectory,
        string? dataDirectory,
        AgentPluginManifest manifest,
        IReadOnlyList<GameSkill> skills,
        IReadOnlyList<AgentPluginMcpServerInfo> mcpServers,
        IReadOnlyDictionary<string, string> clientExtensionDirectories,
        IReadOnlyList<AgentPluginDiagnostic> diagnostics,
        McpToolConnectorExtension? mcpExtension,
        HttpClient? ownedHttpClient,
        IReadOnlyList<AgentPluginMcpConfiguration> mcpConfigurations)
    {
        RootDirectory = rootDirectory;
        DataDirectory = dataDirectory;
        Manifest = manifest;
        Skills = skills;
        McpServers = mcpServers;
        ClientExtensionDirectories = clientExtensionDirectories;
        Diagnostics = diagnostics;
        _skillSource = new InMemoryGameSkillSource(skills, Math.Max(1, skills.Count));
        _mcpExtension = mcpExtension;
        _ownedHttpClient = ownedHttpClient;
        McpConfigurations = mcpConfigurations;

        var capabilities = new List<string> { "agent-plugins-1.0" };
        if (skills.Count > 0)
        {
            capabilities.Add("skills");
        }

        if (mcpServers.Count > 0)
        {
            capabilities.Add("mcp");
        }

        Descriptor = new GameAgentExtensionDescriptor(
            "agent-plugin." + manifest.Name,
            string.IsNullOrWhiteSpace(manifest.Version) ? "0.0.0" : manifest.Version!,
            manifest.Description,
            capabilities);
    }

    public string RootDirectory { get; }

    public string? DataDirectory { get; }

    public AgentPluginManifest Manifest { get; }

    public IReadOnlyList<GameSkill> Skills { get; }

    public IReadOnlyList<AgentPluginMcpServerInfo> McpServers { get; }

    public IReadOnlyDictionary<string, string> ClientExtensionDirectories { get; }

    public IReadOnlyList<AgentPluginDiagnostic> Diagnostics { get; }

    public GameAgentExtensionDescriptor Descriptor { get; }

    internal IReadOnlyList<AgentPluginMcpConfiguration> McpConfigurations { get; }

    public string? GetClientExtensionDirectory(string extensionNamespace)
    {
        if (string.IsNullOrWhiteSpace(extensionNamespace))
        {
            throw new ArgumentException("An extension namespace is required.", nameof(extensionNamespace));
        }

        return ClientExtensionDirectories.TryGetValue(extensionNamespace, out var path) ? path : null;
    }

    public void Configure(GameAgentExtensionApi api)
    {
        if (api is null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentPluginPackage));
        }

        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            throw new InvalidOperationException("An Agent Plugin package can configure only one runtime.");
        }

        if (Skills.Count > 0)
        {
            api.RegisterSkillProvider(
                "portable-skills",
                (context, activeTools, maximumSkills, maximumCharacters, cancellationToken) =>
                    _skillSource.SelectAsync(
                        new GameSkillQuery(context.Input, activeTools, maximumSkills, maximumCharacters),
                        cancellationToken));
        }

        _mcpExtension?.Configure(api);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        if (_mcpExtension is not null)
        {
            try
            {
                await _mcpExtension.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        _ownedHttpClient?.Dispose();
        if (failure is not null)
        {
            throw failure;
        }
    }

    internal static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    internal static IReadOnlyDictionary<string, T> ReadOnlyDictionary<T>(
        IDictionary<string, T> values,
        StringComparer comparer) =>
        new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(values, comparer));
}
