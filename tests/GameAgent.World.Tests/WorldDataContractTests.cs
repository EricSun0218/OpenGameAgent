using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldDataContractTests
{
    [Fact]
    public void PackageArchiveIsByteDeterministicAndSemanticRoundTrip()
    {
        var package = CreatePackage();

        var first = WritePackage(package);
        var second = WritePackage(package);

        Assert.Equal(first, second);
        using var source = new MemoryStream(first);
        var restored = WorldPackageArchive.Read(source);
        Assert.Equal(package.PackageId, restored.PackageId);
        Assert.Equal(package.ContentVersion, restored.ContentVersion);
        Assert.Equal(package.PackageDigest, restored.PackageDigest);
        Assert.Equal(
            package.Files.Select(file => file.Path),
            restored.Files.Select(file => file.Path));
        Assert.Equal(
            package.Files.Select(file => file.Digest),
            restored.Files.Select(file => file.Digest));
        var manifest = Json(
            Encoding.UTF8.GetString(
                ReadZipEntries(first)[WorldDataContractIds.ManifestPath]));
        Assert.All(
            manifest.GetProperty("files").EnumerateArray(),
            file => Assert.Equal(
                JsonValueKind.String,
                file.GetProperty("length").ValueKind));
    }

    [Theory]
    [InlineData("../state.json")]
    [InlineData("/state.json")]
    [InlineData("data\\state.json")]
    [InlineData("C:/state.json")]
    [InlineData("data/../state.json")]
    [InlineData("NUL.json")]
    public void PackageFileRejectsUnsafePaths(string path)
    {
        var exception = Assert.Throws<WorldDataContractException>(
            () => new WorldPackageFile(
                path,
                "application/json",
                Encoding.UTF8.GetBytes("{}")));

        Assert.Equal(WorldDataReasonCodes.InvalidPath, exception.ReasonCode);
    }

    [Fact]
    public void PackageRejectsCaseCollidingPaths()
    {
        var files = new[]
        {
            JsonFile("data/Actor.json", "{}"),
            JsonFile("data/actor.json", "{}")
        };

        var exception = Assert.Throws<WorldDataContractException>(
            () => new WorldPackageDefinition("sample", "1", files));

        Assert.Equal(
            WorldDataReasonCodes.DuplicatePath,
            exception.ReasonCode);
    }

    [Fact]
    public void PackageRejectsExecutableExtensionAndMagic()
    {
        var extension = Assert.Throws<WorldDataContractException>(
            () => new WorldPackageFile(
                "assets/extension.dll",
                "application/octet-stream",
                new byte[] { 1, 2, 3 }));
        var magic = Assert.Throws<WorldDataContractException>(
            () => new WorldPackageFile(
                "assets/blob.bin",
                "application/octet-stream",
                new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }));

        Assert.Equal(
            WorldDataReasonCodes.UnsafeContent,
            extension.ReasonCode);
        Assert.Equal(
            WorldDataReasonCodes.UnsafeContent,
            magic.ReasonCode);
    }

    [Fact]
    public void PackageWriteRejectsDuplicateJsonProperties()
    {
        var package = new WorldPackageDefinition(
            "sample",
            "1",
            new[] { JsonFile("data/value.json", "{\"a\":1,\"a\":2}") });

        using var destination = new MemoryStream();
        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldPackageArchive.Write(destination, package));

        Assert.Equal(
            WorldDataReasonCodes.DuplicateJsonProperty,
            exception.ReasonCode);
    }

    [Fact]
    public void PackageWriteRejectsInvalidEscapedUnicode()
    {
        var package = new WorldPackageDefinition(
            "sample",
            "1",
            new[]
            {
                JsonFile(
                    "data/value.json",
                    "{\"value\":\"\\uD800\"}")
            });

        using var destination = new MemoryStream();
        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldPackageArchive.Write(destination, package));

        Assert.Equal(
            WorldDataReasonCodes.InvalidJson,
            exception.ReasonCode);
    }

    [Fact]
    public void PackageReadRejectsPayloadDigestMismatch()
    {
        var original = WritePackage(CreatePackage());
        var entries = ReadZipEntries(original);
        entries["data/world.json"] =
            Encoding.UTF8.GetBytes("{\"changed\":true}");
        var tampered = WriteRawZip(entries, CompressionLevel.NoCompression);

        using var source = new MemoryStream(tampered);
        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldPackageArchive.Read(source));

        Assert.Equal(
            WorldDataReasonCodes.DigestMismatch,
            exception.ReasonCode);
    }

    [Fact]
    public void PackageReadRejectsCompressionBombBeforeExtraction()
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [WorldDataContractIds.ManifestPath] =
                Encoding.UTF8.GetBytes("{}"),
            ["data/repeated.bin"] = new byte[1024 * 1024]
        };
        var archive = WriteRawZip(entries, CompressionLevel.Optimal);
        var limits = new WorldPackageLimits(
            maxFiles: 8,
            maxFileBytes: 2 * 1024 * 1024,
            maxExpandedBytes: 4 * 1024 * 1024,
            maxCompressedBytes: 2 * 1024 * 1024,
            maxCompressionRatio: 10);

        using var source = new MemoryStream(archive);
        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldPackageArchive.Read(source, limits));

        Assert.Equal(
            WorldDataReasonCodes.CompressionLimitExceeded,
            exception.ReasonCode);
    }

    [Fact]
    public void PackageWriterEnforcesTheCompressedByteLimit()
    {
        var limits = new WorldPackageLimits(maxCompressedBytes: 64);
        using var destination = new MemoryStream();

        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldPackageArchive.Write(
                destination,
                CreatePackage(),
                limits));

        Assert.Equal(
            WorldDataReasonCodes.CompressionLimitExceeded,
            exception.ReasonCode);
    }

    [Fact]
    public void PackageActivationFailsClosedForUnapprovedExtension()
    {
        var package = CreatePackage();

        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldPackageActivationValidator
                .ValidateRequiredExtensions(
                    package,
                    new RejectAllCapabilities()));

        Assert.Equal(
            WorldDataReasonCodes.MissingExtension,
            exception.ReasonCode);
    }

    [Fact]
    public void SaveIsCanonicalAndRoundTripsPendingTransaction()
    {
        var package = CreatePackage();
        var save = CreateSave(package);

        var first = WorldSaveCodec.Write(save);
        var second = WorldSaveCodec.Write(save);
        var restored = WorldSaveCodec.Read(first);

        Assert.Equal(first, second);
        Assert.Equal(save.SaveDigest, restored.SaveDigest);
        Assert.Equal(save.WorldId, restored.WorldId);
        Assert.Equal(save.TimelineId, restored.TimelineId);
        Assert.Equal(save.StateVersion, restored.StateVersion);
        Assert.True(restored.PendingTransaction.HasValue);
        Assert.Equal(
            "pending-1",
            restored.PendingTransaction.Value
                .GetProperty("transactionId")
                .GetString());
        WorldSaveBinding.Validate(restored, package);
    }

    [Fact]
    public void PortableInt64SaveFieldsRoundTripBeyondJavaScriptSafeRange()
    {
        const long firstUnsafeInteger = 9_007_199_254_740_992;
        const long adjacentInteger = firstUnsafeInteger + 1;
        var package = CreatePackage();
        var save = new WorldSaveDocument(
            package.PackageId,
            package.ContentVersion,
            package.PackageDigest,
            "world-unsafe-integer",
            "timeline-b",
            adjacentInteger,
            "state-unsafe-integer",
            new[]
            {
                new WorldClockSnapshot(
                    "calendar",
                    firstUnsafeInteger,
                    adjacentInteger)
            },
            Json("{}"),
            Json("[]"),
            Json("[]"),
            parentTimelineId: "timeline-a",
            parentSaveRevision: firstUnsafeInteger);

        var bytes = WorldSaveCodec.Write(save);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(
            JsonValueKind.String,
            root.GetProperty("saveRevision").ValueKind);
        Assert.Equal(
            adjacentInteger.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            root.GetProperty("saveRevision").GetString());
        Assert.Equal(
            JsonValueKind.String,
            root.GetProperty("parentTimeline")
                .GetProperty("saveRevision")
                .ValueKind);
        var clock = root.GetProperty("clocks")[0];
        Assert.Equal(JsonValueKind.String, clock.GetProperty("epoch").ValueKind);
        Assert.Equal(JsonValueKind.String, clock.GetProperty("tick").ValueKind);

        var restored = WorldSaveCodec.Read(bytes);
        Assert.Equal(adjacentInteger, restored.SaveRevision);
        Assert.Equal(firstUnsafeInteger, restored.ParentSaveRevision);
        Assert.Equal(firstUnsafeInteger, restored.Clocks[0].Epoch);
        Assert.Equal(adjacentInteger, restored.Clocks[0].Tick);
        Assert.Equal(bytes, WorldSaveCodec.Write(restored));

        var adjacentSave = new WorldSaveDocument(
            package.PackageId,
            package.ContentVersion,
            package.PackageDigest,
            "world-unsafe-integer",
            "timeline-b",
            firstUnsafeInteger,
            "state-unsafe-integer",
            new[]
            {
                new WorldClockSnapshot(
                    "calendar",
                    firstUnsafeInteger,
                    firstUnsafeInteger)
            },
            Json("{}"),
            Json("[]"),
            Json("[]"),
            parentTimelineId: "timeline-a",
            parentSaveRevision: firstUnsafeInteger);
        Assert.NotEqual(save.SaveDigest, adjacentSave.SaveDigest);
        Assert.NotEqual(bytes, WorldSaveCodec.Write(adjacentSave));
    }

    [Fact]
    public void SaveReaderRejectsPortableInt64JsonNumbers()
    {
        const long unsafeInteger = 9_007_199_254_740_992;
        var package = CreatePackage();
        var save = new WorldSaveDocument(
            package.PackageId,
            package.ContentVersion,
            package.PackageDigest,
            "world-unsafe-integer",
            "timeline",
            unsafeInteger,
            "state-unsafe-integer",
            new[]
            {
                new WorldClockSnapshot("calendar", unsafeInteger, 0)
            },
            Json("{}"),
            Json("[]"),
            Json("[]"));
        var canonical = Encoding.UTF8.GetString(
            WorldSaveCodec.Write(save));
        var withJsonNumber = canonical.Replace(
            "\"saveRevision\":\""
            + unsafeInteger.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            + "\"",
            "\"saveRevision\":"
            + unsafeInteger.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        Assert.NotEqual(canonical, withJsonNumber);

        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldSaveCodec.Read(
                Encoding.UTF8.GetBytes(withJsonNumber)));

        Assert.Equal(WorldDataReasonCodes.InvalidJson, exception.ReasonCode);
    }

    [Fact]
    public void SaveBindingRequiresExactPackageDigest()
    {
        var package = CreatePackage();
        var changed = new WorldPackageDefinition(
            package.PackageId,
            package.ContentVersion,
            new[] { JsonFile("data/world.json", "{\"v\":\"2\"}") });
        var save = CreateSave(package);

        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldSaveBinding.Validate(save, changed));

        Assert.Equal(
            WorldDataReasonCodes.PackageBindingMismatch,
            exception.ReasonCode);
    }

    [Theory]
    [InlineData("{\"value\":1}")]
    [InlineData("{\"value\":1.25}")]
    [InlineData("{\"value\":1e2}")]
    public void SaveAuthoritativeStateRejectsJsonNumbers(string state)
    {
        var package = CreatePackage();

        Assert.Throws<ArgumentException>(
            () => CreateSave(package, state));
    }

    [Fact]
    public void SaveAuthoritativeAuxiliaryPayloadsRejectJsonNumbers()
    {
        var package = CreatePackage();

        Assert.Throws<ArgumentException>(
            () => CreateSaveWithPayloads(
                package,
                Json("[1]"),
                Json("[]"),
                null));
        Assert.Throws<ArgumentException>(
            () => CreateSaveWithPayloads(
                package,
                Json("[]"),
                Json("[1]"),
                null));
        Assert.Throws<ArgumentException>(
            () => CreateSaveWithPayloads(
                package,
                Json("[]"),
                Json("[]"),
                Json("{\"cursor\":1}")));
    }

    [Fact]
    public void SaveCanonicalTotalCannotExceedTheConfiguredFileLimit()
    {
        var package = CreatePackage();
        var save = CreateSave(
            package,
            "{\"blob\":\"" + new string('x', 1_200) + "\"}");
        var limits = new WorldPackageLimits(
            maxFileBytes: 1_024,
            maxExpandedBytes: 4_096,
            maxCompressedBytes: 4_096);

        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldSaveCodec.Write(save, limits));

        Assert.Equal(
            WorldDataReasonCodes.ByteLimitExceeded,
            exception.ReasonCode);
    }

    [Fact]
    public void OversizedSaveExportLeavesExistingFileAndNoTemporaryArtifact()
    {
        var package = CreatePackage();
        var save = CreateSave(
            package,
            "{\"blob\":\"" + new string('x', 1_200) + "\"}");
        var limits = new WorldPackageLimits(
            maxFileBytes: 1_024,
            maxExpandedBytes: 4_096,
            maxCompressedBytes: 4_096);
        var path = Path.Combine(
            Path.GetTempPath(),
            "world-export-" + Guid.NewGuid().ToString("N") + ".json");
        var original = Encoding.UTF8.GetBytes("previous-save");
        File.WriteAllBytes(path, original);
        try
        {
            var facade = new InteractiveWorldFacade(
                new WorldEventPlanner(
                    new WorldEventHandlerRegistryBuilder().Build(),
                    new InMemoryWorldEventHistory()));

            var exception = Assert.Throws<WorldDataContractException>(
                () => facade.ExportSaveFile(path, save, limits));

            Assert.Equal(
                WorldDataReasonCodes.ByteLimitExceeded,
                exception.ReasonCode);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(
                Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    "." + Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FailedFileExportPreservesThePreviousArtifact()
    {
        var facade = new InteractiveWorldFacade(
            new WorldEventPlanner(
                new WorldEventHandlerRegistryBuilder().Build(),
                new InMemoryWorldEventHistory()));
        var path = Path.Combine(
            Path.GetTempPath(),
            "world-export-" + Guid.NewGuid().ToString("N") + ".json");
        var original = Encoding.UTF8.GetBytes("previous-save");
        File.WriteAllBytes(path, original);
        try
        {
            Assert.Throws<ArgumentNullException>(
                () => facade.ExportSaveFile(path, null!));

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(
                Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    "." + Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveCanonicalizesEveryNestedAuthoritativeObject()
    {
        var package = CreatePackage(reverseExtensions: false);
        var reorderedPackage = CreatePackage(reverseExtensions: true);
        var first = CreateOrderedSave(package, reverse: false);
        var second = CreateOrderedSave(package, reverse: true);

        Assert.Equal(
            package.PackageDigest,
            reorderedPackage.PackageDigest);
        Assert.Equal(
            WritePackage(package),
            WritePackage(reorderedPackage));
        Assert.Equal(first.SaveDigest, second.SaveDigest);
        Assert.Equal(
            WorldSaveCodec.Write(first),
            WorldSaveCodec.Write(second));
    }

    [Fact]
    public void SaveReaderRejectsNonCanonicalNestedPropertyOrder()
    {
        var package = CreatePackage();
        var canonical = Encoding.UTF8.GetString(
            WorldSaveCodec.Write(CreateOrderedSave(package, reverse: false)));
        var nonCanonical = canonical.Replace(
            """{"a":"1","b":"2"}""",
            """{"b":"2","a":"1"}""",
            StringComparison.Ordinal);
        Assert.NotEqual(canonical, nonCanonical);

        var exception = Assert.Throws<WorldDataContractException>(
            () => WorldSaveCodec.Read(
                Encoding.UTF8.GetBytes(nonCanonical)));

        Assert.Equal(
            WorldDataReasonCodes.InvalidJson,
            exception.ReasonCode);
    }

    [Fact]
    public void PackageAndSaveExtensionDataRejectBareJsonNumbers()
    {
        var packageException = Assert.Throws<ArgumentException>(
            () => new WorldPackageDefinition(
                "numeric-extension",
                "1",
                Array.Empty<WorldPackageFile>(),
                extensionData:
                new Dictionary<string, JsonElement>
                {
                    ["com.example.numeric"] = Json("""{"value":1}""")
                }));
        var package = CreatePackage();
        var saveException = Assert.Throws<ArgumentException>(
            () => new WorldSaveDocument(
                package.PackageId,
                package.ContentVersion,
                package.PackageDigest,
                "world-1",
                "timeline-1",
                0,
                "0",
                Array.Empty<WorldClockSnapshot>(),
                Json("{}"),
                Json("[]"),
                Json("[]"),
                extensionData:
                new Dictionary<string, JsonElement>
                {
                    ["com.example.numeric"] = Json("""{"value":1}""")
                }));

        Assert.Contains(
            "canonical strings",
            packageException.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonical strings",
            saveException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackageAndSaveExtensionDataRejectThe257thEntry()
    {
        var extensionData = ExtensionDataJson(257);
        var manifest = string.Concat(
            "{\"contract\":\"",
            WorldDataContractIds.PackageV1,
            "\",\"packageId\":\"package\",\"contentVersion\":\"1\",",
            "\"files\":[],\"requiredExtensions\":[],\"extensionData\":",
            extensionData,
            "}");
        var packageException = Assert.Throws<ArgumentException>(
            () => WorldPackageManifestCodec.Read(
                Encoding.UTF8.GetBytes(manifest),
                new WorldPackageLimits()));

        Assert.Equal("extensionData", packageException.ParamName);

        var save = string.Concat(
            "{\"contract\":\"",
            WorldDataContractIds.SaveV1,
            "\",\"packageId\":\"package\",",
            "\"packageContentVersion\":\"1\",\"packageDigest\":\"",
            new string('a', 64),
            "\",\"worldId\":\"world\",\"timelineId\":\"timeline\",",
            "\"parentTimeline\":null,\"saveRevision\":\"0\",",
            "\"stateVersion\":\"0\",\"clocks\":[],\"state\":{},",
            "\"eventLog\":[],\"memoryReferences\":[],",
            "\"pendingTransaction\":null,\"trustedExtensions\":[],",
            "\"extensionData\":",
            extensionData,
            "}");
        var saveException = Assert.Throws<ArgumentException>(
            () => WorldSaveCodec.Read(Encoding.UTF8.GetBytes(save)));

        Assert.Equal("extensionData", saveException.ParamName);
    }

    [Fact]
    public void SaveRejectsDuplicatePropertiesAndNonCanonicalInput()
    {
        var package = CreatePackage();
        var canonical = Encoding.UTF8.GetString(
            WorldSaveCodec.Write(CreateSave(package)));
        var duplicate = canonical.Replace(
            "\"worldId\":\"world-1\",",
            "\"worldId\":\"world-1\",\"worldId\":\"world-2\",",
            StringComparison.Ordinal);
        var reformatted = canonical.Replace(
            "{\"contract\"",
            "{ \"contract\"",
            StringComparison.Ordinal);

        var duplicateError = Assert.Throws<WorldDataContractException>(
            () => WorldSaveCodec.Read(Encoding.UTF8.GetBytes(duplicate)));
        var canonicalError = Assert.Throws<WorldDataContractException>(
            () => WorldSaveCodec.Read(Encoding.UTF8.GetBytes(reformatted)));

        Assert.Equal(
            WorldDataReasonCodes.DuplicateJsonProperty,
            duplicateError.ReasonCode);
        Assert.Equal(
            WorldDataReasonCodes.InvalidJson,
            canonicalError.ReasonCode);
    }

    [Fact]
    public void SaveTimelineParentIsAllOrNothing()
    {
        var package = CreatePackage();

        Assert.Throws<ArgumentException>(
            () => new WorldSaveDocument(
                package.PackageId,
                package.ContentVersion,
                package.PackageDigest,
                "world-1",
                "timeline-b",
                1,
                "state-1",
                Array.Empty<WorldClockSnapshot>(),
                Json("{}"),
                Json("[]"),
                Json("[]"),
                parentTimelineId: "timeline-a"));
    }

    private static WorldPackageDefinition CreatePackage(
        bool reverseExtensions = false)
    {
        var extensionData =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var extensionEntries = new[]
        {
            new KeyValuePair<string, JsonElement>(
                "com.example.alpha",
                Json("{\"label\":\"alpha\"}")),
            new KeyValuePair<string, JsonElement>(
                "com.example.metadata",
                Json("{\"enabled\":true}")),
            new KeyValuePair<string, JsonElement>(
                "com.example.zeta",
                Json("{\"label\":\"zeta\"}"))
        };
        foreach (var entry in reverseExtensions
                     ? extensionEntries.Reverse()
                     : extensionEntries)
        {
            extensionData.Add(entry.Key, entry.Value);
        }

        return new WorldPackageDefinition(
            "sample.world",
            "1.0.0",
            new[]
            {
                JsonFile(
                    "data/world.json",
                    "{\"kind\":\"fixture\",\"value\":\"12\"}"),
                new WorldPackageFile(
                    "assets/icon.bin",
                    "application/octet-stream",
                    new byte[] { 1, 2, 3, 4 })
            },
            new[]
            {
                new WorldPackageExtensionRequirement(
                    "sample.rules",
                    "[1.0,2.0)")
            },
            extensionData);
    }

    private static WorldSaveDocument CreateSave(
        WorldPackageDefinition package,
        string state = "{\"values\":{\"score\":\"1200\"}}")
    {
        return new WorldSaveDocument(
            package.PackageId,
            package.ContentVersion,
            package.PackageDigest,
            "world-1",
            "timeline-1",
            4,
            "state-v4",
            new[]
            {
                new WorldClockSnapshot("calendar", 0, 8),
                new WorldClockSnapshot("turn", 0, 22)
            },
            Json(state),
            Json("[{\"sequence\":\"1\",\"outcome\":\"accepted\"}]"),
            Json("[{\"scope\":\"private\",\"reference\":\"memory-1\"}]"),
            pendingTransaction:
                Json(
                    "{\"transactionId\":\"pending-1\","
                    + "\"cursor\":\"2\",\"draftDigest\":\""
                    + new string('a', 64)
                    + "\"}"),
            trustedExtensions:
                new[]
                {
                    new WorldTrustedExtensionIdentity(
                        "sample.rules",
                        "1.2.0",
                        new string('b', 64))
                },
            extensionData:
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["com.example.save"] = Json("{\"label\":\"slot\"}")
                });
    }

    private static WorldSaveDocument CreateSaveWithPayloads(
        WorldPackageDefinition package,
        JsonElement eventLog,
        JsonElement memoryReferences,
        JsonElement? pendingTransaction)
    {
        return new WorldSaveDocument(
            package.PackageId,
            package.ContentVersion,
            package.PackageDigest,
            "world-1",
            "timeline-1",
            0,
            "state-0",
            Array.Empty<WorldClockSnapshot>(),
            Json("{}"),
            eventLog,
            memoryReferences,
            pendingTransaction: pendingTransaction);
    }

    private static WorldSaveDocument CreateOrderedSave(
        WorldPackageDefinition package,
        bool reverse)
    {
        var state = reverse
            ? Json("""{"root":{"b":"2","a":"1"}}""")
            : Json("""{"root":{"a":"1","b":"2"}}""");
        var events = reverse
            ? Json("""[{"z":"last","a":"first"}]""")
            : Json("""[{"a":"first","z":"last"}]""");
        var memories = reverse
            ? Json("""[{"scope":"private","id":"memory"}]""")
            : Json("""[{"id":"memory","scope":"private"}]""");
        var pending = reverse
            ? Json("""{"z":"last","a":"first"}""")
            : Json("""{"a":"first","z":"last"}""");
        var extension = reverse
            ? Json("""{"outer":{"z":"last","a":"first"}}""")
            : Json("""{"outer":{"a":"first","z":"last"}}""");
        var extensionData =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var extensionEntries = new[]
        {
            new KeyValuePair<string, JsonElement>(
                "com.example.alpha",
                Json("""{"label":"alpha"}""")),
            new KeyValuePair<string, JsonElement>(
                "com.example.ordered",
                extension),
            new KeyValuePair<string, JsonElement>(
                "com.example.zeta",
                Json("""{"label":"zeta"}"""))
        };
        foreach (var entry in reverse
                     ? extensionEntries.Reverse()
                     : extensionEntries)
        {
            extensionData.Add(entry.Key, entry.Value);
        }

        return new WorldSaveDocument(
            package.PackageId,
            package.ContentVersion,
            package.PackageDigest,
            "world-1",
            "timeline-1",
            0,
            "0",
            Array.Empty<WorldClockSnapshot>(),
            state,
            events,
            memories,
            pendingTransaction: pending,
            extensionData: extensionData);
    }

    private static WorldPackageFile JsonFile(
        string path,
        string content)
    {
        return new WorldPackageFile(
            path,
            "application/json",
            Encoding.UTF8.GetBytes(content));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ExtensionDataJson(int count)
    {
        var output = new StringBuilder();
        output.Append('{');
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                output.Append(',');
            }

            output.Append("\"test.entry.");
            output.Append(index.ToString("D3"));
            output.Append("\":{}");
        }

        output.Append('}');
        return output.ToString();
    }

    private static byte[] WritePackage(WorldPackageDefinition package)
    {
        using var destination = new MemoryStream();
        WorldPackageArchive.Write(destination, package);
        return destination.ToArray();
    }

    private static Dictionary<string, byte[]> ReadZipEntries(byte[] bytes)
    {
        using var source = new MemoryStream(bytes);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var input = entry.Open();
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            },
            StringComparer.Ordinal);
    }

    private static byte[] WriteRawZip(
        IReadOnlyDictionary<string, byte[]> entries,
        CompressionLevel compressionLevel)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (var pair in entries.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(pair.Key, compressionLevel);
                using var stream = entry.Open();
                stream.Write(pair.Value, 0, pair.Value.Length);
            }
        }

        return output.ToArray();
    }

    private sealed class RejectAllCapabilities
        : IWorldExtensionCapabilityResolver
    {
        public bool IsApproved(
            string capabilityId,
            string requiredVersionRange)
        {
            return false;
        }
    }
}
