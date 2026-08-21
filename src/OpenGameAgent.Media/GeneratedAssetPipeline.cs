using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Media;

public enum GameGeneratedAssetStatus
{
    Unknown = 0,
    Prepared = 1,
    Generating = 2,
    Generated = 3,
    Importing = 4,
    Completed = 5,
    Rejected = 6,
    Failed = 7,
    GenerationUncertain = 8,
    ImportUncertain = 9,
}

public enum GameGeneratedAssetImportOutcome
{
    Unknown = 0,
    Committed = 1,
    Rejected = 2,
    Failed = 3,
    Uncertain = 4,
}

public sealed class GameGeneratedAssetRequest
{
    public GameGeneratedAssetRequest(
        string operationId,
        GameSessionKey owner,
        string assetType,
        GameMoment moment,
        string generatorId,
        string modelId,
        string importerId,
        GameMediaGenerationRequest generation,
        string metadataJson = "{}")
    {
        OperationId = GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        Owner = GeneratedAssetValidation.RequireOwner(owner, nameof(owner));
        AssetType = GeneratedAssetValidation.RequireId(assetType, nameof(assetType));
        Moment = GeneratedAssetValidation.RequireMoment(moment, nameof(moment));
        GeneratorId = GeneratedAssetValidation.RequireId(generatorId, nameof(generatorId));
        ModelId = GeneratedAssetValidation.RequireId(modelId, nameof(modelId));
        ImporterId = GeneratedAssetValidation.RequireId(importerId, nameof(importerId));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        MetadataJson = GeneratedAssetValidation.RequireJson(metadataJson, nameof(metadataJson));
        GeneratedAssetValidation.ValidateGenerationRequest(generation, nameof(generation));
        Fingerprint = GeneratedAssetValidation.Fingerprint(this);
    }

    public string OperationId { get; }

    public GameSessionKey Owner { get; }

    public string AssetType { get; }

    public GameMoment Moment { get; }

    public string GeneratorId { get; }

    public string ModelId { get; }

    public string ImporterId { get; }

    public GameMediaGenerationRequest Generation { get; }

    public string MetadataJson { get; }

    public string Fingerprint { get; }
}

public sealed class GameGeneratedAssetBinary
{
    public GameGeneratedAssetBinary(byte[] data, string mediaType, string? name = null)
    {
        if (data is null || data.Length == 0)
        {
            throw new ArgumentException("Generated asset data is required.", nameof(data));
        }

        Data = Array.AsReadOnly(data.ToArray());
        MediaType = GeneratedAssetValidation.RequireMediaType(mediaType, nameof(mediaType));
        Name = GeneratedAssetValidation.OptionalText(name, 1_024, nameof(name));
    }

    public IReadOnlyList<byte> Data { get; }

    public string MediaType { get; }

    public string? Name { get; }
}

public sealed class GameGeneratedAssetResource
{
    public GameGeneratedAssetResource(
        string resourceId,
        string sha256,
        string mediaType,
        long bytes,
        string? name = null)
    {
        ResourceId = GeneratedAssetValidation.RequireId(resourceId, nameof(resourceId));
        Sha256 = GeneratedAssetValidation.RequireSha256(sha256, nameof(sha256));
        MediaType = GeneratedAssetValidation.RequireMediaType(mediaType, nameof(mediaType));
        if (bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        Bytes = bytes;
        Name = GeneratedAssetValidation.OptionalText(name, 1_024, nameof(name));
    }

    public string ResourceId { get; }

    public string Sha256 { get; }

    public string MediaType { get; }

    public long Bytes { get; }

    public string? Name { get; }
}

public sealed class GameGeneratedAssetManifest
{
    public GameGeneratedAssetManifest(
        string assetId,
        IReadOnlyList<GameGeneratedAssetResource> resources,
        string metadataJson = "{}",
        string? providerRequestId = null)
    {
        AssetId = GeneratedAssetValidation.RequireId(assetId, nameof(assetId));
        var copied = (resources ?? throw new ArgumentNullException(nameof(resources))).ToArray();
        if (copied.Length == 0 || copied.Any(static resource => resource is null))
        {
            throw new ArgumentException("A generated asset manifest requires resources.", nameof(resources));
        }

        if (copied.Select(static resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Generated asset resource IDs must be unique.", nameof(resources));
        }

        Resources = Array.AsReadOnly(copied);
        MetadataJson = GeneratedAssetValidation.RequireJson(metadataJson, nameof(metadataJson));
        ProviderRequestId = providerRequestId is null
            ? null
            : GeneratedAssetValidation.RequireId(providerRequestId, nameof(providerRequestId));
    }

    public string AssetId { get; }

    public IReadOnlyList<GameGeneratedAssetResource> Resources { get; }

    public string MetadataJson { get; }

    public string? ProviderRequestId { get; }
}

public sealed class GameGeneratedAssetImportReceipt
{
    public GameGeneratedAssetImportReceipt(
        string operationId,
        GameGeneratedAssetImportOutcome outcome,
        string resultJson = "{}",
        long? stateRevision = null,
        string? code = null,
        string? message = null)
    {
        OperationId = GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        if (!Enum.IsDefined(typeof(GameGeneratedAssetImportOutcome), outcome)
            || outcome == GameGeneratedAssetImportOutcome.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Outcome = outcome;
        ResultJson = GeneratedAssetValidation.RequireJson(resultJson, nameof(resultJson));
        if (stateRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateRevision));
        }

