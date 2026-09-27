using OpenGameAgent.Protocol;

namespace OpenGameAgent.Client;

public sealed class RuntimeActorActionRecoveryOptions
{
    public required long MaximumActions { get; init; }

    public required TimeSpan Timeout { get; init; }

    public required Func<RuntimeActionRecoveryDelivery, CancellationToken, Task<RuntimeActionRecoveryResolution>>
        Reconcile { get; init; }
}

public sealed class RuntimeActionRecoveryResolution
{
    public required Resolution Resolution { get; init; }

    public object? Result { get; init; }

    public object? ModelResult { get; init; }

    public RuntimeActionReconcileRequestAuthorityAfter? AuthorityAfter { get; init; }
}

public sealed class RuntimeActorActionRecoveryLimitException : IOException
{
    public RuntimeActorActionRecoveryLimitException()
        : base("Actor Action recovery exceeded the host-configured delivery limit.")
    {
    }
}

public sealed class RuntimeActorActionRecoveryUnresolvedException : IOException
{
    public RuntimeActorActionRecoveryUnresolvedException(string operationId)
        : base("Actor Action recovery remains outcome-unknown after authoritative reconciliation.")
    {
        OperationId = operationId;
    }

    public string OperationId { get; }
}
