# Performance work (Unreleased)

Sparse MFTs previously required reading every valid slot, then retaining a dictionary and several objects per file. This change skips bitmap-empty ranges, overlaps reads and parsing, retains packed records/shared names/CSR children, and bulk-loads v3 snapshots. CLI/MCP accounting, pagination, canonical hard-link ownership, sequence-bearing IDs, and guarded deletion remain unchanged. Application/package version remains 1.0.1.

## Measurement method

Measured on the local elevated Windows NTFS host, .NET 10.0.3, 20 logical processors, approximately 32 GiB RAM. The baseline is an unmodified detached worktree at `fea0186` (HEAD, 1.0.1), published separately. Old/new executables used separate temporary data directories. `volumes` was called before scanning. No user files were deleted; deletion tests used newly created fixtures.

Each CLI timing covers process startup through exit. Peak working set was polled every 20 ms using Windows' process peak counter. Resident memory was sampled from a fresh MCP process after its first browse while it remained alive. “Cold browse” means a new process and an empty managed index cache. Windows filesystem cache was not flushed; these are not cold-device or reboot measurements. Values below use decimal MB (1,000,000 bytes). The old executable has no phase timers, so its read/parse/build/save phases are not presented as measured values.

| Metric | C: HEAD | C: new | D: HEAD | D: new |
| --- | ---: | ---: | ---: | ---: |
| scan_seconds | 19.168 | 5.457 | 1.364 | 0.698 |
| CLI scan total, seconds | 24.723 | 5.891 | 2.139 | 0.937 |
| Scan peak working set, MB | 1,742.63 | 1,435.00 | 281.39 | 340.77 |
| Snapshot size, MB | 106.83 | 277.74 | 11.83 | 46.33 |
| Cold-process CLI browse, seconds | 5.146 | 0.429 | 0.752 | 0.244 |
| Resident MCP working set, MB | 1,115.59 | 381.57 | 213.66 | 130.15 |
| Raw record slots read | 12,858,368 | 3,771,664 | 367,616 | 367,480 |

| New phase seconds | C: | D: |
| --- | ---: | ---: |
| read | 4.307 | 0.462 |
| parse | 0.200 | 0.069 |
| build | 1.114 | 0.213 |
| save | 0.158 | 0.027 |

`read` includes bootstrap plus the busiest reader's active raw-read time; `parse` is the busiest parser's active time. These overlap and exclude queue waiting, so they are diagnostic workload counters, not an additive timeline. Build and save are elapsed phases. `scan_seconds` measures the complete scan/pipeline/build and still excludes saving. Save timing is patched in the temporary header before atomic replacement and survives reload. Repeated default C: scans during tuning were approximately 5.2–5.5 seconds; working-set peaks vary with GC timing. D: uses more scan working set because bounded parallel buffers have a fixed cost, despite its much smaller resident index.

## Design and tradeoffs

- **Bitmap:** bootstrap unnamed `$BITMAP` (0xB0) attributes alongside `$DATA`, including attribute-list extension records. Support resident values and nonresident extent maps. Unavailable, truncated, sparse, or invalid bitmaps fall back to full MFT reads. Scan bitmap bytes in at least eight-record groups; merge gaps up to 8 KiB and split at MFT extents and 4 MiB read boundaries. On this C: the approach skips 70.7% of the total slots. Testing larger blocks/1 MiB gaps read about 6 million slots and missed the scan target, so smaller gaps were retained.
- **Pipeline:** two readers each own a volume handle/file pointer; bounded request and parsed-block queues limit live buffers. Two to six parser workers (five on this host) keep local segmented buffers and extension lists. Final records are ordered by MFT ordinal, then extension data is merged using the complete sequence-bearing base ID. Exceptions cancel/drain the pipeline and return pooled buffers.
- **Allocation:** attributes and runs use span-based value enumerators; parsing uses value records and allocates a filename list only for multiple names. Construction uses fixed segments rather than repeatedly copying growing arrays. Build uses loops and a temporary ordinal lookup. The resident index stores 41-byte records, deduplicated UTF-16 characters, integer parent/child indexes, directory-only totals, and pooled sparse hard-link names. Cached modification timestamps were not externally exposed; cleanup still validates the preview's timestamp against a live handle.
- **Queries:** case-insensitive Resolve compares pooled spans, aliases keep zero attributed allocation, and Largest traverses integer indexes. Directory totals retain one physical owner, include ADS/nonresident metadata, and preserve unresolved accounting. The temporary ordinal lookup is released after construction; no per-file dictionary remains resident.
- **Cleanup:** after the existing durable deletion/audit sequence, adjust only matching already-loaded snapshots. Check volume serial, disk snapshot timestamp/length, full file ID, selected name and expected remaining link count. Remove aliases without reclaiming physical bytes, transfer canonical ownership when needed, and deduct final-link totals along reachable ancestors. Save once per adjusted snapshot. Missing/stale/drifted indexes report a rescan through the existing `next_action` field. Tombstones preserve stable integer indexes; arrays/name-pool capacity is reclaimed by a new scan or cache release, rather than compacted per deletion.

