using System.Text;
using System.Text.Json;
using GameAgent.Compatibility;
using GameAgent.Runtime;
using GameAgent.World;

namespace GameAgent.Tests;

public sealed class NativeWorldImportComposerTests
{
    [Fact]
    public void ComposerRequiresExplicitUntrustedDataAcceptance()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var composer = new NativeWorldImportComposer("sample", "1");

        Assert.True(import.Success);
        Assert.Throws<InvalidOperationException>(
            () => composer.AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.Reject));
    }

    [Fact]
    public void CharacterAndKnowledgeComposeIntoDeterministicNativePackage()
    {
        var importer = new CompatibilityImporter();
        var character = importer.ImportCharacterCardJson(CharacterJson());
        var lore = importer.ImportLoreBookJson(LoreJson());

        var first = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                character,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .AddLoreBook(
                "world",
                lore,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .AddAgentBinding(
                "keeper-agent",
                "actor",
                new[] { "world" },
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var second = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                character,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .AddLoreBook(
                "world",
                lore,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .AddAgentBinding(
                "keeper-agent",
                "actor",
                new[] { "world" },
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();

        Assert.Equal(first.PackageDigest, second.PackageDigest);
        Assert.Equal(5, first.Files.Count);
        var characterFile = Assert.Single(
            first.Files,
            file => file.Path == "content/characters/actor.json");
        using var document = JsonDocument.Parse(
            characterFile.GetContentCopy());
        var root = document.RootElement;
        Assert.Equal(
            "untrusted_data",
            root.GetProperty("contentTrust").GetString());
        Assert.Equal(
            "Untrusted instruction text.",
            root.GetProperty("authoredContext")
                .GetProperty("systemPrompt")
                .GetString());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("skills", out _));
        Assert.False(root.TryGetProperty("events", out _));
        var diagnosticsFile = Assert.Single(
            first.Files,
            file => file.Path
                    == "imports/character-actor.diagnostics.json");
        using var diagnosticsDocument = JsonDocument.Parse(
            diagnosticsFile.GetContentCopy());
        Assert.Equal(
            "game-agent.import-diagnostics.v2",
            diagnosticsDocument.RootElement
                .GetProperty("contract")
                .GetString());
        Assert.Equal(
            character.SourceDigest,
            diagnosticsDocument.RootElement
                .GetProperty("sourceDigest")
                .GetString());
        Assert.Matches(
            "^[0-9a-f]{64}$",
            diagnosticsDocument.RootElement
                .GetProperty("normalizedContentDigest")
                .GetString()!);
        Assert.NotEqual(
            character.SourceDigest,
            diagnosticsDocument.RootElement
                .GetProperty("normalizedContentDigest")
                .GetString());

        using var archive = new MemoryStream();
        WorldPackageArchive.Write(archive, first);
        archive.Position = 0;
        var restored = WorldPackageArchive.Read(archive);
        Assert.Equal(first.PackageDigest, restored.PackageDigest);
        var content = new ImportedWorldPackageContentReader().Read(restored);
        var restoredCharacter = Assert.Single(content.Characters);
        var restoredLore = Assert.Single(content.LoreBooks);
        var binding = Assert.Single(content.AgentBindings).Value;
        Assert.Equal("actor", restoredCharacter.Key);
        Assert.Equal("Ari", restoredCharacter.Value.Value!.Name);
        Assert.Equal("world", restoredLore.Key);
        Assert.Equal(
            "The harbor closes.",
            Assert.Single(restoredLore.Value.Value!.Entries).Content);
        Assert.Equal("keeper-agent", binding.AgentId);
        Assert.Equal("actor", binding.CharacterContentId);
        Assert.Equal("world", Assert.Single(binding.LoreContentIds));

        var policy = new ImportedRuntimeActivationPolicy(
            ImportedContentAcceptance.AcceptAsUntrustedData,
            "world-1",
            "npc:keeper",
            new DateTimeOffset(
                2026,
                1,
                2,
                3,
                4,
                5,
                TimeSpan.Zero));
        var activator = new ImportedRuntimeContentActivator();
        var characterContentId = binding.CharacterContentId
                                 ?? throw new InvalidOperationException(
                                     "Character binding is missing.");
        var agent = activator.ActivateCharacter(
            binding.AgentId,
            content.Characters[characterContentId],
            policy);
        var loreContentId = Assert.Single(binding.LoreContentIds);
        var knowledge = activator.ActivateLoreBook(
            loreContentId,
            content.LoreBooks[loreContentId],
            policy,
            new ImportedLoreActivationContext(
                "world-1:keeper",
                "turn-1",
                new[] { "The harbor is nearby." }));
        Assert.Contains(
            "A keeper.",
            agent.PersonaContext.Content!.Value.GetRawText(),
            StringComparison.Ordinal);
        Assert.Single(knowledge.Memories);
    }

    [Fact]
    public void ReaderRejectsIdentityTamperAndUnknownImportedShape()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var package = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var files = package.Files
            .Select(
                file => file.Path == "content/characters/actor.json"
                    ? new WorldPackageFile(
                        file.Path,
                        file.MediaType,
                        Encoding.UTF8.GetBytes(
                            Encoding.UTF8
                                .GetString(file.GetContentCopy())
                                .Replace(
                                    "\"contentId\":\"actor\"",
                                    "\"contentId\":\"other\"",
                                    StringComparison.Ordinal)))
                    : file)
            .ToArray();
        var tampered = new WorldPackageDefinition(
            package.PackageId,
            package.ContentVersion,
            files);

        var exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(tampered));
        Assert.Equal(
            ImportedWorldPackageContentReasonCodes.InvalidReference,
            exception.ReasonCode);

        var unknown = new WorldPackageDefinition(
            "sample",
            "1",
            package.Files.Concat(
                new[]
                {
                    new WorldPackageFile(
                        "content/characters/readme.txt",
                        "text/plain",
                        Encoding.UTF8.GetBytes("inert"))
                }));
        exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(unknown));
        Assert.Equal(
            ImportedWorldPackageContentReasonCodes.UnknownFile,
            exception.ReasonCode);
    }

    [Fact]
    public void ReaderRejectsDuplicateJsonProperties()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var package = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var files = package.Files
            .Select(
                file => file.Path == "content/characters/actor.json"
                    ? new WorldPackageFile(
                        file.Path,
                        file.MediaType,
                        Encoding.UTF8.GetBytes(
                            Encoding.UTF8
                                .GetString(file.GetContentCopy())
                                .Replace(
                                    "\"contentTrust\":\"untrusted_data\"",
                                    "\"contentTrust\":\"untrusted_data\","
                                    + "\"contentTrust\":\"untrusted_data\"",
                                    StringComparison.Ordinal)))
                    : file)
            .ToArray();

        var exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(
                new WorldPackageDefinition("sample", "1", files)));
        Assert.Equal(
            ImportedWorldPackageContentReasonCodes.InvalidShape,
            exception.ReasonCode);
    }

    [Fact]
    public void ReaderRejectsNormalizedContentTamperWithOldProvenance()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var package = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var files = package.Files
            .Select(
                file => file.Path == "content/characters/actor.json"
                    ? ReplaceFileText(
                        file,
                        "\"description\":\"A keeper.\"",
                        "\"description\":\"Tampered persona.\"")
                    : file)
            .ToArray();

        var exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(
                new WorldPackageDefinition("sample", "1", files)));

        Assert.Equal(
            ImportedWorldPackageContentReasonCodes
                .NormalizedContentDigestMismatch,
            exception.ReasonCode);
        Assert.DoesNotContain(
            "Tampered persona",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderRejectsLegacyWeakDiagnosticsContract()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var package = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var files = package.Files
            .Select(
                file => file.Path
                        == "imports/character-actor.diagnostics.json"
                    ? ReplaceFileText(
                        file,
                        "\"contract\":\"game-agent.import-diagnostics.v2\"",
                        "\"contract\":\"game-agent.import-diagnostics.v1\"")
                    : file)
            .ToArray();

        var exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(
                new WorldPackageDefinition("sample", "1", files)));

        Assert.Equal(
            ImportedWorldPackageContentReasonCodes.InvalidShape,
            exception.ReasonCode);
    }

    [Fact]
    public void ReaderRejectsMissingNormalizedContentDigest()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var package = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var files = package.Files
            .Select(
                file => file.Path
                        == "imports/character-actor.diagnostics.json"
                    ? RemoveJsonStringProperty(
                        file,
                        "normalizedContentDigest")
                    : file)
            .ToArray();

        var exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(
                new WorldPackageDefinition("sample", "1", files)));

        Assert.Equal(
            ImportedWorldPackageContentReasonCodes.InvalidShape,
            exception.ReasonCode);
    }

    [Fact]
    public void ReaderMapsInvalidContentIdToStableShapeReason()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var package = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var files = package.Files
            .Select(
                file => file.Path == "content/characters/actor.json"
                    ? ReplaceFileText(
                        file,
                        "\"contentId\":\"actor\"",
                        "\"contentId\":\"../actor\"")
                    : file)
            .ToArray();

        var exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(
                new WorldPackageDefinition("sample", "1", files)));

        Assert.Equal(
            ImportedWorldPackageContentReasonCodes.InvalidShape,
            exception.ReasonCode);
        Assert.DoesNotContain(
            "../actor",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderMapsInvalidDiagnosticsPathToReferenceReason()
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var package = new NativeWorldImportComposer("sample", "1")
            .AddCharacter(
                "actor",
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData)
            .Build();
        var files = package.Files
            .Select(
                file => file.Path
                        == "imports/character-actor.diagnostics.json"
                    ? new WorldPackageFile(
                        "imports/character-..diagnostics.json",
                        file.MediaType,
                        file.GetContentCopy())
                    : file)
            .ToArray();

        var exception = Assert.Throws<
            ImportedWorldPackageContentException>(
            () => new ImportedWorldPackageContentReader().Read(
                new WorldPackageDefinition("sample", "1", files)));

        Assert.Equal(
            ImportedWorldPackageContentReasonCodes.InvalidReference,
            exception.ReasonCode);
        Assert.DoesNotContain(
            "character-.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../actor")]
    [InlineData("actor/name")]
    [InlineData("角色")]
    public void ComposerRejectsNonPortableContentIds(string contentId)
    {
        var import = new CompatibilityImporter()
            .ImportCharacterCardJson(CharacterJson());
        var composer = new NativeWorldImportComposer("sample", "1");

        Assert.Throws<ArgumentException>(
            () => composer.AddCharacter(
                contentId,
                import,
                ImportedContentAcceptance.AcceptAsUntrustedData));
    }

    private static WorldPackageFile ReplaceFileText(
        WorldPackageFile file,
        string oldValue,
        string newValue)
    {
        var original = Encoding.UTF8.GetString(file.GetContentCopy());
        var replaced = original.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal);
        Assert.NotEqual(original, replaced);
        return new WorldPackageFile(
            file.Path,
            file.MediaType,
            Encoding.UTF8.GetBytes(replaced));
    }

    private static WorldPackageFile RemoveJsonStringProperty(
        WorldPackageFile file,
        string propertyName)
    {
        var original = Encoding.UTF8.GetString(file.GetContentCopy());
        var marker = ",\"" + propertyName + "\":\"";
        var start = original.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = original.IndexOf(
            '"',
            start + marker.Length);
        Assert.True(end > start);
        var replaced = original.Remove(start, end - start + 1);
        return new WorldPackageFile(
            file.Path,
            file.MediaType,
            Encoding.UTF8.GetBytes(replaced));
    }

    private static string CharacterJson()
    {
        return
            """
            {
              "spec": "chara_card_v2",
              "spec_version": "2.0",
              "data": {
                "name": "Ari",
                "description": "A keeper.",
                "personality": "Patient",
                "scenario": "At a gate.",
                "first_mes": "Hello.",
                "mes_example": "<START>",
                "creator_notes": "",
                "system_prompt": "Untrusted instruction text.",
                "post_history_instructions": "",
                "alternate_greetings": [],
                "tags": ["keeper"],
                "creator": "Example",
                "character_version": "1",
                "extensions": {},
                "character_book": {
                  "extensions": {},
                  "entries": [
                    {
                      "keys": ["gate"],
                      "content": "The gate closes.",
                      "extensions": {},
                      "enabled": true,
                      "insertion_order": 1,
                      "constant": true
                    }
                  ]
                }
              }
            }
            """;
    }

    private static string LoreJson()
    {
        return
            """
            {
              "name": "World",
              "entries": {
                "1": {
                  "uid": 1,
                  "key": ["harbor"],
                  "content": "The harbor closes.",
                  "constant": true,
                  "selective": false,
                  "order": 2,
                  "position": 0,
                  "disable": false,
                  "extensions": {}
                }
              }
            }
            """;
    }
}
