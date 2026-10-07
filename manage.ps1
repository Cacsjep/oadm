# OADM developer commands for Windows PowerShell 5.1 and PowerShell 7 (any OS). Same verbs,
# options, help and exit codes as manage.sh. Help texts live in scripts/manage-help.txt.
# Run ".\manage.ps1 help". PowerShell removes a bare --, write '--' (quoted) to pass app args.
$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$HelpFile = Join-Path $RepoRoot 'scripts/manage-help.txt'
$Solution = Join-Path $RepoRoot 'Oadm.sln'
$ServerProject = Join-Path $RepoRoot 'src/Oadm.Server/Oadm.Server.csproj'
$ClientProject = Join-Path $RepoRoot 'src/Oadm.Client/Oadm.Client.csproj'
$script:Configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }
$script:Dotnet = $null

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# ---------------------------------------------------------------- environment

function Get-DefaultRid {
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    if ($IsLinux) { return "linux-$arch" }
    if ($IsMacOS) { return "osx-$arch" }
    return "win-$arch"
}
$script:Rid = if ($env:RID) { $env:RID } else { Get-DefaultRid }

# Find a dotnet with a .NET 10 SDK: PATH, DOTNET_ROOT, ~/.dotnet, %LOCALAPPDATA%\Microsoft\dotnet.
function Find-Dotnet {
    $candidates = @()
    $cmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $candidates += $cmd.Source }
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet'), (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    if ($HOME) { $candidates += (Join-Path $HOME '.dotnet/dotnet'), (Join-Path $HOME '.dotnet/dotnet.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe') }
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) {
            $sdks = & $c --list-sdks 2>$null
            if (@($sdks) -match '^10\.') { return $c }
        }
    }
    return $null
}

# Sets $script:Dotnet and DOTNET_ROOT so app launchers find the same runtime. Exit 1 without SDK.
function Initialize-Dotnet {
    $script:Dotnet = Find-Dotnet
    if (-not $script:Dotnet) {
        [Console]::Error.WriteLine('error: no .NET 10 SDK found. Install it from https://dot.net or with dotnet-install.ps1 -Channel 10.0')
        exit 1
    }
    $env:DOTNET_ROOT = Split-Path -Parent $script:Dotnet
}

