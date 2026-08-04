using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ProviderRequestPreparationTests
{
    [Fact]
    public async Task SanitizerRepairsProviderViewWithoutMutatingTranscript()
    {
        var source = new[]
        {
            Message(
                "assistant-1",
                NormalizedRoles.Assistant,
                NormalizedContentPart.FromReasoning("private reasoning"),
                Call("call-1", "inspect"),
                Call("call-1", "inspect")),
            Message(
                "tool-1",
                NormalizedRoles.Tool,
                Result("orphan", "inspect"),
                Result("call-1", "inspect"),
                Result("call-1", "inspect")),
            Message(
                "assistant-2",
                NormalizedRoles.Assistant,
                Call("call-2", "move"))
        };
        var request = Request(source);
        var context = Context(
            request,
            new ProviderCapabilities
            {
                ReasoningInput = false,
                RequiresCompleteToolPairs = true
            });

        var prepared = await new ProviderRequestSanitizer()
            .PrepareRequestAsync(context, CancellationToken.None);

        Assert.Equal(1, prepared.Report.RemovedReasoningParts);
        Assert.Equal(2, prepared.Report.RemovedOrphanToolResults);
        Assert.Equal(1, prepared.Report.RemovedDuplicateToolCalls);
        Assert.Equal(1, prepared.Report.SynthesizedToolResults);
        Assert.True(prepared.Report.Changed);
        Assert.DoesNotContain(
            prepared.Request.Messages.SelectMany(item => item.Parts),
            part => part.Type == NormalizedPartTypes.Reasoning);
        Assert.Contains(
            prepared.Request.Messages,
            item => item.MessageId == "provider-repair:call-2");

        Assert.Equal(3, source.Length);
        Assert.Equal(3, source[0].Parts.Count);
        Assert.Equal(3, source[1].Parts.Count);
        Assert.DoesNotContain(
            source,
            item => item.MessageId == "provider-repair:call-2");
    }

    [Fact]
    public async Task SanitizerIsDeterministic()
    {
        var request = Request(
            new[]
            {
                Message(
                    "assistant",
                    NormalizedRoles.Assistant,
                    Call("call-1", "inspect"))
            });
        var context = Context(
            request,
            new ProviderCapabilities
            {
                RequiresCompleteToolPairs = true
            });
        var sanitizer = new ProviderRequestSanitizer();

        var first = await sanitizer.PrepareRequestAsync(
            context,
            CancellationToken.None);
        var second = await sanitizer.PrepareRequestAsync(
            context,
            CancellationToken.None);

        Assert.Equal(first.Report.OutputDigest, second.Report.OutputDigest);
        Assert.Equal(
            first.Request.Messages.Select(Encoded),
            second.Request.Messages.Select(Encoded));
    }

    [Fact]
    public void MessageDigestCanonicalizesJsonObjectOrderAndEscapes()
    {
        var first = new[]
        {
            Message(
                "json",
                NormalizedRoles.User,
                NormalizedContentPart.FromJson(
                    ProtocolJson.ParseElement(
                        """{"a":"\u003C","b":[1,true]}""")))
        };
        var equivalent = new[]
        {
            Message(
                "json",
                NormalizedRoles.User,
                NormalizedContentPart.FromJson(
                    ProtocolJson.ParseElement(
                        """{"b":[1,true],"a":"<"}""")))
        };
        var changed = new[]
        {
            Message(
                "json",
                NormalizedRoles.User,
                NormalizedContentPart.FromJson(
                    ProtocolJson.ParseElement(
                        """{"a":"<","b":[1,false]}""")))
        };

        Assert.Equal(
            ProviderRequestSanitizer.DigestMessages(first, cancellationToken: TestContext.Current.CancellationToken),
            ProviderRequestSanitizer.DigestMessages(equivalent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotEqual(
            ProviderRequestSanitizer.DigestMessages(first, cancellationToken: TestContext.Current.CancellationToken),
            ProviderRequestSanitizer.DigestMessages(changed, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NearLimitJsonStringDigestRejectsWithoutLargeMaterialization()
    {
        var json = ProtocolJson.ParseElement(
            "{\"value\":\""
            + "<"
            + new string(
                'x',
                ProviderRequestContentGuard.MaxUtf8Bytes - 128)
            + "\"}");
        var messages = new[]
        {
            Message(
                "json-limit",
                NormalizedRoles.User,
                NormalizedContentPart.FromJson(json))
        };

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        _ = Assert.Throws<InvalidDataException>(
            () => ProviderRequestSanitizer.DigestMessages(messages, cancellationToken: TestContext.Current.CancellationToken));
        var allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.InRange(allocated, 0, 2 * 1_048_576);
    }

    [Fact]
    public void OversizedJsonScalarIsRejectedBeforeSnapshot()
    {
        var json = ProtocolJson.ParseElement(
            "{\"value\":\""
            + new string(
                'x',
                ProviderRequestContentGuard.MaxJsonScalarUtf8Bytes + 1)
            + "\"}");
        var request = Request(
            new[]
            {
                Message(
                    "json-limit",
                    NormalizedRoles.User,
                    NormalizedContentPart.FromJson(json))
            });

        var error = Assert.Throws<ProviderException>(
            () => new ProviderRequestSanitizer().PrepareRequestAsync(
                Context(request, new ProviderCapabilities()),
                CancellationToken.None));

        Assert.Equal("provider_request_input_limit", error.Code);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public void FieldwiseSnapshotAndDigestHonorCancellation()
    {
        var message = Message(
            "cancelled",
            NormalizedRoles.User,
            Text("hello"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => NormalizedMessageJournalCodec.CloneValidated(
                message,
                cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => ProviderRequestSanitizer.DigestMessages(
                new[] { message },
                cancellation.Token));
    }

    [Fact]
    public async Task ProviderToolCountLimitFailsBeforeDispatch()
    {
        var request = Request(
            new[] { Message("user", NormalizedRoles.User, Text("go")) },
            Tool("one"),
            Tool("two"));
        var context = Context(
            request,
            new ProviderCapabilities { MaxTools = 1 });

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await new ProviderRequestSanitizer()
                .PrepareRequestAsync(context, CancellationToken.None));

        Assert.Equal("provider_tool_count_exceeded", error.Code);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task ProviderSchemaByteLimitFailsBeforeDispatch()
    {
        var request = Request(
            new[] { Message("user", NormalizedRoles.User, Text("go")) },
            Tool("inspect"));
        var context = Context(
            request,
            new ProviderCapabilities { MaxToolSchemaUtf8Bytes = 1 });

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await new ProviderRequestSanitizer()
                .PrepareRequestAsync(context, CancellationToken.None));

        Assert.Equal("provider_tool_schema_bytes_exceeded", error.Code);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task RepairExpansionIsBoundedBeforeSynthesizingMessages()
    {
        var calls = Enumerable.Range(0, 4_097)
            .Select(index => Call("call-" + index, "inspect"))
            .ToArray();
        var request = Request(
            new[]
            {
                Message(
                    "assistant-many-calls",
                    NormalizedRoles.Assistant,
                    calls)
            });
        var context = Context(
            request,
            new ProviderCapabilities
            {
                RequiresCompleteToolPairs = true
            });

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await new ProviderRequestSanitizer()
                .PrepareRequestAsync(context, CancellationToken.None));

        Assert.Equal(
            "provider_request_adapter_output_limit",
            error.Code);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task SanitizerDoesNotMaterializeNearLimitEscapedText()
    {
        var payload = "<"
                      + new string(
                          'x',
                          ProviderRequestContentGuard.MaxUtf8Bytes - 65);
        var request = Request(
            new[]
            {
                Message(
                    "escape-heavy",
                    NormalizedRoles.User,
                    Text(payload))
            });
        var context = Context(request, new ProviderCapabilities());
        var sanitizer = new ProviderRequestSanitizer();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var pending = sanitizer.PrepareRequestAsync(
            context,
            CancellationToken.None);
        Assert.True(pending.IsCompletedSuccessfully);
        var prepared = await pending;
        var allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Same(
            payload,
            Assert.Single(
                Assert.Single(prepared.Request.Messages).Parts).Text);
        Assert.InRange(allocated, 0, 8 * 1_048_576);
    }

    private static ProviderRequestPreparationContext Context(
        StreamingModelRequest request,
        ProviderCapabilities capabilities)
    {
        return new ProviderRequestPreparationContext(
            "provider",
            new ProviderRouteIdentity(
                "provider",
                new ProviderRouteMetadata("model", "transport"),
                capabilities),
            capabilities,
            request);
    }

    private static StreamingModelRequest Request(
        IReadOnlyList<NormalizedMessage> messages,
        params ToolDescriptor[] tools)
    {
        return new StreamingModelRequest
        {
            RunId = "run",
            RunAttemptId = "run-attempt",
            TurnId = "turn",
            ProviderAttemptId = "provider-attempt",
            StreamAttemptId = "stream-attempt",
            Messages = messages,
            Tools = tools,
            MaxOutputTokens = 100
        };
    }

    private static NormalizedMessage Message(
        string id,
        string role,
        params NormalizedContentPart[] parts)
    {
        return new NormalizedMessage
        {
            MessageId = id,
            Role = role,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = parts.ToList()
        };
    }

    private static NormalizedContentPart Text(string text) =>
        NormalizedContentPart.FromText(text);

    private static NormalizedContentPart Call(
        string callId,
        string name) =>
        NormalizedContentPart.FromToolCall(
            new ModelToolCall
            {
                ToolCallId = callId,
                Name = name,
                Arguments = ProtocolJson.ParseElement("""{"value":1}""")
            });

    private static NormalizedContentPart Result(
        string callId,
        string name) =>
        NormalizedContentPart.FromToolResult(
            callId,
            name,
            ProtocolJson.ParseElement("""{"ok":true}"""));

    private static ToolDescriptor Tool(string name)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1",
            Description = "A test tool.",
            Effect = ToolEffects.PureRead,
            ParametersSchema = ProtocolJson.ParseElement(
                """{"type":"object","additionalProperties":false}"""),
            ResultSchema = ProtocolJson.ParseElement(
                """{"type":"object","additionalProperties":false}"""),
            TimeoutMs = 1_000,
            ThreadAffinity = ThreadAffinities.AnyThread
        };
    }

    private static string Encoded(NormalizedMessage message) =>
        NormalizedMessageJournalCodec.Encode(message).GetRawText();
}
