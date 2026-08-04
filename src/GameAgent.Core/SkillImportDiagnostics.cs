using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public static class SkillDiagnosticSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

public sealed class SkillManifestDocument
{
    public SkillManifestDocument(string sourceId, string json)
    {
        SourceId = RuntimeGuard.RequiredUtf8(
            sourceId,
            512,
            nameof(sourceId));
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        JsonUtf8Bytes = Encoding.UTF8.GetByteCount(json);
        if (JsonUtf8Bytes > 1_048_576)
        {
            throw new RuntimeContentLimitException(
                nameof(json),
                "skill_manifest_bytes_exceeded",
                "A skill manifest exceeds one MiB.");
        }

        Json = json;
    }

    public string SourceId { get; }

    public string Json { get; }

    internal int JsonUtf8Bytes { get; }
}

public sealed class SkillManifestImportOptions
{
    public int MaxDocuments { get; set; } = 4_096;

    public long MaxAggregateUtf8Bytes { get; set; } = 64L * 1_048_576;

    public int MaxRetainedManifests { get; set; } = 4_096;

    public int MaxRetainedDiagnostics { get; set; } = 4_096;

    internal SkillManifestImportOptions Snapshot()
    {
        if (MaxDocuments is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDocuments));
        }

        if (MaxAggregateUtf8Bytes is < 1 or > 512L * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAggregateUtf8Bytes));
        }

        if (MaxRetainedManifests is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetainedManifests));
        }

        if (MaxRetainedDiagnostics is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetainedDiagnostics));
        }

        return new SkillManifestImportOptions
        {
            MaxDocuments = MaxDocuments,
            MaxAggregateUtf8Bytes = MaxAggregateUtf8Bytes,
            MaxRetainedManifests = MaxRetainedManifests,
            MaxRetainedDiagnostics = MaxRetainedDiagnostics
        };
    }
}

public sealed class SkillDiagnostic
{
    internal SkillDiagnostic(
        string sourceId,
        string severity,
        string code,
        string message)
    {
        SourceId = sourceId;
        Severity = severity;
        Code = code;
        Message = message;
    }

    public string SourceId { get; }

    public string Severity { get; }

    public string Code { get; }

    public string Message { get; }
}

public sealed class SkillImportResult
{
    internal SkillImportResult(
        IReadOnlyList<SkillManifest> manifests,
        IReadOnlyList<SkillDiagnostic> diagnostics)
    {
        Manifests = manifests;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<SkillManifest> Manifests { get; }

    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(
        item => string.Equals(
            item.Severity,
            SkillDiagnosticSeverities.Error,
            StringComparison.Ordinal));
}

/// <summary>
/// Validates build-time skill documents and returns editor-friendly,
/// source-bound diagnostics. It does not download or execute skill content.
/// </summary>
public sealed class SkillManifestImporter
{
    private readonly SkillManifestImportOptions _options;

    public SkillManifestImporter(SkillManifestImportOptions? options = null)
    {
        _options = (options ?? new SkillManifestImportOptions()).Snapshot();
    }

