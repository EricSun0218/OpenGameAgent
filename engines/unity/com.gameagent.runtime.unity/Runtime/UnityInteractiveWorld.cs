using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.World;
using UnityEngine;
using UnityEngine.Scripting;

namespace GameAgent.Unity
{
    /// <summary>
    /// Unity-facing owner for the shared managed interactive-world session.
    /// No world rules or state mutations are implemented in this adapter.
    /// </summary>
    [Preserve]
    public sealed class UnityInteractiveWorldFacade : IAsyncDisposable
    {
        private readonly InteractiveWorldEngineSession _session;

        public UnityInteractiveWorldFacade(
            InteractiveWorldFacade portableFacade,
            int backgroundCapacity = 256)
        {
            _session = new InteractiveWorldEngineSession(
                portableFacade
                ?? throw new ArgumentNullException(
                    nameof(portableFacade)),
                backgroundCapacity);
        }

        public WorldPackageDefinition CurrentPackage
        {
            get { return _session.CurrentPackage; }
        }

        public WorldSaveDocument CurrentSave
        {
            get { return _session.CurrentSave; }
        }

        public int OutstandingOperationCount
        {
            get { return _session.OutstandingOperationCount; }
        }

        public WorldPackageDefinition ImportPackage(
            byte[] archive,
            WorldPackageLimits limits = null)
        {
            if (archive == null)
            {
                throw new ArgumentNullException(nameof(archive));
            }

            return _session.ImportPackage(archive, limits);
        }

        public WorldPackageDefinition ImportPackageFile(
            string path,
            WorldPackageLimits limits = null)
        {
            return _session.ImportPackageFile(path, limits);
        }

        public byte[] ExportPackage(WorldPackageLimits limits = null)
        {
            return _session.ExportPackage(limits);
        }

        public void ExportPackageFile(
            string path,
            WorldPackageLimits limits = null)
        {
            _session.ExportPackageFile(path, limits);
        }

        public WorldSaveDocument ImportSave(
            byte[] utf8,
            WorldPackageLimits limits = null)
        {
            if (utf8 == null)
            {
                throw new ArgumentNullException(nameof(utf8));
            }

            return _session.ImportSave(utf8, limits);
        }

        public WorldSaveDocument ImportSaveFile(
            string path,
            WorldPackageLimits limits = null)
        {
            return _session.ImportSaveFile(path, limits);
        }

        public void SetSave(WorldSaveDocument save)
        {
            _session.SetSave(save);
        }

        public byte[] ExportSave(WorldPackageLimits limits = null)
        {
            return _session.ExportSave(limits);
        }

        public void ExportSaveFile(
            string path,
            WorldPackageLimits limits = null)
        {
            _session.ExportSaveFile(path, limits);
        }

        public ValueTask<InteractiveWorldResult<WorldEventPlan>>
            PlanTriggerAsync(
                WorldEvolutionTrigger trigger,
                IReadOnlyList<WorldEventDefinition> definitions,
                WorldStateFence currentState,
                int cascadeDepth = 0,
                string parentInstanceId = null,
                object hostContext = null,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.PlanTriggerAsync(
                trigger,
                definitions,
                currentState,
                cascadeDepth,
                parentInstanceId,
                hostContext,
                cancellationToken);
        }

        public ValueTask<InteractiveWorldResult<WorldEventPlan>>
            PlanTriggerAsync(
                WorldEvolutionTrigger trigger,
                WorldEventCatalogSnapshot catalog,
                WorldStateFence currentState,
                int cascadeDepth = 0,
                string parentInstanceId = null,
                object hostContext = null,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.PlanTriggerAsync(
                trigger,
                catalog,
                currentState,
                cascadeDepth,
                parentInstanceId,
                hostContext,
                cancellationToken);
        }

        public ValueTask<InteractiveWorldResult<InteractionQueryResult>>
            QueryInteractionsAsync(
                InteractionCatalogSnapshot catalog,
                InteractionQueryRequest request,
                WorldStateFence currentState,
                IInteractionAdmissionEvaluator evaluator,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.QueryInteractionsAsync(
                catalog,
                request,
                currentState,
                evaluator,
                cancellationToken);
        }

        public ValueTask<InteractiveWorldResult<WorldInteractionPlan>>
            PlanInteractionAsync(
                InteractionCatalogSnapshot catalog,
                InteractionExecutionRequest request,
                WorldStateFence currentState,
                object hostContext = null,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.PlanInteractionAsync(
                catalog,
                request,
                currentState,
                hostContext,
                cancellationToken);
        }

        public ValueTask<
            InteractiveWorldResult<WorldPlanExecutionResult>>
            ExecutePlanAsync(
                WorldEventPlan plan,
                object hostContext = null,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.ExecutePlanAsync(
                plan,
                hostContext,
                cancellationToken);
        }

        public ValueTask<InteractiveWorldResult<
                WorldAuthoritativePlanExecutionResult>>
            ExecuteAuthoritativePlanAsync(
                WorldAuthoritativeEventPlan artifact,
                object hostContext = null,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.ExecuteAuthoritativePlanAsync(
                artifact,
                hostContext,
                cancellationToken);
        }

        public ValueTask<InteractiveWorldResult<
                WorldAuthoritativePlanExecutionResult>>
            ExecuteAuthoritativeInteractionAsync(
                WorldInteractionPlan interaction,
                WorldAuthoritativeCoordinate expectedCoordinate,
                object hostContext = null,
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            return _session.ExecuteAuthoritativeInteractionAsync(
                interaction,
                expectedCoordinate,
                hostContext,
                cancellationToken);
        }

