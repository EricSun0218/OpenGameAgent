using System;
using UnityEngine.Scripting;

namespace GameAgent.Unity
{
    [Preserve]
    public sealed class UnityRunFault
    {
        internal UnityRunFault(
            string operationKind,
            string runId,
            string operationId,
            string parentRunId,
            bool reconciliationRequired,
            Exception exception)
        {
            OperationKind = operationKind;
            RunId = runId;
            OperationId = operationId;
            ParentRunId = parentRunId;
            ReconciliationRequired = reconciliationRequired;
            Exception = exception
                ?? throw new ArgumentNullException(nameof(exception));
        }

        public string OperationKind { get; private set; }

        public string RunId { get; private set; }

        public string OperationId { get; private set; }

        public string ParentRunId { get; private set; }

        public bool ReconciliationRequired { get; private set; }

        public Exception Exception { get; private set; }
    }
}
