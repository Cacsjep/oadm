# Installs the MSI, checks it and removes it again (release workflow, job "smoke windows").
#   -Mode ServerAndClient  default install: service, gRPC answer, data folder, firewall rule, decoder, uninstall
#   -Mode ClientOnly       ADDLOCAL=Client: no service, no server, no firewall rule, decoder, uninstall
# Every line starts with the time, and while a program runs a line every 20 s shows free memory and the busiest
# processes. A runner that stops answering leaves no log file behind, but the live log keeps what was printed.
param(
    [Parameter(Mandatory)] [string] $Msi,
    [ValidateSet('ServerAndClient', 'ClientOnly')] [string] $Mode = 'ServerAndClient'
)

$ErrorActionPreference = 'Stop'
$started = Get-Date

function Say([string] $text) {
    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    Write-Host ("[{0:HH:mm:ss} +{1}s] {2}" -f (Get-Date), $elapsed, $text)
}

function Show-Machine {
    $os = Get-CimInstance Win32_OperatingSystem
    Say ("free memory {0:N0} MB of {1:N0} MB" -f ($os.FreePhysicalMemory / 1KB), ($os.TotalVisibleMemorySize / 1KB))
    Get-Process | Sort-Object CPU -Descending | Select-Object -First 5 Name, Id, CPU, @{ n = 'MB'; e = { [int]($_.WorkingSet64 / 1MB) } } |
        Format-Table -AutoSize | Out-String | Write-Host
}

function Show-ServerLog([int] $lines = 60) {
    Get-ChildItem C:\ProgramData\OADM\logs -Filter *.log -ErrorAction SilentlyContinue | Get-Content -Tail $lines | Write-Host
}

# Runs a program with a time limit; a hang prints the logs and the busiest processes and fails the step.
function Invoke-Limited([string] $file, [string] $arguments, [string] $log, [int] $seconds = 300) {
    Say "start: $file $arguments"
    $p = Start-Process $file -ArgumentList $arguments -PassThru
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not $p.WaitForExit(20000)) {
        if ((Get-Date) -gt $deadline) {
            Say "TIMEOUT after $seconds s: $file $arguments"
            if ($log -and (Test-Path $log)) { Get-Content $log -Tail 80 | Write-Host }
            Show-ServerLog 80
            Show-Machine
            $p.Kill($true)
            throw "timeout: $file $arguments"
        }
        Say "still running: $file"
        Show-Machine
    }
    $p.WaitForExit()
    Say "exit code $($p.ExitCode): $file"
    $p
}

function Uninstall([string] $log) {
    $p = Invoke-Limited msiexec.exe "/x `"$Msi`" /qn /norestart /l*v $log" $log
    if ($p.ExitCode -ne 0) { Get-Content $log -Tail 80 | Write-Host; throw "uninstall failed: $($p.ExitCode) (3010 = restart required)" }
}

function Test-Decoder {
    # WinExe: Start-Process returns at once; Invoke-Limited waits for the exit code.
    $d = Invoke-Limited 'C:\Program Files\OADM\Client\Oadm.Client.exe' '--check-decoder' '' 120
    if ($d.ExitCode -ne 0) { throw "decoder check failed: $($d.ExitCode)" }
}

Show-Machine

if ($Mode -eq 'ClientOnly') {
    $p = Invoke-Limited msiexec.exe "/i `"$Msi`" ADDLOCAL=Client /qn /norestart /l*v install-client.log" install-client.log
    if ($p.ExitCode -ne 0) { Get-Content install-client.log -Tail 80 | Write-Host; throw "install failed: $($p.ExitCode) (3010 = restart required)" }
    if (Get-Service OadmServer -ErrorAction SilentlyContinue) { throw 'client only must not install the service' }
    if (Test-Path 'C:\Program Files\OADM\Server') { throw 'client only must not install the server' }
    if (Get-NetFirewallRule -DisplayName 'OADM Server*' -ErrorAction SilentlyContinue) { throw 'client only must not add a firewall rule' }
    if (-not (Test-Path 'C:\Program Files\OADM\Client\THIRD-PARTY-NOTICES.txt')) { throw 'client license texts missing' }
    Test-Decoder
    Uninstall uninstall-client.log
    if (Test-Path 'C:\Program Files\OADM\Client\Oadm.Client.exe') { throw 'client still installed' }
    Say 'client only: install, decoder, uninstall ok'
    return
}

