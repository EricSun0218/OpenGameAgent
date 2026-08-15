using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Attachments;

namespace OpenGameAgent.Client;

public sealed class RemoteGameAgentEvent
{
    public RemoteGameAgentEvent(string name, string json)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "message" : name;
        Json = RemoteJson.RequireValid(json, nameof(json));
    }

    public string Name { get; }

    public string Json { get; }
}

public sealed class RemoteGameAgentResult
{
    public RemoteGameAgentResult(string status, string route, long sessionRevision, string json, string? error = null)
    {
        if (sessionRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        }

        Status = RequireValue(status, nameof(status));
        Route = RequireValue(route, nameof(route));
        SessionRevision = sessionRevision;
        Json = RemoteJson.RequireValid(json, nameof(json));
        Error = error;
    }

    public string Status { get; }

    public string Route { get; }

    public long SessionRevision { get; }

    public string Json { get; }

    public string? Error { get; }

    public bool Succeeded => string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty result value is required.", parameterName);
        }

        return value;
    }
}

public delegate ValueTask RemoteGameAgentEventHandler(
    RemoteGameAgentEvent agentEvent,
    CancellationToken cancellationToken);

public sealed class ServerGameAgentClientOptions
{
    public ServerGameAgentClientOptions(HttpClient httpClient, Uri serverBaseUri)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ServerBaseUri = serverBaseUri ?? throw new ArgumentNullException(nameof(serverBaseUri));
        if (!serverBaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The server base URI must be absolute.", nameof(serverBaseUri));
        }


        if (!string.Equals(serverBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(serverBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The server base URI must use HTTP or HTTPS.", nameof(serverBaseUri));
        }
    }

    public HttpClient HttpClient { get; }

    public Uri ServerBaseUri { get; set; }

    public string RunPath { get; set; } = "v1/run";

    public string StreamPath { get; set; } = "v1/run/stream";

    public string SteerPath { get; set; } = "v1/control/steer";

    public string AbortPath { get; set; } = "v1/control/abort";

    public string AttachmentReadPath { get; set; } = "v1/attachments/read";

    public string? ApiKey { get; set; }

    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyScheme { get; set; } = "Bearer";

    public bool AllowInsecureHttp { get; set; }

    public int MaxResponseCharacters { get; set; } = 8_000_000;

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxRequestCharacters { get; set; } = 8_000_000;
}

public sealed class ServerGameAgentClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _runEndpoint;
    private readonly Uri _streamEndpoint;
    private readonly Uri _steerEndpoint;
    private readonly Uri _abortEndpoint;
    private readonly Uri _attachmentReadEndpoint;
    private readonly string? _apiKey;
    private readonly string _apiKeyHeader;
    private readonly string _apiKeyScheme;
    private readonly int _maxResponseCharacters;
    private readonly int _maxEventCharacters;
    private readonly int _maxRequestCharacters;

    public ServerGameAgentClient(ServerGameAgentClientOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.MaxResponseCharacters < 2 || options.MaxResponseCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum response size is invalid.");
        }

        if (options.MaxEventCharacters < 2 || options.MaxEventCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum stream-event size is invalid.");
        }

        if (options.MaxRequestCharacters < 2 || options.MaxRequestCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum request size is invalid.");
        }


