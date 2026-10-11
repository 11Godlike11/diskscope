# Contributing

Use Windows and the .NET 10 SDK. Clone the repository, build `src/DiskScope.csproj`, and run the tests with a new, nonexistent directory. Do not use existing user directories as test fixtures.

```powershell
dotnet build src/DiskScope.csproj -c Release
dotnet run --project tests/DiskScope.Tests.csproj -c Release -- "$env:TEMP\diskscope-test-$([guid]::NewGuid())"
.\scripts\package.ps1
python tests/mcp_smoke.py artifacts/staging/<build>/DiskScope.exe
```

Native tests require an elevated Windows NTFS environment. Protocol smoke tests create and delete their own temporary fixtures; they do not scan or delete user folders.

Changes to cleanup must preserve identity validation, ancestor locking, reparse refusal, replay protection, and durable auditing. Add behavior-focused regressions for new safety boundaries. Changes to MFT parsing must test resident data, alternate streams, sparse allocation, and hard links. Validate JSON output and MCP behavior using the actual published executable.

Keep stdout clean in MCP mode. Keep file paths and names as data. Return bounded pages rather than full indexes. Document behavior changes in `CHANGELOG.md` and update both README languages when user-facing behavior changes.

Bug reports should include the release version, Windows version, filesystem, command/tool name, redacted error, and a small reproduction. Do not attach private snapshots, cleanup tokens, full disk inventories, or production data. For security issues, follow `SECURITY.md`.