$p = Invoke-Limited msiexec.exe "/i `"$Msi`" /qn /norestart /l*v install.log" install.log
if ($p.ExitCode -ne 0) { Get-Content install.log -Tail 80 | Write-Host; throw "install failed: $($p.ExitCode) (3010 = restart required)" }
$svc = Get-Service OadmServer
Say "service: $($svc.Status) $($svc.StartType)"

Say 'wait for port 5080'
$ok = $false
for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
    try { $c = New-Object Net.Sockets.TcpClient('127.0.0.1', 5080); $c.Close(); $ok = $true } catch { Start-Sleep 1 }
}
if (-not $ok) { Show-ServerLog 50; throw 'port 5080 not open' }

# gRPC over HTTP/2 with TLS; the server's certificate is self-signed (clients pin it), so the check accepts it.
Say 'gRPC call'
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(60)
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, 'https://127.0.0.1:5080/oadm.Smoke/Test')
$req.Version = [Version]'2.0'
$req.VersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact
$req.Content = [System.Net.Http.ByteArrayContent]::new([byte[]]@())
$req.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/grpc')
$resp = $client.SendAsync($req).GetAwaiter().GetResult()
$null = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
$status = @(@($resp.Headers) + @($resp.TrailingHeaders) | Where-Object { $_.Key -eq 'grpc-status' })
Say "HTTP/$($resp.Version) $([int]$resp.StatusCode), grpc-status: $($status.Value)"
if ($status.Count -eq 0) { throw 'no gRPC answer' }

Say 'files, folder rights, firewall'
foreach ($f in 'oadm.db', 'master.key') { if (-not (Test-Path "C:\ProgramData\OADM\$f")) { throw "$f missing" } }
foreach ($app in 'Server', 'Client') {
    if (-not (Test-Path "C:\Program Files\OADM\$app\plugins\oadm.restart")) { throw "$app plugins missing" }
    foreach ($f in 'TERMS.md', 'LICENSE.txt', 'NOTICE.txt', 'THIRD-PARTY-NOTICES.txt', 'LGPL-2.1.txt') {
        if (-not (Test-Path "C:\Program Files\OADM\$app\$f")) { throw "$app $f missing" }
    }
}
(Get-Acl C:\ProgramData\OADM).Access | Format-Table IdentityReference, FileSystemRights -AutoSize | Out-String | Write-Host
# Only TCP 5080 for Domain and Private; the NTP and DHCP plugins add their UDP rules only while enabled.
$rules = @(Get-NetFirewallRule -DisplayName 'OADM Server*')
$rules | Format-Table DisplayName, Enabled, Direction, Profile -AutoSize | Out-String | Write-Host
if ($rules.Count -ne 1 -or $rules[0].DisplayName -ne 'OADM Server (gRPC, TCP 5080)') { throw 'only the gRPC firewall rule expected' }
if (($rules[0].Profile.ToString() -replace ' ', '') -ne 'Domain,Private') { throw "profile Domain, Private expected, not $($rules[0].Profile)" }
# The server checked its folders at start (service mode) and did not refuse them.
if (Select-String -Path C:\ProgramData\OADM\logs\*.log -Pattern 'could not be fixed' -Quiet) { throw 'folder check failed' }
Show-ServerLog 30

Test-Decoder

# Stop the service first, so its exe is not in use while the files are removed (a file in use asks for a restart;
# /norestart turns that into exit code 3010 instead of a restart of the runner).
Say 'stop the service'
Stop-Service OadmServer -ErrorAction SilentlyContinue
(Get-Service OadmServer).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
Uninstall uninstall.log
if (Get-Service OadmServer -ErrorAction SilentlyContinue) { throw 'service still installed' }
if (Get-NetFirewallRule -DisplayName 'OADM Server*' -ErrorAction SilentlyContinue) { throw 'firewall rule left behind' }
if (-not (Test-Path C:\ProgramData\OADM\master.key)) { throw 'data folder was removed' }
Say 'server and client: install, service, gRPC, firewall, uninstall ok'