        if (options.ServerBaseUri is null
            || !options.ServerBaseUri.IsAbsoluteUri
            || options.ServerBaseUri.UserInfo.Length > 0
            || options.ServerBaseUri.Query.Length > 0
            || options.ServerBaseUri.Fragment.Length > 0
            || (!string.Equals(options.ServerBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.ServerBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The server base URI must be an absolute HTTP or HTTPS URI.", nameof(options));
        }

        if (string.Equals(options.ServerBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !options.ServerBaseUri.IsLoopback
            && !options.AllowInsecureHttp)
        {
            throw new ArgumentException(
                "Remote agent servers must use HTTPS unless insecure HTTP is explicitly enabled.",
                nameof(options));
        }

        if (!IsValidHeaderName(options.ApiKeyHeader) || options.ApiKeyHeader.Length > 256)
        {
            throw new ArgumentException("A valid API key header name is required.", nameof(options));
        }

        if ((options.ApiKey?.Contains('\r') ?? false)
            || (options.ApiKey?.Contains('\n') ?? false)
            || (options.ApiKey?.Contains('\0') ?? false)
            || (options.ApiKey?.Length ?? 0) > 65_536
            || (options.ApiKeyScheme?.Contains('\r') ?? false)
            || (options.ApiKeyScheme?.Contains('\n') ?? false)
            || (options.ApiKeyScheme?.Contains('\0') ?? false)
            || (options.ApiKeyScheme?.Length ?? 0) > 256)
        {
            throw new ArgumentException("API key credentials contain invalid characters or exceed their size limit.", nameof(options));
        }

        if (options.ApiKey is { Length: > 0 } && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("A configured API key cannot contain only whitespace.", nameof(options));
        }

        _httpClient = options.HttpClient;
        _runEndpoint = CreateEndpoint(options.ServerBaseUri, options.RunPath, nameof(options.RunPath));
        _streamEndpoint = CreateEndpoint(options.ServerBaseUri, options.StreamPath, nameof(options.StreamPath));
        _steerEndpoint = CreateEndpoint(options.ServerBaseUri, options.SteerPath, nameof(options.SteerPath));
        _abortEndpoint = CreateEndpoint(options.ServerBaseUri, options.AbortPath, nameof(options.AbortPath));
        _attachmentReadEndpoint = CreateEndpoint(
            options.ServerBaseUri,
            options.AttachmentReadPath,
            nameof(options.AttachmentReadPath));
        _apiKey = options.ApiKey;
        _apiKeyHeader = options.ApiKeyHeader;
        _apiKeyScheme = options.ApiKeyScheme ?? string.Empty;
        _maxResponseCharacters = options.MaxResponseCharacters;
        _maxEventCharacters = options.MaxEventCharacters;
        _maxRequestCharacters = options.MaxRequestCharacters;
    }

    private static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage();
            return request.Headers.TryAddWithoutValidation(name, "value");
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<RemoteGameAgentResult> RunAsync(
        GameInput input,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(_runEndpoint, input);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var json = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);
        return ParseResult(json);
    }

    public async Task<RemoteGameAgentResult> StreamAsync(
        GameInput input,
        RemoteGameAgentEventHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        using var request = CreateRequest(_streamEndpoint, input);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, error);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var eventName = "message";
        var data = new StringBuilder();
        RemoteGameAgentResult? result = null;
        await foreach (var line in ReadBoundedLinesAsync(reader, _maxEventCharacters, cancellationToken).ConfigureAwait(false))
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.Substring(5).TrimStart());
                if (data.Length > _maxEventCharacters)
                {
                    throw new InvalidDataException("A server event exceeded the configured size limit.");
                }
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                var json = data.ToString();
                var agentEvent = new RemoteGameAgentEvent(eventName, json);
                if (string.Equals(eventName, "result", StringComparison.Ordinal))
                {
                    if (result is not null)
                    {
                        throw new InvalidDataException("The server stream emitted more than one result event.");
                    }

                    result = ParseResult(json);
                }
                else if (result is not null)
                {
                    throw new InvalidDataException("The server stream emitted an event after its terminal result.");
                }

                await handler(agentEvent, cancellationToken).ConfigureAwait(false);

