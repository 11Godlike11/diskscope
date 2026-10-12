# Cleanup and accounting

## Authorization and permanence

DiskScope is a privileged local tool when run elevated. Client prompts and review rules remain responsible for the user's intended scope. The server cannot independently prove that an AI obtained authorization. A plan token matches an exact preview; it is not a consent credential.

Execution permanently deletes files and bypasses the Recycle Bin. There is no rollback and no transaction spanning the file set. Back up valuable data through an appropriate independent workflow before deciding to delete it.

## Preview and execution

The preview stores explicit selected paths and an exact file set with volume serial number, file ID, logical size, allocation, modification time, and link count. It expires after one hour. New files are not added at execution time; directories remain.

Execution performs a full preflight, then checks each file again using the same open handle used to mark deletion. Ancestors are opened with directory-list/read-attributes access and read/write sharing, without delete sharing, to block rename/delete; reparse points are refused. Preview and deletion both resolve the file handle's final DOS path, recheck protected-path rules, and require it to match the expected path (case-insensitive, ignoring the extended-path prefix). The file handle denies write and delete sharing while validating and deleting. This is intended to prevent ordinary rename/replacement races through that path.

Deletion prefers `FileDispositionInfoEx` with delete, POSIX semantics, and ignore-readonly flags, allowing read-only files to be deleted without changing their attributes. If Windows reports invalid parameter, not supported, or invalid function, execution falls back to `FileDispositionInfo`; read-only files can still fail on that legacy path.

An exclusive audit-file creation prevents the same plan from executing twice, including across processes. Every delete intent and completed deletion is flushed to a local audit log. Execution stops on its first file error. After interruption, an intent without completion can have an uncertain outcome: inspect current paths and create a new plan for remaining authorized files rather than replaying the old one.

## Protected selections

DiskScope refuses volume roots, whole user profiles, system/application directories, its own executable/state directories, core volume files, and paths through directory junctions/symbolic links. Protections are deliberately conservative and do not identify every important application directory. Ordinary project folders, game data, databases, and personal files still require ownership/use verification before deletion. Uninstall applications and service Windows components through their supported interfaces.

Cleanup also refuses alternate-stream selections and UNC paths. The tool does not bypass Windows ACLs, automatically elevate, or gain access to locked files.

## Size semantics

- `allocated_bytes`: snapshot physical extents attributed once per MFT record, including alternate streams and nonresident metadata.
- `logical_bytes`: logical lengths, which may be very large for sparse files.
- Hard links: one canonical path owns the storage in directory rollups; aliases report zero attributed allocation and expose their physical size/canonical path. Zero attributed bytes does not mean no data exists.
- Cleanup estimates: based on live allocation of the default stream. Deleting only some names of a hard-linked file estimates zero reclaimed storage. Additional streams, open handles, compression, and live-volume changes can make actual free-space changes differ.
- Directory live metadata describes the directory itself, not a recursive sum.
- NTFS metadata appears in scans; its presence is not a recommendation to delete it.

MFT update-sequence repair uses fixed 512-byte boundaries, including on volumes with 4096-byte native sectors. The scan is taken on an active volume and is not a VSS consistent snapshot or a real-time USN monitor. Concurrent file changes can affect the result. Rescan after meaningful changes and use live metadata before cleanup.

Cleanup adjustment runs after successful deletions have been durably audited, without changing preflight, handle checks, ancestor locks, replay protection, or stop-on-error behavior. Only matching, already loaded snapshots are adjusted. The volume serial, snapshot write time/length, file identity, selected name, and expected remaining link count must match. Removing an alias retains physical storage; removing the canonical name transfers its ownership to a remaining name; removing the final name subtracts storage and file counts along its reachable parent chain. Empty directories remain. Adjustment metadata and the updated snapshot are saved atomically. If adjustment fails, the cache is evicted and `next_action` requests a rescan; never replay deletion to retry an index update.

## Privacy and memory

DiskScope does not upload data or make application-level network requests. Local snapshots, preview files, and audit logs contain sensitive filenames. They are excluded from the source repository and release bundles. Share minimal redacted reproduction cases rather than raw scans.

The MCP process caches one volume and drops it after idle expiry or `disk_release`. Scanning still requires memory proportional to file count. Windows' filesystem cache is separate from DiskScope's managed heap; the tool does not automatically trim global OS caches, kill applications, or alter system memory limits.
