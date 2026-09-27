# @opengameagent/resources

Bounded immutable storage for exact original bytes. Content identity is the SHA-256 of the bytes and deliberately
contains no path, MIME type, role, owner, or media-generation lineage.

`LocalImmutableResourceStore` accepts eager bytes, an async byte stream, or a lazy reader with a declared exact length.
Every publish and read requires an explicit byte budget and `AbortSignal`. Length is admitted before a lazy reader is
opened, stream chunks are charged before copying, empty resources are valid, and reads verify the complete length and
digest before returning bytes.

Local publication writes and flushes a private temporary file on the same filesystem, then uses a no-clobber hard link
to make the digest path atomically visible. A concurrent existing digest succeeds only after a complete verified read;
a damaged object is never overwritten or repaired. Unix shard directories are synchronized after publication. Pure
Node.js does not expose equivalent directory-entry synchronization on Windows, so Windows provides atomic visibility
plus a pre-publication file-flush request and OS/device-specific best-effort durability. Filesystems without compatible
hard-link behavior fail closed as storage unavailable; the Store never falls back to copy-and-overwrite.

The storage root is a private trusted-host boundary. Node.js does not expose dirfd-relative `openat`/`linkat`; a host
that must defend against concurrent same-principal retargeting or requires stronger crash durability supplies another
`ImmutableResourceStore` Adapter. This package has no authorization, MIME inspection, decoding, automatic retention,
retry, repair, background work, or game-import authority.

`LocalImmutableResourceStore.collectGarbage` performs explicit, host-owned maintenance. Its required request is:

```ts
await store.collectGarbage({
  beforeModifiedAt: hostCutoff, // canonical UTC timestamp, strict modification-time cutoff
  limit: hostDeletionLimit, // combined object and crash-temporary deletions
  maxScanEntries: hostScanBudget, // every directory entry inspected
  retainSha256: allLiveDigests,
  retainBudget: { maxCount: hostReferenceCountBudget, maxBytes: hostReferenceUtf8Budget },
  signal,
});
```

The host must first stop and drain resource users and every publication/reference writer across all Store instances
and processes, then take a complete retention snapshot. Include resources in active work, retained media jobs,
transcripts, durable archives, and external game references. Keep that maintenance exclusion until collection finishes;
an age cutoff cannot make a missing or concurrently changing reference list safe. A retained digest protects the one
shared object regardless of how many references or media slots use its bytes. No references are inferred from filenames.

All retain entries are counted and charged by total UTF-8 bytes before copying; an invalid, duplicate, or over-budget
list fails before filesystem work. The Store buffers one directory entry at a time, traverses only its two digest-shard
levels, and records at most `limit` candidates. It completes the scan before deleting anything. Exceeding
`maxScanEntries` raises `ImmutableResourceCapacityError("scan")` with zero deletions; the host can retry with a larger
explicit scan budget. There is no ordering guarantee among equally eligible files.

Only unretained digest objects and recognized UUID-v4 `.tmp` crash files older than the cutoff are eligible. Unknown
files and directories, newer files, and live digests are preserved. Collection never recursively deletes directories
or follows a linked shard/file. Root and file identity are rechecked before each unlink. Calls overlapping an active
read/publication or another collection on the same instance fail with `storage_busy`; the host owns cross-instance and
cross-process exclusion. The private-root trust limitation above also applies to unlink because Node.js lacks `unlinkat`.

The content-free result contains `scannedEntries`, `deletedObjects`, `deletedTemporaryFiles`, and exact decimal-string
`deletedBytes`. Abort or I/O failure can occur after some unlinks; completed deletions remain, and repeating the request
is safe once the host verifies the same complete reference snapshot. Referenced objects are never deleted, even if
corrupt; reads report integrity failures and the host owns repair.
