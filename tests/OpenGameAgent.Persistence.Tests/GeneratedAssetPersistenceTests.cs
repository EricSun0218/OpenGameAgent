using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class GeneratedAssetPersistenceTests
{
    [Fact]
    public async Task CompletedAssetAndResourcesRoundTripAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = Request("persistent-asset");
        var first = new GameGeneratedAssetPipeline(
            new FileGameGeneratedAssetJobStore(directory.Jobs),
            new FileGameGeneratedAssetResourceStore(directory.Resources));
        var generator = new TestGenerator();
        var importer = new TestImporter();
        var completed = await first.ExecuteAsync(
            request,
            generator,
            importer,
            cancellationToken: cancellationToken);

        var restartedResources = new FileGameGeneratedAssetResourceStore(directory.Resources);
        var restarted = new GameGeneratedAssetPipeline(
            new FileGameGeneratedAssetJobStore(directory.Jobs),
            restartedResources);
        var loaded = await restarted.LoadAsync(request.Owner, request.OperationId, cancellationToken);
        var repeated = await restarted.ExecuteAsync(
            request,
            generator,
            importer,
            cancellationToken: cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.Completed, loaded?.Status);
        Assert.Equal(completed.Revision, repeated.Revision);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1, importer.Calls);
        var resource = Assert.Single(loaded!.Manifest!.Resources);
        var bytes = await restartedResources.ReadAsync(resource, cancellationToken);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, bytes.Data);
    }

    [Fact]
    public async Task SeparateStoreInstancesUseRevisionCas()
    {
        using var directory = new TemporaryDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = Request("cas-asset");
        var first = new FileGameGeneratedAssetJobStore(directory.Jobs);
        var second = new FileGameGeneratedAssetJobStore(directory.Jobs);
        var prepared = Prepared(request);
        Assert.True((await first.SaveAsync(prepared, 0, cancellationToken)).Saved);

        var generating = prepared.AdvanceForTest(GameGeneratedAssetStatus.Generating);
        var firstSave = first.SaveAsync(generating, 1, cancellationToken).AsTask();
        var secondSave = second.SaveAsync(generating, 1, cancellationToken).AsTask();
        var results = await Task.WhenAll(firstSave, secondSave);

        Assert.Single(results, result => result.Saved);
        Assert.Single(results, result => !result.Saved);
        Assert.All(results, result => Assert.Equal(2, result.Current.Revision));
    }

    [Fact]
    public async Task UncertainImportRecoversAfterRestartWithoutRegeneration()
    {
        using var directory = new TemporaryDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = Request("restart-import-asset");
        var generator = new TestGenerator();
        var importer = new RestartImporter();
        var first = new GameGeneratedAssetPipeline(
            new FileGameGeneratedAssetJobStore(directory.Jobs),
            new FileGameGeneratedAssetResourceStore(directory.Resources));

        var uncertain = await first.ExecuteAsync(
            request,
            generator,
            importer,
            cancellationToken: cancellationToken);
        var restarted = new GameGeneratedAssetPipeline(
            new FileGameGeneratedAssetJobStore(directory.Jobs),
            new FileGameGeneratedAssetResourceStore(directory.Resources));
        var completed = await restarted.ResumeImportAsync(
            request.Owner,
            request.OperationId,
            importer,
            cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.ImportUncertain, uncertain.Status);
        Assert.Equal(GameGeneratedAssetStatus.Completed, completed.Status);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1, importer.ImportCalls);
        Assert.Equal(1, importer.RecoverCalls);
    }

    [Fact]
    public async Task CorruptJobAndResourceFailClosed()
    {
        using var directory = new TemporaryDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = Request("corrupt-asset");
        var jobStore = new FileGameGeneratedAssetJobStore(directory.Jobs);
        await jobStore.SaveAsync(Prepared(request), 0, cancellationToken);
        var jobPath = Assert.Single(Directory.GetFiles(directory.Jobs, "*.generated-asset.json"));
        await File.WriteAllTextAsync(jobPath, "{\"FormatVersion\":1,\"FormatVersion\":1}", cancellationToken);
        await Assert.ThrowsAsync<PersistenceException>(() =>
            jobStore.LoadAsync(request.Owner, request.OperationId, cancellationToken).AsTask());

        var resourceStore = new FileGameGeneratedAssetResourceStore(directory.Resources);
        var saved = await resourceStore.SaveAsync(
            request.OperationId,
            0,
            new GameGeneratedAssetBinary(new byte[] { 1, 2, 3 }, "image/png"),
            cancellationToken);
        var resourcePath = Assert.Single(Directory.GetFiles(directory.Resources, "*.generated-asset.bin"));
        await File.WriteAllBytesAsync(resourcePath, new byte[] { 9, 9, 9 }, cancellationToken);
        await Assert.ThrowsAsync<PersistenceException>(() =>
            resourceStore.ReadAsync(saved, cancellationToken).AsTask());
    }

    private static GameGeneratedAssetRequest Request(string operationId) => new(
        operationId,
        new GameSessionKey("session", "actor"),
        "generated-placeable",
        new GameMoment("timeline", 8),
        "generator",
        "model",
        "importer",
        new GameMediaGenerationRequest(
            "request-" + operationId,
            GameMediaKind.Image,
            "{}",
            "{}",
            "a small lamp"));

    private static GameGeneratedAssetJob Prepared(GameGeneratedAssetRequest request) => new(
        request.OperationId,
        request.Owner,
        request.AssetType,
        request.Moment,
        request.GeneratorId,
        request.ModelId,
        request.ImporterId,
        request.Fingerprint,
        request.MetadataJson,
        request.Generation.Kind,
        1,
        GameGeneratedAssetStatus.Prepared);

    private sealed class TestGenerator : IGameMediaGenerator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            var bytes = new byte[] { 10, 20, 30, 40 };
            return new ValueTask<GameMediaGenerationResult>(new GameMediaGenerationResult(
                new[]
                {
                    new ResourceContent(
                        "data:image/png;base64," + Convert.ToBase64String(bytes),
                        "image/png",
                        "lamp.png"),
                }));
        }
    }

    private sealed class TestImporter : IGameGeneratedAssetImporter
    {
        private int _calls;

        public string ImporterId => "importer";

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<GameGeneratedAssetImportReceipt> ImportAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return new ValueTask<GameGeneratedAssetImportReceipt>(new GameGeneratedAssetImportReceipt(
                context.ImportOperationId,
                GameGeneratedAssetImportOutcome.Committed,
                "{\"asset\":\"lamp\"}"));
        }

        public ValueTask<GameGeneratedAssetImportReceipt> RecoverAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken) => ImportAsync(context, cancellationToken);
    }

    private sealed class RestartImporter : IGameGeneratedAssetImporter
    {
        private int _importCalls;
        private int _recoverCalls;

        public string ImporterId => "importer";

        public int ImportCalls => Volatile.Read(ref _importCalls);

        public int RecoverCalls => Volatile.Read(ref _recoverCalls);

        public ValueTask<GameGeneratedAssetImportReceipt> ImportAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _importCalls);
            throw new IOException("The engine connection closed after dispatch.");
        }

        public ValueTask<GameGeneratedAssetImportReceipt> RecoverAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _recoverCalls);
            return new ValueTask<GameGeneratedAssetImportReceipt>(new GameGeneratedAssetImportReceipt(
                context.ImportOperationId,
                GameGeneratedAssetImportOutcome.Committed,
                "{\"asset\":\"recovered\"}"));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenGameAgent.Tests",
                Guid.NewGuid().ToString("N"));
            Jobs = System.IO.Path.Combine(Path, "jobs");
            Resources = System.IO.Path.Combine(Path, "resources");
            Directory.CreateDirectory(Jobs);
            Directory.CreateDirectory(Resources);
        }

        public string Path { get; }

        public string Jobs { get; }

        public string Resources { get; }

        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenGameAgent.Tests"));
            var target = System.IO.Path.GetFullPath(Path);
            if (target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}

internal static class GeneratedAssetPersistenceTestExtensions
{
    internal static GameGeneratedAssetJob AdvanceForTest(
        this GameGeneratedAssetJob job,
        GameGeneratedAssetStatus status) => new(
            job.OperationId,
            job.Owner,
            job.AssetType,
            job.Moment,
            job.GeneratorId,
            job.ModelId,
            job.ImporterId,
            job.RequestFingerprint,
            job.RequestMetadataJson,
            job.MediaKind,
            job.Revision + 1,
            status,
            job.Manifest,
            job.ImportReceipt,
            job.ErrorCode,
            job.ErrorMessage);
}
