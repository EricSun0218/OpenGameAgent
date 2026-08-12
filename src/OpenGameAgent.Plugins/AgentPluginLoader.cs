using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Connectors.Mcp;
using OpenGameAgent.Persistence;

namespace OpenGameAgent.Plugins;

public static class AgentPluginLoader
{
    private static readonly HashSet<string> ManifestFields = new(StringComparer.Ordinal)
    {
        "$schema",
        "name",
        "version",
        "description",
        "author",
        "homepage",
        "repository",
        "license",
        "keywords",
        "extensions",
    };

    private static readonly HashSet<string> AuthorFields = new(StringComparer.Ordinal)
    {
        "name",
        "email",
        "url",
    };

    private static readonly HashSet<string> StdioFields = new(StringComparer.Ordinal)
    {
        "type",
        "command",
        "args",
        "env",
        "cwd",
    };

    private static readonly HashSet<string> HttpFields = new(StringComparer.Ordinal)
    {
        "type",
        "url",
        "headers",
    };

    public static AgentPluginPackage Load(
        string pluginDirectory,
        AgentPluginLoadOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            throw new ArgumentException("An Agent Plugin directory is required.", nameof(pluginDirectory));
        }

        options ??= new AgentPluginLoadOptions();
        options.Validate();

        var root = Path.GetFullPath(pluginDirectory);
        RequireDirectory(root, "Agent Plugin root");
        RejectReparsePoint(root, "Agent Plugin root");

        var manifestPath = Path.Combine(root, "plugin.json");
        RequirePackageFile(root, manifestPath, "Agent Plugin manifest");

        var diagnostics = new DiagnosticBuffer(options.MaximumDiagnostics, options.MaximumDiagnosticCharacters);
        var manifest = ParseManifest(
            ReadBounded(manifestPath, options.MaximumManifestCharacters),
            manifestPath,
            options,
            diagnostics);

        var dataDirectory = ResolveDataDirectory(options.PluginDataDirectory);
        var skills = options.LoadSkills
            ? LoadSkills(root, manifest.Name, options, diagnostics)
            : Array.Empty<GameSkill>();
        var extensionDirectories = DiscoverClientExtensionDirectories(root, diagnostics);

        var mcpConfigurations = options.LoadMcpServers
            ? LoadMcpConfigurations(root, dataDirectory, manifest.Name, options, diagnostics)
            : Array.Empty<AgentPluginMcpConfiguration>();

        HttpClient? ownedHttpClient = null;
        var httpClient = options.McpHttpClient;
        if (httpClient is null && mcpConfigurations.Any(value => value.Transport == AgentPluginMcpTransport.StreamableHttp))
        {
            ownedHttpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
            httpClient = ownedHttpClient;
        }

        var mcpServers = new List<GameMcpServer>();
        var mcpInfos = new List<AgentPluginMcpServerInfo>();
        for (var index = 0; index < mcpConfigurations.Count; index++)
        {
            var configuration = mcpConfigurations[index];
            var internalId = manifest.Name + ".mcp." + index;
            var prefix = CreateToolPrefix(manifest.Name, configuration.Id, index);
            switch (configuration.Transport)
            {
                case AgentPluginMcpTransport.Stdio:
                    mcpServers.Add(GameMcpServer.Stdio(
                        internalId,
                        configuration.Command!,
                        configuration.Arguments,
                        configuration.WorkingDirectory,
                        configuration.Environment,
                        prefix));
                    mcpInfos.Add(new AgentPluginMcpServerInfo(configuration.Id, configuration.Transport));
                    break;
                case AgentPluginMcpTransport.StreamableHttp:
                    mcpServers.Add(GameMcpServer.Http(
                        internalId,
                        configuration.Endpoint!,
                        httpClient,
                        configuration.Headers,
                        allowInsecureHttp: configuration.Endpoint!.Scheme == Uri.UriSchemeHttp,
                        toolPrefix: prefix));
                    mcpInfos.Add(new AgentPluginMcpServerInfo(configuration.Id, configuration.Transport));
                    break;
            }
        }

        var mcpExtension = mcpServers.Count == 0
            ? null
            : new McpToolConnectorExtension(
                mcpServers,
                continueOnServerFailure: true,
                exposure: options.McpToolExposure);

