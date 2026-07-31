using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public sealed class InteractionExecutionRequest
{
    private readonly JsonElement _parameters;

    public InteractionExecutionRequest(
        string commandId,
        string idempotencyKey,
        string worldId,
        string timelineId,
        long timelineEpoch,
        long expectedSaveRevision,
        string expectedStateVersion,
        string catalogDigest,
        string interactionId,
        string interactionVersion,
        GameEntityIdentity actor,
        IEnumerable<GameEntityIdentity>? targets,
        string channelId,
        JsonElement parameters,
        IEnumerable<string>? capabilityTags = null,
        string? confirmationToken = null,
        GameTimePoint? gameTime = null)
    {
        CommandId = WorldValidation.Required(
            commandId,
            nameof(commandId));
        IdempotencyKey = WorldValidation.Required(
            idempotencyKey,
            nameof(idempotencyKey),
            512);
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (expectedSaveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSaveRevision));
        }

        ExpectedStateVersion = WorldValidation.Required(
            expectedStateVersion,
            nameof(expectedStateVersion));
        if (!CanonicalJsonDigest.IsSha256(catalogDigest))
        {
            throw new ArgumentException(
                "The catalog digest must be a lowercase SHA-256 digest.",
                nameof(catalogDigest));
        }

        InteractionId = WorldValidation.Required(
            interactionId,
            nameof(interactionId));
        InteractionVersion = WorldValidation.Required(
            interactionVersion,
            nameof(interactionVersion),
            96);
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        Targets = InteractionIdentityList.Copy(
            targets,
            nameof(targets),
            maximumCount: 64);
        ChannelId = WorldValidation.Required(channelId, nameof(channelId));
        JsonValueInspector.ValidateAndMeasure(
            parameters,
            InteractionJsonLimits.Parameters,
            nameof(parameters));
        _parameters = parameters.Clone();
        CapabilityTags = WorldValidation.CopyKeys(
            capabilityTags,
            nameof(capabilityTags),
            maximumCount: 128);
        ConfirmationToken = WorldValidation.Optional(
            confirmationToken,
            nameof(confirmationToken),
            512);
        if (gameTime is not null
            && (!string.Equals(
                    gameTime.TimelineId,
                    timelineId,
                    StringComparison.Ordinal)
                || gameTime.Epoch != timelineEpoch))
        {
            throw new ArgumentException(
                "Game time must use the request timeline and epoch.",
                nameof(gameTime));
        }

        TimelineEpoch = timelineEpoch;
        ExpectedSaveRevision = expectedSaveRevision;
        CatalogDigest = catalogDigest;
        GameTime = gameTime;
    }

    public string CommandId { get; }

    public string IdempotencyKey { get; }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long ExpectedSaveRevision { get; }

    public string ExpectedStateVersion { get; }

    public string CatalogDigest { get; }

    public string InteractionId { get; }

    public string InteractionVersion { get; }

    public GameEntityIdentity Actor { get; }

    public IReadOnlyList<GameEntityIdentity> Targets { get; }

    public string ChannelId { get; }

    public JsonElement Parameters => _parameters.Clone();

    public IReadOnlyList<string> CapabilityTags { get; }

    public string? ConfirmationToken { get; }

    public GameTimePoint? GameTime { get; }
}

/// <summary>
/// A selected interaction represented as one root world trigger. This type is
/// only evaluator input; constructing it does not reserve or mutate state.
/// </summary>
public sealed class InteractionExecutionTrigger : WorldEvolutionTrigger
{
    internal InteractionExecutionTrigger(
        InteractionExecutionRequest request,
        InteractionDefinition definition)
        : base(
            request.CommandId,
            WorldInteractionKinds.Requested,
            request.WorldId,
            request.TimelineId,
            request.TimelineEpoch,
            request.GameTime,
            BuildPayload(request, definition))
    {
        InteractionId = definition.InteractionId;
        InteractionVersion = definition.Version;
        CatalogDigest = request.CatalogDigest;
        Actor = request.Actor;
        Targets = request.Targets;
        ChannelId = request.ChannelId;
        InputSchemaId = definition.InputSchemaId;
        Parameters = Payload!.Value.GetProperty("parameters").Clone();
        IdempotencyKey = request.IdempotencyKey;
        ConfirmationToken = request.ConfirmationToken;
        ExpectedSaveRevision = request.ExpectedSaveRevision;
        ExpectedStateVersion = request.ExpectedStateVersion;
    }

    public string InteractionId { get; }

    public string InteractionVersion { get; }

    public string CatalogDigest { get; }

    public GameEntityIdentity Actor { get; }

    public IReadOnlyList<GameEntityIdentity> Targets { get; }

    public string ChannelId { get; }

    public string InputSchemaId { get; }

    public JsonElement Parameters { get; }

    public string IdempotencyKey { get; }

    public string? ConfirmationToken { get; }

    public long ExpectedSaveRevision { get; }

    public string ExpectedStateVersion { get; }

