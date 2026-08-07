using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Connectors.Mcp;

public enum GameMcpToolExposure
{
    OnDemand,
    Direct,
}

public sealed class GameMcpServer
{
    public GameMcpServer(
        string id,
        Func<CancellationToken, ValueTask<McpClient>> connect,
        string? toolPrefix = null,
        ToolRisk toolRisk = ToolRisk.NonIdempotentWrite,
        IReadOnlyCollection<string>? allowedTools = null)
    {
        Id = Require(id, nameof(id));
        Connect = connect ?? throw new ArgumentNullException(nameof(connect));
        ToolPrefix = toolPrefix ?? id + "__";
        if (string.IsNullOrWhiteSpace(ToolPrefix) || ToolPrefix.Length > 256)
        {
            throw new ArgumentException("A tool prefix must contain at most 256 characters.", nameof(toolPrefix));
        }

        if (!Enum.IsDefined(typeof(ToolRisk), toolRisk))
        {
            throw new ArgumentOutOfRangeException(nameof(toolRisk));
        }

        ToolRisk = toolRisk;
        if (allowedTools is { Count: > 10_000 })
        {
            throw new ArgumentException("At most 10,000 allowed tools can be configured.", nameof(allowedTools));
        }

        AllowedTools = new ReadOnlyCollection<string>((allowedTools ?? Array.Empty<string>())
            .Select(value => Require(value, nameof(allowedTools)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
    }

    public string Id { get; }

    public Func<CancellationToken, ValueTask<McpClient>> Connect { get; }

    public string ToolPrefix { get; }

    public ToolRisk ToolRisk { get; }

    public IReadOnlyCollection<string> AllowedTools { get; }

    public static GameMcpServer Http(
        string id,
        Uri endpoint,
        HttpClient? httpClient = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool allowInsecureHttp = false,
        string? toolPrefix = null,
        ToolRisk toolRisk = ToolRisk.NonIdempotentWrite,
        IReadOnlyCollection<string>? allowedTools = null)
    {
        if (endpoint is null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }

        if (!endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || (endpoint.Scheme != Uri.UriSchemeHttps
                && !(allowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp)))
        {
            throw new ArgumentException(
                "An absolute HTTPS endpoint is required unless insecure HTTP is explicitly enabled.",
                nameof(endpoint));
        }

        Dictionary<string, string>? copiedHeaders = null;
        if (headers is not null)
        {
            if (headers.Count > 64)
            {
                throw new ArgumentException("At most 64 HTTP headers can be configured.", nameof(headers));
            }

            copiedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in headers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Key.Length > 256
                    || pair.Key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                    || pair.Value is null
                    || pair.Value.Length > 65_536
                    || pair.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                    || !copiedHeaders.TryAdd(pair.Key, pair.Value))
                {
                    throw new ArgumentException("HTTP headers are invalid or contain duplicate names.", nameof(headers));
                }
            }
        }
        return new GameMcpServer(
            id,
            async cancellationToken =>
            {
                var options = new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    Name = id,
                    AdditionalHeaders = copiedHeaders,
                };
                var transport = httpClient is null
                    ? new HttpClientTransport(options)
                    : new HttpClientTransport(options, httpClient, ownsHttpClient: false);
                try
                {
                    return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
            toolPrefix,
            toolRisk,
            allowedTools);
    }

    public static GameMcpServer Stdio(
        string id,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? toolPrefix = null,
        ToolRisk toolRisk = ToolRisk.NonIdempotentWrite,
        IReadOnlyCollection<string>? allowedTools = null)
    {
        Require(command, nameof(command));
        if (command.Contains('\0'))
        {
            throw new ArgumentException("The process command is invalid.", nameof(command));
        }

        var copiedArguments = (arguments ?? Array.Empty<string>()).ToArray();
        if (copiedArguments.Length > 1_024
            || copiedArguments.Any(value => value is null || value.Length > 65_536 || value.Contains('\0')))
        {
            throw new ArgumentException("Process arguments exceed the configured safety bounds.", nameof(arguments));
        }

        if (workingDirectory is { Length: > 32_768 }
            || (workingDirectory?.Contains('\0') ?? false))
        {
            throw new ArgumentException("The working directory is invalid.", nameof(workingDirectory));
        }

        if (environment is { Count: > 1_024 })
        {
            throw new ArgumentException("At most 1,024 environment variables can be configured.", nameof(environment));
        }

        if (environment is not null && environment.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Length > 512
                || pair.Key.IndexOfAny(new[] { '=', '\0' }) >= 0
                || pair.Value is { Length: > 65_536 }
                || pair.Value?.IndexOf('\0') >= 0))
        {
            throw new ArgumentException("Process environment variables are invalid.", nameof(environment));
        }

        var copiedEnvironment = environment is null
            ? StdioClientTransportOptions.GetDefaultEnvironmentVariables()
            : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
        return new GameMcpServer(
            id,
            async cancellationToken =>
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = id,
                    Command = command,
                    Arguments = copiedArguments,
                    WorkingDirectory = workingDirectory,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = copiedEnvironment,
                });
                return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
            },
            toolPrefix,
            toolRisk,
            allowedTools);
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 512
            ? throw new ArgumentException("A value of at most 512 characters is required.", name)
            : value;
}

