using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Client;

public sealed class RemoteGameServerCapabilities
{
    internal RemoteGameServerCapabilities(string name, string protocolVersion, string json)
    {
        Name = name;
        ProtocolVersion = protocolVersion;
        Json = json;
    }

    public string Name { get; }
    public string ProtocolVersion { get; }
    public string Json { get; }
}

public sealed class RemoteGameSessionUsage
{
    internal RemoteGameSessionUsage(
        GameSessionKey key,
        long sessionRevision,
        long totalRecordCount,
        long totalTokens,
        bool costKnown,
        double? totalCost,
        string json)
    {
        Key = key;
        SessionRevision = sessionRevision;
        TotalRecordCount = totalRecordCount;
        TotalTokens = totalTokens;
        CostKnown = costKnown;
        TotalCost = totalCost;
        Json = json;
    }

    public GameSessionKey Key { get; }
    public long SessionRevision { get; }
    public long TotalRecordCount { get; }
    public long TotalTokens { get; }
    public bool CostKnown { get; }
    public double? TotalCost { get; }
    public string Json { get; }
}

public sealed class RemoteGameActionDelivery
{
    internal RemoteGameActionDelivery(GameActionIntent intent, bool requiresReconciliation)
    {
        Intent = intent;
        RequiresReconciliation = requiresReconciliation;
    }

    public GameActionIntent Intent { get; }
    public bool RequiresReconciliation { get; }
}

public sealed class RemoteGameActionState
{
    internal RemoteGameActionState(
        string status,
        GameActionIntent intent,
        GameActionReceipt? receipt,
        bool requiresReconciliation)
    {
        Status = status;
        Intent = intent;
        Receipt = receipt;
        RequiresReconciliation = requiresReconciliation;
    }

    public string Status { get; }
    public GameActionIntent Intent { get; }
    public GameActionReceipt? Receipt { get; }
    public bool RequiresReconciliation { get; }
}

public delegate ValueTask RemoteGameActionHandler(
    RemoteGameActionDelivery delivery,
    CancellationToken cancellationToken);

