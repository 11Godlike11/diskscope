<p align="center"><img src="docs/assets/logo.svg" width="440" alt="DiskScope" /></p>

<p align="center">Give your AI a focused view of disk space.</p>

<p align="center">
  <a href="https://github.com/11Godlike11/diskscope/actions/workflows/ci.yml"><img src="https://github.com/11Godlike11/diskscope/actions/workflows/ci.yml/badge.svg" alt="Build and tests" /></a>
  <a href="https://github.com/11Godlike11/diskscope/releases"><img src="https://img.shields.io/github/v/release/11Godlike11/diskscope?color=2563eb" alt="Latest release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-059669" alt="MIT license" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%20x64-64748b" alt="Windows x64" />
</p>

<p align="center"><a href="README.zh-CN.md">简体中文</a> · <a href="https://github.com/11Godlike11/diskscope/releases/latest">Download</a> · <a href="docs/MCP.md">MCP reference</a> · <a href="docs/SAFETY.md">Cleanup behavior</a></p>

DiskScope is a local Windows disk analysis and cleanup tool for AI agents. It reads NTFS metadata to build a compact snapshot, then reveals one directory level at a time. Your agent can follow the largest directories without pulling a million-file listing into its context.

It ships as a self-contained executable with a JSON CLI and a standard **stdio MCP server**. No Python, Node.js, or .NET runtime installation is needed to run the release.

## What it does

- **Progressive exploration:** volume summary → one directory level → selected large files.
- **Physical allocation accounting:** distinguish sparse logical sizes and count hard-linked storage once.
- **Metadata scans:** skip empty MFT ranges using its bitmap, overlap bounded reads and parsing, and repair fixed 512-byte update sequences on both 512-byte and 4K native-sector volumes. Full reads and NTFS control codes remain fallbacks.
- **Reviewed cleanup:** preview an exact file set, check current identity and metadata, then delete only that set.
- **Background MCP jobs:** scans, cleanup previews, and deletion return a job ID immediately; poll progress through completion.
- **Bounded resident indexes:** cache one volume, release idle indexes after 120 seconds, or explicitly call `disk_release`.
- **Local operation:** no uploads, telemetry, network listener, or file-content analysis.

DiskScope analyzes Windows 10/11 x64 local NTFS volumes. Scans require an elevated process. Other filesystems, UNC shares, and case-sensitive same-name variants are unsupported. This release was tested on Windows 11; see [validation](docs/VALIDATION.md).

## Start in a minute

