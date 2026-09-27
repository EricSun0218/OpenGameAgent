using System.Net;
using OpenGameAgent.Protocol;

namespace OpenGameAgent.Client;

public sealed class RuntimeRequestLimitException : Exception
{
    public RuntimeRequestLimitException()
        : base("The Runtime request body exceeded its configured byte limit.")
    {
    }
}

public sealed class RuntimeResponseLimitException : Exception
{
    public RuntimeResponseLimitException(string kind)
        : base($"The {kind} exceeded its configured byte limit.")
    {
        Kind = kind;
    }

    public string Kind { get; }
}

public sealed class RuntimeHttpException : HttpRequestException
{
    public RuntimeHttpException(HttpStatusCode statusCode, RuntimeError? runtimeError)
        : base(
            runtimeError?.Error is null
                ? $"Runtime request failed with HTTP status {(int)statusCode}."
                : $"Runtime request failed: {runtimeError.Error.Code}: {runtimeError.Error.Message}",
            inner: null,
            statusCode)
    {
        RuntimeError = runtimeError;
    }

    public RuntimeError? RuntimeError { get; }
}

public sealed class RuntimeRedirectException : HttpRequestException
{
    public RuntimeRedirectException()
        : base("Runtime requests must not follow redirects.")
    {
    }
}

public sealed class RuntimeSseDisconnectedException : IOException
{
    public RuntimeSseDisconnectedException(int connectionAttempts, Exception? innerException = null)
        : base($"The Runtime SSE stream ended after {connectionAttempts} connection attempts.", innerException)
    {
        ConnectionAttempts = connectionAttempts;
    }

    public int ConnectionAttempts { get; }
}

public sealed class RuntimeAttachmentIntegrityException : Exception
{
    public RuntimeAttachmentIntegrityException(string code)
        : base($"The Runtime attachment response failed integrity validation: {code}.")
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class RuntimeRunSessionClosedException : OperationCanceledException
{
    public RuntimeRunSessionClosedException()
        : base("The local Runtime run session was closed without changing the durable run.")
    {
    }
}

public sealed class RuntimeRunSessionIdentityException : IOException
{
    public RuntimeRunSessionIdentityException()
        : base("The Runtime run session received data for a different durable run identity.")
    {
    }
}

public sealed class RuntimeRunSessionGapException : IOException
{
    public RuntimeRunSessionGapException(Gap? gap)
        : base("The Runtime run session requires transcript reconciliation before it can continue.")
    {
        Gap = gap;
    }

    public Gap? Gap { get; }
}

public sealed class RuntimeRunSessionStreamEndedException : IOException
{
    public RuntimeRunSessionStreamEndedException(string stream, Exception? innerException = null)
        : base($"The Runtime {stream} stream ended before the exact run reached a terminal event.", innerException)
    {
        Stream = stream;
    }

    public string Stream { get; }
}

public sealed class RuntimeRunSessionHandlerTimeoutException : TimeoutException
{
    public RuntimeRunSessionHandlerTimeoutException(string handler)
        : base($"The Runtime run session {handler} handler exceeded its configured deadline.")
    {
        Handler = handler;
    }

    public string Handler { get; }
}

public sealed class RuntimeRunSessionApprovalNotFoundException : InvalidOperationException
{
    public RuntimeRunSessionApprovalNotFoundException()
        : base("The approval is not pending in this exact Runtime run session.")
    {
    }
}
