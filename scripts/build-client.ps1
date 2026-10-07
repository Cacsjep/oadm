# Build the Avalonia client.
. (Join-Path $PSScriptRoot '_common.ps1')

Invoke-Dotnet build $ClientProject -c $Configuration @args
