# Publish a self-contained server to artifacts/publish/server/<rid>. Set RID=linux-x64 etc. to cross-publish.
. (Join-Path $PSScriptRoot '_common.ps1')

$out = Join-Path $RepoRoot "artifacts/publish/server/$Rid"
Invoke-Dotnet publish $ServerProject -c Release -r $Rid --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=embedded -o $out @args
Get-ChildItem (Join-Path $RepoRoot 'plugins') -Filter *.csproj -Recurse | ForEach-Object {
    Invoke-Dotnet publish $_.FullName -c Release -o (Join-Path $out "plugins/$($_.Directory.Name)")
}
Write-Host "server published to $out"
