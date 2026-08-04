using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class HostToolExecutorTests
{
    [Fact]
    public async Task InvalidSuccessfulResultIsRemovedWithoutLosingCommitStatus()
    {
        var setup = Setup(
            ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "properties":{"value":{"type":"integer"}},
                  "required":["value"],
                  "additionalProperties":false
                }
                """),
            ProtocolJson.ParseElement("""{"value":"wrong"}"""));

        var returned = await setup.Executor.ExecuteAsync(
            setup.Execution,
            CancellationToken.None);

        Assert.Equal("succeeded", returned.GetProperty("status").GetString());
        Assert.Equal(
            "tool_result_schema_invalid",
            returned.GetProperty("errorCode").GetString());
        Assert.True(
            setup.Executor.TryGetReceipt(
                setup.Execution.ToolCallId,
                out var receipt));
        Assert.Equal(ReceiptStatuses.Succeeded, receipt!.Status);
        Assert.Null(receipt.Result);
        Assert.Equal("tool_result_schema_invalid", receipt.ErrorCode);
        Assert.False(receipt.Retryable);
    }

    [Fact]
    public async Task ValidSuccessfulResultIsSnapshottedAndReturned()
    {
        var setup = Setup(
            ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "properties":{"value":{"type":"integer"}},
                  "required":["value"],
                  "additionalProperties":false
                }
                """),
            ProtocolJson.ParseElement("""{"value":7}"""));

        var returned = await setup.Executor.ExecuteAsync(
            setup.Execution,
            CancellationToken.None);

        Assert.Equal(7, returned.GetProperty("value").GetInt32());
        Assert.True(
            setup.Executor.TryGetReceipt(
                setup.Execution.ToolCallId,
                out var receipt));
        Assert.Equal(7, receipt!.Result!.Value.GetProperty("value").GetInt32());
        Assert.Null(receipt.ErrorCode);
    }

    [Fact]
    public async Task OversizedReceiptIsRejectedBeforeSnapshotting()
    {
        var result = JsonArrayBuilder.Object(
            ("value", JsonArrayBuilder.String(new string('x', 300_000))));
        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            Receipt(result));

        await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());
        Assert.False(
            setup.Executor.TryGetReceipt(
                setup.Execution.ToolCallId,
                out _));
    }

    [Fact]
    public async Task CrossWorldAuthoritativeObservationIsRejected()
    {
        var receipt = Receipt(ProtocolJson.ParseElement("""{"value":7}"""));
        receipt.AuthoritativeObservations.Add(
            Observation("world-other", includePayload: true));
        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt);

        await Assert.ThrowsAsync<OperationLedgerConflictException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task PrivateObservationForAnotherAgentIsRejected()
    {
        var receipt = Receipt(ProtocolJson.ParseElement("""{"value":7}"""));
        var observation = Observation("world-1", includePayload: true);
        observation.Visibility = new VisibilityRule
        {
            Scope = ObservationVisibilityScopes.Private,
            AudienceIds = new List<string> { "agent-other" }
        };
        receipt.AuthoritativeObservations.Add(observation);
        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt);

        var error = await Assert.ThrowsAsync<ObservationAdmissionException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("observation_audience_mismatch", error.ReasonCode);
    }

    [Fact]
    public async Task ObservationForAnotherSessionIsRejected()
    {
        var receipt = Receipt(ProtocolJson.ParseElement("""{"value":7}"""));
        var observation = Observation("world-1", includePayload: true);
        observation.SessionId = "session-other";
        receipt.AuthoritativeObservations.Add(observation);
        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt,
            sessionId: "session-current");

        var error = await Assert.ThrowsAsync<ObservationAdmissionException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("observation_session_mismatch", error.ReasonCode);
    }

    [Fact]
    public async Task InvalidAuthoritativeObservationIsRejected()
    {
        var receipt = Receipt(ProtocolJson.ParseElement("""{"value":7}"""));
        receipt.AuthoritativeObservations.Add(
            Observation("world-1", includePayload: false));
        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt);

        await Assert.ThrowsAsync<JsonException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task AuthoritativeObservationCountIsBounded()
    {
        var receipt = Receipt(ProtocolJson.ParseElement("""{"value":7}"""));
        for (var index = 0;
             index <= ActionReceiptIngressValidator.MaxAuthoritativeObservations;
             index++)
        {
            var observation = Observation("world-1", includePayload: true);
            observation.ObservationId = "observation-" + index;
            receipt.AuthoritativeObservations.Add(observation);
        }

        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt);

        await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task AuthoritativeObservationAggregateBytesAreBounded()
    {
        var receipt = Receipt(ProtocolJson.ParseElement("""{"value":7}"""));
        for (var index = 0; index < 5; index++)
        {
            var observation = Observation("world-1", includePayload: true);
            observation.ObservationId = "observation-" + index;
            observation.Payload = JsonArrayBuilder.Object(
                ("value", JsonArrayBuilder.String(new string('x', 60_000))));
            receipt.AuthoritativeObservations.Add(observation);
        }

        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt);

        await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task ReceiptExtensionsUseTheConcreteDictionarySnapshot()
    {
        var receipt = Receipt(
            ProtocolJson.ParseElement("""{"value":7}"""));
        var extensions = new InterfaceHidingDictionary();
        for (var index = 0; index <= 64; index++)
        {
            extensions.Add(
                "extension-" + index,
                ProtocolJson.ParseElement("true"));
        }
        receipt.Extensions = extensions;
        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("extension_items_exceeded", error.LimitCode);
    }

    [Fact]
    public async Task ObservationListsUseTheConcreteListSnapshot()
    {
        var receipt = Receipt(
            ProtocolJson.ParseElement("""{"value":7}"""));
        var observation = Observation(
            "world-1",
            includePayload: true);
        var subjectIds = new InterfaceHidingList();
        for (var index = 0; index <= 256; index++)
        {
            subjectIds.Add("subject-" + index);
        }
        observation.SubjectIds = subjectIds;
        receipt.AuthoritativeObservations.Add(observation);
        var setup = Setup(
            ProtocolJson.ParseElement("""{}"""),
            receipt);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => setup.Executor.ExecuteAsync(
                    setup.Execution,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("collection_items_exceeded", error.LimitCode);
    }

    [Fact]
    public async Task HostCannotMutateJournaledActionRequest()
    {
        var setup = Setup(
            ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "properties":{"value":{"type":"integer"}},
                  "required":["value"],
                  "additionalProperties":false
                }
                """),
            ProtocolJson.ParseElement("""{"value":7}"""));
        var host = new MutatingHost();
        var executor = new HostToolExecutor(
            host,
            new Dictionary<string, ActionRequest>(StringComparer.Ordinal)
            {
                ["call-1"] = setup.AuthoritativeRequest
            },
            RunFor(setup.AuthoritativeRequest));

        var returned = await executor.ExecuteAsync(
            setup.Execution,
            CancellationToken.None);

        Assert.Equal(7, returned.GetProperty("value").GetInt32());
        Assert.NotNull(host.ReceivedRequest);
        Assert.NotSame(setup.AuthoritativeRequest, host.ReceivedRequest);
        Assert.Equal("inspect", setup.AuthoritativeRequest.ActionName);
        Assert.Equal(
            "{}",
            setup.AuthoritativeRequest.Arguments.GetRawText());
        Assert.Equal("operation-1", setup.AuthoritativeRequest.OperationId);
    }

    [Fact]
    public async Task ProgressIsBoundedClonedAndClosedWithExecutionScope()
    {
        var setup = Setup(
            ProtocolJson.ParseElement(
                """{"type":"object","additionalProperties":true}"""),
            ProtocolJson.ParseElement("""{"value":7}"""));
        var host = new ProgressHost();
        var published = new List<GameActionProgress>();
        var operationIds = new List<string>();
        var executor = new HostToolExecutor(
            host,
            new Dictionary<string, ActionRequest>(StringComparer.Ordinal)
            {
                ["call-1"] = setup.AuthoritativeRequest
            },
            RunFor(setup.AuthoritativeRequest),
            progressPublisher: (action, progress) =>
            {
                operationIds.Add(action.OperationId);
                published.Add(progress);
            });

        await executor.ExecuteAsync(setup.Execution, CancellationToken.None);
        host.Progress!.Report(new GameActionProgress
        {
            Stage = "late",
            Current = 2,
            Total = 2
        });

        var progress = Assert.Single(published);
        Assert.Equal("operation-1", Assert.Single(operationIds));
        Assert.Equal("building", progress.Stage);
        Assert.Equal(1, progress.Current);
        Assert.Equal(2, progress.Total);
        Assert.Equal(7, progress.Data!.Value.GetProperty("block").GetInt32());
    }

    private static SetupResult Setup(
        JsonElement resultSchema,
        JsonElement hostResult)
    {
        return Setup(resultSchema, Receipt(hostResult));
    }

    private static SetupResult Setup(
        JsonElement resultSchema,
        ActionReceipt hostReceipt,
        string? sessionId = null)
    {
        var registry = new ToolCatalogRegistry();
        registry.Replace(
            new[]
            {
                new ToolDescriptor
                {
                    Name = "inspect",
                    Version = "1",
                    Description = "Inspect one value.",
                    ParametersSchema = ProtocolJson.ParseElement(
                        """{"type":"object","additionalProperties":false}"""),
                    ResultSchema = resultSchema,
                    Effect = ToolEffects.PureRead
                }
            });
        Assert.True(registry.Current.TryGet("inspect", out var tool));
        var action = new ActionRequest
        {
            OperationId = "operation-1",
            RunId = "run-1",
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            ActionName = "inspect",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            RequestedAt = DateTimeOffset.UnixEpoch
        };
        var execution = new ToolExecutionRequest(
            "agent-1",
            new ToolInvocation
            {
                ToolCallId = "call-1",
                RunId = "run-1",
                TurnId = "turn-1",
                AttemptId = "attempt-1",
                ToolName = "inspect",
                ToolVersion = "1",
                Arguments = ProtocolJson.ParseElement("{}"),
                Effect = ToolEffects.PureRead,
                Sequence = 0,
                CreatedAt = DateTimeOffset.UnixEpoch
            },
            tool!);
        var host = new ResultHost(hostReceipt);
        var executor = new HostToolExecutor(
            host,
            new Dictionary<string, ActionRequest>(StringComparer.Ordinal)
            {
                ["call-1"] = action
            },
            RunFor(action, sessionId));
        return new SetupResult(executor, execution, action);
    }

    private static AgentRun RunFor(
        ActionRequest action,
        string? sessionId = null)
    {
        return new AgentRun
        {
            RunId = action.RunId,
            AgentId = action.AgentId,
            WorldId = action.WorldId,
            SessionId = sessionId,
            State = RunStates.Running,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static ActionReceipt Receipt(JsonElement result)
    {
        return new ActionReceipt
        {
            OperationId = "operation-1",
            Revision = 1,
            Status = ReceiptStatuses.Succeeded,
            Result = result,
            CommittedAt = DateTimeOffset.UnixEpoch,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static ObservationEnvelope Observation(
        string worldId,
        bool includePayload)
    {
        return new ObservationEnvelope
        {
            ObservationId = "observation-1",
            WorldId = worldId,
            Source = "game",
            Kind = "custom",
            ContentType = "application/json",
            Payload = includePayload
                ? ProtocolJson.ParseElement("""{"value":1}""")
                : null,
            ObservedAt = DateTimeOffset.UnixEpoch,
            Trust = "authoritative"
        };
    }

    private sealed record SetupResult(
        HostToolExecutor Executor,
        ToolExecutionRequest Execution,
        ActionRequest AuthoritativeRequest);

    private sealed class ResultHost : IGameHost
    {
        private readonly ActionReceipt _receipt;

        public ResultHost(ActionReceipt receipt)
        {
            _receipt = receipt;
        }

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(_receipt);
        }
    }

    private sealed class MutatingHost : IGameHost
    {
        public ActionRequest? ReceivedRequest { get; private set; }

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedRequest = request;
            request.ActionName = "mutated_action";
            request.Arguments =
                ProtocolJson.ParseElement("""{"mutated":true}""");
            request.OperationId = "mutated-operation";
            return new ValueTask<ActionReceipt>(
                Receipt(ProtocolJson.ParseElement("""{"value":7}""")));
        }
    }

    private sealed class ProgressHost : IProgressReportingGameHost
    {
        public IGameActionProgressSink? Progress { get; private set; }

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The progress-aware overload must be used.");

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            IGameActionProgressSink progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Progress = progress;
            request.OperationId = "tampered-operation";
            var source = new GameActionProgress
            {
                Stage = "building",
                Message = "Placing blocks.",
                Current = 1,
                Total = 2,
                Data = ProtocolJson.ParseElement("""{"block":7}""")
            };
            progress.Report(source);
            source.Stage = "mutated";
            source.Data = ProtocolJson.ParseElement("""{"block":9}""");
            return new ValueTask<ActionReceipt>(
                Receipt(ProtocolJson.ParseElement("""{"value":7}""")));
        }
    }

    private sealed class InterfaceHidingList :
        List<string>,
        IReadOnlyList<string>,
        IEnumerable<string>
    {
        int IReadOnlyCollection<string>.Count => 0;

        string IReadOnlyList<string>.this[int index] =>
            throw new InvalidOperationException(
                "The interface indexer must not be used.");

        IEnumerator<string> IEnumerable<string>.GetEnumerator() =>
            Enumerable.Empty<string>().GetEnumerator();

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            Enumerable.Empty<string>().GetEnumerator();
    }

    private sealed class InterfaceHidingDictionary :
        Dictionary<string, JsonElement>,
        IReadOnlyDictionary<string, JsonElement>,
        IEnumerable<KeyValuePair<string, JsonElement>>
    {
        int IReadOnlyCollection<KeyValuePair<string, JsonElement>>.Count =>
            0;

        IEnumerator<KeyValuePair<string, JsonElement>>
            IEnumerable<KeyValuePair<string, JsonElement>>
                .GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, JsonElement>>()
                .GetEnumerator();

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, JsonElement>>()
                .GetEnumerator();
    }
}
