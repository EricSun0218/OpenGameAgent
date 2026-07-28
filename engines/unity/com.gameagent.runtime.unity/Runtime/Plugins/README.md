# Generated managed dependencies

This directory is populated by `Build-UpmPackage.ps1` in the staged artifact.
The script copies the authoritative shared runtime assemblies; they are not
forked or reimplemented under `engines/unity`.

The staged set includes the protocol, core, persistence, composition builder,
and optional streaming provider adapter together with their managed
dependencies. `SHA256SUMS` covers every bundled DLL.

Do not publish the source template directly. Publish the assembled artifact
under `engines/unity/artifacts/com.gameagent.runtime.unity`.
