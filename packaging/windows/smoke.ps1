# Installs the MSI, checks it and removes it again (release workflow, job "smoke windows").
#   -Mode ServerAndClient  default install: service, gRPC answer, data folder, firewall rules, decoder, uninstall
#   -Mode ClientOnly       ADDLOCAL=Client: no service, no server, no firewall rule, decoder, uninstall
#   -Mode SiteNode         the OADM Site Node MSI with SERVER=<address>: service as NT SERVICE\OadmSiteNode, "status" answers
#                          unbound with that server, no firewall rule (the node only connects out), uninstall
# Every line starts with the time, and while a program runs a line every 20 s shows free memory and the busiest
# processes. A runner that stops answering leaves no log file behind, but the live log keeps what was printed.
param(
    [Parameter(Mandatory)] [string] $Msi,
    [ValidateSet('ServerAndClient', 'ClientOnly', 'SiteNode')] [string] $Mode = 'ServerAndClient'
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

# The lines before the failed action ("Return value 3") of an MSI log: the end of the log is only the property dump.
function Show-MsiFailure([string] $log) {
    $hit = Select-String -Path $log -Pattern 'Return value 3' -List
    if ($hit) { Get-Content $log | Select-Object -Skip ([Math]::Max(0, $hit.LineNumber - 40)) -First 45 | Write-Host }
    else { Get-Content $log -Tail 80 | Write-Host }
}

# Restart Manager off: a silent removal would otherwise close every program that holds one of its files, also the
# runner's own terminal (the runner is lost, 4.1.0-rc.4) or fail at once with 1603 when it cannot (rc.3). A file in use
# then fails the step with 3010 instead.
# Error 1409 (system error 1018): the service's registry key was still marked for deletion; a second run then passes.
function Uninstall([string] $log) {
    $p = Invoke-Limited msiexec.exe "/x `"$Msi`" MSIRESTARTMANAGERCONTROL=Disable /qn /norestart /l*v $log" $log
    if ($p.ExitCode -eq 1603 -and (Select-String -Path $log -Pattern 'Error 1409' -Quiet)) {
        Show-MsiFailure $log
        Say 'uninstall hit Error 1409, once more'
        Start-Sleep 5
        $p = Invoke-Limited msiexec.exe "/x `"$Msi`" MSIRESTARTMANAGERCONTROL=Disable /qn /norestart /l*v retry-$log" "retry-$log"
        $log = "retry-$log"
    }
    if ($p.ExitCode -ne 0) { Show-MsiFailure $log; throw "uninstall failed: $($p.ExitCode) (3010 = restart required)" }
}

# Stops a service and waits until its program has exited too, releasing the service handle (a handle that stays open
# keeps the service's registry key "marked for deletion" during the uninstall).
function Stop-OadmService([string] $name, [string] $process) {
    Stop-Service $name -ErrorAction SilentlyContinue
    $s = Get-Service $name
    try { $s.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) } finally { $s.Dispose() }
    $deadline = (Get-Date).AddSeconds(60)
    while (Get-Process -Name $process -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw "$process still running after the service stopped" }
        Start-Sleep -Milliseconds 500
    }
}

# After the uninstall: waits until the service is gone (sc.exe: 1060 = does not exist), at most 60 s.
function Wait-ServiceGone([string] $name) {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        sc.exe query $name | Out-Null
        if ($LASTEXITCODE -eq 1060) { return }
        if ((Get-Date) -gt $deadline) { throw "service $name still installed" }
        Start-Sleep -Milliseconds 500
    }
}

# The shortcuts of an app in the Start menu and on the public desktop: both after the install, none after the removal.
function Test-Shortcuts([string] $name, [bool] $installed) {
    foreach ($lnk in "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\$name.lnk", "$env:PUBLIC\Desktop\$name.lnk") {
        if ($installed -and -not (Test-Path $lnk)) { throw "shortcut missing: $lnk" }
        if (-not $installed -and (Test-Path $lnk)) { throw "shortcut left behind: $lnk" }
    }
}

function Test-Decoder {
    # WinExe: Start-Process returns at once; Invoke-Limited waits for the exit code.
    $d = Invoke-Limited 'C:\Program Files\OADM\Client\Oadm.Client.exe' '--check-decoder' '' 120
    if ($d.ExitCode -ne 0) { throw "decoder check failed: $($d.ExitCode)" }
    # Everything the installed program needs: plugins with all their libraries, native libraries (docs/spec/packaging.md).
    $c = Invoke-Limited 'C:\Program Files\OADM\Client\Oadm.Client.exe' '--self-check' '' 120
    if ($c.ExitCode -ne 0) { throw "client self check failed: $($c.ExitCode)" }
    $server = 'C:\Program Files\OADM\Server\Oadm.Server.exe'
    if (Test-Path $server) {
        $s = Invoke-Limited $server '--self-check' '' 120
        if ($s.ExitCode -ne 0) { throw "server self check failed: $($s.ExitCode)" }
    }
}

