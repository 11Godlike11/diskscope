# Performance work (1.1.0)

Sparse MFTs previously required reading every valid slot, then retaining a dictionary and several objects per file. This change skips bitmap-empty ranges, overlaps reads and parsing, retains packed records/shared names/CSR children, and bulk-loads v3 snapshots. CLI/MCP accounting, pagination, canonical hard-link ownership, sequence-bearing IDs, and guarded deletion remain unchanged. The scan pipeline and retained arrays from 566ea21 remain in 1.1.0; compact presentation and default compression are described in the round-two section below.

## Round-one measurement method

Measured on the local elevated Windows NTFS host, .NET 10.0.3, 20 logical processors, approximately 32 GiB RAM. The round-one baseline was an unmodified detached worktree at `fea0186` (1.0.1), published separately. Old/new executables used separate temporary data directories. `volumes` was called before scanning. No user files were deleted; deletion tests used newly created fixtures.

Each CLI timing covers process startup through exit. Peak working set was polled every 20 ms using Windows' process peak counter. Resident memory was sampled from a fresh MCP process after its first browse while it remained alive. “Cold browse” means a new process and an empty managed index cache. Windows filesystem cache was not flushed; these are not cold-device or reboot measurements. Values below use decimal MB (1,000,000 bytes). The old executable has no phase timers, so its read/parse/build/save phases are not presented as measured values.

| Metric | C: fea0186 | C: 566ea21 | D: fea0186 | D: 566ea21 |
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

**Default in 1.1.0: ZLib Fastest.** The representative index shrinks from 277.65 MB to 99.92 MB (64.0%), trading approximately 0.30 s of loading and 0.55 s of saving for substantially less disk space. The packed resident arrays and scan pipeline stay unchanged. Uncompressed v3 (the 566ea21 default) remains readable through the codec flag. v3 retains codec identifiers/readers so the comparison and roundtrip tests are reproducible. Saving uses a unique temporary file plus atomic Move; failed writes remove only their own temporary file. v1/v2 object-graph loading is deliberately not carried forward: old snapshots return `快照格式已过期，请重新 disk_scan`, and volumes report `snapshot_format_current: false`.

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

Round-one core tests: 92 checks passed in a new temporary directory. Added synthetic bitmap segments/fallback failures, segmented-buffer boundaries, base/extension name merging and original direct/ioctl traversal/name ordering, compact Resolve/ListChildren/PathOf/Largest and browse paging/omitted totals, high alias-count hashing, same-parent and unresolved canonical transfers, all three v3 codec roundtrips/timings, legacy-format rejection, partial/all hard-link deletion, ancestor totals/empty directories, persisted adjusted indexes, stale disk snapshots, external link-count drift and unloaded-index rescan behavior. Existing native ADS, sparse, Unicode, 512/4K fixup, and cleanup safety tests remain passing.

The round-one published executable passed all 24 package/stdio smoke checks, including deletion, replay refusal, read-only mode and clean shutdown. All full-volume benchmark snapshots/path-bearing outputs stay outside the repository and are deleted after measurement. This host exercised the direct backend and native cooked-record parsing; a storage stack that actually refuses raw reads, a mechanical disk, and a native-sector-4K physical volume were not available for full-scan measurements. `docs/VALIDATION.md` release numbers are unchanged.


## Round two: 566ea21 versus 1.1.0 (2026-10-12)

The unmodified `566ea21` executable was published from a detached temporary worktree. Each build used its own new temporary `--data-dir`; `volumes` was called before scans. Five scan samples per build/volume were interleaved, with three fresh-process browse samples. Medians include every sample, including the I/O stalls below. These measurements retain the Windows filesystem cache; “cold” means a new process/empty managed cache, not a cold device. Memory is working set sampled in a fresh, still-running MCP process immediately after root browse. Sizes are decimal MB. No user files were cleaned; snapshots and path-bearing results remained outside the repository and were removed after validation.

| Metric | C: 566ea21 | C: 1.1.0 | D: 566ea21 | D: 1.1.0 |
| --- | ---: | ---: | ---: | ---: |
| scan_seconds, median | 6.064 | 6.091 | 0.735 | 0.819 |
| CLI scan total, median seconds | 6.637 | 7.510 | 1.013 | 1.198 |
| Save, median seconds | 0.185 | 0.913 | 0.040 | 0.133 |
| Index size, MB | 278.40 | 100.26 | 46.33 | 12.72 |
| Cold-process CLI browse, median seconds | 0.494 | 0.778 | 0.295 | 0.353 |
| Resident MCP working set, MB | 382.49 | 383.46 | 130.09 | 131.09 |

