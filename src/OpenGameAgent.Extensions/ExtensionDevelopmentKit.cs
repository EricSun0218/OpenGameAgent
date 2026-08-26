using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public static class GameExtensionPermissions
{
    public const string ContextContribute = "context.contribute";
    public const string ToolsRegister = "tools.register";
    public const string ToolsFilter = "tools.filter";
    public const string SkillsRegister = "skills.register";
    public const string HooksRegister = "hooks.register";
    public const string PromptContribute = "prompt.contribute";
    public const string ModelsRegister = "models.register";
    public const string ServicesRegister = "services.register";
    public const string EventsSubscribe = "events.subscribe";

    public static IReadOnlyCollection<string> All { get; } = Array.AsReadOnly(new[]
    {
        ContextContribute,
        ToolsRegister,
        ToolsFilter,
        SkillsRegister,
        HooksRegister,
        PromptContribute,
        ModelsRegister,
        ServicesRegister,
        EventsSubscribe,
    });
}

public sealed class GameExtensionDependency
{
    public GameExtensionDependency(string id, string minimumVersion)
    {
        Id = RequireId(id, nameof(id));
        MinimumVersion = RequireVersion(minimumVersion, nameof(minimumVersion));
    }

    public string Id { get; }

    public string MinimumVersion { get; }

    internal static string RequireId(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded extension identifier is required.", name)
            : value.Trim();

    internal static string RequireVersion(string value, string name)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalized.Length > 128 || normalized.Any(char.IsControl) || !GameExtensionSemanticVersion.TryParse(normalized, out _))
        {
            throw new ArgumentException("A semantic extension version is required.", name);
        }

        return normalized;
    }
}

public sealed class GameExtensionDevelopmentManifest
{
    public GameExtensionDevelopmentManifest(
        string id,
        string version,
        IEnumerable<string>? permissions = null,
        IEnumerable<GameExtensionDependency>? dependencies = null)
    {
        Id = GameExtensionDependency.RequireId(id, nameof(id));
        Version = GameExtensionDependency.RequireVersion(version, nameof(version));
        var known = new HashSet<string>(GameExtensionPermissions.All, StringComparer.Ordinal);
        var permissionValues = (permissions ?? Array.Empty<string>())
            .Select(value => GameExtensionDependency.RequireId(value, nameof(permissions)))
            .ToArray();
        if (permissionValues.Length > 64
            || permissionValues.Distinct(StringComparer.Ordinal).Count() != permissionValues.Length
            || permissionValues.Any(value => !known.Contains(value)))
        {
            throw new ArgumentException("Extension permissions must be unique known permission identifiers.", nameof(permissions));
        }

        var dependencyValues = (dependencies ?? Array.Empty<GameExtensionDependency>()).ToArray();
        if (dependencyValues.Length > 64
            || dependencyValues.Any(value => value is null)
            || dependencyValues.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != dependencyValues.Length
            || dependencyValues.Any(value => string.Equals(value.Id, Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Extension dependencies must be bounded, unique, non-null, and cannot reference the extension itself.", nameof(dependencies));
        }

        Permissions = Array.AsReadOnly(permissionValues.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Dependencies = Array.AsReadOnly(dependencyValues.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());
    }

    public string SchemaVersion => "1";

    public string Id { get; }

    public string Version { get; }

    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<GameExtensionDependency> Dependencies { get; }

    public static GameExtensionDevelopmentManifest Parse(string json, int maximumCharacters = 1_000_000)
    {
        if (string.IsNullOrWhiteSpace(json) || maximumCharacters is < 2 or > 10_000_000 || json.Length > maximumCharacters)
        {
            throw new ArgumentException("The extension manifest is empty or exceeds its configured bound.", nameof(json));
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 32,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The extension manifest root must be an object.");
            }

            var allowed = new HashSet<string>(new[]
            {
                "schemaVersion",
                "id",
                "version",
                "permissions",
                "dependencies",
            }, StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name) || !allowed.Contains(property.Name))
                {
                    throw new FormatException("The extension manifest contains a duplicate or unsupported field.");
                }
            }

            if (!TryRequiredString(root, "schemaVersion", out var schemaVersion)
                || !string.Equals(schemaVersion, "1", StringComparison.Ordinal)
                || !TryRequiredString(root, "id", out var id)
                || !TryRequiredString(root, "version", out var version))
            {
                throw new FormatException("The extension manifest has invalid identity fields.");
            }

            var permissions = ParsePermissions(root);
            var dependencies = ParseDependencies(root);
            return new GameExtensionDevelopmentManifest(id, version, permissions, dependencies);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The extension manifest is invalid JSON.", exception);
        }
    }

