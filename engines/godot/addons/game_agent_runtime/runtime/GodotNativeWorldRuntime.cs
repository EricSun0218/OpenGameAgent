using GameAgent.World;

namespace GameAgent.Godot;

/// <summary>
/// A reflection-free Godot entry point for the engine-neutral native-world
/// composition root.
/// </summary>
public static class GodotNativeWorldRuntime
{
    public static NativeWorldRuntime CreateInMemory(
        ActivatedWorldPackage package,
        string? timelineId = null,
        long timelineEpoch = 0,
        NativeWorldRuntimeOptions? options = null)
    {
        return NativeWorldRuntime.CreateInMemory(
            package,
            timelineId,
            timelineEpoch,
            options);
    }

    public static ValueTask<NativeWorldRuntime> CreateFileAsync(
        ActivatedWorldPackage package,
        string path,
        string? timelineId = null,
        long timelineEpoch = 0,
        FileWorldAuthoritativeTransactionStoreOptions? storeOptions = null,
        NativeWorldRuntimeOptions? runtimeOptions = null,
        CancellationToken cancellationToken = default)
    {
        return NativeWorldRuntime.CreateFileAsync(
            package,
            GlobalizePath(path),
            timelineId,
            timelineEpoch,
            storeOptions,
            runtimeOptions,
            cancellationToken);
    }

    private static string GlobalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A file path is required.",
                nameof(path));
        }

        return path.StartsWith("res://", StringComparison.Ordinal)
               || path.StartsWith("user://", StringComparison.Ordinal)
            ? global::Godot.ProjectSettings.GlobalizePath(path)
            : path;
    }
}
