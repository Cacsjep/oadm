# Build and run the server (gRPC on http://0.0.0.0:5080). Extra arguments go to the server.
. (Join-Path $PSScriptRoot '_common.ps1')

& (Join-Path $PSScriptRoot 'build-server.ps1')
Invoke-Dotnet run --no-build --project $ServerProject -c $Configuration -- @args