        StateRevision = stateRevision;
        Code = GeneratedAssetValidation.OptionalText(code, 1_024, nameof(code));
        Message = GeneratedAssetValidation.OptionalText(message, 16_384, nameof(message));
    }

    public string OperationId { get; }

    public GameGeneratedAssetImportOutcome Outcome { get; }

    public string ResultJson { get; }

    public long? StateRevision { get; }

    public string? Code { get; }

    public string? Message { get; }
}

public sealed class GameGeneratedAssetJob
{
    public GameGeneratedAssetJob(
        string operationId,
        GameSessionKey owner,
        string assetType,
        GameMoment moment,
        string generatorId,
        string modelId,
        string importerId,
        string requestFingerprint,
        string requestMetadataJson,
        GameMediaKind mediaKind,
        long revision,
        GameGeneratedAssetStatus status,
        GameGeneratedAssetManifest? manifest = null,
        GameGeneratedAssetImportReceipt? importReceipt = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        OperationId = GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        Owner = GeneratedAssetValidation.RequireOwner(owner, nameof(owner));
        AssetType = GeneratedAssetValidation.RequireId(assetType, nameof(assetType));
        Moment = GeneratedAssetValidation.RequireMoment(moment, nameof(moment));
        GeneratorId = GeneratedAssetValidation.RequireId(generatorId, nameof(generatorId));
        ModelId = GeneratedAssetValidation.RequireId(modelId, nameof(modelId));
        ImporterId = GeneratedAssetValidation.RequireId(importerId, nameof(importerId));
        RequestFingerprint = GeneratedAssetValidation.RequireSha256(requestFingerprint, nameof(requestFingerprint));
        RequestMetadataJson = GeneratedAssetValidation.RequireJson(requestMetadataJson, nameof(requestMetadataJson));
        if (!Enum.IsDefined(typeof(GameMediaKind), mediaKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaKind));
        }

        MediaKind = mediaKind;
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (!Enum.IsDefined(typeof(GameGeneratedAssetStatus), status)
            || status == GameGeneratedAssetStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ValidateState(status, manifest, importReceipt);
        Revision = revision;
        Status = status;
        Manifest = manifest;
        ImportReceipt = importReceipt;
        ErrorCode = GeneratedAssetValidation.OptionalText(errorCode, 1_024, nameof(errorCode));
        ErrorMessage = GeneratedAssetValidation.OptionalText(errorMessage, 16_384, nameof(errorMessage));
    }

    public string OperationId { get; }

    public GameSessionKey Owner { get; }

    public string AssetType { get; }

    public GameMoment Moment { get; }

    public string GeneratorId { get; }

    public string ModelId { get; }

    public string ImporterId { get; }

    public string RequestFingerprint { get; }

    public string RequestMetadataJson { get; }

    public GameMediaKind MediaKind { get; }

    public long Revision { get; }

    public GameGeneratedAssetStatus Status { get; }

    public GameGeneratedAssetManifest? Manifest { get; }

    public GameGeneratedAssetImportReceipt? ImportReceipt { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public bool IsTerminal => Status is GameGeneratedAssetStatus.Completed
        or GameGeneratedAssetStatus.Rejected
        or GameGeneratedAssetStatus.Failed;

    internal GameGeneratedAssetJob Advance(
        GameGeneratedAssetStatus status,
        GameGeneratedAssetManifest? manifest = null,
        GameGeneratedAssetImportReceipt? receipt = null,
        string? errorCode = null,
        string? errorMessage = null) =>
        new(
            OperationId,
            Owner,
            AssetType,
            Moment,
            GeneratorId,
            ModelId,
            ImporterId,
            RequestFingerprint,
            RequestMetadataJson,
            MediaKind,
            checked(Revision + 1),
            status,
            manifest ?? Manifest,
            receipt ?? ImportReceipt,
            errorCode,
            errorMessage);

    private static void ValidateState(
        GameGeneratedAssetStatus status,
        GameGeneratedAssetManifest? manifest,
        GameGeneratedAssetImportReceipt? receipt)
    {
        if (status is (GameGeneratedAssetStatus.Generated
                or GameGeneratedAssetStatus.Importing
                or GameGeneratedAssetStatus.ImportUncertain
                or GameGeneratedAssetStatus.Completed
                or GameGeneratedAssetStatus.Rejected)
            && manifest is null)
        {
            throw new ArgumentException("This generated asset state requires a manifest.", nameof(manifest));
        }

        if (status is (GameGeneratedAssetStatus.Completed or GameGeneratedAssetStatus.Rejected)
            && receipt is null)
        {
            throw new ArgumentException("This generated asset state requires an import receipt.", nameof(receipt));
        }

        if (status == GameGeneratedAssetStatus.Completed
            && receipt?.Outcome != GameGeneratedAssetImportOutcome.Committed)
        {
            throw new ArgumentException("A completed generated asset requires a committed import receipt.", nameof(receipt));
        }

        if (status == GameGeneratedAssetStatus.Rejected
            && receipt?.Outcome != GameGeneratedAssetImportOutcome.Rejected)
        {
            throw new ArgumentException("A rejected generated asset requires a rejected import receipt.", nameof(receipt));
        }
    }
}

public sealed class GameGeneratedAssetSaveResult
{
    public GameGeneratedAssetSaveResult(bool saved, GameGeneratedAssetJob current)
    {
        Saved = saved;
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public bool Saved { get; }

    public GameGeneratedAssetJob Current { get; }
}

public interface IGameGeneratedAssetJobStore
{
    ValueTask<GameGeneratedAssetJob?> LoadAsync(
        GameSessionKey owner,
        string operationId,
        CancellationToken cancellationToken);