C: loading adds 0.284 seconds, consistent with the approximately 0.30-second decompression tradeoff. Disk size decreases by 64.0% on C: and 72.5% on D:. Resident memory increases by about 1 MB (0.3%/0.8%), with the same retained packed arrays. Scan medians differ by +0.027 seconds on C: (0.4%) and +0.084 seconds on D: (11.4%); the D: absolute change is small but is reported rather than called an improvement. Saving adds about 0.73 seconds on C: and 0.09 seconds on D:, separate from scan_seconds. `NtfsScanner.cs`, `Native.cs`, and guarded cleanup execution are unchanged in this round.

Observed scan samples (seconds):

| Volume/build | All five samples |
| --- | --- |
| C: 566ea21 | 6.211, 6.064, 5.940, 5.553, 26.047 |
| C: 1.1.0 | 6.073, 6.029, 47.831, 6.091, 77.178 |
| D: 566ea21 | 0.816, 0.735, 0.736, 0.700, 0.713 |
| D: 1.1.0 | 0.728, 0.819, 0.824, 0.885, 0.686 |

The C: 47.831/77.178-second runs spent 46.055/75.804 seconds reading; parsing took 0.258/0.187 seconds and building 1.696/1.308 seconds. The baseline 26.047-second run also spent 24.550 seconds reading. A system sample after scanning still showed about 54 MB/s C: reads and 13,656 pages/s input, while these benchmark executables were no longer scanning. This supports an inference of host I/O/paging interference; its source was not traced. The medians and unchanged pipeline show no material C: scan/resident regression in ordinary runs, but these measurements cannot establish a noise-free or worst-case latency guarantee, or exclude the small D: timing difference.

### JSON bytes

Measure UTF-8 CLI JSON bytes, excluding the final newline, using the same saved index and `browse <root> --limit 20` with/without `--verbose`. Both pages contain 20 rows; no filenames or full responses are recorded here.

| Root | Compact bytes | Verbose bytes | Reduction |
| --- | ---: | ---: | ---: |
| C: | 3,831 | 8,707 | 56.0% |
| D: | 3,736 | 8,684 | 57.0% |

The complete verbose root response matched 566ea21 exactly when both executables loaded the same uncompressed v3 snapshot, for both volumes. Conditional logical sizes, hard links/reparse flags, alias attribution, allocation shares, pagination totals, and full compatibility fields have core regressions. Generic accounting/workflow text is sent through initialization/tool descriptions instead of repeated query guidance.

### Type summary

After loading the index in a fresh MCP process, `disk_types` on `C:\Users` covered **2,437,578 canonical files**. Three complete request/response timings were **0.336, 0.251, 0.116 seconds** (median **0.251 seconds**), including aggregation, category classification, example path reconstruction and JSON output. This host's actual subtree is 2.44 million files rather than exactly 3 million; it is comfortably below the requested 1–2-second range. Integer-index traversal and pooled case-insensitive extension slices allocate one key/aggregate per distinct extension, not one string per file; categories are assigned once per distinct extension.

### Round-two checks

Core tests: **127 checks** in a newly created temporary directory. Added compact conditional-field/threshold rules, parent/subtree shares, alias physical ownership, verbose browse/detail/largest, category/no-extension/case-folding/canonical-link summaries, default ZLib v3 header/roundtrip and temporary-file cleanup, zero/early/timeout waits, wait bounds and failure handling. Existing explicit codec-0 tests verify uncompressed v3 reading; prior MFT and deletion safety checks remain intact.

Packaged MCP/CLI smoke: **44 checks**, including 13/9 tool discovery, all-tool verbose schemas, read-only type summaries, waiting ping/tools-list responsiveness, concurrent waits, progress token/strictly increasing notifications, no unsolicited progress, early preview completion, completed-job immediate return, wait bounds, complete verbose rows/scan-job results, exact temporary-fixture deletion/audit/replay refusal, and stdin-close job/wait draining. The final portable package is generated by `scripts/package.ps1` with default version 1.1.0. Full-volume fixtures, indexes and benchmark scripts are removed from their owned temporary directories after measurement.
