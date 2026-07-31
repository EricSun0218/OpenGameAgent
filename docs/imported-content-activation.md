# Imported character and lore activation

Imported character cards and lore books are untrusted authored data. Importing
them does not grant tools, skills, extensions, system-prompt authority, or
permission to mutate the world.

## Game-scoped keyword activation

Constant lore entries can activate without a search context. A keyed entry
requires an explicit `ImportedLoreActivationContext`:

```csharp
var context = new ImportedLoreActivationContext(
    scopeId: "npc:keeper",
    gameTimeId: "month:42",
    searchSegments: new[]
    {
        latestEventText,
        previousEventText
    },
    defaultScanDepth: 2,
    defaultCaseSensitive: false,
    defaultMatchWholeWords: false);

var activation = new ImportedRuntimeContentActivator()
    .ActivateLoreBook(
        "frontier-lore",
        importedLore,
        activationPolicy,
        context);
```

`scopeId` and `gameTimeId` are opaque game-owned coordinates. The runtime does
not substitute chat turns, wall-clock time, or model request count. Search
segments are a deliberate host projection from any structured game context;
the framework does not stringify arbitrary state or decide what an NPC may
observe. Segments are ordered newest first, copied defensively, and bounded by
count and UTF-8 byte limits.

The result exposes only the context digest and coordinates. Search text is not
copied into activated memory. The activation digest binds the context digest,
while an entry's memory ID remains stable across evaluations in the same
world, timeline, scope, session, and perspective.

## Phase-one matching semantics

The supported subset is deterministic:

- a constant, enabled entry activates without keyword evaluation;
- primary keys use `ANY` logic;
- a required secondary-key set uses `Any`, `All`, `NotAny`, or `NotAll`;
- an empty secondary-key set is ignored;
- matching uses ordinal case-sensitive or ordinal case-insensitive comparison;
- whole-word boundaries treat Unicode letters, digits, and `_` as word
  characters;
- entry scan depth overrides book scan depth, which overrides the activation
  context default;
- scan depth `0` scans no supplied segment, `1` scans the newest segment, and
  null scans every supplied segment.

Whole-word matching is normally unsuitable for scripts without word
separators, including most Chinese and Japanese text. Set the entry override or
the host default to `false` for those contexts.

No search context means keyed entries fail closed with
`KeywordContextRequired`; they are never activated merely because they contain
a key.

## Preserved but unsupported semantics

This compatibility phase does not execute regular-expression keys,
probability, sticky turns, cooldown turns, delayed activation, or recursive
scanning. An enabled entry whose activation depends on an unsupported
entry-level feature fails closed. Stable diagnostics identify each unsupported
field. Recursive book scanning is not attempted and produces a book-level
diagnostic; ordinary first-pass literal matching can still proceed.

Imported token budgets remain metadata because the trusted host context
compiler owns the authoritative budget. Imported directives and
instruction-shaped fields remain data only.

`ImportedKnowledgeEntryActivation.Decision` distinguishes constants, keyword
matches, missing context, primary misses, secondary rejection, disabled
entries, missing keys, and fail-closed unsupported semantics. This lets engine
tooling explain an activation without parsing diagnostic messages.

## Embedded lore

`ActivateCharacter` accepts the same optional activation context. The character
persona is activated independently, while embedded lore follows the exact
rules above. Omitting the context activates only supported constant embedded
entries.