    private static IReadOnlyList<string> ParsePermissions(JsonElement root)
    {
        if (!root.TryGetProperty("permissions", out var value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Extension permissions must be an array.");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new FormatException("Extension permissions must contain strings.");
            }

            result.Add(item.GetString()!);
            if (result.Count > 64)
            {
                throw new FormatException("The extension manifest contains too many permissions.");
            }
        }

        return result;
    }

    private static IReadOnlyList<GameExtensionDependency> ParseDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var value))
        {
            return Array.Empty<GameExtensionDependency>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Extension dependencies must be an array.");
        }

        var result = new List<GameExtensionDependency>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Each extension dependency must be an object.");
            }

            var names = item.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 2
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(name => name is not "id" and not "minimumVersion")
                || !TryRequiredString(item, "id", out var id)
                || !TryRequiredString(item, "minimumVersion", out var minimumVersion))
            {
                throw new FormatException("An extension dependency has invalid fields.");
            }

            result.Add(new GameExtensionDependency(id, minimumVersion));
            if (result.Count > 64)
            {
                throw new FormatException("The extension manifest contains too many dependencies.");
            }
        }

        return result;
    }

    private static bool TryRequiredString(JsonElement value, string name, out string result)
    {
        if (value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            result = property.GetString()!;
            return true;
        }

        result = string.Empty;
        return false;
    }
}

public enum GameExtensionConformanceSeverity
{
    Information,
    Warning,
    Error,
}

public sealed class GameExtensionConformanceDiagnostic
{
    public GameExtensionConformanceDiagnostic(
        GameExtensionConformanceSeverity severity,
        string code,
        string message)
    {
        Severity = severity;
        Code = GameExtensionDependency.RequireId(code, nameof(code));
        Message = string.IsNullOrWhiteSpace(message) || message.Length > 4_096
            ? throw new ArgumentException("A bounded conformance message is required.", nameof(message))
            : message;
    }

    public GameExtensionConformanceSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }
}

public sealed class GameExtensionConformanceOptions
{
    public IReadOnlyCollection<string> AllowedPermissions { get; set; } = GameExtensionPermissions.All;

    public IReadOnlyList<GameAgentExtensionDescriptor> AvailableExtensions { get; set; } =
        Array.Empty<GameAgentExtensionDescriptor>();

    public GameInput? SmokeInput { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    internal GameExtensionConformanceOptions CopyAndValidate()
    {
        var copy = (GameExtensionConformanceOptions)MemberwiseClone();
        if (copy.AllowedPermissions is null
            || copy.AllowedPermissions.Count > 64
            || copy.AllowedPermissions.Any(value => string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("The allowed extension permissions are invalid.", nameof(AllowedPermissions));
        }

        if (copy.AvailableExtensions is null
            || copy.AvailableExtensions.Count > 256
            || copy.AvailableExtensions.Any(value => value is null))
        {
            throw new ArgumentException("The available extension set is invalid.", nameof(AvailableExtensions));
        }

        if (copy.Timeout <= TimeSpan.Zero || copy.Timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        return copy;
    }
}

public sealed class GameExtensionConformanceReport
{
    internal GameExtensionConformanceReport(
        IReadOnlyList<GameExtensionConformanceDiagnostic> diagnostics,
        IReadOnlyList<GameAgentExtensionResource> resources,
        int modelRequestCount)
    {
        Diagnostics = diagnostics;
        Resources = resources;
        ModelRequestCount = modelRequestCount;
    }

    public bool Passed => Diagnostics.All(value => value.Severity != GameExtensionConformanceSeverity.Error);

    public IReadOnlyList<GameExtensionConformanceDiagnostic> Diagnostics { get; }

    public IReadOnlyList<GameAgentExtensionResource> Resources { get; }

    public int ModelRequestCount { get; }
}

public static class GameExtensionConformance
{
    public static async ValueTask<GameExtensionConformanceReport> RunAsync(
        IGameAgentExtension extension,
        GameExtensionDevelopmentManifest manifest,
        GameExtensionConformanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (extension is null)
        {
            throw new ArgumentNullException(nameof(extension));
        }

        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var settings = (options ?? new GameExtensionConformanceOptions()).CopyAndValidate();
        var diagnostics = new List<GameExtensionConformanceDiagnostic>();
        ValidateIdentity(extension.Descriptor, manifest, diagnostics);
        ValidateDependencies(manifest, settings.AvailableExtensions, diagnostics);
        ValidateAllowedPermissions(manifest, settings.AllowedPermissions, diagnostics);
        if (HasErrors(diagnostics))
        {
            return Report(diagnostics, Array.Empty<GameAgentExtensionResource>(), 0);
        }

        var provider = new ExtensionConformanceModelProvider();
        GameAgentRuntime? runtime = null;
        try
        {
            runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "extension-conformance")
            {
                Extensions = { extension },
            });
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error("extension.configure", "Extension configuration failed: " + exception.GetType().Name));
            return Report(diagnostics, Array.Empty<GameAgentExtensionResource>(), 0);
        }

        await using (runtime.ConfigureAwait(false))
        {
            var resources = runtime.ExtensionResources.ToArray();
            ValidateActualPermissions(manifest, resources, diagnostics);
            foreach (var diagnostic in runtime.ExtensionDiagnostics.Where(value => value.Severity == GameAgentExtensionDiagnosticSeverity.Error))
            {
                diagnostics.Add(Error("extension.runtime-diagnostic", diagnostic.Code));
            }

            if (!HasErrors(diagnostics))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.Timeout);
                try
                {
                    var result = await runtime.RunAsync(
                        settings.SmokeInput ?? CreateInput(),
                        timeout.Token).ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        diagnostics.Add(Error("extension.smoke-failed", "The extension smoke run did not complete successfully."));
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    diagnostics.Add(Error("extension.smoke-timeout", "The extension smoke run exceeded its configured timeout."));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    diagnostics.Add(Error("extension.smoke-exception", "The extension smoke run failed: " + exception.GetType().Name));
                }

                foreach (var diagnostic in runtime.ExtensionDiagnostics.Where(value => value.Severity == GameAgentExtensionDiagnosticSeverity.Error))
                {
                    diagnostics.Add(Error("extension.runtime-diagnostic", diagnostic.Code));
                }
            }

