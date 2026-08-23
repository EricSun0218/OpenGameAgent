using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Runtime.Protocol;
using static OpenGameAgent.Client.GameServerClientTransport;

namespace OpenGameAgent.Client;

public delegate ValueTask GameRuntimeEventHandler(
    GameRuntimeEventEnvelope value,
    CancellationToken cancellationToken);

public sealed class GameRuntimeStreamResult
{
    public GameRuntimeStreamResult(
        string? lastEventId,
        long lastSequence,
        bool terminal,
        bool requiresTranscriptReconciliation)
    {
        if (lastSequence < 0 || (lastEventId is null) != (lastSequence == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(lastSequence));
        }

        LastEventId = lastEventId;
        LastSequence = lastSequence;
        Terminal = terminal;
        RequiresTranscriptReconciliation = requiresTranscriptReconciliation;
    }

    public string? LastEventId { get; }

    public long LastSequence { get; }

    public bool Terminal { get; }

    public bool RequiresTranscriptReconciliation { get; }
}

public sealed class GameRuntimeServerClientOptions
{
    public GameRuntimeServerClientOptions(HttpClient httpClient, Uri serverBaseUri)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ServerBaseUri = serverBaseUri ?? throw new ArgumentNullException(nameof(serverBaseUri));
    }

    public HttpClient HttpClient { get; }

    public Uri ServerBaseUri { get; }

    public string InitializePath { get; set; } = "runtime/v1/initialize";

    public string StreamPath { get; set; } = "runtime/v1/run/stream";

    public string EventsPath { get; set; } = "runtime/v1/events";

    public string SteerPath { get; set; } = "runtime/v1/control/steer";

    public string InterruptPath { get; set; } = "runtime/v1/control/interrupt";

    public string? ApiKey { get; set; }

    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyScheme { get; set; } = "Bearer";

    public bool AllowInsecureHttp { get; set; }

    public int MaximumResponseCharacters { get; set; } = GameRuntimeProtocol.MaximumJsonCharacters;

    public int MaximumEventCharacters { get; set; } = GameRuntimeProtocol.MaximumJsonCharacters;
}

