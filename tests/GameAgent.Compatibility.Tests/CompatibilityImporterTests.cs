using System.Buffers.Binary;
using System.Text;

namespace GameAgent.Compatibility.Tests;

public sealed class CompatibilityImporterTests
{
    [Fact]
    public void Version2CharacterCardMapsCanonicalFieldsAndPreservesExtensions()
    {
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardJson(Version2Json());

        Assert.True(result.Success);
        Assert.Equal(CompatibilityContentTrust.UntrustedData, result.ContentTrust);
        Assert.Equal("game-agent.character-json", result.AdapterId);
        Assert.Equal("1", result.AdapterVersion);
        Assert.Matches("^[0-9a-f]{64}$", result.SourceDigest!);
        Assert.Equal(CompatibilitySourceFormat.CharacterCardV2Json, result.Value!.SourceFormat);
        Assert.Equal("Ari", result.Value.Name);
        Assert.Equal("Keep the scene grounded.", result.Value.SystemPrompt);
        Assert.Equal("End on an actionable beat.", result.Value.PostHistoryInstructions);
        Assert.Equal("night", Assert.Single(result.Value.Tags));
        Assert.True(result.Value.PreservedFields.RootUnknownFields.ContainsKey("root_future"));
        Assert.True(result.Value.PreservedFields.ObjectUnknownFields.ContainsKey("data_future"));
        Assert.True(result.Value.PreservedFields.ExtensionFields.ContainsKey("sample/voice"));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "untrusted_content_data_only"
                          && diagnostic.Severity == CompatibilityDiagnosticSeverity.Warning);

