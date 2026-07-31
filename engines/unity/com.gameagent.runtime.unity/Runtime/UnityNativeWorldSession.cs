using System;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.World;
using UnityEngine;
using UnityEngine.Scripting;

namespace GameAgent.Unity
{
    /// <summary>
    /// Unity-facing owner for the high-level native-world session. A package
    /// or save is fully admitted before it replaces the active generation.
    /// </summary>
    [Preserve]
    public sealed class UnityNativeWorldSessionFacade : IAsyncDisposable
    {
        private readonly NativeWorldEngineSession _session;

        public UnityNativeWorldSessionFacade(
            NativeWorldEngineSessionOptions options = null)
        {
            _session = new NativeWorldEngineSession(options);
        }

        public NativeWorldEngineSession Typed
        {
            get { return _session; }
        }

        public NativeWorldEngineSessionStatus Status
        {
            get { return _session.Status; }
        }

        public ValueTask<NativeWorldEnginePackageLoadResult>
            LoadPackageAsync(
                byte[] archive,
                string timelineId = null,
                long timelineEpoch = 0,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            if (archive == null)
            {
                throw new ArgumentNullException(nameof(archive));
            }

            return _session.LoadPackageAsync(
                archive,
                timelineId,
                timelineEpoch,
                capabilities: null,
                cancellationToken);
        }

        public ValueTask<NativeWorldEnginePackageLoadResult>
            LoadPackageFileAsync(
                string path,
                string timelineId = null,
                long timelineEpoch = 0,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.LoadPackageFileAsync(
                path,
                timelineId,
                timelineEpoch,
                capabilities: null,
                cancellationToken);
        }

        public ValueTask<NativeWorldEngineSaveLoadResult> LoadSaveAsync(
            byte[] utf8,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            if (utf8 == null)
            {
                throw new ArgumentNullException(nameof(utf8));
            }

            return _session.LoadSaveAsync(utf8, cancellationToken);
        }

        public ValueTask<NativeWorldEngineSaveLoadResult>
            LoadSaveFileAsync(
                string path,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.LoadSaveFileAsync(path, cancellationToken);
        }

        public ValueTask<byte[]> CaptureSaveAsync(
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.CaptureSaveBytesAsync(cancellationToken);
        }

        public ValueTask CaptureSaveFileAsync(
            string path,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.CaptureSaveFileAsync(path, cancellationToken);
        }

        public ValueTask<NativeWorldEngineShutdownReport> ShutdownAsync(
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.ShutdownAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _session.DisposeAsync();
        }
    }

    /// <summary>
    /// Scene lifecycle owner for <see cref="UnityNativeWorldSessionFacade"/>.
    /// Call and await ShutdownAsync during controlled scene/application quit.
    /// </summary>
    [Preserve]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-31989)]
    public sealed class UnityNativeWorldSessionHost : MonoBehaviour
    {
        private UnityNativeWorldSessionFacade _facade;
        private int _disposeStarted;

        public bool IsConfigured
        {
            get { return _facade != null; }
        }

        public UnityNativeWorldSessionFacade Facade
        {
            get
            {
                if (_facade == null)
                {
                    throw new InvalidOperationException(
                        "Configure the native-world host before use.");
                }

                return _facade;
            }
        }

        public void Configure(
            NativeWorldEngineSessionOptions options = null)
        {
            if (_facade != null)
            {
                throw new InvalidOperationException(
                    "The native-world host is already configured.");
            }

            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(UnityNativeWorldSessionHost));
            }

            _facade = new UnityNativeWorldSessionFacade(options);
        }

        public ValueTask<NativeWorldEngineShutdownReport> ShutdownAsync(
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return Facade.ShutdownAsync(cancellationToken);
        }

        private void OnDestroy()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            var facade = _facade;
            _facade = null;
            if (facade != null)
            {
                // Unity cannot await OnDestroy. This is an emergency detach;
                // controlled owners must await ShutdownAsync first.
                _ = facade.DisposeAsync();
            }
        }
    }
}
