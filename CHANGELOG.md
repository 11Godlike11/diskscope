# Changelog

## 1.0.1 — 2026-10-12

- Open ancestor directories with directory-list/read-attributes access so their no-delete sharing blocks rename/delete; validate final handle paths and protected-path rules during preview and deletion.
- Prefer `FileDispositionInfoEx` to delete read-only files, with legacy fallback only for unsupported API/filesystem errors.
- Fix MFT update-sequence repair to use 512-byte strides regardless of native sector size.
- Require expected exception types and refusal reasons in core tests; add synthetic MFT records, prefixed protected paths, ancestor locking, handle-path comparison, and read-only deletion regressions.

## 1.0.0 — 2026-10-12

First public release.

- NTFS MFT scans with direct reads and control-code fallback.
- Progressive directory browsing, physical allocation ordering, bounded largest-file search, and hard-link aliases.
- JSON CLI and 12-tool stdio MCP server.
- Background scan, cleanup preview, and execution jobs with queryable progress.
- One-volume memory cache, 120-second idle expiry, and explicit index release.
- Optional analysis-only mode that omits and rejects cleanup tools.
- Exact-file preview, guarded handle-based deletion, replay protection, and durable execution audits.
- Portable Windows x64 package, bilingual documentation, source/license, automated Windows validation, and SHA-256 checksums.

The private prototype used synchronous MCP cleanup calls and retained every loaded index; those behaviors are replaced in the public release. Windows' global filesystem cache remains managed by the OS.
