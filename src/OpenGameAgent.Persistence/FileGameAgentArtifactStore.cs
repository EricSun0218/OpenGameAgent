using System;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.Persistence;

public sealed class FileGameAgentArtifactStore : IGameAgentArtifactStore
{
    private const string Suffix = ".artifact.json";
    private readonly FileStore _files;

    public FileGameAgentArtifactStore(
        string directory,
        long maximumFileBytes = 20_000_000,
        int concurrencyStripes = 64)
    {
        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
    }

    public async ValueTask PutAsync(GameAgentArtifact artifact, CancellationToken cancellationToken)
    {
        if (artifact is null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }

        var storageKey = StorageKey(artifact.SessionId, artifact.ActorId, artifact.ArtifactId);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(storageKey, Suffix);
            var currentDocument = await _files.ReadAsync<ArtifactDocument>(path, cancellationToken).ConfigureAwait(false);
            if (currentDocument is not null)
            {
                var current = Decode(currentDocument);
                if (!Equivalent(current, artifact))
                {
                    throw new PersistenceException("An artifact ID cannot be reused for different content.");
                }

                return;
            }

            await _files.WriteAtomicAsync(path, Encode(artifact), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameAgentArtifact?> GetAsync(
        string sessionId,
        string actorId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(actorId)
            || string.IsNullOrWhiteSpace(artifactId))
        {
            throw new ArgumentException("Artifact IDs and owners are required.");
        }

        var storageKey = StorageKey(sessionId, actorId, artifactId);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken).ConfigureAwait(false);
            var document = await _files.ReadAsync<ArtifactDocument>(
                _files.PathFor(storageKey, Suffix),
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            var artifact = Decode(document);
            if (!string.Equals(artifact.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(artifact.ActorId, actorId, StringComparison.Ordinal)
                || !string.Equals(artifact.ArtifactId, artifactId, StringComparison.Ordinal))
            {
                throw new PersistenceException("The artifact identity does not match its storage key.");
            }

            return artifact;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string StorageKey(string sessionId, string actorId, string artifactId) =>
        string.Concat(sessionId, "\n", actorId, "\n", artifactId);

    private static ArtifactDocument Encode(GameAgentArtifact artifact) => new()
    {
        FormatVersion = 1,
        ArtifactId = artifact.ArtifactId,
        SessionId = artifact.SessionId,
        ActorId = artifact.ActorId,
        MediaType = artifact.MediaType,
        Content = artifact.Content,
        TimelineId = artifact.CreatedAt.TimelineId,
        Tick = artifact.CreatedAt.Tick,
        CalendarJson = artifact.CreatedAt.CalendarJson,
    };

    private static GameAgentArtifact Decode(ArtifactDocument document)
    {
        if (document.FormatVersion != 1)
        {
            throw new PersistenceException("The artifact document has an unsupported format.");
        }

        return FileStore.DecodeDocument(
            "artifact document",
            () => new GameAgentArtifact(
                document.ArtifactId,
                document.SessionId,
                document.ActorId,
                document.MediaType,
                document.Content,
                new GameMoment(document.TimelineId, document.Tick, document.CalendarJson)));
    }

    private static bool Equivalent(GameAgentArtifact left, GameAgentArtifact right) =>
        string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal)
        && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
        && string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal)
        && string.Equals(left.MediaType, right.MediaType, StringComparison.Ordinal)
        && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
        && left.CreatedAt == right.CreatedAt;

    private sealed class ArtifactDocument
    {
        public int FormatVersion { get; set; }

        public string ArtifactId { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public string TimelineId { get; set; } = string.Empty;

        public long Tick { get; set; }

        public string? CalendarJson { get; set; }
    }
}