            return Report(diagnostics, resources, provider.RequestCount);
        }
    }

    private static void ValidateIdentity(
        GameAgentExtensionDescriptor descriptor,
        GameExtensionDevelopmentManifest manifest,
        ICollection<GameExtensionConformanceDiagnostic> diagnostics)
    {
        if (descriptor is null)
        {
            diagnostics.Add(Error("extension.descriptor-missing", "The extension returned no descriptor."));
            return;
        }

        if (!string.Equals(descriptor.Id, manifest.Id, StringComparison.Ordinal))
        {
            diagnostics.Add(Error("extension.id-mismatch", "The extension descriptor and manifest IDs differ."));
        }

        if (!string.Equals(descriptor.Version, manifest.Version, StringComparison.Ordinal))
        {
            diagnostics.Add(Error("extension.version-mismatch", "The extension descriptor and manifest versions differ."));
        }
    }

    private static void ValidateDependencies(
        GameExtensionDevelopmentManifest manifest,
        IReadOnlyList<GameAgentExtensionDescriptor> available,
        ICollection<GameExtensionConformanceDiagnostic> diagnostics)
    {
        var byId = available.GroupBy(value => value.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies)
        {
            if (!byId.TryGetValue(dependency.Id, out var descriptors))
            {
                diagnostics.Add(Error("extension.dependency-missing", "Required extension '" + dependency.Id + "' is unavailable."));
                continue;
            }

            var minimum = GameExtensionSemanticVersion.Parse(dependency.MinimumVersion);
            if (!descriptors.Any(value => GameExtensionSemanticVersion.TryParse(value.Version, out var actual) && actual.CompareTo(minimum) >= 0))
            {
                diagnostics.Add(Error("extension.dependency-version", "Required extension '" + dependency.Id + "' is below the declared minimum version."));
            }
        }
    }

    private static void ValidateAllowedPermissions(
        GameExtensionDevelopmentManifest manifest,
        IReadOnlyCollection<string> allowed,
        ICollection<GameExtensionConformanceDiagnostic> diagnostics)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var permission in manifest.Permissions.Where(permission => !set.Contains(permission)))
        {
            diagnostics.Add(Error("extension.permission-denied", "The host does not allow permission '" + permission + "'."));
        }
    }

    private static void ValidateActualPermissions(
        GameExtensionDevelopmentManifest manifest,
        IReadOnlyList<GameAgentExtensionResource> resources,
        ICollection<GameExtensionConformanceDiagnostic> diagnostics)
    {
        var declared = new HashSet<string>(manifest.Permissions, StringComparer.Ordinal);
        foreach (var permission in resources.Select(value => PermissionFor(value.Kind)).Distinct(StringComparer.Ordinal))
        {
            if (!declared.Contains(permission))
            {
                diagnostics.Add(Error(
                    "extension.permission-undeclared",
                    "The extension registered a resource requiring undeclared permission '" + permission + "'."));
            }
        }
    }

    private static string PermissionFor(GameAgentExtensionResourceKind kind) => kind switch
    {
        GameAgentExtensionResourceKind.ContextProvider => GameExtensionPermissions.ContextContribute,
        GameAgentExtensionResourceKind.Tool => GameExtensionPermissions.ToolsRegister,
        GameAgentExtensionResourceKind.ToolProvider => GameExtensionPermissions.ToolsRegister,
        GameAgentExtensionResourceKind.ToolVisibilityPolicy => GameExtensionPermissions.ToolsFilter,
        GameAgentExtensionResourceKind.SkillProvider => GameExtensionPermissions.SkillsRegister,
        GameAgentExtensionResourceKind.AgentHooks => GameExtensionPermissions.HooksRegister,
        GameAgentExtensionResourceKind.PromptFragment => GameExtensionPermissions.PromptContribute,
        GameAgentExtensionResourceKind.ModelProvider => GameExtensionPermissions.ModelsRegister,
        GameAgentExtensionResourceKind.Service => GameExtensionPermissions.ServicesRegister,
        GameAgentExtensionResourceKind.EventHandler => GameExtensionPermissions.EventsSubscribe,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool HasErrors(IEnumerable<GameExtensionConformanceDiagnostic> diagnostics) =>
        diagnostics.Any(value => value.Severity == GameExtensionConformanceSeverity.Error);

    private static GameExtensionConformanceDiagnostic Error(string code, string message) =>
        new(GameExtensionConformanceSeverity.Error, code, message);

    private static GameExtensionConformanceReport Report(
        IEnumerable<GameExtensionConformanceDiagnostic> diagnostics,
        IEnumerable<GameAgentExtensionResource> resources,
        int requestCount) => new(
        Array.AsReadOnly(diagnostics.ToArray()),
        Array.AsReadOnly(resources.ToArray()),
        requestCount);

    private static GameInput CreateInput() => new(
        "extension-conformance",
        "extension",
        "extension.conformance",
        "{}",
        new GameMoment("conformance", 0),
        "extension-conformance-input");

    private sealed class ExtensionConformanceModelProvider : IModelProvider
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("extension conformance") },
                ModelStopReason.Stop));
        }
    }
}

