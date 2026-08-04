using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameAgent.Workflow;

internal static class WorkflowRunSnapshotCodec
{
    private const string SchemaName = "gameagent.workflow.run-snapshot";
    private const int SchemaVersion = 1;

    private static readonly HashSet<string> RootFields =
        Fields(
            "schema",
            "version",
            "runId",
            "workflowId",
            "workflowVersion",
            "definitionDigest",
            "input",
            "inputDigest",
            "revision",
            "status",
            "reasonCode",
            "cancellationRequested",
            "cancellationReason",
            "hasOutput",
            "output",
            "outputDigest",
            "createdAt",
            "updatedAt",
            "fencingEpoch",
            "lease",
            "usage",
            "stages");

    private static readonly HashSet<string> UsageFields =
        Fields(
            "stageExecutions",
            "executeCalls",
            "recoveryCalls",
            "foreachItems",
            "loopIterations",
            "retainedOutputBytes");

    private static readonly HashSet<string> LeaseFields =
        Fields("ownerId", "fencingEpoch", "expiresAt");

    private static readonly HashSet<string> StageFields =
        Fields(
            "instanceId",
            "stageId",
            "instanceKind",
            "parentInstanceId",
            "itemIdentityDigest",
            "itemOrdinal",
            "loopIteration",
            "status",
            "attempt",
            "generation",
            "recoveryAttempts",
            "cursor",
            "hasInput",
            "input",
            "inputDigest",
            "hasOutput",
            "output",
            "outputDigest",
            "hasCheckpoint",
            "checkpoint",
            "checkpointDigest",
            "reasonCode",
            "updatedAt");

    public static byte[] Encode(
        WorkflowRunSnapshot snapshot,
        int maximumBytes)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var buffer = new WorkflowSnapshotBufferWriter(maximumBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SchemaName);
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("runId", snapshot.RunId);
            writer.WriteString("workflowId", snapshot.WorkflowId);
            writer.WriteString(
                "workflowVersion",
                snapshot.WorkflowVersion);
            writer.WriteString(
                "definitionDigest",
                snapshot.DefinitionDigest);
            writer.WritePropertyName("input");
            WriteJson(writer, snapshot.Input);
            writer.WriteString("inputDigest", snapshot.InputDigest);
            writer.WriteNumber("revision", snapshot.Revision);
            writer.WriteNumber("status", (int)snapshot.Status);
            WriteOptionalString(writer, "reasonCode", snapshot.ReasonCode);
            writer.WriteBoolean(
                "cancellationRequested",
                snapshot.CancellationRequested);
            WriteOptionalString(
                writer,
                "cancellationReason",
                snapshot.CancellationReason);
            writer.WriteBoolean("hasOutput", snapshot.Output.HasValue);
            writer.WritePropertyName("output");
            WriteOptionalJson(writer, snapshot.Output);
            WriteOptionalString(
                writer,
                "outputDigest",
                snapshot.OutputDigest);
            writer.WriteString(
                "createdAt",
                FormatTimestamp(snapshot.CreatedAt));
            writer.WriteString(
                "updatedAt",
                FormatTimestamp(snapshot.UpdatedAt));
            writer.WriteNumber(
                "fencingEpoch",
                snapshot.FencingEpoch);
            writer.WritePropertyName("lease");
            WriteLease(writer, snapshot.Lease);
            writer.WritePropertyName("usage");
            WriteUsage(writer, snapshot.Usage);
            writer.WritePropertyName("stages");
            writer.WriteStartArray();
            foreach (var stage in snapshot.StageInstances)
            {
                WriteStage(writer, stage);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return buffer.ToArray();
    }