public sealed class McpToolSetChange
{
    public McpToolSetChange(string serverId, IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > 512)
        {
            throw new ArgumentException("A server ID of at most 512 characters is required.", nameof(serverId));
        }

        ServerId = serverId;
        Added = CopyNames(added, nameof(added));
        Removed = CopyNames(removed, nameof(removed));
    }

    public string ServerId { get; }

    public IReadOnlyList<string> Added { get; }

    public IReadOnlyList<string> Removed { get; }

    private static IReadOnlyList<string> CopyNames(IReadOnlyList<string> values, string parameterName)
    {
        var copy = (values ?? throw new ArgumentNullException(parameterName)).ToArray();
        if (copy.Length > 10_000
            || copy.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512)
            || copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Tool names are invalid or duplicated.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

public sealed class McpToolConnectorExtension : IGameAgentExtension, IAsyncDisposable
{
    private const string ProxySchema = """
        {"type":"object","required":["action"],"properties":{"action":{"type":"string","enum":["search","describe","call"]},"query":{"type":"string","maxLength":512},"server":{"type":"string","maxLength":512},"path":{"type":"string","maxLength":1024},"limit":{"type":"integer","minimum":1,"maximum":50},"arguments":{"type":"object"}},"additionalProperties":false}
        """;

    private readonly IReadOnlyList<GameMcpServer> _servers;
    private readonly Dictionary<string, ServerState> _states;
    private readonly TimeSpan _refreshInterval;
    private readonly int _maximumToolsPerServer;
    private readonly int _maximumSchemaCharacters;
    private readonly int _maximumInlineResultCharacters;
    private readonly int _maximumResultCharacters;
    private readonly IGameAgentArtifactStore? _artifactStore;
    private readonly Func<DateTimeOffset> _clock;
    private readonly GameMcpToolExposure _exposure;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public McpToolConnectorExtension(
        IReadOnlyList<GameMcpServer> servers,
        TimeSpan? refreshInterval = null,
        int maximumToolsPerServer = 256,
        int maximumSchemaCharacters = 262_144,
        int maximumInlineResultCharacters = 262_144,
        IGameAgentArtifactStore? artifactStore = null,
        Func<DateTimeOffset>? operationalClock = null,
        int maximumResultCharacters = 10_000_000,
        GameMcpToolExposure exposure = GameMcpToolExposure.OnDemand)
    {
        var copied = (servers ?? throw new ArgumentNullException(nameof(servers))).ToArray();
        if (copied.Length == 0 || copied.Any(server => server is null))
        {
            throw new ArgumentException("At least one non-null server is required.", nameof(servers));
        }

        var duplicate = copied.GroupBy(server => server.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Server ID '{duplicate.Key}' is duplicated.", nameof(servers));
        }

        var prefixes = copied.GroupBy(server => server.ToolPrefix, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (prefixes is not null)
        {
            throw new ArgumentException($"Tool prefix '{prefixes.Key}' is duplicated.", nameof(servers));
        }

        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(5);
        if (_refreshInterval < TimeSpan.Zero || _refreshInterval > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }

        if (maximumToolsPerServer < 1 || maximumToolsPerServer > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumToolsPerServer));
        }

        if (maximumSchemaCharacters < 2 || maximumSchemaCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSchemaCharacters));
        }

        if (maximumInlineResultCharacters < 1_024 || maximumInlineResultCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInlineResultCharacters));
        }

        if (maximumResultCharacters < maximumInlineResultCharacters || maximumResultCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResultCharacters));
        }

        if (!Enum.IsDefined(typeof(GameMcpToolExposure), exposure))
        {
            throw new ArgumentOutOfRangeException(nameof(exposure));
        }

        _servers = Array.AsReadOnly(copied);
        _states = copied.ToDictionary(server => server.Id, _ => new ServerState(), StringComparer.Ordinal);
        _maximumToolsPerServer = maximumToolsPerServer;
        _maximumSchemaCharacters = maximumSchemaCharacters;
        _maximumInlineResultCharacters = maximumInlineResultCharacters;
        _maximumResultCharacters = maximumResultCharacters;
        _artifactStore = artifactStore;
        _clock = operationalClock ?? (() => DateTimeOffset.UtcNow);
        _exposure = exposure;
    }

    public static GameAgentExtensionChannel<McpToolSetChange> ToolSetChanged { get; } = new("mcp.tools.changed");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.mcp",
        "1.0.0",
        "Optional standard external tool discovery and invocation connector.",
        new[] { "external-tools", "dynamic-tools", "stdio", "http" });

    public void Configure(GameAgentExtensionApi api)
    {
        if (_exposure == GameMcpToolExposure.Direct)
        {
            api.RegisterToolProvider("mcp-tools", (context, token) => CollectToolsAsync(api, context, token));
            return;
        }

        api.RegisterToolProvider(
            "mcp-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { CreateProxyTool(api, context) }));
    }

    public void Invalidate(string serverId)
    {
        if (!_states.TryGetValue(serverId, out var state))
        {
            throw new KeyNotFoundException($"Server '{serverId}' is not configured.");
        }

        lock (state.Sync)
        {
            state.RefreshAfter = DateTimeOffset.MinValue;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException)
        {
            // A connector callback cannot prevent cleanup of the remaining servers.
        }
        foreach (var state in _states.Values)
        {
            McpClient? client;
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (state.Sync)
                {
                    state.IsDisposing = true;
                    client = state.Client;
                    state.Client = null;
                    state.Tools = Array.Empty<McpClientTool>();
                }
            }
            finally
            {
                state.Gate.Release();
            }

            await state.WaitForCallsAsync().ConfigureAwait(false);
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<IReadOnlyList<AgentTool>> CollectToolsAsync(
        GameAgentExtensionApi api,
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var result = new List<AgentTool>();
        foreach (var server in _servers)
        {
            var tools = await GetToolsAsync(api, server, linked.Token).ConfigureAwait(false);
            result.AddRange(tools.Select(tool => CreateTool(server, tool, context)));
        }

        return result;
    }

    private async ValueTask<IReadOnlyList<McpClientTool>> GetToolsAsync(
        GameAgentExtensionApi api,
        GameMcpServer server,
        CancellationToken cancellationToken)
    {
        var state = _states[server.Id];
        lock (state.Sync)
        {
            if (state.Tools.Count > 0 && _clock() < state.RefreshAfter)
            {
                return state.Tools;
            }
        }

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (state.Sync)
            {
                if (state.Tools.Count > 0 && _clock() < state.RefreshAfter)
                {
                    return state.Tools;
                }
            }

            McpClient? client;
            lock (state.Sync)
            {
                if (state.IsDisposing)
                {
                    throw new ObjectDisposedException(nameof(McpToolConnectorExtension));
                }

                client = state.Client;
            }

            if (client is null)
            {
                client = await server.Connect(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Server '{server.Id}' returned a null client.");
                lock (state.Sync)
                {
                    if (state.IsDisposing)
                    {
                        throw new ObjectDisposedException(nameof(McpToolConnectorExtension));
                    }

                    state.Client = client;
                }
            }

            var discovered = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var filtered = discovered
                .Where(tool => server.AllowedTools.Count == 0 || server.AllowedTools.Contains(tool.Name, StringComparer.Ordinal))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .ToArray();
            if (filtered.Length > _maximumToolsPerServer)
            {
                throw new InvalidOperationException($"Server '{server.Id}' exceeded the configured tool limit.");
            }

            var duplicate = filtered.GroupBy(tool => tool.Name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException(
                    $"Server '{server.Id}' returned duplicate tool name '{duplicate.Key}'.");
            }

            foreach (var tool in filtered)
            {
                var schema = tool.JsonSchema.GetRawText();
                if (string.IsNullOrWhiteSpace(tool.Name)
                    || tool.Name.Length > 512
                    || (tool.Description?.Length ?? 0) > 100_000
                    || schema.Length > _maximumSchemaCharacters)
                {
                    throw new InvalidOperationException(
                        $"Server '{server.Id}' returned a tool with an invalid name, description, or schema size.");
                }
            }

            IReadOnlyList<string> previous;
            lock (state.Sync)
            {
                previous = state.Tools.Select(tool => tool.Name).ToArray();
                state.Tools = Array.AsReadOnly(filtered);
                var now = _clock();
                state.RefreshAfter = DateTimeOffset.MaxValue - now < _refreshInterval
                    ? DateTimeOffset.MaxValue
                    : now + _refreshInterval;
            }

            var current = filtered.Select(tool => tool.Name).ToArray();
            var added = current.Except(previous, StringComparer.Ordinal).ToArray();
            var removed = previous.Except(current, StringComparer.Ordinal).ToArray();
            if (added.Length > 0 || removed.Length > 0)
            {
                await api.PublishAsync(
                    ToolSetChanged,
                    new McpToolSetChange(server.Id, added, removed),
                    cancellationToken).ConfigureAwait(false);
            }

            return state.Tools;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private AgentTool CreateTool(
        GameMcpServer server,
        McpClientTool remoteTool,
        GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                server.ToolPrefix + remoteTool.Name,
                string.IsNullOrWhiteSpace(remoteTool.Description)
                    ? $"Invoke external tool '{remoteTool.Name}' from '{server.Id}'."
                    : remoteTool.Description,
                remoteTool.JsonSchema.GetRawText()),
            (arguments, execution, cancellationToken) =>
                InvokeAsync(server, remoteTool, arguments, execution, context, cancellationToken),
            server.ToolRisk,
            ToolExecutionMode.Sequential);

    private AgentTool CreateProxyTool(
        GameAgentExtensionApi api,
        GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "external_tools",
                "Discover, inspect, or call a configured external tool. Search before calling an unfamiliar path.",
                ProxySchema),
            async (arguments, execution, cancellationToken) =>
            {
                var action = arguments.GetProperty("action").GetString();
                switch (action)
                {
                    case "search":
                        return await SearchAsync(api, arguments, cancellationToken).ConfigureAwait(false);
                    case "describe":
                        {
                            var resolved = await ResolveAsync(
                                api,
                                arguments.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null,
                                cancellationToken).ConfigureAwait(false);
                            return resolved.Error is not null
                                ? ToolResult.Error(resolved.Error)
                                : new ToolResult(new AgentContent[]
                                {
                                new JsonContent(JsonSerializer.Serialize(new
                                {
                                    path = resolved.Path,
                                    server = resolved.Server!.Id,
                                    name = resolved.Tool!.Name,
                                    resolved.Tool.Description,
                                    inputSchema = resolved.Tool.JsonSchema,
                                    risk = resolved.Server.ToolRisk.ToString(),
                                })),
                                });
                        }
                    case "call":
                        {
                            var resolved = await ResolveAsync(
                                api,
                                arguments.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null,
                                cancellationToken).ConfigureAwait(false);
                            if (resolved.Error is not null)
                            {
                                return ToolResult.Error(resolved.Error);
                            }

                            var callArguments = arguments.TryGetProperty("arguments", out var value)
                                ? value
                                : EmptyObject();
                            var validationError = ValidateRemoteArguments(resolved.Tool!, callArguments);
                            if (validationError is not null)
                            {
                                return ToolResult.Error("Invalid external tool arguments: " + validationError);
                            }

                            return await InvokeAsync(
                                resolved.Server!,
                                resolved.Tool!,
                                callArguments,
                                execution,
                                context,
                                cancellationToken).ConfigureAwait(false);
                        }
                    default:
                        return ToolResult.Error("Unsupported external tool action.");
                }
            },
            ToolRisk.NonIdempotentWrite,
            ToolExecutionMode.Sequential);

    private async ValueTask<ToolResult> SearchAsync(
        GameAgentExtensionApi api,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = arguments.TryGetProperty("query", out var queryElement)
            ? queryElement.GetString() ?? string.Empty
            : string.Empty;
        var serverFilter = arguments.TryGetProperty("server", out var serverElement)
            ? serverElement.GetString()
            : null;
        var limit = arguments.TryGetProperty("limit", out var limitElement) ? limitElement.GetInt32() : 20;
        if (serverFilter is not null && !_states.ContainsKey(serverFilter))
        {
            return ToolResult.Error($"External tool server '{serverFilter}' is not configured.");
        }

        var matches = new List<object>();
        foreach (var server in _servers.Where(value => serverFilter is null
                     || string.Equals(value.Id, serverFilter, StringComparison.Ordinal)))
        {
            var tools = await GetToolsAsync(api, server, cancellationToken).ConfigureAwait(false);
            foreach (var tool in tools)
            {
                if (query.Length > 0
                    && tool.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && (tool.Description?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                {
                    continue;
                }

                matches.Add(new
                {
                    path = server.ToolPrefix + tool.Name,
                    server = server.Id,
                    name = tool.Name,
                    description = tool.Description ?? string.Empty,
                    risk = server.ToolRisk.ToString(),
                });
                if (matches.Count > limit)
                {
                    break;
                }
            }

            if (matches.Count > limit)
            {
                break;
            }
        }

        var truncated = matches.Count > limit;
        var returned = matches.Take(limit).ToArray();

        return new ToolResult(new AgentContent[]
        {
            new JsonContent(JsonSerializer.Serialize(new
            {
                query,
                matches = returned,
                truncated,
            })),
        });
    }

    private async ValueTask<ResolvedTool> ResolveAsync(
        GameAgentExtensionApi api,
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ResolvedTool.Failed("An external tool path is required.");
        }

        foreach (var server in _servers.OrderByDescending(value => value.ToolPrefix.Length))
        {
            if (!path.StartsWith(server.ToolPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = path.Substring(server.ToolPrefix.Length);
            var tools = await GetToolsAsync(api, server, cancellationToken).ConfigureAwait(false);
            var tool = tools.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.Ordinal));
            if (tool is not null)
            {
                return ResolvedTool.Found(path, server, tool);
            }
        }

        return ResolvedTool.Failed($"External tool '{path}' does not exist.");
    }

    private async ValueTask<ToolResult> InvokeAsync(
        GameMcpServer server,
        McpClientTool remoteTool,
        JsonElement arguments,
        ToolExecutionContext execution,
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken)
    {
        var state = _states[server.Id];
        using var lease = state.LeaseClient(server.Id);
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.GetRawText())
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var boxed = values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
        var result = await lease.Client.CallToolAsync(
            remoteTool.Name,
            boxed,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(result);
        if (json.Length > _maximumResultCharacters)
        {
            return ToolResult.Error("The external tool result exceeded the configured result limit.");
        }

        if (json.Length <= _maximumInlineResultCharacters)
        {
            return new ToolResult(
                new AgentContent[] { new JsonContent(json) },
                isError: result.IsError is true);
        }

        if (_artifactStore is null)
        {
            return ToolResult.Error(
                "The external tool result exceeded the inline limit and no artifact store is configured.");
        }

        var artifactId = CreateArtifactId(server, remoteTool, execution, context);
        await _artifactStore.PutAsync(
            new GameAgentArtifact(
                artifactId,
                context.Input.SessionId,
                context.Input.ActorId,
                "application/json",
                json,
                context.Input.Moment),
            cancellationToken).ConfigureAwait(false);
        return new ToolResult(
            new AgentContent[]
            {
                new JsonContent(JsonSerializer.Serialize(new
                {
                    artifactId,
                    mediaType = "application/json",
                    totalCharacters = json.Length,
                    readTool = "read_agent_artifact",
                })),
            },
            isError: result.IsError is true);
    }

    private static string? ValidateRemoteArguments(McpClientTool tool, JsonElement arguments)
    {
        var validator = new AgentTool(
            new ToolDefinition("external_validation", "Validate external tool arguments.", tool.JsonSchema.GetRawText()),
            (_, _, _) => new ValueTask<ToolResult>(ToolResult.Error("Validation-only tool.")));
        return validator.ValidateArguments(arguments.GetRawText());
    }

    private static string CreateArtifactId(
        GameMcpServer server,
        McpClientTool remoteTool,
        ToolExecutionContext execution,
        GameAgentExtensionRunContext context)
    {
        using var hash = SHA256.Create();
        var identity = string.Join(
            "\n",
            context.Input.SessionId,
            context.Input.ActorId,
            context.Input.InputId,
            execution.RunId,
            execution.Turn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            execution.ToolCallIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            server.Id,
            remoteTool.Name);
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(identity));
        var encoded = new StringBuilder(bytes.Length * 2 + 4);
        encoded.Append("mcp-");
        foreach (var value in bytes)
        {
            encoded.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return encoded.ToString();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed class ResolvedTool
    {
        private ResolvedTool(string? path, GameMcpServer? server, McpClientTool? tool, string? error)
        {
            Path = path;
            Server = server;
            Tool = tool;
            Error = error;
        }

        public string? Path { get; }

        public GameMcpServer? Server { get; }

        public McpClientTool? Tool { get; }

        public string? Error { get; }

        public static ResolvedTool Found(string path, GameMcpServer server, McpClientTool tool) =>
            new(path, server, tool, null);

        public static ResolvedTool Failed(string error) => new(null, null, null, error);
    }

    private sealed class ServerState
    {
        public object Sync { get; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public McpClient? Client { get; set; }

        public IReadOnlyList<McpClientTool> Tools { get; set; } = Array.Empty<McpClientTool>();

        public DateTimeOffset RefreshAfter { get; set; } = DateTimeOffset.MinValue;

        public bool IsDisposing { get; set; }

        private int ActiveCalls { get; set; }

        private TaskCompletionSource<bool>? CallsDrained { get; set; }

        public ClientLease LeaseClient(string serverId)
        {
            lock (Sync)
            {
                if (IsDisposing || Client is null)
                {
                    throw new ObjectDisposedException($"MCP server '{serverId}' is not available.");
                }

                ActiveCalls = checked(ActiveCalls + 1);
                return new ClientLease(this, Client);
            }
        }

        public Task WaitForCallsAsync()
        {
            lock (Sync)
            {
                if (ActiveCalls == 0)
                {
                    return Task.CompletedTask;
                }

                CallsDrained ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return CallsDrained.Task;
            }
        }

        private void ReleaseClient()
        {
            lock (Sync)
            {
                ActiveCalls--;
                if (ActiveCalls == 0)
                {
                    CallsDrained?.TrySetResult(true);
                }
            }
        }

        public sealed class ClientLease : IDisposable
        {
            private ServerState? _owner;

            public ClientLease(ServerState owner, McpClient client)
            {
                _owner = owner;
                Client = client;
            }

            public McpClient Client { get; }

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseClient();
        }
    }
}
