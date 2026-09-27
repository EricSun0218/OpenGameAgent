using System.Net;
using System.Text.Json;

namespace OpenGameAgent.Client;

public enum RuntimeFailureDisposition
{
    Retry,
    ReadAuthority,
    Halt,
}

public static class RuntimeFailureClassifier
{
    public static RuntimeFailureDisposition Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is RuntimeRedirectException
            or RuntimeRequestLimitException
            or RuntimeResponseLimitException
            or RuntimeAttachmentIntegrityException
            or RuntimeRunSessionIdentityException
            or JsonException
            or ArgumentException)
        {
            return RuntimeFailureDisposition.Halt;
        }
        if (exception is HttpRequestException http)
        {
            if (http.StatusCode == HttpStatusCode.Conflict) return RuntimeFailureDisposition.ReadAuthority;
            if (http.StatusCode is HttpStatusCode status)
            {
                return (int)status >= 500
                    ? RuntimeFailureDisposition.Retry
                    : RuntimeFailureDisposition.Halt;
            }
            return RuntimeFailureDisposition.Retry;
        }
        return exception is IOException
            ? RuntimeFailureDisposition.Retry
            : RuntimeFailureDisposition.Halt;
    }
}
