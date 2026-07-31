using System.Buffers;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GodotArray = global::Godot.Collections.Array;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

internal static class GodotMultiActorVariantMapper
{
    private const int MaximumBatchInputUtf8Bytes = 16 * 1_048_576;
    private const int MaximumParticipantInputUtf8Bytes = 8_192;
    private const int MaximumCoordinateParents = 64;
    private const int MaximumBatchOutputUtf8Bytes = 48 * 1_048_576;
    private const int MaximumManifestOutputUtf8Bytes = 8 * 1_048_576;
    private const int MaximumParticipantOutputUtf8Bytes = 1_048_576;
    private const int MaximumFinalOutputUtf8Bytes = 32_768;

    internal static MultiActorDecisionBatch ToDecisionBatch(
        GodotDictionary input,
        int maximumBatchSize)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (maximumBatchSize is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));
        }

        using var document = ParseDictionary(
            input,
            MaximumBatchInputUtf8Bytes,
            "batch");
        var root = document.RootElement;
        RejectUnknown(
            root,
            "batch_id",
            "coordinate",
            "runs",
            "aggregate_budget");
        var batchId = ReadRequiredString(
            root,
            "batch_id",
            128,
            "batch.batch_id");
        EnsureIdentifier(batchId, "batch.batch_id");
        var coordinate = ReadCoordinate(
            ReadRequiredObject(root, "coordinate", "batch.coordinate"));
        var aggregateBudget = root.TryGetProperty(
                "aggregate_budget",
                out var aggregateBudgetValue)
            ? ReadAggregateBudget(
                ReadObject(
                    aggregateBudgetValue,
                    "batch.aggregate_budget"))
            : null;
        if (!root.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("batch.runs must be an Array.");
        }

        if (runs.GetArrayLength() is < 1)
        {
            throw new JsonException("batch.runs must not be empty.");
        }

        if (runs.GetArrayLength() > maximumBatchSize)
        {
            throw new JsonException(
                $"batch.runs cannot exceed {maximumBatchSize} items.");
        }

        var requests = new List<DurableRunRequest>(runs.GetArrayLength());
        var index = 0;
        foreach (var item in runs.EnumerateArray())
        {
            var path = $"batch.runs[{index}]";
            EnsureObject(item, path);
            RejectUnknown(item, "run", "observations", "options");
            var run = ReadRequiredObject(item, "run", $"{path}.run");
            if (!item.TryGetProperty("observations", out var observations)
                || observations.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    $"{path}.observations must be an Array.");
            }

            JsonElement? options = item.TryGetProperty(
                "options",
                out var optionsValue)
                ? ReadObject(optionsValue, $"{path}.options")
                : null;
            using var runDictionary =
                GodotProtocolVariantMapper.ParseDictionary(
                    run.GetRawText());
            using var observationsVariant =
                GodotProtocolVariantMapper.ParseVariant(
                    observations.GetRawText());
            using var observationsArray =
                observationsVariant.AsGodotArray();
            using var optionsDictionary = options is null
                ? new GodotDictionary()
                : GodotProtocolVariantMapper.ParseDictionary(
                    options.Value.GetRawText());
            requests.Add(
                GodotProtocolVariantMapper.ToDurableRunRequest(
                    runDictionary,
                    observationsArray,
                    optionsDictionary));
            index++;
        }

        return new MultiActorDecisionBatch(
            batchId,
            coordinate,
            requests,
            aggregateBudget);
    }

    internal static MultiActorBatchParticipant ToParticipant(
        GodotDictionary input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        using var document = ParseDictionary(
            input,
            MaximumParticipantInputUtf8Bytes,
            "participant");
        var root = document.RootElement;
        RejectUnknown(
            root,
            "input_index",
            "agent_id",
            "run_id",
            "decision_key");
        return new MultiActorBatchParticipant(
            ReadRequiredInt32(
                root,
                "input_index",
                minimum: 0,
                maximum: 16_383,
                "participant.input_index"),
            ReadRequiredIdentifier(
                root,
                "agent_id",
                "participant.agent_id"),
            ReadRequiredIdentifier(
                root,
                "run_id",
                "participant.run_id"),
            ReadRequiredString(
                root,
                "decision_key",
                1_024,
                "participant.decision_key"));
    }

    internal static string ValidateReasonCode(string reasonCode)
    {
        ValidateRequiredUtf8(reasonCode, 128, "reason_code");
        EnsureIdentifier(reasonCode, "reason_code");
        return reasonCode;
    }

    internal static string SerializeBatchOutcome(
        MultiActorBatchOutcome outcome)
    {
        if (outcome is null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        return WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("manifest");
                WriteManifest(writer, outcome.Manifest);
                writer.WritePropertyName("results");
                writer.WriteStartArray();
                foreach (var result in outcome.Results)
                {
                    WriteResult(writer, result);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            MaximumBatchOutputUtf8Bytes);
    }

    internal static string SerializeManifest(
        MultiActorBatchManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        return WriteJson(
            writer => WriteManifest(writer, manifest),
            MaximumManifestOutputUtf8Bytes);
    }

    internal static string SerializeResult(MultiActorRunResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        return WriteJson(
            writer => WriteResult(writer, result),
            MaximumParticipantOutputUtf8Bytes);
    }

    private static GameContextCoordinate ReadCoordinate(JsonElement value)
    {
        RejectUnknown(
            value,
            "world_id",
            "timeline_id",
            "save_revision",
            "session_id",
            "observer",
            "scene_id",
            "region_id",
            "state_version",
            "game_time",
            "causality");
        var observer = value.TryGetProperty("observer", out var observerValue)
            ? ReadEntity(
                ReadObject(observerValue, "batch.coordinate.observer"),
                "batch.coordinate.observer")
            : null;
        var gameTime = value.TryGetProperty("game_time", out var timeValue)
            ? ReadGameTime(
                ReadObject(timeValue, "batch.coordinate.game_time"),
                "batch.coordinate.game_time")
            : null;
        var causality = value.TryGetProperty(
                "causality",
                out var causalityValue)
            ? ReadCausality(
                ReadObject(
                    causalityValue,
                    "batch.coordinate.causality"),
                "batch.coordinate.causality")
            : null;
        return new GameContextCoordinate(
            ReadRequiredString(
                value,
                "world_id",
                128,
                "batch.coordinate.world_id"),
            ReadRequiredString(
                value,
                "timeline_id",
                128,
                "batch.coordinate.timeline_id"),
            ReadRequiredInt64(
                value,
                "save_revision",
                minimum: 0,
                maximum: long.MaxValue,
                "batch.coordinate.save_revision"),
            observer,
            ReadOptionalString(
                value,
                "scene_id",
                256,
                "batch.coordinate.scene_id"),
            ReadOptionalString(
                value,
                "region_id",
                256,
                "batch.coordinate.region_id"),
            ReadOptionalString(
                value,
                "state_version",
                128,
                "batch.coordinate.state_version"),
            gameTime,
            causality,
            ReadOptionalString(
                value,
                "session_id",
                128,
                "batch.coordinate.session_id"));
    }

    private static MultiActorBatchBudget ReadAggregateBudget(
        JsonElement value)
    {
        RejectUnknown(
            value,
            "max_tokens",
            "max_actions",
            "max_duration_ms",
            "max_cost_usd");
        return new MultiActorBatchBudget(
            ReadRequiredInt64(
                value,
                "max_tokens",
                minimum: 1,
                maximum: long.MaxValue,
                "batch.aggregate_budget.max_tokens"),
            ReadRequiredInt64(
                value,
                "max_actions",
                minimum: 0,
                maximum: long.MaxValue,
                "batch.aggregate_budget.max_actions"),
            ReadRequiredInt64(
                value,
                "max_duration_ms",
                minimum: 1,
                maximum: long.MaxValue,
                "batch.aggregate_budget.max_duration_ms"),
            ReadRequiredString(
                value,
                "max_cost_usd",
                64,
                "batch.aggregate_budget.max_cost_usd"));
    }

    private static GameEntityIdentity ReadEntity(
        JsonElement value,
        string path)
    {
        RejectUnknown(value, "entity_id", "incarnation");
        return new GameEntityIdentity(
            ReadRequiredString(
                value,
                "entity_id",
                128,
                $"{path}.entity_id"),
            ReadRequiredInt64(
                value,
                "incarnation",
                minimum: 0,
                maximum: long.MaxValue,
                $"{path}.incarnation"));
    }

    private static GameTimePoint ReadGameTime(
        JsonElement value,
        string path)
    {
        RejectUnknown(value, "clock_id", "timeline_id", "epoch", "tick");
        return new GameTimePoint(
            ReadRequiredString(
                value,
                "clock_id",
                128,
                $"{path}.clock_id"),
            ReadRequiredString(
                value,
                "timeline_id",
                128,
                $"{path}.timeline_id"),
            ReadRequiredInt64(
                value,
                "epoch",
                minimum: 0,
                maximum: long.MaxValue,
                $"{path}.epoch"),
            ReadRequiredInt64(
                value,
                "tick",
                minimum: long.MinValue,
                maximum: long.MaxValue,
                $"{path}.tick"));
    }

    private static GameCausalityStamp ReadCausality(
        JsonElement value,
        string path)
    {
        RejectUnknown(
            value,
            "event_id",
            "based_on_state_version",
            "parent_event_ids");
        var parents = new List<string>();
        if (value.TryGetProperty("parent_event_ids", out var parentValue))
        {
            if (parentValue.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    $"{path}.parent_event_ids must be an Array.");
            }

            if (parentValue.GetArrayLength() > MaximumCoordinateParents)
            {
                throw new JsonException(
                    $"{path}.parent_event_ids cannot exceed "
                    + $"{MaximumCoordinateParents} items.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var parent in parentValue.EnumerateArray())
            {
                if (parent.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException(
                        $"{path}.parent_event_ids[{index}] must be a String.");
                }

                var id = parent.GetString();
                ValidateRequiredUtf8(
                    id,
                    128,
                    $"{path}.parent_event_ids[{index}]");
                if (!seen.Add(id!))
                {
                    throw new JsonException(
                        $"{path}.parent_event_ids[{index}] is duplicated.");
                }

                parents.Add(id!);
                index++;
            }
        }

        return new GameCausalityStamp(
            ReadRequiredString(
                value,
                "event_id",
                128,
                $"{path}.event_id"),
            ReadRequiredString(
                value,
                "based_on_state_version",
                128,
                $"{path}.based_on_state_version"),
            parents);
    }

    private static JsonDocument ParseDictionary(
        GodotDictionary value,
        int maximumUtf8Bytes,
        string path)
    {
        var normalized =
            GodotVariantInputGuard.StringifyAndNormalizeDictionary(
                value,
                path,
                maximumUtf8Bytes);

        var document = JsonDocument.Parse(normalized);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException($"{path} must be a Dictionary.");
        }

        return document;
    }

    private static JsonElement ReadRequiredObject(
        JsonElement value,
        string propertyName,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            throw new JsonException($"{path} is required.");
        }

        return ReadObject(property, path);
    }

    private static JsonElement ReadObject(JsonElement value, string path)
    {
        EnsureObject(value, path);
        return value;
    }

    private static string ReadRequiredIdentifier(
        JsonElement value,
        string propertyName,
        string path)
    {
        var result = ReadRequiredString(value, propertyName, 128, path);
        EnsureIdentifier(result, path);
        return result;
    }

    private static string ReadRequiredString(
        JsonElement value,
        string propertyName,
        int maximumUtf8Bytes,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{path} must be a String.");
        }

        var result = property.GetString();
        ValidateRequiredUtf8(result, maximumUtf8Bytes, path);
        return result!;
    }

    private static string? ReadOptionalString(
        JsonElement value,
        string propertyName,
        int maximumUtf8Bytes,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{path} must be a String.");
        }

        var result = property.GetString();
        ValidateRequiredUtf8(result, maximumUtf8Bytes, path);
        return result;
    }

    private static int ReadRequiredInt32(
        JsonElement value,
        string propertyName,
        int minimum,
        int maximum,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var result)
            || result < minimum
            || result > maximum)
        {
            throw new JsonException(
                $"{path} must be an integer from {minimum} through {maximum}.");
        }

        return result;
    }

    private static long ReadRequiredInt64(
        JsonElement value,
        string propertyName,
        long minimum,
        long maximum,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var result)
            || result < minimum
            || result > maximum)
        {
            throw new JsonException(
                $"{path} must be an integer from {minimum} through {maximum}.");
        }

        return result;
    }

    private static void ValidateRequiredUtf8(
        string? value,
        int maximumUtf8Bytes,
        string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"{path} must not be empty.");
        }

        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new JsonException(
                $"{path} exceeds {maximumUtf8Bytes} UTF-8 bytes.");
        }
    }

    private static void EnsureIdentifier(string value, string path)
    {
        foreach (var character in value)
        {
            var allowed = character is >= 'A' and <= 'Z'
                          || character is >= 'a' and <= 'z'
                          || character is >= '0' and <= '9'
                          || character is '.' or '_' or ':' or '-';
            if (!allowed)
            {
                throw new JsonException($"{path} is not a runtime identifier.");
            }
        }
    }

    private static void EnsureObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{path} must be a Dictionary.");
        }
    }

    private static void RejectUnknown(
        JsonElement value,
        params string[] allowed)
    {
        EnsureObject(value, "input");
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException(
                    $"Unknown input field '{property.Name}'.");
            }
        }
    }

    private static string WriteJson(
        Action<Utf8JsonWriter> write,
        int maximumUtf8Bytes)
    {
        var buffer = new BoundedByteBufferWriter(maximumUtf8Bytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteManifest(
        Utf8JsonWriter writer,
        MultiActorBatchManifest manifest)
    {
        writer.WriteStartObject();
        writer.WriteString("batch_id", manifest.BatchId);
        writer.WritePropertyName("coordinate");
        WriteCoordinate(writer, manifest.Coordinate);
        writer.WritePropertyName("participants");
        writer.WriteStartArray();
        foreach (var participant in manifest.Participants)
        {
            WriteParticipant(writer, participant);
        }

        writer.WriteEndArray();
        if (manifest.BudgetReservation is not null)
        {
            var reservation = manifest.BudgetReservation;
            writer.WritePropertyName("budget_reservation");
            writer.WriteStartObject();
            writer.WriteNumber(
                "reserved_tokens",
                reservation.ReservedTokens);
            writer.WriteNumber(
                "reserved_actions",
                reservation.ReservedActions);
            writer.WriteNumber(
                "reserved_duration_ms",
                reservation.ReservedDurationMs);
            writer.WriteString(
                "reserved_cost_usd",
                reservation.ReservedCostUsd);
            writer.WritePropertyName("limit");
            writer.WriteStartObject();
            writer.WriteNumber(
                "max_tokens",
                reservation.Limit.MaxTokens);
            writer.WriteNumber(
                "max_actions",
                reservation.Limit.MaxActions);
            writer.WriteNumber(
                "max_duration_ms",
                reservation.Limit.MaxDurationMs);
            writer.WriteString(
                "max_cost_usd",
                reservation.Limit.MaxCostUsd);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteParticipant(
        Utf8JsonWriter writer,
        MultiActorBatchParticipant participant)
    {
        writer.WriteStartObject();
        writer.WriteNumber("input_index", participant.InputIndex);
        writer.WriteString("agent_id", participant.AgentId);
        writer.WriteString("run_id", participant.RunId);
        writer.WriteString("decision_key", participant.DecisionKey);
        writer.WriteEndObject();
    }

    private static void WriteResult(
        Utf8JsonWriter writer,
        MultiActorRunResult result)
    {
        writer.WriteStartObject();
        writer.WriteNumber("input_index", result.InputIndex);
        writer.WriteString("agent_id", result.AgentId);
        writer.WriteString("decision_key", result.DecisionKey);
        writer.WriteBoolean("succeeded", result.Succeeded);
        writer.WritePropertyName("outcome");
        if (result.Outcome is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WritePropertyName("run");
            WriteRunSummary(writer, result.Outcome.Run);
            writer.WritePropertyName("final_output");
            var finalOutput = result.Outcome.FinalOutput;
            var finalOutputOmitted = finalOutput.HasValue
                                     && Encoding.UTF8.GetByteCount(
                                         finalOutput.Value.GetRawText())
                                     > MaximumFinalOutputUtf8Bytes;
            if (finalOutput.HasValue && !finalOutputOmitted)
            {
                finalOutput.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteBoolean(
                "final_output_omitted",
                finalOutputOmitted);
            writer.WriteString("error_code", result.Outcome.ErrorCode);
            writer.WriteString("error_category", result.Outcome.ErrorCategory);
            writer.WriteString(
                "safe_error_message",
                result.Outcome.SafeErrorMessage);
            writer.WriteBoolean(
                "reconciliation_required",
                result.Outcome.ReconciliationRequired);
            writer.WriteBoolean("terminal", result.Outcome.IsTerminal);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("error");
        WriteError(writer, result.Error);
        writer.WriteEndObject();
    }

    private static void WriteRunSummary(
        Utf8JsonWriter writer,
        AgentRun run)
    {
        writer.WriteStartObject();
        writer.WriteString("runId", run.RunId);
        writer.WriteString("agentId", run.AgentId);
        writer.WriteString("worldId", run.WorldId);
        writer.WriteString("sessionId", run.SessionId);
        writer.WriteString("decisionKey", run.DecisionKey);
        writer.WriteString("batchId", run.BatchId);
        writer.WriteString("state", run.State);
        writer.WriteNumber("revision", run.Revision);
        writer.WriteNumber("runtimeGeneration", run.RuntimeGeneration);
        writer.WritePropertyName("pendingOperationIds");
        writer.WriteStartArray();
        foreach (var operationId in run.PendingOperationIds)
        {
            writer.WriteStringValue(operationId);
        }

        writer.WriteEndArray();
        writer.WriteString("terminalReason", run.TerminalReason);
        writer.WriteString("completionIntent", run.CompletionIntent);
        writer.WriteString("updatedAt", run.UpdatedAt);
        writer.WriteEndObject();
    }

    private static void WriteError(Utf8JsonWriter writer, Exception? error)
    {
        if (error is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        if (error is MultiActorParticipantAbandonedException abandoned)
        {
            writer.WriteString("code", "participant_abandoned");
            writer.WriteString("reason_code", abandoned.ReasonCode);
            writer.WriteString(
                "message",
                "The participant was durably abandoned by the game host.");
        }
        else
        {
            writer.WriteString("code", "participant_failed");
            writer.WriteString(
                "message",
                "The participant did not complete successfully.");
        }

        writer.WriteEndObject();
    }

    private static void WriteCoordinate(
        Utf8JsonWriter writer,
        GameContextCoordinate coordinate)
    {
        writer.WriteStartObject();
        writer.WriteString("world_id", coordinate.WorldId);
        writer.WriteString("timeline_id", coordinate.TimelineId);
        writer.WriteNumber("save_revision", coordinate.SaveRevision);
        writer.WriteString("session_id", coordinate.SessionId);
        writer.WriteString("scene_id", coordinate.SceneId);
        writer.WriteString("region_id", coordinate.RegionId);
        writer.WriteString("state_version", coordinate.StateVersion);
        if (coordinate.Observer is not null)
        {
            writer.WritePropertyName("observer");
            WriteEntity(writer, coordinate.Observer);
        }

        if (coordinate.GameTime is not null)
        {
            writer.WritePropertyName("game_time");
            WriteGameTime(writer, coordinate.GameTime);
        }

        if (coordinate.Causality is not null)
        {
            writer.WritePropertyName("causality");
            WriteCausality(writer, coordinate.Causality);
        }

        writer.WriteEndObject();
    }

    private static void WriteEntity(
        Utf8JsonWriter writer,
        GameEntityIdentity entity)
    {
        writer.WriteStartObject();
        writer.WriteString("entity_id", entity.EntityId);
        writer.WriteNumber("incarnation", entity.Incarnation);
        writer.WriteEndObject();
    }

    private static void WriteGameTime(
        Utf8JsonWriter writer,
        GameTimePoint gameTime)
    {
        writer.WriteStartObject();
        writer.WriteString("clock_id", gameTime.ClockId);
        writer.WriteString("timeline_id", gameTime.TimelineId);
        writer.WriteNumber("epoch", gameTime.Epoch);
        writer.WriteNumber("tick", gameTime.Tick);
        writer.WriteEndObject();
    }

    private static void WriteCausality(
        Utf8JsonWriter writer,
        GameCausalityStamp causality)
    {
        writer.WriteStartObject();
        writer.WriteString("event_id", causality.EventId);
        writer.WriteString(
            "based_on_state_version",
            causality.BasedOnStateVersion);
        writer.WritePropertyName("parent_event_ids");
        writer.WriteStartArray();
        foreach (var parent in causality.ParentEventIds)
        {
            writer.WriteStringValue(parent);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed class BoundedByteBufferWriter : IBufferWriter<byte>
    {
        private readonly int _maximumBytes;
        private byte[] _buffer;
        private int _written;

        internal BoundedByteBufferWriter(int maximumBytes)
        {
            if (maximumBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            _maximumBytes = maximumBytes;
            _buffer = new byte[Math.Min(4_096, maximumBytes)];
        }

        internal ReadOnlySpan<byte> WrittenSpan =>
            _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || _written > _maximumBytes - count)
            {
                throw new InvalidOperationException(
                    "The Godot multi-actor output exceeded its byte limit.");
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            sizeHint = Math.Max(1, sizeHint);
            if (_written > _maximumBytes - sizeHint)
            {
                throw new InvalidOperationException(
                    "The Godot multi-actor output exceeded its byte limit.");
            }

            var required = _written + sizeHint;
            if (required <= _buffer.Length)
            {
                return;
            }

            var next = Math.Min(
                _maximumBytes,
                Math.Max(required, checked(_buffer.Length * 2)));
            Array.Resize(ref _buffer, next);
        }
    }
}
