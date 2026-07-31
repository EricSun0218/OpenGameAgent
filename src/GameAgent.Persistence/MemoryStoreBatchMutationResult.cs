using System.Collections.ObjectModel;
using GameAgent.Core;

namespace GameAgent.Persistence;

public sealed class MemoryStoreBatchMutationResult
{
    public MemoryStoreBatchMutationResult(
        long revision,
        IReadOnlyList<MemoryMutationResult> mutations)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (mutations is null)
        {
            throw new ArgumentNullException(nameof(mutations));
        }

        var count = mutations.Count;
        if (count is < 1 or > MemoryBatchLimits.MaxMutations)
        {
            throw new ArgumentOutOfRangeException(nameof(mutations));
        }

        var snapshot = new MemoryMutationResult[count];
        for (var index = 0; index < count; index++)
        {
            MemoryMutationResult? mutation;
            try
            {
                mutation = mutations[index];
            }
            catch (Exception exception)
                when (exception is ArgumentOutOfRangeException
                      or IndexOutOfRangeException)
            {
                throw new InvalidDataException(
                    "The memory mutation result collection changed while "
                    + "it was being snapshotted.",
                    exception);
            }

            snapshot[index] = mutation
                              ?? throw new ArgumentException(
                                  $"Memory mutation result {index} is null.",
                                  nameof(mutations));
        }

        Revision = revision;
        Mutations = new ReadOnlyCollection<MemoryMutationResult>(snapshot);
    }

    public long Revision { get; }

    public IReadOnlyList<MemoryMutationResult> Mutations { get; }

    public bool Changed => Mutations.Any(item => item.Changed);
}