/// <summary>
/// Typed client for the optional transport-neutral OpenGameAgent Runtime Protocol. A disconnected stream does not
/// cancel its run; reconnect with the same start request and <see cref="GameRuntimeStreamResult.LastEventId"/>.
/// </summary>
public sealed class GameRuntimeServerClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _initializeEndpoint;
    private readonly Uri _streamEndpoint;
    private readonly Uri _eventsEndpoint;
    private readonly Uri _steerEndpoint;
    private readonly Uri _interruptEndpoint;
    private readonly string? _apiKey;
    private readonly string _apiKeyHeader;
    private readonly string _apiKeyScheme;
    private readonly int _maximumResponseCharacters;
    private readonly int _maximumEventCharacters;

    public GameRuntimeServerClient(GameRuntimeServerClientOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        ValidateBaseUri(
            options.ServerBaseUri,
            options.AllowInsecureHttp,
            nameof(options),
            "The Runtime server base URI must be an absolute HTTP or HTTPS URI.",
            "Remote Runtime servers must use HTTPS.");
        if (options.MaximumResponseCharacters is < 2 or > GameRuntimeProtocol.MaximumJsonCharacters
            || options.MaximumEventCharacters is < 2 or > GameRuntimeProtocol.MaximumJsonCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (!IsValidHeaderName(options.ApiKeyHeader)
            || ContainsInvalidCredentialCharacters(options.ApiKey, 65_536)
            || ContainsInvalidCredentialCharacters(options.ApiKeyScheme, 256))
        {
            throw new ArgumentException("The Runtime authentication options are invalid.", nameof(options));
        }

        _httpClient = options.HttpClient;
        _initializeEndpoint = CreateEndpoint(options.ServerBaseUri, options.InitializePath, nameof(options.InitializePath));
        _streamEndpoint = CreateEndpoint(options.ServerBaseUri, options.StreamPath, nameof(options.StreamPath));
        _eventsEndpoint = CreateEndpoint(options.ServerBaseUri, options.EventsPath, nameof(options.EventsPath));
        _steerEndpoint = CreateEndpoint(options.ServerBaseUri, options.SteerPath, nameof(options.SteerPath));
        _interruptEndpoint = CreateEndpoint(options.ServerBaseUri, options.InterruptPath, nameof(options.InterruptPath));
        _apiKey = options.ApiKey;
        _apiKeyHeader = options.ApiKeyHeader;
        _apiKeyScheme = options.ApiKeyScheme ?? string.Empty;
        _maximumResponseCharacters = options.MaximumResponseCharacters;
        _maximumEventCharacters = options.MaximumEventCharacters;
    }

    public async Task<GameRuntimeInitializeResponse> InitializeAsync(
        GameRuntimeInitializeRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateJsonRequest(
            _initializeEndpoint,
            GameRuntimeJson.Serialize(request ?? new GameRuntimeInitializeRequest()));
        var json = await SendForJsonAsync(message, cancellationToken).ConfigureAwait(false);
        return GameRuntimeJson.Deserialize<GameRuntimeInitializeResponse>(json);
    }

    public Task<GameRuntimeStreamResult> StreamAsync(
        GameInput input,
        string requestId,
        GameRuntimeEventHandler handler,
        string? lastEventId = null,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        return StreamAsync(
            new GameRuntimeStartRequest(requestId, GameAgentWire.SerializeInput(input)),
            handler,
            lastEventId,
            presentedCredential,
            cancellationToken);
    }

    public async Task<GameRuntimeStreamResult> StreamAsync(
        GameRuntimeStartRequest start,
        GameRuntimeEventHandler handler,
        string? lastEventId = null,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        if (start is null)
        {
            throw new ArgumentNullException(nameof(start));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        ValidateEventId(lastEventId);
        ValidatePresentedCredential(presentedCredential);
        var body = JsonSerializer.Serialize(new
        {
            requestId = start.RequestId,
            inputJson = start.InputJson,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(_streamEndpoint, body);
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBoundedAsync(
                response.Content,
                _maximumResponseCharacters,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, error);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var name = "message";
        string? id = null;
        var data = new StringBuilder();
        string? lastId = lastEventId;
        var lastSequence = lastEventId is null ? 0 : ReadEventSequence(lastEventId);
        var terminal = false;
        var gap = false;
        await foreach (var line in ReadBoundedLinesAsync(reader, _maximumEventCharacters, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                name = line.Substring(6).Trim();
            }
            else if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                id = line.Substring(3).Trim();
                ValidateEventId(id);
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.Substring(5).TrimStart());
                if (data.Length > _maximumEventCharacters)
                {
                    throw new InvalidDataException("A Runtime event exceeded its configured size limit.");
                }
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                if (string.Equals(name, "gap", StringComparison.Ordinal))
                {
                    ValidateGap(data.ToString());
                    gap = true;
                    lastId = null;
                    lastSequence = 0;
                }
                else if (string.Equals(name, "runtime", StringComparison.Ordinal))
                {
                    var value = GameRuntimeJson.Deserialize<GameRuntimeEventEnvelope>(data.ToString());
                    if (id is null || !string.Equals(id, value.EventId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("A Runtime SSE event ID does not match its envelope.");
                    }

                    if (value.Sequence <= lastSequence)
                    {
                        throw new InvalidDataException("Runtime SSE sequences must advance monotonically.");
                    }

                    lastId = id;
                    lastSequence = value.Sequence;
                    terminal = value.Terminal;
                    await handler(value, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidDataException("The server emitted an unsupported Runtime SSE event.");
                }

                name = "message";
                id = null;
                data.Clear();
                if (terminal)
                {
                    break;
                }
            }
        }

        return new GameRuntimeStreamResult(lastId, lastSequence, terminal, gap);
    }

    public async Task<GameRuntimeEventPage> ReadEventsAsync(
        GameSessionKey key,
        long afterSequence = 0,
        int maximum = 256,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        ValidatePresentedCredential(presentedCredential);
        var requestValue = new GameRuntimeReadEventsRequest(
            key.SessionId,
            key.ActorId,
            afterSequence,
            maximum);
        var json = JsonSerializer.Serialize(new
        {
            sessionId = requestValue.SessionId,
            actorId = requestValue.ActorId,
            afterSequence = requestValue.AfterSequence,
            maximum = requestValue.Maximum,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(_eventsEndpoint, json);
        var responseJson = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
        var page = GameRuntimeJson.Deserialize<GameRuntimeEventPage>(responseJson);
        if (!string.Equals(page.SessionId, key.SessionId, StringComparison.Ordinal)
            || !string.Equals(page.ActorId, key.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Runtime event page belongs to a different session actor.");
        }

        return page;
    }

    public Task<GameRuntimeControlResponse> SteerAsync(
        GameRuntimeControlRequest control,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        if (control?.MessageJson is null)
        {
            throw new ArgumentException("A steering control with message JSON is required.", nameof(control));
        }

        return SendControlAsync(_steerEndpoint, control, presentedCredential, cancellationToken);
    }

    public Task<GameRuntimeControlResponse> InterruptAsync(
        GameRuntimeControlRequest control,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        if (control is null)
        {
            throw new ArgumentNullException(nameof(control));
        }

        return SendControlAsync(_interruptEndpoint, control, presentedCredential, cancellationToken);
    }

    private async Task<GameRuntimeControlResponse> SendControlAsync(
        Uri endpoint,
        GameRuntimeControlRequest control,
        string? presentedCredential,
        CancellationToken cancellationToken)
    {
        ValidatePresentedCredential(presentedCredential);
        var json = JsonSerializer.Serialize(new
        {
            sessionId = control.SessionId,
            actorId = control.ActorId,
            expectedRunId = control.ExpectedRunId,
            expectedTurnId = control.ExpectedTurnId,
            expectedTurn = control.ExpectedTurn,
            messageJson = control.MessageJson,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(endpoint, json);
        var response = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
        return GameRuntimeJson.Deserialize<GameRuntimeControlResponse>(response);
    }

    private async Task<string> SendForJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(
            response.Content,
            _maximumResponseCharacters,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body);
        return body;
    }

    private HttpRequestMessage CreateJsonRequest(Uri endpoint, string json)
    {
        if (json.Length > GameRuntimeProtocol.MaximumJsonCharacters)
        {
            throw new InvalidDataException("The Runtime request exceeded its protocol boundary.");
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
                throw new InvalidOperationException("The configured Runtime API key header is invalid.");
            }
        }

        return request;
    }

    private static void ValidateGap(string json)
    {
        using var document = RemoteJson.Parse(json, nameof(json));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("requiresTranscriptReconciliation", out var required)
            || required.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("A Runtime gap event has an invalid shape.");
        }
    }

    private static long ReadEventSequence(string value)
    {
        if (!GameRuntimeCursor.TryReadSequence(value, out var sequence))
        {
            throw new ArgumentException("The Runtime event ID is invalid.", nameof(value));
        }

        return sequence;
    }

    private static void ValidateEventId(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > 1_024 || value.Length == 0 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("The Runtime event ID is invalid.", nameof(value));
        }

        _ = ReadEventSequence(value);
    }

    private static void ValidatePresentedCredential(string? value)
    {
        if (ContainsInvalidCredentialCharacters(value, 4_096))
        {
            throw new ArgumentException("The presented Runtime credential is invalid.", nameof(value));
        }
    }

}
