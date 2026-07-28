using System.Collections;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeProtocolInputGuardTests
{
    [Fact]
    public void JsonMeasurementMatchesCompactWireEncoding()
    {
        var value = Json(
            """
            {
              "escaped": "\u0001\"\\",
              "unicode": "世界"
            }
            """);
        var expected = Encoding.UTF8.GetByteCount(
            ProtocolJson.Serialize(value));

        var measured = JsonValueInspector.ValidateAndMeasure(
            value,
            Limits(),
            "value");

        Assert.Equal(expected, measured);
    }

    [Fact]
    public void OversizedJsonIsRejectedByTheBoundedWriter()
    {
        var value = Json(
            "\"" + new string('x', 32_000) + "\"");

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => JsonValueInspector.ValidateAndMeasure(
                value,
                new JsonValueLimits(
                    maxUtf8Bytes: 1_024,
                    maxStringUtf8Bytes: 65_536),
                "value"));

        Assert.Equal("json_bytes_exceeded", error.LimitCode);
    }

    [Fact]
    public void AgentRunSnapshotPreservesAllFieldsAndDetachesObjects()
    {
        var extensionDocument = JsonDocument.Parse(
            """{"nested":{"value":"run"}}""");
        var run = FullAgentRun();
        run.Extensions.Add(
            "run-extension",
            extensionDocument.RootElement);
        var expected = ProtocolJson.Serialize(run);

        var snapshot =
            RuntimeProtocolInputGuard.ValidateAgentRunBeforeSerialization(
                run,
                Limits(),
                65_536,
                "run");

        Assert.NotSame(run, snapshot);
        Assert.NotSame(run.Trigger, snapshot.Trigger);
        Assert.NotSame(run.Budget, snapshot.Budget);
        Assert.NotSame(run.Usage, snapshot.Usage);
        Assert.NotSame(
            run.TriggerObservationIds,
            snapshot.TriggerObservationIds);
        Assert.NotSame(
            run.PendingOperationIds,
            snapshot.PendingOperationIds);
        Assert.NotSame(run.Extensions, snapshot.Extensions);

        run.Trigger.Type = "manual";
        run.Budget.MaxTurns = 1;
        run.Usage.Turns = 0;
        run.TriggerObservationIds.Clear();
        run.PendingOperationIds.Clear();
        run.Extensions.Clear();

        Assert.Equal(expected, ProtocolJson.Serialize(snapshot));
        extensionDocument.Dispose();
    }

    [Fact]
    public void ResourceObservationSnapshotPreservesAllFieldsAndDetachesObjects()
    {
        var extensionDocument = JsonDocument.Parse(
            """{"nested":{"value":"observation"}}""");
        var observation = FullResourceObservation();
        observation.Extensions.Add(
            "observation-extension",
            extensionDocument.RootElement);
        var expected = ProtocolJson.Serialize(observation);

        var snapshot =
            RuntimeProtocolInputGuard.ValidateObservationBeforeSerialization(
                observation,
                Limits(),
                65_536,
                "observation");

        Assert.NotSame(observation, snapshot);
        Assert.NotSame(observation.ResourceRef, snapshot.ResourceRef);
        Assert.NotSame(observation.Visibility, snapshot.Visibility);
        Assert.NotSame(
            observation.SubjectIds,
            snapshot.SubjectIds);
        Assert.NotSame(
            observation.Visibility.AudienceIds,
            snapshot.Visibility.AudienceIds);
        Assert.NotSame(observation.Extensions, snapshot.Extensions);

        observation.ResourceRef!.Uri = "changed";
        observation.Visibility.Scope = "changed";
        observation.SubjectIds.Clear();
        observation.Visibility.AudienceIds.Clear();
        observation.Extensions.Clear();

        Assert.Equal(expected, ProtocolJson.Serialize(snapshot));
        extensionDocument.Dispose();
    }

    [Fact]
    public void ObservationPayloadIsPreserved()
    {
        var payloadDocument = JsonDocument.Parse(
            """{"nested":{"value":"payload"}}""");
        var observation = ValidObservation();
        observation.Payload = payloadDocument.RootElement;
        var expected = ProtocolJson.Serialize(observation);

        var snapshot =
            RuntimeProtocolInputGuard.ValidateObservationBeforeSerialization(
                observation,
                Limits(),
                65_536,
                "observation");

        Assert.Equal(expected, ProtocolJson.Serialize(snapshot));
        payloadDocument.Dispose();
    }

    [Fact]
    public void ToolSnapshotPreservesAllFieldsAndDetachesObjects()
    {
        var parameterDocument = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"type":"integer"}}}""");
        var resultDocument = JsonDocument.Parse(
            """{"type":"object","required":["accepted"]}""");
        var extensionDocument = JsonDocument.Parse(
            """{"nested":{"value":"tool"}}""");
        var tool = FullTool(
            parameterDocument.RootElement,
            resultDocument.RootElement);
        tool.Extensions.Add(
            "tool-extension",
            extensionDocument.RootElement);
        var expected = ProtocolJson.Serialize(tool);

        var snapshot =
            RuntimeProtocolInputGuard.ValidateToolBeforeSerialization(
                tool,
                Limits(),
                65_536,
                "tool");

        Assert.NotSame(tool, snapshot);
        Assert.NotSame(tool.ConflictScopes, snapshot.ConflictScopes);
        Assert.NotSame(tool.Extensions, snapshot.Extensions);

        tool.ConflictScopes.Clear();
        tool.Extensions.Clear();

        Assert.Equal(expected, ProtocolJson.Serialize(snapshot));
        parameterDocument.Dispose();
        resultDocument.Dispose();
        extensionDocument.Dispose();
    }

    [Fact]
    public void DerivedPendingOperationListCannotHideBaseItems()
    {
        var run = FullAgentRun();
        run.PendingOperationIds = CreateDeceptiveList(
            "operation-1",
            "operation-2",
            "operation-3");

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeProtocolInputGuard
                .ValidateAgentRunBeforeSerialization(
                    run,
                    Limits(maxContainerItems: 2),
                    65_536,
                    "run"));

        Assert.Equal("agent_run_items_exceeded", error.LimitCode);
    }

    [Fact]
    public void DerivedSubjectListCannotHideBaseItems()
    {
        var observation = ValidObservation();
        observation.SubjectIds = CreateDeceptiveList(
            "subject-1",
            "subject-2",
            "subject-3");

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeProtocolInputGuard
                .ValidateObservationBeforeSerialization(
                    observation,
                    Limits(maxContainerItems: 2),
                    65_536,
                    "observation"));

        Assert.Equal("observation_items_exceeded", error.LimitCode);
    }

    [Fact]
    public void DerivedConflictScopeListCannotHideBaseItems()
    {
        var tool = ValidTool();
        tool.ConflictScopes = CreateDeceptiveList(
            "scope-1",
            "scope-2",
            "scope-3");

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeProtocolInputGuard
                .ValidateToolBeforeSerialization(
                    tool,
                    Limits(maxContainerItems: 2),
                    65_536,
                    "tool"));

        Assert.Equal(
            "tool_descriptor_items_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void DerivedAgentRunExtensionDictionaryCannotHideBaseItems()
    {
        var run = FullAgentRun();
        run.Extensions = DeceptiveExtensions(3);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeProtocolInputGuard
                .ValidateAgentRunBeforeSerialization(
                    run,
                    Limits(maxContainerItems: 2),
                    65_536,
                    "run"));

        Assert.Equal("agent_run_items_exceeded", error.LimitCode);
    }

    [Fact]
    public void DerivedObservationExtensionDictionaryCannotHideBaseItems()
    {
        var observation = ValidObservation();
        observation.Extensions = DeceptiveExtensions(2);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeProtocolInputGuard
                .ValidateObservationBeforeSerialization(
                    observation,
                    Limits(),
                    65_536,
                    "observation",
                    maximumExtensionItems: 1));

        Assert.Equal("observation_items_exceeded", error.LimitCode);
    }

    [Fact]
    public void DerivedToolExtensionDictionaryCannotHideBaseItems()
    {
        var tool = ValidTool();
        tool.Extensions = DeceptiveExtensions(3);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeProtocolInputGuard
                .ValidateToolBeforeSerialization(
                    tool,
                    Limits(maxContainerItems: 2),
                    65_536,
                    "tool"));

        Assert.Equal(
            "tool_descriptor_items_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void NullListElementIsRejectedFromBaseStorage()
    {
        var run = FullAgentRun();
        run.PendingOperationIds =
            CreateDeceptiveList(new string[] { null! });

        var error = Assert.Throws<ArgumentException>(
            () => RuntimeProtocolInputGuard
                .ValidateAgentRunBeforeSerialization(
                    run,
                    Limits(),
                    65_536,
                    "run"));

        Assert.Equal("run", error.ParamName);
    }

    [Fact]
    public void CopyExtensionsUsesBaseDictionaryStorage()
    {
        var extensions = DeceptiveExtensions(65);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeGuard.CopyExtensions(
                extensions,
                Limits()));

        Assert.Equal("extension_items_exceeded", error.LimitCode);
    }

    [Fact]
    public void CopyExtensionsBoundsInfiniteCustomDictionary()
    {
        var extensions = new CustomReadOnlyDictionary(
            Json("""{"value":1}"""),
            count: null);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeGuard.CopyExtensions(
                extensions,
                Limits()));

        Assert.Equal("extension_items_exceeded", error.LimitCode);
        Assert.Equal(65, extensions.MoveNextCount);
    }

    [Fact]
    public void CopyExtensionsIgnoresCustomCountAndBoundsEnumeration()
    {
        var extensions = new CustomReadOnlyDictionary(
            Json("""{"value":1}"""),
            count: 65);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => RuntimeGuard.CopyExtensions(
                extensions,
                Limits()));

        Assert.Equal("extension_items_exceeded", error.LimitCode);
        Assert.Equal(65, extensions.MoveNextCount);
    }

    [Fact]
    public void ProtocolGuardDoesNotCloneJsonElementsBeforeBudgetChecks()
    {
        var documents = Enumerable.Range(0, 8)
            .Select(
                index => JsonDocument.Parse(
                    $$"""{"index":{{index}},"data":"{{new string('x', 4_096)}}"}"""))
            .ToArray();
        var observation = ValidObservation();
        observation.Extensions = documents
            .Select(
                (document, index) => new KeyValuePair<
                    string,
                    JsonElement>(
                        $"extension-{index}",
                        document.RootElement))
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);

        var snapshot =
            RuntimeProtocolInputGuard.ValidateObservationBeforeSerialization(
                observation,
                Limits(),
                65_536,
                "observation");
        foreach (var document in documents)
        {
            document.Dispose();
        }

        Assert.Throws<ObjectDisposedException>(
            () => snapshot.Extensions["extension-0"].GetRawText());
    }

    private static AgentRun FullAgentRun()
    {
        return new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            Trigger = new AgentTrigger
            {
                Type = "scheduled",
                SourceId = "source-1",
                ScheduledFor = DateTimeOffset.UnixEpoch.AddMinutes(1)
            },
            TriggerObservationIds = new List<string>
            {
                "observation-1"
            },
            DecisionKey = "decision-key",
            BatchId = "batch-1",
            State = RunStates.Failed,
            Revision = 2,
            CurrentTurnId = "turn-1",
            RuntimeGeneration = 3,
            Budget = new AgentBudget
            {
                MaxTurns = 4,
                MaxDurationMs = 5_000,
                MaxTokens = 6_000,
                MaxCostUsd = "7.5",
                MaxActions = 8
            },
            Usage = new AgentUsage
            {
                Turns = 1,
                DurationMs = 2_000,
                InputTokens = 300,
                OutputTokens = 400,
                CostUsd = "0.5",
                Actions = 1,
                HasUnaccountedUsage = true,
                UnaccountedProviderAttempts = 2
            },
            PendingOperationIds = new List<string>
            {
                "operation-1"
            },
            TerminalReason = "provider_error",
            CompletionIntent = CompletionIntents.Failed,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1)
        };
    }

    private static ObservationEnvelope FullResourceObservation()
    {
        return new ObservationEnvelope
        {
            ObservationId = "observation-1",
            WorldId = "world-1",
            SessionId = "session-1",
            Source = "game",
            Kind = "custom",
            SubjectIds = new List<string> { "subject-1" },
            ContentType = "application/octet-stream",
            SchemaRef = "schema:observation",
            ContentSchemaVersion = "2",
            ResourceRef = new ResourceReference
            {
                Uri = "game://resources/observation-1",
                MediaType = "application/octet-stream",
                Digest = "sha256:0011",
                SizeBytes = 42
            },
            ObservedAt = DateTimeOffset.UnixEpoch,
            TtlMs = 60_000,
            Sequence = 7,
            StateVersion = "state-2",
            Trust = "trusted",
            Visibility = new VisibilityRule
            {
                Scope = "audience",
                AudienceIds = new List<string> { "agent-1" }
            },
            Priority = 9,
            CacheKey = "cache-key"
        };
    }

    private static ObservationEnvelope ValidObservation()
    {
        return new ObservationEnvelope
        {
            ObservationId = "observation-1",
            WorldId = "world-1",
            Source = "game",
            Kind = "custom",
            Payload = Json("""{"value":1}"""),
            ObservedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static ToolDescriptor FullTool(
        JsonElement parametersSchema,
        JsonElement resultSchema)
    {
        return new ToolDescriptor
        {
            Name = "world.update",
            Version = "2.0.0",
            Description = "Updates a world value.",
            ParametersSchema = parametersSchema,
            ResultSchema = resultSchema,
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string>
            {
                "world:{worldId}"
            },
            ThreadAffinity = ThreadAffinities.EngineMainThread,
            TimeoutMs = 12_345,
            RetryPolicy = ToolRetryPolicies.Idempotent,
            IdempotencyPolicy = ToolIdempotencyPolicies.Required,
            Toolset = "world",
            Visibility = ToolVisibilities.Deferred
        };
    }

    private static ToolDescriptor ValidTool()
    {
        return FullTool(
            Json("""{"type":"object"}"""),
            Json("""{"type":"object"}"""));
    }

    private static JsonValueLimits Limits(int maxContainerItems = 128)
    {
        return new JsonValueLimits(
            maxUtf8Bytes: 65_536,
            maxDepth: 16,
            maxNodes: 512,
            maxStringUtf8Bytes: 16_384,
            maxContainerItems: maxContainerItems);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static DeceptiveList<T> CreateDeceptiveList<T>(
        params T[] values)
    {
        var result = new DeceptiveList<T>();
        result.AddRange(values);
        Assert.True(((IReadOnlyCollection<T>)result).Count == 0);
        Assert.Empty((IEnumerable<T>)result);
        Assert.Equal(values.Length, result.Count);
        return result;
    }

    private static DeceptiveExtensionDictionary DeceptiveExtensions(
        int count)
    {
        var result = new DeceptiveExtensionDictionary();
        for (var index = 0; index < count; index++)
        {
            result.Add(
                $"key-{index:D4}",
                Json($$"""{"value":{{index}}}"""));
        }

        Assert.True(
            ((IReadOnlyCollection<
                KeyValuePair<string, JsonElement>>)result).Count == 0);
        Assert.Empty(
            (IEnumerable<KeyValuePair<string, JsonElement>>)result);
        Assert.Equal(count, result.Count);
        return result;
    }

    private sealed class DeceptiveList<T> : List<T>, IReadOnlyList<T>
    {
        int IReadOnlyCollection<T>.Count => 0;

        IEnumerator<T> IEnumerable<T>.GetEnumerator() =>
            Enumerable.Empty<T>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            Enumerable.Empty<T>().GetEnumerator();
    }

    private sealed class DeceptiveExtensionDictionary
        : Dictionary<string, JsonElement>,
          IReadOnlyDictionary<string, JsonElement>
    {
        int IReadOnlyCollection<
            KeyValuePair<string, JsonElement>>.Count => 0;

        IEnumerator<KeyValuePair<string, JsonElement>>
            IEnumerable<KeyValuePair<string, JsonElement>>
                .GetEnumerator() =>
            Enumerable.Empty<
                KeyValuePair<string, JsonElement>>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            Enumerable.Empty<
                KeyValuePair<string, JsonElement>>().GetEnumerator();
    }

    private sealed class CustomReadOnlyDictionary
        : IReadOnlyDictionary<string, JsonElement>
    {
        private readonly JsonElement _value;
        private readonly int? _count;

        public CustomReadOnlyDictionary(
            JsonElement value,
            int? count)
        {
            _value = value;
            _count = count;
        }

        public int Count => 0;

        public IEnumerable<string> Keys =>
            throw new InvalidOperationException("Keys must not be read.");

        public IEnumerable<JsonElement> Values =>
            throw new InvalidOperationException("Values must not be read.");

        public JsonElement this[string key] =>
            throw new KeyNotFoundException(key);

        public int MoveNextCount { get; private set; }

        public bool ContainsKey(string key)
        {
            _ = key;
            return false;
        }

        public bool TryGetValue(
            string key,
            out JsonElement value)
        {
            _ = key;
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<string, JsonElement>>
            GetEnumerator()
        {
            var index = 0;
            while (!_count.HasValue || index < _count.Value)
            {
                MoveNextCount++;
                yield return new(
                    $"key-{index:D4}",
                    _value);
                index++;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