        return new AgentPluginPackage(
            root,
            dataDirectory,
            manifest,
            AgentPluginPackage.ReadOnly(skills),
            AgentPluginPackage.ReadOnly(mcpInfos),
            extensionDirectories,
            AgentPluginPackage.ReadOnly(diagnostics.Items),
            mcpExtension,
            ownedHttpClient,
            AgentPluginPackage.ReadOnly(mcpConfigurations));
    }

    private static AgentPluginManifest ParseManifest(
        string json,
        string path,
        AgentPluginLoadOptions options,
        DiagnosticBuffer diagnostics)
    {
        try
        {
            using var document = ParseJson(json, path, validateDescendantObjects: false);
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "The Agent Plugin manifest must be a JSON object.");

            foreach (var property in root.EnumerateObject())
            {
                if (!ManifestFields.Contains(property.Name))
                {
                    diagnostics.Add(
                        AgentPluginDiagnosticSeverity.Warning,
                        "manifest.unknown-field",
                        $"Unknown manifest field '{property.Name}' was ignored.",
                        path,
                        "manifest");
                }
            }

            var schema = RequiredString(root, "$schema", options.MaximumMetadataStringCharacters);
            if (!string.Equals(schema, AgentPluginSpecification.ManifestSchema, StringComparison.Ordinal))
            {
                throw new PluginConfigurationException("The Agent Plugin manifest targets an unsupported schema.");
            }

            var name = RequiredString(root, "name", 64);
            if (!IsPluginName(name))
            {
                throw new PluginConfigurationException("The Agent Plugin name does not satisfy the 1.0.0 name rules.");
            }

            var version = OptionalString(root, "version", options.MaximumMetadataStringCharacters);
            var description = OptionalString(root, "description", options.MaximumMetadataStringCharacters);
            var homepage = OptionalString(root, "homepage", options.MaximumMetadataStringCharacters);
            var repository = OptionalString(root, "repository", options.MaximumMetadataStringCharacters);
            var license = OptionalString(root, "license", options.MaximumMetadataStringCharacters);
            var author = ParseAuthor(root, options.MaximumMetadataStringCharacters);
            var keywords = ParseKeywords(root, options.MaximumMetadataStringCharacters);
            var extensions = ParseManifestExtensions(root, path, diagnostics);
            return new AgentPluginManifest(
                name,
                version,
                description,
                author,
                homepage,
                repository,
                license,
                keywords,
                extensions);
        }
        catch (AgentPluginLoadException)
        {
            throw;
        }
        catch (Exception exception) when (IsConfigurationFailure(exception))
        {
            throw new AgentPluginLoadException(
                $"Agent Plugin manifest '{path}' is invalid: {exception.Message}",
                path,
                exception);
        }
    }

    private static AgentPluginAuthor? ParseAuthor(JsonElement root, int maximumCharacters)
    {
        if (!root.TryGetProperty("author", out var author))
        {
            return null;
        }

        RequireKind(author, JsonValueKind.Object, "Manifest author must be an object.");
        EnsureObjectNamesUnique(author);
        foreach (var property in author.EnumerateObject())
        {
            if (!AuthorFields.Contains(property.Name))
            {
                throw new PluginConfigurationException($"Unknown author field '{property.Name}'.");
            }
        }

        return new AgentPluginAuthor(
            OptionalString(author, "name", maximumCharacters),
            OptionalString(author, "email", maximumCharacters),
            OptionalString(author, "url", maximumCharacters));
    }

    private static IReadOnlyList<string> ParseKeywords(JsonElement root, int maximumCharacters)
    {
        if (!root.TryGetProperty("keywords", out var keywords))
        {
            return Array.Empty<string>();
        }

        RequireKind(keywords, JsonValueKind.Array, "Manifest keywords must be an array.");
        var values = keywords.EnumerateArray().ToArray();
        if (values.Length > 1_024)
        {
            throw new PluginConfigurationException("Manifest keywords exceed the client safety limit.");
        }

        return Array.AsReadOnly(values.Select(value => RequireString(value, "keyword", maximumCharacters)).ToArray());
    }

    private static IReadOnlyDictionary<string, string> ParseManifestExtensions(
        JsonElement root,
        string path,
        DiagnosticBuffer diagnostics)
    {
        if (!root.TryGetProperty("extensions", out var extensions))
        {
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (extensions.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Warning,
                "manifest.invalid-extensions",
                "The non-object manifest extensions field was ignored.",
                path,
                "manifest");
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        EnsureObjectNamesUnique(extensions);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in extensions.EnumerateObject())
        {
            if (property.Name.Length > 256 || property.Name.IndexOf('\0') >= 0)
            {
                throw new PluginConfigurationException("An extension namespace exceeds the client safety bounds.");
            }

            RequireKind(
                property.Value,
                JsonValueKind.Object,
                $"Extension namespace '{property.Name}' must contain an object.");
            result.Add(property.Name, property.Value.GetRawText());
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static IReadOnlyList<GameSkill> LoadSkills(
        string root,
        string pluginName,
        AgentPluginLoadOptions options,
        DiagnosticBuffer diagnostics)
    {
        var skillsRoot = Path.Combine(root, "skills");
        if (!Directory.Exists(skillsRoot))
        {
            if (File.Exists(skillsRoot))
            {
                diagnostics.Add(
                    AgentPluginDiagnosticSeverity.Error,
                    "skills.invalid-location",
                    "The skills component exists but is not a directory.",
                    skillsRoot,
                    "skills");
            }

            return Array.Empty<GameSkill>();
        }

        if (!IsWithin(root, skillsRoot) || IsReparsePoint(skillsRoot))
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Error,
                "skills.invalid-location",
                "The skills component does not resolve safely inside the plugin root.",
                skillsRoot,
                "skills");
            return Array.Empty<GameSkill>();
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(skillsRoot, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(options.MaximumSkills + 1)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Error,
                "skills.discovery-failed",
                "The skills component could not be enumerated.",
                skillsRoot,
                "skills");
            return Array.Empty<GameSkill>();
        }

        if (directories.Length > options.MaximumSkills)
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Error,
                "skills.limit-exceeded",
                "The skills component exceeds the configured immediate-child limit.",
                skillsRoot,
                "skills");
            directories = directories.Take(options.MaximumSkills).ToArray();
        }

        var loaded = new List<GameSkill>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            var skillPath = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillPath))
            {
                continue;
            }

            if (!IsWithin(root, directory)
                || IsReparsePoint(directory)
                || IsReparsePoint(skillPath))
            {
                diagnostics.Add(
                    AgentPluginDiagnosticSeverity.Warning,
                    "skills.unsafe-entry",
                    "A skill that does not resolve safely inside the plugin root was skipped.",
                    skillPath,
                    "skills");
                continue;
            }

            try
            {
                var source = new DirectoryGameSkillSource(
                    directory,
                    maximumSkills: 1,
                    maximumManifestCharacters: Math.Min(options.MaximumManifestCharacters, 100_000_000),
                    maximumInstructionsCharacters: 10_000_000,
                    maximumScannedDirectories: 1,
                    maximumIgnoreCharacters: 0,
                    continueOnError: true,
                    honorIgnoreFiles: false,
                    source: "agent-plugin",
                    sourceScope: pluginName,
                    maximumDiagnostics: Math.Min(options.MaximumDiagnostics, 1_000_000),
                    maximumDiagnosticCharacters: Math.Min(options.MaximumDiagnosticCharacters, 10_000_000));
                var discovered = source.Discover();
                foreach (var diagnostic in discovered.Diagnostics)
                {
                    diagnostics.Add(
                        MapSeverity(diagnostic.Severity),
                        "skills." + diagnostic.Code,
                        diagnostic.Message,
                        diagnostic.Path,
                        "skills");
                }

                foreach (var skill in discovered.Skills)
                {
                    if (!ids.Add(skill.SkillId))
                    {
                        diagnostics.Add(
                            AgentPluginDiagnosticSeverity.Warning,
                            "skills.duplicate-name",
                            $"Duplicate skill name '{skill.SkillId}' was skipped.",
                            skillPath,
                            "skills");
                        continue;
                    }

                    loaded.Add(skill);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PersistenceException or GameRuntimeLimitException)
            {
                diagnostics.Add(
                    AgentPluginDiagnosticSeverity.Warning,
                    "skills.invalid-entry",
                    "An invalid skill was skipped: " + Bound(exception.Message, 2_048),
                    skillPath,
                    "skills");
            }
        }

        return Array.AsReadOnly(loaded.ToArray());
    }

    private static IReadOnlyList<AgentPluginMcpConfiguration> LoadMcpConfigurations(
        string root,
        string? dataDirectory,
        string pluginName,
        AgentPluginLoadOptions options,
        DiagnosticBuffer diagnostics)
    {
        var path = Path.Combine(root, "mcp.json");
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
            {
                diagnostics.Add(
                    AgentPluginDiagnosticSeverity.Error,
                    "mcp.invalid-location",
                    "The MCP component exists but is not a regular file.",
                    path,
                    "mcp");
            }

            return Array.Empty<AgentPluginMcpConfiguration>();
        }

        if (!IsWithin(root, path) || IsReparsePoint(path))
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Error,
                "mcp.invalid-location",
                "The MCP component does not resolve safely inside the plugin root.",
                path,
                "mcp");
            return Array.Empty<AgentPluginMcpConfiguration>();
        }

        try
        {
            using var document = ParseJson(ReadBounded(path, options.MaximumMcpCharacters), path);
            var value = document.RootElement;
            RequireKind(value, JsonValueKind.Object, "The MCP configuration must be an object.");
            RequireExactFields(value, new HashSet<string>(StringComparer.Ordinal) { "$schema", "mcpServers" });
            var schema = RequiredString(value, "$schema", options.MaximumMetadataStringCharacters);
            if (!string.Equals(schema, AgentPluginSpecification.McpSchema, StringComparison.Ordinal))
            {
                throw new PluginConfigurationException("The MCP configuration targets a different or unsupported version.");
            }

            if (!value.TryGetProperty("mcpServers", out var servers))
            {
                throw new PluginConfigurationException("The MCP configuration requires mcpServers.");
            }

            RequireKind(servers, JsonValueKind.Object, "mcpServers must be an object.");
            var entries = servers.EnumerateObject().OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
            if (entries.Length > options.MaximumMcpServers)
            {
                throw new PluginConfigurationException("The MCP configuration exceeds the server limit.");
            }

            var result = new List<AgentPluginMcpConfiguration>();
            foreach (var entry in entries)
            {
                try
                {
                    var parsed = ParseMcpServer(
                        entry.Name,
                        entry.Value,
                        root,
                        dataDirectory,
                        options);
                    if (parsed.Transport == AgentPluginMcpTransport.LegacySse)
                    {
                        diagnostics.Add(
                            AgentPluginDiagnosticSeverity.Warning,
                            "mcp.unsupported-transport",
                            $"MCP server '{entry.Name}' uses the optional legacy SSE transport and was skipped.",
                            path,
                            entry.Name);
                        continue;
                    }

                    result.Add(parsed);
                }
                catch (Exception exception) when (IsConfigurationFailure(exception))
                {
                    diagnostics.Add(
                        AgentPluginDiagnosticSeverity.Warning,
                        "mcp.invalid-server",
                        $"MCP server '{entry.Name}' was skipped: {Bound(exception.Message, 2_048)}",
                        path,
                        entry.Name);
                }
            }

            foreach (var clientHeader in options.McpServerHeaders.Keys)
            {
                if (!entries.Any(entry => string.Equals(entry.Name, clientHeader, StringComparison.Ordinal)))
                {
                    diagnostics.Add(
                        AgentPluginDiagnosticSeverity.Warning,
                        "mcp.unknown-client-header-target",
                        $"Client headers target unknown MCP server '{clientHeader}' and were ignored.",
                        path,
                        clientHeader);
                }
            }

            return Array.AsReadOnly(result.ToArray());
        }
        catch (Exception exception) when (IsConfigurationFailure(exception) || exception is AgentPluginLoadException)
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Error,
                "mcp.invalid-component",
                "The MCP component was disabled: " + Bound(exception.Message, 2_048),
                path,
                "mcp");
            return Array.Empty<AgentPluginMcpConfiguration>();
        }
    }

    private static AgentPluginMcpConfiguration ParseMcpServer(
        string id,
        JsonElement value,
        string root,
        string? dataDirectory,
        AgentPluginLoadOptions options)
    {
        if (id.Length > options.MaximumMetadataStringCharacters)
        {
            throw new PluginConfigurationException("The MCP server ID exceeds the client safety limit.");
        }

        RequireKind(value, JsonValueKind.Object, "An MCP server entry must be an object.");
        var type = RequiredString(value, "type", 64);
        return type switch
        {
            "stdio" => ParseStdioServer(id, value, root, dataDirectory, options),
            "streamable-http" => ParseHttpServer(id, value, AgentPluginMcpTransport.StreamableHttp, options),
            "sse" => ParseHttpServer(id, value, AgentPluginMcpTransport.LegacySse, options),
            _ => throw new PluginConfigurationException($"Unsupported MCP transport '{type}'."),
        };
    }

    private static AgentPluginMcpConfiguration ParseStdioServer(
        string id,
        JsonElement value,
        string root,
        string? dataDirectory,
        AgentPluginLoadOptions options)
    {
        RequireExactFields(value, StdioFields);
        if (dataDirectory is null)
        {
            throw new PluginConfigurationException("A client-managed PluginDataDirectory is required for stdio MCP servers.");
        }

        EnsureDataDirectory(dataDirectory);
        var command = RequiredString(value, "command", 32_768);
        if (command.IndexOf('\0') >= 0)
        {
            throw new PluginConfigurationException("The MCP command contains a null character.");
        }

        if (command.StartsWith("./", StringComparison.Ordinal))
        {
            if (command.Length == 2)
            {
                throw new PluginConfigurationException("The plugin-relative MCP command is empty.");
            }

            command = ResolveContained(root, command.Substring(2), "MCP command");
        }
        else if (command.IndexOf('/') >= 0 || command.IndexOf('\\') >= 0)
        {
            throw new PluginConfigurationException("An MCP command must be a bare executable name or begin with './'.");
        }

        var arguments = new List<string>();
        if (value.TryGetProperty("args", out var args))
        {
            RequireKind(args, JsonValueKind.Array, "MCP args must be an array.");
            var items = args.EnumerateArray().ToArray();
            if (items.Length > options.MaximumArgumentsPerServer)
            {
                throw new PluginConfigurationException("MCP arguments exceed the client safety limit.");
            }

            foreach (var item in items)
            {
                arguments.Add(ExpandPlaceholders(
                    RequireString(item, "MCP argument", 65_536),
                    root,
                    dataDirectory));
            }
        }

        var environmentNameComparer = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var environment = new Dictionary<string, string?>(environmentNameComparer);
        if (value.TryGetProperty("env", out var env))
        {
            RequireKind(env, JsonValueKind.Object, "MCP env must be an object.");
            var entries = env.EnumerateObject().ToArray();
            if (entries.Length > options.MaximumEnvironmentVariablesPerServer)
            {
                throw new PluginConfigurationException("MCP environment variables exceed the client safety limit.");
            }

            foreach (var pair in entries)
            {
                if (environmentNameComparer.Equals(pair.Name, "PLUGIN_ROOT")
                    || environmentNameComparer.Equals(pair.Name, "PLUGIN_DATA")
                    || pair.Name.Length == 0
                    || pair.Name.Length > 512
                    || pair.Name.IndexOfAny(new[] { '=', '\0' }) >= 0
                    || !environment.TryAdd(
                        pair.Name,
                        ExpandPlaceholders(
                            RequireString(pair.Value, "MCP environment value", 65_536),
                            root,
                            dataDirectory)))
                {
                    throw new PluginConfigurationException("MCP environment variables are invalid or reserved.");
                }
            }
        }

        environment.Add("PLUGIN_ROOT", root);
        environment.Add("PLUGIN_DATA", dataDirectory);

        var workingDirectory = root;
        if (value.TryGetProperty("cwd", out var cwd))
        {
            workingDirectory = ResolveWorkingDirectory(
                RequireString(cwd, "MCP cwd", 32_768),
                root,
                dataDirectory);
        }

        return new AgentPluginMcpConfiguration(
            id,
            AgentPluginMcpTransport.Stdio,
            command,
            Array.AsReadOnly(arguments.ToArray()),
            workingDirectory,
            new ReadOnlyDictionary<string, string?>(environment),
            null,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    private static AgentPluginMcpConfiguration ParseHttpServer(
        string id,
        JsonElement value,
        AgentPluginMcpTransport transport,
        AgentPluginLoadOptions options)
    {
        RequireExactFields(value, HttpFields);
        var url = RequiredString(value, "url", 32_768);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || endpoint.UserInfo.Length > 0
            || endpoint.Fragment.Length > 0
            || (endpoint.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(endpoint.Host)))
        {
            throw new PluginConfigurationException(
                "A remote MCP endpoint must be absolute HTTPS; HTTP is allowed only for localhost or a loopback IP literal.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value.TryGetProperty("headers", out var configuredHeaders))
        {
            ParseHeaders(configuredHeaders, headers, options.MaximumHeadersPerServer, overwrite: false);
        }

        if (options.McpServerHeaders.TryGetValue(id, out var clientHeaders))
        {
            if (clientHeaders.Count > options.MaximumHeadersPerServer)
            {
                throw new PluginConfigurationException("Client MCP headers exceed the configured limit.");
            }

            var clientHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in clientHeaders)
            {
                if (!clientHeaderNames.Add(pair.Key))
                {
                    throw new PluginConfigurationException("Client MCP headers contain duplicate case-insensitive names.");
                }

                ValidateHeader(pair.Key, pair.Value);
                headers[pair.Key] = pair.Value;
            }
        }

        if (headers.Count > options.MaximumHeadersPerServer)
        {
            throw new PluginConfigurationException("Merged MCP headers exceed the configured limit.");
        }

        return new AgentPluginMcpConfiguration(
            id,
            transport,
            null,
            Array.Empty<string>(),
            null,
            new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)),
            endpoint,
            new ReadOnlyDictionary<string, string>(headers));
    }

    private static void ParseHeaders(
        JsonElement value,
        Dictionary<string, string> destination,
        int maximumHeaders,
        bool overwrite)
    {
        RequireKind(value, JsonValueKind.Object, "MCP headers must be an object.");
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length > maximumHeaders)
        {
            throw new PluginConfigurationException("MCP headers exceed the configured limit.");
        }

        foreach (var property in properties)
        {
            var headerValue = RequireString(property.Value, "MCP header value", 65_536);
            ValidateHeader(property.Name, headerValue);
            if (overwrite)
            {
                destination[property.Name] = headerValue;
            }
            else if (!destination.TryAdd(property.Name, headerValue))
            {
                throw new PluginConfigurationException("MCP headers contain duplicate case-insensitive names.");
            }
        }
    }

    private static void ValidateHeader(string name, string value)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > 256
            || value is null
            || value.Length > 65_536
            || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
            || name.Any(character => !IsHeaderTokenCharacter(character)))
        {
            throw new PluginConfigurationException("An MCP HTTP header is invalid.");
        }
    }

    private static bool IsHeaderTokenCharacter(char value) =>
        value is >= '0' and <= '9'
        or >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or '!'
        or '#'
        or '$'
        or '%'
        or '&'
        or '\''
        or '*'
        or '+'
        or '-'
        or '.'
        or '^'
        or '_'
        or '`'
        or '|'
        or '~';

    private static string ResolveWorkingDirectory(string value, string root, string dataDirectory)
    {
        if (value.StartsWith("./", StringComparison.Ordinal))
        {
            return value.Length == 2
                ? root
                : ResolveContained(root, value.Substring(2), "MCP cwd");
        }

        if (string.Equals(value, "${PLUGIN_ROOT}", StringComparison.Ordinal))
        {
            return root;
        }

        if (value.StartsWith("${PLUGIN_ROOT}/", StringComparison.Ordinal))
        {
            var relative = value.Substring("${PLUGIN_ROOT}/".Length);
            return relative.Length == 0 ? root : ResolveContained(root, relative, "MCP cwd");
        }

        if (string.Equals(value, "${PLUGIN_DATA}", StringComparison.Ordinal))
        {
            return dataDirectory;
        }

        if (value.StartsWith("${PLUGIN_DATA}/", StringComparison.Ordinal))
        {
            var relative = value.Substring("${PLUGIN_DATA}/".Length);
            return relative.Length == 0 ? dataDirectory : ResolveContained(dataDirectory, relative, "MCP cwd");
        }

        throw new PluginConfigurationException(
            "MCP cwd must begin with './', '${PLUGIN_ROOT}', or '${PLUGIN_DATA}'.");
    }

    private static string ExpandPlaceholders(string value, string root, string dataDirectory)
    {
        const string rootToken = "${PLUGIN_ROOT}";
        const string dataToken = "${PLUGIN_DATA}";
        var builder = new StringBuilder(value.Length + root.Length + dataDirectory.Length);
        for (var index = 0; index < value.Length;)
        {
            if (value.AsSpan(index).StartsWith(rootToken.AsSpan(), StringComparison.Ordinal))
            {
                builder.Append(root);
                index += rootToken.Length;
            }
            else if (value.AsSpan(index).StartsWith(dataToken.AsSpan(), StringComparison.Ordinal))
            {
                builder.Append(dataDirectory);
                index += dataToken.Length;
            }
            else
            {
                builder.Append(value[index]);
                index++;
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, string> DiscoverClientExtensionDirectories(
        string root,
        DiagnosticBuffer diagnostics)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(1_025)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Warning,
                "extensions.discovery-failed",
                "Client extension directories could not be enumerated.",
                root,
                "extensions");
            return new ReadOnlyDictionary<string, string>(result);
        }

        if (directories.Length > 1_024)
        {
            diagnostics.Add(
                AgentPluginDiagnosticSeverity.Warning,
                "extensions.limit-exceeded",
                "Client extension directory discovery reached its safety limit.",
                root,
                "extensions");
            directories = directories.Take(1_024).ToArray();
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (!IsExtensionNamespace(name))
            {
                continue;
            }

            if (!IsWithin(root, directory) || IsReparsePoint(directory))
            {
                diagnostics.Add(
                    AgentPluginDiagnosticSeverity.Warning,
                    "extensions.unsafe-directory",
                    $"Client extension directory '{name}' was skipped because it does not resolve safely.",
                    directory,
                    name);
                continue;
            }

            result.Add(name, directory);
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static string? ResolveDataDirectory(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("PluginDataDirectory cannot be empty.", nameof(value));
        }

        return Path.GetFullPath(value);
    }

    private static void EnsureDataDirectory(string path)
    {
        Directory.CreateDirectory(path);
        RequireDirectory(path, "Plugin data directory");
        RejectReparsePoint(path, "Plugin data directory");
    }

    private static string ResolveContained(string root, string relativePath, string description)
    {
        if (relativePath.Length == 0 || Path.IsPathRooted(relativePath))
        {
            throw new PluginConfigurationException($"The {description} path is invalid.");
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(root, normalized));
        if (!IsWithin(root, resolved))
        {
            throw new PluginConfigurationException($"The {description} path escapes its configured root.");
        }

        RejectExistingReparsePoints(root, resolved, description);
        return resolved;
    }

    private static void RejectExistingReparsePoints(string root, string resolved, string description)
    {
        var relative = resolved.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(root);
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
            {
                throw new PluginConfigurationException($"The {description} path crosses a symbolic link or reparse point.");
            }
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonicalPath = Path.GetFullPath(path);
        return string.Equals(canonicalRoot, canonicalPath, comparison)
               || canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison)
               || canonicalPath.StartsWith(canonicalRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static string CreateToolPrefix(string pluginName, string serverId, int index)
    {
        var safePlugin = SanitizeToolName(pluginName);
        var safeServer = SanitizeToolName(serverId);
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(serverId)), 0, 6)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
        var prefix = safePlugin + "__" + safeServer + "__" + index + "_" + hash + "__";
        return prefix.Length <= 256 ? prefix : safePlugin.Substring(0, Math.Min(64, safePlugin.Length)) + "__" + hash + "__";
    }

    private static string SanitizeToolName(string value)
    {
        var result = new string(value.Select(character =>
            character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' ? character : '_').ToArray());
        return string.IsNullOrEmpty(result) ? "server" : result;
    }

    private static bool IsPluginName(string value)
    {
        if (value.Length is < 1 or > 64
            || !IsLowerAlphaNumeric(value[0])
            || !IsLowerAlphaNumeric(value[value.Length - 1])
            || value.Contains("--", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return value.All(character => IsLowerAlphaNumeric(character) || character is '-' or '.');
    }

    private static bool IsExtensionNamespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
        {
            return false;
        }

        var labels = value.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63
            && IsLowerAlphaNumeric(label[0])
            && IsLowerAlphaNumeric(label[label.Length - 1])
            && label.All(character => IsLowerAlphaNumeric(character) || character == '-'));
    }

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static AgentPluginDiagnosticSeverity MapSeverity(GameResourceDiagnosticSeverity severity) =>
        severity == GameResourceDiagnosticSeverity.Warning
            ? AgentPluginDiagnosticSeverity.Warning
            : AgentPluginDiagnosticSeverity.Error;

    private static JsonDocument ParseJson(
        string value,
        string path,
        bool validateDescendantObjects = true)
    {
        try
        {
            var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
            if (validateDescendantObjects)
            {
                EnsureUnambiguous(document.RootElement);
            }
            else
            {
                EnsureObjectNamesUnique(document.RootElement);
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new PluginConfigurationException($"JSON document '{path}' is invalid.", exception);
        }
    }

    private static void EnsureObjectNamesUnique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new PluginConfigurationException($"Duplicate JSON property '{property.Name}' is not allowed.");
            }
        }
    }

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new PluginConfigurationException($"Duplicate JSON property '{property.Name}' is not allowed.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    private static void RequireExactFields(JsonElement value, HashSet<string> fields)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Contains(property.Name))
            {
                throw new PluginConfigurationException($"Unknown field '{property.Name}' is not allowed here.");
            }
        }
    }

    private static string RequiredString(JsonElement value, string propertyName, int maximumCharacters)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            throw new PluginConfigurationException($"Required field '{propertyName}' is missing.");
        }

        return RequireString(property, propertyName, maximumCharacters, requireNonEmpty: true);
    }

    private static string? OptionalString(JsonElement value, string propertyName, int maximumCharacters)
    {
        return value.TryGetProperty(propertyName, out var property)
            ? RequireString(property, propertyName, maximumCharacters)
            : null;
    }

    private static string RequireString(
        JsonElement value,
        string description,
        int maximumCharacters,
        bool requireNonEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PluginConfigurationException($"{description} must be a string.");
        }

        var result = value.GetString()!;
        if ((requireNonEmpty && result.Length == 0) || result.Length > maximumCharacters || result.IndexOf('\0') >= 0)
        {
            throw new PluginConfigurationException($"{description} violates its client safety bounds.");
        }

        return result;
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, string message)
    {
        if (value.ValueKind != kind)
        {
            throw new PluginConfigurationException(message);
        }
    }

    private static string ReadBounded(string path, int maximumCharacters)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            var buffer = new char[Math.Min(maximumCharacters + 1, 65_536)];
            var builder = new StringBuilder(Math.Min(maximumCharacters, 65_536));
            while (builder.Length <= maximumCharacters)
            {
                var remaining = maximumCharacters + 1 - builder.Length;
                var read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    return builder.ToString();
                }

                builder.Append(buffer, 0, read);
            }

            throw new PluginConfigurationException($"File '{path}' exceeds its configured character limit.");
        }
        catch (DecoderFallbackException exception)
        {
            throw new PluginConfigurationException($"File '{path}' is not valid UTF-8.", exception);
        }
        catch (IOException exception)
        {
            throw new AgentPluginLoadException($"File '{path}' could not be read.", path, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentPluginLoadException($"File '{path}' could not be read.", path, exception);
        }
    }

    private static void RequirePackageFile(string root, string path, string description)
    {
        if (!IsWithin(root, path) || !File.Exists(path) || IsReparsePoint(path))
        {
            throw new AgentPluginLoadException($"The {description} is missing or unsafe.", path);
        }
    }

    private static void RequireDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new AgentPluginLoadException($"The {description} does not exist.", path);
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if (IsReparsePoint(path))
        {
            throw new AgentPluginLoadException($"The {description} cannot be a symbolic link or reparse point.", path);
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsConfigurationFailure(Exception exception) =>
        exception is PluginConfigurationException
        or JsonException
        or OverflowException
        or ArgumentException
        or NotSupportedException
        or PathTooLongException;

    private static string Bound(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value.Substring(0, maximumCharacters);

    private sealed class PluginConfigurationException : Exception
    {
        public PluginConfigurationException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    private sealed class DiagnosticBuffer
    {
        private readonly int _maximumCount;
        private readonly int _maximumCharacters;
        private readonly List<AgentPluginDiagnostic> _items = new();
        private int _characters;

        public DiagnosticBuffer(int maximumCount, int maximumCharacters)
        {
            _maximumCount = maximumCount;
            _maximumCharacters = maximumCharacters;
        }

        public IReadOnlyList<AgentPluginDiagnostic> Items => _items;

        public void Add(
            AgentPluginDiagnosticSeverity severity,
            string code,
            string message,
            string path,
            string? component)
        {
            if (_items.Count >= _maximumCount || _characters >= _maximumCharacters)
            {
                return;
            }

            var bounded = Bound(SanitizeDiagnostic(message), Math.Min(8_192, _maximumCharacters - _characters));
            if (bounded.Length == 0)
            {
                return;
            }

            _items.Add(new AgentPluginDiagnostic(severity, code, bounded, path, component));
            _characters += bounded.Length;
        }

        private static string SanitizeDiagnostic(string value)
        {
            var characters = value.Select(character =>
                char.IsControl(character) && character != '\t' ? ' ' : character).ToArray();
            return new string(characters);
        }
    }
}
