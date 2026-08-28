using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.EngineClient;

/// <summary>Configures the bounded remote runtime client used by managed game engines.</summary>
public sealed class EngineGameAgentClientOptions
{
    /// <summary>Creates options for one trusted runtime service.</summary>
    public EngineGameAgentClientOptions(Uri baseUri)
    {
        BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
    }

    /// <summary>Gets the trusted runtime service root.</summary>
    public Uri BaseUri { get; }
    /// <summary>Gets or sets an optional caller-owned transport handler.</summary>
    public HttpMessageHandler? MessageHandler { get; set; }
    /// <summary>Gets or sets a callback that returns a bounded JSON authentication object.</summary>
    public Func<CancellationToken, ValueTask<string?>>? AuthenticationJsonProvider { get; set; }
    /// <summary>Gets or sets the maximum serialized request size.</summary>
    public int MaximumRequestBytes { get; set; } = 1024 * 1024;
    /// <summary>Gets or sets the maximum non-stream response size.</summary>
    public int MaximumResponseBytes { get; set; } = 4 * 1024 * 1024;
    /// <summary>Gets or sets the maximum size of one streamed event.</summary>
    public int MaximumEventBytes { get; set; } = 1024 * 1024;
}

/// <summary>Represents one validated server-sent event from a run or action stream.</summary>
public sealed class EngineGameAgentEvent
{
    internal EngineGameAgentEvent(string id, string name, string json)
    {
        Id = id;
        Name = name;
        Json = json;
    }

    /// <summary>Gets the stable event identifier supplied by the service.</summary>
    public string Id { get; }
    /// <summary>Gets the event name.</summary>
    public string Name { get; }
    /// <summary>Gets the validated JSON event payload.</summary>
    public string Json { get; }
}

/// <summary>Reports a safe HTTP failure category without reflecting response content.</summary>
public sealed class EngineGameAgentClientException : Exception
{
    internal EngineGameAgentClientException(HttpStatusCode statusCode, string category)
        : base($"Game Agent server request failed ({(int)statusCode}, {category}).")
    {
        StatusCode = statusCode;
        Category = category;
    }

    /// <summary>Gets the HTTP status returned by the service.</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>Gets the bounded machine-readable failure category.</summary>
    public string Category { get; }
}