# Runs dotnet; a failure ends the script with dotnet's exit code.
function Invoke-Dotnet([string[]]$Arguments) {
    & $script:Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# ---------------------------------------------------------------- help

# Returns one section of scripts/manage-help.txt with the placeholders filled in.
function Get-HelpText([string]$Section) {
    $on = $false
    $lines = foreach ($line in (Get-Content -LiteralPath $HelpFile -Encoding UTF8)) {
        if ($line.StartsWith('## ')) { $on = ($line.Substring(3).Trim() -eq $Section); continue }
        if ($on) { $line.Replace('{RID}', $script:Rid).Replace('{CONFIGURATION}', $script:Configuration) }
    }
    return $lines
}

function Show-Help([string]$Section) {
    Get-HelpText $Section | Write-Output
}

# Usage error: message and the relevant help on stderr, exit code 2.
function Exit-Usage([string]$Section, [string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    [Console]::Error.WriteLine()
    foreach ($line in (Get-HelpText $Section)) { [Console]::Error.WriteLine($line) }
    exit 2
}

$Verbs = @('build', 'run', 'test', 'publish', 'clean', 'info', 'help')

# ---------------------------------------------------------------- argument parsing

$script:Target = ''
$script:OptRelease = $false
$script:OptFake = $false
$script:OptPort = ''
$script:OptData = ''
$script:OptServer = ''
$script:OptFilter = ''
$script:OptRid = ''
$script:Extra = @()

$ValueOptions = @('--port', '--data', '--server', '--filter', '--rid')

# Whether option $Name is valid for verb $Verb and target $Tgt.
function Test-OptionAllowed([string]$Verb, [string]$Tgt, [string]$Name) {
    switch ("${Verb}:${Tgt}:${Name}") {
        { $_ -like 'build:*:--release' } { return $true }
        { $_ -in 'run:server:--port', 'run:server:--data', 'run:server:--release' } { return $true }
        { $_ -in 'run:client:--fake', 'run:client:--server', 'run:client:--data', 'run:client:--release' } { return $true }
        { $_ -in 'run:dev:--port', 'run:dev:--data', 'run:dev:--release' } { return $true }
        { $_ -like 'test:*:--filter' -or $_ -like 'test:*:--release' } { return $true }
        { $_ -like 'publish:*:--rid' } { return $true }
    }
    return $false
}

function Read-Arguments([string]$Verb, [string[]]$Arguments) {
    $opts = @()
    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]; $i++
        if ($arg -cin 'help', '-h', '--help') { Show-Help $Verb; exit 0 }
        if ($arg -eq '--') {
            if ($i -lt $Arguments.Count) { $script:Extra = @($Arguments[$i..($Arguments.Count - 1)]) }
            break
        }
        if ($arg.StartsWith('-')) {
            $isValue = $ValueOptions -ccontains $arg
            if (-not $isValue -and $arg -cne '--release' -and $arg -cne '--fake') {
                Exit-Usage $Verb "unknown option '$arg' (arguments for dotnet or the app go after --)"
            }
            if ($isValue) {
                if ($i -ge $Arguments.Count -or -not $Arguments[$i]) { Exit-Usage $Verb "option $arg needs a value" }
                $opts += , @($arg, $Arguments[$i]); $i++
            } else {
                $opts += , @($arg, '')
            }
            continue
        }
        if ($script:Target) { Exit-Usage $Verb "unexpected argument '$arg'" }
        $script:Target = $arg
    }

    switch ($Verb) {
        'build' {
            if (-not $script:Target) { $script:Target = 'all' }
            if ($script:Target -cnotin 'all', 'server', 'client', 'plugins') { Exit-Usage build "unknown target '$($script:Target)' for build" }
        }
        'run' {
            if (-not $script:Target) { Exit-Usage run 'missing target for run (server, client or dev)' }
            if ($script:Target -cnotin 'server', 'client', 'dev') { Exit-Usage run "unknown target '$($script:Target)' for run" }
        }
        'test' {
            if (-not $script:Target) { $script:Target = 'unit' }
            if ($script:Target -cnotin 'unit', 'hardware', 'all') { Exit-Usage test "unknown target '$($script:Target)' for test" }
        }
        'publish' {
            if (-not $script:Target) { $script:Target = 'all' }
            if ($script:Target -cnotin 'all', 'server', 'client') { Exit-Usage publish "unknown target '$($script:Target)' for publish" }
        }
        { $_ -in 'clean', 'info' } {
            if ($script:Target) { Exit-Usage $Verb "$Verb takes no target" }
            if ($script:Extra.Count -gt 0) { Exit-Usage $Verb "$Verb takes no extra arguments" }
        }
    }

    foreach ($o in $opts) {
        $name = $o[0]; $value = $o[1]
        if (-not (Test-OptionAllowed $Verb $script:Target $name)) {
            if ($Verb -in 'clean', 'info') { Exit-Usage $Verb "$Verb takes no options" }
            Exit-Usage $Verb "option $name is not valid for '$Verb $($script:Target)'"
        }
        switch -CaseSensitive ($name) {
            '--release' { $script:OptRelease = $true }
            '--fake' { $script:OptFake = $true }
            '--port' {
                $n = 0
                if ($value -notmatch '^[0-9]+$' -or -not [int]::TryParse($value, [ref]$n) -or $n -lt 1 -or $n -gt 65535) {
                    Exit-Usage $Verb '--port needs a number from 1 to 65535'
                }
                $script:OptPort = $value
            }
            '--data' { $script:OptData = $value }
            '--server' { $script:OptServer = $value }
            '--filter' { $script:OptFilter = $value }
            '--rid' { $script:OptRid = $value }
        }
    }
    if ($script:OptRelease) { $script:Configuration = 'Release' }
}

# ---------------------------------------------------------------- helpers

function Get-PluginProjects {
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'plugins') -Directory | Sort-Object Name | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter *.csproj -File | Sort-Object Name
    }
}

function Build-Project([string]$Project) {
    Invoke-Dotnet (@('build', $Project, '-c', $script:Configuration) + $script:Extra)
}

function Build-Plugins {
    foreach ($p in @(Get-PluginProjects)) { Build-Project $p.FullName }
}

