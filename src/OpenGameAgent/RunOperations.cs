using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public enum GameRunToolClaimStatus
{
    Execute,
    Replay,
    Recover,
    Blocked,
}

public sealed class GameRunToolIntent
{
    public GameRunToolIntent(
        string operationId,
        GameSessionKey key,
        string inputId,
        int turn,
        int toolCallIndex,
        string toolName,
        string argumentsJson,
        ToolRisk risk,
        ToolReplayPolicy replayPolicy)
    {
        Key = key.EnsureValid(nameof(key));
        InputId = GameJson.RequireId(inputId, nameof(inputId));
        Turn = turn > 0 ? turn : throw new ArgumentOutOfRangeException(nameof(turn));
        ToolCallIndex = toolCallIndex >= 0 ? toolCallIndex : throw new ArgumentOutOfRangeException(nameof(toolCallIndex));
        ToolName = GameJson.RequireId(toolName, nameof(toolName));
        ArgumentsJson = GameRunToolOperationIds.NormalizeArguments(argumentsJson);
        if (!Enum.IsDefined(typeof(ToolRisk), risk) || !Enum.IsDefined(typeof(ToolReplayPolicy), replayPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        Risk = risk;
        ReplayPolicy = replayPolicy;
        ArgumentsDigest = GameRunToolOperationIds.DigestArguments(ArgumentsJson);
        OperationId = GameJson.RequireId(operationId, nameof(operationId));
        var expectedOperationId = GameRunToolOperationIds.CreateV1(
            Key.SessionId,
            Key.ActorId,
            InputId,
            Turn,
            ToolCallIndex,
            ToolName,
            ArgumentsJson);
        if (!string.Equals(OperationId, expectedOperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The run tool operation ID does not match its canonical intent.", nameof(operationId));
        }
    }

    public string OperationId { get; }
    public GameSessionKey Key { get; }
    public string InputId { get; }
    public int Turn { get; }
    public int ToolCallIndex { get; }
    public string ToolName { get; }
    public string ArgumentsJson { get; }
    public string ArgumentsDigest { get; }
    public ToolRisk Risk { get; }
    public ToolReplayPolicy ReplayPolicy { get; }
}

public sealed class GameRunToolEntry
{
    public GameRunToolEntry(
        GameRunToolIntent intent,
        int dispatchAttempts,
        ToolResult? result = null)
    {
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        DispatchAttempts = dispatchAttempts >= 0
            ? dispatchAttempts
            : throw new ArgumentOutOfRangeException(nameof(dispatchAttempts));
        Result = result;
    }

    public GameRunToolIntent Intent { get; }
    public int DispatchAttempts { get; }
    public bool Dispatched => DispatchAttempts > 0;
    public ToolResult? Result { get; }
    public bool Completed => Result is not null && !Result.OutcomeUncertain;
}

public sealed class GameRunToolClaim
{
    public GameRunToolClaim(GameRunToolClaimStatus status, GameRunToolEntry entry)
    {
        if (!Enum.IsDefined(typeof(GameRunToolClaimStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        if (status == GameRunToolClaimStatus.Replay && !entry.Completed)
        {
            throw new ArgumentException("A replay claim requires a completed result.", nameof(entry));
        }

        Status = status;
    }

    public GameRunToolClaimStatus Status { get; }
    public GameRunToolEntry Entry { get; }
}

/// <summary>
/// Durable exactly-once boundary for ordinary tools. It is optional and deliberately separate from
/// <see cref="IGameActionJournal"/>, which remains authoritative for world-changing game actions.
/// </summary>
public interface IGameRunOperationJournal
{
    ValueTask<GameRunToolClaim> ClaimToolAsync(GameRunToolIntent intent, CancellationToken cancellationToken);

    ValueTask<GameRunToolEntry?> FindToolAsync(string operationId, CancellationToken cancellationToken);

    ValueTask<GameRunToolEntry> CompleteToolAsync(
        string operationId,
        ToolResult result,
        CancellationToken cancellationToken);
}

public static class GameRunToolOperationIds
{
    public const string Version1Prefix = "oga-run-tool-v1:";

    public static string CreateV1(GameInput input, BeforeToolExecutionContext execution)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (execution is null)
        {
            throw new ArgumentNullException(nameof(execution));
        }

        return CreateV1(
            input.SessionId,
            input.ActorId,
            input.InputId,
            execution.Turn,
            execution.ToolCallIndex,
            execution.ToolCall.Name,
            execution.Arguments.GetRawText());
    }

    public static string CreateV1(
        string sessionId,
        string actorId,
        string inputId,
        int turn,
        int toolCallIndex,
        string toolName,
        string argumentsJson)
    {
        if (turn <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turn));
        }

        if (toolCallIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toolCallIndex));
        }

        using var canonical = new MemoryStream();
        using (var writer = new BinaryWriter(canonical, Encoding.UTF8, leaveOpen: true))
        {
            Write(writer, "OpenGameAgent.GameRunToolOperationId.v1");
            Write(writer, sessionId);
            Write(writer, actorId);
            Write(writer, inputId);
            writer.Write(turn);
            writer.Write(toolCallIndex);
            Write(writer, toolName);
            Write(writer, DigestArguments(argumentsJson));
        }

        canonical.Position = 0;
        using var algorithm = SHA256.Create();
        return Version1Prefix + Hex(algorithm.ComputeHash(canonical));
    }

    public static bool IsVersion1(string? operationId) =>
        operationId?.StartsWith(Version1Prefix, StringComparison.Ordinal) == true
        && operationId.Length == Version1Prefix.Length + 64
        && operationId.Skip(Version1Prefix.Length).All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string DigestArguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(NormalizeArguments(argumentsJson));
        var builder = new StringBuilder();
        AppendCanonical(builder, document.RootElement);
        using var algorithm = SHA256.Create();
        return Hex(algorithm.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendCanonical(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                foreach (var property in value.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    AppendString(builder, property.Name);
                    AppendCanonical(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                foreach (var item in value.EnumerateArray())
                {
                    AppendCanonical(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(builder, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                builder.Append('n').Append(value.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append('t');
                break;
            case JsonValueKind.False:
                builder.Append('f');
                break;
            case JsonValueKind.Null:
                builder.Append('0');
                break;
            default:
                throw new ArgumentException("Unsupported JSON value in tool arguments.", nameof(value));
        }
    }

    private static void AppendString(StringBuilder builder, string value) =>
        builder.Append('s').Append(value.Length).Append(':').Append(value);

    internal static string NormalizeArguments(string argumentsJson)
    {
        var valid = GameJson.RequireValid(argumentsJson, nameof(argumentsJson));
        using var document = JsonDocument.Parse(valid);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must contain a JSON object.", nameof(argumentsJson));
        }

        return document.RootElement.GetRawText();
    }

    private static void Write(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(GameJson.RequireId(value, nameof(value)));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string Hex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
}

public sealed class InMemoryGameRunOperationJournal : IGameRunOperationJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameRunToolEntry> _tools = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public InMemoryGameRunOperationJournal(int capacity = 100_000)
    {
        _capacity = capacity is >= 1 and <= 10_000_000
            ? capacity
            : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public ValueTask<GameRunToolClaim> ClaimToolAsync(
        GameRunToolIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        lock (_gate)
        {
            if (!_tools.TryGetValue(intent.OperationId, out var current))
            {
                if (_tools.Count >= _capacity)
                {
                    throw new GameRuntimeLimitException(nameof(_capacity), "The run-operation journal reached its capacity.");
                }

                var created = new GameRunToolEntry(intent, 1);
                _tools.Add(intent.OperationId, created);
                return new ValueTask<GameRunToolClaim>(new GameRunToolClaim(GameRunToolClaimStatus.Execute, created));
            }

            EnsureSameIntent(current.Intent, intent);
            if (current.Completed)
            {
                return new ValueTask<GameRunToolClaim>(new GameRunToolClaim(GameRunToolClaimStatus.Replay, current));
            }

            return intent.ReplayPolicy switch
            {
                ToolReplayPolicy.Safe => Retry(current),
                ToolReplayPolicy.Recoverable => new ValueTask<GameRunToolClaim>(
                    new GameRunToolClaim(GameRunToolClaimStatus.Recover, current)),
                _ => new ValueTask<GameRunToolClaim>(
                    new GameRunToolClaim(GameRunToolClaimStatus.Blocked, current)),
            };
        }
    }

    public ValueTask<GameRunToolEntry?> FindToolAsync(string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        operationId = GameJson.RequireId(operationId, nameof(operationId));
        lock (_gate)
        {
            return new ValueTask<GameRunToolEntry?>(_tools.TryGetValue(operationId, out var value) ? value : null);
        }
    }

    public ValueTask<GameRunToolEntry> CompleteToolAsync(
        string operationId,
        ToolResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        operationId = GameJson.RequireId(operationId, nameof(operationId));
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }
        lock (_gate)
        {
            if (!_tools.TryGetValue(operationId, out var current) || !current.Dispatched)
            {
                throw new InvalidOperationException("Cannot complete an undispatched run tool operation.");
            }

            if (result.OutcomeUncertain)
            {
                return new ValueTask<GameRunToolEntry>(current);
            }

            if (current.Result is not null)
            {
                if (!GameRunToolResults.ValueEquals(current.Result, result))
                {
                    throw new InvalidOperationException("A completed run tool operation cannot change its result.");
                }

                return new ValueTask<GameRunToolEntry>(current);
            }

            var completed = new GameRunToolEntry(current.Intent, current.DispatchAttempts, result);
            _tools[operationId] = completed;
            return new ValueTask<GameRunToolEntry>(completed);
        }
    }

    private ValueTask<GameRunToolClaim> Retry(GameRunToolEntry current)
    {
        var retried = new GameRunToolEntry(current.Intent, checked(current.DispatchAttempts + 1));
        _tools[current.Intent.OperationId] = retried;
        return new ValueTask<GameRunToolClaim>(new GameRunToolClaim(GameRunToolClaimStatus.Execute, retried));
    }

    internal static void EnsureSameIntent(GameRunToolIntent stored, GameRunToolIntent requested)
    {
        if (!stored.Key.Equals(requested.Key)
            || !string.Equals(stored.InputId, requested.InputId, StringComparison.Ordinal)
            || stored.Turn != requested.Turn
            || stored.ToolCallIndex != requested.ToolCallIndex
            || !string.Equals(stored.ToolName, requested.ToolName, StringComparison.Ordinal)
            || !string.Equals(stored.ArgumentsDigest, requested.ArgumentsDigest, StringComparison.Ordinal)
            || stored.Risk != requested.Risk
            || stored.ReplayPolicy != requested.ReplayPolicy)
        {
            throw new InvalidOperationException("The run tool operation ID is already reserved for a different intent.");
        }
    }
}

internal static class GameRunToolResults
{
    internal static bool ValueEquals(ToolResult left, ToolResult right) =>
        left.IsError == right.IsError
        && string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal)
        && left.Terminate == right.Terminate
        && left.OutcomeUncertain == right.OutcomeUncertain
        && left.FailureCategory == right.FailureCategory
        && left.AddedToolNames.SequenceEqual(right.AddedToolNames, StringComparer.Ordinal)
        && GameAgentValueComparer.MessagesEqual(
            new[] { AgentMessage.ToolResult(new ToolCallContent("compare", "compare", "{}"), left, DateTimeOffset.UnixEpoch) },
            new[] { AgentMessage.ToolResult(new ToolCallContent("compare", "compare", "{}"), right, DateTimeOffset.UnixEpoch) });
}
