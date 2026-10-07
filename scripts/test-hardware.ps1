# Run hardware tests against the cameras in dev-cameras.yaml.
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not (Test-Path (Join-Path $RepoRoot 'dev-cameras.yaml')) -and -not $env:OADM_DEV_CAMERAS) {
    throw 'dev-cameras.yaml not found, copy dev-cameras.example.yaml first'
}
Invoke-Dotnet test $Solution -c $Configuration --filter 'Category=Hardware' @args
