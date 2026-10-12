[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')][string]$Version='1.0.1')
$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifacts=Join-Path $projectRoot 'artifacts'
$stage=Join-Path $artifacts ('staging\'+$Version+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
& dotnet publish (Join-Path $projectRoot 'src\DiskScope.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -o $stage --nologo
if($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'README.zh-CN.md'),(Join-Path $projectRoot 'CHANGELOG.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Register-Codex.ps1') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination (Join-Path $stage 'docs') -Recurse
$restore=Get-Content -LiteralPath (Join-Path $projectRoot 'src\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtime=$restore.project.frameworks.'net10.0'.downloadDependencies | Where-Object name -EQ 'Microsoft.NETCore.App.Runtime.win-x64'
$runtimeVersion=($runtime.version -split '[\[\], ]+' | Where-Object {$_})[0]
$licenseDir=Join-Path $stage 'licenses'
New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
$found=$false
foreach($folder in $restore.packageFolders.PSObject.Properties.Name) {
    $pack=Join-Path $folder "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
    if(Test-Path -LiteralPath (Join-Path $pack 'LICENSE.TXT')) {
        Copy-Item -LiteralPath (Join-Path $pack 'LICENSE.TXT') -Destination (Join-Path $licenseDir 'dotnet-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $pack 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenseDir 'dotnet-THIRD-PARTY-NOTICES.txt')
        $found=$true; break
    }
}
if(-not $found) { throw 'Could not locate the bundled .NET runtime license notices.' }
$zip=Join-Path $artifacts "DiskScope-$Version-win-x64.zip"
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $stage | Select-Object -ExpandProperty FullName) -DestinationPath $zip -Force
$hash=Get-FileHash -LiteralPath $zip -Algorithm SHA256
$checksum=Join-Path $artifacts "DiskScope-$Version-SHA256SUMS.txt"
(($hash.Hash.ToLowerInvariant())+'  '+(Split-Path -Leaf $zip)) | Set-Content -LiteralPath $checksum -Encoding ascii
[pscustomobject]@{Version=$Version;Executable=(Join-Path $stage 'DiskScope.exe');Zip=$zip;Checksums=$checksum} | ConvertTo-Json
