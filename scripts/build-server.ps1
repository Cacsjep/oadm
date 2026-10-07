# Build the server and the bundled plugins.
. (Join-Path $PSScriptRoot '_common.ps1')

Invoke-Dotnet build $ServerProject -c $Configuration @args
Get-ChildItem (Join-Path $RepoRoot 'plugins') -Filter *.csproj -Recurse | ForEach-Object { Invoke-Dotnet build $_.FullName -c $Configuration }
