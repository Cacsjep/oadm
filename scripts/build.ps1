# Build the whole solution (server, client, plugins, tests).
. (Join-Path $PSScriptRoot '_common.ps1')

Invoke-Dotnet build $Solution -c $Configuration @args
