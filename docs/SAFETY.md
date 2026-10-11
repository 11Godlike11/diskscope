# Cleanup and accounting

## Authorization and permanence

DiskScope is a privileged local tool when run elevated. Client prompts and review rules remain responsible for the user's intended scope. The server cannot independently prove that an AI obtained authorization. A plan token matches an exact preview; it is not a consent credential.

Execution permanently deletes files and bypasses the Recycle Bin. There is no rollback and no transaction spanning the file set. Back up valuable data through an appropriate independent workflow before deciding to delete it.

## Preview and execution

The preview stores explicit selected paths and an exact file set with volume serial number, file ID, logical size, allocation, modification time, and link count. It expires after one hour. New files are not added at execution time; directories remain.

Execution performs a full preflight, then checks each file again using the same open handle used to mark deletion. Ancestors are opened without delete sharing to pin their path, and reparse points are refused. The file handle denies write and delete sharing while validating and deleting. This is intended to prevent ordinary rename/replacement races through that path.

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

The scan is taken on an active volume and is not a VSS consistent snapshot or a real-time USN monitor. Concurrent file changes can affect the result. Rescan after meaningful changes and use live metadata before cleanup.

## Privacy and memory

DiskScope does not upload data or make application-level network requests. Local snapshots, preview files, and audit logs contain sensitive filenames. They are excluded from the source repository and release bundles. Share minimal redacted reproduction cases rather than raw scans.

The MCP process caches one volume and drops it after idle expiry or `disk_release`. Scanning still requires memory proportional to file count. Windows' filesystem cache is separate from DiskScope's managed heap; the tool does not automatically trim global OS caches, kill applications, or alter system memory limits.