## Compression comparison

The same representative C: v3 index was saved/reloaded with each codec. These are isolated Save/Load method timings in the same benchmark process after GC, with an already warm runtime, and are separate from the fresh-process CLI timings above.

| Codec | Save seconds | Load seconds | Size MB |
| --- | ---: | ---: | ---: |
| Uncompressed | 0.153 | 0.190 | 277.65 |
| ZLib Fastest | 0.699 | 0.489 | 99.92 |
| Brotli Fastest (low quality) | 1.148 | 0.705 | 79.63 |

**Default: uncompressed.** Bulk reads avoid decompression and achieve the lowest measured loading/saving time. Approximately 278 MB per multi-million-file C: snapshot is acceptable for the requested speed/memory targets, at the cost of a 2.6× larger file than the v2 GZip snapshot. v3 retains codec identifiers/readers so the comparison and roundtrip tests are reproducible. Saving uses a unique temporary file plus atomic Move; failed writes remove only their own temporary file. v1/v2 object-graph loading is deliberately not carried forward: old snapshots return `快照格式已过期，请重新 disk_scan`, and volumes report `snapshot_format_current: false`.

## Runtime settings

Tested environment overrides on the segmented-buffer implementation. Single-run results are diagnostic; the earlier growing-buffer implementation showed a small Server GC improvement which did not persist after allocation changes.

| Configuration | C: scan seconds | Scan peak MB | Resident MB |
| --- | ---: | ---: | ---: |
| Defaults | 5.189 | 1,454.88 | 381.29 |
| Server GC | 5.256 | 1,461.21 | 388.73 |
| Server GC, 4 heaps | 5.280 | 1,563.91 | 382.41 |
| Nonconcurrent GC | 5.463 | 1,473.14 | 381.60 |
| Tiered PGO disabled | 5.529 | 1,432.01 | 375.71 |

**No csproj runtime overrides retained.** Server GC/heap caps/concurrent-GC changes showed no stable benefit after the structural changes. Keep the runtime defaults, including enabled Tiered PGO, rather than adding configuration that changes memory/pause behavior on other machines.

## Equivalence and tests

D: indexed 365,092, invalid 0, unresolved 4 on both builds. Root allocated/logical/files/directories all matched exactly. Root and three child directories contained 20/9/20/20 compared rows; every requested name/allocated_bytes/logical_bytes/files value matched.

Final C: scans differed by 11 indexed records (old 3,493,980; new 3,493,991), with unchanged invalid 0/unresolved 4. Root deltas were +257,134,592 allocated bytes, +256,951,122 logical bytes, +6 files, +5 directories. Root/three child pages differed in 4/1/0/1 rows out of 20/8/20/20. Earlier paired results showed a one-record/+1.23 MB allocated difference. Both scans read the active system volume at different times; these are observed differences, not a claim of VSS consistency. Baseline C: scan times across runs ranged from about 17.0 to 20.3 seconds. Larger logical differences from newly created 128 MiB sparse fixtures were excluded by creating all fixtures before the final paired scans.

Core tests: 92 checks passed in a new temporary directory. Added synthetic bitmap segments/fallback failures, segmented-buffer boundaries, base/extension name merging and original direct/ioctl traversal/name ordering, compact Resolve/ListChildren/PathOf/Largest and browse paging/omitted totals, high alias-count hashing, same-parent and unresolved canonical transfers, all three v3 codec roundtrips/timings, legacy-format rejection, partial/all hard-link deletion, ancestor totals/empty directories, persisted adjusted indexes, stale disk snapshots, external link-count drift and unloaded-index rescan behavior. Existing native ADS, sparse, Unicode, 512/4K fixup, and cleanup safety tests remain passing.

The published executable passed all 24 package/stdio smoke checks, including deletion, replay refusal, read-only mode and clean shutdown. All full-volume benchmark snapshots/path-bearing outputs stay outside the repository and are deleted after measurement. This host exercised the direct backend and native cooked-record parsing; a storage stack that actually refuses raw reads, a mechanical disk, and a native-sector-4K physical volume were not available for full-scan measurements. `docs/VALIDATION.md` release numbers are unchanged.