    ValueTask<GameGeneratedAssetSaveResult> SaveAsync(
        GameGeneratedAssetJob job,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public interface IGameGeneratedAssetResourceStore
{
    ValueTask<GameGeneratedAssetResource> SaveAsync(
        string operationId,
        int outputIndex,
        GameGeneratedAssetBinary resource,
        CancellationToken cancellationToken);

    ValueTask<GameGeneratedAssetBinary> ReadAsync(
        GameGeneratedAssetResource resource,
        CancellationToken cancellationToken);
}

public interface IGameGeneratedAssetResourceMaterializer
{
    ValueTask<GameGeneratedAssetBinary> MaterializeAsync(
        ResourceContent resource,
        CancellationToken cancellationToken);
}

public sealed class GameGeneratedAssetImportContext
{
    public GameGeneratedAssetImportContext(
        GameGeneratedAssetJob job,
        IGameGeneratedAssetResourceStore resources,
        string importOperationId)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
        if (job.Manifest is null)
        {
            throw new ArgumentException("An import context requires a generated asset manifest.", nameof(job));
        }

        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        ImportOperationId = GeneratedAssetValidation.RequireId(importOperationId, nameof(importOperationId));
    }

    public GameGeneratedAssetJob Job { get; }

    public GameGeneratedAssetManifest Manifest => Job.Manifest!;

    public IGameGeneratedAssetResourceStore Resources { get; }

    public string ImportOperationId { get; }
}

public interface IGameGeneratedAssetImporter
{
    string ImporterId { get; }

    ValueTask<GameGeneratedAssetImportReceipt> ImportAsync(
        GameGeneratedAssetImportContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an import whose commit outcome is unknown. Implementations must be idempotent and
    /// must never repeat the original world mutation merely because the caller was restarted.
    /// </summary>
    ValueTask<GameGeneratedAssetImportReceipt> RecoverAsync(
        GameGeneratedAssetImportContext context,
        CancellationToken cancellationToken);
}

public sealed class GameGeneratedAssetPipelineOptions
{
    public int MaxOutputs { get; set; } = 32;

    public int MaxResourceBytes { get; set; } = 50_000_000;

    public long MaxAggregateResourceBytes { get; set; } = 200_000_000;

    public int SettlementTimeoutMilliseconds { get; set; } = 10_000;
}

public sealed class GameGeneratedAssetPipeline
{
    private readonly IGameGeneratedAssetJobStore _jobs;
    private readonly IGameGeneratedAssetResourceStore _resources;
    private readonly IGameGeneratedAssetResourceMaterializer _materializer;
    private readonly SemaphoreSlim[] _gates;
    private readonly int _maxOutputs;
    private readonly int _maxResourceBytes;
    private readonly long _maxAggregateResourceBytes;
    private readonly int _settlementTimeoutMilliseconds;

    public GameGeneratedAssetPipeline(
        IGameGeneratedAssetJobStore jobs,
        IGameGeneratedAssetResourceStore resources,
        IGameGeneratedAssetResourceMaterializer? materializer = null,
        GameGeneratedAssetPipelineOptions? options = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        options ??= new GameGeneratedAssetPipelineOptions();
        _maxOutputs = GeneratedAssetValidation.RequireRange(options.MaxOutputs, 1, 1_000, nameof(options.MaxOutputs));
        _maxResourceBytes = GeneratedAssetValidation.RequireRange(
            options.MaxResourceBytes,
            1,
            500_000_000,
            nameof(options.MaxResourceBytes));
        if (options.MaxAggregateResourceBytes < _maxResourceBytes
            || options.MaxAggregateResourceBytes > 2_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxAggregateResourceBytes));
        }

        _maxAggregateResourceBytes = options.MaxAggregateResourceBytes;
        _materializer = materializer ?? new InlineGameGeneratedAssetMaterializer(_maxResourceBytes);
        _settlementTimeoutMilliseconds = GeneratedAssetValidation.RequireRange(
            options.SettlementTimeoutMilliseconds,
            100,
            300_000,
            nameof(options.SettlementTimeoutMilliseconds));
        _gates = Enumerable.Range(0, 64).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();
    }

