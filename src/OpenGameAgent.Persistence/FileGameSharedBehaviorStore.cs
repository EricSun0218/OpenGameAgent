using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.Persistence;

/// <summary>
/// Crash-tolerant local storage for immutable shared behavior publications and revocation records.
/// </summary>
public sealed class FileGameSharedBehaviorStore : IGameSharedBehaviorStore
{
    private const string Suffix = ".shared-behavior.json";
    private const string CapacityIdentity = "shared-behavior-catalog-capacity";
    private const string IndexFileName = "shared-behavior-catalog.index.json";
    private const string PendingFileName = "shared-behavior-catalog.pending.json";
    private readonly FileStore _files;
    private readonly int _maximumPublications;
    private readonly long _maximumFileBytes;
    private readonly string _indexPath;
    private readonly string _pendingPath;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    public FileGameSharedBehaviorStore(
        string directory,
        int maximumPublications = 10_000,
        long maximumFileBytes = 4_000_000,
        int concurrencyStripes = 64)
    {
        if (maximumPublications < 1 || maximumPublications > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPublications));
        }

        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
        _maximumPublications = maximumPublications;
        _maximumFileBytes = maximumFileBytes;
        _indexPath = Path.Combine(_files.DirectoryPath, IndexFileName);
        _pendingPath = Path.Combine(_files.DirectoryPath, PendingFileName);
        EnsureDirectoryChainIsSafe(_files.DirectoryPath);
    }

    public async ValueTask<GameSharedBehaviorPublication?> LoadAsync(
        string publicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Require(publicationId, 256, nameof(publicationId));
        EnsureDirectoryChainIsSafe(_files.DirectoryPath);
        EnsureRegularFileOrMissing(_files.PathFor(id, Suffix));
        await RecoverPendingInsertIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCommittedAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GameSharedBehaviorStoreSaveResult> SaveAsync(
        GameSharedBehaviorPublication publication,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (publication is null)
        {
            throw new ArgumentNullException(nameof(publication));
        }

        if (expectedRevision < 0 || publication.Revision != checked(expectedRevision + 1))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        EnsureDirectoryChainIsSafe(_files.DirectoryPath);
        EnsureRegularFileOrMissing(_files.PathFor(publication.PublicationId + Suffix, ".lock"));
        var publicationPath = _files.PathFor(publication.PublicationId, Suffix);
        EnsureRegularFileOrMissing(publicationPath);
        return expectedRevision == 0
            ? await InsertAsync(publication, publicationPath, cancellationToken).ConfigureAwait(false)
            : await UpdateAsync(publication, expectedRevision, publicationPath, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GameSharedBehaviorStoreSaveResult> InsertAsync(
        GameSharedBehaviorPublication publication,
        string publicationPath,
        CancellationToken cancellationToken)
    {
        EnsureRegularFileOrMissing(_files.PathFor(CapacityIdentity, ".lock"));
        await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var capacityLease = await _files.AcquireProcessLeaseAsync(
                CapacityIdentity,
                cancellationToken).ConfigureAwait(false);
            await RecoverPendingInsertAsync(cancellationToken).ConfigureAwait(false);
            var gate = _files.GateFor(publication.PublicationId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var recordLease = await _files.AcquireProcessLeaseAsync(
                    publication.PublicationId + Suffix,
                    cancellationToken).ConfigureAwait(false);
                var current = await ReadCommittedAsync(publication.PublicationId, cancellationToken).ConfigureAwait(false);
                if (current is not null)
                {
                    return new GameSharedBehaviorStoreSaveResult(false, current);
                }

                var catalog = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
                if (catalog.EntryCount >= _maximumPublications)
                {
                    throw new PersistenceException("The shared behavior catalog reached its configured capacity.");
                }

                var existingReservation = await ReadFamilyReservationAsync(
                    publication.BehaviorFamilyId,
                    publication.FamilyVersion,
                    cancellationToken).ConfigureAwait(false);
                if (existingReservation is not null)
                {
                    var conflict = await ReadCommittedAsync(
                        existingReservation.PublicationId,
                        cancellationToken).ConfigureAwait(false)
                        ?? throw new PersistenceException("A shared behavior family-version reservation has no publication.");
                    return new GameSharedBehaviorStoreSaveResult(false, conflict);
                }

                try
                {
                    GameSharedBehaviorStoreContract.ValidateTransition(null, publication, 0);
                }
                catch (InvalidOperationException exception)
                {
                    throw new PersistenceException("The shared behavior publication transition is invalid.", exception);
                }

                var audienceState = await ReadAudienceStateForInsertAsync(
                    publication.Audience,
                    catalog,
                    cancellationToken).ConfigureAwait(false);
                var audienceIndex = audienceState.Index;
                var audienceManifest = audienceState.Manifest;
                var expectedAudienceCount = audienceManifest.PublicationCount;
                AddToAudienceIndex(audienceIndex, publication.PublicationId);
                audienceManifest.PublicationCount = checked(audienceManifest.PublicationCount + 1);
                audienceManifest.PublicationIdsHash = AudienceIndexHash(audienceIndex);
                catalog.EntryCount = checked(catalog.EntryCount + 1);
                var reservation = FamilyReservationDocument.For(publication);
                var pending = new PendingInsertDocument
                {
                    FormatVersion = 1,
                    PublicationId = publication.PublicationId,
                    AudienceKind = publication.Audience.Kind,
                    AudienceId = publication.Audience.AudienceId,
                    ExpectedEntryCount = catalog.EntryCount - 1,
                    ExpectedAudienceCount = expectedAudienceCount,
                    BehaviorFamilyId = publication.BehaviorFamilyId,
                    FamilyVersion = publication.FamilyVersion,
                    ContentHash = publication.Behavior.ContentHash,
                };
                EnsureDocumentFits(Encode(publication), "publication");
                EnsureDocumentFits(audienceIndex, "audience index");
                EnsureDocumentFits(audienceManifest, "audience manifest");
                EnsureDocumentFits(reservation, "family-version reservation");
                EnsureDocumentFits(catalog, "catalog manifest");
                EnsureDocumentFits(pending, "pending insert");

                await _files.WriteAtomicAsync(_pendingPath, pending, cancellationToken).ConfigureAwait(false);
                EnsureRegularFile(_pendingPath);
                await _files.WriteAtomicAsync(
                    publicationPath,
                    Encode(publication),
                    cancellationToken).ConfigureAwait(false);
                EnsureRegularFile(publicationPath);
                await WriteAudienceIndexAsync(audienceIndex, cancellationToken).ConfigureAwait(false);
                await WriteAudienceManifestAsync(audienceManifest, cancellationToken).ConfigureAwait(false);
                await WriteFamilyReservationAsync(reservation, cancellationToken).ConfigureAwait(false);
                await WriteIndexAsync(catalog, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(_pendingPath);
                return new GameSharedBehaviorStoreSaveResult(true, publication);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            _capacityGate.Release();
        }
    }

    private async ValueTask<GameSharedBehaviorStoreSaveResult> UpdateAsync(
        GameSharedBehaviorPublication publication,
        long expectedRevision,
        string publicationPath,
        CancellationToken cancellationToken)
    {
        await RecoverPendingInsertIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var gate = _files.GateFor(publication.PublicationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var recordLease = await _files.AcquireProcessLeaseAsync(
                publication.PublicationId + Suffix,
                cancellationToken).ConfigureAwait(false);
            var current = await ReadCommittedAsync(publication.PublicationId, cancellationToken).ConfigureAwait(false);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                return new GameSharedBehaviorStoreSaveResult(false, current);
            }

            try
            {
                GameSharedBehaviorStoreContract.ValidateTransition(current, publication, expectedRevision);
            }
            catch (InvalidOperationException exception)
            {
                throw new PersistenceException("The shared behavior publication transition is invalid.", exception);
            }

            await _files.WriteAtomicAsync(publicationPath, Encode(publication), cancellationToken).ConfigureAwait(false);
            EnsureRegularFile(publicationPath);
            return new GameSharedBehaviorStoreSaveResult(true, publication);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GameSharedBehaviorPublication>> QueryAsync(
        GameSharedBehaviorStoreQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectoryChainIsSafe(_files.DirectoryPath);
        await RecoverPendingInsertIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var audienceIndexes = await Task.WhenAll(query.Audiences
            .Select(value => ReadAudienceIndexRecoveringAsync(value, cancellationToken).AsTask()))
            .ConfigureAwait(false);
        var cursors = new List<AudienceCursor>(audienceIndexes.Length);
        foreach (var index in audienceIndexes)
        {
            var audience = new GameSharedBehaviorAudience(index.Kind, index.AudienceId);
            var position = 0;
            if (query.AfterPublicationId is not null)
            {
                position = index.PublicationIds.BinarySearch(query.AfterPublicationId, StringComparer.Ordinal);
                position = position >= 0 ? position + 1 : ~position;
            }

            cursors.Add(new AudienceCursor(index, audience, position));
        }

        var results = new List<GameSharedBehaviorPublication>();
        var scanned = 0;
        while (results.Count < query.MaximumResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<PublicationCandidate>(Math.Min(16, query.MaximumResults - results.Count));
            while (batch.Count < batch.Capacity)
            {
                var candidate = TakeNextCandidate(cursors);
                if (candidate is null)
                {
                    break;
                }

                batch.Add(candidate);
            }

            if (batch.Count == 0)
            {
                break;
            }

            scanned = checked(scanned + batch.Count);
            if (scanned > _maximumPublications)
            {
                throw new PersistenceException("The shared behavior catalog exceeds its configured capacity.");
            }

            var publications = await Task.WhenAll(batch
                .Select(candidate => ReadCandidateAsync(candidate, cancellationToken).AsTask()))
                .ConfigureAwait(false);
            foreach (var publication in publications)
            {
                if (query.IncludeRevoked || publication.Status == GameSharedBehaviorPublicationStatus.Published)
                {
                    results.Add(publication);
                    if (results.Count == query.MaximumResults)
                    {
                        break;
                    }
                }
            }
        }

        return Array.AsReadOnly(results.ToArray());
    }

    private async ValueTask<GameSharedBehaviorPublication> ReadCandidateAsync(
        PublicationCandidate candidate,
        CancellationToken cancellationToken)
    {
        var publication = await ReadCommittedAsync(candidate.PublicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceException("An indexed shared behavior publication is missing.");
        if (!publication.Audience.Equals(candidate.Audience))
        {
            throw new PersistenceException("A shared behavior publication is indexed under the wrong audience.");
        }

        return publication;
    }

    private static PublicationCandidate? TakeNextCandidate(IReadOnlyList<AudienceCursor> cursors)
    {
        PublicationCandidate? selected = null;
        AudienceCursor? selectedCursor = null;
        foreach (var cursor in cursors)
        {
            if (cursor.Position >= cursor.Index.PublicationIds.Count)
            {
                continue;
            }

            var publicationId = cursor.Index.PublicationIds[cursor.Position];
            var comparison = selected is null
                ? -1
                : string.CompareOrdinal(publicationId, selected.PublicationId);
            if (comparison < 0)
            {
                selected = new PublicationCandidate(publicationId, cursor.Audience);
                selectedCursor = cursor;
            }
            else if (comparison == 0)
            {
                throw new PersistenceException("A shared behavior publication is indexed under multiple audiences.");
            }
        }

        if (selectedCursor is not null)
        {
            selectedCursor.Position++;
        }

        return selected;
    }

    private async ValueTask<GameSharedBehaviorPublication?> ReadAsync(
        string publicationId,
        CancellationToken cancellationToken)
    {
        var document = await _files.ReadAtomicSnapshotAsync<PublicationDocument>(
            _files.PathFor(publicationId, Suffix),
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var publication = Decode(document);
        if (!string.Equals(publication.PublicationId, publicationId, StringComparison.Ordinal))
        {
            throw new PersistenceException("The shared behavior publication identity does not match its storage key.");
        }

        return publication;
    }

    private async ValueTask<GameSharedBehaviorPublication?> ReadCommittedAsync(
        string publicationId,
        CancellationToken cancellationToken)
    {
        var publication = await ReadAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return null;
        }

        // The immutable payload precedes the reservation that linearizes an insert. A reader
        // that sees the payload must first reconcile any concurrent pending transaction.
        await RecoverPendingInsertIfNeededAsync(cancellationToken).ConfigureAwait(false);
        publication = await ReadAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceException("A shared behavior publication disappeared during commit recovery.");
        var reservation = await ReadFamilyReservationAsync(
            publication.BehaviorFamilyId,
            publication.FamilyVersion,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null || !ReservationMatches(reservation, publication))
        {
            throw new PersistenceException("The shared behavior family-version reservation is missing or inconsistent.");
        }

        return publication;
    }

    private int CountFiles()
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly))
        {
            EnsureRegularFile(path);
            if (++count > _maximumPublications)
            {
                break;
            }
        }

        return count;
    }

    private async ValueTask RecoverPendingInsertIfNeededAsync(CancellationToken cancellationToken)
    {
        EnsureRegularFileOrMissing(_pendingPath);
        if (!File.Exists(_pendingPath))
        {
            return;
        }

        EnsureRegularFileOrMissing(_files.PathFor(CapacityIdentity, ".lock"));
        await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var capacityLease = await _files.AcquireProcessLeaseAsync(
                CapacityIdentity,
                cancellationToken).ConfigureAwait(false);
            await RecoverPendingInsertAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _capacityGate.Release();
        }
    }

    private async ValueTask RecoverPendingInsertAsync(CancellationToken cancellationToken)
    {
        EnsureRegularFileOrMissing(_pendingPath);
        if (!File.Exists(_pendingPath))
        {
            return;
        }

        var pending = await _files.ReadAtomicSnapshotAsync<PendingInsertDocument>(
            _pendingPath,
            cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceException("The pending shared behavior insert disappeared during recovery.");
        string publicationId;
        GameSharedBehaviorAudience pendingAudience;
        string behaviorFamilyId;
        try
        {
            if (pending.FormatVersion != 1
                || pending.ExpectedEntryCount < 0
                || pending.ExpectedEntryCount >= _maximumPublications
                || pending.ExpectedAudienceCount < 0
                || pending.ExpectedAudienceCount >= _maximumPublications
                || !Enum.IsDefined(typeof(GameSharedBehaviorAudienceKind), pending.AudienceKind)
                || pending.FamilyVersion < 1
                || pending.ContentHash.Length != 64
                || pending.ContentHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new PersistenceException("The pending shared behavior insert is corrupt.");
            }

            publicationId = Require(pending.PublicationId, 256, nameof(pending.PublicationId));
            pendingAudience = new GameSharedBehaviorAudience(pending.AudienceKind, pending.AudienceId);
            behaviorFamilyId = RequireStableId(pending.BehaviorFamilyId, nameof(pending.BehaviorFamilyId));
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new PersistenceException("The pending shared behavior insert is corrupt.", exception);
        }
        var catalog = await ReadIndexAsync(cancellationToken, allowMissingWithPublications: true).ConfigureAwait(false);
        var audienceIndex = await ReadAudienceIndexAsync(pendingAudience, cancellationToken).ConfigureAwait(false);
        var audienceManifest = await ReadAudienceManifestAsync(pendingAudience, cancellationToken).ConfigureAwait(false);
        var publication = await ReadAsync(publicationId, cancellationToken).ConfigureAwait(false);
        var reservation = await ReadFamilyReservationAsync(
            behaviorFamilyId,
            pending.FamilyVersion,
            cancellationToken).ConfigureAwait(false);
        var desiredAudienceCount = checked(pending.ExpectedAudienceCount + 1);
        if (catalog.EntryCount == pending.ExpectedEntryCount)
        {
            if (publication is null)
            {
                if (reservation is not null
                    || !AudienceStateMatchesBeforeInsert(
                        audienceIndex,
                        audienceManifest,
                        publicationId,
                        pending.ExpectedAudienceCount))
                {
                    throw new PersistenceException("The pending shared behavior insert has partial indexes without a payload.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(_pendingPath);
                return;
            }

            if (!publication.Audience.Equals(pendingAudience)
                || !string.Equals(publication.BehaviorFamilyId, behaviorFamilyId, StringComparison.Ordinal)
                || publication.FamilyVersion != pending.FamilyVersion
                || !string.Equals(publication.Behavior.ContentHash, pending.ContentHash, StringComparison.Ordinal))
            {
                throw new PersistenceException("The pending shared behavior insert metadata does not match its payload.");
            }

            ValidatePendingAudienceState(
                audienceIndex,
                audienceManifest,
                pendingAudience,
                publicationId,
                pending.ExpectedAudienceCount,
                desiredAudienceCount);
            audienceIndex = NormalizePendingAudienceIndex(
                audienceIndex,
                pendingAudience,
                publicationId,
                pending.ExpectedAudienceCount,
                desiredAudienceCount);
            audienceManifest = NormalizePendingAudienceManifest(
                audienceManifest,
                pendingAudience,
                pending.ExpectedAudienceCount,
                desiredAudienceCount);
            audienceManifest.PublicationIdsHash = AudienceIndexHash(audienceIndex);
            EnsureDocumentFits(audienceIndex, "audience index");
            EnsureDocumentFits(audienceManifest, "audience manifest");
            await WriteAudienceIndexAsync(audienceIndex, cancellationToken).ConfigureAwait(false);
            await WriteAudienceManifestAsync(audienceManifest, cancellationToken).ConfigureAwait(false);

            if (reservation is null)
            {
                reservation = FamilyReservationDocument.For(publication);
                EnsureDocumentFits(reservation, "family-version reservation");
                await WriteFamilyReservationAsync(reservation, cancellationToken).ConfigureAwait(false);
            }
            else if (!ReservationMatches(reservation, publication))
            {
                throw new PersistenceException("The pending shared behavior insert conflicts with a family-version reservation.");
            }

            catalog.EntryCount = checked(catalog.EntryCount + 1);
            EnsureDocumentFits(catalog, "catalog manifest");
            await WriteIndexAsync(catalog, cancellationToken).ConfigureAwait(false);
        }
        else if (catalog.EntryCount != checked(pending.ExpectedEntryCount + 1)
                 || publication is null
                 || !publication.Audience.Equals(pendingAudience)
                 || !string.Equals(publication.BehaviorFamilyId, behaviorFamilyId, StringComparison.Ordinal)
                 || publication.FamilyVersion != pending.FamilyVersion
                 || !string.Equals(publication.Behavior.ContentHash, pending.ContentHash, StringComparison.Ordinal)
                 || audienceIndex is null
                 || audienceManifest is null
                 || !AudienceStateMatches(audienceIndex, audienceManifest)
                 || audienceIndex.PublicationIds.Count != desiredAudienceCount
                 || !audienceIndex.PublicationIds.Contains(publicationId, StringComparer.Ordinal)
                 || reservation is null
                 || !ReservationMatches(reservation, publication))
        {
            throw new PersistenceException("The pending shared behavior insert cannot be reconciled safely.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_pendingPath);
    }

    private static void ValidatePendingAudienceState(
        CatalogAudienceIndexDocument? index,
        AudienceManifestDocument? manifest,
        GameSharedBehaviorAudience audience,
        string publicationId,
        int expectedCount,
        int desiredCount)
    {
        if (index is null && manifest is null)
        {
            if (expectedCount != 0)
            {
                throw new PersistenceException("The pending shared behavior audience state is missing.");
            }

            return;
        }

        if (manifest is null)
        {
            if (expectedCount == 0
                && index is not null
                && index.PublicationIds.Count == desiredCount
                && index.PublicationIds.Contains(publicationId, StringComparer.Ordinal))
            {
                return;
            }

            throw new PersistenceException("The pending shared behavior audience manifest is inconsistent.");
        }

        if (index is null)
        {
            throw new PersistenceException("The pending shared behavior audience index is inconsistent.");
        }

        if (index.PublicationIds.Count == manifest.PublicationCount)
        {
            EnsureAudienceStateMatches(index, manifest);
            var count = index.PublicationIds.Count;
            if (count != expectedCount && count != desiredCount)
            {
                throw new PersistenceException("The pending shared behavior audience state has an unexpected count.");
            }

            return;
        }

        if (index.PublicationIds.Count != desiredCount
            || manifest.PublicationCount != expectedCount
            || !index.PublicationIds.Contains(publicationId, StringComparer.Ordinal))
        {
            throw new PersistenceException("The pending shared behavior audience state cannot be reconciled safely.");
        }

        var prior = NewAudienceIndex(audience);
        prior.PublicationIds.AddRange(index.PublicationIds.Where(value =>
            !string.Equals(value, publicationId, StringComparison.Ordinal)));
        if (prior.PublicationIds.Count != expectedCount
            || !string.Equals(manifest.PublicationIdsHash, AudienceIndexHash(prior), StringComparison.Ordinal))
        {
            throw new PersistenceException("The pending shared behavior audience manifest does not describe the prior state.");
        }
    }

    private static bool AudienceStateMatchesBeforeInsert(
        CatalogAudienceIndexDocument? index,
        AudienceManifestDocument? manifest,
        string publicationId,
        int expectedCount)
    {
        if (index is null || manifest is null)
        {
            return expectedCount == 0 && index is null && manifest is null;
        }

        return index.PublicationIds.Count == expectedCount
            && manifest.PublicationCount == expectedCount
            && string.Equals(manifest.PublicationIdsHash, AudienceIndexHash(index), StringComparison.Ordinal)
            && !index.PublicationIds.Contains(publicationId, StringComparer.Ordinal);
    }

    private static CatalogAudienceIndexDocument NormalizePendingAudienceIndex(
        CatalogAudienceIndexDocument? index,
        GameSharedBehaviorAudience audience,
        string publicationId,
        int expectedCount,
        int desiredCount)
    {
        if (index is null)
        {
            if (expectedCount != 0)
            {
                throw new PersistenceException("The pending shared behavior audience index is missing.");
            }

            index = NewAudienceIndex(audience);
        }

        if (index.PublicationIds.Count == expectedCount)
        {
            if (index.PublicationIds.Contains(publicationId, StringComparer.Ordinal))
            {
                throw new PersistenceException("The pending shared behavior audience index count is inconsistent.");
            }

            AddToAudienceIndex(index, publicationId);
        }
        else if (index.PublicationIds.Count != desiredCount
                 || !index.PublicationIds.Contains(publicationId, StringComparer.Ordinal))
        {
            throw new PersistenceException("The pending shared behavior audience index cannot be reconciled safely.");
        }

        return index;
    }

    private static AudienceManifestDocument NormalizePendingAudienceManifest(
        AudienceManifestDocument? manifest,
        GameSharedBehaviorAudience audience,
        int expectedCount,
        int desiredCount)
    {
        if (manifest is null)
        {
            if (expectedCount != 0)
            {
                throw new PersistenceException("The pending shared behavior audience manifest is missing.");
            }

            manifest = NewAudienceManifest(audience);
        }

        if (manifest.PublicationCount == expectedCount)
        {
            manifest.PublicationCount = desiredCount;
        }
        else if (manifest.PublicationCount != desiredCount)
        {
            throw new PersistenceException("The pending shared behavior audience manifest cannot be reconciled safely.");
        }

        return manifest;
    }

    private async ValueTask<CatalogIndexDocument> ReadIndexAsync(
        CancellationToken cancellationToken,
        bool allowMissingWithPublications = false)
    {
        EnsureRegularFileOrMissing(_indexPath);
        var document = await _files.ReadAtomicSnapshotAsync<CatalogIndexDocument>(
            _indexPath,
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            if (!allowMissingWithPublications && CountFiles() != 0)
            {
                throw new PersistenceException("The shared behavior catalog index is missing.");
            }

            return new CatalogIndexDocument { FormatVersion = 3 };
        }

        try
        {
            ValidateIndex(document);
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or FormatException
                                          or OverflowException)
        {
            throw new PersistenceException("The shared behavior catalog index contains invalid data.", exception);
        }

        return document;
    }

    private async ValueTask WriteIndexAsync(CatalogIndexDocument index, CancellationToken cancellationToken)
    {
        ValidateIndex(index);
        await _files.WriteAtomicAsync(_indexPath, index, cancellationToken).ConfigureAwait(false);
        EnsureRegularFile(_indexPath);
    }

    private void ValidateIndex(CatalogIndexDocument index)
    {
        if (index.FormatVersion != 3
            || index.EntryCount < 0
            || index.EntryCount > _maximumPublications)
        {
            throw new PersistenceException("The shared behavior catalog index is corrupt.");
        }
    }

    private async ValueTask<FamilyReservationDocument?> ReadFamilyReservationAsync(
        string behaviorFamilyId,
        int familyVersion,
        CancellationToken cancellationToken)
    {
        var path = FamilyReservationPath(behaviorFamilyId, familyVersion);
        EnsureRegularFileOrMissing(path);
        var document = await _files.ReadAtomicSnapshotAsync<FamilyReservationDocument>(
            path,
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        ValidateFamilyReservation(document, behaviorFamilyId, familyVersion);
        return document;
    }

    private async ValueTask WriteFamilyReservationAsync(
        FamilyReservationDocument reservation,
        CancellationToken cancellationToken)
    {
        ValidateFamilyReservation(
            reservation,
            reservation.BehaviorFamilyId,
            reservation.FamilyVersion);
        var path = FamilyReservationPath(reservation.BehaviorFamilyId, reservation.FamilyVersion);
        await _files.WriteAtomicAsync(path, reservation, cancellationToken).ConfigureAwait(false);
        EnsureRegularFile(path);
    }

    private static void ValidateFamilyReservation(
        FamilyReservationDocument reservation,
        string expectedFamilyId,
        int expectedFamilyVersion)
    {
        if (reservation.FormatVersion != 1
            || !string.Equals(reservation.BehaviorFamilyId, expectedFamilyId, StringComparison.Ordinal)
            || reservation.FamilyVersion != expectedFamilyVersion
            || reservation.FamilyVersion < 1
            || string.IsNullOrEmpty(reservation.ContentHash)
            || reservation.ContentHash.Length != 64
            || reservation.ContentHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new PersistenceException("The shared behavior family-version reservation is corrupt.");
        }

        _ = RequireStableId(reservation.BehaviorFamilyId, nameof(reservation.BehaviorFamilyId));
        _ = Require(reservation.PublicationId, 256, nameof(reservation.PublicationId));
    }

    private static bool ReservationMatches(
        FamilyReservationDocument reservation,
        GameSharedBehaviorPublication publication) =>
        string.Equals(reservation.PublicationId, publication.PublicationId, StringComparison.Ordinal)
        && string.Equals(reservation.BehaviorFamilyId, publication.BehaviorFamilyId, StringComparison.Ordinal)
        && reservation.FamilyVersion == publication.FamilyVersion
        && string.Equals(reservation.ContentHash, publication.Behavior.ContentHash, StringComparison.Ordinal);

    private async ValueTask<CatalogAudienceIndexDocument?> ReadAudienceIndexAsync(
        GameSharedBehaviorAudience audience,
        CancellationToken cancellationToken)
    {
        var path = AudienceIndexPath(audience);
        EnsureRegularFileOrMissing(path);
        var document = await _files.ReadAtomicSnapshotAsync<CatalogAudienceIndexDocument>(
            path,
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        ValidateAudienceIndex(document, audience);
        return document;
    }

    private async ValueTask<AudienceState> ReadAudienceStateForInsertAsync(
        GameSharedBehaviorAudience audience,
        CatalogIndexDocument catalog,
        CancellationToken cancellationToken)
    {
        var index = await ReadAudienceIndexAsync(audience, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadAudienceManifestAsync(audience, cancellationToken).ConfigureAwait(false);
        if (index is null && manifest is null)
        {
            return new AudienceState(NewAudienceIndex(audience), NewAudienceManifest(audience));
        }

        if (manifest is null)
        {
            throw new PersistenceException("The shared behavior audience manifest is missing.");
        }

        index ??= await RebuildAudienceIndexUnderLeaseAsync(
            audience,
            catalog,
            manifest,
            cancellationToken).ConfigureAwait(false);
        EnsureAudienceStateMatches(index, manifest);

        return new AudienceState(index, manifest);
    }

    private async ValueTask<CatalogAudienceIndexDocument> ReadAudienceIndexRecoveringAsync(
        GameSharedBehaviorAudience audience,
        CancellationToken cancellationToken)
    {
        var index = await ReadAudienceIndexAsync(audience, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadAudienceManifestAsync(audience, cancellationToken).ConfigureAwait(false);
        if (index is null && manifest is null)
        {
            return NewAudienceIndex(audience);
        }

        if (index is not null && manifest is not null && AudienceStateMatches(index, manifest))
        {
            return index;
        }

        EnsureRegularFileOrMissing(_files.PathFor(CapacityIdentity, ".lock"));
        await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var capacityLease = await _files.AcquireProcessLeaseAsync(
                CapacityIdentity,
                cancellationToken).ConfigureAwait(false);
            await RecoverPendingInsertAsync(cancellationToken).ConfigureAwait(false);
            index = await ReadAudienceIndexAsync(audience, cancellationToken).ConfigureAwait(false);
            manifest = await ReadAudienceManifestAsync(audience, cancellationToken).ConfigureAwait(false);
            if (index is null && manifest is null)
            {
                return NewAudienceIndex(audience);
            }

            if (manifest is null)
            {
                throw new PersistenceException("The shared behavior audience manifest is missing.");
            }

            if (index is not null)
            {
                EnsureAudienceStateMatches(index, manifest);
                return index;
            }

            var catalog = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            return await RebuildAudienceIndexUnderLeaseAsync(
                audience,
                catalog,
                manifest,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _capacityGate.Release();
        }
    }

    private async ValueTask<CatalogAudienceIndexDocument> RebuildAudienceIndexUnderLeaseAsync(
        GameSharedBehaviorAudience audience,
        CatalogIndexDocument catalog,
        AudienceManifestDocument manifest,
        CancellationToken cancellationToken)
    {
        var rebuilt = NewAudienceIndex(audience);
        var total = 0;
        foreach (var path in Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularFile(path);
            if (++total > _maximumPublications)
            {
                throw new PersistenceException("The shared behavior catalog exceeds its configured capacity.");
            }

            var document = await _files.ReadAtomicSnapshotAsync<PublicationDocument>(path, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new PersistenceException("A shared behavior publication disappeared during index recovery.");
            var publication = Decode(document);
            if (!string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(_files.PathFor(publication.PublicationId, Suffix)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new PersistenceException("A shared behavior publication is stored under the wrong key.");
            }

            var reservation = await ReadFamilyReservationAsync(
                publication.BehaviorFamilyId,
                publication.FamilyVersion,
                cancellationToken).ConfigureAwait(false);
            if (reservation is null || !ReservationMatches(reservation, publication))
            {
                throw new PersistenceException("A shared behavior publication has no valid family-version reservation.");
            }

            if (publication.Audience.Equals(audience))
            {
                AddToAudienceIndex(rebuilt, publication.PublicationId);
            }
        }

        if (total != catalog.EntryCount
            || rebuilt.PublicationIds.Count != manifest.PublicationCount
            || !string.Equals(manifest.PublicationIdsHash, AudienceIndexHash(rebuilt), StringComparison.Ordinal))
        {
            throw new PersistenceException("The shared behavior audience index cannot be rebuilt consistently.");
        }

        EnsureDocumentFits(rebuilt, "audience index");
        await WriteAudienceIndexAsync(rebuilt, cancellationToken).ConfigureAwait(false);
        return rebuilt;
    }

    private static void EnsureAudienceStateMatches(
        CatalogAudienceIndexDocument index,
        AudienceManifestDocument manifest)
    {
        if (!AudienceStateMatches(index, manifest))
        {
            throw new PersistenceException("The shared behavior audience index and manifest disagree.");
        }
    }

    private static bool AudienceStateMatches(
        CatalogAudienceIndexDocument index,
        AudienceManifestDocument manifest) =>
        index.Kind == manifest.Kind
        && string.Equals(index.AudienceId, manifest.AudienceId, StringComparison.Ordinal)
        && index.PublicationIds.Count == manifest.PublicationCount
        && string.Equals(manifest.PublicationIdsHash, AudienceIndexHash(index), StringComparison.Ordinal);

    private static CatalogAudienceIndexDocument NewAudienceIndex(GameSharedBehaviorAudience audience) => new()
    {
        FormatVersion = 1,
        Kind = audience.Kind,
        AudienceId = audience.AudienceId,
    };

    private async ValueTask WriteAudienceIndexAsync(
        CatalogAudienceIndexDocument index,
        CancellationToken cancellationToken)
    {
        var audience = new GameSharedBehaviorAudience(index.Kind, index.AudienceId);
        ValidateAudienceIndex(index, audience);
        var path = AudienceIndexPath(audience);
        await _files.WriteAtomicAsync(path, index, cancellationToken).ConfigureAwait(false);
        EnsureRegularFile(path);
    }

    private async ValueTask<AudienceManifestDocument?> ReadAudienceManifestAsync(
        GameSharedBehaviorAudience audience,
        CancellationToken cancellationToken)
    {
        var path = AudienceManifestPath(audience);
        EnsureRegularFileOrMissing(path);
        var document = await _files.ReadAtomicSnapshotAsync<AudienceManifestDocument>(
            path,
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        ValidateAudienceManifest(document, audience);
        return document;
    }

    private async ValueTask WriteAudienceManifestAsync(
        AudienceManifestDocument manifest,
        CancellationToken cancellationToken)
    {
        var audience = new GameSharedBehaviorAudience(manifest.Kind, manifest.AudienceId);
        ValidateAudienceManifest(manifest, audience);
        var path = AudienceManifestPath(audience);
        await _files.WriteAtomicAsync(path, manifest, cancellationToken).ConfigureAwait(false);
        EnsureRegularFile(path);
    }

    private void ValidateAudienceManifest(
        AudienceManifestDocument manifest,
        GameSharedBehaviorAudience expectedAudience)
    {
        if (manifest.FormatVersion != 1
            || !Enum.IsDefined(typeof(GameSharedBehaviorAudienceKind), manifest.Kind)
            || manifest.PublicationCount < 0
            || manifest.PublicationCount > _maximumPublications
            || manifest.PublicationIdsHash.Length != 64
            || manifest.PublicationIdsHash.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new PersistenceException("The shared behavior audience manifest is corrupt.");
        }

        var actualAudience = new GameSharedBehaviorAudience(manifest.Kind, manifest.AudienceId);
        if (!actualAudience.Equals(expectedAudience))
        {
            throw new PersistenceException("The shared behavior audience manifest does not match its storage key.");
        }
    }

    private void ValidateAudienceIndex(
        CatalogAudienceIndexDocument index,
        GameSharedBehaviorAudience expectedAudience)
    {
        if (index.FormatVersion != 1
            || !Enum.IsDefined(typeof(GameSharedBehaviorAudienceKind), index.Kind)
            || index.PublicationIds is null
            || index.PublicationIds.Count > _maximumPublications)
        {
            throw new PersistenceException("The shared behavior audience index is corrupt.");
        }

        var actualAudience = new GameSharedBehaviorAudience(index.Kind, index.AudienceId);
        if (!actualAudience.Equals(expectedAudience))
        {
            throw new PersistenceException("The shared behavior audience index does not match its storage key.");
        }

        string? previous = null;
        foreach (var publicationId in index.PublicationIds)
        {
            var validated = Require(publicationId, 256, nameof(publicationId));
            if (previous is not null && string.CompareOrdinal(previous, validated) >= 0)
            {
                throw new PersistenceException("The shared behavior audience index is not strictly ordered and unique.");
            }

            previous = validated;
        }
    }

    private static void AddToAudienceIndex(CatalogAudienceIndexDocument index, string publicationId)
    {
        if (index.PublicationIds.Contains(publicationId, StringComparer.Ordinal))
        {
            throw new PersistenceException("The shared behavior publication is already indexed.");
        }

        index.PublicationIds.Add(publicationId);
        index.PublicationIds.Sort(StringComparer.Ordinal);
    }

    private string AudienceIndexPath(GameSharedBehaviorAudience audience)
    {
        var name = "shared-behavior-audience-"
            + AudienceHash(audience)
            + ".index.json";
        return Path.Combine(_files.DirectoryPath, name);
    }

    private string AudienceManifestPath(GameSharedBehaviorAudience audience)
    {
        var name = "shared-behavior-audience-"
            + AudienceHash(audience)
            + ".manifest.json";
        return Path.Combine(_files.DirectoryPath, name);
    }

    private static AudienceManifestDocument NewAudienceManifest(GameSharedBehaviorAudience audience) => new()
    {
        FormatVersion = 1,
        Kind = audience.Kind,
        AudienceId = audience.AudienceId,
        PublicationIdsHash = AudienceIndexHash(NewAudienceIndex(audience)),
    };

    private static string AudienceIndexHash(CatalogAudienceIndexDocument index)
    {
        var payload = new StringBuilder("opengameagent.shared-behavior-audience.v1\0")
            .Append(AudienceKey(index.Kind, index.AudienceId))
            .Append('\0')
            .Append(index.PublicationIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var publicationId in index.PublicationIds)
        {
            payload.Append('\0').Append(publicationId);
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(payload.ToString()));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string AudienceHash(GameSharedBehaviorAudience audience)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(AudienceKey(audience)));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private string FamilyReservationPath(string behaviorFamilyId, int familyVersion)
    {
        var key = behaviorFamilyId + "\u0000" + familyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
        var name = "shared-behavior-family-"
            + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant()
            + ".reservation.json";
        return Path.Combine(_files.DirectoryPath, name);
    }

    private void EnsureDocumentFits<T>(T document, string kind)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(document).LongLength > _maximumFileBytes)
        {
            throw new PersistenceException($"The shared behavior {kind} exceeds the configured file size limit.");
        }
    }

    private static string AudienceKey(GameSharedBehaviorAudience audience) =>
        AudienceKey(audience.Kind, audience.AudienceId);

    private static string AudienceKey(GameSharedBehaviorAudienceKind kind, string audienceId) =>
        ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0000" + audienceId;

    private static PublicationDocument Encode(GameSharedBehaviorPublication publication) => new()
    {
        FormatVersion = 2,
        PublicationId = publication.PublicationId,
        BehaviorFamilyId = publication.BehaviorFamilyId,
        FamilyVersion = publication.FamilyVersion,
        Revision = publication.Revision,
        Status = publication.Status,
        AudienceKind = publication.Audience.Kind,
        AudienceId = publication.Audience.AudienceId,
        SourceBehaviorId = publication.Behavior.SourceBehaviorId,
        SourceBehaviorVersion = publication.Behavior.SourceBehaviorVersion,
        Title = publication.Behavior.Title,
        Instructions = publication.Behavior.Instructions,
        Observation = publication.Behavior.Reflection.Observation,
        Strategy = publication.Behavior.Reflection.Strategy,
        Outcome = publication.Behavior.Reflection.Outcome,
        Applicability = publication.Behavior.Reflection.Applicability,
        FailureModes = publication.Behavior.Reflection.FailureModes.ToList(),
        Steps = publication.Behavior.Steps.Select(value => new StepDocument
        {
            StepId = value.StepId,
            ToolName = value.ToolName,
            Instruction = value.Instruction,
        }).ToList(),
        InputTypes = publication.Behavior.InputTypes.ToList(),
        ToolNames = publication.Behavior.ToolNames.ToList(),
        ContentHash = publication.Behavior.ContentHash,
        SourceSessionId = publication.SourceSession.SessionId,
        SourceActorId = publication.SourceSession.ActorId,
        TimelineId = publication.TimelineId,
        WorldGeneration = publication.WorldGeneration,
        WorldRevision = publication.WorldRevision,
        AuditReference = publication.AuditReference,
        LastReason = publication.LastReason,
    };

    private static GameSharedBehaviorPublication Decode(PublicationDocument document) =>
        FileStore.DecodeDocument(
            "shared behavior publication",
            () =>
            {
                if (document.FormatVersion != 2)
                {
                    throw new PersistenceException("The shared behavior publication has an unsupported format.");
                }

                var behavior = new GameSharedBehaviorDefinition(
                    document.SourceBehaviorId,
                    document.SourceBehaviorVersion,
                    document.Title,
                    document.Instructions,
                    new GameBehaviorReflection(
                        document.Observation,
                        document.Strategy,
                        document.Outcome,
                        document.Applicability,
                        document.FailureModes ?? throw new PersistenceException("Behavior failure modes are missing.")),
                    (document.Steps ?? throw new PersistenceException("Behavior steps are missing."))
                    .Select(value => new GameBehaviorStep(value.StepId, value.ToolName, value.Instruction)),
                    document.InputTypes ?? throw new PersistenceException("Behavior input types are missing."),
                    document.ToolNames ?? throw new PersistenceException("Behavior tool names are missing."));
                if (!string.Equals(behavior.ContentHash, document.ContentHash, StringComparison.Ordinal))
                {
                    throw new PersistenceException("The shared behavior content hash does not match its payload.");
                }

                return new GameSharedBehaviorPublication(
                    document.PublicationId,
                    document.BehaviorFamilyId,
                    document.FamilyVersion,
                    document.Revision,
                    document.Status,
                    new GameSharedBehaviorAudience(document.AudienceKind, document.AudienceId),
                    behavior,
                    new GameSessionKey(document.SourceSessionId, document.SourceActorId),
                    document.TimelineId,
                    document.WorldGeneration,
                    document.WorldRevision,
                    document.AuditReference,
                    document.LastReason);
            });

    private static void EnsureDirectoryChainIsSafe(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PersistenceException("Shared behavior storage cannot use symbolic links or reparse points.");
            }

            current = current.Parent;
        }
    }

    private static void EnsureRegularFileOrMissing(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PersistenceException("Shared behavior storage expected a regular file or a missing path.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsureRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PersistenceException("Shared behavior storage expected a regular file.");
            }
        }
        catch (FileNotFoundException exception)
        {
            throw new PersistenceException("A shared behavior storage file disappeared during an operation.", exception);
        }
    }

    private static string Require(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"The value must contain 1 to {maximum} printable characters.", name)
            : value;

    private static string RequireStableId(string value, string name)
    {
        var result = Require(value, 128, name);
        if (result[0] is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
            || result.Any(character => character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("The shared behavior family ID is invalid.", name);
        }

        return result;
    }

    private sealed class PublicationDocument
    {
        public int FormatVersion { get; set; }
        public string PublicationId { get; set; } = string.Empty;
        public string BehaviorFamilyId { get; set; } = string.Empty;
        public int FamilyVersion { get; set; }
        public long Revision { get; set; }
        public GameSharedBehaviorPublicationStatus Status { get; set; }
        public GameSharedBehaviorAudienceKind AudienceKind { get; set; }
        public string AudienceId { get; set; } = string.Empty;
        public string SourceBehaviorId { get; set; } = string.Empty;
        public int SourceBehaviorVersion { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public string Observation { get; set; } = string.Empty;
        public string Strategy { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string Applicability { get; set; } = string.Empty;
        public List<string>? FailureModes { get; set; }
        public List<StepDocument>? Steps { get; set; }
        public List<string>? InputTypes { get; set; }
        public List<string>? ToolNames { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string SourceSessionId { get; set; } = string.Empty;
        public string SourceActorId { get; set; } = string.Empty;
        public string TimelineId { get; set; } = string.Empty;
        public string WorldGeneration { get; set; } = string.Empty;
        public long WorldRevision { get; set; }
        public string AuditReference { get; set; } = string.Empty;
        public string? LastReason { get; set; }
    }

    private sealed class CatalogIndexDocument
    {
        public int FormatVersion { get; set; }
        public int EntryCount { get; set; }
    }

    private sealed class CatalogAudienceIndexDocument
    {
        public int FormatVersion { get; set; }
        public GameSharedBehaviorAudienceKind Kind { get; set; }
        public string AudienceId { get; set; } = string.Empty;
        public List<string> PublicationIds { get; set; } = new();
    }

    private sealed class AudienceManifestDocument
    {
        public int FormatVersion { get; set; }
        public GameSharedBehaviorAudienceKind Kind { get; set; }
        public string AudienceId { get; set; } = string.Empty;
        public int PublicationCount { get; set; }
        public string PublicationIdsHash { get; set; } = string.Empty;
    }

    private sealed class AudienceState
    {
        public AudienceState(CatalogAudienceIndexDocument index, AudienceManifestDocument manifest)
        {
            Index = index;
            Manifest = manifest;
        }

        public CatalogAudienceIndexDocument Index { get; }
        public AudienceManifestDocument Manifest { get; }
    }

    private sealed class AudienceCursor
    {
        public AudienceCursor(
            CatalogAudienceIndexDocument index,
            GameSharedBehaviorAudience audience,
            int position)
        {
            Index = index;
            Audience = audience;
            Position = position;
        }

        public CatalogAudienceIndexDocument Index { get; }
        public GameSharedBehaviorAudience Audience { get; }
        public int Position { get; set; }
    }

    private sealed class PublicationCandidate
    {
        public PublicationCandidate(string publicationId, GameSharedBehaviorAudience audience)
        {
            PublicationId = publicationId;
            Audience = audience;
        }

        public string PublicationId { get; }
        public GameSharedBehaviorAudience Audience { get; }
    }

    private sealed class PendingInsertDocument
    {
        public int FormatVersion { get; set; }
        public string PublicationId { get; set; } = string.Empty;
        public GameSharedBehaviorAudienceKind AudienceKind { get; set; }
        public string AudienceId { get; set; } = string.Empty;
        public int ExpectedEntryCount { get; set; }
        public int ExpectedAudienceCount { get; set; }
        public string BehaviorFamilyId { get; set; } = string.Empty;
        public int FamilyVersion { get; set; }
        public string ContentHash { get; set; } = string.Empty;
    }

    private sealed class FamilyReservationDocument
    {
        public int FormatVersion { get; set; }
        public string BehaviorFamilyId { get; set; } = string.Empty;
        public int FamilyVersion { get; set; }
        public string PublicationId { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;

        public static FamilyReservationDocument For(GameSharedBehaviorPublication publication) => new()
        {
            FormatVersion = 1,
            BehaviorFamilyId = publication.BehaviorFamilyId,
            FamilyVersion = publication.FamilyVersion,
            PublicationId = publication.PublicationId,
            ContentHash = publication.Behavior.ContentHash,
        };
    }

    private sealed class StepDocument
    {
        public string StepId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string Instruction { get; set; } = string.Empty;
    }
}