1. Download `DiskScope-<version>-win-x64.zip` from [Releases](https://github.com/11Godlike11/diskscope/releases/latest).
2. Extract it to a permanent folder, for example `C:\Tools\DiskScope`.
3. Run `DiskScope.exe help`, or connect an MCP client below.

The default state directory is `%LOCALAPPDATA%\DiskScope`. Override it with `--data-dir PATH`. Snapshots and audit records contain local filenames; keep them private.

### Connect Codex

From the extracted release folder:

```powershell
# Analysis tools only. No deletion tools are exposed.
.\Register-Codex.ps1

# Enable preview and deletion tools when you want agent-assisted cleanup.
.\Register-Codex.ps1 -EnableCleanup
```

The script uses `codex mcp add diskscope -- <executable> mcp ...`. Restart your client after changing the configuration. Full-volume scanning needs administrator rights; alternatively scan from an elevated terminal, then let a regular client browse the saved snapshot.

### Connect another MCP client

Adjust the executable path to your extraction folder:

```json
{
  "mcpServers": {
    "diskscope": {
      "command": "C:\\Tools\\DiskScope\\DiskScope.exe",
      "args": ["mcp", "--read-only"]
    }
  }
}
```

Remove `--read-only` to expose cleanup tools. This flag disables cleanup; analysis may still save local snapshots. [MCP setup and tool reference](docs/MCP.md).

### Ask your agent

> Use DiskScope to inspect F: by allocated size. Start with volumes, scan if the snapshot is missing or stale, and expand at most 10 items per directory. Identify the owner and purpose of large files. Preserve source code, assets, game saves, and service data. Preview specific cleanup candidates and delete only the scope I authorize. Poll every background job to completion, rescan after deletion, and finish with disk_release.

## JSON CLI

```powershell
.\DiskScope.exe volumes
.\DiskScope.exe scan F:
.\DiskScope.exe browse 'F:\' --limit 10
.\DiskScope.exe largest 'F:\Downloads' --extension zip --min-bytes 104857600 --limit 10
.\DiskScope.exe detail 'F:\Downloads\old-package.zip'
```

CLI scans and cleanup are synchronous. Standard output contains one JSON result; diagnostics go to stderr. Error results have a nonzero exit code. The CLI also works for agents that can run commands but do not support MCP.

Cleanup uses three separate calls:

```powershell
# Preview only; no deletion.
.\DiskScope.exe plan 'F:\specific-disposable-cache' --max-files 10000
.\DiskScope.exe plan-show PLAN_ID --limit 20

# Only after the exact scope has been reviewed and authorized.
.\DiskScope.exe execute PLAN_ID --confirm CONFIRM_TOKEN

# Inspect durable results if a caller lost the response.
.\DiskScope.exe execution-status PLAN_ID
```

**Deletion is permanent, bypasses the Recycle Bin, and is not transactional.** Plans expire after one hour. Execution refuses changed files and reparse-point ancestors, locks ancestors against rename/delete, checks the handle's final path against the expected and protected paths, prevents plan replay, records each deletion, and stops on the first file error. Read-only files can be deleted through `FileDispositionInfoEx`; unsupported systems fall back to legacy deletion, which may reject read-only files. New files and directories remain. A confirmation token identifies a plan; it does not represent user authorization. [Safety and accounting details](docs/SAFETY.md).

## Memory and long operations

The MCP server keeps at most one loaded volume index and evicts it after the idle timeout. `disk_release` unloads it immediately while retaining the snapshot on disk. Configure `--idle-seconds 30` for a shorter idle window.

The resident index uses packed records, shared UTF-16 names, CSR children, and totals stored only for directories. v3 snapshots use contiguous uncompressed arrays for bulk loading; they are larger on disk than v2 compressed snapshots. v1/v2 snapshots must be recreated with `disk_scan`; `disk_volumes.snapshot_format_current` identifies usable formats. Scan info reports read/parse/build/save timings and distinguishes actually read `raw_records` from `total_record_slots`. [Measurements and tradeoffs](docs/PERFORMANCE.md).

Large scans still need memory proportional to the volume's file count, including temporary construction buffers. Windows may also grow its own filesystem cache during heavy metadata I/O. Releasing DiskScope's index does **not** flush the system cache or change other programs. DiskScope does not install services or apply global memory limits.

After cleanup, a matching loaded index removes the successfully deleted names, keeps hard-linked allocation until the final name is removed, and saves the adjusted snapshot. Scan info reports `adjusted_after_cleanup` and `cleanup_adjustments`. If the index is unloaded, replaced, or no longer matches the planned identity/link count, the result's existing `next_action` asks for a rescan. Incremental adjustment does not track unrelated live changes; rescan for a fresh volume-wide view.

MCP scan, preview, and execute calls return `job_id`. Use `disk_job_status` until `complete` or `failed`, and keep the server alive. After a timeout, inspect the running job or durable audit rather than replaying deletion. [Job workflow](docs/MCP.md#background-jobs).

## Build and contribute

Build with the .NET 10 SDK on Windows:

```powershell
dotnet build src/DiskScope.csproj -c Release
# Use a new, nonexistent test directory. Tests only delete their own fixtures.
dotnet run --project tests/DiskScope.Tests.csproj -c Release -- "$env:TEMP\diskscope-test-$([guid]::NewGuid())"
.\scripts\package.ps1
```

The package script writes a portable ZIP and SHA-256 checksums under `artifacts/`. GitHub Actions runs the tests and packages release artifacts. Releases are currently unsigned; checksums verify download integrity and are not a signing identity.

Read [CONTRIBUTING.md](CONTRIBUTING.md) before changing cleanup or MFT parsing. Report a reproducible issue without attaching private disk indexes, tokens, or full path inventories. Security reports: [SECURITY.md](SECURITY.md).

## License and references

MIT © 2026 11Godlike11. DiskScope is an independent project and is not affiliated with WizTree, OpenAI, or Microsoft.

Implementation references: [Microsoft MFT](https://learn.microsoft.com/en-us/windows/win32/fileio/master-file-table), [NTFS record control](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record), [MCP stdio](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports), [Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).
