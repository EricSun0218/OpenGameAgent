using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public enum GameMediaKind
{
    Image,
    Audio,
    Video,
}

public sealed class GameMediaGenerationRequest
{
    public GameMediaGenerationRequest(
        string requestId,
        GameMediaKind kind,
        string contextJson,
        string parametersJson = "{}",
        string? prompt = null,
        IReadOnlyList<ResourceContent>? sources = null)
    {
        RequestId = GameJson.RequireId(requestId, nameof(requestId));
        if (!Enum.IsDefined(typeof(GameMediaKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ContextJson = GameJson.RequireValid(contextJson, nameof(contextJson));
        ParametersJson = GameJson.RequireValid(parametersJson, nameof(parametersJson));
        Prompt = prompt;
        var copiedSources = (sources ?? Array.Empty<ResourceContent>()).ToArray();
        if (copiedSources.Any(source => source is null))
        {
            throw new ArgumentException("Media sources cannot contain null resources.", nameof(sources));
        }

        Sources = Array.AsReadOnly(copiedSources);
    }

    public string RequestId { get; }

    public GameMediaKind Kind { get; }

    public string ContextJson { get; }

    public string ParametersJson { get; }

    public string? Prompt { get; }

    public IReadOnlyList<ResourceContent> Sources { get; }
}

public sealed class GameMediaGenerationProgress
{
    public GameMediaGenerationProgress(string stage, double? fraction = null, string? detailsJson = null)
    {
        if (fraction is < 0 or > 1 || double.IsNaN(fraction ?? 0) || double.IsInfinity(fraction ?? 0))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }

        Stage = GameJson.RequireId(stage, nameof(stage));
        Fraction = fraction;
        DetailsJson = detailsJson is null ? null : GameJson.RequireValid(detailsJson, nameof(detailsJson));
    }

    public string Stage { get; }

    public double? Fraction { get; }

    public string? DetailsJson { get; }
}

public sealed class GameMediaGenerationResult
{
    public GameMediaGenerationResult(
        IReadOnlyList<ResourceContent> outputs,
        string metadataJson = "{}",
        string? providerRequestId = null)
    {
        var copiedOutputs = (outputs ?? throw new ArgumentNullException(nameof(outputs))).ToArray();
        if (copiedOutputs.Length == 0)
        {
            throw new ArgumentException("Media generation must return at least one output.", nameof(outputs));
        }

        if (copiedOutputs.Any(output => output is null))
        {
            throw new ArgumentException("Media outputs cannot contain null resources.", nameof(outputs));
        }

        Outputs = Array.AsReadOnly(copiedOutputs);
        MetadataJson = GameJson.RequireValid(metadataJson, nameof(metadataJson));
        ProviderRequestId = providerRequestId is null
            ? null
            : GameJson.RequireId(providerRequestId, nameof(providerRequestId));
    }

    public IReadOnlyList<ResourceContent> Outputs { get; }

    public string MetadataJson { get; }

    public string? ProviderRequestId { get; }
}

public delegate ValueTask GameMediaProgressHandler(
    GameMediaGenerationProgress progress,
    CancellationToken cancellationToken);

public interface IGameMediaGenerator
{
    ValueTask<GameMediaGenerationResult> GenerateAsync(
        GameMediaGenerationRequest request,
        GameMediaProgressHandler? progress,
        CancellationToken cancellationToken);
}

public delegate GameMediaGenerationRequest GameMediaRequestFactory(
    GameInput input,
    JsonElement arguments,
    ToolExecutionContext execution);

public static class GameMediaGenerationTool
{
    public static AgentTool Create(
        GameInput input,
        string name,
        string description,
        string inputSchemaJson,
        IGameMediaGenerator generator,
        GameMediaRequestFactory requestFactory)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (generator is null)
        {
            throw new ArgumentNullException(nameof(generator));
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
                    ?? throw new InvalidOperationException("The media request factory returned null.");
                var result = await generator.GenerateAsync(
                    request,
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
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The media generator returned null.");
                var content = result.Outputs.Cast<AgentContent>().ToList();
                content.Add(new JsonContent(JsonSerializer.Serialize(new
                {
                    requestId = request.RequestId,
                    providerRequestId = result.ProviderRequestId,
                    metadata = GameJson.ParseElement(result.MetadataJson),
                })));
                return new ToolResult(content, detailsJson: result.MetadataJson);
            },
            ToolRisk.NonIdempotentWrite,
            ToolExecutionMode.SafeParallel,
            conflictKey: _ => input.ActorId + ":media");
    }
}
