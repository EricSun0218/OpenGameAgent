using System.Diagnostics;

namespace GameAgent.World;

public static class NativeWorldSaveBridgeReasonCodes
{
    public const string UnsupportedStore =
        "native_world_save_unsupported_store";
    public const string PendingTransactions =
        "native_world_save_pending_transactions";
    public const string BindingMismatch =
        "native_world_save_binding_mismatch";
    public const string InvalidArtifact =
        "native_world_save_invalid_artifact";
    public const string IncompleteHistory =
        "native_world_save_incomplete_history";
    public const string CapacityExceeded =
        "native_world_save_capacity_exceeded";
    public const string UnsafePath =
        "native_world_save_unsafe_path";
    public const string TargetExists =
        "native_world_save_target_exists";
}

public sealed class NativeWorldSaveBridgeException : Exception
{
    public NativeWorldSaveBridgeException(
        string reasonCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
    }

    public string ReasonCode { get; }
}

public enum NativeWorldSaveCaptureMode
{
    RequireSettled = 0
}

/// <summary>
/// Bounded policy for converting one authoritative native timeline to or
/// from a portable save. Version one deliberately supports settled captures
/// only; pending work is rejected rather than represented as resumable.
/// </summary>
public sealed class NativeWorldSaveBridgeOptions
{
    public NativeWorldSaveBridgeOptions(
        NativeWorldSaveCaptureMode captureMode =
            NativeWorldSaveCaptureMode.RequireSettled,
        int maxTransactionRecords = 4_096,
        int maxHistoryRecords = 16_384,
        int maxEntityIncarnations = 4_096,
        WorldPackageLimits? artifactLimits = null,
        int maxScheduleRecords = 4_096,
        int maxScheduleOperations = 16_384,
        int maxIssuedEntityIncarnations = 65_536)
    {
        if (captureMode
            != NativeWorldSaveCaptureMode.RequireSettled)
        {
            throw new ArgumentOutOfRangeException(nameof(captureMode));
        }

        CaptureMode = captureMode;
        MaxTransactionRecords = InRange(
            maxTransactionRecords,
            1,
            100_000,
            nameof(maxTransactionRecords));
        MaxHistoryRecords = InRange(
            maxHistoryRecords,
            1,
            100_000,
            nameof(maxHistoryRecords));
        MaxEntityIncarnations = InRange(
            maxEntityIncarnations,
            1,
            WorldValidation.MaximumParticipants,
            nameof(maxEntityIncarnations));
        MaxIssuedEntityIncarnations = InRange(
            maxIssuedEntityIncarnations,
            1,
            WorldAuthoritativeStateSnapshot
                .MaximumIssuedIncarnationCount,
            nameof(maxIssuedEntityIncarnations));
        MaxScheduleRecords = InRange(
            maxScheduleRecords,
            1,
            100_000,
            nameof(maxScheduleRecords));
        MaxScheduleOperations = InRange(
            maxScheduleOperations,
            1,
            100_000,
            nameof(maxScheduleOperations));
        ArtifactLimits = artifactLimits ?? new WorldPackageLimits();
    }

    public NativeWorldSaveCaptureMode CaptureMode { get; }

    public int MaxTransactionRecords { get; }

    public int MaxHistoryRecords { get; }

    public int MaxEntityIncarnations { get; }

    public int MaxIssuedEntityIncarnations { get; }

    public int MaxScheduleRecords { get; }

    public int MaxScheduleOperations { get; }

    public WorldPackageLimits ArtifactLimits { get; }

