using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldP1RegressionTests
{
    private const string CatalogDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string PayloadDigest =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void InteractionTriggerRejectsOversizedInputAtInputBoundary()
    {
        var input = Json(
            "{\"text\":\""
            + new string('x', 70_000)
            + "\"}");

        var exception = Assert.Throws<RuntimeContentLimitException>(
            () => new InteractionRequestedTrigger(
                "request",
                "world",
                "timeline",
                1,
                new GameEntityIdentity("actor", 1),
                "input",
                input));

        Assert.Equal("input", exception.ParamName);
        Assert.Equal("json_bytes_exceeded", exception.LimitCode);
    }

    [Fact]
    public void NativeConditionDepthLimitRejectsSafelyAtExactBoundary()
    {
        var package = Compile(MinimalPackage());
        var context = new NativeWorldConditionEvaluationContext(
            package,
            package.World.InitialState,
            "turn",
            0);

        Assert.True(
            NativeWorldConditionEvaluator.Evaluate(
                NotChain(62),
                context));
        Assert.False(
            NativeWorldConditionEvaluator.Evaluate(
                NotChain(63),
                context));
        var exception = Assert.Throws<ArgumentException>(
            () => NativeWorldConditionEvaluator.Evaluate(
                NotChain(64),
                context));
        Assert.Equal("root", exception.ParamName);
    }

    [Fact]
    public async Task InMemoryEpochMismatchReceiptNeverExposesOtherEpoch()
    {
        var request = EpochMismatchRequest();
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            Snapshot());

        var first = await store.BeginAsync(request, default);
        var retry = await store.BeginAsync(request, default);
        var reconciled = await store.ReconcileAsync(
            request.ExpectedCoordinate.Scope,
            request.OperationId,
            request.RequestFingerprint,
            default);

        AssertRejectedEpochReceipt(first);
        AssertRejectedEpochReceipt(retry);
        Assert.Equal(
            WorldTransactionReconciliationStatus.TerminalReceipt,
            reconciled.Status);
        AssertRejectedEpochReceipt(reconciled.Receipt!);
        Assert.Equal(
            first.Receipt!.ReceiptId,
            reconciled.Receipt!.ReceiptId);
    }

    [Fact]
    public async Task FileEpochMismatchReceiptSurvivesRestartWithoutState()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var request = EpochMismatchRequest();
        var store = new FileWorldAuthoritativeTransactionStore(
            path,
            new[] { Snapshot() });

        var first = await store.BeginAsync(request, default);
        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var replay = await restarted.BeginAsync(request, default);
        var reconciled = await restarted.ReconcileAsync(
            request.ExpectedCoordinate.Scope,
            request.OperationId,
            request.RequestFingerprint,
            default);

        AssertRejectedEpochReceipt(first);
        AssertRejectedEpochReceipt(replay);
        Assert.Equal(
            WorldTransactionReconciliationStatus.TerminalReceipt,
            reconciled.Status);
        AssertRejectedEpochReceipt(reconciled.Receipt!);
        Assert.Equal(
            first.Receipt!.ReceiptId,
            reconciled.Receipt!.ReceiptId);
    }

    [Fact]
    public void InitialOversizeStoreLeavesNoTargetOrNextFile()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var state = Json(
            "{\"content\":\""
            + new string('x', 4_096)
            + "\"}");
        var snapshot = new WorldAuthoritativeStateSnapshot(
            Coordinate(timelineEpoch: 1),
            state);

        var exception = Assert.Throws<
            FileWorldAuthoritativeStoreException>(
            () => new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot },
                new FileWorldAuthoritativeTransactionStoreOptions(
                    maxFileBytes: 512)));

        Assert.Equal(
            FileWorldAuthoritativeStoreReasonCodes.ByteLimitExceeded,
            exception.ReasonCode);
        Assert.False(File.Exists(path));
        Assert.Empty(
            Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + "*.next"));
    }

    [Fact]
    public void LargeContentCatalogsCompileWithStableSemanticDigests()
    {
        var agentBody = new string('a', 300 * 1_024);
        var knowledgeBodyA = new string('k', 150 * 1_024);
        var knowledgeBodyB = new string('w', 150 * 1_024);
        var first = ContentPackage(
            agentBody,
            knowledgeBodyA,
            knowledgeBodyB,
            reordered: false);
        var reordered = ContentPackage(
            agentBody,
            knowledgeBodyA,
            knowledgeBodyB,
            reordered: true);

        Assert.True(
            first.Files.Single(file => file.Path == "agents.json").Length
            > 256 * 1_024);
        Assert.True(
            first.Files.Single(file => file.Path == "knowledge.json").Length
            > 256 * 1_024);
        Assert.NotEqual(first.PackageDigest, reordered.PackageDigest);

        var compiled = Compile(first);
        var compiledReordered = Compile(reordered);
        var agent = Assert.Single(compiled.Agents.Entries);
        var reorderedAgent =
            Assert.Single(compiledReordered.Agents.Entries);
        Assert.True(
            Encoding.UTF8.GetByteCount(agent.Data.GetRawText())
            > 256 * 1_024);
        Assert.Equal(agentBody.Length, agent.Data
            .GetProperty("body").GetString()!.Length);
        Assert.Equal(agent.Digest, reorderedAgent.Digest);
        Assert.Equal(
            compiled.Agents.Digest,
            compiledReordered.Agents.Digest);

        Assert.Equal(2, compiled.Knowledge.Entries.Count);
        Assert.Equal(
            compiled.Knowledge.Entries.Select(entry => entry.Digest),
            compiledReordered.Knowledge.Entries.Select(
                entry => entry.Digest));
        Assert.Equal(
            compiled.Knowledge.Digest,
            compiledReordered.Knowledge.Digest);
    }

    private static NativeWorldCondition NotChain(int count)
    {
        NativeWorldCondition result = new NativeWorldAlwaysCondition();
        for (var index = 0; index < count; index++)
        {
            result = new NativeWorldNotCondition(result);
        }

        return result;
    }

    private static WorldTransactionRequest EpochMismatchRequest()
    {
        return new WorldTransactionRequest(
            "operation",
            "command",
            PayloadDigest,
            Coordinate(timelineEpoch: 2));
    }

    private static WorldAuthoritativeStateSnapshot Snapshot()
    {
        return new WorldAuthoritativeStateSnapshot(
            Coordinate(timelineEpoch: 1),
            Json("""{"value":"before"}"""));
    }

    private static WorldAuthoritativeCoordinate Coordinate(
        long timelineEpoch)
    {
        return new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            timelineEpoch,
            0,
            0,
            CatalogDigest);
    }

    private static void AssertRejectedEpochReceipt(
        WorldTransactionBeginResult result)
    {
        Assert.Equal(
            WorldTransactionBeginStatus.TerminalReceipt,
            result.Status);
        AssertRejectedEpochReceipt(result.Receipt!);
    }

    private static void AssertRejectedEpochReceipt(
        WorldCommandReceipt receipt)
    {
        Assert.Equal(
            WorldCommandReceiptStatus.Rejected,
            receipt.Status);
        Assert.Equal(
            WorldTransactionReasonCodes.StaleCoordinate,
            receipt.OutcomeCode);
        Assert.Null(receipt.ResultingCoordinate);
        Assert.Null(receipt.ResultingStateDigest);
    }

    private static WorldPackageDefinition MinimalPackage()
    {
        return new WorldPackageDefinition(
            "world-package",
            "1",
            new[] { WorldFile() });
    }

    private static WorldPackageDefinition ContentPackage(
        string agentBody,
        string knowledgeBodyA,
        string knowledgeBodyB,
        bool reordered)
    {
        return new WorldPackageDefinition(
            "world-package",
            "1",
            new[]
            {
                WorldFile(),
                ContentCatalogFile(
                    "agents.json",
                    NativeWorldSemanticContractIds.AgentsV1,
                    "agents",
                    new[]
                    {
                        new ContentEntry("actor", "agent", agentBody)
                    },
                    reordered),
                ContentCatalogFile(
                    "knowledge.json",
                    NativeWorldSemanticContractIds.KnowledgeV1,
                    "knowledge",
                    new[]
                    {
                        new ContentEntry(
                            "setting-a",
                            "knowledge-a",
                            knowledgeBodyA),
                        new ContentEntry(
                            "setting-b",
                            "knowledge-b",
                            knowledgeBodyB)
                    },
                    reordered)
            });
    }

    private static WorldPackageFile WorldFile()
    {
        return JsonFile(
            "world.json",
            """
            {
              "contract": "game-agent.world-definition.v1",
              "worldId": "world",
              "defaultTimelineId": "timeline",
              "initialState": {}
            }
            """);
    }

    private static WorldPackageFile ContentCatalogFile(
        string path,
        string contract,
        string arrayProperty,
        IReadOnlyList<ContentEntry> entries,
        bool reordered)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            if (!reordered)
            {
                writer.WriteString("contract", contract);
            }

            writer.WritePropertyName(arrayProperty);
            writer.WriteStartArray();
            var ordered = reordered
                ? entries.Reverse()
                : entries;
            foreach (var entry in ordered)
            {
                WriteContentEntry(writer, entry, reordered);
            }

            writer.WriteEndArray();
            if (reordered)
            {
                writer.WriteString("contract", contract);
            }

            writer.WriteEndObject();
        }

        return new WorldPackageFile(
            path,
            "application/json",
            output.ToArray());
    }

    private static void WriteContentEntry(
        Utf8JsonWriter writer,
        ContentEntry entry,
        bool reordered)
    {
        writer.WriteStartObject();
        if (!reordered)
        {
            writer.WriteString("id", entry.Id);
            writer.WriteString("version", "1");
        }

        writer.WritePropertyName("data");
        writer.WriteStartObject();
        if (!reordered)
        {
            writer.WriteString("label", entry.Label);
        }

        writer.WriteString("body", entry.Body);
        if (reordered)
        {
            writer.WriteString("label", entry.Label);
        }

        writer.WriteEndObject();
        if (reordered)
        {
            writer.WriteString("version", "1");
            writer.WriteString("id", entry.Id);
        }

        writer.WriteEndObject();
    }

    private static ActivatedWorldPackage Compile(
        WorldPackageDefinition package)
    {
        var result = new NativeWorldPackageCompiler().Compile(package);
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(
                    diagnostic => diagnostic.Code
                                  + " "
                                  + diagnostic.Path
                                  + " "
                                  + diagnostic.Message)));
        return Assert.IsType<ActivatedWorldPackage>(result.Package);
    }

    private static WorldPackageFile JsonFile(
        string path,
        string value)
    {
        return new WorldPackageFile(
            path,
            "application/json",
            Encoding.UTF8.GetBytes(value));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class ContentEntry
    {
        public ContentEntry(string id, string label, string body)
        {
            Id = id;
            Label = label;
            Body = body;
        }

        public string Id { get; }

        public string Label { get; }

        public string Body { get; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "game-agent-world-p1-tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string File(string name)
        {
            return Path.Combine(_path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
