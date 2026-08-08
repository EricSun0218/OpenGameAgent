using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Models.Auth.BuiltIn;

internal static class BoundedOAuthHttp
{
    private const int MaximumResponseBytes = 1_000_000;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static ValueTask<OAuthJsonResponse> PostFormAsync(
        HttpClient client,
        Uri endpoint,
        IReadOnlyDictionary<string, string> fields,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        PostAsync(
            client,
            endpoint,
            new FormUrlEncodedContent(ValidateFields(fields)),
            timeout,
            cancellationToken);

    public static ValueTask<OAuthJsonResponse> PostJsonAsync(
        HttpClient client,
        Uri endpoint,
        IReadOnlyDictionary<string, string> fields,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(ValidateFields(fields));
        return PostAsync(
            client,
            endpoint,
            new StringContent(json, Encoding.UTF8, "application/json"),
            timeout,
            cancellationToken);
    }

    private static async ValueTask<OAuthJsonResponse> PostAsync(
        HttpClient client,
        Uri endpoint,
        HttpContent content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        RequireHttps(endpoint, nameof(endpoint));
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using (content)
        using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            operation.CancelAfter(timeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await WaitAsync(
                    client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operation.Token),
                    operation.Token).ConfigureAwait(false);
                var body = await ReadBoundedAsync(response.Content, operation.Token).ConfigureAwait(false);
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(body, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 32,
                    });
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException("The OAuth endpoint returned invalid JSON.", exception);
                }

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    throw new InvalidOperationException("The OAuth endpoint returned a non-object response.");
                }

                return new OAuthJsonResponse(response.StatusCode, document);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested && operation.IsCancellationRequested)
            {
                throw new TimeoutException("The OAuth HTTP request timed out.", exception);
            }
        }
    }

    private static async ValueTask<string> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidOperationException("The OAuth response exceeded its safety bound.");
        }

        var stream = await WaitAsync(content.ReadAsStreamAsync(), cancellationToken).ConfigureAwait(false);
        using (stream)
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[8192];
            while (true)
            {
                var read = await WaitAsync(
                    stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("The OAuth response exceeded its safety bound.");
                }

                buffer.Write(chunk, 0, read);
            }

            try
            {
                return StrictUtf8.GetString(buffer.ToArray());
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidOperationException("The OAuth response is not valid UTF-8.", exception);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ValidateFields(
        IReadOnlyDictionary<string, string> fields)
    {
        if (fields is null || fields.Count > 64)
        {
            throw new ArgumentException("OAuth requests support at most 64 fields.", nameof(fields));
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in fields)
        {
            if (!IsBoundedValue(pair.Key, 256) || !IsBoundedValue(pair.Value, 65_536))
            {
                throw new ArgumentException("An OAuth request field is invalid.", nameof(fields));
            }

            copy.Add(pair.Key, pair.Value);
        }

        return copy;
    }

    internal static Uri RequireHttps(Uri value, string parameterName)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.Scheme != Uri.UriSchemeHttps
            || value.UserInfo.Length != 0
            || value.Fragment.Length != 0)
        {
            throw new ArgumentException("An absolute HTTPS endpoint without credentials or a fragment is required.", parameterName);
        }

        return value;
    }

    internal static string RequiredString(JsonElement root, string name, int maximum = 65_536)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || !IsBoundedValue(value.GetString(), maximum))
        {
            throw new InvalidOperationException($"The OAuth response omitted or invalidated '{name}'.");
        }

        return value.GetString()!;
    }

    internal static string? OptionalString(JsonElement root, string name, int maximum = 65_536)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !IsBoundedValue(value.GetString(), maximum))
        {
            throw new InvalidOperationException($"The OAuth response field '{name}' is invalid.");
        }

        return value.GetString();
    }

    internal static TimeSpan ReadSeconds(
        JsonElement root,
        string name,
        TimeSpan fallback,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        double seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out seconds))
        {
        }
        else if (value.ValueKind == JsonValueKind.String
                 && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
        }
        else
        {
            throw new InvalidOperationException($"The OAuth response field '{name}' is invalid.");
        }

        if (double.IsNaN(seconds)
            || double.IsInfinity(seconds)
            || seconds < minimum.TotalSeconds
            || seconds > maximum.TotalSeconds)
        {
            throw new InvalidOperationException($"The OAuth response field '{name}' is outside its allowed range.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    internal static InvalidOperationException Failure(string operation, OAuthJsonResponse response)
    {
        var error = OptionalString(response.Root, "error", 4096);
        return new InvalidOperationException(
            $"{operation} failed with HTTP {(int)response.StatusCode}"
            + (string.IsNullOrWhiteSpace(error) ? "." : $" ({error})."));
    }

    internal static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => canceled.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, canceled.Task).ConfigureAwait(false))
            {
                _ = task.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
    }

    internal static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => canceled.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, canceled.Task).ConfigureAwait(false))
            {
                _ = task.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }
        }

        await task.ConfigureAwait(false);
    }

    private static bool IsBoundedValue(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum
        && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
}

internal sealed class OAuthJsonResponse : IDisposable
{
    private readonly JsonDocument _document;

    public OAuthJsonResponse(HttpStatusCode statusCode, JsonDocument document)
    {
        StatusCode = statusCode;
        _document = document;
    }

    public HttpStatusCode StatusCode { get; }

    public bool IsSuccess => (int)StatusCode is >= 200 and <= 299;

    public JsonElement Root => _document.RootElement;

    public void Dispose() => _document.Dispose();
}
