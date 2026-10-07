# Shared helpers for OADM scripts. Dot-source, do not execute.
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ServerProject = Join-Path $RepoRoot 'src/Oadm.Server/Oadm.Server.csproj'
$ClientProject = Join-Path $RepoRoot 'src/Oadm.Client/Oadm.Client.csproj'
$Solution = Join-Path $RepoRoot 'Oadm.sln'
if (-not $Configuration) { $Configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' } }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# Find a dotnet that has a .NET 10 SDK: PATH first, then the usual per-user install folders.
function Find-Dotnet {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe') }
    if ($HOME) { $candidates += (Join-Path $HOME '.dotnet/dotnet') }
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks -match '^10\.') { return $c }
        }
    }
    throw 'No .NET 10 SDK found. Install it from https://dot.net or with dotnet-install.ps1 -Channel 10.0'
}

$Dotnet = Find-Dotnet

function Get-DefaultRid {
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    if ($IsLinux) { return "linux-$arch" }
    if ($IsMacOS) { return "osx-$arch" }
    return "win-$arch"
}
if (-not $Rid) { $Rid = if ($env:RID) { $env:RID } else { Get-DefaultRid } }

function Invoke-Dotnet {
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}