internal readonly struct GameExtensionSemanticVersion : IComparable<GameExtensionSemanticVersion>
{
    private GameExtensionSemanticVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }

    public static GameExtensionSemanticVersion Parse(string value) =>
        TryParse(value, out var result)
            ? result
            : throw new FormatException("The extension version is invalid.");

    public static bool TryParse(string value, out GameExtensionSemanticVersion result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        var normalized = value.Trim();
        var plus = normalized.IndexOf('+');
        if (plus >= 0)
        {
            if (normalized.IndexOf('+', plus + 1) >= 0
                || !AreIdentifiersValid(normalized.Substring(plus + 1), rejectNumericLeadingZeros: false))
            {
                return false;
            }

            normalized = normalized.Substring(0, plus);
        }

        string? prerelease = null;
        var dash = normalized.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = normalized.Substring(dash + 1);
            normalized = normalized.Substring(0, dash);
            if (!AreIdentifiersValid(prerelease, rejectNumericLeadingZeros: true))
            {
                return false;
            }
        }

        var parts = normalized.Split('.');
        if (parts.Length != 3
            || !TryNumber(parts[0], out var major)
            || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch))
        {
            return false;
        }

        result = new GameExtensionSemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(GameExtensionSemanticVersion other)
    {
        var value = Major.CompareTo(other.Major);
        if (value != 0) return value;
        value = Minor.CompareTo(other.Minor);
        if (value != 0) return value;
        value = Patch.CompareTo(other.Patch);
        if (value != 0) return value;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static bool TryNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0
            && (value.Length == 1 || value[0] != '0')
            && value.All(char.IsDigit)
            && int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out number);
    }

    private static bool AreIdentifiersValid(string value, bool rejectNumericLeadingZeros) =>
        value.Length > 0 && value.Split('.').All(part =>
            part.Length > 0
            && part.All(character =>
                character is >= '0' and <= '9'
                || character is >= 'A' and <= 'Z'
                || character is >= 'a' and <= 'z'
                || character == '-')
            && (!rejectNumericLeadingZeros
                || !part.All(character => character is >= '0' and <= '9')
                || part.Length == 1
                || part[0] != '0'));

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var leftNumeric = leftParts[index].All(character => character is >= '0' and <= '9');
            var rightNumeric = rightParts[index].All(character => character is >= '0' and <= '9');
            int comparison;
            if (leftNumeric && rightNumeric)
            {
                comparison = leftParts[index].Length.CompareTo(rightParts[index].Length);
                if (comparison == 0)
                {
                    comparison = string.CompareOrdinal(leftParts[index], rightParts[index]);
                }
            }
            else if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.CompareOrdinal(leftParts[index], rightParts[index]);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }
}
