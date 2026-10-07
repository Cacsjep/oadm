# Start the server in the background, then the client. Stopping the client stops the server.
. (Join-Path $PSScriptRoot '_common.ps1')

& (Join-Path $PSScriptRoot 'build.ps1')
$logDir = Join-Path $RepoRoot 'artifacts/logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$server = Start-Process -FilePath $Dotnet -ArgumentList @('run','--no-build','--project',$ServerProject,'-c',$Configuration) -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $logDir 'server-dev.log') -RedirectStandardError (Join-Path $logDir 'server-dev.err.log')
Write-Host "server started (pid $($server.Id)), log: artifacts/logs/server-dev.log"
try {
    Start-Sleep -Seconds 3
    Invoke-Dotnet run --no-build --project $ClientProject -c $Configuration -- @args
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
