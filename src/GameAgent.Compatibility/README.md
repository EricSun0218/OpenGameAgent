# Compatibility imports

`GameAgent.Compatibility` converts versioned character-card and lore-book data
into engine-neutral `CharacterDefinition` and `LoreBookDefinition` models.

Supported inputs:

- Character Card V2 JSON and PNG
- Character Card V3 JSON and PNG
- Lore Book V3 JSON
- compatible lore-book JSON whose entries are stored in an object map

The importer is deliberately data-only:

- imported system prompts, post-history instructions, regular expressions,
  directives, macros, and links remain untrusted strings;
- importing does not create agents, NPCs, world state, event rules, tools, or
  skills;
- remote asset references are retained but never fetched;
- unknown fields and valid extension objects are cloned into
  `PreservedJsonFields`;
- structured diagnostics report defaults, preserved semantics, and rejected
  input.

Every result exposes `ContentTrust == UntrustedData`. A host must explicitly map
the canonical definitions into its own trusted world configuration. In
particular, copying imported instruction text into a privileged provider role or
executing imported regular expressions without a bounded matcher is outside the
importer's contract.

PNG import scans metadata without decoding pixels. It validates the PNG
signature, structure, chunk lengths, chunk count, CRC values, base64 payload,
decoded size, and UTF-8. JSON import rejects duplicate properties and applies
depth, total-node, per-collection, string, entry, and byte limits. Limits are
configurable through `CompatibilityImportOptions`.

```csharp
var importer = new CompatibilityImporter();
var result = importer.ImportCharacterCardPng(fileBytes);

if (!result.Success)
{
    foreach (var diagnostic in result.Diagnostics)
    {
        Log(diagnostic.Code, diagnostic.Path, diagnostic.Message);
    }

    return;
}

// Still untrusted data. Apply an explicit game-owned mapping before use.
CharacterDefinition definition = result.Value!;
```