                eventName = "message";
                data.Clear();
            }
        }

        if (data.Length > 0)
        {
            var json = data.ToString();
            var agentEvent = new RemoteGameAgentEvent(eventName, json);
            if (string.Equals(eventName, "result", StringComparison.Ordinal))
            {
                if (result is not null)
                {
                    throw new InvalidDataException("The server stream emitted more than one result event.");
                }

                result = ParseResult(json);
            }
            else if (result is not null)
            {
                throw new InvalidDataException("The server stream emitted an event after its terminal result.");
            }

            await handler(agentEvent, cancellationToken).ConfigureAwait(false);
        }

        return result ?? throw new InvalidDataException("The server stream ended without a result event.");
    }

    public Task<bool> SteerAsync(
        GameSessionKey key,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        if (payloadJson is null)
        {
            throw new ArgumentNullException(nameof(payloadJson));
        }

        if (payloadJson.Length > _maxRequestCharacters)
        {
            throw new InvalidDataException("The steering payload exceeded the configured size limit.");
        }

        using var document = RemoteJson.Parse(payloadJson, nameof(payloadJson));
        var json = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            payload = document.RootElement,
        });
        return SendControlAsync(_steerEndpoint, json, cancellationToken);
    }

    public Task<bool> AbortAsync(
        GameSessionKey key,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        var json = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
        });
        return SendControlAsync(_abortEndpoint, json, cancellationToken);
    }

    public async Task<StoredGameImageAttachment?> ReadImageAttachmentAsync(
        GameSessionKey key,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        if (string.IsNullOrWhiteSpace(attachmentId)
            || attachmentId.Length > 256
            || attachmentId.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("A bounded attachment ID is required.", nameof(attachmentId));
        }

        var json = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            attachmentId,
        });
        using var request = CreateJsonRequest(_attachmentReadEndpoint, json);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, body);
        return ParseAttachment(body);
    }

    private static StoredGameImageAttachment ParseAttachment(string json)
    {
        using var document = RemoteJson.Parse(json, nameof(json));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("attachment", out var descriptor)
            || descriptor.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The attachment response does not match the expected shape.");
        }

        var attachmentId = RequireString(descriptor, "attachmentId");
        var mediaType = RequireString(descriptor, "mediaType");
        var bytes = RequireInt32(descriptor, "bytes");
        var width = RequireInt32(descriptor, "width");
        var height = RequireInt32(descriptor, "height");
        string? name = null;
        if (descriptor.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind != JsonValueKind.Null)
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The attachment name must be a string or null.");
            }

            name = nameElement.GetString();
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(dataElement.GetString()!);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The attachment response contains invalid base64 data.", exception);
        }

        if (data.Length != bytes)
        {
            throw new InvalidDataException("The attachment response length does not match its descriptor.");
        }

        return new StoredGameImageAttachment(
            new GameImageAttachment(attachmentId, mediaType, bytes, width, height, name),
            data);
    }

    private static string RequireString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException("The attachment response is missing '" + propertyName + "'.");
        }

        return property.GetString()!;
    }

    private static int RequireInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || !property.TryGetInt32(out var result)
            || result <= 0)
        {
            throw new InvalidDataException("The attachment response has an invalid '" + propertyName + "'.");
        }

        return result;
    }

    private async Task<bool> SendControlAsync(
        Uri endpoint,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(endpoint, json);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        EnsureSuccess(response, body);
        using var document = RemoteJson.Parse(body, nameof(body));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("accepted", out var accepted)
            || accepted.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException("The server control response must contain a boolean 'accepted' field.");
        }

        return accepted.GetBoolean();
    }

    private HttpRequestMessage CreateRequest(Uri endpoint, GameInput input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var json = GameAgentWire.SerializeInput(input);
        if (json.Length > _maxRequestCharacters)
        {
            throw new InvalidDataException("The server request exceeded the configured size limit.");
        }

        return CreateJsonRequest(endpoint, json);
    }

    private HttpRequestMessage CreateJsonRequest(Uri endpoint, string json)
    {
        if (json.Length > _maxRequestCharacters)
        {
            throw new InvalidDataException("The server request exceeded the configured size limit.");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_apiKey))
        {
            var value = string.IsNullOrWhiteSpace(_apiKeyScheme) ? _apiKey : _apiKeyScheme + " " + _apiKey;
            if (!request.Headers.TryAddWithoutValidation(_apiKeyHeader, value))
            {
                request.Dispose();
                throw new InvalidOperationException("The configured server API key header is invalid.");
            }
        }

        return request;
    }

    private static RemoteGameAgentResult ParseResult(string json)
    {
        using var document = RemoteJson.Parse(json, nameof(json));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("route", out var route)
            || route.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("sessionRevision", out var revision)
            || !revision.TryGetInt64(out var revisionValue))
        {
            throw new InvalidDataException("The server result does not match the expected response shape.");
        }

        string? errorValue = null;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            if (error.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The server result error must be a string or null.");
            }

            errorValue = error.GetString();
        }

        return new RemoteGameAgentResult(
            status.GetString()!,
            route.GetString()!,
            revisionValue,
            json,
            errorValue);
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var buffer = new char[Math.Min(4096, maximumCharacters)];
        var result = new StringBuilder();
        while (result.Length < maximumCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(
                buffer,
                0,
                Math.Min(buffer.Length, maximumCharacters - result.Length)).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            result.Append(buffer, 0, read);
        }

        if (await reader.ReadAsync(buffer, 0, 1).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("The server response exceeded the configured size limit.");
        }

        return result.ToString();
    }

    private static async IAsyncEnumerable<string> ReadBoundedLinesAsync(
        StreamReader reader,
        int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumCharacters + 1));
        var line = new StringBuilder(Math.Min(4096, maximumCharacters));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (read == 0)
                {
                    if (line.Length > 0)
                    {
                        yield return TrimCarriageReturn(line);
                    }

                    yield break;
                }

                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        yield return TrimCarriageReturn(line);
                        line.Clear();
                        continue;
                    }

                    line.Append(buffer[index]);
                    if (line.Length > maximumCharacters)
                    {
                        throw new InvalidDataException("A server event line exceeded the configured size limit.");
                    }
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static string TrimCarriageReturn(StringBuilder line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
        {
            length--;
        }

        return line.ToString(0, length);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The agent server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
        }
    }

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsoluteUri.EndsWith('/')
            ? value
            : new Uri(value.AbsoluteUri + "/");

    private static Uri CreateEndpoint(Uri serverBaseUri, string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(path, UriKind.Relative, out _))
        {
            throw new ArgumentException("A server endpoint path must be relative.", parameterName);
        }

        var endpoint = new Uri(EnsureTrailingSlash(serverBaseUri), path);
        if (!string.Equals(endpoint.Scheme, serverBaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(endpoint.Host, serverBaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != serverBaseUri.Port)
        {
            throw new ArgumentException("A server endpoint path cannot change the configured server origin.", parameterName);
        }

        return endpoint;
    }
}

internal static class RemoteJson
{
    public static string RequireValid(string value, string parameterName)
    {
        using var document = Parse(value, parameterName);
        return value;
    }

    public static JsonDocument Parse(string value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        try
        {
            var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
            try
            {
                EnsureUnambiguous(document.RootElement, parameterName);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The value must contain valid JSON.", parameterName, exception);
        }
    }

    private static void EnsureUnambiguous(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException("JSON objects cannot contain duplicate property names.", parameterName);
                }

                EnsureUnambiguous(property.Value, parameterName);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item, parameterName);
            }
        }
    }
}

internal static class GameSessionKeyClientValidation
{
    public static void EnsureValidForClient(this GameSessionKey key, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(key.SessionId) || string.IsNullOrWhiteSpace(key.ActorId))
        {
            throw new ArgumentException("A valid game session key is required.", parameterName);
        }
    }
}
