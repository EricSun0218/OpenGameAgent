using System.Globalization;
using System.Net.Http;

namespace OpenGameAgent.ProviderTransport;

public sealed class ProviderHttpRetryMetadata
{
    public static readonly TimeSpan DefaultMaximumServerRetryDelay = TimeSpan.FromSeconds(60);

    private ProviderHttpRetryMetadata(bool isTransient, TimeSpan? retryAfter)
    {
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public bool IsTransient { get; }

    public TimeSpan? RetryAfter { get; }

    public static ProviderHttpRetryMetadata FromResponse(
        HttpResponseMessage response,
        DateTimeOffset? now = null,
        string? errorText = null,
        TimeSpan? maximumServerRetryDelay = null)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var directive = FirstHeader(response, "x-should-retry");
        var status = (int)response.StatusCode;
        var retryAfter = ResolveRetryAfter(response, now ?? DateTimeOffset.UtcNow);
        var isTransient = status == 429 && IsTerminalQuotaError(errorText)
            ? false
            : string.Equals(directive, "true", StringComparison.OrdinalIgnoreCase)
              || !string.Equals(directive, "false", StringComparison.OrdinalIgnoreCase)
              && IsRetryableStatus(status);
        var maximumDelay = maximumServerRetryDelay ?? DefaultMaximumServerRetryDelay;
        if (maximumDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumServerRetryDelay));
        }

        if (maximumDelay > TimeSpan.Zero && retryAfter > maximumDelay)
        {
            isTransient = false;
        }

        return new ProviderHttpRetryMetadata(isTransient, retryAfter);
    }

    public static ProviderHttpRetryMetadata FromStatus(
        int? statusCode,
        bool? providerRetryable = null,
        TimeSpan? retryAfter = null)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (retryAfter is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

        var isTransient = providerRetryable ?? statusCode is null || IsRetryableStatus(statusCode.Value);
        if (retryAfter > DefaultMaximumServerRetryDelay)
        {
            isTransient = false;
        }

        return new ProviderHttpRetryMetadata(isTransient, retryAfter);
    }

    private static bool IsRetryableStatus(int statusCode) =>
        statusCode is 408 or 409 or 429 || statusCode >= 500;

    private static bool IsTerminalQuotaError(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText))
        {
            return false;
        }

        var bounded = errorText.Length <= 65_536 ? errorText : errorText.Substring(0, 65_536);
        return bounded.IndexOf("GoUsageLimitError", StringComparison.OrdinalIgnoreCase) >= 0
               || bounded.IndexOf("FreeUsageLimitError", StringComparison.OrdinalIgnoreCase) >= 0
               || bounded.IndexOf("Monthly usage limit reached", StringComparison.OrdinalIgnoreCase) >= 0
               || bounded.IndexOf("available balance", StringComparison.OrdinalIgnoreCase) >= 0
               || bounded.IndexOf("insufficient_quota", StringComparison.OrdinalIgnoreCase) >= 0
               || bounded.IndexOf("out of budget", StringComparison.OrdinalIgnoreCase) >= 0
               || bounded.IndexOf("quota exceeded", StringComparison.OrdinalIgnoreCase) >= 0
               || bounded.IndexOf("billing", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static TimeSpan? ResolveRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var millisecondsValue = FirstHeader(response, "retry-after-ms");
        if (double.TryParse(millisecondsValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
            && !double.IsNaN(milliseconds)
            && !double.IsInfinity(milliseconds))
        {
            return FromMilliseconds(milliseconds);
        }

        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - now;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static TimeSpan FromMilliseconds(double milliseconds)
    {
        if (milliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        return milliseconds >= TimeSpan.MaxValue.TotalMilliseconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string? FirstHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
