using System.Text.Json;

namespace OpenGameAgent.Media;

public delegate GameActionIntent GameGeneratedAssetActionFactory(GameGeneratedAssetImportContext context);

/// <summary>
/// Imports generated assets through the same durable action journal used by authoritative game
/// mutations. This is the recommended bridge for a remote engine client: generation stays in the
/// sidecar while the game validates and commits the final asset reference.
/// </summary>
public sealed class GameGeneratedAssetActionImporter : IGameGeneratedAssetImporter
{
    private readonly DurableGameActionDispatcher _dispatcher;
    private readonly GameGeneratedAssetActionFactory _factory;

    public GameGeneratedAssetActionImporter(
        string importerId,
        DurableGameActionDispatcher dispatcher,
        GameGeneratedAssetActionFactory factory)
    {
        ImporterId = GeneratedAssetValidation.RequireId(importerId, nameof(importerId));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public string ImporterId { get; }

    public ValueTask<GameGeneratedAssetImportReceipt> ImportAsync(
        GameGeneratedAssetImportContext context,
        CancellationToken cancellationToken) => ExecuteOrRecoverAsync(context, cancellationToken);

    public ValueTask<GameGeneratedAssetImportReceipt> RecoverAsync(
        GameGeneratedAssetImportContext context,
        CancellationToken cancellationToken) => ExecuteOrRecoverAsync(context, cancellationToken);

    public static string CreateManifestArgumentsJson(GameGeneratedAssetImportContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return JsonSerializer.Serialize(new
        {
            assetId = context.Manifest.AssetId,
            assetType = context.Job.AssetType,
            generator = context.Job.GeneratorId,
            model = context.Job.ModelId,
            metadata = ParseElement(context.Job.RequestMetadataJson),
            generationMetadata = ParseElement(context.Manifest.MetadataJson),
            resources = context.Manifest.Resources.Select(static resource => new
            {
                resourceId = resource.ResourceId,
                sha256 = resource.Sha256,
                mediaType = resource.MediaType,
                bytes = resource.Bytes,
                name = resource.Name,
            }),
        });
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async ValueTask<GameGeneratedAssetImportReceipt> ExecuteOrRecoverAsync(
        GameGeneratedAssetImportContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var intent = _factory(context)
            ?? throw new InvalidOperationException("The generated asset action factory returned null.");
        ValidateIntent(context, intent);
        var receipt = await _dispatcher.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false);
        return new GameGeneratedAssetImportReceipt(
            context.ImportOperationId,
            receipt.Status switch
            {
                GameActionStatus.Committed => GameGeneratedAssetImportOutcome.Committed,
                GameActionStatus.Rejected => GameGeneratedAssetImportOutcome.Rejected,
                GameActionStatus.Failed => GameGeneratedAssetImportOutcome.Failed,
                GameActionStatus.Uncertain => GameGeneratedAssetImportOutcome.Uncertain,
                _ => throw new InvalidOperationException("The game action returned an unknown status."),
            },
            receipt.ResultJson,
            receipt.StateRevision,
            receipt.Code,
            receipt.Message);
    }

    private static void ValidateIntent(
        GameGeneratedAssetImportContext context,
        GameActionIntent intent)
    {
        if (!string.Equals(intent.OperationId, context.ImportOperationId, StringComparison.Ordinal)
            || !string.Equals(intent.SessionId, context.Job.Owner.SessionId, StringComparison.Ordinal)
            || !string.Equals(intent.ActorId, context.Job.Owner.ActorId, StringComparison.Ordinal)
            || intent.Moment != context.Job.Moment)
        {
            throw new InvalidOperationException(
                "The generated asset action must preserve the import operation, owner, and game moment.");
        }
    }
}
