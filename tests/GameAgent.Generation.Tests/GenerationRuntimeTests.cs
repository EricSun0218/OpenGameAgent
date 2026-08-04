using System.Text.Json;
using GameAgent.Protocol;
using Xunit;

namespace GameAgent.Generation.Tests;

public sealed class GenerationRuntimeTests
{
    [Fact]
    public async Task Submit_is_idempotent_for_canonically_equal_request()
    {
        var provider = new FakeProvider
        {
            SubmitResult = Submission(GenerationJobStatuses.Succeeded)
        };
        var runtime = Runtime(provider);

        var first = await runtime.SubmitAsync(Request("same", "{\"b\":2,\"a\":1}"), cancellationToken: TestContext.Current.CancellationToken);
        var second = await runtime.SubmitAsync(Request("same", "{\"a\":1,\"b\":2}"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first.RequestDigest, second.RequestDigest);
        Assert.Equal(1, provider.SubmitCalls);
    }

    [Fact]
    public async Task Submit_rejects_operation_identity_reuse_with_changed_payload()
    {
        var provider = new FakeProvider
        {
            SubmitResult = Submission(GenerationJobStatuses.Succeeded)
        };
        var runtime = Runtime(provider);
        await runtime.SubmitAsync(Request("same", "{\"value\":1}"), cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await runtime.SubmitAsync(Request("same", "{\"value\":2}"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("generation_operation_conflict", exception.ReasonCode);
        Assert.Equal(1, provider.SubmitCalls);
    }

    [Fact]
    public async Task Submit_resumes_a_durably_queued_request_but_never_replays_uncertain_dispatch()
    {
        var provider = new FakeProvider
        {
            SubmitResult = Submission(GenerationJobStatuses.Succeeded)
        };
        var jobs = new InMemoryGenerationJobStore();
        var request = Request("resume", "{\"value\":1}");
        var snapshot = GenerationRequestSnapshotter.Snapshot(request);
        await jobs.PutAsync(
            Job(
                snapshot,
                GenerationAcceptance.NotAccepted,
                GenerationJobStatuses.Queued),
            CancellationToken.None);
        var runtime = new GenerationRuntime(new[] { provider }, jobs, new PassArtifactStore());

        var completed = await runtime.SubmitAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GenerationJobStatuses.Succeeded, completed.Status);
        Assert.Equal(1, provider.SubmitCalls);

        var uncertainRequest = Request("uncertain", "{\"value\":2}");
        var uncertainSnapshot = GenerationRequestSnapshotter.Snapshot(uncertainRequest);
        await jobs.PutAsync(
            Job(
                uncertainSnapshot,
                GenerationAcceptance.Unknown,
                GenerationJobStatuses.Unknown),
            CancellationToken.None);

        var uncertain = await runtime.SubmitAsync(uncertainRequest, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GenerationJobStatuses.Unknown, uncertain.Status);
        Assert.Equal(1, provider.SubmitCalls);
    }

    [Fact]
    public async Task Shared_store_claims_provider_dispatch_once_across_runtime_instances()
    {
        var provider = new FakeProvider
        {
            SubmitResult = Submission(GenerationJobStatuses.Succeeded)
        };
        var jobs = new SimultaneousAbsentReadStore();
        var left = new GenerationRuntime(new[] { provider }, jobs, new PassArtifactStore());
        var right = new GenerationRuntime(new[] { provider }, jobs, new PassArtifactStore());
        var request = Request("cross-runtime", "{\"value\":1}");

        _ = await Task.WhenAll(
            left.SubmitAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask(),
            right.SubmitAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        var durable = await jobs.TryGetAsync("cross-runtime", CancellationToken.None);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(GenerationJobStatuses.Succeeded, durable!.Status);
    }

    [Fact]
    public async Task Refresh_and_cancel_preserve_job_modality()
    {
        var provider = new FakeProvider
        {
            SubmitResult = Submission(GenerationJobStatuses.Queued, "remote-1"),
            GetResult = new GenerationProviderResult
            {
                Status = GenerationJobStatuses.Succeeded,
                ProviderJobId = "remote-1",
                Output = Json("{\"done\":true}")
            }
        };
        var runtime = Runtime(provider);
        await runtime.SubmitAsync(Request("poll", "{}", GenerationModalities.StructuredContent), cancellationToken: TestContext.Current.CancellationToken);

        var completed = await runtime.RefreshAsync("poll", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GenerationJobStatuses.Succeeded, completed.Status);
        Assert.Equal(GenerationModalities.StructuredContent, provider.LastGetModality);

        provider.SubmitResult = Submission(GenerationJobStatuses.Queued, "remote-2");
        await runtime.SubmitAsync(Request("cancel", "{}", GenerationModalities.Video), cancellationToken: TestContext.Current.CancellationToken);
        await runtime.RequestCancellationAsync("cancel", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GenerationModalities.Video, provider.LastCancelModality);
    }

    [Fact]
    public async Task Failed_artifact_import_leaves_recoverable_materializing_checkpoint()
    {
        var source = new GenerationArtifactSource
        {
            InlineData = new byte[] { 1, 2, 3 },
            MediaType = "application/octet-stream",
            SizeBytes = 3
        };
        var provider = new FakeProvider
        {
            SubmitResult = Submission(
                GenerationJobStatuses.Succeeded,
                "remote-1",
                source),
            GetResult = new GenerationProviderResult
            {
                Status = GenerationJobStatuses.Succeeded,
                ProviderJobId = "remote-1",
                Artifacts = new[] { source }
            }
        };
        var artifacts = new FailOnceArtifactStore();
        var jobs = new InMemoryGenerationJobStore();
        var runtime = new GenerationRuntime(
            new[] { provider },
            jobs,
            artifacts);

        var exception = await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await runtime.SubmitAsync(
                Request("artifact", "{}", GenerationModalities.Video), cancellationToken: TestContext.Current.CancellationToken));
        var pending = await runtime.TryGetAsync("artifact", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("generation_artifact_import_incomplete", exception.ReasonCode);
        Assert.NotNull(pending);
        Assert.Equal(GenerationJobStatuses.Materializing, pending!.Status);
        Assert.Equal("remote-1", pending.ProviderJobId);

        var recovered = await runtime.RefreshAsync("artifact", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GenerationJobStatuses.Succeeded, recovered.Status);
        Assert.Single(recovered.Artifacts);
    }

    [Fact]
    public async Task Materialization_recovers_without_a_provider_job_identity()
    {
        var source = new GenerationArtifactSource
        {
            InlineData = new byte[] { 1, 2, 3 },
            MediaType = "application/octet-stream",
            SizeBytes = 3
        };
        var provider = new FakeProvider
        {
            SubmitResult = Submission(
                GenerationJobStatuses.Succeeded,
                providerJobId: null,
                source)
        };
        var artifacts = new FailOnceArtifactStore();
        var jobs = new InMemoryGenerationJobStore();
        var runtime = new GenerationRuntime(new[] { provider }, jobs, artifacts);

        await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await runtime.SubmitAsync(
                Request("sync-artifact", "{}", GenerationModalities.Speech), cancellationToken: TestContext.Current.CancellationToken));

        var recovered = await runtime.RecoverUnfinishedAsync(cancellationToken: TestContext.Current.CancellationToken);

        var completed = Assert.Single(recovered);
        Assert.Equal(GenerationJobStatuses.Succeeded, completed.Status);
        Assert.Single(completed.Artifacts);
        Assert.Empty(completed.PendingArtifacts);
        Assert.Equal(1, provider.SubmitCalls);
    }

    [Fact]
    public async Task Invalid_accepted_provider_contract_is_durable_unknown()
    {
        var provider = new FakeProvider
        {
            SubmitResult = Submission(
                GenerationJobStatuses.Materializing,
                "remote-invalid")
        };
        var runtime = Runtime(provider);

        var exception = await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await runtime.SubmitAsync(Request("invalid", "{}"), cancellationToken: TestContext.Current.CancellationToken));
        var durable = await runtime.TryGetAsync("invalid", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(exception.OutcomeUncertain);
        Assert.Equal(GenerationJobStatuses.Unknown, durable!.Status);
        Assert.Equal("remote-invalid", durable.ProviderJobId);
    }

    [Fact]
    public async Task Tool_bridge_exposes_valid_tools_and_returns_generation_receipt()
    {
        var provider = new FakeProvider
        {
            SubmitResult = Submission(GenerationJobStatuses.Succeeded)
        };
        var bridge = new GenerationToolBridge(Runtime(provider));
        Assert.All(bridge.Tools, tool => Assert.Empty(ProtocolValidator.Validate(tool)));
        var request = new ActionRequest
        {
            OperationId = "action-1",
            AgentId = "npc-1",
            WorldId = "world-1",
            ActionName = GenerationToolNames.GenerateImage,
            ActionVersion = "1.0.0",
            Arguments = Json("{\"input\":{\"prompt\":\"a tree\"}}"),
            RequestedAt = DateTimeOffset.UtcNow
        };

        var receipt = await bridge.HandleAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReceiptStatuses.Succeeded, receipt.Status);
        Assert.Equal("action-1", receipt.Result!.Value.GetProperty("operationId").GetString());
        Assert.Equal(1, provider.SubmitCalls);
    }

    private static GenerationRuntime Runtime(FakeProvider provider) =>
        new(
            new[] { provider },
            new InMemoryGenerationJobStore(),
            new PassArtifactStore());

    private static GenerationRequest Request(
        string operationId,
        string json,
        string modality = GenerationModalities.Image) =>
        new()
        {
            OperationId = operationId,
            Modality = modality,
            Input = Json(json),
            IdempotencyKey = operationId,
            AuthorityId = "npc-1"
        };

    private static GenerationSubmission Submission(
        string status,
        string? providerJobId = null,
        params GenerationArtifactSource[] artifacts) =>
        new()
        {
            Acceptance = GenerationAcceptance.Accepted,
            Result = new GenerationProviderResult
            {
                Status = status,
                ProviderJobId = providerJobId,
                Artifacts = artifacts,
                Output = Json("{\"ok\":true}")
            }
        };

    private static GenerationJob Job(
        GenerationRequest request,
        string acceptance,
        string status) => new()
        {
            OperationId = request.OperationId,
            RequestDigest = GenerationRequestSnapshotter.ComputeDigest(request),
            Modality = request.Modality,
            Provider = "fake",
            Acceptance = acceptance,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AuthorityId = request.AuthorityId,
            Revision = 1
        };

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FakeProvider : IGenerationProvider
    {
        public string Name => "fake";

        public GenerationProviderCapabilities Capabilities { get; } = new()
        {
            Modalities = new[]
            {
                GenerationModalities.Image,
                GenerationModalities.Video,
                GenerationModalities.Speech,
                GenerationModalities.StructuredContent
            },
            SupportsPolling = true,
            SupportsCancellation = true
        };

        public GenerationSubmission SubmitResult { get; set; } = new();

        public GenerationProviderResult GetResult { get; set; } = new()
        {
            Status = GenerationJobStatuses.Running,
            ProviderJobId = "remote"
        };

        public int SubmitCalls { get; private set; }

        public string? LastGetModality { get; private set; }

        public string? LastCancelModality { get; private set; }

        public ValueTask<GenerationSubmission> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCalls++;
            return new ValueTask<GenerationSubmission>(SubmitResult);
        }

        public ValueTask<GenerationProviderResult> GetAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastGetModality = modality;
            return new ValueTask<GenerationProviderResult>(GetResult);
        }

        public ValueTask<GenerationCancelResult> CancelAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCancelModality = modality;
            return new ValueTask<GenerationCancelResult>(new GenerationCancelResult
            {
                Accepted = true,
                Status = GenerationJobStatuses.CancelRequested
            });
        }
    }

    private sealed class PassArtifactStore : IGenerationArtifactStore
    {
        public ValueTask<GenerationArtifact> ImportAsync(
            string operationId,
            int ordinal,
            GenerationArtifactSource source,
            CancellationToken cancellationToken) =>
            new(new GenerationArtifact
            {
                ArtifactId = operationId + ":" + ordinal,
                Uri = "file:///generated/" + operationId + "/" + ordinal,
                MediaType = source.MediaType,
                Sha256 = new string('a', 64),
                SizeBytes = source.SizeBytes ?? source.InlineData.Length
            });
    }

    private sealed class FailOnceArtifactStore : IGenerationArtifactStore
    {
        private int _attempts;

        public ValueTask<GenerationArtifact> ImportAsync(
            string operationId,
            int ordinal,
            GenerationArtifactSource source,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new IOException("injected import failure");
            }

            return new ValueTask<GenerationArtifact>(new GenerationArtifact
            {
                ArtifactId = operationId + ":" + ordinal,
                Uri = "file:///generated/recovered.bin",
                MediaType = source.MediaType,
                Sha256 = new string('b', 64),
                SizeBytes = source.SizeBytes ?? source.InlineData.Length
            });
        }
    }

    private sealed class SimultaneousAbsentReadStore : IGenerationJobStore
    {
        private readonly InMemoryGenerationJobStore _inner = new();
        private readonly TaskCompletionSource<bool> _bothInitialReads =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialReads;

        public async ValueTask<GenerationJob?> TryGetAsync(
            string operationId,
            CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _initialReads);
            if (ordinal <= 2)
            {
                if (ordinal == 2)
                {
                    _bothInitialReads.TrySetResult(true);
                }

                await _bothInitialReads.Task.WaitAsync(cancellationToken);
                return null;
            }

            return await _inner.TryGetAsync(operationId, cancellationToken);
        }

        public ValueTask PutAsync(
            GenerationJob job,
            CancellationToken cancellationToken) =>
            _inner.PutAsync(job, cancellationToken);

        public ValueTask<IReadOnlyList<GenerationJob>> ListUnfinishedAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            _inner.ListUnfinishedAsync(maximumCount, cancellationToken);
    }
}