        var loreBook = Assert.IsType<LoreBookDefinition>(result.Value.CharacterLoreBook);
        Assert.Equal(CompatibilitySourceFormat.LoreBookV2Embedded, loreBook.SourceFormat);
        var entry = Assert.Single(loreBook.Entries);
        Assert.True(entry.Activation.AlwaysActive);
        Assert.Equal(LoreBookPosition.BeforeCharacter, entry.Position);
        Assert.Equal("forest", Assert.Single(entry.Activation.PrimaryKeys));
        Assert.True(entry.PreservedFields.ExtensionFields.ContainsKey("future_entry"));
    }

    [Fact]
    public void Version3CharacterCardMapsAssetsDirectivesAndTimestampsWithoutFetching()
    {
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardJson(Version3Json());

        Assert.True(result.Success);
        Assert.Equal(CompatibilitySourceFormat.CharacterCardV3Json, result.Value!.SourceFormat);
        Assert.Equal("A", result.Value.Nickname);
        Assert.Equal("说明", result.Value.MultilingualCreatorNotes["zh"]);
        Assert.Equal("For the group.", Assert.Single(result.Value.GroupOnlyGreetings));
        Assert.Equal(
            CharacterAssetLocationKind.Https,
            Assert.Single(result.Value.Assets).LocationKind);
        Assert.NotNull(result.Value.CreatedAt);
        Assert.NotNull(result.Value.ModifiedAt);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "remote_assets_not_fetched");

        var entry = Assert.Single(result.Value.CharacterLoreBook!.Entries);
        Assert.Equal(
            CompatibilitySourceFormat.LoreBookV3Embedded,
            result.Value.CharacterLoreBook.SourceFormat);
        Assert.Equal(LoreBookMatchMode.RegularExpression, entry.Activation.MatchMode);
        Assert.Equal(0.75d, entry.Activation.Probability);
        Assert.Equal(3, entry.Activation.StickyTurns);
        Assert.Equal("gate", entry.Identifier);
        var directive = Assert.Single(entry.Directives);
        Assert.Equal("activate_only_every", directive.Name);
        Assert.Equal("2", directive.Value);
        Assert.Contains("@@activate_only_every 2", entry.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneVersion3LoreBookImports()
    {
        var importer = new CompatibilityImporter();
        var json =
            """
            {
              "spec": "lorebook_v3",
              "root_future": 1,
              "data": {
                "name": "Frontier",
                "scan_depth": 8,
                "token_budget": 512,
                "recursive_scanning": true,
                "extensions": { "book_future": true },
                "entries": [
                  {
                    "keys": ["harbor"],
                    "content": "The harbor closes at dusk.",
                    "extensions": {},
                    "enabled": true,
                    "insertion_order": 20,
                    "use_regex": false
                  }
                ]
              }
            }
            """;

        var result = importer.ImportLoreBookJson(json);

        Assert.True(result.Success);
        Assert.Equal(CompatibilitySourceFormat.LoreBookV3Json, result.Value!.SourceFormat);
        Assert.Equal(8, result.Value.ScanDepth);
        Assert.Equal(512, result.Value.TokenBudget);
        Assert.True(result.Value.RecursiveScanning);
        Assert.True(result.Value.PreservedFields.RootUnknownFields.ContainsKey("root_future"));
        Assert.True(result.Value.PreservedFields.ExtensionFields.ContainsKey("book_future"));
    }

    [Fact]
    public void ObjectMapLoreBookMapsActivationAndPreservesAdvancedMetadata()
    {
        var importer = new CompatibilityImporter();
        var json =
            """
            {
              "name": "Frontier",
              "entries": {
                "7": {
                  "uid": 7,
                  "key": ["gate.*"],
                  "keysecondary": ["moon"],
                  "content": "The gate opens.",
                  "constant": false,
                  "selective": true,
                  "selectiveLogic": 3,
                  "order": 250,
                  "position": 4,
                  "disable": false,
                  "probability": 40,
                  "useProbability": true,
                  "scanDepth": 6,
                  "caseSensitive": true,
                  "matchWholeWords": false,
                  "sticky": 2,
                  "cooldown": 5,
                  "delay": 1,
                  "vectorized": true,
                  "extensions": { "future": "kept" }
                }
              }
            }
            """;

        var result = importer.ImportLoreBookJson(json);

        Assert.True(result.Success);
        Assert.Equal(
            CompatibilitySourceFormat.LoreBookObjectMapJson,
            result.Value!.SourceFormat);
        var entry = Assert.Single(result.Value.Entries);
        Assert.Equal("7", entry.Identifier);
        Assert.Equal(LoreBookEntryIdentifierKind.Number, entry.IdentifierKind);
        Assert.Equal(LoreBookPosition.AtDepth, entry.Position);
        Assert.Equal(LoreBookMatchMode.RegularExpression, entry.Activation.MatchMode);
        Assert.Equal(LoreBookSecondaryKeyLogic.All, entry.Activation.SecondaryKeyLogic);
        Assert.Equal(0.4d, entry.Activation.Probability);
        Assert.Equal(6, entry.Activation.ScanDepth);
        Assert.Equal(2, entry.Activation.StickyTurns);
        Assert.True(entry.PreservedFields.ExtensionFields.ContainsKey("future"));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "advanced_semantics_preserved");
    }

    [Fact]
    public void PngPrefersVersion3PayloadAndDoesNotDecodePixels()
    {
        var png = PngBuilder.Build(
            ("chara", Version2Json()),
            ("ccv3", Version3Json()));
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardPng(png);

        Assert.True(result.Success);
        Assert.Equal(CompatibilitySourceFormat.CharacterCardV3Png, result.Value!.SourceFormat);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "secondary_payload_ignored");
    }

    [Fact]
    public void PngDoesNotDecodeIgnoredCompatibilityPayload()
    {
        var png = PngBuilder.BuildEncoded(
            ("chara", "not-base64"),
            ("ccv3", Convert.ToBase64String(Encoding.UTF8.GetBytes(Version3Json()))));
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardPng(png);

        Assert.True(result.Success);
        Assert.Equal(CompatibilitySourceFormat.CharacterCardV3Png, result.Value!.SourceFormat);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "secondary_payload_ignored");
    }

    [Fact]
    public void PngRejectsChecksumFailure()
    {
        var png = PngBuilder.Build(("chara", Version2Json()));
        png[^6] ^= 0x01;
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardPng(png);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "invalid_png_crc");
    }

    [Fact]
    public void PngRejectsPayloadIdentifierMismatch()
    {
        var png = PngBuilder.Build(("ccv3", Version2Json()));
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardPng(png);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "png_payload_format_mismatch");
    }

    [Fact]
    public void JsonRejectsDuplicateProperties()
    {
        var json =
            """
            {
              "spec": "chara_card_v2",
              "spec": "chara_card_v2",
              "spec_version": "2.0",
              "data": {}
            }
            """;
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardJson(json);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "duplicate_property");
    }

    [Fact]
    public void JsonRejectsExcessiveDepth()
    {
        var options = new CompatibilityImportOptions(maxJsonDepth: 4);
        var importer = new CompatibilityImporter(options);

        var result = importer.ImportLoreBookJson(
            """{"entries":{"0":{"content":{"too":{"deep":{"value":1}}}}}}""");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "invalid_json");
    }

    [Fact]
    public void LoreBookRejectsConfiguredEntryLimit()
    {
        var options = new CompatibilityImportOptions(maxLoreBookEntries: 1);
        var importer = new CompatibilityImporter(options);
        var json =
            """
            {
              "spec": "lorebook_v3",
              "data": {
                "entries": [
                  {
                    "keys": [],
                    "content": "",
                    "extensions": {},
                    "enabled": true,
                    "insertion_order": 1,
                    "use_regex": false
                  },
                  {
                    "keys": [],
                    "content": "",
                    "extensions": {},
                    "enabled": true,
                    "insertion_order": 2,
                    "use_regex": false
                  }
                ]
              }
            }
            """;

        var result = importer.ImportLoreBookJson(json);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "entry_limit_exceeded");
    }

    [Fact]
    public void JsonRejectsConfiguredTotalNodeLimit()
    {
        var options = new CompatibilityImportOptions(maxJsonNodes: 5);
        var importer = new CompatibilityImporter(options);

        var result = importer.ImportLoreBookJson(
            """{"entries":{"0":{"content":"","key":[],"keysecondary":[],"extensions":{}}}}""");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "node_limit_exceeded");
    }

    [Fact]
    public void PngRejectsDecodedPayloadLimitBeforeJsonParsing()
    {
        var png = PngBuilder.Build(("chara", Version2Json()));
        var options = new CompatibilityImportOptions(maxDecodedPayloadBytes: 32);
        var importer = new CompatibilityImporter(options);

        var result = importer.ImportCharacterCardPng(png);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "invalid_character_payload"
                          || diagnostic.Code == "decoded_payload_too_large");
    }

    [Fact]
    public void InvalidKnownFieldTypeIsRejectedWithoutCoercion()
    {
        var json = Version2Json().Replace(
            "\"name\": \"Ari\"",
            "\"name\": { \"unsafe\": true }",
            StringComparison.Ordinal);
        var importer = new CompatibilityImporter();

        var result = importer.ImportCharacterCardJson(json);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "invalid_field_type"
                          && diagnostic.Path == "$.data.name");
    }

    [Fact]
    public void StringInputRejectsInvalidUnicodeWithoutThrowing()
    {
        var importer = new CompatibilityImporter();
        var json = "{\"spec\":\"\ud800\"}";

        var result = importer.ImportCharacterCardJson(json);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "invalid_utf16");
    }

    [Fact]
    public void DeterministicMalformedByteInputsNeverEscapeAsExceptions()
    {
        var importer = new CompatibilityImporter();
        var random = new Random(741_991);

        for (var index = 0; index < 256; index++)
        {
            var bytes = new byte[random.Next(0, 4096)];
            random.NextBytes(bytes);

            var jsonException = Record.Exception(() => importer.ImportCharacterCardJson(bytes));
            var pngException = Record.Exception(() => importer.ImportCharacterCardPng(bytes));
            var loreException = Record.Exception(() => importer.ImportLoreBookJson(bytes));

            Assert.Null(jsonException);
            Assert.Null(pngException);
            Assert.Null(loreException);
        }
    }

    private static string Version2Json()
    {
        return
            """
            {
              "spec": "chara_card_v2",
              "spec_version": "2.0",
              "root_future": { "keep": true },
              "data": {
                "name": "Ari",
                "description": "A night watch keeper.",
                "personality": "Patient",
                "scenario": "At a forest gate.",
                "first_mes": "Who goes there?",
                "mes_example": "<START>",
                "creator_notes": "For readers.",
                "system_prompt": "Keep the scene grounded.",
                "post_history_instructions": "End on an actionable beat.",
                "alternate_greetings": ["The gate is closed."],
                "tags": ["night"],
                "creator": "Example",
                "character_version": "1",
                "extensions": {
                  "sample/voice": { "id": "quiet" }
                },
                "data_future": [1, 2],
                "character_book": {
                  "name": "Gate",
                  "extensions": {},
                  "entries": [
                    {
                      "keys": ["forest"],
                      "content": "The forest is old.",
                      "extensions": { "future_entry": true },
                      "enabled": true,
                      "insertion_order": 10,
                      "constant": true,
                      "position": "before_char"
                    }
                  ]
                }
              }
            }
            """;
    }

    private static string Version3Json()
    {
        return
            """
            {
              "spec": "chara_card_v3",
              "spec_version": "3.0",
              "data": {
                "name": "Ari",
                "description": "A night watch keeper.",
                "personality": "Patient",
                "scenario": "At a forest gate.",
                "first_mes": "Who goes there?",
                "mes_example": "<START>",
                "creator_notes": "For readers.",
                "system_prompt": "Keep the scene grounded.",
                "post_history_instructions": "End on an actionable beat.",
                "alternate_greetings": [],
                "group_only_greetings": ["For the group."],
                "tags": [],
                "creator": "Example",
                "character_version": "2",
                "extensions": {},
                "nickname": "A",
                "creator_notes_multilingual": { "zh": "说明" },
                "source": ["source:example"],
                "assets": [
                  {
                    "type": "icon",
                    "uri": "https://assets.invalid/icon.png",
                    "name": "main",
                    "ext": "png"
                  }
                ],
                "creation_date": 1700000000,
                "modification_date": 1700000100,
                "character_book": {
                  "extensions": {},
                  "entries": [
                    {
                      "id": "gate",
                      "keys": ["gate.*"],
                      "secondary_keys": [],
                      "content": "@@activate_only_every 2\nThe gate opens.",
                      "extensions": {
                        "probability": 75,
                        "use_probability": true,
                        "sticky": 3
                      },
                      "enabled": true,
                      "insertion_order": 20,
                      "use_regex": true
                    }
                  ]
                }
              }
            }
            """;
    }

    private static class PngBuilder
    {
        private static readonly byte[] Signature =
        {
            137,
            80,
            78,
            71,
            13,
            10,
            26,
            10,
        };

        internal static byte[] Build(params (string Keyword, string Json)[] payloads)
        {
            return BuildEncoded(
                payloads
                    .Select(payload =>
                        (
                            payload.Keyword,
                            Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Json))))
                    .ToArray());
        }

        internal static byte[] BuildEncoded(
            params (string Keyword, string EncodedPayload)[] payloads)
        {
            using var stream = new MemoryStream();
            stream.Write(Signature);
            WriteChunk(
                stream,
                "IHDR",
                new byte[]
                {
                    0, 0, 0, 1,
                    0, 0, 0, 1,
                    8,
                    6,
                    0,
                    0,
                    0,
                });
            foreach (var payload in payloads)
            {
                var data = Encoding.ASCII.GetBytes(
                    payload.Keyword + "\0" + payload.EncodedPayload);
                WriteChunk(stream, "tEXt", data);
            }

            WriteChunk(
                stream,
                "IDAT",
                new byte[]
                {
                    0x78, 0x9c, 0x63, 0x60, 0x60, 0x60,
                    0x00, 0x00, 0x00, 0x04, 0x00, 0x01,
                });
            WriteChunk(stream, "IEND", Array.Empty<byte>());
            return stream.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            stream.Write(length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
            stream.Write(crc);
        }

        private static uint Crc32(byte[] first, byte[] second)
        {
            var crc = uint.MaxValue;
            foreach (var value in first.Concat(second))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0
                        ? 0xedb88320U ^ (crc >> 1)
                        : crc >> 1;
                }
            }

            return crc ^ uint.MaxValue;
        }
    }
}
