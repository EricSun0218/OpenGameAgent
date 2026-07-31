using System.Threading;
using System.Threading.Tasks;
using GameAgent.World;
using UnityEngine.Scripting;

namespace GameAgent.Unity
{
    /// <summary>
    /// A linker-preserved Unity entry point for the engine-neutral
    /// native-world composition root.
    /// </summary>
    [Preserve]
    public static class UnityNativeWorldRuntime
    {
        [Preserve]
        public static NativeWorldRuntime CreateInMemory(
            ActivatedWorldPackage package,
            string timelineId = null,
            long timelineEpoch = 0,
            NativeWorldRuntimeOptions options = null)
        {
            return NativeWorldRuntime.CreateInMemory(
                package,
                timelineId,
                timelineEpoch,
                options);
        }

        [Preserve]
        public static ValueTask<NativeWorldRuntime> CreateFileAsync(
            ActivatedWorldPackage package,
            string path,
            string timelineId = null,
            long timelineEpoch = 0,
            FileWorldAuthoritativeTransactionStoreOptions storeOptions = null,
            NativeWorldRuntimeOptions runtimeOptions = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return NativeWorldRuntime.CreateFileAsync(
                package,
                path,
                timelineId,
                timelineEpoch,
                storeOptions,
                runtimeOptions,
                cancellationToken);
        }
    }
}
