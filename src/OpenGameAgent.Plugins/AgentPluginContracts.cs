using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using OpenGameAgent.Connectors.Mcp;

namespace OpenGameAgent.Plugins;

public static class AgentPluginSpecification
{
    public const string Version = "1.0.0";

    public const string ManifestSchema =
        "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";

    public const string McpSchema =
        "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json";
}

public enum AgentPluginDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed class AgentPluginDiagnostic
{
    internal AgentPluginDiagnostic(
        AgentPluginDiagnosticSeverity severity,
        string code,
        string message,
        string path,
        string? component = null)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Path = path;
        Component = component;
    }

    public AgentPluginDiagnosticSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string Path { get; }

    public string? Component { get; }
}

public sealed class AgentPluginAuthor
{
    internal AgentPluginAuthor(string? name, string? email, string? url)
    {
        Name = name;
        Email = email;
        Url = url;
    }

    public string? Name { get; }

    public string? Email { get; }

    public string? Url { get; }
}

public sealed class AgentPluginManifest
{
    internal AgentPluginManifest(
        string name,
        string? version,
        string? description,
        AgentPluginAuthor? author,
        string? homepage,
        string? repository,
        string? license,
        IReadOnlyList<string> keywords,
        IReadOnlyDictionary<string, string> extensions)
    {
        Name = name;
        Version = version;
        Description = description;
        Author = author;
        Homepage = homepage;
        Repository = repository;
        License = license;
        Keywords = keywords;
        Extensions = extensions;
    }

    public string Schema => AgentPluginSpecification.ManifestSchema;

    public string Name { get; }

    public string? Version { get; }

    public string? Description { get; }

    public AgentPluginAuthor? Author { get; }

    public string? Homepage { get; }

    public string? Repository { get; }

    public string? License { get; }

    public IReadOnlyList<string> Keywords { get; }

    /// <summary>
    /// Client-specific manifest values as bounded JSON objects. OpenGameAgent does not execute
    /// code or assign behavior to unknown namespaces.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extensions { get; }
}

public enum AgentPluginMcpTransport
{
    Stdio,
    StreamableHttp,
    LegacySse,
}

public sealed class AgentPluginMcpServerInfo
{
    internal AgentPluginMcpServerInfo(string id, AgentPluginMcpTransport transport)
    {
        Id = id;
        Transport = transport;
    }

    public string Id { get; }

    public AgentPluginMcpTransport Transport { get; }
}

public sealed class AgentPluginLoadOptions
{
    /// <summary>
    /// Client-managed writable root used for ${PLUGIN_DATA}. A stdio server is skipped when this
    /// directory is not configured; skills and remote MCP servers remain available.
    /// </summary>
    public string? PluginDataDirectory { get; set; }

    public bool LoadSkills { get; set; } = true;

    public bool LoadMcpServers { get; set; } = true;

    public GameMcpToolExposure McpToolExposure { get; set; } = GameMcpToolExposure.OnDemand;

    /// <summary>
    /// Client-owned headers keyed by MCP server ID. They override case-insensitive package header
    /// names, keeping authentication outside the plugin package.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> McpServerHeaders { get; set; } =
        new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));

    /// <summary>
    /// Optional client-owned HTTP transport. Redirect and authorization behavior is then owned by
    /// the caller. The default transport disables redirects so package headers cannot cross origins.
    /// </summary>
    public HttpClient? McpHttpClient { get; set; }

    public int MaximumManifestCharacters { get; set; } = 1_000_000;

    public int MaximumMcpCharacters { get; set; } = 2_000_000;

    public int MaximumMetadataStringCharacters { get; set; } = 65_536;

    public int MaximumSkills { get; set; } = 1_000;

    public int MaximumMcpServers { get; set; } = 256;

    public int MaximumArgumentsPerServer { get; set; } = 1_024;

    public int MaximumEnvironmentVariablesPerServer { get; set; } = 1_024;

    public int MaximumHeadersPerServer { get; set; } = 64;

    public int MaximumDiagnostics { get; set; } = 1_024;

    public int MaximumDiagnosticCharacters { get; set; } = 128_000;

    internal void Validate()
    {
        if (!Enum.IsDefined(typeof(GameMcpToolExposure), McpToolExposure))
        {
            throw new ArgumentOutOfRangeException(nameof(McpToolExposure));
        }

        RequireRange(MaximumManifestCharacters, 2, 100_000_000, nameof(MaximumManifestCharacters));
        RequireRange(MaximumMcpCharacters, 2, 100_000_000, nameof(MaximumMcpCharacters));
        RequireRange(MaximumMetadataStringCharacters, 1, 10_000_000, nameof(MaximumMetadataStringCharacters));
        RequireRange(MaximumSkills, 0, 100_000, nameof(MaximumSkills));
        RequireRange(MaximumMcpServers, 0, 10_000, nameof(MaximumMcpServers));
        RequireRange(MaximumArgumentsPerServer, 0, 100_000, nameof(MaximumArgumentsPerServer));
        RequireRange(MaximumEnvironmentVariablesPerServer, 0, 100_000, nameof(MaximumEnvironmentVariablesPerServer));
        RequireRange(MaximumHeadersPerServer, 0, 10_000, nameof(MaximumHeadersPerServer));
        RequireRange(MaximumDiagnostics, 0, 100_000, nameof(MaximumDiagnostics));
        RequireRange(MaximumDiagnosticCharacters, 1, 10_000_000, nameof(MaximumDiagnosticCharacters));

        if (McpServerHeaders is null
            || McpServerHeaders.Any(pair => pair.Key is null || pair.Value is null))
        {
            throw new ArgumentException("MCP client header mappings cannot contain null values.", nameof(McpServerHeaders));
        }
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public sealed class AgentPluginLoadException : Exception
{
    public AgentPluginLoadException(string message, string path, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Path { get; }
}

internal sealed class AgentPluginMcpConfiguration
{
    public AgentPluginMcpConfiguration(
        string id,
        AgentPluginMcpTransport transport,
        string? command,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        Uri? endpoint,
        IReadOnlyDictionary<string, string> headers)
    {
        Id = id;
        Transport = transport;
        Command = command;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        Endpoint = endpoint;
        Headers = headers;
    }

    public string Id { get; }

    public AgentPluginMcpTransport Transport { get; }

    public string? Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string?> Environment { get; }

    public Uri? Endpoint { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }
}
