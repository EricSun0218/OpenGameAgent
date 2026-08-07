using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Models;

public sealed class GameStoredModelCatalog
{
    public GameStoredModelCatalog(
        string providerId,
        string catalogVersion,
        IReadOnlyList<GameModelDescriptor> models,
        DateTimeOffset checkedAt,
        long revision = 0)
    {
        ProviderId = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        CatalogVersion = GameModelDescriptor.RequireId(catalogVersion, nameof(catalogVersion));
        Models = GameModelProviderRegistration.ValidateModels(ProviderId, models);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (checkedAt == default)
        {
            throw new ArgumentException("A catalog check time is required.", nameof(checkedAt));
        }

        CheckedAt = checkedAt;
        Revision = revision;
    }

    public string ProviderId { get; }

    public string CatalogVersion { get; }

    public IReadOnlyList<GameModelDescriptor> Models { get; }

    public DateTimeOffset CheckedAt { get; }

    public long Revision { get; }
}

public enum GameModelCatalogSaveStatus
{
    Saved,
    Conflict,
}

public sealed class GameModelCatalogSaveResult
{
    public GameModelCatalogSaveResult(GameModelCatalogSaveStatus status, long revision)
    {
        if (!Enum.IsDefined(typeof(GameModelCatalogSaveStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Status = status;
        Revision = revision;
    }

    public GameModelCatalogSaveStatus Status { get; }

    public long Revision { get; }
}

public interface IGameModelCatalogStore
{
    ValueTask<GameStoredModelCatalog?> LoadAsync(string providerId, CancellationToken cancellationToken);

    ValueTask<GameModelCatalogSaveResult> SaveAsync(
        GameStoredModelCatalog catalog,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameModelCatalogStore : IGameModelCatalogStore
{
    private readonly Dictionary<string, GameStoredModelCatalog> _catalogs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _capacity;

    public InMemoryGameModelCatalogStore(int capacity = 128)
    {
        if (capacity <= 0 || capacity > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public async ValueTask<GameStoredModelCatalog?> LoadAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _catalogs.TryGetValue(id, out var catalog) ? catalog : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GameModelCatalogSaveResult> SaveAsync(
        GameStoredModelCatalog catalog,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentRevision = _catalogs.TryGetValue(catalog.ProviderId, out var current)
                ? current.Revision
                : 0;
            if (currentRevision != expectedRevision)
            {
                return new GameModelCatalogSaveResult(GameModelCatalogSaveStatus.Conflict, currentRevision);
            }

            if (current is null && _catalogs.Count >= _capacity)
            {
                throw new InvalidOperationException("The model catalog store reached its capacity.");
            }

            var nextRevision = checked(currentRevision + 1);
            _catalogs[catalog.ProviderId] = new GameStoredModelCatalog(
                catalog.ProviderId,
                catalog.CatalogVersion,
                catalog.Models,
                catalog.CheckedAt,
                nextRevision);
            return new GameModelCatalogSaveResult(GameModelCatalogSaveStatus.Saved, nextRevision);
        }
        finally
        {
            _gate.Release();
        }
    }
}