# Absolute path of a folder (created if missing).
function Get-AbsoluteDir([string]$Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

# Built app dll: bin/<configuration>/<tfm>/<name>.dll.
function Get-AppDll([string]$Name) {
    $bin = Join-Path $RepoRoot "src/$Name/bin/$($script:Configuration)"
    $dll = Get-ChildItem -LiteralPath $bin -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "$Name.dll" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $dll) {
        [Console]::Error.WriteLine("error: $Name.dll not found under src/$Name/bin/$($script:Configuration)")
        exit 1
    }
    return $dll
}

# Arguments for the server from --port and --data.
function Get-ServerArgs {
    $a = @()
    if ($script:OptPort) { $a += "--Oadm:ListenUrl=http://0.0.0.0:$($script:OptPort)" }
    if ($script:OptData) { $a += "--Oadm:DataDir=$(Get-AbsoluteDir $script:OptData)" }
    return , $a
}

function Test-PortOpen([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $Port); return $true } catch { return $false } finally { $client.Close() }
}

# Same environment as the launch profile used by "dotnet run".
function Set-DevEnvironment {
    if (-not $env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT = 'Development' }
    if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }
}

# Start-Process joins arguments with spaces, so quote the ones that need it.
function ConvertTo-ArgumentString([string[]]$Arguments) {
    ($Arguments | ForEach-Object { if ($_ -match '[\s"]' -or $_ -eq '') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}

function Publish-App([string]$Project, [string]$Out) {
    Invoke-Dotnet (@('publish', $Project, '-c', 'Release', '-r', $script:Rid, '--self-contained',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:PublishReadyToRun=true', '-p:DebugType=embedded', '-o', $Out) + $script:Extra)
}

# ---------------------------------------------------------------- verbs

function Invoke-Build {
    Initialize-Dotnet
    switch ($script:Target) {
        'all' { Build-Project $Solution }
        'server' { Build-Project $ServerProject; Build-Plugins }
        'client' { Build-Project $ClientProject; Build-Plugins }
        'plugins' { Build-Plugins }
    }
}

function Invoke-Run {
    Initialize-Dotnet
    Set-DevEnvironment
    $appArgs = $script:Extra
    $script:Extra = @()
    switch ($script:Target) {
        'server' {
            Build-Project $ServerProject; Build-Plugins
            $serverArgs = Get-ServerArgs
            & $script:Dotnet exec (Get-AppDll 'Oadm.Server') @serverArgs @appArgs
            exit $LASTEXITCODE
        }
        'client' {
            Build-Project $ClientProject; Build-Plugins
            $clientArgs = @()
            if ($script:OptFake) { $clientArgs += '--fake' }
            if ($script:OptServer) { $clientArgs += '--server', $script:OptServer }
            if ($script:OptData) { $clientArgs += '--data', (Get-AbsoluteDir $script:OptData) }
            & $script:Dotnet exec (Get-AppDll 'Oadm.Client') @clientArgs @appArgs
            exit $LASTEXITCODE
        }
        'dev' {
            Build-Project $Solution
            $serverArgs = Get-ServerArgs
            $serverDll = Get-AppDll 'Oadm.Server'
            $clientDll = Get-AppDll 'Oadm.Client'
            $port = if ($script:OptPort) { [int]$script:OptPort } else { 5080 }
            $logDir = Get-AbsoluteDir (Join-Path $RepoRoot 'artifacts/logs')
            $server = Start-Process -FilePath $script:Dotnet -ArgumentList (ConvertTo-ArgumentString (@('exec', $serverDll) + $serverArgs)) `
                -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $logDir 'server-dev.log') `
                -RedirectStandardError (Join-Path $logDir 'server-dev.err.log')
            Write-Host "server started (pid $($server.Id)), log: artifacts/logs/server-dev.log"
            $code = 0
            try {
                $waited = 0
                while (-not (Test-PortOpen $port)) {
                    if ($server.HasExited) {
                        [Console]::Error.WriteLine('error: server exited during startup, see artifacts/logs/server-dev.log')
                        Get-Content -LiteralPath (Join-Path $logDir 'server-dev.log') -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { [Console]::Error.WriteLine($_) }
                        $code = 1
                        return
                    }
                    if ($waited -ge 60) { [Console]::Error.WriteLine("warning: port $port not open after 30 s, starting the client anyway"); break }
                    Start-Sleep -Milliseconds 500; $waited++
                }
                $clientArgs = @()
                if ($script:OptPort) { $clientArgs += '--server', "http://localhost:$($script:OptPort)" }
                & $script:Dotnet exec $clientDll @clientArgs @appArgs
                $code = $LASTEXITCODE
            } finally {
                if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
                exit $code
            }
        }
    }
}

function Invoke-Test {
    Initialize-Dotnet
    switch ($script:Target) {
        'unit' { $filter = 'Category!=Hardware' }
        'hardware' {
            if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot 'dev-cameras.yaml')) -and -not $env:OADM_DEV_CAMERAS) {
                [Console]::Error.WriteLine('error: dev-cameras.yaml not found and OADM_DEV_CAMERAS not set, copy dev-cameras.example.yaml first')
                exit 1
            }
            $filter = 'Category=Hardware'
        }
        'all' { $filter = 'Category!=HardwareWrite' }
    }
    if ($script:OptFilter) { $filter = "($filter)&($($script:OptFilter))" }
    Invoke-Dotnet (@('test', $Solution, '-c', $script:Configuration, '--filter', $filter) + $script:Extra)
}

function Invoke-Publish {
    Initialize-Dotnet
    if ($script:OptRid) { $script:Rid = $script:OptRid }
    if ($script:Target -in 'all', 'server') {
        $out = Join-Path $RepoRoot "artifacts/publish/server/$($script:Rid)"
        Publish-App $ServerProject $out
        foreach ($p in @(Get-PluginProjects)) {
            Invoke-Dotnet @('publish', $p.FullName, '-c', 'Release', '-o', (Join-Path $out "plugins/$($p.Directory.Name)"))
        }
        Write-Host "server published to $out"
    }
    if ($script:Target -in 'all', 'client') {
        $out = Join-Path $RepoRoot "artifacts/publish/client/$($script:Rid)"
        Publish-App $ClientProject $out
        Write-Host "client published to $out"
    }
}

# Removes bin and obj folders below $Dir without descending into them.
function Remove-BuildOutput([string]$Dir) {
    foreach ($d in @(Get-ChildItem -LiteralPath $Dir -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($d.Name -in 'bin', 'obj') { Remove-Item -LiteralPath $d.FullName -Recurse -Force }
        else { Remove-BuildOutput $d.FullName }
    }
}

function Invoke-Clean {
    foreach ($d in 'src', 'plugins', 'tests', 'tools') {
        $path = Join-Path $RepoRoot $d
        if (Test-Path -LiteralPath $path) { Remove-BuildOutput $path }
    }
    $art = Join-Path $RepoRoot 'artifacts'
    if (Test-Path -LiteralPath $art) { Remove-Item -LiteralPath $art -Recurse -Force }
    Write-Host 'clean'
}

function Invoke-Info {
    Initialize-Dotnet
    Write-Host "repository     $RepoRoot"
    Write-Host "dotnet         $($script:Dotnet)"
    Write-Host "DOTNET_ROOT    $env:DOTNET_ROOT"
    Write-Host "configuration  $($script:Configuration)"
    Write-Host "rid            $($script:Rid)"
    Write-Host '.NET 10 SDKs'
    & $script:Dotnet --list-sdks | Where-Object { $_ -match '^10\.' } | ForEach-Object { Write-Host "  $_" }
}

# ---------------------------------------------------------------- main

# Values like 5099 arrive as numbers; treat everything as text.
$argv = @($args | ForEach-Object { "$_" })
if ($argv.Count -eq 0) { Show-Help overview; exit 0 }
$verb = $argv[0]
[string[]]$rest = @(if ($argv.Count -gt 1) { $argv[1..($argv.Count - 1)] })

if ($verb -cin 'help', '-h', '--help') {
    if ($rest.Count -eq 0) { Show-Help overview; exit 0 }
    if ($rest[0] -cin '-h', '--help') { Show-Help help; exit 0 }
    if ($Verbs -cnotcontains $rest[0]) { Exit-Usage overview "unknown verb '$($rest[0])'" }
    Show-Help $rest[0]
    exit 0
}
if ($Verbs -cnotcontains $verb) { Exit-Usage overview "unknown verb '$verb'" }

Read-Arguments $verb $rest
switch ($verb) {
    'build' { Invoke-Build }
    'run' { Invoke-Run }
    'test' { Invoke-Test }
    'publish' { Invoke-Publish }
    'clean' { Invoke-Clean }
    'info' { Invoke-Info }
}
exit 0
