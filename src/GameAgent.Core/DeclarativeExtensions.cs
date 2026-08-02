using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class DeclarativeExtensionResource
{
    public string Uri { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public string Digest { get; set; } = string.Empty;
}

public sealed class DeclarativeExtensionManifest
{
    public string ManifestVersion { get; set; } = "1";

    public string Namespace { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;

    public string Digest { get; set; } = string.Empty;

    public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> SkillRefs { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ToolSchemaRefs { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> WorkflowTemplateRefs { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ProviderIds { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ContextContributorIds { get; set; } = Array.Empty<string>();

    public IReadOnlyList<DeclarativeExtensionResource> Resources { get; set; } =
        Array.Empty<DeclarativeExtensionResource>();
}

public sealed class DeclarativeExtensionException : Exception
{
    public DeclarativeExtensionException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed class DeclarativeExtensionCatalogSnapshot
{
    internal DeclarativeExtensionCatalogSnapshot(
        long revision,
        IReadOnlyList<DeclarativeExtensionManifest> manifests)
    {
        Revision = revision;
        Manifests = manifests;
    }

    public long Revision { get; }

    public IReadOnlyList<DeclarativeExtensionManifest> Manifests { get; }
}

/// <summary>
/// Captures a closed-world set of declarative bindings. The catalog never
/// loads assemblies, scripts, native libraries, or executable payloads; every
/// reference must resolve against a registry owned by the game host.
/// </summary>
public sealed class DeclarativeExtensionCatalog
{
    private readonly object _gate = new();
    private DeclarativeExtensionCatalogSnapshot _snapshot =
        new(0, Array.Empty<DeclarativeExtensionManifest>());

    public DeclarativeExtensionCatalogSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return Snapshot(_snapshot);
            }
        }
    }

    public DeclarativeExtensionCatalogSnapshot Replace(
        IEnumerable<DeclarativeExtensionManifest> manifests)
    {
        if (manifests is null)
        {
            throw new ArgumentNullException(nameof(manifests));
        }

        var admitted = manifests.Take(1_025)
            .Select(DeclarativeExtensionCodec.Validate)
            .OrderBy(item => item.Namespace, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ToArray();
        if (admitted.Length > 1_024)
        {
            throw Invalid(
                "extension_manifest_count_exceeded",
                "The declarative extension catalog exceeds 1,024 manifests.");
        }

        var byIdentity = new Dictionary<string, DeclarativeExtensionManifest>(
            StringComparer.Ordinal);
        foreach (var manifest in admitted)
        {
            var identity = Identity(manifest);
            if (!byIdentity.TryAdd(identity, manifest))
            {
                throw Invalid(
                    "extension_identity_duplicate",
                    $"Declarative extension '{identity}' is duplicated.");
            }
        }

        foreach (var manifest in admitted)
        {
            foreach (var dependency in manifest.Dependencies)
            {
                if (!byIdentity.ContainsKey(dependency))
                {
                    throw Invalid(
                        "extension_dependency_missing",
                        $"Declarative extension dependency '{dependency}' is missing.");
                }
            }
        }

        EnsureAcyclic(byIdentity);
        lock (_gate)
        {
            _snapshot = new DeclarativeExtensionCatalogSnapshot(
                checked(_snapshot.Revision + 1),
                new ReadOnlyCollection<DeclarativeExtensionManifest>(
                    admitted.Select(DeclarativeExtensionCodec.Snapshot).ToArray()));
            return Snapshot(_snapshot);
        }
    }

    private static void EnsureAcyclic(
        IReadOnlyDictionary<string, DeclarativeExtensionManifest> manifests)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in manifests.Keys.OrderBy(item => item, StringComparer.Ordinal))
        {
            Visit(identity);
        }

        void Visit(string identity)
        {
            if (visited.Contains(identity))
            {
                return;
            }

            if (!visiting.Add(identity))
            {
                throw Invalid(
                    "extension_dependency_cycle",
                    "Declarative extension dependencies contain a cycle.");
            }

            foreach (var dependency in manifests[identity].Dependencies)
            {
                Visit(dependency);
            }

            visiting.Remove(identity);
            visited.Add(identity);
        }
    }

    private static string Identity(DeclarativeExtensionManifest manifest) =>
        manifest.Namespace + "@" + manifest.Version;

    private static DeclarativeExtensionCatalogSnapshot Snapshot(
        DeclarativeExtensionCatalogSnapshot snapshot) =>
        new(
            snapshot.Revision,
            new ReadOnlyCollection<DeclarativeExtensionManifest>(
                snapshot.Manifests.Select(DeclarativeExtensionCodec.Snapshot).ToArray()));

    private static DeclarativeExtensionException Invalid(string code, string message) =>
        new(code, message);
}

public static class DeclarativeExtensionCodec
{
    private const int MaxManifestBytes = 1_048_576;
    private static readonly HashSet<string> RootProperties = new(
        new[]
        {
            "manifestVersion", "namespace", "version", "protocolVersion", "digest",
            "dependencies", "skillRefs", "toolSchemaRefs", "workflowTemplateRefs",
            "providerIds", "contextContributorIds", "resources"
        },
        StringComparer.Ordinal);
    private static readonly HashSet<string> ResourceProperties = new(
        new[] { "uri", "mediaType", "digest" },
        StringComparer.Ordinal);

    public static DeclarativeExtensionManifest Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 2 or > MaxManifestBytes)
        {
            throw Invalid(
                "extension_manifest_bytes_invalid",
                "A declarative extension manifest must be between 2 bytes and 1 MiB.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
        }
        catch (JsonException exception)
        {
            throw new DeclarativeExtensionException(
                "extension_manifest_json_invalid",
                "The declarative extension manifest is not strict JSON: "
                + exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(
                    "extension_manifest_shape_invalid",
                    "A declarative extension manifest must be an object.");
            }

            RejectUnknown(root, RootProperties);
            var resources = new List<DeclarativeExtensionResource>();
            if (root.TryGetProperty("resources", out var resourceArray))
            {
                if (resourceArray.ValueKind != JsonValueKind.Array
                    || resourceArray.GetArrayLength() > 256)
                {
                    throw Invalid(
                        "extension_resource_limit",
                        "Declarative extension resources must be an array of at most 256 items.");
                }

                foreach (var resource in resourceArray.EnumerateArray())
                {
                    if (resource.ValueKind != JsonValueKind.Object)
                    {
                        throw Invalid(
                            "extension_resource_invalid",
                            "Every declarative extension resource must be an object.");
                    }

                    RejectUnknown(resource, ResourceProperties);
                    resources.Add(new DeclarativeExtensionResource
                    {
                        Uri = RequiredString(resource, "uri"),
                        MediaType = RequiredString(resource, "mediaType"),
                        Digest = RequiredString(resource, "digest")
                    });
                }
            }

            return Validate(new DeclarativeExtensionManifest
            {
                ManifestVersion = RequiredString(root, "manifestVersion"),
                Namespace = RequiredString(root, "namespace"),
                Version = RequiredString(root, "version"),
                ProtocolVersion = RequiredString(root, "protocolVersion"),
                Digest = RequiredString(root, "digest"),
                Dependencies = ReadStrings(root, "dependencies"),
                SkillRefs = ReadStrings(root, "skillRefs"),
                ToolSchemaRefs = ReadStrings(root, "toolSchemaRefs"),
                WorkflowTemplateRefs = ReadStrings(root, "workflowTemplateRefs"),
                ProviderIds = ReadStrings(root, "providerIds"),
                ContextContributorIds = ReadStrings(root, "contextContributorIds"),
                Resources = resources
            });
        }
    }

    public static string ComputeDigest(DeclarativeExtensionManifest manifest)
    {
        var snapshot = SnapshotCore(manifest, validateDigest: false);
        using var document = JsonDocument.Parse(SerializeCore(snapshot, includeDigest: false));
        return CanonicalJsonDigest.ComputeSha256(document.RootElement);
    }

    public static byte[] Serialize(DeclarativeExtensionManifest manifest) =>
        SerializeCore(Validate(manifest), includeDigest: true);

    internal static DeclarativeExtensionManifest Validate(
        DeclarativeExtensionManifest manifest)
    {
        var snapshot = SnapshotCore(manifest, validateDigest: true);
        if (snapshot.Digest != ComputeDigest(snapshot))
        {
            throw Invalid(
                "extension_digest_mismatch",
                "The declarative extension manifest digest does not match its content.");
        }

        return snapshot;
    }

    internal static DeclarativeExtensionManifest Snapshot(
        DeclarativeExtensionManifest manifest) => SnapshotCore(manifest, validateDigest: true);

    private static DeclarativeExtensionManifest SnapshotCore(
        DeclarativeExtensionManifest manifest,
        bool validateDigest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var result = new DeclarativeExtensionManifest
        {
            ManifestVersion = Required(manifest.ManifestVersion, "manifestVersion", 32),
            Namespace = Namespace(manifest.Namespace),
            Version = Version(manifest.Version),
            ProtocolVersion = Required(manifest.ProtocolVersion, "protocolVersion", 32),
            Digest = validateDigest ? Digest(manifest.Digest) : manifest.Digest,
            Dependencies = IdentityReferences(manifest.Dependencies),
            SkillRefs = Strings(manifest.SkillRefs, "skillRefs"),
            ToolSchemaRefs = Strings(manifest.ToolSchemaRefs, "toolSchemaRefs"),
            WorkflowTemplateRefs = Strings(
                manifest.WorkflowTemplateRefs, "workflowTemplateRefs"),
            ProviderIds = Strings(manifest.ProviderIds, "providerIds"),
            ContextContributorIds = Strings(
                manifest.ContextContributorIds, "contextContributorIds")
        };
        if (result.ManifestVersion != "1"
            || result.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            throw Invalid(
                "extension_compatibility_invalid",
                "The declarative extension manifest version is incompatible.");
        }

        var resources = (manifest.Resources ?? Array.Empty<DeclarativeExtensionResource>())
            .Take(257)
            .Select(resource => resource is null
                ? throw Invalid(
                    "extension_resource_invalid",
                    "A declarative extension resource cannot be null.")
                : new DeclarativeExtensionResource
                {
                    Uri = Required(resource.Uri, "resource.uri", 2_048),
                    MediaType = Required(resource.MediaType, "resource.mediaType", 128),
                    Digest = Digest(resource.Digest)
                })
            .OrderBy(resource => resource.Uri, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length > 256
            || resources.Select(resource => resource.Uri)
                .Distinct(StringComparer.Ordinal).Count() != resources.Length)
        {
            throw Invalid(
                "extension_resource_invalid",
                "Declarative extension resources are duplicated or exceed 256 items.");
        }

        result.Resources = new ReadOnlyCollection<DeclarativeExtensionResource>(resources);
        return result;
    }

    private static byte[] SerializeCore(
        DeclarativeExtensionManifest manifest,
        bool includeDigest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("manifestVersion", manifest.ManifestVersion);
            writer.WriteString("namespace", manifest.Namespace);
            writer.WriteString("version", manifest.Version);
            writer.WriteString("protocolVersion", manifest.ProtocolVersion);
            if (includeDigest)
            {
                writer.WriteString("digest", manifest.Digest);
            }

            WriteStrings(writer, "dependencies", manifest.Dependencies);
            WriteStrings(writer, "skillRefs", manifest.SkillRefs);
            WriteStrings(writer, "toolSchemaRefs", manifest.ToolSchemaRefs);
            WriteStrings(writer, "workflowTemplateRefs", manifest.WorkflowTemplateRefs);
            WriteStrings(writer, "providerIds", manifest.ProviderIds);
            WriteStrings(writer, "contextContributorIds", manifest.ContextContributorIds);
            writer.WriteStartArray("resources");
            foreach (var resource in manifest.Resources)
            {
                writer.WriteStartObject();
                writer.WriteString("uri", resource.Uri);
                writer.WriteString("mediaType", resource.MediaType);
                writer.WriteString("digest", resource.Digest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 256)
        {
            throw Invalid(
                "extension_list_invalid",
                $"Declarative extension property '{name}' must be an array of at most 256 strings.");
        }

        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw Invalid(
                    "extension_list_invalid",
                    $"Declarative extension property '{name}' contains a non-string."))
            .ToArray();
    }

    private static IReadOnlyList<string> Strings(
        IEnumerable<string>? values,
        string name)
    {
        var items = (values ?? Array.Empty<string>()).Take(257)
            .Select(value => Required(value, name, 256))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (items.Length > 256 || items.Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw Invalid(
                "extension_list_invalid",
                $"Declarative extension property '{name}' is duplicated or too large.");
        }

        return new ReadOnlyCollection<string>(items);
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string name,
        IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void RejectUnknown(JsonElement value, ISet<string> allowed)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid(
                    "extension_property_unknown",
                    $"Declarative extension property '{property.Name}' is not admitted.");
            }
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                "extension_property_missing",
                $"Declarative extension property '{name}' is required.");
        }

        return value.GetString()!;
    }

    private static string Required(string value, string name, int maximum) =>
        RuntimeGuard.RequiredUtf8(value, maximum, name);

    private static string Namespace(string value)
    {
        var candidate = Required(value, "namespace", 128);
        if (!candidate.All(character =>
                IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-'))
        {
            throw Invalid(
                "extension_namespace_invalid",
                "A declarative extension namespace may contain only ASCII letters, digits, '.', '_' and '-'.");
        }

        return candidate;
    }

    private static string Version(string value)
    {
        var candidate = Required(value, "version", 64);
        if (!candidate.All(character =>
                IsAsciiLetterOrDigit(character)
                || character is '.' or '+' or '_' or '-'))
        {
            throw Invalid(
                "extension_version_invalid",
                "A declarative extension version contains an unsupported character.");
        }

        return candidate;
    }

    private static IReadOnlyList<string> IdentityReferences(
        IEnumerable<string>? values)
    {
        var identities = Strings(values, "dependencies");
        foreach (var identity in identities)
        {
            var separator = identity.IndexOf('@');
            if (separator <= 0
                || separator != identity.LastIndexOf('@')
                || separator == identity.Length - 1)
            {
                throw Invalid(
                    "extension_dependency_invalid",
                    "A declarative extension dependency must use 'namespace@version'.");
            }

            _ = Namespace(identity.Substring(0, separator));
            _ = Version(identity.Substring(separator + 1));
        }

        return identities;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9';

    private static string Digest(string value)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw Invalid(
                "extension_digest_invalid",
                "Declarative extension digests must be lowercase SHA-256 values.");
        }

        return value;
    }

    private static DeclarativeExtensionException Invalid(string code, string message) =>
        new(code, message);
}
