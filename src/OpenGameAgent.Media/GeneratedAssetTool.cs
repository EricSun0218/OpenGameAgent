using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Media;

public delegate GameGeneratedAssetRequest GameGeneratedAssetRequestFactory(
    GameInput input,
    JsonElement arguments,
    ToolExecutionContext execution);

public static class GameGeneratedAssetTool
{
    public static AgentTool Create(
        GameInput input,
        string name,
        string description,
        string inputSchemaJson,
        GameGeneratedAssetPipeline pipeline,
        IGameMediaGenerator generator,
        IGameGeneratedAssetImporter importer,
        GameGeneratedAssetRequestFactory requestFactory)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (pipeline is null)
        {
            throw new ArgumentNullException(nameof(pipeline));
        }

        if (generator is null)
        {
            throw new ArgumentNullException(nameof(generator));
        }

        if (importer is null)
        {
            throw new ArgumentNullException(nameof(importer));
        }

        if (requestFactory is null)
        {
            throw new ArgumentNullException(nameof(requestFactory));
        }

        return new AgentTool(
            new ToolDefinition(name, description, inputSchemaJson),
            async (arguments, execution, cancellationToken) =>
            {
                var request = requestFactory(input, arguments, execution)
                    ?? throw new InvalidOperationException("The generated asset request factory returned null.");
                if (request.Owner != new GameSessionKey(input.SessionId, input.ActorId))
                {
                    throw new InvalidOperationException(
                        "The generated asset request owner must match the current game input.");
                }

                var job = await pipeline.ExecuteAsync(
                    request,
                    generator,
                    importer,
                    async (progress, token) =>
                    {
                        if (progress is null)
                        {
                            throw new InvalidOperationException("The media generator reported null progress.");
                        }

                        await execution.ReportProgressAsync(
                            new ToolProgress(progress.Stage, progress.Fraction, progress.DetailsJson),
                            token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);

                var details = Serialize(job);
                var uncertain = job.Status is GameGeneratedAssetStatus.Generating
                    or GameGeneratedAssetStatus.Generated
                    or GameGeneratedAssetStatus.Importing
                    or GameGeneratedAssetStatus.GenerationUncertain
                    or GameGeneratedAssetStatus.ImportUncertain;
                var error = job.Status != GameGeneratedAssetStatus.Completed;
                return new ToolResult(
                    new AgentContent[] { new JsonContent(details) },
                    isError: error,
                    detailsJson: details,
                    outcomeUncertain: uncertain);
            },
            ToolRisk.NonIdempotentWrite,
            ToolExecutionMode.SafeParallel,
            conflictKey: _ => input.SessionId + ":" + input.ActorId + ":generated-assets");
    }

    private static string Serialize(GameGeneratedAssetJob job) => JsonSerializer.Serialize(new
    {
        operationId = job.OperationId,
        status = job.Status.ToString(),
        assetId = job.Manifest?.AssetId,
        resources = job.Manifest?.Resources.Select(static resource => new
        {
            resourceId = resource.ResourceId,
            sha256 = resource.Sha256,
            mediaType = resource.MediaType,
            bytes = resource.Bytes,
            name = resource.Name,
        }),
        import = job.ImportReceipt is null
            ? null
            : new
            {
                operationId = job.ImportReceipt.OperationId,
                outcome = job.ImportReceipt.Outcome.ToString(),
                stateRevision = job.ImportReceipt.StateRevision,
                code = job.ImportReceipt.Code,
                message = job.ImportReceipt.Message,
            },
        error = job.ErrorCode is null && job.ErrorMessage is null
            ? null
            : new { code = job.ErrorCode, message = job.ErrorMessage },
    });
}