Show-Machine

if ($Mode -eq 'SiteNode') {
    $node = 'C:\Program Files\OADM Site Node\oadm-site-node.exe'
    $data = 'C:\ProgramData\OADM Site Node'
    # The command line of the node: its output and exit code (the local channel answers within seconds). A hidden process
    # of its own, never in the runner's terminal: the terminal would keep a handle to the exe until the removal.
    function Invoke-Node([string[]] $arguments) {
        Say "oadm-site-node $($arguments -join ' ')"
        $info = [System.Diagnostics.ProcessStartInfo]::new($node)
        foreach ($a in $arguments) { $info.ArgumentList.Add($a) }
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $proc = [System.Diagnostics.Process]::Start($info)
        $errTask = $proc.StandardError.ReadToEndAsync()
        $out = $proc.StandardOutput.ReadToEnd() + $errTask.GetAwaiter().GetResult()
        if (-not $proc.WaitForExit(120000)) { $proc.Kill($true); throw "oadm-site-node $($arguments -join ' ') did not end within 120 s" }
        $code = $proc.ExitCode
        Write-Host $out
        if ($code -ne 0) { Get-ChildItem "$data\logs" -Filter *.log -ErrorAction SilentlyContinue | Get-Content -Tail 40 | Write-Host; throw "oadm-site-node $($arguments -join ' ') failed: $code" }
        $out
    }
    function Get-NodeRule { @(Get-NetFirewallRule -DisplayName 'OADM Site Node*' -ErrorAction SilentlyContinue) }

    $p = Invoke-Limited msiexec.exe "/i `"$Msi`" SERVER=oadm.example.com /qn /norestart /l*v install-node.log" install-node.log
    if ($p.ExitCode -ne 0) { Get-Content install-node.log -Tail 80 | Write-Host; throw "install failed: $($p.ExitCode) (3010 = restart required)" }
    $svc = Get-CimInstance Win32_Service -Filter "Name='OadmSiteNode'"
    Say "service: $($svc.State) $($svc.StartMode) $($svc.StartName)"
    if ($svc.State -ne 'Running') { throw 'service OadmSiteNode not running' }
    if ($svc.StartName -ne 'NT SERVICE\OadmSiteNode') { throw "service account NT SERVICE\OadmSiteNode expected, not $($svc.StartName)" }
    foreach ($f in 'TERMS.md', 'LICENSE.txt', 'NOTICE.txt', 'THIRD-PARTY-NOTICES.txt', 'oadm-site-node-app.exe') {
        if (-not (Test-Path "C:\Program Files\OADM Site Node\$f")) { throw "$f missing" }
    }
    if (Test-Path 'C:\Program Files\OADM Site Node\LGPL-2.1.txt') { throw 'the node has no LGPL part' }
    Test-Shortcuts 'OADM Site Node' $true
    if ((Get-NodeRule).Count -ne 0) { throw 'the node MSI must not add a firewall rule' }

    # SERVER=<address> reached the node; it is not bound (and cannot connect: the name does not exist).
    $status = Invoke-Node @('status')
    if ($status -notmatch 'Not bound') { throw 'status must say Not bound' }
    if ($status -notmatch 'oadm\.example\.com') { throw 'SERVER was not set' }
    # The data folder is for SYSTEM and the node's account only: an administrator cannot read inside it (status above shows
    # that the service wrote and read node.json), so the check is its permissions.
    $acl = (Get-Acl $data).Access
    $acl | Format-Table IdentityReference, FileSystemRights -AutoSize | Out-String | Write-Host
    $who = @($acl | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    if (($who -join ',') -ne 'NT AUTHORITY\SYSTEM,NT SERVICE\OadmSiteNode') { throw "data folder for SYSTEM and the node's account only, not: $($who -join ', ')" }

    # Another server address is taken; the node never listens, so still no firewall rule.
    $null = Invoke-Node @('server', 'oadm2.example.com')
    if ((Invoke-Node @('status')) -notmatch 'oadm2\.example\.com') { throw 'the new server address was not set' }
    if ((Get-NodeRule).Count -ne 0) { throw 'the node must not add a firewall rule' }

    Say 'stop the service'
    Stop-OadmService OadmSiteNode oadm-site-node
    Uninstall uninstall-node.log
    Wait-ServiceGone OadmSiteNode
    if (Test-Path $node) { throw 'node still installed' }
    Test-Shortcuts 'OADM Site Node' $false
    if ((Get-NodeRule).Count -ne 0) { throw 'firewall rule left behind' }
    if (-not (Test-Path $data)) { throw 'data folder was removed' }
    Say 'site node: install, service, status, no firewall rule, uninstall ok'
    return
}

if ($Mode -eq 'ClientOnly') {
    $p = Invoke-Limited msiexec.exe "/i `"$Msi`" ADDLOCAL=Client /qn /norestart /l*v install-client.log" install-client.log
    if ($p.ExitCode -ne 0) { Get-Content install-client.log -Tail 80 | Write-Host; throw "install failed: $($p.ExitCode) (3010 = restart required)" }
    if (Get-Service OadmServer -ErrorAction SilentlyContinue) { throw 'client only must not install the service' }
    if (Test-Path 'C:\Program Files\OADM\Server') { throw 'client only must not install the server' }
    if (Get-NetFirewallRule -DisplayName 'OADM Server*' -ErrorAction SilentlyContinue) { throw 'client only must not add a firewall rule' }
    if (-not (Test-Path 'C:\Program Files\OADM\Client\THIRD-PARTY-NOTICES.txt')) { throw 'client license texts missing' }
    if (-not (Test-Path 'C:\Program Files\OADM\Client\docs\index.html')) { throw 'client Help (docs) missing' }
    Test-Shortcuts 'OADM' $true
    Test-Decoder
    Uninstall uninstall-client.log
    if (Test-Path 'C:\Program Files\OADM\Client\Oadm.Client.exe') { throw 'client still installed' }
    Test-Shortcuts 'OADM' $false
    Say 'client only: install, decoder, uninstall ok'
    return
}

