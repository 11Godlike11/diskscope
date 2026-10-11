# Changelog

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