    private static JsonElement BuildPayload(
        InteractionExecutionRequest request,
        InteractionDefinition definition)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "interactionId",
                definition.InteractionId);
            writer.WriteString(
                "interactionVersion",
                definition.Version);
            writer.WriteString(
                "definitionDigest",
                definition.ContentDigest);
            writer.WriteString("catalogDigest", request.CatalogDigest);
            writer.WriteString(
                "idempotencyKey",
                request.IdempotencyKey);
            writer.WriteString(
                "expectedSaveRevision",
                request.ExpectedSaveRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString(
                "expectedStateVersion",
                request.ExpectedStateVersion);
            WriteIdentity(writer, "actor", request.Actor);
            writer.WritePropertyName("targets");
            writer.WriteStartArray();
            foreach (var target in request.Targets)
            {
                WriteIdentity(writer, null, target);
            }

            writer.WriteEndArray();
            writer.WriteString("channelId", request.ChannelId);
            writer.WriteString(
                "inputSchemaId",
                definition.InputSchemaId);
            writer.WritePropertyName("capabilityTags");
            writer.WriteStartArray();
            foreach (var capability in request.CapabilityTags)
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            if (request.ConfirmationToken is not null)
            {
                writer.WriteString(
                    "confirmationToken",
                    request.ConfirmationToken);
            }

            writer.WritePropertyName("parameters");
            request.Parameters.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteIdentity(
        Utf8JsonWriter writer,
        string? propertyName,
        GameEntityIdentity identity)
    {
        if (propertyName is not null)
        {
            writer.WritePropertyName(propertyName);
        }

        writer.WriteStartObject();
        writer.WriteString("entityId", identity.EntityId);
        writer.WriteString(
            "incarnation",
            identity.Incarnation.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }
}

public sealed class CompiledInteractionExecution
{
    internal CompiledInteractionExecution(
        InteractionExecutionTrigger trigger,
        WorldEventDefinition rootEventDefinition)
    {
        Trigger = trigger;
        RootEventDefinition = rootEventDefinition;
    }

    public InteractionExecutionTrigger Trigger { get; }

    public WorldEventDefinition RootEventDefinition { get; }
}

public sealed class InteractionCompilationResult
{
    private InteractionCompilationResult(
        bool succeeded,
        CompiledInteractionExecution? execution,
        string reasonCode,
        IReadOnlyList<InteractionParameterValidationError>? errors)
    {
        Succeeded = succeeded;
        Execution = execution;
        ReasonCode = reasonCode;
        ParameterErrors = errors
                          ?? Array.Empty<
                              InteractionParameterValidationError>();
    }

    public bool Succeeded { get; }

    public CompiledInteractionExecution? Execution { get; }

    public string ReasonCode { get; }

    public IReadOnlyList<InteractionParameterValidationError>
        ParameterErrors
    { get; }

    internal static InteractionCompilationResult Success(
        CompiledInteractionExecution execution)
    {
        return new InteractionCompilationResult(
            true,
            execution,
            string.Empty,
            null);
    }

    internal static InteractionCompilationResult Failure(
        string reasonCode,
        IReadOnlyList<InteractionParameterValidationError>? errors = null)
    {
        return new InteractionCompilationResult(
            false,
            null,
            reasonCode,
            errors);
    }
}

public static class InteractionExecutionCompiler
{
    public static InteractionCompilationResult Compile(
        InteractionCatalogSnapshot catalog,
        InteractionExecutionRequest request)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!string.Equals(
                catalog.Digest,
                request.CatalogDigest,
                StringComparison.Ordinal))
        {
            return InteractionCompilationResult.Failure(
                InteractionReasonCodes.StaleCatalog);
        }

        var definition = catalog.Find(
            request.InteractionId,
            request.InteractionVersion);
        if (definition is null)
        {
            return InteractionCompilationResult.Failure(
                InteractionReasonCodes.DefinitionNotFound);
        }

        var details = definition.Details;
        if (details is not null)
        {
            if (details.ChannelIds.Count > 0
                && !details.ChannelIds.Contains(
                    request.ChannelId,
                    StringComparer.Ordinal))
            {
                return InteractionCompilationResult.Failure(
                    InteractionReasonCodes.UnsupportedChannel);
            }

            if (details.RequiredCapabilities.Any(
                    capability => !request.CapabilityTags.Contains(
                        capability,
                        StringComparer.Ordinal)))
            {
                return InteractionCompilationResult.Failure(
                    InteractionReasonCodes.CapabilityUnavailable);
            }

            var targetContract = details.TargetContract;
            var minimumTargets = targetContract?.MinimumTargets ?? 0;
            var maximumTargets = targetContract?.MaximumTargets ?? 0;
            if (request.Targets.Count < minimumTargets
                || request.Targets.Count > maximumTargets)
            {
                return InteractionCompilationResult.Failure(
                    InteractionReasonCodes.InvalidTargetCount);
            }

            var parameterValidation =
                details.ParameterContract.Validate(request.Parameters);
            if (!parameterValidation.IsValid)
            {
                return InteractionCompilationResult.Failure(
                    InteractionReasonCodes.InvalidParameters,
                    parameterValidation.Errors);
            }
        }

        return InteractionCompilationResult.Success(
            new CompiledInteractionExecution(
                new InteractionExecutionTrigger(request, definition),
                definition.ToEventDefinition()));
    }
}
