# @opengameagent/memory-usearch

Optional benchmark-selected USearch HNSW accelerator. It keeps one derived index per owner/timeline/generation partition and can be replaced atomically from authoritative memory records. Mutations within one partition are serialized so updates arriving during a rebuild are applied after the replacement snapshot instead of being lost; searches may continue against the prior complete index until the swap.

Hosts must explicitly supply HNSW connectivity, construction expansion, and search expansion. Zero deliberately selects the locked USearch dependency's upstream value; explicit connectivity starts at two, matching the dependency's accepted domain. Other valid nonzero values remain host-selected workload parameters rather than OpenGameAgent product defaults. The checked-in memory recall profiles show reproducible acceptance configurations for their synthetic workloads.

To preserve completed indexes across process restarts, configure `persistence: { directory, maxSnapshotBytes }`.
The directory must be a real, exclusive, host-controlled directory, retained alongside the SQLite authority; it must not
be shared by concurrent index owners or modified by untrusted processes. Omitting persistence keeps the index in memory.
The host selects the snapshot byte budget. USearch writes a native temporary file before its size can be checked; oversized
checkpoints fail and remove that temporary file, preserving the serving index. Restores check size before reading native data.

`GameMemory` uses native USearch save/load through `replace(partition, entries, revision)`, `checkpoint(partition, revision)`
and `restore(partition, revision)`. A replacement is only a checkpoint candidate until GameMemory confirms scan completeness
and an unchanged authority revision. Mutations invalidate candidates. Snapshot keys bind model identity, dimensions, HNSW
configuration, and owner/timeline/generation. Files contain vectors and keys, so hosts must protect this directory like the
authority database. Fixed-size metadata binds the authority revision and SHA-256 of native bytes; missing, mismatched or
corrupt derived data cannot be accepted as a ready index. Model identity must change whenever embedding semantics change.

Snapshot publication uses temporary files and atomic renames. An interrupted publication leaves a missing or mismatched
derived snapshot, which is rebuilt from authority data. Normal checkpoints remove their temporary files. Hosts may remove
the entire disposable cache while the index is stopped, including crash temporaries and snapshots for retired identities;
this never removes authoritative memories or their persisted embeddings.
