using System.Text.Json;
using Xunit;

namespace GameAgent.Generation.Tests;

public sealed class GenerationPersistenceTests
{
    [Fact]
    public async Task File_job_store_survives_restart_and_rejects_corruption()
    {
        var root = TempDirectory();
        try
        {
            var job = ValidJob("persisted");
            using (var store = new FileGenerationJobStore(root))
            {
                await store.PutAsync(job, CancellationToken.None);
            }

            using (var reopened = new FileGenerationJobStore(root))
            {
                var restored = await reopened.TryGetAsync(
                    "persisted",
                    CancellationToken.None);
                Assert.Equal(job.RequestDigest, restored!.RequestDigest);
            }

            var record = Assert.Single(Directory.GetFiles(root, "*.job.json"));
            await File.WriteAllTextAsync(record, "{broken");
            using var corrupt = new FileGenerationJobStore(root);
            var exception = await Assert.ThrowsAsync<GenerationOperationException>(
                async () => await corrupt.TryGetAsync(
                    "persisted",
                    CancellationToken.None));
            Assert.Equal("generation_job_record_corrupt", exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task File_job_store_round_trips_pending_materialization_sources()
    {
        var root = TempDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var job = ValidJob("materializing");
            job.Status = GenerationJobStatuses.Materializing;
            job.PendingArtifacts = new[]
            {
                new GenerationArtifactSource
                {
                    InlineData = new byte[] { 1, 2, 3, 4 },
                    MediaType = "audio/wav",
                    FileName = "speech.wav",
                    SizeBytes = 4,
                    ExpiresAt = now.AddMinutes(5),
                    AuthorizationReference = "provider-token"
                }
            };

            using (var store = new FileGenerationJobStore(root))
            {
                await store.PutAsync(job, CancellationToken.None);
            }

            using var reopened = new FileGenerationJobStore(root);
            var restored = await reopened.TryGetAsync(
                "materializing",
                CancellationToken.None);

            var source = Assert.Single(restored!.PendingArtifacts);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, source.InlineData.ToArray());
            Assert.Equal("audio/wav", source.MediaType);
            Assert.Equal("provider-token", source.AuthorizationReference);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void File_job_store_enforces_single_writer_lease()
    {
        var root = TempDirectory();
        try
        {
            using var first = new FileGenerationJobStore(root);
            var exception = Assert.Throws<GenerationOperationException>(
                () => new FileGenerationJobStore(root));
            Assert.Equal("generation_job_store_writer_active", exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Durable_stores_reject_revision_gaps()
    {
        var jobRoot = TempDirectory();
        var contentRoot = TempDirectory();
        try
        {
            using var jobs = new FileGenerationJobStore(jobRoot);
            var job = ValidJob("revisioned-job");
            await jobs.PutAsync(job, CancellationToken.None);
            job.Revision = 3;
            var jobError = await Assert.ThrowsAsync<GenerationOperationException>(
                async () => await jobs.PutAsync(job, CancellationToken.None));
            Assert.Equal("generation_revision_conflict", jobError.ReasonCode);

            using var transactions = new FileGeneratedContentTransactionStore(contentRoot);
            var now = DateTimeOffset.UtcNow;
            var transaction = new GeneratedContentTransaction
            {
                TransactionId = "revisioned-content",
                Manifest = Manifest(),
                State = ContentTransactionStates.Prepared,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            };
            await transactions.PutAsync(transaction, CancellationToken.None);
            transaction.Revision = 3;
            var contentError = await Assert.ThrowsAsync<GenerationOperationException>(
                async () => await transactions.PutAsync(
                    transaction,
                    CancellationToken.None));
            Assert.Equal(
                "content_transaction_revision_conflict",
                contentError.ReasonCode);
        }
        finally
        {
            Directory.Delete(jobRoot, recursive: true);
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Content_transaction_store_survives_restart()
    {
        var root = TempDirectory();
        try
        {
            var host = new RecoveringContentHost();
            using (var store = new FileGeneratedContentTransactionStore(root))
            {
                var coordinator = new GeneratedContentCoordinator(host, store);
                var completed = await coordinator.StageValidateAndCommitAsync(
                    "tx-1",
                    Manifest(),
                    CancellationToken.None);
                Assert.Equal(ContentTransactionStates.Committed, completed.State);
            }

            using var reopened = new FileGeneratedContentTransactionStore(root);
            var restored = await reopened.TryGetAsync("tx-1", CancellationToken.None);
            Assert.Equal(ContentTransactionStates.Committed, restored!.State);
            Assert.Equal("receipt-1", restored.HostReceiptId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Content_coordinator_resumes_after_validation_outcome_is_reconciled()
    {
        var host = new RecoveringContentHost { FailValidationOnce = true };
        var store = new InMemoryGeneratedContentTransactionStore();
        var coordinator = new GeneratedContentCoordinator(host, store);

        var exception = await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await coordinator.StageValidateAndCommitAsync(
                "tx-recover",
                Manifest(),
                CancellationToken.None));
        Assert.True(exception.OutcomeUncertain);
        host.ReportedState = ContentTransactionStates.Staged;

        var recovered = await coordinator.StageValidateAndCommitAsync(
            "tx-recover",
            Manifest(),
            CancellationToken.None);

        Assert.Equal(ContentTransactionStates.Committed, recovered.State);
        Assert.Equal(1, host.StageCalls);
        Assert.Equal(2, host.ValidationCalls);
        Assert.Equal(1, host.CommitCalls);
    }

    [Fact]
    public async Task Content_manifest_rejects_null_artifact_entries()
    {
        var manifest = Manifest();
        manifest.Artifacts = new GenerationArtifact[] { null! };
        var coordinator = new GeneratedContentCoordinator(
            new RecoveringContentHost(),
            new InMemoryGeneratedContentTransactionStore());

        await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await coordinator.StageValidateAndCommitAsync(
                "tx-invalid",
                manifest,
                CancellationToken.None));
    }

    [Fact]
    public async Task Artifact_store_rejects_oversized_declared_size_before_writing()
    {
        var root = TempDirectory();
        try
        {
            using var store = new FileGenerationArtifactStore(
                new FileGenerationArtifactStoreOptions
                {
                    RootDirectory = root,
                    MaxArtifactBytes = 1_024
                });
            var exception = await Assert.ThrowsAsync<GenerationOperationException>(
                async () => await store.ImportAsync(
                    "too-large",
                    0,
                    new GenerationArtifactSource
                    {
                        InlineData = new byte[] { 1 },
                        MediaType = "application/octet-stream",
                        SizeBytes = 2_048
                    },
                    CancellationToken.None));
            Assert.Equal("generation_artifact_too_large", exception.ReasonCode);
            Assert.Empty(Directory.GetFiles(root, ".tmp-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Artifact_store_does_not_trust_loopback_urls_by_default()
    {
        var root = TempDirectory();
        try
        {
            using var store = new FileGenerationArtifactStore(
                new FileGenerationArtifactStoreOptions
                {
                    RootDirectory = root
                });

            var exception = await Assert.ThrowsAsync<GenerationOperationException>(
                async () => await store.ImportAsync(
                    "loopback-source",
                    0,
                    new GenerationArtifactSource
                    {
                        RemoteUri = new Uri("https://localhost/private-artifact"),
                        MediaType = "application/octet-stream"
                    },
                    CancellationToken.None));

            Assert.Equal(
                "generation_artifact_host_not_allowed",
                exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Artifact_store_rejects_a_null_remote_host_allowlist()
    {
        var root = TempDirectory();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new FileGenerationArtifactStore(
                    new FileGenerationArtifactStoreOptions
                    {
                        RootDirectory = root,
                        AllowedRemoteHosts = null!
                    }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GenerationJob ValidJob(string operationId) => new()
    {
        OperationId = operationId,
        RequestDigest = new string('a', 64),
        Modality = GenerationModalities.Image,
        Provider = "fake",
        Acceptance = GenerationAcceptance.Accepted,
        Status = GenerationJobStatuses.Succeeded,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Output = Json("{\"ok\":true}"),
        Revision = 1
    };

    private static GeneratedContentManifest Manifest() => new()
    {
        ContentId = "content-1",
        Kind = "scene",
        Version = "1.0.0",
        SourceOperationId = "generation-1",
        Data = Json("{\"nodes\":[]}"),
        Provenance = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = "configured-provider"
        }
    };

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "game-agent-generation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecoveringContentHost : IGeneratedContentHost
    {
        public bool FailValidationOnce { get; set; }

        public string ReportedState { get; set; } = ContentTransactionStates.Committed;

        public int StageCalls { get; private set; }

        public int ValidationCalls { get; private set; }

        public int CommitCalls { get; private set; }

        public ValueTask StageAsync(
            string transactionId,
            GeneratedContentManifest manifest,
            CancellationToken cancellationToken)
        {
            StageCalls++;
            return default;
        }

        public ValueTask<ContentValidationResult> ValidateAsync(
            string transactionId,
            GeneratedContentManifest manifest,
            CancellationToken cancellationToken)
        {
            ValidationCalls++;
            if (FailValidationOnce)
            {
                FailValidationOnce = false;
                throw new IOException("injected validation interruption");
            }

            return new ValueTask<ContentValidationResult>(new ContentValidationResult
            {
                Accepted = true
            });
        }

        public ValueTask<ContentCommitResult> CommitAsync(
            string transactionId,
            GeneratedContentManifest manifest,
            CancellationToken cancellationToken)
        {
            CommitCalls++;
            return new ValueTask<ContentCommitResult>(new ContentCommitResult
            {
                HostReceiptId = "receipt-1",
                Result = Json("{\"installed\":true}")
            });
        }

        public ValueTask AbortAsync(
            string transactionId,
            GeneratedContentManifest manifest,
            CancellationToken cancellationToken) => default;

        public ValueTask<ContentHostStatus> GetStatusAsync(
            string transactionId,
            CancellationToken cancellationToken) =>
            new(new ContentHostStatus
            {
                State = ReportedState,
                HostReceiptId = ReportedState == ContentTransactionStates.Committed
                    ? "receipt-1"
                    : null
            });
    }
}
