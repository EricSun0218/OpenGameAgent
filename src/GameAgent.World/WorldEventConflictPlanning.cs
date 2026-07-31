using System.Collections.ObjectModel;

namespace GameAgent.World;

public sealed class WorldEventExecutionBatch
{
    internal WorldEventExecutionBatch(
        int batchIndex,
        IReadOnlyList<WorldEventInstance> instances)
    {
        BatchIndex = batchIndex;
        Instances = instances;
    }

    public int BatchIndex { get; }

    public IReadOnlyList<WorldEventInstance> Instances { get; }
}

/// <summary>
/// Places events into deterministic execution waves. Read/read overlap is
/// safe; write/write and read/write overlap are ordered into later waves.
/// </summary>
public static class WorldEventConflictBatchPlanner
{
    public static IReadOnlyList<WorldEventExecutionBatch> Plan(
        IReadOnlyList<WorldEventInstance> instances,
        int maximumBatchSize = 256,
        int maximumBatches = 4_096)
    {
        if (instances is null)
        {
            throw new ArgumentNullException(nameof(instances));
        }

        if (maximumBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));
        }

        if (maximumBatches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatches));
        }

        var maximumInstances = (int)Math.Min(
            1_000_000L,
            (long)maximumBatchSize * maximumBatches);
        var ordered = WorldValidation.MaterializeBounded(
                instances,
                maximumInstances,
                nameof(instances))
            .Select(
                instance => instance
                            ?? throw new ArgumentException(
                                "Instances cannot contain null entries.",
                                nameof(instances)))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenBy(item => item.DefinitionVersion, StringComparer.Ordinal)
            .ThenBy(item => item.ResolutionKey, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (ordered.Any(item => !seen.Add(item.InstanceId)))
        {
            throw new ArgumentException(
                "Instances must have unique identifiers.",
                nameof(instances));
        }

        var batches = new List<List<WorldEventInstance>>();
        var lastReaderBatch = new Dictionary<string, int>(
            StringComparer.Ordinal);
        var lastWriterBatch = new Dictionary<string, int>(
            StringComparer.Ordinal);
        foreach (var instance in ordered)
        {
            var minimumBatch = 0;
            foreach (var resource in instance.ReadResourceKeys)
            {
                if (lastWriterBatch.TryGetValue(resource, out var batch))
                {
                    minimumBatch = Math.Max(
                        minimumBatch,
                        batch + 1);
                }
            }

            foreach (var resource in instance.WriteResourceKeys)
            {
                if (lastWriterBatch.TryGetValue(resource, out var writerBatch))
                {
                    minimumBatch = Math.Max(
                        minimumBatch,
                        writerBatch + 1);
                }

                if (lastReaderBatch.TryGetValue(resource, out var readerBatch))
                {
                    minimumBatch = Math.Max(
                        minimumBatch,
                        readerBatch + 1);
                }
            }

            var batchIndex = minimumBatch;
            while (batchIndex < batches.Count
                   && batches[batchIndex].Count >= maximumBatchSize)
            {
                batchIndex++;
            }

            if (batchIndex >= maximumBatches)
            {
                throw new WorldEvolutionLimitException(
                    WorldEvolutionReasonCodes.BatchLimitExceeded,
                    "Conflict planning exceeds its execution-batch limit.");
            }

            while (batches.Count <= batchIndex)
            {
                batches.Add(new List<WorldEventInstance>());
            }

            batches[batchIndex].Add(instance);
            foreach (var resource in instance.ReadResourceKeys)
            {
                if (!lastReaderBatch.TryGetValue(
                        resource,
                        out var existing)
                    || batchIndex > existing)
                {
                    lastReaderBatch[resource] = batchIndex;
                }
            }

            foreach (var resource in instance.WriteResourceKeys)
            {
                lastWriterBatch[resource] = batchIndex;
            }
        }

        return new ReadOnlyCollection<WorldEventExecutionBatch>(
            batches
                .Select(
                    (batch, index) =>
                        new WorldEventExecutionBatch(
                            index,
                            new ReadOnlyCollection<WorldEventInstance>(
                                batch.ToArray())))
                .ToArray());
    }

    public static bool HasConflict(
        WorldEventInstance left,
        WorldEventInstance right)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        var leftReads = new HashSet<string>(
            left.ReadResourceKeys,
            StringComparer.Ordinal);
        var leftWrites = new HashSet<string>(
            left.WriteResourceKeys,
            StringComparer.Ordinal);
        var rightReads = new HashSet<string>(
            right.ReadResourceKeys,
            StringComparer.Ordinal);
        var rightWrites = new HashSet<string>(
            right.WriteResourceKeys,
            StringComparer.Ordinal);
        return leftWrites.Overlaps(rightWrites)
               || leftWrites.Overlaps(rightReads)
               || leftReads.Overlaps(rightWrites);
    }
}