    public async ValueTask<GameGeneratedAssetJob> ExecuteAsync(
        GameGeneratedAssetRequest request,
        IGameMediaGenerator generator,
        IGameGeneratedAssetImporter importer,
        GameMediaProgressHandler? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (generator is null)
        {
            throw new ArgumentNullException(nameof(generator));
        }

        ValidateImporter(request, importer);
        var gate = GateFor(request.Owner, request.OperationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await ReserveAsync(request, cancellationToken).ConfigureAwait(false);
            if (job.Status != GameGeneratedAssetStatus.Prepared)
            {
                return await ContinueImportIfPossibleAsync(
                    job,
                    importer,
                    allowRecovery: false,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var claimResult = await _jobs.SaveAsync(
                job.Advance(GameGeneratedAssetStatus.Generating),
                job.Revision,
                cancellationToken).ConfigureAwait(false);
            if (!claimResult.Saved)
            {
                return claimResult.Current;
            }

            var claimed = claimResult.Current;

            GameMediaGenerationResult result;
            try
            {
                result = await generator.GenerateAsync(request.Generation, progress, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The media generator returned null.");
            }
            catch (OperationCanceledException)
            {
                await SettleAsync(
                    claimed.Advance(
                        GameGeneratedAssetStatus.GenerationUncertain,
                        errorCode: "generation_cancelled_after_dispatch",
                        errorMessage: "Generation was cancelled after dispatch; the provider outcome must not be replayed blindly."),
                    claimed.Revision).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                return await SettleAsync(
                    claimed.Advance(
                        GameGeneratedAssetStatus.GenerationUncertain,
                        errorCode: "generation_outcome_uncertain",
                        errorMessage: "The generator failed after dispatch; its outcome must be reconciled before another submission."),
                    claimed.Revision).ConfigureAwait(false);
            }

            GameGeneratedAssetManifest manifest;
            try
            {
                manifest = await PersistOutputsAsync(
                    request.Owner,
                    request.OperationId,
                    request.Generation.Kind,
                    result,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await SettleAsync(
                    claimed.Advance(
                        GameGeneratedAssetStatus.GenerationUncertain,
                        errorCode: "asset_persistence_cancelled",
                        errorMessage: "Generated output persistence was cancelled; inspect the operation before retrying."),
                    claimed.Revision).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                return await SettleAsync(
                    claimed.Advance(
                        GameGeneratedAssetStatus.Failed,
                        errorCode: "invalid_generated_asset",
                        errorMessage: "Generated output validation or persistence failed."),
                    claimed.Revision).ConfigureAwait(false);
            }

            var generatedResult = await _jobs.SaveAsync(
                claimed.Advance(GameGeneratedAssetStatus.Generated, manifest),
                claimed.Revision,
                cancellationToken).ConfigureAwait(false);
            if (!generatedResult.Saved)
            {
                return generatedResult.Current;
            }

            return await ContinueImportIfPossibleAsync(
                generatedResult.Current,
                importer,
                allowRecovery: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameGeneratedAssetJob?> LoadAsync(
        GameSessionKey owner,
        string operationId,
        CancellationToken cancellationToken = default) =>
        await _jobs.LoadAsync(
            GeneratedAssetValidation.RequireOwner(owner, nameof(owner)),
            GeneratedAssetValidation.RequireId(operationId, nameof(operationId)),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<GameGeneratedAssetJob> ResumeImportAsync(
        GameSessionKey owner,
        string operationId,
        IGameGeneratedAssetImporter importer,
        CancellationToken cancellationToken = default)
    {
        if (importer is null)
        {
            throw new ArgumentNullException(nameof(importer));
        }

        var checkedOwner = GeneratedAssetValidation.RequireOwner(owner, nameof(owner));
        var checkedOperation = GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        var gate = GateFor(checkedOwner, checkedOperation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await _jobs.LoadAsync(checkedOwner, checkedOperation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The generated asset operation does not exist.");
            if (!string.Equals(job.ImporterId, importer.ImporterId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The generated asset importer does not match the prepared operation.");
            }

            return await ContinueImportIfPossibleAsync(
                job,
                importer,
                allowRecovery: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Supplies the authoritative result of a previously dispatched generation operation. This is
    /// the only supported path out of an uncertain generation state; the pipeline never submits the
    /// original provider request again automatically.
    /// </summary>
    public async ValueTask<GameGeneratedAssetJob> ResolveGenerationAsync(
        GameSessionKey owner,
        string operationId,
        GameMediaGenerationResult recoveredResult,
        IGameGeneratedAssetImporter importer,
        CancellationToken cancellationToken = default)
    {
        if (recoveredResult is null)
        {
            throw new ArgumentNullException(nameof(recoveredResult));
        }

        if (importer is null)
        {
            throw new ArgumentNullException(nameof(importer));
        }

        var checkedOwner = GeneratedAssetValidation.RequireOwner(owner, nameof(owner));
        var checkedOperation = GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        var gate = GateFor(checkedOwner, checkedOperation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await _jobs.LoadAsync(checkedOwner, checkedOperation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The generated asset operation does not exist.");
            if (job.Status is not (GameGeneratedAssetStatus.Generating
                or GameGeneratedAssetStatus.GenerationUncertain))
            {
                throw new InvalidOperationException("The generated asset operation is not awaiting generation recovery.");
            }

            if (!string.Equals(job.ImporterId, importer.ImporterId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The generated asset importer does not match the prepared operation.");
            }

            var manifest = await PersistOutputsAsync(
                job.Owner,
                job.OperationId,
                job.MediaKind,
                recoveredResult,
                cancellationToken).ConfigureAwait(false);
            var generatedResult = await _jobs.SaveAsync(
                job.Advance(GameGeneratedAssetStatus.Generated, manifest),
                job.Revision,
                cancellationToken).ConfigureAwait(false);
            if (!generatedResult.Saved)
            {
                return generatedResult.Current;
            }

            return await ContinueImportIfPossibleAsync(
                generatedResult.Current,
                importer,
                allowRecovery: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameGeneratedAssetJob> FailUnresolvedGenerationAsync(
        GameSessionKey owner,
        string operationId,
        string code,
        string message,
        CancellationToken cancellationToken = default)
    {
        var checkedOwner = GeneratedAssetValidation.RequireOwner(owner, nameof(owner));
        var checkedOperation = GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        var checkedCode = GeneratedAssetValidation.RequireId(code, nameof(code));
        var checkedMessage = GeneratedAssetValidation.BoundError(message);
        var gate = GateFor(checkedOwner, checkedOperation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await _jobs.LoadAsync(checkedOwner, checkedOperation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The generated asset operation does not exist.");
            if (job.Status is not (GameGeneratedAssetStatus.Generating
                or GameGeneratedAssetStatus.GenerationUncertain))
            {
                throw new InvalidOperationException("The generated asset operation is not awaiting generation recovery.");
            }

            return await SaveCurrentAsync(
                job.Advance(GameGeneratedAssetStatus.Failed, errorCode: checkedCode, errorMessage: checkedMessage),
                job.Revision,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public static string CreateImportOperationId(GameGeneratedAssetJob job)
    {
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        return "asset-import-v1-" + GeneratedAssetValidation.Hash(
            job.Owner.SessionId + "\n"
            + job.Owner.ActorId + "\n"
            + job.Moment.TimelineId + "\n"
            + job.OperationId + "\n"
            + job.ImporterId).Substring(0, 40);
    }

    private async ValueTask<GameGeneratedAssetJob> ReserveAsync(
        GameGeneratedAssetRequest request,
        CancellationToken cancellationToken)
    {
        var current = await _jobs.LoadAsync(request.Owner, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            EnsureSameRequest(current, request);
            return current;
        }

        var prepared = new GameGeneratedAssetJob(
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
        var saved = await _jobs.SaveAsync(prepared, 0, cancellationToken).ConfigureAwait(false);
        EnsureSameRequest(saved.Current, request);
        return saved.Current;
    }

    private async ValueTask<GameGeneratedAssetManifest> PersistOutputsAsync(
        GameSessionKey owner,
        string operationId,
        GameMediaKind mediaKind,
        GameMediaGenerationResult result,
        CancellationToken cancellationToken)
    {
        if (result.Outputs.Count == 0 || result.Outputs.Count > _maxOutputs)
        {
            throw new InvalidDataException("The generated output count is invalid.");
        }

        var expectedPrefix = mediaKind switch
        {
            GameMediaKind.Image => "image/",
            GameMediaKind.Audio => "audio/",
            GameMediaKind.Video => "video/",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind)),
        };
        var stored = new List<GameGeneratedAssetResource>(result.Outputs.Count);
        long aggregate = 0;
        for (var index = 0; index < result.Outputs.Count; index++)
        {
            var output = result.Outputs[index]
                ?? throw new InvalidDataException("The media generator returned a null output.");
            if (!output.MediaType.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The media generator returned an output of the wrong media kind.");
            }

            var binary = await _materializer.MaterializeAsync(output, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The generated asset materializer returned null.");
            if (binary.Data.Count > _maxResourceBytes)
            {
                throw new InvalidDataException("A generated asset resource exceeded the configured byte limit.");
            }

            aggregate = checked(aggregate + binary.Data.Count);
            if (aggregate > _maxAggregateResourceBytes)
            {
                throw new InvalidDataException("Generated asset resources exceeded the configured aggregate byte limit.");
            }

            var saved = await _resources.SaveAsync(
                operationId,
                index,
                binary,
                cancellationToken).ConfigureAwait(false);
            GeneratedAssetValidation.VerifySavedResource(saved, binary);
            stored.Add(saved);
        }

        return new GameGeneratedAssetManifest(
            "asset-v1-" + GeneratedAssetValidation.Hash(
                owner.SessionId + "\n" + owner.ActorId + "\n" + operationId).Substring(0, 40),
            stored,
            result.MetadataJson,
            result.ProviderRequestId);
    }

    private async ValueTask<GameGeneratedAssetJob> ContinueImportIfPossibleAsync(
        GameGeneratedAssetJob job,
        IGameGeneratedAssetImporter importer,
        bool allowRecovery,
        CancellationToken cancellationToken)
    {
        if (job.IsTerminal
            || job.Status is GameGeneratedAssetStatus.Prepared
                or GameGeneratedAssetStatus.Generating
                or GameGeneratedAssetStatus.GenerationUncertain)
        {
            return job;
        }

        if (job.Manifest is null)
        {
            throw new InvalidOperationException("The generated asset operation is missing its manifest.");
        }

        if (job.Status == GameGeneratedAssetStatus.Generated)
        {
            var claimResult = await _jobs.SaveAsync(
                job.Advance(GameGeneratedAssetStatus.Importing),
                job.Revision,
                cancellationToken).ConfigureAwait(false);
            if (!claimResult.Saved)
            {
                return claimResult.Current;
            }

            return await InvokeImporterAsync(
                claimResult.Current,
                importer,
                recover: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (job.Status is GameGeneratedAssetStatus.Importing or GameGeneratedAssetStatus.ImportUncertain)
        {
            return allowRecovery
                ? await InvokeImporterAsync(job, importer, recover: true, cancellationToken).ConfigureAwait(false)
                : job;
        }

        return job;
    }

    private async ValueTask<GameGeneratedAssetJob> InvokeImporterAsync(
        GameGeneratedAssetJob job,
        IGameGeneratedAssetImporter importer,
        bool recover,
        CancellationToken cancellationToken)
    {
        var context = new GameGeneratedAssetImportContext(job, _resources, CreateImportOperationId(job));
        GameGeneratedAssetImportReceipt receipt;
        try
        {
            receipt = recover
                ? await importer.RecoverAsync(context, cancellationToken).ConfigureAwait(false)
                : await importer.ImportAsync(context, cancellationToken).ConfigureAwait(false);
            if (receipt is null)
            {
                throw new InvalidOperationException("The generated asset importer returned null.");
            }

            if (!string.Equals(receipt.OperationId, context.ImportOperationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The generated asset import receipt has the wrong operation ID.");
            }
        }
        catch (OperationCanceledException)
        {
            await SettleAsync(
                job.Advance(
                    GameGeneratedAssetStatus.ImportUncertain,
                    errorCode: "import_cancelled_after_dispatch",
                    errorMessage: "Import was cancelled after dispatch; the host must reconcile it before retrying."),
                job.Revision).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            return await SettleAsync(
                job.Advance(
                    GameGeneratedAssetStatus.ImportUncertain,
                    errorCode: "import_outcome_uncertain",
                    errorMessage: "The importer failed after dispatch; the authoritative engine outcome must be reconciled."),
                job.Revision).ConfigureAwait(false);
        }

        var status = receipt.Outcome switch
        {
            GameGeneratedAssetImportOutcome.Committed => GameGeneratedAssetStatus.Completed,
            GameGeneratedAssetImportOutcome.Rejected => GameGeneratedAssetStatus.Rejected,
            GameGeneratedAssetImportOutcome.Failed => GameGeneratedAssetStatus.Failed,
            GameGeneratedAssetImportOutcome.Uncertain => GameGeneratedAssetStatus.ImportUncertain,
            _ => throw new InvalidOperationException("The generated asset importer returned an unknown outcome."),
        };
        return await SettleAsync(
            job.Advance(
                status,
                receipt: receipt,
                errorCode: receipt.Code,
                errorMessage: receipt.Message),
            job.Revision).ConfigureAwait(false);
    }

    private async ValueTask<GameGeneratedAssetJob> SettleAsync(
        GameGeneratedAssetJob next,
        long expectedRevision)
    {
        using var timeout = new CancellationTokenSource(_settlementTimeoutMilliseconds);
        var saved = await _jobs.SaveAsync(next, expectedRevision, timeout.Token).ConfigureAwait(false);
        return saved.Current;
    }

    private async ValueTask<GameGeneratedAssetJob> SaveCurrentAsync(
        GameGeneratedAssetJob next,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var saved = await _jobs.SaveAsync(next, expectedRevision, cancellationToken).ConfigureAwait(false);
        return saved.Current;
    }

    private SemaphoreSlim GateFor(GameSessionKey owner, string operationId)
    {
        var key = owner.SessionId + "\n" + owner.ActorId + "\n" + operationId;
        return _gates[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % _gates.Length];
    }

    private static void ValidateImporter(GameGeneratedAssetRequest request, IGameGeneratedAssetImporter importer)
    {
        if (importer is null)
        {
            throw new ArgumentNullException(nameof(importer));
        }

        if (!string.Equals(request.ImporterId, importer.ImporterId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The generated asset importer does not match the request.", nameof(importer));
        }
    }

    private static void EnsureSameRequest(GameGeneratedAssetJob job, GameGeneratedAssetRequest request)
    {
        if (job.Owner != request.Owner
            || !string.Equals(job.OperationId, request.OperationId, StringComparison.Ordinal)
            || !string.Equals(job.RequestFingerprint, request.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The generated asset operation ID is already bound to a different request.");
        }
    }
}

public sealed class InlineGameGeneratedAssetMaterializer : IGameGeneratedAssetResourceMaterializer
{
    private readonly int _maxResourceBytes;

    public InlineGameGeneratedAssetMaterializer(int maxResourceBytes = 50_000_000)
    {
        _maxResourceBytes = GeneratedAssetValidation.RequireRange(
            maxResourceBytes,
            1,
            500_000_000,
            nameof(maxResourceBytes));
    }

    public ValueTask<GameGeneratedAssetBinary> MaterializeAsync(
        ResourceContent resource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resource is null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        var prefix = "data:" + resource.MediaType + ";base64,";
        if (!resource.Uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Generated asset outputs must be inline data URIs unless the host supplies a trusted resource materializer.");
        }

        var value = resource.Uri.Substring(prefix.Length);
        var maximumEncodedCharacters = checked((((long)_maxResourceBytes + 2L) / 3L) * 4L);
        if (value.Length == 0
            || value.Length > maximumEncodedCharacters
            || value.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("The generated asset data URI is invalid.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The generated asset data URI is invalid.", exception);
        }

        return new ValueTask<GameGeneratedAssetBinary>(
            new GameGeneratedAssetBinary(bytes, resource.MediaType, resource.Name));
    }
}

public sealed class InMemoryGameGeneratedAssetJobStore : IGameGeneratedAssetJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameGeneratedAssetJob> _jobs = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public InMemoryGameGeneratedAssetJobStore(int capacity = 10_000)
    {
        _capacity = GeneratedAssetValidation.RequireRange(capacity, 1, 1_000_000, nameof(capacity));
    }

    public ValueTask<GameGeneratedAssetJob?> LoadAsync(
        GameSessionKey owner,
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(owner, operationId);
        lock (_gate)
        {
            return new ValueTask<GameGeneratedAssetJob?>(_jobs.TryGetValue(key, out var job) ? job : null);
        }
    }

    public ValueTask<GameGeneratedAssetSaveResult> SaveAsync(
        GameGeneratedAssetJob job,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        var key = Key(job.Owner, job.OperationId);
        lock (_gate)
        {
            _jobs.TryGetValue(key, out var current);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                if (current is null)
                {
                    throw new InvalidOperationException("The generated asset operation disappeared during a compare-and-swap update.");
                }

                return new ValueTask<GameGeneratedAssetSaveResult>(
                    new GameGeneratedAssetSaveResult(false, current));
            }

            if (current is null && _jobs.Count >= _capacity)
            {
                throw new InvalidOperationException("The generated asset job store reached its capacity.");
            }

            GeneratedAssetValidation.ValidateTransition(current, job, expectedRevision);
            _jobs[key] = job;
            return new ValueTask<GameGeneratedAssetSaveResult>(new GameGeneratedAssetSaveResult(true, job));
        }
    }

    private static string Key(GameSessionKey owner, string operationId)
    {
        var checkedOwner = GeneratedAssetValidation.RequireOwner(owner, nameof(owner));
        return checkedOwner.SessionId + "\n" + checkedOwner.ActorId + "\n"
            + GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
    }
}

public sealed class InMemoryGameGeneratedAssetResourceStore : IGameGeneratedAssetResourceStore
{
    private readonly ConcurrentDictionary<string, byte[]> _resources = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public InMemoryGameGeneratedAssetResourceStore(int capacity = 10_000)
    {
        _capacity = GeneratedAssetValidation.RequireRange(capacity, 1, 1_000_000, nameof(capacity));
    }

    public ValueTask<GameGeneratedAssetResource> SaveAsync(
        string operationId,
        int outputIndex,
        GameGeneratedAssetBinary resource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        if (outputIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputIndex));
        }

        if (resource is null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        var bytes = resource.Data.ToArray();
        var hash = GeneratedAssetValidation.Hash(bytes);
        var id = "sha256-" + hash;
        if (_resources.Count >= _capacity && !_resources.ContainsKey(id))
        {
            throw new InvalidOperationException("The generated asset resource store reached its capacity.");
        }

        var stored = _resources.GetOrAdd(id, bytes);
        if (!stored.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException("A generated asset resource hash collision was detected.");
        }

        return new ValueTask<GameGeneratedAssetResource>(
            new GameGeneratedAssetResource(id, hash, resource.MediaType, bytes.Length, resource.Name));
    }

    public ValueTask<GameGeneratedAssetBinary> ReadAsync(
        GameGeneratedAssetResource resource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resource is null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        if (!_resources.TryGetValue(resource.ResourceId, out var bytes))
        {
            throw new InvalidOperationException("The generated asset resource does not exist.");
        }

        GeneratedAssetValidation.VerifyResource(resource, bytes);
        return new ValueTask<GameGeneratedAssetBinary>(
            new GameGeneratedAssetBinary(bytes, resource.MediaType, resource.Name));
    }
}

internal static class GeneratedAssetValidation
{
    private const int MaximumIdCharacters = 1_024;
    private const int MaximumJsonCharacters = 2_000_000;
    private const int MaximumPromptCharacters = 1_000_000;
    private const int MaximumSourceCount = 64;
    private const int MaximumSourceUriCharacters = 100_000_000;
    private const long MaximumAggregateSourceUriCharacters = 200_000_000;

    public static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumIdCharacters
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-empty identifier is required.", parameterName);
        }

        return value;
    }

    public static GameSessionKey RequireOwner(GameSessionKey owner, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(owner.SessionId) || string.IsNullOrWhiteSpace(owner.ActorId))
        {
            throw new ArgumentException("A generated asset owner is required.", parameterName);
        }

        RequireId(owner.SessionId, parameterName);
        RequireId(owner.ActorId, parameterName);
        return owner;
    }

    public static GameMoment RequireMoment(GameMoment moment, string parameterName)
    {
        try
        {
            return new GameMoment(moment.TimelineId, moment.Tick, moment.CalendarJson);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("A valid game moment is required.", parameterName, exception);
        }
    }

    public static string RequireJson(string value, string parameterName)
    {
        if (value is null || value.Length > MaximumJsonCharacters)
        {
            throw new ArgumentException("Generated asset JSON is missing or too large.", parameterName);
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            EnsureUnambiguous(document.RootElement);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Generated asset JSON is invalid.", parameterName, exception);
        }
    }

    public static string RequireMediaType(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !value.Contains('/'))
        {
            throw new ArgumentException("A valid bounded media type is required.", parameterName);
        }

        return value;
    }

    public static string RequireSha256(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A hexadecimal SHA-256 digest is required.", parameterName);
        }

        return value.ToLowerInvariant();
    }

    public static string? OptionalText(string? value, int maximum, string parameterName)
    {
        if ((value?.Length ?? 0) > maximum || (value?.Any(static character => character == '\0') ?? false))
        {
            throw new ArgumentException("Generated asset text is invalid or too large.", parameterName);
        }

        return value;
    }

    public static int RequireRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static string Fingerprint(GameGeneratedAssetRequest request)
    {
        using var algorithm = SHA256.Create();
        using var crypto = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true);
        using var writer = new StreamWriter(crypto, new UTF8Encoding(false), 4_096, leaveOpen: true);
        void Add(string? value)
        {
            writer.Write(value?.Length ?? -1);
            writer.Write(':');
            writer.Write(value);
            writer.Write('\n');
        }

        Add("OpenGameAgent.GeneratedAssetRequest.v1");
        Add(request.OperationId);
        Add(request.Owner.SessionId);
        Add(request.Owner.ActorId);
        Add(request.AssetType);
        Add(request.Moment.TimelineId);
        Add(request.Moment.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(request.Moment.CalendarJson);
        Add(request.GeneratorId);
        Add(request.ModelId);
        Add(request.ImporterId);
        Add(request.Generation.RequestId);
        Add(request.Generation.Kind.ToString());
        Add(request.Generation.ContextJson);
        Add(request.Generation.ParametersJson);
        Add(request.Generation.Prompt);
        Add(request.MetadataJson);
        foreach (var source in request.Generation.Sources)
        {
            Add(source.Uri);
            Add(source.MediaType);
            Add(source.Name);
        }

        writer.Flush();
        crypto.FlushFinalBlock();
        return BitConverter.ToString(algorithm.Hash!).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static void ValidateGenerationRequest(GameMediaGenerationRequest request, string parameterName)
    {
        RequireJson(request.ContextJson, parameterName);
        RequireJson(request.ParametersJson, parameterName);
        if ((request.Prompt?.Length ?? 0) > MaximumPromptCharacters)
        {
            throw new ArgumentException("The generated asset prompt is too large.", parameterName);
        }

        if (request.Sources.Count > MaximumSourceCount)
        {
            throw new ArgumentException("The generated asset source count is too large.", parameterName);
        }

        long aggregate = 0;
        foreach (var source in request.Sources)
        {
            if (source.Uri.Length > MaximumSourceUriCharacters)
            {
                throw new ArgumentException("A generated asset source URI is too large.", parameterName);
            }

            aggregate = checked(aggregate + source.Uri.Length);
            if (aggregate > MaximumAggregateSourceUriCharacters)
            {
                throw new ArgumentException("Generated asset source URIs are too large in aggregate.", parameterName);
            }

            RequireMediaType(source.MediaType, parameterName);
            OptionalText(source.Name, 1_024, parameterName);
        }
    }

    public static void VerifySavedResource(
        GameGeneratedAssetResource resource,
        GameGeneratedAssetBinary binary)
    {
        if (resource is null)
        {
            throw new InvalidDataException("The generated asset resource store returned null.");
        }

        var bytes = binary.Data.ToArray();
        var hash = Hash(bytes);
        if (resource.Bytes != bytes.LongLength
            || !string.Equals(resource.Sha256, hash, StringComparison.Ordinal)
            || !string.Equals(resource.ResourceId, "sha256-" + hash, StringComparison.Ordinal)
            || !string.Equals(resource.MediaType, binary.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The generated asset resource store returned an invalid resource record.");
        }
    }

    public static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    public static string Hash(byte[] value)
    {
        using var algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(value)).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static void VerifyResource(GameGeneratedAssetResource resource, byte[] bytes)
    {
        if (resource.Bytes != bytes.LongLength
            || !string.Equals(resource.Sha256, Hash(bytes), StringComparison.Ordinal)
            || !string.Equals(resource.ResourceId, "sha256-" + resource.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The generated asset resource failed its integrity check.");
        }
    }

    public static string BoundError(string? value)
    {
        const int maximum = 16_384;
        var sanitized = new string((value ?? "Generated asset operation failed.")
            .Select(static character => char.IsControl(character) && character is not '\t' ? ' ' : character)
            .ToArray());
        return sanitized.Length <= maximum ? sanitized : sanitized.Substring(0, maximum);
    }

    public static void ValidateTransition(
        GameGeneratedAssetJob? current,
        GameGeneratedAssetJob next,
        long expectedRevision)
    {
        if (next.Revision != expectedRevision + 1)
        {
            throw new ArgumentException("A generated asset revision must advance by exactly one.", nameof(next));
        }

        if (current is null)
        {
            if (expectedRevision != 0 || next.Status != GameGeneratedAssetStatus.Prepared)
            {
                throw new InvalidOperationException("A generated asset operation must begin in the prepared state.");
            }

            return;
        }

        if (current.Owner != next.Owner
            || current.Moment != next.Moment
            || !string.Equals(current.OperationId, next.OperationId, StringComparison.Ordinal)
            || !string.Equals(current.AssetType, next.AssetType, StringComparison.Ordinal)
            || !string.Equals(current.GeneratorId, next.GeneratorId, StringComparison.Ordinal)
            || !string.Equals(current.ModelId, next.ModelId, StringComparison.Ordinal)
            || !string.Equals(current.ImporterId, next.ImporterId, StringComparison.Ordinal)
            || !string.Equals(current.RequestFingerprint, next.RequestFingerprint, StringComparison.Ordinal)
            || !string.Equals(current.RequestMetadataJson, next.RequestMetadataJson, StringComparison.Ordinal)
            || current.MediaKind != next.MediaKind)
        {
            throw new InvalidOperationException("A generated asset operation cannot change its identity.");
        }

        if (current.IsTerminal)
        {
            throw new InvalidOperationException("A terminal generated asset operation is immutable.");
        }

        var allowed = current.Status switch
        {
            GameGeneratedAssetStatus.Prepared => next.Status == GameGeneratedAssetStatus.Generating,
            GameGeneratedAssetStatus.Generating or GameGeneratedAssetStatus.GenerationUncertain =>
                next.Status is GameGeneratedAssetStatus.Generated
                or GameGeneratedAssetStatus.GenerationUncertain
                or GameGeneratedAssetStatus.Failed,
            GameGeneratedAssetStatus.Generated => next.Status == GameGeneratedAssetStatus.Importing,
            GameGeneratedAssetStatus.Importing or GameGeneratedAssetStatus.ImportUncertain =>
                next.Status is GameGeneratedAssetStatus.Completed
                    or GameGeneratedAssetStatus.Rejected
                    or GameGeneratedAssetStatus.Failed
                    or GameGeneratedAssetStatus.ImportUncertain,
            _ => false,
        };
        if (!allowed)
        {
            throw new InvalidOperationException("The generated asset state transition is invalid.");
        }
    }

    private static void EnsureUnambiguous(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Generated asset JSON contains duplicate property names.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }
}
