using OpenGameAgent;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;

var jobs = new InMemoryGameGeneratedAssetJobStore();
var resources = new InMemoryGameGeneratedAssetResourceStore();
var pipeline = new GameGeneratedAssetPipeline(jobs, resources);
var engine = new ExampleEngineImporter();
var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), engine);
var importer = new GameGeneratedAssetActionImporter(
    "example-engine-importer",
    dispatcher,
    context => new GameActionIntent(
        context.ImportOperationId,
        context.Job.OperationId,
        context.Job.Owner.SessionId,
        context.Job.Owner.ActorId,
        "import_generated_asset",
        GameGeneratedAssetActionImporter.CreateManifestArgumentsJson(context),
        context.Job.Moment,
        conflictKey: context.Job.Owner.SessionId + ":generated-assets"));

var request = new GameGeneratedAssetRequest(
    operationId: "example-generated-lamp-v1",
    owner: new GameSessionKey("example-save", "builder"),
    assetType: "placeable",
    moment: new GameMoment("example-world", 120),
    generatorId: "offline-example",
    modelId: "deterministic-pixel",
    importerId: importer.ImporterId,
    generation: new GameMediaGenerationRequest(
        "example-image-request",
        GameMediaKind.Image,
        contextJson: "{\"biome\":\"forest\",\"palette\":\"warm\"}",
        parametersJson: "{\"size\":\"1x1\"}",
        prompt: "a small wooden lamp"));

var completed = await pipeline.ExecuteAsync(
    request,
    new OfflinePixelGenerator(),
    importer,
    (progress, _) =>
    {
        Console.WriteLine($"Generation: {progress.Stage} {progress.Fraction:P0}");
        return default;
    });

Console.WriteLine($"Asset status: {completed.Status}");
Console.WriteLine($"Manifest: {completed.Manifest?.AssetId}");
Console.WriteLine($"Engine revision: {completed.ImportReceipt?.StateRevision}");

internal sealed class OfflinePixelGenerator : IGameMediaGenerator
{
    // A deterministic one-pixel PNG keeps the example offline and key-free.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public async ValueTask<GameMediaGenerationResult> GenerateAsync(
        GameMediaGenerationRequest request,
        GameMediaProgressHandler? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (progress is not null)
        {
            await progress(new GameMediaGenerationProgress("rendering", 0.5), cancellationToken);
        }

        return new GameMediaGenerationResult(new[]
        {
            new ResourceContent(
                "data:image/png;base64," + Convert.ToBase64String(Pixel),
                "image/png",
                "lamp.png"),
        });
    }
}

internal sealed class ExampleEngineImporter : IGameActionHandler
{
    private readonly Dictionary<string, GameActionReceipt> _receipts = new(StringComparer.Ordinal);
    private long _revision;

    public ValueTask<GameActionReceipt> ExecuteAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_receipts.TryGetValue(intent.OperationId, out var existing))
        {
            return new ValueTask<GameActionReceipt>(existing);
        }

        // A real Unity, Godot, or Unreal adapter validates the manifest, reads each resource,
        // creates engine assets, commits them to authoritative state, then stores this receipt.
        var revision = Interlocked.Increment(ref _revision);
        var receipt = GameActionReceipt.Committed(
            intent,
            "{\"engineAssetId\":\"game://placeables/lamp\"}",
            revision);
        _receipts.Add(intent.OperationId, receipt);
        return new ValueTask<GameActionReceipt>(receipt);
    }

    public ValueTask<GameActionReceipt?> RecoverAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<GameActionReceipt?>(
            _receipts.TryGetValue(intent.OperationId, out var receipt) ? receipt : null);
    }
}
