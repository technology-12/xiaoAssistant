param([int]$Port = 4388)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'WorkAssistant.Desktop' -ErrorAction SilentlyContinue) { throw 'Please exit the existing Xiao assistant and its tray instance before launching this build.' }
$migrationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate = [IO.Path]::GetFullPath((Join-Path $migrationRoot '../artifacts/opencode-candidate'))
& (Join-Path $PSScriptRoot 'Initialize-Desktop.ps1') -GatewayUrl "http://127.0.0.1:$Port" -CandidatePath $candidate
$tokenFile = Join-Path $migrationRoot '.local/gateway.token'
$token = [IO.File]::ReadAllText($tokenFile).Trim()
$headers = @{ Authorization = 'Bearer ' + $token }
$healthUrl = "http://127.0.0.1:$Port/health"
$ready = $false
try { $health = Invoke-RestMethod -Uri $healthUrl -Headers $headers -TimeoutSec 2; $ready = $health.upstreamVersion -eq '1.1.20' } catch { }
if (!$ready) {
    $launcherRoot = Join-Path $migrationRoot '.local/launcher'
    New-Item -ItemType Directory -Path $launcherRoot -Force | Out-Null
    $shellPath = (Get-Process -Id $PID).Path
    $scriptPath = Join-Path $PSScriptRoot 'Start-Gateway.ps1'
    Start-Process -FilePath $shellPath -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'), '-Port', $Port) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $launcherRoot 'gateway.out.log') -RedirectStandardError (Join-Path $launcherRoot 'gateway.err.log') | Out-Null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        try { $health = Invoke-RestMethod -Uri $healthUrl -Headers $headers -TimeoutSec 1; $ready = $health.upstreamVersion -eq '1.1.20'; if ($ready) { break } } catch { }
    }
}
if (!$ready) { throw 'Gateway did not become ready. Inspect migration/.local/launcher/gateway.err.log.' }
Start-Process -FilePath (Join-Path $candidate 'WorkAssistant.Desktop.exe') -WorkingDirectory $candidate -WindowStyle Normal