$p = Invoke-Limited msiexec.exe "/i `"$Msi`" /qn /norestart /l*v install.log" install.log
if ($p.ExitCode -ne 0) { Get-Content install.log -Tail 80 | Write-Host; throw "install failed: $($p.ExitCode) (3010 = restart required)" }
$svc = Get-Service OadmServer
Say "service: $($svc.Status) $($svc.StartType)"
$svc.Dispose()

# The first start creates the database, the certificate and the master key: a busy runner may need minutes.
Say 'wait for port 5080'
$ok = $false
$deadline = (Get-Date).AddSeconds(180)
while (-not $ok -and (Get-Date) -lt $deadline) {
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
# Help: the offline docs only next to the client (docs/spec/help.md).
if (-not (Test-Path 'C:\Program Files\OADM\Client\docs\index.html')) { throw 'client Help (docs) missing' }
if (Test-Path 'C:\Program Files\OADM\Server\docs') { throw 'the server has no Help (docs)' }
Test-Shortcuts 'OADM' $true
(Get-Acl C:\ProgramData\OADM).Access | Format-Table IdentityReference, FileSystemRights -AutoSize | Out-String | Write-Host
# TCP 5080 (clients) for Domain and Private; the server adds TCP 5081 only while Accept site nodes is on (off by default),
# the NTP and DHCP plugins their UDP rules only while enabled.
$rules = @(Get-NetFirewallRule -DisplayName 'OADM Server*' | Sort-Object DisplayName)
$rules | Format-Table DisplayName, Enabled, Direction, Profile -AutoSize | Out-String | Write-Host
if (($rules.DisplayName -join ',') -ne 'OADM Server (gRPC, TCP 5080)') { throw 'only the rule for TCP 5080 expected' }
foreach ($rule in $rules) {
    if (($rule.Profile.ToString() -replace ' ', '') -ne 'Domain,Private') { throw "profile Domain, Private expected, not $($rule.Profile)" }
}
# The server checked its folders at start (service mode) and did not refuse them.
if (Select-String -Path C:\ProgramData\OADM\logs\*.log -Pattern 'could not be fixed' -Quiet) { throw 'folder check failed' }
Show-ServerLog 30

Test-Decoder

# Stop the service first, so its exe is not in use while the files are removed (a file in use asks for a restart;
# /norestart turns that into exit code 3010 instead of a restart of the runner).
Say 'stop the service'
Stop-OadmService OadmServer Oadm.Server
Uninstall uninstall.log
Wait-ServiceGone OadmServer
if (Get-NetFirewallRule -DisplayName 'OADM Server*' -ErrorAction SilentlyContinue) { throw 'firewall rule left behind' }
if (-not (Test-Path C:\ProgramData\OADM\master.key)) { throw 'data folder was removed' }
Test-Shortcuts 'OADM' $false
Say 'server and client: install, service, gRPC, firewall, uninstall ok'
