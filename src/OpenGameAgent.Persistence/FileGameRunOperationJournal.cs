using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Persistence;

public sealed class FileGameRunOperationJournal : IGameRunOperationJournal
{
    private const int FormatVersion = 1;
    private const string Suffix = ".run-tool.json";
    private readonly FileStore _files;
    private readonly int _maximumEntries;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    public FileGameRunOperationJournal(
        string directory,
        int maximumEntries = 100_000,
        long maximumFileBytes = 8_000_000,
        int concurrencyStripes = 64)
    {
        if (maximumEntries is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
        _maximumEntries = maximumEntries;
    }

    public async ValueTask<GameRunToolClaim> ClaimToolAsync(
        GameRunToolIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        var gate = _files.GateFor(intent.OperationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(intent.OperationId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(intent.OperationId, Suffix);
            var document = await _files.ReadAsync<RunToolDocument>(path, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                await EnsureCapacityAsync(path, intent, cancellationToken).ConfigureAwait(false);
                var created = new GameRunToolEntry(intent, 1);
                await _files.WriteAtomicAsync(path, Encode(created), cancellationToken).ConfigureAwait(false);
                return new GameRunToolClaim(GameRunToolClaimStatus.Execute, created);
            }

            var current = Decode(document);
            EnsureIdentity(current, intent.OperationId);
            InMemoryGameRunOperationJournal.EnsureSameIntent(current.Intent, intent);
            if (current.Completed)
            {
                return new GameRunToolClaim(GameRunToolClaimStatus.Replay, current);
            }

            if (intent.ReplayPolicy == ToolReplayPolicy.Safe)
            {
                var retried = new GameRunToolEntry(current.Intent, checked(current.DispatchAttempts + 1));
                await _files.WriteAtomicAsync(path, Encode(retried), cancellationToken).ConfigureAwait(false);
                return new GameRunToolClaim(GameRunToolClaimStatus.Execute, retried);
            }

            return new GameRunToolClaim(
                intent.ReplayPolicy == ToolReplayPolicy.Recoverable
                    ? GameRunToolClaimStatus.Recover
                    : GameRunToolClaimStatus.Blocked,
                current);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameRunToolEntry?> FindToolAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        var gate = _files.GateFor(operationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(operationId + Suffix, cancellationToken).ConfigureAwait(false);
            var document = await _files.ReadAsync<RunToolDocument>(
                _files.PathFor(operationId, Suffix),
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            var entry = Decode(document);
            EnsureIdentity(entry, operationId);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameRunToolEntry> CompleteToolAsync(
        string operationId,
        ToolResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }
        var gate = _files.GateFor(operationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(operationId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(operationId, Suffix);
            var document = await _files.ReadAsync<RunToolDocument>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cannot complete an unknown run tool operation.");
            var current = Decode(document);
            EnsureIdentity(current, operationId);
            if (!current.Dispatched)
            {
                throw new InvalidOperationException("Cannot complete an undispatched run tool operation.");
            }

            if (result.OutcomeUncertain)
            {
                return current;
            }

            if (current.Result is not null)
            {
                if (!GameRunToolResults.ValueEquals(current.Result, result))
                {
                    throw new PersistenceException("A completed run tool operation cannot change its result.");
                }

                return current;
            }

            var completed = new GameRunToolEntry(current.Intent, current.DispatchAttempts, result);
            await _files.WriteAtomicAsync(path, Encode(completed), cancellationToken).ConfigureAwait(false);
            return completed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask EnsureCapacityAsync(
        string path,
        GameRunToolIntent intent,
        CancellationToken cancellationToken)
    {
        await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var capacityLease = await _files.AcquireProcessLeaseAsync("run-operation-capacity", cancellationToken).ConfigureAwait(false);
            var raced = await _files.ReadAsync<RunToolDocument>(path, cancellationToken).ConfigureAwait(false);
            if (raced is not null)
            {
                var entry = Decode(raced);
                InMemoryGameRunOperationJournal.EnsureSameIntent(entry.Intent, intent);
                throw new PersistenceException("The run tool operation appeared during capacity admission; retry the claim.");
            }

            if (Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly)
                .Take(_maximumEntries)
                .Count() >= _maximumEntries)
            {
                throw new GameRuntimeLimitException(nameof(_maximumEntries), "The file run-operation journal reached its capacity.");
            }
        }
        finally
        {
            _capacityGate.Release();
        }
    }

    private static RunToolDocument Encode(GameRunToolEntry entry) => new()
    {
        FormatVersion = FormatVersion,
        OperationId = entry.Intent.OperationId,
        SessionId = entry.Intent.Key.SessionId,
        ActorId = entry.Intent.Key.ActorId,
        InputId = entry.Intent.InputId,
        Turn = entry.Intent.Turn,
        ToolCallIndex = entry.Intent.ToolCallIndex,
        ToolName = entry.Intent.ToolName,
        ArgumentsJson = entry.Intent.ArgumentsJson,
        ArgumentsDigest = entry.Intent.ArgumentsDigest,
        Risk = entry.Intent.Risk.ToString(),
        ReplayPolicy = entry.Intent.ReplayPolicy.ToString(),
        DispatchAttempts = entry.DispatchAttempts,
        Result = entry.Result is null ? null : EncodeResult(entry.Result),
    };

    private static GameRunToolEntry Decode(RunToolDocument document) =>
        FileStore.DecodeDocument("run tool operation", () =>
        {
            if (document.FormatVersion != FormatVersion
                || !Enum.TryParse<ToolRisk>(document.Risk, out var risk)
                || !Enum.IsDefined(typeof(ToolRisk), risk)
                || !Enum.TryParse<ToolReplayPolicy>(document.ReplayPolicy, out var replayPolicy)
                || !Enum.IsDefined(typeof(ToolReplayPolicy), replayPolicy))
            {
                throw new PersistenceException("The run tool operation format is unsupported or corrupt.");
            }

            var intent = new GameRunToolIntent(
                document.OperationId ?? throw new PersistenceException("The run tool operation ID is missing."),
                new GameSessionKey(
                    document.SessionId ?? throw new PersistenceException("The run tool session ID is missing."),
                    document.ActorId ?? throw new PersistenceException("The run tool actor ID is missing.")),
                document.InputId ?? throw new PersistenceException("The run tool input ID is missing."),
                document.Turn,
                document.ToolCallIndex,
                document.ToolName ?? throw new PersistenceException("The run tool name is missing."),
                document.ArgumentsJson ?? throw new PersistenceException("The run tool arguments are missing."),
                risk,
                replayPolicy);
            if (!string.Equals(intent.ArgumentsDigest, document.ArgumentsDigest, StringComparison.Ordinal))
            {
                throw new PersistenceException("The run tool argument digest is invalid.");
            }

            return new GameRunToolEntry(intent, document.DispatchAttempts, DecodeResult(document.Result));
        });

    private static ResultDocument EncodeResult(ToolResult result)
    {
        var call = new ToolCallContent("persisted", "persisted", "{}");
        return new ResultDocument
        {
            Message = AgentMessageCodec.Encode(AgentMessage.ToolResult(call, result, DateTimeOffset.UnixEpoch)),
            Terminate = result.Terminate,
            OutcomeUncertain = result.OutcomeUncertain,
            FailureCategory = result.FailureCategory.ToString(),
        };
    }

    private static ToolResult? DecodeResult(ResultDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        var message = AgentMessageCodec.Decode(
            document.Message ?? throw new PersistenceException("The persisted run tool result message is missing."));
        if (!Enum.TryParse<ToolFailureCategory>(document.FailureCategory, out var failureCategory)
            || !Enum.IsDefined(typeof(ToolFailureCategory), failureCategory))
        {
            throw new PersistenceException("The persisted run tool failure category is invalid.");
        }

        return new ToolResult(
            message.Content,
            message.IsError,
            message.DetailsJson,
            document.Terminate,
            message.Usage,
            document.OutcomeUncertain,
            message.AddedToolNames,
            failureCategory);
    }

    private static void EnsureIdentity(GameRunToolEntry entry, string operationId)
    {
        if (!string.Equals(entry.Intent.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new PersistenceException("The run tool operation identity does not match its storage path.");
        }
    }

    private sealed class RunToolDocument
    {
        public int FormatVersion { get; set; }
        public string? OperationId { get; set; }
        public string? SessionId { get; set; }
        public string? ActorId { get; set; }
        public string? InputId { get; set; }
        public int Turn { get; set; }
        public int ToolCallIndex { get; set; }
        public string? ToolName { get; set; }
        public string? ArgumentsJson { get; set; }
        public string? ArgumentsDigest { get; set; }
        public string? Risk { get; set; }
        public string? ReplayPolicy { get; set; }
        public int DispatchAttempts { get; set; }
        public ResultDocument? Result { get; set; }
    }

    private sealed class ResultDocument
    {
        public MessageDocument? Message { get; set; }
        public bool Terminate { get; set; }
        public bool OutcomeUncertain { get; set; }
        public string? FailureCategory { get; set; }
    }
}
