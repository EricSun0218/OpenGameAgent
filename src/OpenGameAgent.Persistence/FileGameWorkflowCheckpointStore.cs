using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class FileGameWorkflowCheckpointStore : IGameWorkflowCheckpointStore
{
    private const int CurrentFormatVersion = 2;
    private const string Suffix = ".workflow.json";
    private readonly FileStore _files;

    public FileGameWorkflowCheckpointStore(
        string directory,
        long maximumFileBytes = 4_000_000,
        int concurrencyStripes = 64)
    {
        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
    }

    public async ValueTask<GameWorkflowCheckpoint?> LoadAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ArgumentException("A workflow instance ID is required.", nameof(instanceId));
        }

        var gate = _files.GateFor(instanceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(instanceId + Suffix, cancellationToken).ConfigureAwait(false);
            var document = await _files.ReadAsync<CheckpointDocument>(
                _files.PathFor(instanceId, Suffix),
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            var checkpoint = Decode(document);
            if (!string.Equals(checkpoint.InstanceId, instanceId, StringComparison.Ordinal))
            {
                throw new PersistenceException("The workflow checkpoint identity does not match its storage key.");
            }

            return checkpoint;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameWorkflowCheckpointSaveResult> SaveAsync(
        GameWorkflowCheckpoint checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (checkpoint is null)
        {
            throw new ArgumentNullException(nameof(checkpoint));
        }

        var gate = _files.GateFor(checkpoint.InstanceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(checkpoint.InstanceId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(checkpoint.InstanceId, Suffix);
            var document = await _files.ReadAsync<CheckpointDocument>(path, cancellationToken).ConfigureAwait(false);
            var current = document is null ? null : Decode(document);
            if (current is not null
                && !string.Equals(current.InstanceId, checkpoint.InstanceId, StringComparison.Ordinal))
            {
                throw new PersistenceException("The workflow checkpoint identity does not match its storage key.");
            }

            if (current is not null
                && !string.Equals(current.Workflow, checkpoint.Workflow, StringComparison.Ordinal))
            {
                throw new PersistenceException("A workflow checkpoint instance cannot change workflows.");
            }

            if (current?.Completed == true)
            {
                throw new PersistenceException("A completed workflow checkpoint is immutable.");
            }

            if ((current?.Revision ?? 0) != expectedRevision)
            {
                return new GameWorkflowCheckpointSaveResult(
                    false,
                    current ?? new GameWorkflowCheckpoint(checkpoint.InstanceId, checkpoint.Workflow, 0, 0, "{}"));
            }

            if (checkpoint.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A workflow checkpoint revision must advance by exactly one.", nameof(checkpoint));
            }

            await _files.WriteAtomicAsync(path, Encode(checkpoint), cancellationToken).ConfigureAwait(false);
            return new GameWorkflowCheckpointSaveResult(true, checkpoint);
        }
        finally
        {
            gate.Release();
        }
    }

    private static CheckpointDocument Encode(GameWorkflowCheckpoint checkpoint) => new()
    {
        FormatVersion = CurrentFormatVersion,
        InstanceId = checkpoint.InstanceId,
        Workflow = checkpoint.Workflow,
        Revision = checkpoint.Revision,
        NextStep = checkpoint.NextStep,
        StateJson = checkpoint.StateJson,
        Completed = checkpoint.Completed,
        Error = checkpoint.Error,
        Invocation = checkpoint.Invocation is null ? null : new InvocationDocument
        {
            InputId = checkpoint.Invocation.InputId,
            Messages = checkpoint.Invocation.Messages.Select(AgentMessageCodec.Encode).ToList(),
            Complete = checkpoint.Invocation.Complete,
            Succeeded = checkpoint.Invocation.Succeeded,
            Error = checkpoint.Invocation.Error,
        },
    };

    private static GameWorkflowCheckpoint Decode(CheckpointDocument document)
    {
        if (document.FormatVersion != CurrentFormatVersion)
        {
            throw new PersistenceException("The workflow checkpoint has an unsupported format.");
        }

        return FileStore.DecodeDocument(
            "workflow checkpoint document",
            () => new GameWorkflowCheckpoint(
                document.InstanceId,
                document.Workflow,
                document.Revision,
                document.NextStep,
                document.StateJson,
                document.Completed,
                document.Error,
                document.Invocation is not null
                    ? new GameWorkflowInvocationResult(
                        document.Invocation.InputId,
                        (document.Invocation.Messages ?? throw new PersistenceException("Workflow invocation messages are missing."))
                        .Select(AgentMessageCodec.Decode)
                        .ToArray(),
                        document.Invocation.Complete,
                        document.Invocation.Succeeded,
                        document.Invocation.Error)
                    : null));
    }

    private sealed class CheckpointDocument
    {
        public int FormatVersion { get; set; }

        public string InstanceId { get; set; } = string.Empty;

        public string Workflow { get; set; } = string.Empty;

        public long Revision { get; set; }

        public int NextStep { get; set; }

        public string StateJson { get; set; } = "{}";

        public bool Completed { get; set; }

        public string? Error { get; set; }

        public InvocationDocument? Invocation { get; set; }
    }

    private sealed class InvocationDocument
    {
        public string InputId { get; set; } = string.Empty;

        public List<MessageDocument>? Messages { get; set; }

        public bool Complete { get; set; }

        public bool Succeeded { get; set; }

        public string? Error { get; set; }
    }
}
