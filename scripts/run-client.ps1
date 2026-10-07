# Build and run the client. Pass --fake to run without a server on sample data.
. (Join-Path $PSScriptRoot '_common.ps1')

Invoke-Dotnet run --project $ClientProject -c $Configuration -- @args
