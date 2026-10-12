# Release validation

## 1.0.1

Local verification on Windows 11 x64, NTFS, .NET SDK 10.0.103:

- **55 core checks**, adding synthetic 1 KiB / 4 KiB MFT update-sequence records, `\\?\`, `\\.\` and UNC protected-path refusals, effective ancestor locking, final handle-path comparison, read-only file deletion, and exact refusal reasons.
- **24 packaged protocol/CLI checks** against the rebuilt self-contained executable.
- A live direct-MFT scan of a local NTFS volume matched the 1.0.0 record counts with zero parse failures.
- The 4 KiB update-sequence path is covered by synthetic records only; it was not verified on physical 4Kn hardware.

## 1.0.0

Local verification on Windows 11 x64, NTFS, .NET SDK 10.0.103:

- **39 core checks:** native MFT parsing against fresh fixture files; alternate streams; resident and sparse data; hard-link accounting; protected selections; junction/ancestor refusal; stale preview refusal; exact deletion; replay prevention; new-file preservation; durable execution status; snapshot roundtrip; one-volume cache; explicit/idle release; asynchronous job completion/failure/overlap handling.
- **24 packaged protocol/CLI checks:** the actual self-contained EXE, initialization and schemas, all 12 tools, background preview/deletion, responsive ping, pagination, preserving a newly created file, audit results, replay refusal, explicit release, clean process shutdown, hiding/rejecting cleanup in analysis-only mode, and CLI enforcement.
- **Portable package:** self-contained Windows x64 executable, bilingual documentation, Codex registration helper, MIT license, SHA-256 checksum. No private disk snapshot or cleanup audit is included.

Native tests require an elevated NTFS Windows environment. They refuse an existing test directory and only delete newly created fixtures. Protocol tests use their own temporary directory and do not scan a user's disk. GitHub Actions runs the same core and package checks on a hosted Windows runner.

Earlier private-prototype scans were exercised against large local NTFS volumes. Those measurements are not a throughput guarantee for this release. The public release changes memory lifetime and MCP job behavior; automated checks target these changes. Windows 10 compatibility is intended by the selected runtime/platform but was not separately verified locally.

## Remaining limits

- Windows x64 and local NTFS only; no UNC, FAT/exFAT/ReFS, or case-sensitive same-name handling.
- Activity during a scan can change the inventory; this is not a VSS consistent snapshot or USN incremental monitor.
- Live-volume accounting can contain unresolved records, which are reported separately.
- Index memory still scales with file count. Idle/explicit release removes our managed cache, not Windows' global filesystem cache.
- Cleanup is permanent and can be partial after interruption or a file error. Audit output and actual remaining paths are the recovery evidence.
- The executable is not code signed. No antivirus-certification or every-client compatibility claim is made.
