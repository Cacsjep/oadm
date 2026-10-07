# Run unit tests (no camera needed). This is what CI runs.
. (Join-Path $PSScriptRoot '_common.ps1')

Invoke-Dotnet test $Solution -c $Configuration --filter 'Category!=Hardware' @args
