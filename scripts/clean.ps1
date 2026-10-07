# Remove build output (bin, obj, artifacts). Keeps the server data folder.
. (Join-Path $PSScriptRoot '_common.ps1')

Get-ChildItem (Join-Path $RepoRoot 'src'),(Join-Path $RepoRoot 'plugins'),(Join-Path $RepoRoot 'tests') -Directory -Recurse -Include bin,obj -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$art = Join-Path $RepoRoot 'artifacts'
if (Test-Path $art) { Remove-Item -Recurse -Force $art }
Write-Host 'clean'