        public bool TryScheduleTrigger(
            string operationId,
            WorldEvolutionTrigger trigger,
            IReadOnlyList<WorldEventDefinition> definitions,
            WorldStateFence currentState,
            out string rejectionReason,
            int cascadeDepth = 0,
            string parentInstanceId = null,
            object hostContext = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.TryScheduleTrigger(
                operationId,
                trigger,
                definitions,
                currentState,
                out rejectionReason,
                cascadeDepth,
                parentInstanceId,
                hostContext,
                cancellationToken);
        }

        public bool TryScheduleTrigger(
            string operationId,
            WorldEvolutionTrigger trigger,
            WorldEventCatalogSnapshot catalog,
            WorldStateFence currentState,
            out string rejectionReason,
            int cascadeDepth = 0,
            string parentInstanceId = null,
            object hostContext = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.TryScheduleTrigger(
                operationId,
                trigger,
                catalog,
                currentState,
                out rejectionReason,
                cascadeDepth,
                parentInstanceId,
                hostContext,
                cancellationToken);
        }

        public bool TryScheduleInteractionQuery(
            string operationId,
            InteractionCatalogSnapshot catalog,
            InteractionQueryRequest request,
            WorldStateFence currentState,
            IInteractionAdmissionEvaluator evaluator,
            out string rejectionReason,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.TryScheduleInteractionQuery(
                operationId,
                catalog,
                request,
                currentState,
                evaluator,
                out rejectionReason,
                cancellationToken);
        }

        public bool TryScheduleInteraction(
            string operationId,
            InteractionCatalogSnapshot catalog,
            InteractionExecutionRequest request,
            WorldStateFence currentState,
            out string rejectionReason,
            object hostContext = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.TryScheduleInteraction(
                operationId,
                catalog,
                request,
                currentState,
                out rejectionReason,
                hostContext,
                cancellationToken);
        }

        public bool TryScheduleExecution(
            string operationId,
            WorldEventPlan plan,
            out string rejectionReason,
            object hostContext = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.TryScheduleExecution(
                operationId,
                plan,
                out rejectionReason,
                hostContext,
                cancellationToken);
        }

        public bool TryScheduleAuthoritativeExecution(
            string operationId,
            WorldAuthoritativeEventPlan artifact,
            out string rejectionReason,
            object hostContext = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.TryScheduleAuthoritativeExecution(
                operationId,
                artifact,
                out rejectionReason,
                hostContext,
                cancellationToken);
        }

        public bool TryScheduleAuthoritativeInteraction(
            string operationId,
            WorldInteractionPlan interaction,
            WorldAuthoritativeCoordinate expectedCoordinate,
            out string rejectionReason,
            object hostContext = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            return _session.TryScheduleAuthoritativeInteraction(
                operationId,
                interaction,
                expectedCoordinate,
                out rejectionReason,
                hostContext,
                cancellationToken);
        }

        public bool TryCancel(string operationId)
        {
            return _session.TryCancel(operationId);
        }

        /// <summary>
        /// Must be called from Unity's main thread. Completion callbacks are
        /// never invoked by a worker thread.
        /// </summary>
        public int Pump(
            int maximumResults,
            Action<WorldBackgroundOperationResult> publish)
        {
            return _session.Pump(maximumResults, publish);
        }

        public ValueTask<IReadOnlyList<WorldBackgroundOperationResult>>
            ShutdownAsync(
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

    [Preserve]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-31990)]
    public sealed class UnityInteractiveWorldHost : MonoBehaviour
    {
        [SerializeField]
        [Min(1)]
        private int backgroundCapacity = 256;

        [SerializeField]
        [Min(1)]
        private int maxResultsPerFrame = 64;

        private UnityInteractiveWorldFacade _facade;
        private int _disposeStarted;

        public event Action<WorldBackgroundOperationResult>
            OperationCompleted;

        public UnityInteractiveWorldFacade Facade
        {
            get
            {
                if (_facade == null)
                {
                    throw new InvalidOperationException(
                        "Configure the interactive-world host before use.");
                }

                return _facade;
            }
        }

        public bool IsConfigured
        {
            get { return _facade != null; }
        }

        public void Configure(InteractiveWorldFacade portableFacade)
        {
            if (portableFacade == null)
            {
                throw new ArgumentNullException(nameof(portableFacade));
            }

            if (_facade != null)
            {
                throw new InvalidOperationException(
                    "The interactive-world host is already configured.");
            }

            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(UnityInteractiveWorldHost));
            }

            _facade = new UnityInteractiveWorldFacade(
                portableFacade,
                Math.Max(1, backgroundCapacity));
        }

        public int PumpWorldResults(int maximumResults)
        {
            if (_facade == null)
            {
                return 0;
            }

            return _facade.Pump(
                Math.Max(1, maximumResults),
                PublishResult);
        }

        /// <summary>
        /// Controlled scene/application shutdown. Call and await this on
        /// Unity's main thread before game-owned handlers and stores are
        /// released. OnDestroy remains an emergency detach fallback.
        /// </summary>
        public async ValueTask<
                IReadOnlyList<WorldBackgroundOperationResult>>
            ShutdownAsync(
                CancellationToken cancellationToken =
                    default(CancellationToken))
        {
            var facade = Facade;
            var results = await facade.ShutdownAsync(cancellationToken);
            foreach (var result in results)
            {
                PublishResult(result);
            }

            return results;
        }

        private void Update()
        {
            PumpWorldResults(Math.Max(1, maxResultsPerFrame));
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
                _ = facade.DisposeAsync();
            }
        }

        private void PublishResult(
            WorldBackgroundOperationResult result)
        {
            var handler = OperationCompleted;
            if (handler == null)
            {
                return;
            }

            foreach (Action<WorldBackgroundOperationResult> subscriber
                     in handler.GetInvocationList())
            {
                try
                {
                    subscriber(result);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
