using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameAgent.Core;
using GameAgent.World;

namespace GameAgent.Persistence;

/// <summary>
/// Settled, engine-neutral carrier for a native authoritative save and the
/// local sidecars that belong to its timeline. The carrier is deterministic
/// and digest-bound; it is not a signature or an author-identity mechanism.
/// </summary>
public static class InteractiveWorldBundle
{
    public const string ContractId = "game-agent.interactive-world-bundle.v1";

    public const string AuthoritativeStoreFileName = "world.store";

    public const string MemoryStoreFileName = "memory.store";

    public const string GroupStoreFileName = "groups.store";

    public const string PresentationStoreFileName = "presentations.store";

    private const string SaveEntryPath = "world-save.json";
    private const string MemoryEntryPath = "memory-sidecar.json";
    private const string GroupEntryPath = "group-sidecar.json";
    private const string PresentationEntryPath =
        "presentation-sidecar.json";
    private const string SidecarContract =
        "game-agent.interactive-world-sidecar.v1";
    private const int HeaderLength = 44;

    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes("GAIWBND1");

    public static async ValueTask<InteractiveWorldBundleArtifact>
        CaptureAsync(
            InteractiveWorldBundleCaptureSource source,
            InteractiveWorldBundleExportMode exportMode =
                InteractiveWorldBundleExportMode.PrivateLocal,
            InteractiveWorldBundleOptions? options = null,
            CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        ValidateMode(exportMode);
        var effective = options ?? new InteractiveWorldBundleOptions();
        cancellationToken.ThrowIfCancellationRequested();
        await using var quiescenceLease =
            await source.Quiescence.TryAcquireSettledQuiescenceAsync(
                    cancellationToken)
                .ConfigureAwait(false)
            ?? throw Error(
                InteractiveWorldBundleReasonCodes.Unsettled,
                "The settlement outbox contains pending or "
                + "reconciliation work.");
        var bridge = new NativeWorldSaveBridge();
        var before = await CaptureSettledSaveAsync(
                bridge,
                source.Runtime,
                effective.NativeSave,
                cancellationToken)
            .ConfigureAwait(false);

        await using var memoryLease = source.MemoryStore is null
            ? null
            : await source.MemoryStore
                .AcquireInteractiveWorldBundleCaptureAsync(
                    effective.Limits.MaxMemoryRecords,
                    cancellationToken)
                .ConfigureAwait(false);
        await using var groupLease =
            source.GroupInteractionStore is null
                ? null
                : await source.GroupInteractionStore
                    .AcquireInteractiveWorldBundleCaptureAsync(
                        effective.Limits.MaxGroupSessions,
                        cancellationToken)
                    .ConfigureAwait(false);
        await using var presentationLease =
            source.PresentationStore is null
                ? null
                : await source.PresentationStore
                    .AcquireInteractiveWorldBundleCaptureAsync(
                        effective.Limits.MaxPresentationRecords,
                        cancellationToken)
                    .ConfigureAwait(false);
        var memories = memoryLease?.Items.ToArray()
                       ?? Array.Empty<MemoryRecord>();
        var groups = groupLease?.Items.ToArray()
                     ?? Array.Empty<GroupInteractionSession>();
        var presentations = presentationLease?.Items.ToArray()
                            ?? Array.Empty<
                                VerifiedWorldPresentation>();

        var after = await CaptureSettledSaveAsync(
                bridge,
                source.Runtime,
                effective.NativeSave,
                cancellationToken)
            .ConfigureAwait(false);
        var beforeBytes = WorldSaveCodec.Write(
            before,
            effective.NativeSave.ArtifactLimits);
        var afterBytes = WorldSaveCodec.Write(
            after,
            effective.NativeSave.ArtifactLimits);
        if (!beforeBytes.AsSpan().SequenceEqual(afterBytes))
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.Unsettled,
                "The authoritative world changed while its bundle "
                + "sidecars were captured.");
        }

        var snapshot = await source.Runtime.ReadSnapshotAsync(
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Error(
                InteractiveWorldBundleReasonCodes.Unsettled,
                "The authoritative snapshot disappeared during capture.");
        var binding = CreateBinding(after, snapshot, source.Runtime.Package);
        ValidateSidecars(
            binding,
            snapshot,
            memories,
            groups,
            presentations,
            effective.Limits);
        await ValidatePresentationEvidenceAsync(
                source.Runtime,
                presentations,
                cancellationToken)
            .ConfigureAwait(false);

        if (exportMode == InteractiveWorldBundleExportMode.PublicExport)
        {
            memories = Array.Empty<MemoryRecord>();
            groups = Array.Empty<GroupInteractionSession>();
            presentations = Array.Empty<VerifiedWorldPresentation>();
        }

        return BuildArtifact(
            after,
            binding,
            memories,
            groups,
            presentations,
            exportMode,
            effective);
    }

    public static async ValueTask<InteractiveWorldBundleArtifact>
        ForkAsync(
            ActivatedWorldPackage package,
            ReadOnlyMemory<byte> sourceArtifact,
            string forkTimelineId,
            InteractiveWorldBundleOptions? options = null,
            CancellationToken cancellationToken = default)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        if (string.IsNullOrWhiteSpace(forkTimelineId))
        {
            throw new ArgumentException(
                "A fork timeline identifier is required.",
                nameof(forkTimelineId));
        }

        var effective = options ?? new InteractiveWorldBundleOptions();
        var admitted = await AdmitAsync(
                package,
                sourceArtifact,
                effective,
                cancellationToken)
            .ConfigureAwait(false);
        var bridge = new NativeWorldSaveBridge();
        var forkSave = await bridge.ForkAsync(
                package,
                admitted.Save,
                forkTimelineId,
                effective.NativeSave,
                cancellationToken)
            .ConfigureAwait(false);
        var forkRuntime = await bridge.RestoreInMemoryAsync(
                package,
                forkSave,
                bridgeOptions: effective.NativeSave,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var snapshot = await forkRuntime.ReadSnapshotAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Error(
                InteractiveWorldBundleReasonCodes.InvalidArtifact,
                "The admitted fork has no authoritative snapshot.");
        var binding = CreateBinding(forkSave, snapshot, package);
        var memories = admitted.ExportMode
                       == InteractiveWorldBundleExportMode.PublicExport
            ? Array.Empty<MemoryRecord>()
            : admitted.Memories
                .Select(
                    item => RebindMemoryForFork(
                        item,
                        binding.TimelineId,
                        binding.TimelineEpoch))
                .ToArray();
        var groups = admitted.ExportMode
                     == InteractiveWorldBundleExportMode.PublicExport
            ? Array.Empty<GroupInteractionSession>()
            : admitted.Groups
                .Select(
                    item => new GroupInteractionStateMachine(
                            MaximumGroupInteractionLimits())
                        .RebindWorld(
                            item,
                            new GroupInteractionWorldBinding(
                                binding.WorldId,
                                binding.TimelineId,
                                binding.TimelineEpoch,
                                binding.SaveRevision)))
                .ToArray();

        // Presentation evidence is bound to the parent receipt and exact
        // parent coordinate. It cannot honestly be rewritten as verified on
        // the fork, so the derived cache starts empty and may be regenerated.
        var presentations = Array.Empty<VerifiedWorldPresentation>();
        ValidateSidecars(
            binding,
            snapshot,
            memories,
            groups,
            presentations,
            effective.Limits);
        return BuildArtifact(
            forkSave,
            binding,
            memories,
            groups,
            presentations,
            admitted.ExportMode,
            effective);
    }

    public static async ValueTask<InteractiveWorldBundleImportResult>
        ImportAsync(
            ActivatedWorldPackage package,
            ReadOnlyMemory<byte> artifact,
            string targetDirectory,
            InteractiveWorldBundleImportOptions? options = null,
            CancellationToken cancellationToken = default)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        var effective = SnapshotImportOptions(
            options ?? new InteractiveWorldBundleImportOptions());
        var admitted = await AdmitAsync(
                package,
                artifact,
                effective.Bundle,
                cancellationToken)
            .ConfigureAwait(false);
        PreflightStoreCapacities(admitted, effective);
        var target = ValidateNewTargetDirectory(targetDirectory);
        var seed = target + ".bundle.seed";
        var restoreLockPath = target + ".bundle.restore.lock";
        FileStream? restoreLease = null;
        var published = false;
        try
        {
            restoreLease = AcquireRestoreLease(
                restoreLockPath,
                effective.AuthoritativeStore.LockTimeout,
                cancellationToken);
            _ = ValidateNewTargetDirectory(target);
            ReclaimSeed(seed, target);
            Directory.CreateDirectory(seed);

            var bridge = new NativeWorldSaveBridge();
            _ = await bridge.RestoreFileAsync(
                    package,
                    admitted.Save,
                    Path.Combine(seed, AuthoritativeStoreFileName),
                    effective.AuthoritativeStore,
                    bridgeOptions: effective.Bundle.NativeSave,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await RestoreMemoryAsync(
                    Path.Combine(seed, MemoryStoreFileName),
                    admitted.Memories,
                    effective.MemoryStore,
                    cancellationToken)
                .ConfigureAwait(false);
            await RestoreGroupsAsync(
                    Path.Combine(seed, GroupStoreFileName),
                    admitted.Groups,
                    effective.GroupInteractionStore,
                    cancellationToken)
                .ConfigureAwait(false);
            await RestorePresentationsAsync(
                    Path.Combine(seed, PresentationStoreFileName),
                    admitted.Presentations,
                    effective.PresentationStore,
                    cancellationToken)
                .ConfigureAwait(false);
            VerifySeed(seed, admitted, effective);
            cancellationToken.ThrowIfCancellationRequested();
            _ = ValidateNewTargetDirectory(target);
            Directory.Move(seed, target);
            published = true;
        }
        catch (InteractiveWorldBundleException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or JsonException
            or NativeWorldSaveBridgeException)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.PublicationFailed,
                "The admitted bundle could not be published.",
                exception);
        }
        finally
        {
            if (restoreLease is not null)
            {
                TryRemoveSeed(seed, target);
                if (published)
                {
                    try
                    {
                        restoreLease.Dispose();
                    }
                    catch
                    {
                        // Publication is already visible. Never report a
                        // failed import after the atomic target move.
                    }
                }
                else
                {
                    restoreLease.Dispose();
                }
            }
        }

        return new InteractiveWorldBundleImportResult(
            target,
            admitted.ExportMode,
            admitted.Binding,
            admitted.ArtifactDigest);
    }

    private static InteractiveWorldBundleArtifact BuildArtifact(
        WorldSaveDocument save,
        InteractiveWorldBundleBinding binding,
        IReadOnlyList<MemoryRecord> memories,
        IReadOnlyList<GroupInteractionSession> groups,
        IReadOnlyList<VerifiedWorldPresentation> presentations,
        InteractiveWorldBundleExportMode exportMode,
        InteractiveWorldBundleOptions options)
    {
        var saveBytes = WorldSaveCodec.Write(
            save,
            options.NativeSave.ArtifactLimits);
        var normalizedPresentations = presentations
            .Select(
                static (item, index) => item.WithSequence(
                    checked(index + 1L)))
            .ToArray();
        var bindingDigest = ComputeBindingDigest(binding);
        var memoryBytes = SerializeSidecar(
            new InteractiveWorldMemorySidecar
            {
                Contract = SidecarContract,
                BindingDigest = bindingDigest,
                Records = memories
                    .Select(PersistedMemoryRecord.FromMemoryRecord)
                    .ToList()
            },
            PersistenceJsonContext.Default
                .InteractiveWorldMemorySidecar,
            options.Limits,
            MemoryEntryPath);
        var groupBytes = SerializeSidecar(
            new InteractiveWorldGroupSidecar
            {
                Contract = SidecarContract,
                BindingDigest = bindingDigest,
                Sessions = groups
                    .Select(
                        PersistedGroupInteractionSession.FromSession)
                    .ToList()
            },
            PersistenceJsonContext.Default.InteractiveWorldGroupSidecar,
            options.Limits,
            GroupEntryPath);
        var presentationBytes = SerializeSidecar(
            new InteractiveWorldPresentationSidecar
            {
                Contract = SidecarContract,
                BindingDigest = bindingDigest,
                Presentations = normalizedPresentations
                    .Select(
                        PersistedWorldPresentation.FromPresentation)
                    .ToList()
            },
            PersistenceJsonContext.Default
                .InteractiveWorldPresentationSidecar,
            options.Limits,
            PresentationEntryPath);
        var entries = new[]
        {
            new BundleEntry(SaveEntryPath, saveBytes),
            new BundleEntry(MemoryEntryPath, memoryBytes),
            new BundleEntry(GroupEntryPath, groupBytes),
            new BundleEntry(PresentationEntryPath, presentationBytes)
        };
        var manifest = WriteManifest(
            binding,
            exportMode,
            entries,
            memories.Count,
            groups.Count,
            normalizedPresentations.Length,
            options.Limits);
        var total = checked(
            HeaderLength
            + manifest.Length
            + entries.Sum(static item => item.Bytes.Length));
        if (total > options.Limits.MaxArchiveBytes)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                "The bundle exceeds its archive byte limit.");
        }

        var artifact = new byte[total];
        Magic.CopyTo(artifact, 0);
        WriteInt32LittleEndian(artifact, 8, manifest.Length);
        ComputeSha256Bytes(manifest).CopyTo(artifact, 12);
        manifest.CopyTo(artifact, HeaderLength);
        var offset = HeaderLength + manifest.Length;
        foreach (var entry in entries)
        {
            entry.Bytes.CopyTo(artifact, offset);
            offset += entry.Bytes.Length;
        }

        return new InteractiveWorldBundleArtifact(
            artifact,
            ComputeSha256(artifact),
            exportMode,
            binding);
    }

    private static async ValueTask<AdmittedBundle> AdmitAsync(
        ActivatedWorldPackage package,
        ReadOnlyMemory<byte> artifact,
        InteractiveWorldBundleOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = Decode(artifact, options.Limits);
        var native = await AdmitNativeSaveAsync(
                package,
                decoded.Entry(SaveEntryPath),
                options.NativeSave,
                cancellationToken)
            .ConfigureAwait(false);
        var save = native.Save;
        var snapshot = native.Snapshot;
        var binding = CreateBinding(save, snapshot, package);
        EnsureSameBinding(decoded.Binding, binding);

        var memoryPayload = DeserializeSidecar(
            decoded.Entry(MemoryEntryPath),
            PersistenceJsonContext.Default.InteractiveWorldMemorySidecar,
            options.Limits,
            decoded.MemoryCount,
            MemoryEntryPath);
        var groupPayload = DeserializeSidecar(
            decoded.Entry(GroupEntryPath),
            PersistenceJsonContext.Default.InteractiveWorldGroupSidecar,
            options.Limits,
            decoded.GroupCount,
            GroupEntryPath);
        var presentationPayload = DeserializeSidecar(
            decoded.Entry(PresentationEntryPath),
            PersistenceJsonContext.Default
                .InteractiveWorldPresentationSidecar,
            options.Limits,
            decoded.PresentationCount,
            PresentationEntryPath);
        RequireSidecarContract(memoryPayload.Contract);
        RequireSidecarContract(groupPayload.Contract);
        RequireSidecarContract(presentationPayload.Contract);
        var expectedBindingDigest = ComputeBindingDigest(binding);
        if (!string.Equals(
                memoryPayload.BindingDigest,
                expectedBindingDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                groupPayload.BindingDigest,
                expectedBindingDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                presentationPayload.BindingDigest,
                expectedBindingDigest,
                StringComparison.Ordinal))
        {
            throw Binding(
                "A bundle sidecar does not bind the exact authoritative "
                + "coordinate.");
        }
        MemoryRecord[] memories;
        GroupInteractionSession[] groups;
        VerifiedWorldPresentation[] presentations;
        try
        {
            memories = RequiredList(
                    memoryPayload.Records,
                    MemoryEntryPath)
                .Select(
                    item => (item
                             ?? throw Invalid(
                                 "A memory sidecar contains null."))
                        .ToMemoryRecord())
                .ToArray();
            var stateMachine = new GroupInteractionStateMachine(
                MaximumGroupInteractionLimits());
            groups = RequiredList(
                    groupPayload.Sessions,
                    GroupEntryPath)
                .Select(
                    item => (item
                             ?? throw Invalid(
                                 "A group sidecar contains null."))
                        .Restore(stateMachine))
                .ToArray();
            presentations = RequiredList(
                    presentationPayload.Presentations,
                    PresentationEntryPath)
                .Select(
                    item => (item
                             ?? throw Invalid(
                                 "A presentation sidecar contains null."))
                        .Restore())
                .ToArray();
        }
        catch (InteractiveWorldBundleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            throw Invalid(
                "A bundle sidecar violates its semantic contract.",
                exception);
        }

        if (decoded.ExportMode
                == InteractiveWorldBundleExportMode.PublicExport
            && (memories.Length != 0
                || groups.Length != 0
                || presentations.Length != 0))
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.PrivacyPolicyViolation,
                "A public bundle cannot carry local memory, group "
                + "transcripts, audience identities, or verified "
                + "presentation internals.");
        }

        ValidateSidecars(
            binding,
            snapshot,
            memories,
            groups,
            presentations,
            options.Limits);
        await ValidatePresentationEvidenceAsync(
                native.Runtime,
                presentations,
                cancellationToken)
            .ConfigureAwait(false);
        return new AdmittedBundle(
            save,
            binding,
            decoded.ExportMode,
            memories,
            groups,
            presentations,
            ComputeSha256(artifact.Span));
    }

    private static void ValidateSidecars(
        InteractiveWorldBundleBinding binding,
        WorldAuthoritativeStateSnapshot snapshot,
        IReadOnlyList<MemoryRecord> memories,
        IReadOnlyList<GroupInteractionSession> groups,
        IReadOnlyList<VerifiedWorldPresentation> presentations,
        InteractiveWorldBundleLimits limits)
    {
        if (memories.Count > limits.MaxMemoryRecords
            || groups.Count > limits.MaxGroupSessions
            || presentations.Count > limits.MaxPresentationRecords)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                "A bundle sidecar exceeds its item limit.");
        }

        long entityReferences = 0;
        foreach (var memory in memories)
        {
            var provenance = memory.Provenance;
            if (provenance is null
                || !provenance.Committed
                || provenance.TimelineId is null
                || !provenance.TimelineEpoch.HasValue)
            {
                throw Error(
                    InteractiveWorldBundleReasonCodes.Unsettled,
                    "Every bundled memory must have committed world and "
                    + "exact timeline-epoch provenance.");
            }

            if (!string.Equals(
                    provenance.WorldId,
                    binding.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    provenance.TimelineId,
                    binding.TimelineId,
                    StringComparison.Ordinal)
                || provenance.TimelineEpoch.Value
                != binding.TimelineEpoch
                || provenance.SaveRevision > binding.SaveRevision)
            {
                throw Binding(
                    "A memory record is outside the bundle timeline "
                    + "fence.");
            }

            if (memory.GameTimeWindow is not null)
            {
                ValidateTime(
                    memory.GameTimeWindow.ValidFrom,
                    binding);
                ValidateTime(
                    memory.GameTimeWindow.ValidUntil,
                    binding);
            }

            if (provenance.Perspective is not null)
            {
                ValidateIdentity(
                    provenance.Perspective.Observer,
                    snapshot);
                entityReferences++;
                if (provenance.Perspective.Source is not null)
                {
                    ValidateIdentity(
                        provenance.Perspective.Source,
                        snapshot);
                    entityReferences++;
                }
            }
        }

        long groupMessages = 0;
        long groupRevisions = 0;
        string? previousSessionId = null;
        foreach (var session in groups)
        {
            var worldBinding = session.WorldBinding;
            if (worldBinding is null)
            {
                throw Binding(
                    "Every bundled group interaction requires an exact "
                    + "world-timeline binding.");
            }

            if (!string.Equals(
                    worldBinding.WorldId,
                    binding.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    worldBinding.TimelineId,
                    binding.TimelineId,
                    StringComparison.Ordinal)
                || worldBinding.TimelineEpoch
                != binding.TimelineEpoch
                || worldBinding.SaveRevision > binding.SaveRevision)
            {
                throw Binding(
                    "A group interaction is outside the bundle timeline "
                    + "fence.");
            }

            if (!string.Equals(
                    session.Status,
                    GroupInteractionStatuses.Open,
                    StringComparison.Ordinal)
                && !string.Equals(
                    session.Status,
                    GroupInteractionStatuses.Closed,
                    StringComparison.Ordinal))
            {
                throw Invalid(
                    "A bundled group interaction has an unsupported "
                    + "lifecycle status.");
            }

            if (previousSessionId is not null
                && string.CompareOrdinal(
                    previousSessionId,
                    session.SessionId) >= 0)
            {
                throw Invalid(
                    "Group sessions are not uniquely ordered.");
            }

            previousSessionId = session.SessionId;
            groupMessages = checked(groupMessages + session.Messages.Count);
            groupRevisions = checked(groupRevisions + session.Revision + 1);
            foreach (var history in session.MembershipHistory)
            {
                foreach (var member in history.Members)
                {
                    ValidateIdentity(member.Actor, snapshot);
                    entityReferences++;
                }
            }

            foreach (var member in session.Members)
            {
                ValidateIdentity(member.Actor, snapshot);
                entityReferences++;
            }

            foreach (var message in session.Messages)
            {
                if (message.Author is not null)
                {
                    ValidateIdentity(message.Author, snapshot);
                    entityReferences++;
                }

                foreach (var audience in message.Audience)
                {
                    ValidateIdentity(audience, snapshot);
                    entityReferences++;
                }
            }
        }

        if (groupMessages > limits.MaxGroupMessages
            || groupRevisions > limits.MaxGroupRevisionFrames)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                "The aggregate group sidecar exceeds its message or "
                + "revision limit.");
        }

        long previousSequence = 0;
        var groupsBySession =
            new Dictionary<string, GroupInteractionSession>(
                StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (!groupsBySession.TryAdd(group.SessionId, group))
            {
                throw Invalid(
                    "Group session identifiers must be unique.");
            }
        }
        foreach (var presentation in presentations)
        {
            if (presentation.Sequence != checked(previousSequence + 1))
            {
                throw Invalid(
                    "Presentation records are not uniquely ordered.");
            }

            previousSequence = presentation.Sequence;
            var coordinate = presentation.Binding;
            if (!string.Equals(
                    coordinate.WorldId,
                    binding.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    coordinate.TimelineId,
                    binding.TimelineId,
                    StringComparison.Ordinal)
                || coordinate.TimelineEpoch != binding.TimelineEpoch
                || coordinate.SaveRevision > binding.SaveRevision
                || coordinate.StateVersion > binding.StateVersion
                || !string.Equals(
                    coordinate.CatalogDigest,
                    binding.CatalogDigest,
                    StringComparison.Ordinal))
            {
                throw Binding(
                    "A presentation record is outside the bundle "
                    + "timeline fence.");
            }

            ValidateTime(coordinate.GameTime, binding);
            foreach (var audience in presentation.Audience.Members)
            {
                ValidateIdentity(audience, snapshot);
                entityReferences++;
            }

            if (groupsBySession.TryGetValue(
                    presentation.Audience.MembershipScopeId,
                    out var group))
            {
                var membership = group.MembershipHistory.SingleOrDefault(
                    item => item.MembershipRevision
                            == presentation.Audience.MembershipRevision);
                if (membership is null
                    || !SameIdentities(
                        membership.Members.Select(
                            static item => item.Actor),
                        presentation.Audience.Members))
                {
                    throw Binding(
                        "A presentation audience does not match its "
                        + "bundled group membership snapshot.");
                }
            }
        }

        if (entityReferences > limits.MaxEntityReferences)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                "The bundle exceeds its entity-reference limit.");
        }
    }

    private static InteractiveWorldBundleBinding CreateBinding(
        WorldSaveDocument save,
        WorldAuthoritativeStateSnapshot snapshot,
        ActivatedWorldPackage package)
    {
        if (!long.TryParse(
                save.StateVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stateVersion)
            || !string.Equals(
                save.PackageId,
                package.SourcePackage.PackageId,
                StringComparison.Ordinal)
            || !string.Equals(
                save.PackageContentVersion,
                package.SourcePackage.ContentVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                save.PackageDigest,
                package.SourcePackage.PackageDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                save.WorldId,
                snapshot.Coordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                save.TimelineId,
                snapshot.Coordinate.TimelineId,
                StringComparison.Ordinal)
            || save.SaveRevision != snapshot.Coordinate.SaveRevision
            || stateVersion != snapshot.Coordinate.StateVersion
            || !string.Equals(
                snapshot.Coordinate.CatalogDigest,
                package.CatalogDigest,
                StringComparison.Ordinal))
        {
            throw Binding(
                "The native save, snapshot, and package bindings differ.");
        }

        return new InteractiveWorldBundleBinding(
            save.PackageId,
            save.PackageContentVersion,
            save.PackageDigest,
            save.WorldId,
            save.TimelineId,
            snapshot.Coordinate.TimelineEpoch,
            save.SaveRevision,
            stateVersion,
            package.CatalogDigest,
            snapshot.StateDigest,
            save.SaveDigest);
    }

    private static async ValueTask<NativeSaveAdmission>
        AdmitNativeSaveAsync(
            ActivatedWorldPackage package,
            byte[] saveBytes,
            NativeWorldSaveBridgeOptions options,
            CancellationToken cancellationToken)
    {
        try
        {
            var save = WorldSaveCodec.Read(
                saveBytes,
                options.ArtifactLimits);
            if (!WorldSaveCodec.Write(save, options.ArtifactLimits)
                .AsSpan()
                .SequenceEqual(saveBytes))
            {
                throw Invalid(
                    "The native save entry is not canonical.");
            }

            var runtime = await new NativeWorldSaveBridge()
                .RestoreInMemoryAsync(
                    package,
                    save,
                    bridgeOptions: options,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await runtime.ReadSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw Invalid(
                    "The bundle save has no authoritative snapshot.");
            return new NativeSaveAdmission(save, snapshot, runtime);
        }
        catch (InteractiveWorldBundleException)
        {
            throw;
        }
        catch (NativeWorldSaveBridgeException exception)
        {
            var reason = exception.ReasonCode switch
            {
                NativeWorldSaveBridgeReasonCodes.BindingMismatch =>
                    InteractiveWorldBundleReasonCodes.BindingMismatch,
                NativeWorldSaveBridgeReasonCodes.CapacityExceeded =>
                    InteractiveWorldBundleReasonCodes.CapacityExceeded,
                NativeWorldSaveBridgeReasonCodes.PendingTransactions =>
                    InteractiveWorldBundleReasonCodes.Unsettled,
                _ => InteractiveWorldBundleReasonCodes.InvalidArtifact
            };
            throw Error(
                reason,
                "The bundle native save failed admission.",
                exception);
        }
        catch (WorldDataContractException exception)
        {
            throw Error(
                string.Equals(
                    exception.ReasonCode,
                    WorldDataReasonCodes.PackageBindingMismatch,
                    StringComparison.Ordinal)
                    ? InteractiveWorldBundleReasonCodes.BindingMismatch
                    : InteractiveWorldBundleReasonCodes.InvalidArtifact,
                "The bundle native save violates its data contract.",
                exception);
        }
        catch (Exception exception) when (
            exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or OverflowException
            or FormatException)
        {
            throw Invalid(
                "The bundle native save is malformed.",
                exception);
        }
    }

    private static async ValueTask<WorldSaveDocument>
        CaptureSettledSaveAsync(
            NativeWorldSaveBridge bridge,
            NativeWorldRuntime runtime,
            NativeWorldSaveBridgeOptions options,
            CancellationToken cancellationToken)
    {
        try
        {
            return await bridge.CaptureAsync(
                    runtime,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NativeWorldSaveBridgeException exception)
        {
            var reason = exception.ReasonCode switch
            {
                NativeWorldSaveBridgeReasonCodes.PendingTransactions =>
                    InteractiveWorldBundleReasonCodes.Unsettled,
                NativeWorldSaveBridgeReasonCodes.CapacityExceeded =>
                    InteractiveWorldBundleReasonCodes.CapacityExceeded,
                NativeWorldSaveBridgeReasonCodes.BindingMismatch =>
                    InteractiveWorldBundleReasonCodes.BindingMismatch,
                _ => InteractiveWorldBundleReasonCodes.InvalidArtifact
            };
            throw Error(
                reason,
                "The authoritative world cannot provide a settled bundle "
                + "capture.",
                exception);
        }
    }

    private static async ValueTask ValidatePresentationEvidenceAsync(
        NativeWorldRuntime runtime,
        IReadOnlyList<VerifiedWorldPresentation> presentations,
        CancellationToken cancellationToken)
    {
        foreach (var presentation in presentations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = presentation.Source;
            if (source.OperationId is null)
            {
                throw Binding(
                    "A bundled presentation source does not identify its "
                    + "authoritative world operation.");
            }

            var binding = presentation.Binding;
            var inspection = await runtime.TransactionStore.InspectAsync(
                    new WorldTransactionScope(
                        binding.WorldId,
                        binding.TimelineId,
                        binding.TimelineEpoch),
                    source.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            var receipt = inspection.Status
                          == WorldTransactionInspectionStatus
                              .TerminalReceipt
                ? inspection.Receipt
                : null;
            if (receipt is null
                || receipt.Status != WorldCommandReceiptStatus.Applied)
            {
                throw Binding(
                    "A bundled presentation has no terminal applied "
                    + "receipt in the authoritative ledger.");
            }

            CommittedWorldPresentationEvidence projected;
            try
            {
                projected =
                    WorldCommandPresentationEvidence.CreateApplied(
                        receipt,
                        binding.GameTime);
            }
            catch (ArgumentException exception)
            {
                throw Binding(
                    "A bundled presentation cannot be projected from its "
                    + "authoritative receipt.",
                    exception);
            }

            if (!source.IsSameAs(projected.Source)
                || !binding.IsSameAs(projected.Binding)
                || !string.Equals(
                    presentation.EvidenceDigest,
                    projected.SemanticDigest,
                    StringComparison.Ordinal))
            {
                throw Binding(
                    "A bundled presentation differs from its "
                    + "authoritative receipt evidence.");
            }
        }
    }

    private static MemoryRecord RebindMemoryForFork(
        MemoryRecord memory,
        string timelineId,
        long timelineEpoch)
    {
        var provenance = memory.Provenance
                         ?? throw Error(
                             InteractiveWorldBundleReasonCodes.Unsettled,
                             "A fork memory lacks provenance.");
        var reboundProvenance = new MemoryProvenance(
            provenance.WorldId,
            provenance.SessionId,
            saveRevision: 0,
            provenance.SourceRunId,
            provenance.SourceEventId,
            committed: true,
            timelineId,
            provenance.Perspective,
            timelineEpoch);
        GameTimeWindow? reboundWindow = null;
        if (memory.GameTimeWindow is not null)
        {
            reboundWindow = new GameTimeWindow(
                RebindTime(
                    memory.GameTimeWindow.ValidFrom,
                    timelineId,
                    timelineEpoch),
                RebindTime(
                    memory.GameTimeWindow.ValidUntil,
                    timelineId,
                    timelineEpoch));
        }

        return new MemoryRecord(
            memory.MemoryId,
            memory.Scope,
            memory.Content,
            memory.Tags,
            memory.Importance,
            memory.CreatedAt,
            memory.UpdatedAt,
            memory.ExpiresAt,
            reboundProvenance,
            reboundWindow);
    }

    private static GameTimePoint? RebindTime(
        GameTimePoint? time,
        string timelineId,
        long timelineEpoch)
    {
        return time is null
            ? null
            : new GameTimePoint(
                time.ClockId,
                timelineId,
                timelineEpoch,
                time.Tick);
    }

    private static void ValidateTime(
        GameTimePoint? time,
        InteractiveWorldBundleBinding binding)
    {
        if (time is not null
            && (!string.Equals(
                    time.TimelineId,
                    binding.TimelineId,
                    StringComparison.Ordinal)
                || time.Epoch != binding.TimelineEpoch))
        {
            throw Binding(
                "A game-time value is outside the bundle timeline epoch.");
        }
    }

    private static void ValidateIdentity(
        GameEntityIdentity identity,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        if (!snapshot.WasIncarnationIssued(
                identity.EntityId,
                identity.Incarnation))
        {
            throw Binding(
                "A sidecar entity identity was not issued by the "
                + "authoritative timeline.");
        }
    }

    private static bool SameIdentities(
        IEnumerable<GameEntityIdentity> left,
        IEnumerable<GameEntityIdentity> right)
    {
        return left
            .OrderBy(static item => item.EntityId, StringComparer.Ordinal)
            .ThenBy(static item => item.Incarnation)
            .Select(
                static item => item.EntityId
                               + "\n"
                               + item.Incarnation.ToString(
                                   CultureInfo.InvariantCulture))
            .SequenceEqual(
                right
                    .OrderBy(
                        static item => item.EntityId,
                        StringComparer.Ordinal)
                    .ThenBy(static item => item.Incarnation)
                    .Select(
                        static item => item.EntityId
                                       + "\n"
                                       + item.Incarnation.ToString(
                                           CultureInfo.InvariantCulture)),
                StringComparer.Ordinal);
    }

    private static GroupInteractionLimits
        MaximumGroupInteractionLimits()
    {
        return new GroupInteractionLimits(
            maxMembers: 4_096,
            maxMessages: 65_536,
            maxOperations: 131_072,
            maxMessagesPerAppend: 4_096,
            maxPayloadUtf8Bytes: 262_144,
            maxTotalPayloadUtf8Bytes: 256 * 1_048_576,
            maxSharedScopeUtf8Bytes: 262_144,
            maxJsonDepth: 32,
            maxJsonNodesPerValue: 8_192,
            maxMembershipHistoryMembers: 1_048_576);
    }

    private static byte[] SerializeSidecar<T>(
        T value,
        JsonTypeInfo<T> typeInfo,
        InteractiveWorldBundleLimits limits,
        string entryPath)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (bytes.Length > limits.MaxEntryBytes)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                $"Bundle entry '{entryPath}' exceeds its byte limit.");
        }

        ValidateJson(bytes, limits, expectedCount: null, entryPath);
        return bytes;
    }

    private static T DeserializeSidecar<T>(
        byte[] bytes,
        JsonTypeInfo<T> typeInfo,
        InteractiveWorldBundleLimits limits,
        int expectedCount,
        string entryPath)
        where T : class
    {
        ValidateJson(bytes, limits, expectedCount, entryPath);
        try
        {
            var result = JsonSerializer.Deserialize(bytes, typeInfo)
                         ?? throw Invalid(
                             $"Bundle entry '{entryPath}' is null.");
            if (!JsonSerializer.SerializeToUtf8Bytes(result, typeInfo)
                .AsSpan()
                .SequenceEqual(bytes))
            {
                throw Invalid(
                    $"Bundle entry '{entryPath}' is not canonical.");
            }

            return result;
        }
        catch (InteractiveWorldBundleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            throw Invalid(
                $"Bundle entry '{entryPath}' is malformed.",
                exception);
        }
    }

    private static void ValidateJson(
        ReadOnlySpan<byte> bytes,
        InteractiveWorldBundleLimits limits,
        int? expectedCount,
        string entryPath)
    {
        if (bytes.Length > limits.MaxEntryBytes)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                $"Bundle entry '{entryPath}' exceeds its byte limit.");
        }

        try
        {
            var reader = new Utf8JsonReader(
                bytes,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaxJsonDepth
                });
            var tokens = 0;
            while (reader.Read())
            {
                tokens++;
                if (tokens > limits.MaxJsonTokensPerEntry)
                {
                    throw Error(
                        InteractiveWorldBundleReasonCodes.CapacityExceeded,
                        $"Bundle entry '{entryPath}' exceeds its JSON "
                        + "token limit.");
                }
            }

            using var document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaxJsonDepth
                });
            RejectDuplicateProperties(document.RootElement);
            if (expectedCount.HasValue)
            {
                var arrayName = string.Equals(
                    entryPath,
                    MemoryEntryPath,
                    StringComparison.Ordinal)
                    ? "records"
                    : string.Equals(
                        entryPath,
                        GroupEntryPath,
                        StringComparison.Ordinal)
                        ? "sessions"
                        : "presentations";
                if (document.RootElement.ValueKind
                        != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(
                        arrayName,
                        out var array)
                    || array.ValueKind != JsonValueKind.Array
                    || array.GetArrayLength() != expectedCount)
                {
                    throw Invalid(
                        $"Bundle entry '{entryPath}' count does not match "
                        + "the manifest.");
                }
            }
        }
        catch (InteractiveWorldBundleException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"Bundle entry '{entryPath}' is invalid JSON.",
                exception);
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid(
                        "Bundle JSON contains a duplicate property.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void ValidateMode(
        InteractiveWorldBundleExportMode exportMode)
    {
        if (exportMode is not
                InteractiveWorldBundleExportMode.PrivateLocal
            and not InteractiveWorldBundleExportMode.PublicExport)
        {
            throw new ArgumentOutOfRangeException(nameof(exportMode));
        }
    }

    private static InteractiveWorldBundleException Error(
        string reasonCode,
        string message,
        Exception? innerException = null)
    {
        return new InteractiveWorldBundleException(
            reasonCode,
            message,
            innerException);
    }

    private static InteractiveWorldBundleException Invalid(
        string message,
        Exception? innerException = null)
    {
        return Error(
            InteractiveWorldBundleReasonCodes.InvalidArtifact,
            message,
            innerException);
    }

    private static InteractiveWorldBundleException Binding(
        string message,
        Exception? innerException = null)
    {
        return Error(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            message,
            innerException);
    }

    private static void WriteInt32LittleEndian(
        byte[] destination,
        int offset,
        int value)
    {
        destination[offset] = (byte)(value & 0xFF);
        destination[offset + 1] = (byte)(value >> 8 & 0xFF);
        destination[offset + 2] = (byte)(value >> 16 & 0xFF);
        destination[offset + 3] = (byte)(value >> 24 & 0xFF);
    }

    private static int ReadInt32LittleEndian(
        ReadOnlySpan<byte> source,
        int offset)
    {
        return source[offset]
               | source[offset + 1] << 8
               | source[offset + 2] << 16
               | source[offset + 3] << 24;
    }

    private static byte[] ComputeSha256Bytes(ReadOnlySpan<byte> bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes.ToArray());
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes)
    {
        var digest = ComputeSha256Bytes(bytes);
        var builder = new StringBuilder(digest.Length * 2);
        foreach (var value in digest)
        {
            builder.Append(
                value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string ComputeBindingDigest(
        InteractiveWorldBundleBinding binding)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("packageId", binding.PackageId);
            writer.WriteString(
                "packageContentVersion",
                binding.PackageContentVersion);
            writer.WriteString("packageDigest", binding.PackageDigest);
            writer.WriteString("worldId", binding.WorldId);
            writer.WriteString("timelineId", binding.TimelineId);
            writer.WriteString(
                "timelineEpoch",
                binding.TimelineEpoch.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString(
                "saveRevision",
                binding.SaveRevision.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString(
                "stateVersion",
                binding.StateVersion.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString("catalogDigest", binding.CatalogDigest);
            writer.WriteString("stateDigest", binding.StateDigest);
            writer.WriteString("saveDigest", binding.SaveDigest);
            writer.WriteEndObject();
        }

        return ComputeSha256(stream.ToArray());
    }

    private sealed class BundleEntry
    {
        public BundleEntry(string path, byte[] bytes)
        {
            Path = path;
            Bytes = bytes;
            Digest = ComputeSha256(bytes);
        }

        public string Path { get; }

        public byte[] Bytes { get; }

        public string Digest { get; }
    }

    private sealed class AdmittedBundle
    {
        public AdmittedBundle(
            WorldSaveDocument save,
            InteractiveWorldBundleBinding binding,
            InteractiveWorldBundleExportMode exportMode,
            MemoryRecord[] memories,
            GroupInteractionSession[] groups,
            VerifiedWorldPresentation[] presentations,
            string artifactDigest)
        {
            Save = save;
            Binding = binding;
            ExportMode = exportMode;
            Memories = memories;
            Groups = groups;
            Presentations = presentations;
            ArtifactDigest = artifactDigest;
        }

        public WorldSaveDocument Save { get; }

        public InteractiveWorldBundleBinding Binding { get; }

        public InteractiveWorldBundleExportMode ExportMode { get; }

        public MemoryRecord[] Memories { get; }

        public GroupInteractionSession[] Groups { get; }

        public VerifiedWorldPresentation[] Presentations { get; }

        public string ArtifactDigest { get; }
    }

    private sealed class NativeSaveAdmission
    {
        public NativeSaveAdmission(
            WorldSaveDocument save,
            WorldAuthoritativeStateSnapshot snapshot,
            NativeWorldRuntime runtime)
        {
            Save = save;
            Snapshot = snapshot;
            Runtime = runtime;
        }

        public WorldSaveDocument Save { get; }

        public WorldAuthoritativeStateSnapshot Snapshot { get; }

        public NativeWorldRuntime Runtime { get; }
    }

    private static byte[] WriteManifest(
        InteractiveWorldBundleBinding binding,
        InteractiveWorldBundleExportMode exportMode,
        IReadOnlyList<BundleEntry> entries,
        int memoryCount,
        int groupCount,
        int presentationCount,
        InteractiveWorldBundleLimits limits)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", ContractId);
            writer.WriteString("formatVersion", "1");
            writer.WriteString(
                "exportMode",
                exportMode
                == InteractiveWorldBundleExportMode.PrivateLocal
                    ? "private-local"
                    : "public-export");
            writer.WritePropertyName("binding");
            writer.WriteStartObject();
            writer.WriteString("packageId", binding.PackageId);
            writer.WriteString(
                "packageContentVersion",
                binding.PackageContentVersion);
            writer.WriteString("packageDigest", binding.PackageDigest);
            writer.WriteString("worldId", binding.WorldId);
            writer.WriteString("timelineId", binding.TimelineId);
            writer.WriteString(
                "timelineEpoch",
                binding.TimelineEpoch.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString(
                "saveRevision",
                binding.SaveRevision.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString(
                "stateVersion",
                binding.StateVersion.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString("catalogDigest", binding.CatalogDigest);
            writer.WriteString("stateDigest", binding.StateDigest);
            writer.WriteString("saveDigest", binding.SaveDigest);
            writer.WriteEndObject();
            writer.WritePropertyName("counts");
            writer.WriteStartObject();
            writer.WriteString(
                "memories",
                memoryCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "groups",
                groupCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "presentations",
                presentationCount.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path);
                writer.WriteString(
                    "length",
                    entry.Bytes.Length.ToString(
                        CultureInfo.InvariantCulture));
                writer.WriteString("sha256", entry.Digest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var result = stream.ToArray();
        if (result.Length > limits.MaxManifestBytes)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                "The bundle manifest exceeds its byte limit.");
        }

        return result;
    }

    private static DecodedBundle Decode(
        ReadOnlyMemory<byte> artifact,
        InteractiveWorldBundleLimits limits)
    {
        if (artifact.Length < HeaderLength
            || artifact.Length > limits.MaxArchiveBytes)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                "The bundle archive is outside its byte bounds.");
        }

        var span = artifact.Span;
        if (!span[..Magic.Length].SequenceEqual(Magic))
        {
            throw Invalid("The bundle magic header is invalid.");
        }

        var manifestLength = ReadInt32LittleEndian(span, 8);
        if (manifestLength < 1
            || manifestLength > limits.MaxManifestBytes
            || manifestLength > artifact.Length - HeaderLength)
        {
            throw Invalid(
                "The bundle manifest length is invalid or truncated.");
        }

        var manifestBytes = span.Slice(
            HeaderLength,
            manifestLength);
        if (!FixedEquals(
                span.Slice(12, 32),
                ComputeSha256Bytes(manifestBytes)))
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.DigestMismatch,
                "The bundle manifest digest does not match.");
        }

        ValidateJson(
            manifestBytes,
            limits,
            expectedCount: null,
            "manifest.json");
        ManifestAdmission manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes.ToArray());
            manifest = ParseManifest(document.RootElement, limits);
        }
        catch (InteractiveWorldBundleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or OverflowException
            or FormatException)
        {
            throw Invalid("The bundle manifest is malformed.", exception);
        }

        var payloadOffset = HeaderLength + manifestLength;
        var payloadLength = manifest.Entries.Sum(
            static item => (long)item.Length);
        if (payloadLength != artifact.Length - payloadOffset)
        {
            throw Invalid(
                "The bundle payload is truncated or has trailing bytes.");
        }

        var entries = new Dictionary<string, byte[]>(
            StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            var bytes = artifact.Slice(payloadOffset, entry.Length)
                .ToArray();
            payloadOffset += entry.Length;
            if (!string.Equals(
                    ComputeSha256(bytes),
                    entry.Digest,
                    StringComparison.Ordinal))
            {
                throw Error(
                    InteractiveWorldBundleReasonCodes.DigestMismatch,
                    $"Bundle entry '{entry.Path}' has a digest mismatch.");
            }

            entries.Add(entry.Path, bytes);
        }

        return new DecodedBundle(
            manifest.Binding,
            manifest.ExportMode,
            manifest.MemoryCount,
            manifest.GroupCount,
            manifest.PresentationCount,
            entries);
    }

    private static void EnsureSameBinding(
        InteractiveWorldBundleBinding left,
        InteractiveWorldBundleBinding right)
    {
        if (!string.Equals(
                left.PackageId,
                right.PackageId,
                StringComparison.Ordinal)
            || !string.Equals(
                left.PackageContentVersion,
                right.PackageContentVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                left.PackageDigest,
                right.PackageDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                left.WorldId,
                right.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                left.TimelineId,
                right.TimelineId,
                StringComparison.Ordinal)
            || left.TimelineEpoch != right.TimelineEpoch
            || left.SaveRevision != right.SaveRevision
            || left.StateVersion != right.StateVersion
            || !string.Equals(
                left.CatalogDigest,
                right.CatalogDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                left.StateDigest,
                right.StateDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                left.SaveDigest,
                right.SaveDigest,
                StringComparison.Ordinal))
        {
            throw Binding(
                "The bundle manifest does not bind its native save.");
        }
    }

    private static void PreflightStoreCapacities(
        AdmittedBundle admitted,
        InteractiveWorldBundleImportOptions options)
    {
        var groupFrames = admitted.Groups.Sum(
            static item => checked(item.Revision + 1));
        if (admitted.Memories.Length > options.MemoryStore.Capacity
            || admitted.Memories.LongLength
            > options.MemoryStore.MaxMutationFrames
            || admitted.Groups.Length
            > options.GroupInteractionStore.MaxSessions
            || groupFrames
            > options.GroupInteractionStore.MaxMutationFrames
            || admitted.Presentations.LongLength
            > options.PresentationStore.MaxRecords)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                "The target store options cannot admit the bundle "
                + "sidecars.");
        }
    }

    private static InteractiveWorldBundleImportOptions
        SnapshotImportOptions(
            InteractiveWorldBundleImportOptions options)
    {
        var memory = options.MemoryStore;
        var groups = options.GroupInteractionStore;
        var presentations = options.PresentationStore;
        var authoritative = options.AuthoritativeStore;
        var schedules = authoritative.Schedules;
        return new InteractiveWorldBundleImportOptions(
            options.Bundle,
            new FileWorldAuthoritativeTransactionStoreOptions(
                authoritative.MaxStates,
                authoritative.MaxOperations,
                authoritative.MaxHistoryRecords,
                authoritative.MaxFileBytes,
                authoritative.LockTimeout,
                new WorldScheduleStoreOptions(
                    schedules.MaxSchedules,
                    schedules.MaxOperations,
                    schedules.MaxAggregatePayloadBytes),
                authoritative.MaxIssuedEntityIncarnations),
            new FileMemoryStoreOptions
            {
                ProviderId = memory.ProviderId,
                Capacity = memory.Capacity,
                FlushToDiskOnMutation = memory.FlushToDiskOnMutation,
                MaxFramePayloadBytes = memory.MaxFramePayloadBytes,
                MaxLogBytes = memory.MaxLogBytes,
                MaxMutationFrames = memory.MaxMutationFrames,
                SearchMode = memory.SearchMode,
                Bm25Options = CloneBm25Options(memory.Bm25Options),
                FaultInjector = memory.FaultInjector
            },
            new FileGroupInteractionStoreOptions
            {
                Limits = CloneGroupLimits(
                    groups.Limits
                    ?? throw new ArgumentException(
                        "Group-interaction store limits are required.",
                        nameof(options))),
                FlushToDiskOnMutation = groups.FlushToDiskOnMutation,
                MaxFramePayloadBytes = groups.MaxFramePayloadBytes,
                MaxLogBytes = groups.MaxLogBytes,
                MaxMutationFrames = groups.MaxMutationFrames,
                MaxSessions = groups.MaxSessions,
                FaultInjector = groups.FaultInjector
            },
            new FileWorldPresentationStoreOptions
            {
                Limits = ClonePresentationLimits(
                    presentations.Limits
                    ?? throw new ArgumentException(
                        "Presentation store limits are required.",
                        nameof(options))),
                FlushToDiskOnMutation =
                    presentations.FlushToDiskOnMutation,
                MaxFramePayloadBytes =
                    presentations.MaxFramePayloadBytes,
                MaxLogBytes = presentations.MaxLogBytes,
                MaxRecords = presentations.MaxRecords,
                MaxFrameJsonTokens =
                    presentations.MaxFrameJsonTokens,
                MaxResidentBytes = presentations.MaxResidentBytes,
                FaultInjector = presentations.FaultInjector
            });
    }

    private static Bm25MemoryStoreOptions? CloneBm25Options(
        Bm25MemoryStoreOptions? options)
    {
        return options is null
            ? null
            : new Bm25MemoryStoreOptions(
                options.MaxDocumentUtf8Bytes,
                options.MaxDocumentTerms,
                options.MaxUniqueDocumentTerms,
                options.MaxQueryUtf8Bytes,
                options.MaxQueryTerms,
                options.MaxUniqueQueryTerms,
                options.MaxTermUtf8Bytes,
                options.MaxIndexUtf8Bytes,
                options.MaxIndexTerms,
                options.MaxComparisonsPerSearch,
                options.ContentWeight,
                options.TagWeight,
                options.ContentLengthNormalization,
                options.TagLengthNormalization,
                options.K1,
                options.ScoreScale);
    }

    private static GroupInteractionLimits CloneGroupLimits(
        GroupInteractionLimits limits)
    {
        return new GroupInteractionLimits(
            limits.MaxMembers,
            limits.MaxMessages,
            limits.MaxOperations,
            limits.MaxMessagesPerAppend,
            limits.MaxPayloadUtf8Bytes,
            limits.MaxTotalPayloadUtf8Bytes,
            limits.MaxSharedScopeUtf8Bytes,
            limits.MaxJsonDepth,
            limits.MaxJsonNodesPerValue,
            limits.MaxMembershipHistoryMembers);
    }

    private static WorldPresentationLimits ClonePresentationLimits(
        WorldPresentationLimits limits)
    {
        return new WorldPresentationLimits(
            limits.MaxAudienceMembers,
            limits.MaxMediaCues,
            limits.MaxParentPresentationIds,
            limits.MaxPayloadUtf8Bytes,
            limits.MaxMetadataUtf8Bytes,
            limits.MaxJsonDepth,
            limits.MaxJsonNodes,
            limits.MaxAggregateUtf8Bytes,
            limits.MaxAggregateJsonNodes);
    }

    private static string ValidateNewTargetDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.UnsafePath,
                "A target directory is required.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.UnsafePath,
                "The target directory path is invalid.",
                exception);
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.UnsafePath,
                "The target parent directory must already exist.");
        }

        for (var current = new DirectoryInfo(parent);
             current is not null;
             current = current.Parent)
        {
            if (!current.Exists
                || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Error(
                    InteractiveWorldBundleReasonCodes.UnsafePath,
                    "The target path cannot traverse a symbolic link or "
                    + "reparse point.");
            }
        }

        if (PathExists(fullPath))
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.TargetExists,
                "The target directory already exists.");
        }

        return fullPath;
    }

    private static FileStream AcquireRestoreLease(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (started.Elapsed < timeout)
            {
                Thread.Sleep(10);
            }
            catch (IOException exception)
            {
                throw Error(
                    InteractiveWorldBundleReasonCodes.TargetExists,
                    "Another importer owns the target publication path.",
                    exception);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                or NotSupportedException)
            {
                throw Error(
                    InteractiveWorldBundleReasonCodes.UnsafePath,
                    "The target publication lease cannot be opened.",
                    exception);
            }
        }
    }

    private static void ReclaimSeed(string seed, string target)
    {
        EnsureSeedPath(seed, target);
        try
        {
            if (File.Exists(seed))
            {
                throw new IOException(
                    "The abandoned bundle seed is not a directory.");
            }

            if (Directory.Exists(seed))
            {
                if ((File.GetAttributes(seed)
                     & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The abandoned bundle seed is a reparse point.");
                }

                Directory.Delete(seed, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.TargetExists,
                "An abandoned bundle seed could not be reclaimed.",
                exception);
        }
    }

    private static async ValueTask RestoreMemoryAsync(
        string path,
        IReadOnlyList<MemoryRecord> memories,
        FileMemoryStoreOptions options,
        CancellationToken cancellationToken)
    {
        var store = new FileMemoryStore(path, options);
        try
        {
            foreach (var memory in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await store.UpsertAsync(memory, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask RestoreGroupsAsync(
        string path,
        IReadOnlyList<GroupInteractionSession> groups,
        FileGroupInteractionStoreOptions options,
        CancellationToken cancellationToken)
    {
        var store = new FileGroupInteractionStore(path, options);
        try
        {
            await store.RestoreInteractiveWorldBundleAsync(
                    groups,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask RestorePresentationsAsync(
        string path,
        IReadOnlyList<VerifiedWorldPresentation> presentations,
        FileWorldPresentationStoreOptions options,
        CancellationToken cancellationToken)
    {
        var store = new FileWorldPresentationStore(path, options);
        try
        {
            foreach (var presentation in presentations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await store.PublishVerifiedAsync(
                        presentation,
                        presentation.ContentRevision - 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        result.Status,
                        WorldPresentationWriteStatuses.Applied,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A presentation sidecar did not restore as one "
                        + "ordered history.");
                }
            }
        }
        finally
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void VerifySeed(
        string seed,
        AdmittedBundle admitted,
        InteractiveWorldBundleImportOptions options)
    {
        using (var memory = new FileMemoryStore(
                   Path.Combine(seed, MemoryStoreFileName),
                   options.MemoryStore))
        {
            var captured = memory.CaptureInteractiveWorldBundleAsync(
                    admitted.Memories.Length,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (!MemoryEquivalent(captured, admitted.Memories))
            {
                throw new InvalidOperationException(
                    "The seeded memory sidecar failed verification.");
            }
        }

        using (var groups = new FileGroupInteractionStore(
                   Path.Combine(seed, GroupStoreFileName),
                   options.GroupInteractionStore))
        {
            var captured = groups.CaptureInteractiveWorldBundleAsync(
                    admitted.Groups.Length,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (!GroupEquivalent(captured, admitted.Groups))
            {
                throw new InvalidOperationException(
                    "The seeded group sidecar failed verification.");
            }
        }

        using var presentations = new FileWorldPresentationStore(
            Path.Combine(seed, PresentationStoreFileName),
            options.PresentationStore);
        var capturedPresentations = presentations
            .CaptureInteractiveWorldBundleAsync(
                admitted.Presentations.Length,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (!PresentationEquivalent(
                capturedPresentations,
                admitted.Presentations))
        {
            throw new InvalidOperationException(
                "The seeded presentation sidecar failed verification.");
        }
    }

    private static void TryRemoveSeed(string seed, string target)
    {
        try
        {
            EnsureSeedPath(seed, target);
            if (Directory.Exists(seed))
            {
                if ((File.GetAttributes(seed)
                     & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                Directory.Delete(seed, recursive: true);
            }
        }
        catch
        {
            // The published target is never removed. A bounded, fixed-name
            // seed may be reclaimed by the next importer holding the lease.
        }
    }

    private static void RequireSidecarContract(string contract)
    {
        if (!string.Equals(
                contract,
                SidecarContract,
                StringComparison.Ordinal))
        {
            throw Invalid("A bundle sidecar contract is unsupported.");
        }
    }

    private static List<T> RequiredList<T>(
        List<T>? value,
        string entryPath)
    {
        return value
               ?? throw Invalid(
                   $"Bundle entry '{entryPath}' has no item array.");
    }

    private static ManifestAdmission ParseManifest(
        JsonElement root,
        InteractiveWorldBundleLimits limits)
    {
        RequireObjectFields(
            root,
            "contract",
            "formatVersion",
            "exportMode",
            "binding",
            "counts",
            "entries");
        if (!string.Equals(
                RequiredString(root, "contract", 128),
                ContractId,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(root, "formatVersion", 16),
                "1",
                StringComparison.Ordinal))
        {
            throw Invalid("The bundle manifest contract is unsupported.");
        }

        var modeText = RequiredString(root, "exportMode", 32);
        var mode = modeText switch
        {
            "private-local" =>
                InteractiveWorldBundleExportMode.PrivateLocal,
            "public-export" =>
                InteractiveWorldBundleExportMode.PublicExport,
            _ => throw Invalid(
                "The bundle export mode is unsupported.")
        };
        var bindingValue = RequiredObject(root, "binding");
        RequireObjectFields(
            bindingValue,
            "packageId",
            "packageContentVersion",
            "packageDigest",
            "worldId",
            "timelineId",
            "timelineEpoch",
            "saveRevision",
            "stateVersion",
            "catalogDigest",
            "stateDigest",
            "saveDigest");
        var binding = new InteractiveWorldBundleBinding(
            RequiredString(bindingValue, "packageId", 256),
            RequiredString(
                bindingValue,
                "packageContentVersion",
                128),
            RequiredDigest(bindingValue, "packageDigest"),
            RequiredString(bindingValue, "worldId", 256),
            RequiredString(bindingValue, "timelineId", 256),
            RequiredInt64String(bindingValue, "timelineEpoch"),
            RequiredInt64String(bindingValue, "saveRevision"),
            RequiredInt64String(bindingValue, "stateVersion"),
            RequiredDigest(bindingValue, "catalogDigest"),
            RequiredDigest(bindingValue, "stateDigest"),
            RequiredDigest(bindingValue, "saveDigest"));

        var counts = RequiredObject(root, "counts");
        RequireObjectFields(
            counts,
            "memories",
            "groups",
            "presentations");
        var memoryCount = RequiredCount(
            counts,
            "memories",
            limits.MaxMemoryRecords);
        var groupCount = RequiredCount(
            counts,
            "groups",
            limits.MaxGroupSessions);
        var presentationCount = RequiredCount(
            counts,
            "presentations",
            limits.MaxPresentationRecords);

        var entriesValue = RequiredProperty(root, "entries");
        if (entriesValue.ValueKind != JsonValueKind.Array
            || entriesValue.GetArrayLength() != 4)
        {
            throw Invalid(
                "The bundle manifest must contain exactly four entries.");
        }

        var requiredPaths = new[]
        {
            SaveEntryPath,
            MemoryEntryPath,
            GroupEntryPath,
            PresentationEntryPath
        };
        var entries = new List<ManifestEntry>(4);
        var index = 0;
        long totalBytes = 0;
        foreach (var value in entriesValue.EnumerateArray())
        {
            RequireObjectFields(value, "path", "length", "sha256");
            var path = RequiredString(value, "path", 128);
            if (!string.Equals(
                    path,
                    requiredPaths[index],
                    StringComparison.Ordinal))
            {
                throw Invalid(
                    "Bundle entries are missing, duplicated, or out of "
                    + "canonical order.");
            }

            var lengthValue = RequiredInt64String(value, "length");
            if (lengthValue > limits.MaxEntryBytes)
            {
                throw Error(
                    InteractiveWorldBundleReasonCodes.CapacityExceeded,
                    $"Bundle entry '{path}' exceeds its byte limit.");
            }

            var length = checked((int)lengthValue);
            totalBytes = checked(totalBytes + length);
            if (totalBytes > limits.MaxArchiveBytes)
            {
                throw Error(
                    InteractiveWorldBundleReasonCodes.CapacityExceeded,
                    "The bundle payload exceeds its aggregate byte limit.");
            }

            entries.Add(
                new ManifestEntry(
                    path,
                    length,
                    RequiredDigest(value, "sha256")));
            index++;
        }

        return new ManifestAdmission(
            binding,
            mode,
            memoryCount,
            groupCount,
            presentationCount,
            entries);
    }

    private static void RequireObjectFields(
        JsonElement value,
        params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A bundle manifest value must be an object.");
        }

        var admitted = new HashSet<string>(
            fields,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!admitted.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                throw Invalid(
                    "The bundle manifest contains an unknown or duplicate "
                    + "field.");
            }
        }

        if (seen.Count != admitted.Count)
        {
            throw Invalid(
                "The bundle manifest is missing a required field.");
        }
    }

    private static JsonElement RequiredProperty(
        JsonElement value,
        string name)
    {
        return value.TryGetProperty(name, out var property)
            ? property
            : throw Invalid(
                $"The bundle manifest is missing '{name}'.");
    }

    private static JsonElement RequiredObject(
        JsonElement value,
        string name)
    {
        var property = RequiredProperty(value, name);
        return property.ValueKind == JsonValueKind.Object
            ? property
            : throw Invalid(
                $"Bundle manifest field '{name}' must be an object.");
    }

    private static string RequiredString(
        JsonElement value,
        string name,
        int maxUtf8Bytes)
    {
        var property = RequiredProperty(value, name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                $"Bundle manifest field '{name}' must be a string.");
        }

        var result = property.GetString();
        if (string.IsNullOrWhiteSpace(result)
            || Encoding.UTF8.GetByteCount(result) > maxUtf8Bytes)
        {
            throw Invalid(
                $"Bundle manifest field '{name}' is outside its bounds.");
        }

        return result;
    }

    private static string RequiredDigest(
        JsonElement value,
        string name)
    {
        var digest = RequiredString(value, name, 64);
        if (digest.Length != 64
            || digest.Any(
                static character =>
                    character is not
                        (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw Invalid(
                $"Bundle manifest field '{name}' is not a lowercase "
                + "SHA-256 digest.");
        }

        return digest;
    }

    private static long RequiredInt64String(
        JsonElement value,
        string name)
    {
        var text = RequiredString(value, name, 20);
        if (!long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result)
            || result < 0
            || !string.Equals(
                text,
                result.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"Bundle manifest field '{name}' is not a canonical "
                + "non-negative integer.");
        }

        return result;
    }

    private static int RequiredCount(
        JsonElement value,
        string name,
        int maximum)
    {
        var count = RequiredInt64String(value, name);
        if (count > maximum)
        {
            throw Error(
                InteractiveWorldBundleReasonCodes.CapacityExceeded,
                $"Bundle count '{name}' exceeds its limit.");
        }

        return checked((int)count);
    }

    private static bool FixedEquals(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }

    private static bool PathExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }

        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureSeedPath(string seed, string target)
    {
        var expected = Path.GetFullPath(target + ".bundle.seed");
        var actual = Path.GetFullPath(seed);
        if (!string.Equals(
                expected,
                actual,
                OperatingSystemPathComparison))
        {
            throw new InvalidOperationException(
                "The bundle seed path escaped its target sibling.");
        }
    }

    private static StringComparison OperatingSystemPathComparison =>
        Environment.OSVersion.Platform == PlatformID.Win32NT
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool MemoryEquivalent(
        IReadOnlyList<MemoryRecord> left,
        IReadOnlyList<MemoryRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftPayload = new InteractiveWorldMemorySidecar
        {
            Contract = SidecarContract,
            Records = left
                .Select(PersistedMemoryRecord.FromMemoryRecord)
                .ToList()
        };
        var rightPayload = new InteractiveWorldMemorySidecar
        {
            Contract = SidecarContract,
            Records = right
                .Select(PersistedMemoryRecord.FromMemoryRecord)
                .ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(
                leftPayload,
                PersistenceJsonContext.Default
                    .InteractiveWorldMemorySidecar)
            .AsSpan()
            .SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(
                    rightPayload,
                    PersistenceJsonContext.Default
                        .InteractiveWorldMemorySidecar));
    }

    private static bool GroupEquivalent(
        IReadOnlyList<GroupInteractionSession> left,
        IReadOnlyList<GroupInteractionSession> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftPayload = new InteractiveWorldGroupSidecar
        {
            Contract = SidecarContract,
            Sessions = left
                .Select(PersistedGroupInteractionSession.FromSession)
                .ToList()
        };
        var rightPayload = new InteractiveWorldGroupSidecar
        {
            Contract = SidecarContract,
            Sessions = right
                .Select(PersistedGroupInteractionSession.FromSession)
                .ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(
                leftPayload,
                PersistenceJsonContext.Default.InteractiveWorldGroupSidecar)
            .AsSpan()
            .SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(
                    rightPayload,
                    PersistenceJsonContext.Default
                        .InteractiveWorldGroupSidecar));
    }

    private static bool PresentationEquivalent(
        IReadOnlyList<VerifiedWorldPresentation> left,
        IReadOnlyList<VerifiedWorldPresentation> right)
    {
        return left.Count == right.Count
               && left.Zip(
                       right,
                       static (first, second) =>
                           first.Sequence == second.Sequence
                           && first.ContentRevision
                           == second.ContentRevision
                           && string.Equals(
                               first.SemanticDigest,
                               second.SemanticDigest,
                               StringComparison.Ordinal))
                   .All(static value => value);
    }

    private sealed class ManifestAdmission
    {
        public ManifestAdmission(
            InteractiveWorldBundleBinding binding,
            InteractiveWorldBundleExportMode exportMode,
            int memoryCount,
            int groupCount,
            int presentationCount,
            IReadOnlyList<ManifestEntry> entries)
        {
            Binding = binding;
            ExportMode = exportMode;
            MemoryCount = memoryCount;
            GroupCount = groupCount;
            PresentationCount = presentationCount;
            Entries = entries;
        }

        public InteractiveWorldBundleBinding Binding { get; }

        public InteractiveWorldBundleExportMode ExportMode { get; }

        public int MemoryCount { get; }

        public int GroupCount { get; }

        public int PresentationCount { get; }

        public IReadOnlyList<ManifestEntry> Entries { get; }
    }

    private sealed class ManifestEntry
    {
        public ManifestEntry(string path, int length, string digest)
        {
            Path = path;
            Length = length;
            Digest = digest;
        }

        public string Path { get; }

        public int Length { get; }

        public string Digest { get; }
    }

    private sealed class DecodedBundle
    {
        private readonly IReadOnlyDictionary<string, byte[]> _entries;

        public DecodedBundle(
            InteractiveWorldBundleBinding binding,
            InteractiveWorldBundleExportMode exportMode,
            int memoryCount,
            int groupCount,
            int presentationCount,
            IReadOnlyDictionary<string, byte[]> entries)
        {
            Binding = binding;
            ExportMode = exportMode;
            MemoryCount = memoryCount;
            GroupCount = groupCount;
            PresentationCount = presentationCount;
            _entries = entries;
        }

        public InteractiveWorldBundleBinding Binding { get; }

        public InteractiveWorldBundleExportMode ExportMode { get; }

        public int MemoryCount { get; }

        public int GroupCount { get; }

        public int PresentationCount { get; }

        public byte[] Entry(string path)
        {
            return _entries.TryGetValue(path, out var value)
                ? value
                : throw Invalid(
                    $"The bundle entry '{path}' is missing.");
        }
    }
}
