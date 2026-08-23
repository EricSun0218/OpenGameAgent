using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent;
using OpenGameAgent.Persistence;

const string targetSession = "target-session";
const string targetOwner = "target-owner";
var entryCount = ReadEntryCount(args);
var root = Path.Combine(Path.GetTempPath(), "OpenGameAgent.Memory.Benchmarks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    await SeedLegacyFilesAsync(root, entryCount);

    var baselineStartedAt = Stopwatch.GetTimestamp();
    var baselineMatches = await ScanLegacyDirectoryAsync(root, targetSession, targetOwner);
    var baseline = Elapsed(baselineStartedAt);

    var store = new FileGameMemoryStore(root, maximumEntries: Math.Max(entryCount, 100_000));
    var migrationStartedAt = Stopwatch.GetTimestamp();
    var migration = await store.MigrateLegacyLayoutAsync();
    var migrationDuration = Elapsed(migrationStartedAt);

    var query = new GameMemoryQuery(targetSession, 8, ownerId: targetOwner, text: "target");
    _ = await store.SearchSnapshotAsync(query, entryCount, CancellationToken.None);
    var samples = new List<double>();
    GameMemorySearchSnapshot? last = null;
    for (var sample = 0; sample < 7; sample++)
    {
        var startedAt = Stopwatch.GetTimestamp();
        last = await store.SearchSnapshotAsync(query, entryCount, CancellationToken.None);
        samples.Add(Elapsed(startedAt).TotalMilliseconds);
    }

    samples.Sort();
    var authoritative = last!.Stages.Single(stage => stage.Stage == GameMemorySearchStageKind.AuthoritativeSnapshot);
    if (baselineMatches != 8
        || migration.MigratedEntries != entryCount
        || migration.PartitionedEntries != entryCount
        || authoritative.ScannedCount != baselineMatches
        || last.AuthoritativeMemories.Count != baselineMatches
        || last.Memories.Count != baselineMatches)
    {
        throw new InvalidOperationException(
            "The partition benchmark detected a migration, isolation, or result-equivalence regression.");
    }

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            entries = entryCount,
            targetEntries = baselineMatches,
            baselineFullDirectoryScanMilliseconds = baseline.TotalMilliseconds,
            migrationMilliseconds = migrationDuration.TotalMilliseconds,
            migratedEntries = migration.MigratedEntries,
            partitionedEntries = migration.PartitionedEntries,
            partitionedHotQueryMedianMilliseconds = samples[samples.Count / 2],
            authoritativeScanned = authoritative.ScannedCount,
            resultCount = last.Memories.Count,
            operatingSystem = Environment.OSVersion.Platform.ToString(),
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        },
        new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OpenGameAgent.Memory.Benchmarks"));
    var target = Path.GetFullPath(root);
    if (!target.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Refusing to remove a directory outside the benchmark root.");
    }

    if (Directory.Exists(target))
    {
        Directory.Delete(target, recursive: true);
    }
}

static int ReadEntryCount(string[] arguments)
{
    if (arguments.Length == 0)
    {
        return 10_000;
    }

    if (arguments.Length != 2
        || !string.Equals(arguments[0], "--entries", StringComparison.Ordinal)
        || !int.TryParse(arguments[1], out var count)
        || count < 100
        || count > 100_000)
    {
        throw new ArgumentException("Usage: OpenGameAgent.Memory.Benchmarks [--entries 100..100000]");
    }

    return count;
}

async Task SeedLegacyFilesAsync(string directory, int count)
{
    for (var offset = 0; offset < count; offset += 128)
    {
        var batch = Enumerable.Range(offset, Math.Min(128, count - offset))
            .Select(index => WriteLegacyFileAsync(directory, index));
        await Task.WhenAll(batch);
    }
}

async Task WriteLegacyFileAsync(string directory, int index)
{
    var isTarget = index < 8;
    var sessionId = isTarget ? targetSession : "session-" + (index % 100).ToString();
    var ownerId = isTarget ? targetOwner : "owner-" + ((index / 100) % 100).ToString();
    var memoryId = "memory-" + index.ToString();
    var storageKey = sessionId + "\n" + ownerId + "\n" + memoryId;
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storageKey))).ToLowerInvariant();
    var document = new
    {
        FormatVersion = 1,
        MemoryId = memoryId,
        SessionId = sessionId,
        OwnerId = ownerId,
        Scope = "fact",
        Kind = nameof(GameMemoryKind.Fact),
        PayloadJson = isTarget ? "{\"target\":true}" : "{\"target\":false}",
        Moment = new { TimelineId = "world", Tick = (long)index, CalendarJson = (string?)null },
        Importance = 0.5,
        SearchableText = isTarget ? "target" : "unrelated",
        Tags = Array.Empty<string>(),
        SourceInputId = (string?)null,
        ExpiresAt = (object?)null,
        Metadata = new Dictionary<string, string>(),
    };
    await File.WriteAllTextAsync(
        Path.Combine(directory, hash + ".memory.json"),
        JsonSerializer.Serialize(document));
}

static async Task<int> ScanLegacyDirectoryAsync(string directory, string sessionId, string ownerId)
{
    var matches = 0;
    foreach (var path in Directory.EnumerateFiles(directory, "*.memory.json", SearchOption.TopDirectoryOnly))
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        if (string.Equals(root.GetProperty("SessionId").GetString(), sessionId, StringComparison.Ordinal)
            && string.Equals(root.GetProperty("OwnerId").GetString(), ownerId, StringComparison.Ordinal))
        {
            matches++;
        }
    }

    return matches;
}

static TimeSpan Elapsed(long startedAt) =>
    TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - startedAt) / Stopwatch.Frequency);