    private static int InRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

/// <summary>
/// Portable save boundary for a live native-world runtime. Capture observes
/// one atomic settled store image. Restore validates the complete artifact
/// before it creates or publishes a target store.
/// </summary>
public interface INativeWorldSaveBridge
{
    ValueTask<WorldSaveDocument> CaptureAsync(
        NativeWorldRuntime runtime,
        NativeWorldSaveBridgeOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<NativeWorldRuntime> RestoreInMemoryAsync(
        ActivatedWorldPackage package,
        WorldSaveDocument save,
        NativeWorldRuntimeOptions? runtimeOptions = null,
        NativeWorldSaveBridgeOptions? bridgeOptions = null,
        CancellationToken cancellationToken = default);

    ValueTask<NativeWorldRuntime> RestoreFileAsync(
        ActivatedWorldPackage package,
        WorldSaveDocument save,
        string targetStorePath,
        FileWorldAuthoritativeTransactionStoreOptions?
            storeOptions = null,
        NativeWorldRuntimeOptions? runtimeOptions = null,
        NativeWorldSaveBridgeOptions? bridgeOptions = null,
        CancellationToken cancellationToken = default);

    ValueTask<WorldSaveDocument> ForkAsync(
        ActivatedWorldPackage package,
        WorldSaveDocument source,
        string forkTimelineId,
        NativeWorldSaveBridgeOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class NativeWorldSaveBridge : INativeWorldSaveBridge
{
    public async ValueTask<WorldSaveDocument> CaptureAsync(
        NativeWorldRuntime runtime,
        NativeWorldSaveBridgeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        var effective = options ?? new NativeWorldSaveBridgeOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var capture = await runtime.CaptureSettledStoreAsync(
                effective.MaxTransactionRecords,
                effective.MaxHistoryRecords,
                effective.MaxScheduleRecords,
                effective.MaxScheduleOperations,
                cancellationToken)
            .ConfigureAwait(false);
        return NativeWorldSaveArtifactCodec.CreateDocument(
            runtime.Package,
            capture,
            NativeWorldSaveArtifactKind.Settled,
            parentTimelineId: null,
            parentSaveRevision: null,
            parentSaveDigest: null,
            effective,
            cancellationToken);
    }

    public ValueTask<NativeWorldRuntime> RestoreInMemoryAsync(
        ActivatedWorldPackage package,
        WorldSaveDocument save,
        NativeWorldRuntimeOptions? runtimeOptions = null,
        NativeWorldSaveBridgeOptions? bridgeOptions = null,
        CancellationToken cancellationToken = default)
    {
        var effective =
            bridgeOptions ?? new NativeWorldSaveBridgeOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var capture = NativeWorldSaveArtifactCodec.Read(
            package,
            save,
            effective,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<NativeWorldRuntime>(
            NativeWorldRuntime.RestoreInMemory(
                package,
                capture,
                runtimeOptions,
                new WorldScheduleStoreOptions(
                    effective.MaxScheduleRecords,
                    effective.MaxScheduleOperations,
                    effective.ArtifactLimits.MaxFileBytes)));
    }

    public async ValueTask<NativeWorldRuntime> RestoreFileAsync(
        ActivatedWorldPackage package,
        WorldSaveDocument save,
        string targetStorePath,
        FileWorldAuthoritativeTransactionStoreOptions?
            storeOptions = null,
        NativeWorldRuntimeOptions? runtimeOptions = null,
        NativeWorldSaveBridgeOptions? bridgeOptions = null,
        CancellationToken cancellationToken = default)
    {
        var effective =
            bridgeOptions ?? new NativeWorldSaveBridgeOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var capture = NativeWorldSaveArtifactCodec.Read(
            package,
            save,
            effective,
            cancellationToken);
        var target = NativeWorldSavePath.ValidateNewStorePath(
            targetStorePath);
        var effectiveStoreOptions =
            storeOptions
            ?? new FileWorldAuthoritativeTransactionStoreOptions();
        await DispatchAsync(
                () =>
                {
                    PublishSeededStore(
                        target,
                        capture,
                        effectiveStoreOptions,
                        effective,
                        cancellationToken);
                    return new ValueTask<bool>(true);
                })
            .ConfigureAwait(false);

        var coordinate = capture.Snapshot.Coordinate;
        return await NativeWorldRuntime.CreateFileAsync(
                package,
                target,
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                effectiveStoreOptions,
                runtimeOptions,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    public ValueTask<WorldSaveDocument> ForkAsync(
        ActivatedWorldPackage package,
        WorldSaveDocument source,
        string forkTimelineId,
        NativeWorldSaveBridgeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? new NativeWorldSaveBridgeOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var capture = NativeWorldSaveArtifactCodec.Read(
            package,
            source,
            effective,
            cancellationToken);
        var normalizedTimeline = WorldValidation.Required(
            forkTimelineId,
            nameof(forkTimelineId),
            256);
        if (string.Equals(
                normalizedTimeline,
                capture.Snapshot.Coordinate.TimelineId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A fork requires a distinct timeline identifier.",
                nameof(forkTimelineId));
        }

        var fork = NativeWorldSaveArtifactCodec.Fork(
            capture,
            normalizedTimeline,
            effective,
            cancellationToken);
        return new ValueTask<WorldSaveDocument>(
            NativeWorldSaveArtifactCodec.CreateDocument(
                package,
                fork,
                NativeWorldSaveArtifactKind.Fork,
                source.TimelineId,
                source.SaveRevision,
                source.SaveDigest,
                effective,
                cancellationToken));
    }

    private static void PublishSeededStore(
        string targetPath,
        WorldAuthoritativeStoreCapture capture,
        FileWorldAuthoritativeTransactionStoreOptions storeOptions,
        NativeWorldSaveBridgeOptions bridgeOptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        targetPath = NativeWorldSavePath.ValidateNewStorePath(
            targetPath);

        var seedPath = targetPath + ".seed";
        var seedLockPath = seedPath + ".lock";
        var seedNextPath = seedPath + ".next";
        var restoreLockPath = targetPath + ".restore.lock";
        FileStream? restoreLease = null;
        try
        {
            restoreLease = AcquireRestoreLock(
                restoreLockPath,
                storeOptions.LockTimeout,
                cancellationToken);
            targetPath = NativeWorldSavePath.ValidateNewStorePath(
                targetPath);
            RemoveStaleSeed(seedNextPath);
            RemoveStaleSeed(seedPath);
            RemoveStaleSeed(seedLockPath);
            var seed =
                new FileWorldAuthoritativeTransactionStore(
                    seedPath,
                    new[] { capture.Snapshot },
                    storeOptions);
            seed.ReplaceWithSettledCapture(
                capture,
                cancellationToken);
            var verified = ((IWorldAuthoritativeStoreCaptureSource)seed)
                .CaptureSettledAsync(
                    capture.Snapshot.Coordinate.Address,
                    capture.Snapshot.Coordinate.TimelineEpoch,
                    bridgeOptions.MaxTransactionRecords,
                    bridgeOptions.MaxHistoryRecords,
                    bridgeOptions.MaxScheduleRecords,
                    bridgeOptions.MaxScheduleOperations,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            NativeWorldSaveArtifactCodec.EnsureEquivalent(
                capture,
                verified,
                bridgeOptions);
            cancellationToken.ThrowIfCancellationRequested();
            _ = NativeWorldSavePath.ValidateNewStorePath(targetPath);
            File.Move(seedPath, targetPath);
        }
        catch (NativeWorldSaveBridgeException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.TargetExists,
                "The target authoritative store could not be published.",
                exception);
        }
        finally
        {
            if (restoreLease is not null)
            {
                TryDelete(seedPath);
                TryDelete(seedLockPath);
                TryDelete(seedNextPath);
                restoreLease.Dispose();
            }
        }
    }

    private static FileStream AcquireRestoreLock(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
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
                throw new NativeWorldSaveBridgeException(
                    NativeWorldSaveBridgeReasonCodes.TargetExists,
                    "Another restore owns the target publication path.",
                    exception);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                or NotSupportedException)
            {
                throw new NativeWorldSaveBridgeException(
                    NativeWorldSaveBridgeReasonCodes.UnsafePath,
                    "The restore ownership artifact cannot be opened safely.",
                    exception);
            }
        }
    }

    private static void RemoveStaleSeed(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(path))
            {
                throw new IOException(
                    "An uncommitted seed path is a directory.");
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.TargetExists,
                "An abandoned restore seed could not be reclaimed.",
                exception);
        }
    }

    private static async ValueTask<T> DispatchAsync<T>(
        Func<ValueTask<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ = WorldBackgroundWorkDispatcher.Dispatch(
                async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await operation().ConfigureAwait(false));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled();
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A seed file is never authoritative. Preserve the publication
            // result and let host maintenance remove an abandoned seed.
        }
    }

}

internal static class NativeWorldSavePath
{
    public static string ValidateNewStorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Unsafe("A target store path is required.");
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
            throw Unsafe("The target store path is invalid.", exception);
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)
            || !Directory.Exists(parent))
        {
            throw Unsafe(
                "The target store parent directory must already exist.");
        }

        for (var current = new DirectoryInfo(parent);
             current is not null;
             current = current.Parent)
        {
            if (!current.Exists
                || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Unsafe(
                    "The target store path cannot traverse a symbolic link.");
            }
        }

        if (Exists(fullPath) || Exists(fullPath + ".lock"))
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.TargetExists,
                "The target store or its ownership artifact already exists.");
        }

        return fullPath;
    }

    public static bool Exists(string path)
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

    private static NativeWorldSaveBridgeException Unsafe(
        string message,
        Exception? innerException = null)
    {
        return new NativeWorldSaveBridgeException(
            NativeWorldSaveBridgeReasonCodes.UnsafePath,
            message,
            innerException);
    }
}
