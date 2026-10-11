[CmdletBinding()]
param([switch]$EnableCleanup, [ValidateRange(1,86400)][int]$IdleSeconds=120)
$ErrorActionPreference='Stop'
$exe=Join-Path $PSScriptRoot 'DiskScope.exe'
if(-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Run this script from the extracted release beside DiskScope.exe.' }
if(-not (Get-Command codex -ErrorAction SilentlyContinue)) { throw 'Codex CLI is not on PATH. Configure your MCP client using the README example instead.' }
$mcpArgs=@('mcp','--idle-seconds',"$IdleSeconds")
if(-not $EnableCleanup) { $mcpArgs+='--read-only' }
& codex mcp add diskscope -- $exe @mcpArgs
if($LASTEXITCODE -ne 0) { throw "codex mcp add failed: $LASTEXITCODE" }
Write-Host 'DiskScope configured. Restart your client to reconnect.'
Write-Host ('Cleanup tools enabled: '+[bool]$EnableCleanup)