    public SkillImportResult Import(
        IEnumerable<SkillManifestDocument> documents)
    {
        if (documents is null)
        {
            throw new ArgumentNullException(nameof(documents));
        }

        var manifests = new List<SkillManifest>();
        var diagnostics = new List<SkillDiagnostic>();
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var documentCount = 0;
        var aggregateUtf8Bytes = 0L;
        using var enumerator = documents.GetEnumerator();
        while (enumerator.MoveNext())
        {
            documentCount++;
            if (documentCount > _options.MaxDocuments)
            {
                throw Limit(
                    "skill_import_document_count_exceeded",
                    "The skill import contains too many documents.");
            }

            var document = enumerator.Current;
            if (document is null)
            {
                AddDiagnostic(
                    diagnostics,
                    "<unknown>",
                    SkillDiagnosticSeverities.Error,
                    "skill_document_null",
                    "A skill document is null.");
                continue;
            }

            AddDocumentBytes(document, ref aggregateUtf8Bytes);
            if (!sourceIds.Add(document.SourceId))
            {
                AddDiagnostic(
                    diagnostics,
                    document.SourceId,
                    SkillDiagnosticSeverities.Error,
                    "skill_source_duplicate",
                    "The skill source id appears more than once.");
                continue;
            }

            SkillManifest manifest;
            try
            {
                manifest = ProtocolJson.DeserializeSkillManifest(
                    document.Json);
                _ = new SkillCatalogRegistry().Replace(new[] { manifest });
            }
            catch (JsonException)
            {
                AddDiagnostic(
                    diagnostics,
                    document.SourceId,
                    SkillDiagnosticSeverities.Error,
                    "skill_json_invalid",
                    "The skill manifest is not valid closed-world JSON.");
                continue;
            }
            catch (Exception exception)
                when (exception is ArgumentException
                      or InvalidDataException
                      or OverflowException)
            {
                AddDiagnostic(
                    diagnostics,
                    document.SourceId,
                    SkillDiagnosticSeverities.Error,
                    "skill_contract_invalid",
                    "The skill manifest does not satisfy the runtime contract.");
                continue;
            }

            var reference = manifest.SkillId + "@" + manifest.Version;
            if (references.TryGetValue(reference, out var firstSource))
            {
                AddDiagnostic(
                    diagnostics,
                    document.SourceId,
                    SkillDiagnosticSeverities.Error,
                    "skill_reference_duplicate",
                    $"The skill reference is already defined by '{firstSource}'.");
                continue;
            }

            if (manifests.Count >= _options.MaxRetainedManifests)
            {
                throw Limit(
                    "skill_import_manifest_count_exceeded",
                    "The skill import contains too many valid manifests.");
            }

            references.Add(reference, document.SourceId);
            manifests.Add(Clone(manifest));
            if (string.Equals(
                    manifest.Trust,
                    "untrusted",
                    StringComparison.Ordinal))
            {
                AddDiagnostic(
                    diagnostics,
                    document.SourceId,
                    SkillDiagnosticSeverities.Warning,
                    "skill_untrusted_requires_policy",
                    "The skill requires an explicit admission policy before activation.");
            }
        }

        manifests.Sort(
            (left, right) =>
            {
                var id = StringComparer.Ordinal.Compare(
                    left.SkillId,
                    right.SkillId);
                return id != 0
                    ? id
                    : StringComparer.Ordinal.Compare(
                        left.Version,
                        right.Version);
            });
        diagnostics.Sort(
            (left, right) =>
            {
                var source = StringComparer.Ordinal.Compare(
                    left.SourceId,
                    right.SourceId);
                return source != 0
                    ? source
                    : StringComparer.Ordinal.Compare(left.Code, right.Code);
            });
        return new SkillImportResult(
            new ReadOnlyCollection<SkillManifest>(manifests),
            new ReadOnlyCollection<SkillDiagnostic>(diagnostics));
    }

    private void AddDocumentBytes(
        SkillManifestDocument document,
        ref long aggregateUtf8Bytes)
    {
        var remaining =
            _options.MaxAggregateUtf8Bytes - aggregateUtf8Bytes;
        if (remaining <= 0 || document.SourceId.Length > remaining)
        {
            throw Limit(
                "skill_import_bytes_exceeded",
                "The skill import exceeds its aggregate UTF-8 byte limit.");
        }

        var sourceBytes = Encoding.UTF8.GetByteCount(document.SourceId);
        if (sourceBytes > remaining
            || document.JsonUtf8Bytes > remaining - sourceBytes)
        {
            throw Limit(
                "skill_import_bytes_exceeded",
                "The skill import exceeds its aggregate UTF-8 byte limit.");
        }

        aggregateUtf8Bytes += sourceBytes + document.JsonUtf8Bytes;
    }

    private void AddDiagnostic(
        ICollection<SkillDiagnostic> diagnostics,
        string sourceId,
        string severity,
        string code,
        string message)
    {
        if (diagnostics.Count >= _options.MaxRetainedDiagnostics)
        {
            throw Limit(
                "skill_import_diagnostic_count_exceeded",
                "The skill import contains too many diagnostics.");
        }

        diagnostics.Add(
            new SkillDiagnostic(
                sourceId,
                severity,
                code,
                message));
    }

    private static SkillManifest Clone(SkillManifest manifest)
    {
        return ProtocolJson.DeserializeSkillManifest(
            ProtocolJson.Serialize(manifest));
    }

    private static RuntimeContentLimitException Limit(
        string code,
        string message)
    {
        return new RuntimeContentLimitException(
            "documents",
            code,
            message);
    }
}