/// <summary>Bounded JSON/SSE client for a separately hosted OpenGameAgent runtime.</summary>
public sealed class EngineGameAgentClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly Func<CancellationToken, ValueTask<string?>>? _authenticationJsonProvider;
    private readonly int _maximumRequestBytes;
    private readonly int _maximumResponseBytes;
    private readonly int _maximumEventBytes;
    private bool _disposed;

    /// <summary>Creates a client and takes ownership of its configured message handler.</summary>
    public EngineGameAgentClient(EngineGameAgentClientOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        ValidateBaseUri(options.BaseUri);
        _maximumRequestBytes = Bounded(options.MaximumRequestBytes, 1024, 64 * 1024 * 1024, nameof(options.MaximumRequestBytes));
        _maximumResponseBytes = Bounded(options.MaximumResponseBytes, 1024, 64 * 1024 * 1024, nameof(options.MaximumResponseBytes));
        _maximumEventBytes = Bounded(options.MaximumEventBytes, 1024, 8 * 1024 * 1024, nameof(options.MaximumEventBytes));
        _baseUri = EnsureTrailingSlash(options.BaseUri);
        _authenticationJsonProvider = options.AuthenticationJsonProvider;
        if (options.MessageHandler is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            }, disposeHandler: true);
        }
        else
        {
            _httpClient = new HttpClient(options.MessageHandler, disposeHandler: true);
        }
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Reads protocol features and public limits.</summary>
    public Task<string> ReadCapabilitiesAsync(CancellationToken cancellationToken = default)
        => SendJsonAsync(HttpMethod.Get, "v1/capabilities", bodyJson: null, cancellationToken);

    /// <summary>Reads the authorized persistent usage summary for a canonical session.</summary>
    public Task<string> ReadUsageAsync(string sessionJson, CancellationToken cancellationToken = default)
        => PostObjectAsync("v1/sessions/usage", ("session", sessionJson), cancellationToken);

    /// <summary>Reads one bounded authorized transcript page.</summary>
    public Task<string> ReadTranscriptAsync(
        string sessionJson,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is { Length: > 1024 }) throw new ArgumentOutOfRangeException(nameof(cursor));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return PostObjectAsync(
            "v1/sessions/transcript/read",
            new (string Name, string? Json)[]
            {
                ("session", sessionJson),
                ("cursor", cursor is null ? null : JsonSerializer.Serialize(cursor)),
                ("limit", limit.HasValue ? limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null),
            },
            cancellationToken);
    }

    /// <summary>Reads persisted run events after a sequence coordinate for reconnect recovery.</summary>
    public Task<string> ReadRunEventsAsync(
        string sessionJson,
        string runId,
        int afterSequence,
        int maximum = 100,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(runId, nameof(runId));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maximum is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximum));
        return PostObjectAsync(
            "v1/runs/events/read",
            new (string Name, string? Json)[]
            {
                ("session", sessionJson),
                ("runId", JsonSerializer.Serialize(runId)),
                ("afterSequence", afterSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("maximum", maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
            cancellationToken);
    }

    /// <summary>Claims pending durable action deliveries.</summary>
    public Task<string> ClaimActionsAsync(string sessionJson, int maximum = 1, CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximum));
        return PostObjectAsync(
            "v1/actions/claim",
            new (string Name, string? Json)[]
            {
                ("session", sessionJson),
                ("maximum", maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
            cancellationToken);
    }

    /// <summary>Submits an authoritative terminal action receipt.</summary>
    public Task<string> SubmitActionReceiptAsync(
        string sessionJson,
        string receiptJson,
        CancellationToken cancellationToken = default)
        => PostObjectAsync("v1/actions/receipt", new (string Name, string? Json)[] { ("session", sessionJson), ("receipt", receiptJson) }, cancellationToken);

    /// <summary>Reads canonical journal state for an operation that may have crossed the host boundary.</summary>
    public Task<string> ReconcileActionAsync(
        string sessionJson,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationId, nameof(operationId));
        return PostObjectAsync(
            "v1/actions/reconcile",
            new (string Name, string? Json)[] { ("session", sessionJson), ("operationId", JsonSerializer.Serialize(operationId)) },
            cancellationToken);
    }

    /// <summary>Lists bounded pending high-risk tool approvals.</summary>
    public Task<string> ListApprovalsAsync(string sessionJson, int maximum = 32, CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximum));
        return PostObjectAsync(
            "v1/tool-approvals/list",
            new (string Name, string? Json)[]
            {
                ("session", sessionJson),
                ("maximum", maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
            cancellationToken);
    }

    /// <summary>Approves or denies one exact pending tool request revision.</summary>
    public Task<string> RespondApprovalAsync(
        string sessionJson,
        string approvalId,
        int expectedRevision,
        bool approve,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(approvalId, nameof(approvalId));
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (reason is { Length: > 4096 }) throw new ArgumentOutOfRangeException(nameof(reason));
        return PostObjectAsync(
            "v1/tool-approvals/respond",
            new (string Name, string? Json)[]
            {
                ("session", sessionJson),
                ("approvalId", JsonSerializer.Serialize(approvalId)),
                ("expectedRevision", expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("decision", JsonSerializer.Serialize(approve ? "approve" : "deny")),
                ("reason", reason is null ? null : JsonSerializer.Serialize(reason)),
            },
            cancellationToken);
    }

    /// <summary>Steers only the exact active run and turn.</summary>
    public Task<bool> SteerAsync(
        string sessionJson,
        string expectedCoordinateJson,
        string inputJson,
        CancellationToken cancellationToken = default)
        => ControlAsync("steer", sessionJson, expectedCoordinateJson, inputJson, cancellationToken);

    /// <summary>Queues a follow-up for only the exact active run and turn.</summary>
    public Task<bool> FollowUpAsync(
        string sessionJson,
        string expectedCoordinateJson,
        string inputJson,
        CancellationToken cancellationToken = default)
        => ControlAsync("follow-up", sessionJson, expectedCoordinateJson, inputJson, cancellationToken);

    /// <summary>Aborts only the exact active run and turn.</summary>
    public Task<bool> AbortAsync(
        string sessionJson,
        string expectedCoordinateJson,
        CancellationToken cancellationToken = default)
        => ControlAsync("abort", sessionJson, expectedCoordinateJson, inputJson: null, cancellationToken);

    /// <summary>Starts a run and delivers validated stream events in wire order.</summary>
    public async Task RunAsync(
        string inputJson,
        Func<EngineGameAgentEvent, CancellationToken, Task> handler,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (runId is not null) RequireIdentifier(runId, nameof(runId));
        string body = await CreateBodyAsync(
            new (string Name, string? Json)[]
            {
                ("input", inputJson),
                ("runId", runId is null ? null : JsonSerializer.Serialize(runId)),
            },
            cancellationToken).ConfigureAwait(false);
        await StreamAsync("v1/runs/stream", body, handler, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams durable action deliveries; reconnect deliveries require reconciliation.</summary>
    public async Task StreamActionsAsync(
        string sessionJson,
        Func<EngineGameAgentEvent, CancellationToken, Task> handler,
        int maximum = 1,
        CancellationToken cancellationToken = default)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (maximum is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximum));
        string body = await CreateBodyAsync(
            new (string Name, string? Json)[]
            {
                ("session", sessionJson),
                ("maximum", maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
            cancellationToken).ConfigureAwait(false);
        await StreamAsync("v1/actions/stream", body, handler, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads authorized attachment bytes separately from transcript metadata.</summary>
    public async Task<byte[]> ReadAttachmentAsync(
        string sessionJson,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(attachmentId, nameof(attachmentId));
        string body = await CreateBodyAsync(
            new (string Name, string? Json)[] { ("session", sessionJson), ("attachmentId", JsonSerializer.Serialize(attachmentId)) },
            cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = CreateJsonRequest("v1/sessions/attachments/read", body);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        long? declared = response.Content.Headers.ContentLength;
        if (declared > _maximumResponseBytes) throw new InvalidDataException("Server response is too large.");
        using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await ReadBoundedBytesAsync(stream, _maximumResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels and disposes the owned HTTP transport.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }

    private async Task<bool> ControlAsync(
        string operation,
        string sessionJson,
        string expectedCoordinateJson,
        string? inputJson,
        CancellationToken cancellationToken)
    {
        string result = await PostObjectAsync(
            $"v1/control/{operation}",
            new (string Name, string? Json)[]
            {
                ("session", sessionJson),
                ("expected", expectedCoordinateJson),
                ("input", inputJson),
            },
            cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(result);
        return document.RootElement.TryGetProperty("accepted", out JsonElement accepted) && accepted.GetBoolean();
    }

    private Task<string> PostObjectAsync(
        string path,
        (string Name, string? Json) field,
        CancellationToken cancellationToken)
        => PostObjectAsync(path, new (string Name, string? Json)[] { field }, cancellationToken);

    private async Task<string> PostObjectAsync(
        string path,
        IReadOnlyList<(string Name, string? Json)> fields,
        CancellationToken cancellationToken)
    {
        string body = await CreateBodyAsync(fields, cancellationToken).ConfigureAwait(false);
        return await SendJsonAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CreateBodyAsync(
        IReadOnlyList<(string Name, string? Json)> fields,
        CancellationToken cancellationToken)
    {
        string? authenticationJson = _authenticationJsonProvider is null
            ? null
            : await _authenticationJsonProvider(cancellationToken).ConfigureAwait(false);
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach ((string name, string? json) in fields)
            {
                if (json is null) continue;
                writer.WritePropertyName(name);
                WriteJsonValue(writer, json, requireObject: false, name);
            }
            if (authenticationJson is not null)
            {
                writer.WritePropertyName("authentication");
                WriteJsonValue(writer, authenticationJson, requireObject: true, "authentication");
            }
            writer.WriteEndObject();
        }
        if (stream.Length > _maximumRequestBytes) throw new InvalidDataException("Client request is too large.");
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task StreamAsync(
        string path,
        string body,
        Func<EngineGameAgentEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateJsonRequest(path, body);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureContentType(response, "text/event-stream");
        using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await ReadEventStreamAsync(stream, handler, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendJsonAsync(
        HttpMethod method,
        string path,
        string? bodyJson,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = bodyJson is null
            ? new HttpRequestMessage(method, Endpoint(path))
            : CreateJsonRequest(path, bodyJson);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureJsonContentType(response);
        using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        byte[] bytes = await ReadBoundedBytesAsync(stream, _maximumResponseBytes, cancellationToken).ConfigureAwait(false);
        string json = Encoding.UTF8.GetString(bytes);
        using JsonDocument _ = JsonDocument.Parse(json);
        return json;
    }

    private HttpRequestMessage CreateJsonRequest(string path, string json)
    {
        ThrowIfDisposed();
        if (Encoding.UTF8.GetByteCount(json) > _maximumRequestBytes) throw new InvalidDataException("Client request is too large.");
        return new HttpRequestMessage(HttpMethod.Post, Endpoint(path))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        EnsureNoRedirect(response);
        if (response.IsSuccessStatusCode) return;
        string category = $"http-{(int)response.StatusCode}";
        try
        {
            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            byte[] bytes = await ReadBoundedBytesAsync(stream, Math.Min(_maximumResponseBytes, 64 * 1024), cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String)
            {
                string? candidate = error.GetString();
                if (IsSafeCategory(candidate)) category = candidate!;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        throw new EngineGameAgentClientException(response.StatusCode, category);
    }

    private async Task ReadEventStreamAsync(
        Stream stream,
        Func<EngineGameAgentEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(8192);
        StringBuilder pending = new StringBuilder();
        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(rented.Length)];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(rented, 0, rented.Length, cancellationToken).ConfigureAwait(false);
                int count = decoder.GetChars(rented, 0, read, chars, 0, flush: read == 0);
                pending.Append(chars, 0, count);
                while (TryTakeFrame(pending, out string frame))
                {
                    if (Encoding.UTF8.GetByteCount(frame) > _maximumEventBytes) throw new InvalidDataException("Server event is too large.");
                    EngineGameAgentEvent? item = ParseEvent(frame);
                    if (item is not null) await handler(item, cancellationToken).ConfigureAwait(false);
                }
                if (Encoding.UTF8.GetByteCount(pending.ToString()) > _maximumEventBytes) throw new InvalidDataException("Server event is too large.");
                if (read == 0) break;
            }
            if (pending.ToString().Trim().Length != 0) throw new InvalidDataException("Server stream ended with an incomplete event.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static EngineGameAgentEvent? ParseEvent(string frame)
    {
        string id = string.Empty;
        string name = "message";
        StringBuilder data = new StringBuilder();
        using StringReader reader = new StringReader(frame.Replace("\r", string.Empty));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("id:", StringComparison.Ordinal)) id = line.Substring(3).TrimStart();
            else if (line.StartsWith("event:", StringComparison.Ordinal)) name = line.Substring(6).TrimStart();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.Substring(5).TrimStart());
            }
        }
        if (data.Length == 0) return null;
        string json = data.ToString();
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("error", out _))
            throw new InvalidDataException("Game Agent stream reported a bounded remote failure.");
        return new EngineGameAgentEvent(id, name, json);
    }

    private static bool TryTakeFrame(StringBuilder pending, out string frame)
    {
        string text = pending.ToString();
        int lf = text.IndexOf("\n\n", StringComparison.Ordinal);
        int crlf = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int index;
        int length;
        if (lf < 0 && crlf < 0)
        {
            frame = string.Empty;
            return false;
        }
        if (crlf >= 0 && (lf < 0 || crlf < lf))
        {
            index = crlf;
            length = 4;
        }
        else
        {
            index = lf;
            length = 2;
        }
        frame = text.Substring(0, index);
        pending.Remove(0, index + length);
        return true;
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using MemoryStream output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                if (output.Length + read > maximumBytes) throw new InvalidDataException("Server response is too large.");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, string json, bool requireObject, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("JSON is required.", parameterName);
        using JsonDocument document = JsonDocument.Parse(json);
        if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A JSON object is required.", parameterName);
        document.RootElement.WriteTo(writer);
    }

    private Uri Endpoint(string path)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..")) throw new ArgumentException("Invalid endpoint path.", nameof(path));
        return new Uri(_baseUri, path);
    }

    private static void ValidateBaseUri(Uri value)
    {
        if (!value.IsAbsoluteUri || !string.IsNullOrEmpty(value.UserInfo) || !string.IsNullOrEmpty(value.Query) || !string.IsNullOrEmpty(value.Fragment))
            throw new ArgumentException("The server URI must be an absolute URL without credentials, query, or fragment.", nameof(value));
        if (!string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(value.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && value.IsLoopback))
            throw new ArgumentException("Remote Game Agent servers require HTTPS; HTTP is restricted to loopback.", nameof(value));
    }

    private static Uri EnsureTrailingSlash(Uri value)
        => value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? value : new Uri(value.AbsoluteUri + "/");

    private void EnsureNoRedirect(HttpResponseMessage response)
    {
        Uri? final = response.RequestMessage?.RequestUri;
        if (final is not null && final.GetLeftPart(UriPartial.Authority) != _baseUri.GetLeftPart(UriPartial.Authority))
            throw new InvalidDataException("Cross-origin redirects are not allowed.");
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("Redirects are not allowed.");
    }

    private static void EnsureJsonContentType(HttpResponseMessage response)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return;
        if (mediaType is not null && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException("Server returned an unexpected content type.");
    }

    private static void EnsureContentType(HttpResponseMessage response, string expected)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Server returned an unexpected content type.");
    }

    private static int Bounded(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    private static void RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            throw new ArgumentException("A bounded identifier is required.", parameterName);
        foreach (char character in value)
            if (char.IsControl(character)) throw new ArgumentException("Identifiers cannot contain control characters.", parameterName);
    }

    private static bool IsSafeCategory(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
        foreach (char character in value)
            if (!(character is >= 'a' and <= 'z') && !(character is >= '0' and <= '9') && character != '-') return false;
        return true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EngineGameAgentClient));
    }
}
