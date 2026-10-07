# Publish a self-contained client to artifacts/publish/client/<rid>. Set RID=osx-arm64 etc. to cross-publish.
. (Join-Path $PSScriptRoot '_common.ps1')

$out = Join-Path $RepoRoot "artifacts/publish/client/$Rid"
Invoke-Dotnet publish $ClientProject -c Release -r $Rid --self-contained -o $out @args
Write-Host "client published to $out"