public sealed partial class ServerGameAgentClient
{
    public async Task<RemoteGameServerCapabilities> ReadCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, _capabilitiesEndpoint, content: null);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body);
        using var document = RemoteJson.Parse(body, nameof(body));
        var root = document.RootElement;
        return new RemoteGameServerCapabilities(
            RequireString(root, "name"),
            RequireString(root, "protocolVersion"),
            body);
    }

    public async Task<RemoteGameSessionUsage> ReadUsageAsync(
        GameSessionKey key,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        ValidatePresentedCredential(presentedCredential);
        var body = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(_usageEndpoint, body);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var json = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);
        return ParseUsage(json, key);
    }

    public async Task<IReadOnlyList<RemoteGameActionDelivery>> ClaimActionsAsync(
        GameSessionKey key,
        int limit = 32,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        if (limit is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ValidatePresentedCredential(presentedCredential);
        var body = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            limit,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(_actionClaimEndpoint, body);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var json = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);
        using var document = RemoteJson.Parse(json, nameof(json));
        if (!document.RootElement.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array
            || actions.GetArrayLength() > 256)
        {
            throw new InvalidDataException("The action-claim response has an invalid shape.");
        }

        return Array.AsReadOnly(actions.EnumerateArray().Select(ParseActionDelivery).ToArray());
    }

    public async Task StreamActionsAsync(
        GameSessionKey key,
        RemoteGameActionHandler handler,
        int limit = 32,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (limit is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ValidatePresentedCredential(presentedCredential);
        var body = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            limit,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(_actionStreamEndpoint, body);
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
                    throw new InvalidDataException("An action-stream event exceeded its configured size limit.");
                }
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                if (!string.Equals(eventName, "action", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The action stream emitted an unsupported event.");
                }

                using var document = RemoteJson.Parse(data.ToString(), nameof(data));
                await handler(ParseActionDelivery(document.RootElement), cancellationToken).ConfigureAwait(false);
                eventName = "message";
                data.Clear();
            }
        }
    }

    public async Task<GameActionReceipt> SubmitActionReceiptAsync(
        GameSessionKey key,
        long? expectedRevision,
        string? generationId,
        GameActionReceipt receipt,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        if (receipt.Status == GameActionStatus.Uncertain)
        {
            throw new ArgumentException("A submitted action receipt must be terminal.", nameof(receipt));
        }

        ValidatePresentedCredential(presentedCredential);
        using var result = RemoteJson.Parse(receipt.ResultJson, nameof(receipt));
        JsonElement? calendar = null;
        if (receipt.Moment.CalendarJson is not null)
        {
            using var parsed = RemoteJson.Parse(receipt.Moment.CalendarJson, nameof(receipt));
            calendar = parsed.RootElement.Clone();
        }

        var body = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            operationId = receipt.OperationId,
            status = receipt.Status.ToString().ToLowerInvariant(),
            result = result.RootElement.Clone(),
            timelineId = receipt.Moment.TimelineId,
            tick = receipt.Moment.Tick,
            calendar,
            generationId,
            expectedRevision,
            stateRevision = receipt.StateRevision,
            code = receipt.Code,
            message = receipt.Message,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(_actionReceiptEndpoint, body);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var json = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);
        using var document = RemoteJson.Parse(json, nameof(json));
        if (!document.RootElement.TryGetProperty("receipt", out var stored))
        {
            throw new InvalidDataException("The action receipt response has an invalid shape.");
        }

        return ParseReceipt(stored);
    }

    public async Task<RemoteGameActionState?> ReconcileActionAsync(
        GameSessionKey key,
        string operationId,
        string? presentedCredential = null,
        CancellationToken cancellationToken = default)
    {
        key.EnsureValidForClient(nameof(key));
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 16_384 || operationId.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded operation ID is required.", nameof(operationId));
        }

        ValidatePresentedCredential(presentedCredential);
        var body = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            operationId,
            credential = presentedCredential,
        });
        using var request = CreateJsonRequest(_actionReconcileEndpoint, body);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var json = await ReadBoundedAsync(response.Content, _maxResponseCharacters, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, json);
        using var document = RemoteJson.Parse(json, nameof(json));
        var root = document.RootElement;
        var status = RequireString(root, "status");
        if (!root.TryGetProperty("action", out var action))
        {
            throw new InvalidDataException("The action reconciliation response has no action.");
        }

        GameActionReceipt? receipt = null;
        if (root.TryGetProperty("receipt", out var receiptElement) && receiptElement.ValueKind != JsonValueKind.Null)
        {
            receipt = ParseReceipt(receiptElement);
        }

        var delivery = ParseActionDelivery(action);
        return new RemoteGameActionState(status, delivery.Intent, receipt, delivery.RequiresReconciliation);
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, Uri endpoint, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, endpoint) { Content = content };
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

    private static RemoteGameSessionUsage ParseUsage(string json, GameSessionKey expectedKey)
    {
        using var document = RemoteJson.Parse(json, nameof(json));
        var root = document.RootElement;
        if (!string.Equals(RequireString(root, "sessionId"), expectedKey.SessionId, StringComparison.Ordinal)
            || !string.Equals(RequireString(root, "actorId"), expectedKey.ActorId, StringComparison.Ordinal)
            || !root.TryGetProperty("total", out var total)
            || !total.TryGetProperty("cost", out var cost)
            || !cost.TryGetProperty("known", out var known)
            || known.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException("The usage response has an invalid owner or shape.");
        }

        double? totalCost = null;
        if (cost.TryGetProperty("total", out var costTotal) && costTotal.ValueKind == JsonValueKind.Number)
        {
            totalCost = costTotal.GetDouble();
        }

        return new RemoteGameSessionUsage(
            expectedKey,
            RequireNonNegativeInt64(root, "sessionRevision"),
            RequireNonNegativeInt64(root, "totalRecordCount"),
            RequireNonNegativeInt64(total, "totalTokens"),
            known.GetBoolean(),
            totalCost,
            json);
    }

    private static RemoteGameActionDelivery ParseActionDelivery(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("arguments", out var arguments)
            || !value.TryGetProperty("requiresReconciliation", out var reconcile)
            || reconcile.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException("The action delivery has an invalid shape.");
        }

        var intent = new GameActionIntent(
            RequireString(value, "operationId"),
            RequireString(value, "inputId"),
            RequireString(value, "sessionId"),
            RequireString(value, "actorId"),
            RequireString(value, "action"),
            arguments.GetRawText(),
            ParseMoment(value),
            ReadNullableInt64(value, "expectedRevision"),
            ReadNullableString(value, "generationId"),
            ReadNullableString(value, "conflictKey"));
        return new RemoteGameActionDelivery(intent, reconcile.GetBoolean());
    }

    private static GameActionReceipt ParseReceipt(JsonElement value) => new(
        RequireString(value, "operationId"),
        Enum.TryParse<GameActionStatus>(RequireString(value, "status"), true, out var status)
            && Enum.IsDefined(typeof(GameActionStatus), status)
                ? status
                : throw new InvalidDataException("The action receipt status is invalid."),
        value.TryGetProperty("result", out var result) ? result.GetRawText() : "{}",
        ParseMoment(value),
        ReadNullableInt64(value, "stateRevision"),
        ReadNullableString(value, "code"),
        ReadNullableString(value, "message"));

    private static GameMoment ParseMoment(JsonElement value)
    {
        if (!value.TryGetProperty("tick", out var tick) || !tick.TryGetInt64(out var tickValue))
        {
            throw new InvalidDataException("The action wire value has an invalid tick.");
        }

        string? calendar = null;
        if (value.TryGetProperty("calendar", out var calendarElement)
            && calendarElement.ValueKind != JsonValueKind.Null)
        {
            calendar = calendarElement.GetRawText();
        }

        return new GameMoment(RequireString(value, "timelineId"), tickValue, calendar);
    }

    private static long? ReadNullableInt64(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException("The action wire value has an invalid nullable integer.");
    }

    private static string? ReadNullableString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : throw new InvalidDataException("The action wire value has an invalid nullable string.");
    }
}