    public static WorkflowRunSnapshot Decode(
        ReadOnlyMemory<byte> payload,
        int maxStageInstances)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128
                });
        }
        catch (JsonException exception)
        {
            throw InvalidSnapshot(
                "The committed workflow snapshot is not valid JSON.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            RequireObject(root, RootFields, "snapshot");
            if (!string.Equals(
                    RequiredString(root, "schema", 128),
                    SchemaName,
                    StringComparison.Ordinal)
                || RequiredInt32(root, "version") != SchemaVersion)
            {
                throw new WorkflowFileStoreCorruptionException(
                    WorkflowFileStoreReasonCodes.UnsupportedVersion,
                    "The workflow snapshot schema or version is unsupported.");
            }

            var hasOutput = RequiredBoolean(root, "hasOutput");
            var output = OptionalJson(root, "output", hasOutput);
            var outputDigest = OptionalString(
                root,
                "outputDigest",
                64);
            if (hasOutput != (outputDigest is not null))
            {
                throw InvalidSnapshot(
                    "The workflow output presence marker is inconsistent.");
            }

            var lease = ReadLease(root.GetProperty("lease"));
            var usage = ReadUsage(root.GetProperty("usage"));
            var stages = ReadStages(
                root.GetProperty("stages"),
                maxStageInstances);
            try
            {
                return WorkflowRunSnapshot.Restore(
                    RequiredString(root, "runId", 80),
                    RequiredString(root, "workflowId", 128),
                    RequiredString(root, "workflowVersion", 64),
                    RequiredString(root, "definitionDigest", 64),
                    RequiredJson(root, "input"),
                    RequiredString(root, "inputDigest", 64),
                    RequiredInt64(root, "revision"),
                    ReadRunStatus(root),
                    OptionalString(root, "reasonCode", 128),
                    RequiredBoolean(root, "cancellationRequested"),
                    OptionalString(root, "cancellationReason", 128),
                    output,
                    outputDigest,
                    RequiredTimestamp(root, "createdAt"),
                    RequiredTimestamp(root, "updatedAt"),
                    RequiredInt64(root, "fencingEpoch"),
                    lease,
                    usage,
                    stages);
            }
            catch (ArgumentException exception)
            {
                throw InvalidSnapshot(
                    "The committed workflow snapshot violates its invariants.",
                    exception);
            }
        }
    }

    private static void WriteLease(
        Utf8JsonWriter writer,
        WorkflowLeaseSnapshot? lease)
    {
        if (lease is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("ownerId", lease.OwnerId);
        writer.WriteNumber("fencingEpoch", lease.FencingEpoch);
        writer.WriteString("expiresAt", FormatTimestamp(lease.ExpiresAt));
        writer.WriteEndObject();
    }

    private static WorkflowLeaseSnapshot? ReadLease(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireObject(value, LeaseFields, "lease");
        try
        {
            return new WorkflowLeaseSnapshot(
                RequiredString(value, "ownerId", 128),
                RequiredInt64(value, "fencingEpoch"),
                RequiredTimestamp(value, "expiresAt"));
        }
        catch (ArgumentException exception)
        {
            throw InvalidSnapshot(
                "The committed workflow lease is invalid.",
                exception);
        }
    }

    private static void WriteUsage(
        Utf8JsonWriter writer,
        WorkflowUsage usage)
    {
        writer.WriteStartObject();
        writer.WriteNumber(
            "stageExecutions",
            usage.StageExecutions);
        writer.WriteNumber("executeCalls", usage.ExecuteCalls);
        writer.WriteNumber("recoveryCalls", usage.RecoveryCalls);
        writer.WriteNumber("foreachItems", usage.ForeachItems);
        writer.WriteNumber("loopIterations", usage.LoopIterations);
        writer.WriteNumber(
            "retainedOutputBytes",
            usage.RetainedOutputBytes);
        writer.WriteEndObject();
    }

    private static WorkflowUsage ReadUsage(JsonElement value)
    {
        RequireObject(value, UsageFields, "usage");
        try
        {
            return new WorkflowUsage(
                RequiredInt32(value, "stageExecutions"),
                RequiredInt32(value, "executeCalls"),
                RequiredInt32(value, "recoveryCalls"),
                RequiredInt32(value, "foreachItems"),
                RequiredInt32(value, "loopIterations"),
                RequiredInt32(value, "retainedOutputBytes"));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw InvalidSnapshot(
                "The committed workflow usage is invalid.",
                exception);
        }
    }

    private static void WriteStage(
        Utf8JsonWriter writer,
        WorkflowStageInstanceSnapshot stage)
    {
        writer.WriteStartObject();
        writer.WriteString("instanceId", stage.InstanceId);
        writer.WriteString("stageId", stage.StageId);
        writer.WriteNumber("instanceKind", (int)stage.InstanceKind);
        WriteOptionalString(
            writer,
            "parentInstanceId",
            stage.ParentInstanceId);
        WriteOptionalString(
            writer,
            "itemIdentityDigest",
            stage.ItemIdentityDigest);
        WriteOptionalInt32(writer, "itemOrdinal", stage.ItemOrdinal);
        WriteOptionalInt32(writer, "loopIteration", stage.LoopIteration);
        writer.WriteNumber("status", (int)stage.Status);
        writer.WriteNumber("attempt", stage.Attempt);
        writer.WriteNumber("generation", stage.Generation);
        writer.WriteNumber("recoveryAttempts", stage.RecoveryAttempts);
        writer.WriteNumber("cursor", stage.Cursor);
        writer.WriteBoolean("hasInput", stage.Input.HasValue);
        writer.WritePropertyName("input");
        WriteOptionalJson(writer, stage.Input);
        WriteOptionalString(writer, "inputDigest", stage.InputDigest);
        writer.WriteBoolean("hasOutput", stage.Output.HasValue);
        writer.WritePropertyName("output");
        WriteOptionalJson(writer, stage.Output);
        WriteOptionalString(writer, "outputDigest", stage.OutputDigest);
        writer.WriteBoolean("hasCheckpoint", stage.Checkpoint.HasValue);
        writer.WritePropertyName("checkpoint");
        WriteOptionalJson(writer, stage.Checkpoint);
        WriteOptionalString(
            writer,
            "checkpointDigest",
            stage.CheckpointDigest);
        WriteOptionalString(writer, "reasonCode", stage.ReasonCode);
        writer.WriteString("updatedAt", FormatTimestamp(stage.UpdatedAt));
        writer.WriteEndObject();
    }

    private static IReadOnlyList<WorkflowStageInstanceSnapshot> ReadStages(
        JsonElement value,
        int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidSnapshot("Workflow stages must be an array.");
        }

        var stages = new List<WorkflowStageInstanceSnapshot>();
        foreach (var item in value.EnumerateArray())
        {
            if (stages.Count >= maximum)
            {
                throw InvalidSnapshot(
                    "The committed workflow stage count exceeds its limit.");
            }

            RequireObject(item, StageFields, "stage");
            var hasInput = RequiredBoolean(item, "hasInput");
            var hasOutput = RequiredBoolean(item, "hasOutput");
            var hasCheckpoint = RequiredBoolean(item, "hasCheckpoint");
            var input = OptionalJson(item, "input", hasInput);
            var output = OptionalJson(item, "output", hasOutput);
            var checkpoint = OptionalJson(
                item,
                "checkpoint",
                hasCheckpoint);
            try
            {
                stages.Add(WorkflowStageInstanceSnapshot.Restore(
                    RequiredString(item, "instanceId", 80),
                    RequiredString(item, "stageId", 128),
                    ReadInstanceKind(item),
                    OptionalString(item, "parentInstanceId", 80),
                    OptionalString(item, "itemIdentityDigest", 64),
                    OptionalInt32(item, "itemOrdinal"),
                    OptionalInt32(item, "loopIteration"),
                    ReadStageStatus(item),
                    RequiredInt32(item, "attempt"),
                    RequiredInt32(item, "generation"),
                    RequiredInt32(item, "recoveryAttempts"),
                    RequiredInt32(item, "cursor"),
                    input,
                    OptionalString(item, "inputDigest", 64),
                    output,
                    OptionalString(item, "outputDigest", 64),
                    checkpoint,
                    OptionalString(item, "checkpointDigest", 64),
                    OptionalString(item, "reasonCode", 128),
                    RequiredTimestamp(item, "updatedAt")));
            }
            catch (ArgumentException exception)
            {
                throw InvalidSnapshot(
                    "A committed workflow stage violates its invariants.",
                    exception);
            }
        }

        return stages;
    }

    private static WorkflowRunStatus ReadRunStatus(JsonElement value)
    {
        var raw = RequiredInt32(value, "status");
        return raw is >= (int)WorkflowRunStatus.Pending
            and <= (int)WorkflowRunStatus.Cancelled
            ? (WorkflowRunStatus)raw
            : throw InvalidSnapshot("Workflow run status is unknown.");
    }

    private static WorkflowStageStatus ReadStageStatus(JsonElement value)
    {
        var raw = RequiredInt32(value, "status");
        return raw is >= (int)WorkflowStageStatus.Pending
            and <= (int)WorkflowStageStatus.Cancelled
            ? (WorkflowStageStatus)raw
            : throw InvalidSnapshot("Workflow stage status is unknown.");
    }

    private static WorkflowInstanceKind ReadInstanceKind(JsonElement value)
    {
        var raw = RequiredInt32(value, "instanceKind");
        return raw is >= (int)WorkflowInstanceKind.Stage
            and <= (int)WorkflowInstanceKind.LoopIteration
            ? (WorkflowInstanceKind)raw
            : throw InvalidSnapshot("Workflow instance kind is unknown.");
    }

    private static void RequireObject(
        JsonElement value,
        ISet<string> allowedFields,
        string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidSnapshot($"{label} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowedFields.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                throw InvalidSnapshot(
                    $"{label} contains unknown or duplicate fields.");
            }
        }

        if (seen.Count != allowedFields.Count)
        {
            throw InvalidSnapshot($"{label} is missing required fields.");
        }
    }

    private static string RequiredString(
        JsonElement value,
        string propertyName,
        int maximumLength)
    {
        var property = value.GetProperty(propertyName);
        var result = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
        if (string.IsNullOrEmpty(result) || result.Length > maximumLength)
        {
            throw InvalidSnapshot(
                $"Snapshot field '{propertyName}' is not a bounded string.");
        }

        return result;
    }

    private static string? OptionalString(
        JsonElement value,
        string propertyName,
        int maximumLength)
    {
        var property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var result = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
        if (string.IsNullOrEmpty(result) || result.Length > maximumLength)
        {
            throw InvalidSnapshot(
                $"Snapshot field '{propertyName}' is not a bounded optional string.");
        }

        return result;
    }

    private static bool RequiredBoolean(
        JsonElement value,
        string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidSnapshot(
                $"Snapshot field '{propertyName}' is not Boolean.")
        };
    }

    private static int RequiredInt32(
        JsonElement value,
        string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out var result)
            ? result
            : throw InvalidSnapshot(
                $"Snapshot field '{propertyName}' is not Int32.");
    }

    private static int? OptionalInt32(
        JsonElement value,
        string propertyName)
    {
        var property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out var result)
            ? result
            : throw InvalidSnapshot(
                $"Snapshot field '{propertyName}' is not optional Int32.");
    }

    private static long RequiredInt64(
        JsonElement value,
        string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out var result)
            ? result
            : throw InvalidSnapshot(
                $"Snapshot field '{propertyName}' is not Int64.");
    }

    private static DateTimeOffset RequiredTimestamp(
        JsonElement value,
        string propertyName)
    {
        var raw = RequiredString(value, propertyName, 64);
        if (!DateTimeOffset.TryParseExact(
                raw,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result))
        {
            throw InvalidSnapshot(
                $"Snapshot field '{propertyName}' is not a timestamp.");
        }

        return result;
    }

    private static JsonElement RequiredJson(
        JsonElement value,
        string propertyName)
    {
        return value.GetProperty(propertyName).Clone();
    }

    private static JsonElement? OptionalJson(
        JsonElement value,
        string propertyName,
        bool isPresent)
    {
        var property = value.GetProperty(propertyName);
        if (!isPresent)
        {
            if (property.ValueKind != JsonValueKind.Null)
            {
                throw InvalidSnapshot(
                    $"Snapshot field '{propertyName}' has an inconsistent presence marker.");
            }

            return null;
        }

        return property.Clone();
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteOptionalInt32(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteOptionalJson(
        Utf8JsonWriter writer,
        JsonElement? value)
    {
        if (value.HasValue)
        {
            WriteJson(writer, value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteJson(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    value.GetRawText(),
                    skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException(
                    "Undefined JSON cannot be persisted.",
                    nameof(value));
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static HashSet<string> Fields(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static WorkflowFileStoreCorruptionException InvalidSnapshot(
        string message,
        Exception? innerException = null)
    {
        return new WorkflowFileStoreCorruptionException(
            WorkflowFileStoreReasonCodes.InvalidSnapshot,
            message,
            innerException);
    }
}

internal sealed class WorkflowSnapshotBufferWriter : IBufferWriter<byte>
{
    private readonly int _maximumBytes;
    private byte[] _buffer;
    private int _written;

    public WorkflowSnapshotBufferWriter(int maximumBytes)
    {
        _maximumBytes = maximumBytes;
        _buffer = new byte[Math.Min(maximumBytes, 4_096)];
    }

    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public byte[] ToArray()
    {
        var result = new byte[_written];
        Buffer.BlockCopy(_buffer, 0, result, 0, _written);
        return result;
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        var requiredAdditionalBytes = Math.Max(sizeHint, 1);
        if (requiredAdditionalBytes
            > _maximumBytes - _written)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow snapshot exceeds its byte limit.");
        }

        var requiredLength =
            checked(_written + requiredAdditionalBytes);
        if (requiredLength <= _buffer.Length)
        {
            return;
        }

        var doubled = _buffer.Length <= _maximumBytes / 2
            ? _buffer.Length * 2
            : _maximumBytes;
        var nextLength = Math.Min(
            _maximumBytes,
            Math.Max(requiredLength, doubled));
        Array.Resize(ref _buffer, nextLength);
    }
}
