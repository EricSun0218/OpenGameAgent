# Native world v1 fixtures

`interactive-smoke` is a complete, engine-neutral authored package. It contains
only the seven semantic files accepted by `NativeWorldPackageCompiler`.

The fixture demonstrates:

- two NPC identities and two matching agent-content entries;
- one knowledge entry;
- a typed, single-target interaction;
- fixed-point numeric effects represented by canonical integer strings;
- a discrete monthly clock and a deterministic monthly event; and
- explicit catalog paths in `world.json`.

The world test loads these files from disk, compiles them in different file
orders, activates an in-memory runtime, executes the interaction, advances one
month, and checks the resulting authoritative state and stable digests.
