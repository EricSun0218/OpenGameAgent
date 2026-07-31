using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class NativeWorldSaveBridgeTests
{
    private const string MetadataKey =
        "game-agent.native-world-save-bridge.v1";
    private const string ScheduleMetadataKey =
        "game-agent.native-world-schedules.v1";

    [Fact]
    public async Task LiveCaptureRestoresNewFileAndContinuesWithParity()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var source = NativeWorldRuntime.CreateInMemory(package);
        await ExecuteInteractionAndAdvanceAsync(source, package);
        var bridge = new NativeWorldSaveBridge();

        var save = await bridge.CaptureAsync(source);
        var restored = await bridge.RestoreFileAsync(
            package,
            save,
            directory.StorePath);
        AssertSnapshotParity(
            await source.ReadSnapshotAsync(),
            await restored.ReadSnapshotAsync());

        await ContinueAsync(source);
        await ContinueAsync(restored);
        AssertSnapshotParity(
            await source.ReadSnapshotAsync(),
            await restored.ReadSnapshotAsync());
        var sourceSave = await bridge.CaptureAsync(source);
        var restoredSave = await bridge.CaptureAsync(restored);

        Assert.Equal(
            WorldSaveCodec.Write(sourceSave),
            WorldSaveCodec.Write(restoredSave));
        Assert.True(File.Exists(directory.StorePath));
    }

    [Fact]
    public async Task WrongPackageOrCatalogFailsBeforeTargetPublication()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(
            NativeWorldRuntime.CreateInMemory(package));
        var changedPackage = Compile(Package(interactionReward: "2"));

        var packageFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreFileAsync(
                    changedPackage,
                    save,
                    directory.StorePath));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.BindingMismatch,
            packageFailure.ReasonCode);
        Assert.False(File.Exists(directory.StorePath));

        var wrongCatalog = RewriteMetadata(
            save,
            "catalogDigest",
            new string('0', 64));
        var catalogFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreFileAsync(
                    package,
                    wrongCatalog,
                    directory.StorePath));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.BindingMismatch,
            catalogFailure.ReasonCode);
        Assert.False(File.Exists(directory.StorePath));
    }

    [Fact]
    public async Task TornCorruptAndIncompleteArtifactsFailClosed()
    {
        var package = Compile(Package());
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(
            NativeWorldRuntime.CreateInMemory(package));
        var bytes = WorldSaveCodec.Write(save);
        var torn = bytes.Take(bytes.Length - 7).ToArray();
        var corrupt = (byte[])bytes.Clone();
        corrupt[0] = (byte)'!';

        Assert.Throws<WorldDataContractException>(
            () => WorldSaveCodec.Read(torn));
        Assert.Throws<WorldDataContractException>(
            () => WorldSaveCodec.Read(corrupt));

        var wrongDigest = RewriteMetadata(
            save,
            "recordDigest",
            new string('f', 64));
        var digestFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreInMemoryAsync(
                    package,
                    wrongDigest));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
            digestFailure.ReasonCode);

        var incomplete = RewriteMetadata(
            save,
            "historyCompleteness",
            "partial");
        var completenessFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreInMemoryAsync(
                    package,
                    incomplete));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.IncompleteHistory,
            completenessFailure.ReasonCode);
    }

    [Fact]
    public async Task SettledCaptureRejectsPendingOwnership()
    {
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        var snapshot = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
        var request = new WorldTransactionRequest(
            "pending-operation",
            "pending-command",
            CanonicalJsonDigest.ComputeSha256(Json("""{}""")),
            snapshot.Coordinate);
        var pending = await runtime.TransactionStore.BeginAsync(
            request,
            CancellationToken.None);
        Assert.Equal(
            WorldTransactionBeginStatus.Acquired,
            pending.Status);

        var exception =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await new NativeWorldSaveBridge()
                    .CaptureAsync(runtime));

        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.PendingTransactions,
            exception.ReasonCode);
        await pending.Transaction!.DisposeAsync();
    }

    [Fact]
    public async Task FailedSeedAndExistingTargetRemainUnmodified()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        await ExecuteInteractionAndAdvanceAsync(runtime, package);
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(runtime);
        var tooSmall =
            new FileWorldAuthoritativeTransactionStoreOptions(
                maxOperations: 1);

        await Assert.ThrowsAsync<FileWorldAuthoritativeStoreException>(
            async () => await bridge.RestoreFileAsync(
                package,
                save,
                directory.StorePath,
                tooSmall));
        Assert.False(File.Exists(directory.StorePath));

        var sentinel = Encoding.UTF8.GetBytes("existing-store");
        File.WriteAllBytes(directory.StorePath, sentinel);
        var existing =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreFileAsync(
                    package,
                    save,
                    directory.StorePath));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.TargetExists,
            existing.ReasonCode);
        Assert.Equal(
            sentinel,
            File.ReadAllBytes(directory.StorePath));
    }

    [Fact]
    public async Task RestoreReclaimsBoundedAbandonedSeedArtifacts()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var source = NativeWorldRuntime.CreateInMemory(package);
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(source);
        var seedPath = directory.StorePath + ".seed";
        var seedLockPath = seedPath + ".lock";
        var seedNextPath = seedPath + ".next";
        File.WriteAllText(
            seedPath,
            "abandoned-seed",
            new UTF8Encoding(false));
        File.WriteAllText(
            seedLockPath,
            "abandoned-lock",
            new UTF8Encoding(false));
        File.WriteAllText(
            seedNextPath,
            "abandoned-next",
            new UTF8Encoding(false));

        var restored = await bridge.RestoreFileAsync(
            package,
            save,
            directory.StorePath);

        Assert.NotNull(await restored.ReadSnapshotAsync());
        Assert.True(File.Exists(directory.StorePath));
        Assert.False(File.Exists(seedPath));
        Assert.False(File.Exists(seedLockPath));
        Assert.False(File.Exists(seedNextPath));
    }

    [Fact]
    public async Task ConcurrentRestorePublishesTargetOnce()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(
            NativeWorldRuntime.CreateInMemory(package));
        var attempts = Enumerable.Range(0, 2)
            .Select(
                index => Task.Run(
                    async () =>
                    {
                        try
                        {
                            var restored =
                                await bridge.RestoreFileAsync(
                                package,
                                save,
                                directory.StorePath);
                            Assert.NotNull(restored);
                            return "published";
                        }
                        catch (NativeWorldSaveBridgeException exception)
                        {
                            return exception.ReasonCode;
                        }
                    }))
            .ToArray();

        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(
            outcomes,
            outcome => string.Equals(
                outcome,
                "published",
                StringComparison.Ordinal));
        Assert.Single(
            outcomes,
            outcome => string.Equals(
                outcome,
                NativeWorldSaveBridgeReasonCodes.TargetExists,
                StringComparison.Ordinal));
        Assert.True(File.Exists(directory.StorePath));
    }

    [Fact]
    public async Task ForkDerivesIdentityAndIsolatesAbandonedFuture()
    {
        var package = Compile(Package());
        var source = NativeWorldRuntime.CreateInMemory(package);
        await ExecuteInteractionAndAdvanceAsync(source, package);
        var bridge = new NativeWorldSaveBridge();
        var parent = await bridge.CaptureAsync(source);
        await ContinueAsync(source);
        var abandonedFuture = Assert.IsType<
            WorldAuthoritativeStateSnapshot>(
            await source.ReadSnapshotAsync());

        var fork = await bridge.ForkAsync(
            package,
            parent,
            "alternate");
        var secondFork = await bridge.ForkAsync(
            package,
            parent,
            "alternate");
        Assert.Equal(
            WorldSaveCodec.Write(fork),
            WorldSaveCodec.Write(secondFork));
        Assert.Equal(parent.TimelineId, fork.ParentTimelineId);
        Assert.Equal(parent.SaveRevision, fork.ParentSaveRevision);
        Assert.Equal("alternate", fork.TimelineId);
        Assert.Equal(0, fork.SaveRevision);
        Assert.Equal("0", fork.StateVersion);
        Assert.All(
            fork.Clocks,
            item => Assert.Equal(1, item.Epoch));

        var forkRuntime = await bridge.RestoreInMemoryAsync(
            package,
            fork);
        var forkBefore = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await forkRuntime.ReadSnapshotAsync());
        Assert.Equal(parent.State.GetRawText(), forkBefore.State.GetRawText());
        Assert.NotEqual(
            abandonedFuture.StateDigest,
            forkBefore.StateDigest);
        Assert.Equal("alternate", forkBefore.Coordinate.TimelineId);
        Assert.Equal(1, forkBefore.Coordinate.TimelineEpoch);

        var planned = await forkRuntime.PlanInteractionAsync(
            Execution(
                forkBefore,
                package.CatalogDigest));
        Assert.True(planned.Succeeded);
        var execution = await forkRuntime.ExecuteInteractionAsync(
            planned.Value!);
        Assert.True(execution.Value!.Succeeded);
        var sourceAfterFork = Assert.IsType<
            WorldAuthoritativeStateSnapshot>(
            await source.ReadSnapshotAsync());
        Assert.Equal(
            abandonedFuture.StateDigest,
            sourceAfterFork.StateDigest);
    }

    [Fact]
    public async Task IssuedIncarnationsRoundTripThroughSaveFileAndFork()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        var initial = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
        var upgraded = await MutateIncarnationsAsync(
            runtime,
            initial,
            "upgrade-ledger",
            draft => draft.SetIncarnation("actor", 3));
        Assert.True(upgraded.WasIncarnationIssued("actor", 1));
        Assert.False(upgraded.WasIncarnationIssued("actor", 2));
        Assert.True(upgraded.WasIncarnationIssued("actor", 3));

        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(runtime);
        var restored = await bridge.RestoreInMemoryAsync(package, save);
        var restoredFile = await bridge.RestoreFileAsync(
            package,
            save,
            directory.StorePath);
        var forkSave = await bridge.ForkAsync(
            package,
            save,
            "ledger-fork");
        var fork = await bridge.RestoreInMemoryAsync(
            package,
            forkSave);

        foreach (var candidate in new[]
                 {
                     await restored.ReadSnapshotAsync(),
                     await restoredFile.ReadSnapshotAsync(),
                     await fork.ReadSnapshotAsync()
                 })
        {
            var snapshot = Assert.IsType<
                WorldAuthoritativeStateSnapshot>(candidate);
            Assert.Equal(3, snapshot.EntityIncarnations["actor"]);
            Assert.True(snapshot.WasIncarnationIssued("actor", 1));
            Assert.False(snapshot.WasIncarnationIssued("actor", 2));
            Assert.True(snapshot.WasIncarnationIssued("actor", 3));
        }

        var bounded = new NativeWorldSaveBridgeOptions(
            maxIssuedEntityIncarnations: 2);
        var capacity =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.CaptureAsync(
                    runtime,
                    bounded));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
            capacity.ReasonCode);
    }

    [Fact]
    public async Task MaximumIssuedLedgerFitsDefaultSaveNodeAndByteLimits()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var declared = Assert.IsType<
            WorldAuthoritativeStateSnapshot>(
            await NativeWorldRuntime.CreateInMemory(package)
                .ReadSnapshotAsync());
        var records = Enumerable.Range(
                0,
                WorldAuthoritativeStateSnapshot
                    .MaximumIssuedIncarnationCount)
            .Select(
                index => new WorldIssuedEntityIncarnation(
                    WorstCaseMaximumLengthEntityId(index),
                    long.MaxValue))
            .ToArray();
        Assert.All(
            new[] { records[0], records[^1] },
            item => Assert.Equal(
                WorldValidation.MaximumIdentifierUtf8Bytes,
                Encoding.UTF8.GetByteCount(item.EntityId)));
        var current = records
            .Take(WorldValidation.MaximumParticipants - 1)
            .Append(records[^1])
            .ToDictionary(
                item => item.EntityId,
                item => item.Incarnation,
                StringComparer.Ordinal);
        var snapshot = new WorldAuthoritativeStateSnapshot(
            declared.Coordinate,
            declared.State,
            current,
            issuedEntityIncarnations: records);
        var runtime = NativeWorldRuntime.CreateInMemoryFromSnapshot(
            package,
            snapshot);
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(runtime);
        var defaults = new WorldPackageLimits();
        var bytes = WorldSaveCodec.Write(save, defaults);

        Assert.InRange(
            bytes.LongLength,
            1,
            defaults.MaxFileBytes);
        var admitted = WorldSaveCodec.Read(bytes, defaults);
        var ledgerRecord = Assert.Single(
            admitted.EventLog.EnumerateArray(),
            item => string.Equals(
                item.GetProperty("kind").GetString(),
                "packedIncarnationLedger",
                StringComparison.Ordinal));
        Assert.Equal(
            "packedIncarnationLedger",
            ledgerRecord.GetProperty("kind").GetString());
        Assert.Equal(
            "base85-v1",
            ledgerRecord.GetProperty("encoding").GetString());
        var packedByteLength = int.Parse(
            ledgerRecord.GetProperty("byteLength").GetString()!,
            CultureInfo.InvariantCulture);
        Assert.Equal(13_180_942, packedByteLength);
        var encodedCharacters = ledgerRecord.GetProperty("chunks")
            .EnumerateArray()
            .Sum(chunk => chunk.GetString()!.Length);
        Assert.Equal(
            checked(((packedByteLength + 3) / 4) * 5),
            encodedCharacters);
        Assert.Equal(
            4,
            ledgerRecord.GetProperty("chunks").GetArrayLength());
        Assert.All(
            ledgerRecord.GetProperty("chunks").EnumerateArray(),
            chunk => Assert.InRange(
                Encoding.UTF8.GetByteCount(chunk.GetString()!),
                1,
                defaults.MaxJsonStringUtf8Bytes));
        Assert.True(
            defaults.MaxFileBytes - bytes.LongLength > 250_000);
        Assert.True(
            CountJsonNodes(admitted.EventLog)
            <= defaults.MaxJsonNodes);
        Assert.InRange(
            JsonValueInspector.ValidateAndMeasure(
                admitted.EventLog,
                new JsonValueLimits(
                    maxUtf8Bytes: checked((int)defaults.MaxFileBytes),
                    maxDepth: defaults.MaxJsonDepth,
                    maxNodes: defaults.MaxJsonNodes,
                    maxStringUtf8Bytes:
                    defaults.MaxJsonStringUtf8Bytes,
                    maxContainerItems:
                    defaults.MaxJsonContainerItems),
                "eventLog"),
            1,
            checked((int)defaults.MaxFileBytes));

        var memory = await bridge.RestoreInMemoryAsync(
            package,
            admitted);
        var file = await bridge.RestoreFileAsync(
            package,
            admitted,
            directory.StorePath);
        var forkSave = await bridge.ForkAsync(
            package,
            admitted,
            "maximum-ledger-fork");
        var fork = await bridge.RestoreInMemoryAsync(
            package,
            forkSave);
        foreach (var candidate in new[]
                 {
                     await memory.ReadSnapshotAsync(),
                     await file.ReadSnapshotAsync(),
                     await fork.ReadSnapshotAsync()
                 })
        {
            var restored = Assert.IsType<
                WorldAuthoritativeStateSnapshot>(candidate);
            Assert.Equal(
                records.Length,
                restored.IssuedEntityIncarnations.Count);
            Assert.Equal(
                current.Count,
                restored.EntityIncarnations.Count);
            Assert.Equal(
                long.MaxValue,
                restored.EntityIncarnations[
                    records[0].EntityId]);
            Assert.Equal(
                long.MaxValue,
                restored.EntityIncarnations[
                    records[^1].EntityId]);
            Assert.True(
                restored.WasIncarnationIssued(
                    records[0].EntityId,
                    long.MaxValue));
            Assert.True(
                restored.WasIncarnationIssued(
                    records[^1].EntityId,
                    long.MaxValue));
        }
    }

    [Fact]
    public async Task PackedLedgerRejectsInvalidBase85WithoutTrustingMetadata()
    {
        var package = Compile(Package());
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(
            NativeWorldRuntime.CreateInMemory(package));
        var corrupt = RewritePackedLedgerChunk(
            save,
            chunk => "\"" + chunk[1..]);

        var failure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreInMemoryAsync(
                    package,
                    corrupt));

        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
            failure.ReasonCode);
    }

    [Fact]
    public void PackedLedgerAlphabetIsUniqueAndNeverJsonEscaped()
    {
        var alphabet =
            NativeWorldIncarnationLedgerCodec.Base85Alphabet;
        Assert.Equal(85, alphabet.Length);
        Assert.Equal(
            alphabet.Length,
            alphabet.Distinct().Count());

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStringValue(alphabet);
        }

        Assert.Equal(
            "\"" + alphabet + "\"",
            Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void PackedLedgerRejectsImpossibleLengthsAndHeaderCounts()
    {
        Assert.Throws<InvalidDataException>(
            () => NativeWorldIncarnationLedgerCodec.Decode(
                new[] { "!!!!!" },
                int.MaxValue,
                WorldAuthoritativeStateSnapshot
                    .MaximumIssuedIncarnationCount,
                WorldValidation.MaximumParticipants));

        var raw = EmptyPackedLedgerRaw();
        BinaryPrimitives.WriteUInt32LittleEndian(
            raw.AsSpan(8, sizeof(uint)),
            uint.MaxValue);
        var chunks = EncodeBase85ForTest(raw);
        Assert.Throws<InvalidDataException>(
            () => NativeWorldIncarnationLedgerCodec.Decode(
                chunks,
                raw.Length,
                WorldAuthoritativeStateSnapshot
                    .MaximumIssuedIncarnationCount,
                WorldValidation.MaximumParticipants));
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("nonzero-padding")]
    [InlineData("noncanonical-chunk-split")]
    [InlineData("trailing-data")]
    public void PackedLedgerRejectsNonCanonicalBase85Forms(
        string corruption)
    {
        var raw = EmptyPackedLedgerRaw();
        IReadOnlyList<string> chunks;
        int byteLength;
        switch (corruption)
        {
            case "overflow":
                chunks =
                [
                    new string(
                        NativeWorldIncarnationLedgerCodec
                            .Base85Alphabet[^1],
                        5)
                    + new string(
                        NativeWorldIncarnationLedgerCodec
                            .Base85Alphabet[0],
                        15)
                ];
                byteLength = raw.Length;
                break;
            case "nonzero-padding":
                var padded = new byte[16];
                raw.CopyTo(padded, 0);
                padded[raw.Length] = 1;
                chunks = EncodeBase85ForTest(padded);
                byteLength = raw.Length;
                break;
            case "noncanonical-chunk-split":
                var encoded = Assert.Single(
                    EncodeBase85ForTest(raw));
                chunks = [encoded[..5], encoded[5..]];
                byteLength = raw.Length;
                break;
            case "trailing-data":
                var trailing = new byte[raw.Length + 1];
                raw.CopyTo(trailing, 0);
                trailing[^1] = 1;
                chunks = EncodeBase85ForTest(trailing);
                byteLength = trailing.Length;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(corruption));
        }

        Assert.Throws<InvalidDataException>(
            () => NativeWorldIncarnationLedgerCodec.Decode(
                chunks,
                byteLength,
                WorldAuthoritativeStateSnapshot
                    .MaximumIssuedIncarnationCount,
                WorldValidation.MaximumParticipants));
    }

    [Fact]
    public void PackedLedgerEncodeEnforcesFormatCapacityBeforeWriting()
    {
        var issued = Enumerable.Range(
                0,
                WorldValidation.MaximumParticipants + 1)
            .Select(
                index => new WorldIssuedEntityIncarnation(
                    "entity-" + index,
                    1))
            .ToArray();
        var current = issued.ToDictionary(
            item => item.EntityId,
            item => item.Incarnation,
            StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(
            () => NativeWorldIncarnationLedgerCodec.Encode(
                current,
                issued,
                out _));
    }

    [Fact]
    public async Task LegacyCurrentOnlySaveSeedsLedgerAndDuplicateLedgerFailsClosed()
    {
        var package = Compile(Package());
        var bridge = new NativeWorldSaveBridge();
        var current = await bridge.CaptureAsync(
            NativeWorldRuntime.CreateInMemory(package));

        var legacy = ToLegacyCurrentOnlySave(current);
        var restored = await bridge.RestoreInMemoryAsync(
            package,
            legacy);
        var snapshot = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await restored.ReadSnapshotAsync());
        Assert.True(snapshot.WasIncarnationIssued("actor", 1));
        Assert.True(snapshot.WasIncarnationIssued("target", 1));

        var parallelForm =
            ToParallelIssuedIncarnationSave(current);
        var parallelRestored = await bridge.RestoreInMemoryAsync(
            package,
            parallelForm);
        var parallelSnapshot =
            Assert.IsType<WorldAuthoritativeStateSnapshot>(
                await parallelRestored.ReadSnapshotAsync());
        Assert.True(
            parallelSnapshot.WasIncarnationIssued("actor", 1));
        Assert.True(
            parallelSnapshot.WasIncarnationIssued("target", 1));

        var objectForm = ToObjectIssuedIncarnationSave(current);
        var objectRestored = await bridge.RestoreInMemoryAsync(
            package,
            objectForm);
        var objectSnapshot =
            Assert.IsType<WorldAuthoritativeStateSnapshot>(
                await objectRestored.ReadSnapshotAsync());
        Assert.True(objectSnapshot.WasIncarnationIssued("actor", 1));
        Assert.True(objectSnapshot.WasIncarnationIssued("target", 1));

        var duplicate = DuplicateIssuedIncarnationRecord(current);
        var corrupt =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreInMemoryAsync(
                    package,
                    duplicate));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
            corrupt.ReasonCode);
    }

    [Fact]
    public async Task DeterministicBytesAndCancellationAreStable()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        await ExecuteInteractionAndAdvanceAsync(runtime, package);
        var bridge = new NativeWorldSaveBridge();
        var first = await bridge.CaptureAsync(runtime);
        var second = await bridge.CaptureAsync(runtime);

        Assert.Equal(
            WorldSaveCodec.Write(first),
            WorldSaveCodec.Write(second));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await bridge.CaptureAsync(
                runtime,
                cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await bridge.RestoreFileAsync(
                package,
                first,
                directory.StorePath,
                cancellationToken: cancellation.Token));
        Assert.False(File.Exists(directory.StorePath));
    }

    [Fact]
    public async Task BridgeBoundsRejectCaptureAndAdmission()
    {
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        await ExecuteInteractionAndAdvanceAsync(runtime, package);
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(runtime);
        var bounded = new NativeWorldSaveBridgeOptions(
            maxTransactionRecords: 1);

        var captureFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.CaptureAsync(
                    runtime,
                    bounded));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
            captureFailure.ReasonCode);

        var admissionFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreInMemoryAsync(
                    package,
                    save,
                    bridgeOptions: bounded));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
            admissionFailure.ReasonCode);
    }

    [Fact]
    public async Task SchedulesRoundTripWithSameInFlightOccurrence()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        var scope = new WorldTransactionScope(
            runtime.Address.WorldId,
            runtime.Address.TimelineId,
            runtime.TimelineEpoch);
        var created = await runtime.ExecuteScheduleAsync(
            WorldScheduleCommand.Create(
                "create-schedule",
                ScheduleIntent(scope, "long-intent", dueTick: 0)));
        Assert.True(created.Applied);
        var claimCommand = WorldScheduleCommand.Claim(
            "claim-schedule",
            scope,
            "long-intent",
            expectedGeneration: 0,
            new GameTimePoint("turn", "main", 0, 0),
            "worker");
        var claimed = await runtime.ExecuteScheduleAsync(
            claimCommand);
        Assert.True(claimed.Applied);

        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(runtime);
        var memory = await bridge.RestoreInMemoryAsync(
            package,
            save);
        var file = await bridge.RestoreFileAsync(
            package,
            save,
            directory.StorePath);
        var sourceSchedule = await runtime.FindScheduleAsync(
            "long-intent");
        var memorySchedule = await memory.FindScheduleAsync(
            "long-intent");
        var fileSchedule = await file.FindScheduleAsync(
            "long-intent");
        Assert.NotNull(sourceSchedule);
        Assert.NotNull(memorySchedule);
        Assert.NotNull(fileSchedule);
        Assert.Equal(
            sourceSchedule!.RecordDigest,
            memorySchedule!.RecordDigest);
        Assert.Equal(
            sourceSchedule.RecordDigest,
            fileSchedule!.RecordDigest);
        Assert.Equal(
            sourceSchedule.OccurrenceId,
            fileSchedule.OccurrenceId);
        Assert.Equal(
            sourceSchedule.Claim!.ClaimToken,
            fileSchedule.Claim!.ClaimToken);

        var memoryReplay = await memory.ExecuteScheduleAsync(
            claimCommand);
        var fileReplay = await file.ExecuteScheduleAsync(
            claimCommand);
        Assert.True(memoryReplay.IsReplay);
        Assert.True(fileReplay.IsReplay);
        Assert.Equal(
            claimed.Receipt!.ReceiptId,
            memoryReplay.Receipt!.ReceiptId);
        Assert.Equal(
            claimed.Receipt.ReceiptId,
            fileReplay.Receipt!.ReceiptId);
        Assert.Equal(
            WorldSaveCodec.Write(save),
            WorldSaveCodec.Write(await bridge.CaptureAsync(file)));
        var missingScheduleMetadata =
            WithoutExtension(save, ScheduleMetadataKey);
        var missingMetadataFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreInMemoryAsync(
                    package,
                    missingScheduleMetadata));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
            missingMetadataFailure.ReasonCode);
    }

    [Fact]
    public async Task ForkRehomesSchedulesAndDropsParentClaim()
    {
        var package = Compile(Package());
        var source = NativeWorldRuntime.CreateInMemory(package);
        var sourceScope = new WorldTransactionScope(
            source.Address.WorldId,
            source.Address.TimelineId,
            source.TimelineEpoch);
        var created = await source.ExecuteScheduleAsync(
            WorldScheduleCommand.Create(
                "create-schedule",
                ScheduleIntent(sourceScope, "intent", dueTick: 0)));
        var claimed = await source.ExecuteScheduleAsync(
            WorldScheduleCommand.Claim(
                "claim-schedule",
                sourceScope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 0, 0),
                "parent-worker"));
        Assert.True(claimed.Applied);
        var bridge = new NativeWorldSaveBridge();
        var parent = await bridge.CaptureAsync(source);

        var fork = await bridge.ForkAsync(
            package,
            parent,
            "alternate");
        var forkRuntime = await bridge.RestoreInMemoryAsync(
            package,
            fork);
        var forkSchedule = await forkRuntime.FindScheduleAsync(
            "intent");
        var parentSchedule = await source.FindScheduleAsync("intent");
        Assert.NotNull(forkSchedule);
        Assert.NotNull(parentSchedule);
        Assert.Equal(0, forkSchedule!.Generation);
        Assert.Equal(
            WorldScheduleStatus.Active,
            forkSchedule.Status);
        Assert.Null(forkSchedule.Claim);
        Assert.Equal("alternate", forkSchedule.Scope.TimelineId);
        Assert.Equal(1, forkSchedule.Scope.TimelineEpoch);
        Assert.NotEqual(
            created.Schedule!.OccurrenceId,
            forkSchedule.OccurrenceId);
        Assert.Equal(
            claimed.Schedule!.OccurrenceId,
            parentSchedule!.OccurrenceId);
        Assert.NotNull(parentSchedule.Claim);

        var forkClaim = await forkRuntime.ExecuteScheduleAsync(
            WorldScheduleCommand.Claim(
                "claim-schedule",
                forkSchedule.Scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "alternate", 1, 0),
                "fork-worker"));
        Assert.True(forkClaim.Applied);
        Assert.Equal(
            forkSchedule.OccurrenceId,
            forkClaim.Schedule!.OccurrenceId);
    }

    [Fact]
    public async Task MissingOptionalScheduleSectionMeansEmptyState()
    {
        var package = Compile(Package());
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(
            NativeWorldRuntime.CreateInMemory(package));
        var legacy = WithoutExtension(
            save,
            ScheduleMetadataKey);

        var restored = await bridge.RestoreInMemoryAsync(
            package,
            legacy);

        Assert.Null(
            await restored.FindScheduleAsync("not-present"));
    }

    [Fact]
    public async Task ScheduleBridgeBoundsFailBeforeRestoreMutation()
    {
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        var scope = new WorldTransactionScope(
            runtime.Address.WorldId,
            runtime.Address.TimelineId,
            runtime.TimelineEpoch);
        _ = await runtime.ExecuteScheduleAsync(
            WorldScheduleCommand.Create(
                "create-a",
                ScheduleIntent(scope, "a", 1)));
        _ = await runtime.ExecuteScheduleAsync(
            WorldScheduleCommand.Create(
                "create-b",
                ScheduleIntent(scope, "b", 2)));
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(runtime);
        var recordBound = new NativeWorldSaveBridgeOptions(
            maxScheduleRecords: 1);
        var operationBound = new NativeWorldSaveBridgeOptions(
            maxScheduleOperations: 1);

        var captureFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.CaptureAsync(
                    runtime,
                    recordBound));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
            captureFailure.ReasonCode);
        var admissionFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreInMemoryAsync(
                    package,
                    save,
                    bridgeOptions: operationBound));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
            admissionFailure.ReasonCode);
    }

    [Fact]
    public async Task RuntimeRejectsFutureScheduleObservation()
    {
        var package = Compile(Package());
        var runtime = NativeWorldRuntime.CreateInMemory(package);
        var scope = new WorldTransactionScope(
            runtime.Address.WorldId,
            runtime.Address.TimelineId,
            runtime.TimelineEpoch);
        _ = await runtime.ExecuteScheduleAsync(
            WorldScheduleCommand.Create(
                "create",
                ScheduleIntent(scope, "intent", 0)));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await runtime.ExecuteScheduleAsync(
                WorldScheduleCommand.Claim(
                    "future-claim",
                    scope,
                    "intent",
                    expectedGeneration: 0,
                    new GameTimePoint("turn", "main", 0, 1),
                    "worker")));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await runtime.QueryDueSchedulesAsync(
                new WorldScheduleDueQuery(
                    scope,
                    "turn",
                    throughTick: 1)));
        var schedule = await runtime.FindScheduleAsync("intent");
        Assert.NotNull(schedule);
        Assert.Null(schedule!.Claim);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await runtime.ExecuteScheduleAsync(
                WorldScheduleCommand.Create(
                    "undeclared-runtime",
                    ScheduleIntent(
                        scope,
                        "undeclared-runtime",
                        dueTick: 0,
                        clockId: "missing-clock"))));
        var bypass = await runtime.ScheduleStore.ExecuteAsync(
            WorldScheduleCommand.Create(
                "undeclared-bypass",
                ScheduleIntent(
                    scope,
                    "undeclared-bypass",
                    dueTick: 0,
                    clockId: "missing-clock")),
            CancellationToken.None);
        Assert.True(bypass.Applied);
        var bridgeFailure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await new NativeWorldSaveBridge()
                    .CaptureAsync(runtime));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.BindingMismatch,
            bridgeFailure.ReasonCode);
    }

    [Fact]
    public async Task ExistingFileWithUndeclaredScheduleFailsComposition()
    {
        using var directory = new TemporaryBridgeDirectory();
        var package = Compile(Package());
        var initial = await NativeWorldRuntime.CreateInMemory(package)
            .ReadSnapshotAsync();
        Assert.NotNull(initial);
        var store = new FileWorldAuthoritativeTransactionStore(
            directory.StorePath,
            [initial!]);
        var scope = initial.Coordinate.Scope;
        var inserted = await store.ExecuteAsync(
            WorldScheduleCommand.Create(
                "direct-create",
                ScheduleIntent(
                    scope,
                    "undeclared",
                    dueTick: 0,
                    clockId: "missing-clock")),
            CancellationToken.None);
        Assert.True(inserted.Applied);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await NativeWorldRuntime.CreateFileAsync(
                package,
                directory.StorePath,
                scope.TimelineId,
                scope.TimelineEpoch));
    }

    [Fact]
    public async Task SymbolicParentPathIsRejectedWhenSupported()
    {
        using var directory = new TemporaryBridgeDirectory();
        var link = Path.Combine(directory.RootPath, "linked");
        var real = Path.Combine(directory.RootPath, "real");
        Directory.CreateDirectory(real);
        try
        {
            _ = Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        var package = Compile(Package());
        var bridge = new NativeWorldSaveBridge();
        var save = await bridge.CaptureAsync(
            NativeWorldRuntime.CreateInMemory(package));
        var target = Path.Combine(link, "world-store.json");

        var failure =
            await Assert.ThrowsAsync<NativeWorldSaveBridgeException>(
                async () => await bridge.RestoreFileAsync(
                    package,
                    save,
                    target));

        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.UnsafePath,
            failure.ReasonCode);
        Assert.False(File.Exists(Path.Combine(real, "world-store.json")));
    }

    private static async Task ExecuteInteractionAndAdvanceAsync(
        NativeWorldRuntime runtime,
        ActivatedWorldPackage package)
    {
        var initial = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
        var planned = await runtime.PlanInteractionAsync(
            Execution(initial, package.CatalogDigest));
        Assert.True(planned.Succeeded);
        var execution = await runtime.ExecuteInteractionAsync(
            planned.Value!);
        Assert.True(execution.Value!.Succeeded);
        var command = new WorldAdvanceClockCommand(
            "advance-command",
            "advance-operation",
            execution.Value.Coordinate,
            "turn",
            expectedClockTick: 0,
            ticks: 1);
        var advanced = await runtime.AdvanceClockAsync(command);
        Assert.True(advanced.Succeeded);
    }

    private static async Task<WorldAuthoritativeStateSnapshot>
        MutateIncarnationsAsync(
            NativeWorldRuntime runtime,
            WorldAuthoritativeStateSnapshot source,
            string suffix,
            Action<IWorldStateDraft> mutate)
    {
        var coordinate = source.Coordinate;
        var request = new WorldTransactionRequest(
            "operation-" + suffix,
            "command-" + suffix,
            coordinate.CatalogDigest,
            coordinate,
            eventOccurrence: new WorldEventHistoryRecord(
                "instance-" + suffix,
                new WorldEventDefinitionKey(
                    coordinate.WorldId,
                    coordinate.TimelineId,
                    coordinate.TimelineEpoch,
                    "incarnation-change",
                    "1"),
                "trigger-" + suffix,
                "resolution-" + suffix,
                coordinate.CatalogDigest,
                occurredAt: null));
        var begin = await runtime.TransactionStore.BeginAsync(
            request,
            CancellationToken.None);
        var transaction = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(begin.Transaction);
        mutate(transaction.Draft);
        var committed = await transaction.CommitEventAsync(
            new WorldEffectReceipt(true, "applied"),
            CancellationToken.None);
        await transaction.DisposeAsync();
        Assert.Equal(
            WorldTransactionCommitStatus.Committed,
            committed.Status);
        return Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
    }

    private static async Task ContinueAsync(NativeWorldRuntime runtime)
    {
        var current = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
        var command = new WorldAdvanceClockCommand(
            "continue-command",
            "continue-operation",
            current.Coordinate,
            "turn",
            expectedClockTick: 1,
            ticks: 1);
        var result = await runtime.AdvanceClockAsync(command);
        Assert.True(result.Succeeded);
    }

    private static InteractionExecutionRequest Execution(
        WorldAuthoritativeStateSnapshot snapshot,
        string catalogDigest)
    {
        var coordinate = snapshot.Coordinate;
        return new InteractionExecutionRequest(
            "interaction-command",
            "interaction-operation",
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(
                CultureInfo.InvariantCulture),
            catalogDigest,
            "mark-target",
            "1",
            new GameEntityIdentity("actor", 1),
            [new GameEntityIdentity("target", 1)],
            "local",
            Json("""{}"""));
    }

    private static void AssertSnapshotParity(
        WorldAuthoritativeStateSnapshot? expected,
        WorldAuthoritativeStateSnapshot? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.True(
            expected!.Coordinate.IsExactMatch(actual!.Coordinate));
        Assert.Equal(expected.StateDigest, actual.StateDigest);
        Assert.Equal(
            expected.EntityIncarnations.OrderBy(
                item => item.Key,
                StringComparer.Ordinal),
            actual.EntityIncarnations.OrderBy(
                item => item.Key,
                StringComparer.Ordinal));
        Assert.Equal(
            expected.IssuedEntityIncarnations.Select(
                item => (item.EntityId, item.Incarnation)),
            actual.IssuedEntityIncarnations.Select(
                item => (item.EntityId, item.Incarnation)));
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
                    item => item.Code
                            + " "
                            + item.Path
                            + " "
                            + item.Message)));
        return Assert.IsType<ActivatedWorldPackage>(result.Package);
    }

    private static WorldScheduleIntent ScheduleIntent(
        WorldTransactionScope scope,
        string scheduleId,
        long dueTick,
        string clockId = "turn")
    {
        return new WorldScheduleIntent(
            scheduleId,
            scope,
            new GameTimePoint(
                clockId,
                scope.TimelineId,
                scope.TimelineEpoch,
                dueTick),
            new GameEntityIdentity("actor", 1),
            "long-intent",
            "1",
            Json(
                """
                {
                  "type": "object",
                  "properties": {
                    "intent": {"type": "string"}
                  },
                  "required": ["intent"],
                  "additionalProperties": false
                }
                """),
            Json("""{"intent":"remember"}"""));
    }

    private static WorldPackageDefinition Package(
        string interactionReward = "1",
        string worldId = "world")
    {
        return new WorldPackageDefinition(
            "runtime-test-package",
            "1",
            [
                JsonFile(
                    "world.json",
                    $$"""
                    {
                      "contract": "game-agent.world-definition.v1",
                      "worldId": "{{worldId}}",
                      "defaultTimelineId": "main",
                      "entityStateRootPath": "/entities",
                      "relationshipRootPath": "/relationships",
                      "initialState": {
                        "entities": {
                          "actor": {
                            "tags": ["npc"],
                            "score": "10"
                          },
                          "target": {
                            "tags": []
                          }
                        },
                        "relationships": {}
                      },
                      "entityIncarnations": {
                        "actor": "1",
                        "target": "1"
                      }
                    }
                    """),
                JsonFile(
                    "clocks.json",
                    """
                    {
                      "contract": "game-agent.world-clocks.v1",
                      "clocks": [
                        {
                          "clockId": "turn",
                          "statePath": "/clocks/turn/tick",
                          "initialTick": "0"
                        }
                      ]
                    }
                    """),
                JsonFile(
                    "numerics.json",
                    """
                    {
                      "contract": "game-agent.world-numerics.v1",
                      "schemas": [
                        {
                          "schemaId": "score",
                          "scale": 0,
                          "unitId": "score-unit",
                          "minimum": "0",
                          "maximum": "100",
                          "defaultValue": "0"
                        }
                      ]
                    }
                    """),
                JsonFile(
                    "events.json",
                    """
                    {
                      "contract": "game-agent.world-events.v1",
                      "events": [
                        {
                          "definitionId": "increment",
                          "version": "1",
                          "priority": 0,
                          "trigger": {
                            "kind": "clock",
                            "clockId": "turn",
                            "everyTicks": "1"
                          },
                          "selector": {
                            "kind": "entity",
                            "entityId": "actor",
                            "incarnation": "1"
                          },
                          "condition": {"kind": "always"},
                          "effects": [
                            {
                              "kind": "numeric",
                              "effectId": "increment-score",
                              "entity": "subject",
                              "path": "/score",
                              "resourceKey": "actor:score",
                              "schemaId": "score",
                              "operation": "add",
                              "value": "1"
                            }
                          ]
                        }
                      ]
                    }
                    """),
                JsonFile(
                    "interactions.json",
                    $$"""
                    {
                      "contract": "game-agent.world-interactions.v1",
                      "interactions": [
                        {
                          "interactionId": "mark-target",
                          "version": "1",
                          "contentRevision": "1",
                          "priority": 0,
                          "parameterSchemaId": "mark-target.input",
                          "parameterSchemaVersion": "1",
                          "parameterSchema": {
                            "type": "object",
                            "properties": {},
                            "additionalProperties": false
                          },
                          "target": {
                            "schemaId": "entity",
                            "minimumTargets": 1,
                            "maximumTargets": 1
                          },
                          "channelIds": ["local"],
                          "tags": ["social"],
                          "requiredCapabilities": [],
                          "availability": {
                            "kind": "tag",
                            "tag": "npc"
                          },
                          "effects": [
                            {
                              "kind": "numeric",
                              "effectId": "reward-actor",
                              "entity": "subject",
                              "path": "/score",
                              "resourceKey": "actor:score",
                              "schemaId": "score",
                              "operation": "add",
                              "value": "{{interactionReward}}"
                            },
                            {
                              "kind": "set",
                              "effectId": "mark-target",
                              "entity": "target:0",
                              "path": "/noticed",
                              "resourceKey": "target:noticed",
                              "value": true
                            }
                          ],
                          "presentation": {"label": "Mark"}
                        }
                      ]
                    }
                    """)
            ]);
    }

    private static WorldPackageFile JsonFile(string path, string value)
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

    private static string WorstCaseMaximumLengthEntityId(int index)
    {
        var suffix = index.ToString(
            "D5",
            CultureInfo.InvariantCulture);
        var escaping = new[] { '\0', '\u0001', '\u001f', '"', '\\' };
        var prefix = new string(
            Enumerable.Range(
                    0,
                    WorldValidation.MaximumIdentifierUtf8Bytes
                    - suffix.Length)
                .Select(value => escaping[value % escaping.Length])
                .ToArray());
        return prefix + suffix;
    }

    private static int CountJsonNodes(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => checked(
                1 + value.EnumerateObject().Sum(
                    property => CountJsonNodes(property.Value))),
            JsonValueKind.Array => checked(
                1 + value.EnumerateArray().Sum(CountJsonNodes)),
            _ => 1
        };
    }

    private static WorldSaveDocument ToObjectIssuedIncarnationSave(
        WorldSaveDocument save)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var item in save.EventLog.EnumerateArray())
            {
                var kind = item.GetProperty("kind").GetString();
                if (string.Equals(
                        kind,
                        "packedIncarnationLedger",
                        StringComparison.Ordinal))
                {
                    var packed = DecodePackedLedger(item);
                    foreach (var pair in packed.Current)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", "incarnation");
                        writer.WriteString("entityId", pair.Key);
                        writer.WriteString(
                            "incarnation",
                            pair.Value.ToString(
                                CultureInfo.InvariantCulture));
                        writer.WriteEndObject();
                    }

                    foreach (var issued in packed.Issued)
                    {
                        writer.WriteStartObject();
                        writer.WriteString(
                            "kind",
                            "issuedIncarnation");
                        writer.WriteString(
                            "entityId",
                            issued.EntityId);
                        writer.WriteString(
                            "incarnation",
                            issued.Incarnation.ToString(
                                CultureInfo.InvariantCulture));
                        writer.WriteEndObject();
                    }

                    continue;
                }

                if (!string.Equals(
                        kind,
                        "issuedIncarnationLedger",
                        StringComparison.Ordinal))
                {
                    item.WriteTo(writer);
                    continue;
                }

                var entityIds = item.GetProperty("entityIds");
                var incarnations = item.GetProperty("incarnations");
                Assert.Equal(
                    entityIds.GetArrayLength(),
                    incarnations.GetArrayLength());
                var entityEnumerator = entityIds.EnumerateArray();
                var incarnationEnumerator =
                    incarnations.EnumerateArray();
                while (entityEnumerator.MoveNext()
                       && incarnationEnumerator.MoveNext())
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", "issuedIncarnation");
                    writer.WriteString(
                        "entityId",
                        entityEnumerator.Current.GetString());
                    writer.WriteString(
                        "incarnation",
                        incarnationEnumerator.Current.GetString());
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }

        using var eventDocument = JsonDocument.Parse(output.ToArray());
        var eventLog = eventDocument.RootElement.Clone();
        var recordDigest = WorldLargeCanonicalJsonDigest.Compute(
            eventLog,
            WorldPackageLimits.HardMaximumFileBytes,
            "eventLog");
        var metadata = RewriteStringProperty(
            save.ExtensionData[MetadataKey],
            "recordDigest",
            recordDigest);
        return RewriteSave(save, eventLog, metadata);
    }

    private static WorldSaveDocument ToParallelIssuedIncarnationSave(
        WorldSaveDocument save)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var item in save.EventLog.EnumerateArray())
            {
                if (!string.Equals(
                        item.GetProperty("kind").GetString(),
                        "packedIncarnationLedger",
                        StringComparison.Ordinal))
                {
                    item.WriteTo(writer);
                    continue;
                }

                var packed = DecodePackedLedger(item);
                foreach (var pair in packed.Current)
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", "incarnation");
                    writer.WriteString("entityId", pair.Key);
                    writer.WriteString(
                        "incarnation",
                        pair.Value.ToString(
                            CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                }

                writer.WriteStartObject();
                writer.WriteString(
                    "kind",
                    "issuedIncarnationLedger");
                writer.WritePropertyName("entityIds");
                writer.WriteStartArray();
                foreach (var issued in packed.Issued)
                {
                    writer.WriteStringValue(issued.EntityId);
                }

                writer.WriteEndArray();
                writer.WritePropertyName("incarnations");
                writer.WriteStartArray();
                foreach (var issued in packed.Issued)
                {
                    writer.WriteStringValue(
                        issued.Incarnation.ToString(
                            CultureInfo.InvariantCulture));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var eventDocument = JsonDocument.Parse(output.ToArray());
        var eventLog = eventDocument.RootElement.Clone();
        var recordDigest = WorldLargeCanonicalJsonDigest.Compute(
            eventLog,
            WorldPackageLimits.HardMaximumFileBytes,
            "eventLog");
        var metadata = RewriteStringProperty(
            save.ExtensionData[MetadataKey],
            "recordDigest",
            recordDigest);
        return RewriteSave(save, eventLog, metadata);
    }

    private static JsonElement RewriteStringProperty(
        JsonElement source,
        string propertyName,
        string replacement)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in source.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.Ordinal))
                {
                    writer.WriteStringValue(replacement);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private static WorldSaveDocument ToLegacyCurrentOnlySave(
        WorldSaveDocument save)
    {
        using var eventOutput = new MemoryStream();
        using (var writer = new Utf8JsonWriter(eventOutput))
        {
            writer.WriteStartArray();
            foreach (var item in save.EventLog.EnumerateArray())
            {
                var kind = item.GetProperty("kind").GetString();
                if (string.Equals(
                        kind,
                        "packedIncarnationLedger",
                        StringComparison.Ordinal))
                {
                    foreach (var pair in DecodePackedLedger(item).Current)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", "incarnation");
                        writer.WriteString("entityId", pair.Key);
                        writer.WriteString(
                            "incarnation",
                            pair.Value.ToString(
                                CultureInfo.InvariantCulture));
                        writer.WriteEndObject();
                    }

                    continue;
                }

                if (!string.Equals(
                        kind,
                        "issuedIncarnation",
                        StringComparison.Ordinal)
                    && !string.Equals(
                        kind,
                        "issuedIncarnationLedger",
                        StringComparison.Ordinal))
                {
                    item.WriteTo(writer);
                }
            }

            writer.WriteEndArray();
        }

        using var eventDocument = JsonDocument.Parse(
            eventOutput.ToArray());
        var eventLog = eventDocument.RootElement.Clone();
        var recordDigest = WorldLargeCanonicalJsonDigest.Compute(
            eventLog,
            WorldPackageLimits.HardMaximumFileBytes,
            "eventLog");
        var metadata = save.ExtensionData[MetadataKey];
        var snapshotDigest = WorldDataDigest.Compute(
            Encoding.UTF8.GetBytes(
                WorldValidation.ComposeStableKey(
                    "native-world-snapshot-v1",
                    metadata.GetProperty("timelineDigest").GetString()!,
                    metadata.GetProperty("saveRevision").GetString()!,
                    metadata.GetProperty("stateVersion").GetString()!,
                    metadata.GetProperty("catalogDigest").GetString()!,
                    metadata.GetProperty("stateDigest").GetString()!,
                    metadata.GetProperty("incarnationDigest")
                        .GetString()!)));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        "issuedIncarnationDigest",
                        StringComparison.Ordinal)
                    || string.Equals(
                        property.Name,
                        "issuedIncarnationCount",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                if (string.Equals(
                        property.Name,
                        "recordDigest",
                        StringComparison.Ordinal))
                {
                    writer.WriteStringValue(recordDigest);
                }
                else if (string.Equals(
                             property.Name,
                             "snapshotDigest",
                             StringComparison.Ordinal))
                {
                    writer.WriteStringValue(snapshotDigest);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return RewriteSave(
            save,
            eventLog,
            document.RootElement.Clone());
    }

    private static WorldSaveDocument DuplicateIssuedIncarnationRecord(
        WorldSaveDocument save)
    {
        JsonElement? firstIssued = null;
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var item in save.EventLog.EnumerateArray())
            {
                item.WriteTo(writer);
                if (!firstIssued.HasValue
                    && (string.Equals(
                            item.GetProperty("kind").GetString(),
                            "issuedIncarnation",
                            StringComparison.Ordinal)
                        || string.Equals(
                            item.GetProperty("kind").GetString(),
                            "issuedIncarnationLedger",
                            StringComparison.Ordinal)
                        || string.Equals(
                            item.GetProperty("kind").GetString(),
                            "packedIncarnationLedger",
                            StringComparison.Ordinal)))
                {
                    firstIssued = item.Clone();
                    firstIssued.Value.WriteTo(writer);
                }
            }

            writer.WriteEndArray();
        }

        Assert.True(firstIssued.HasValue);
        using var document = JsonDocument.Parse(output.ToArray());
        return RewriteSave(
            save,
            document.RootElement.Clone(),
            save.ExtensionData[MetadataKey]);
    }

    private static NativeWorldPackedIncarnationLedger
        DecodePackedLedger(JsonElement item)
    {
        var chunks = item.GetProperty("chunks")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        return NativeWorldIncarnationLedgerCodec.Decode(
            chunks,
            int.Parse(
                item.GetProperty("byteLength").GetString()!,
                CultureInfo.InvariantCulture),
            WorldAuthoritativeStateSnapshot
                .MaximumIssuedIncarnationCount,
            WorldValidation.MaximumParticipants);
    }

    private static IReadOnlyList<string> EncodeBase85ForTest(
        ReadOnlySpan<byte> raw)
    {
        var output = new StringBuilder(
            checked(((raw.Length + 3) / 4) * 5));
        Span<byte> block = stackalloc byte[4];
        Span<char> digits = stackalloc char[5];
        for (var offset = 0; offset < raw.Length; offset += 4)
        {
            block.Clear();
            raw.Slice(
                    offset,
                    Math.Min(4, raw.Length - offset))
                .CopyTo(block);
            var value = BinaryPrimitives.ReadUInt32BigEndian(block);
            for (var index = 4; index >= 0; index--)
            {
                digits[index] =
                    NativeWorldIncarnationLedgerCodec
                        .Base85Alphabet[(int)(value % 85)];
                value /= 85;
            }

            output.Append(digits);
        }

        return new[] { output.ToString() };
    }

    private static byte[] EmptyPackedLedgerRaw()
    {
        var raw = new byte[14];
        Encoding.ASCII.GetBytes("GAIIL001").CopyTo(raw, 0);
        return raw;
    }

    private static WorldSaveDocument RewritePackedLedgerChunk(
        WorldSaveDocument save,
        Func<string, string> rewrite)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var item in save.EventLog.EnumerateArray())
            {
                if (!string.Equals(
                        item.GetProperty("kind").GetString(),
                        "packedIncarnationLedger",
                        StringComparison.Ordinal))
                {
                    item.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject();
                foreach (var property in item.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (!string.Equals(
                            property.Name,
                            "chunks",
                            StringComparison.Ordinal))
                    {
                        property.Value.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartArray();
                    var first = true;
                    foreach (var chunk in
                             property.Value.EnumerateArray())
                    {
                        var value = chunk.GetString()!;
                        writer.WriteStringValue(
                            first ? rewrite(value) : value);
                        first = false;
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        var eventLog = document.RootElement.Clone();
        var recordDigest = WorldLargeCanonicalJsonDigest.Compute(
            eventLog,
            WorldPackageLimits.HardMaximumFileBytes,
            "eventLog");
        var metadata = RewriteStringProperty(
            save.ExtensionData[MetadataKey],
            "recordDigest",
            recordDigest);
        return RewriteSave(save, eventLog, metadata);
    }

    private static WorldSaveDocument RewriteSave(
        WorldSaveDocument save,
        JsonElement eventLog,
        JsonElement metadata)
    {
        var extensionData = save.ExtensionData.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);
        extensionData[MetadataKey] = metadata.Clone();
        return new WorldSaveDocument(
            save.PackageId,
            save.PackageContentVersion,
            save.PackageDigest,
            save.WorldId,
            save.TimelineId,
            save.SaveRevision,
            save.StateVersion,
            save.Clocks,
            save.State,
            eventLog,
            save.MemoryReferences,
            save.ParentTimelineId,
            save.ParentSaveRevision,
            save.PendingTransaction,
            save.TrustedExtensions,
            extensionData);
    }

    private static WorldSaveDocument RewriteMetadata(
        WorldSaveDocument save,
        string propertyName,
        string replacement)
    {
        var metadata = save.ExtensionData[MetadataKey];
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in metadata.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.Ordinal))
                {
                    writer.WriteStringValue(replacement);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var rewrittenDocument = JsonDocument.Parse(stream.ToArray());
        var extensionData = save.ExtensionData.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);
        extensionData[MetadataKey] =
            rewrittenDocument.RootElement.Clone();
        return new WorldSaveDocument(
            save.PackageId,
            save.PackageContentVersion,
            save.PackageDigest,
            save.WorldId,
            save.TimelineId,
            save.SaveRevision,
            save.StateVersion,
            save.Clocks,
            save.State,
            save.EventLog,
            save.MemoryReferences,
            save.ParentTimelineId,
            save.ParentSaveRevision,
            save.PendingTransaction,
            save.TrustedExtensions,
            extensionData);
    }

    private static WorldSaveDocument WithoutExtension(
        WorldSaveDocument save,
        string extensionKey)
    {
        var extensionData = save.ExtensionData
            .Where(
                item => !string.Equals(
                    item.Key,
                    extensionKey,
                    StringComparison.Ordinal))
            .ToDictionary(
                item => item.Key,
                item => item.Value.Clone(),
                StringComparer.Ordinal);
        return new WorldSaveDocument(
            save.PackageId,
            save.PackageContentVersion,
            save.PackageDigest,
            save.WorldId,
            save.TimelineId,
            save.SaveRevision,
            save.StateVersion,
            save.Clocks,
            save.State,
            save.EventLog,
            save.MemoryReferences,
            save.ParentTimelineId,
            save.ParentSaveRevision,
            save.PendingTransaction,
            save.TrustedExtensions,
            extensionData);
    }

    private sealed class TemporaryBridgeDirectory : IDisposable
    {
        public TemporaryBridgeDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "game-agent-native-save-bridge-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string StorePath =>
            Path.Combine(RootPath, "world-store.json");

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
