param([int]$Port = 4388, [string]$ListenAddress = '127.0.0.1')
$ErrorActionPreference = 'Stop'
$migrationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokenFile = Join-Path $migrationRoot '.local/gateway.token'
if (!(Test-Path -LiteralPath $tokenFile)) {
    Write-Error 'Run Initialize-Desktop.ps1 first, or create .local/gateway.token with a random token of at least 32 characters.'
    exit 1
}
if (!(Test-Path -LiteralPath (Join-Path $migrationRoot 'gateway/node_modules/opencode-ai/package.json'))) {
    Push-Location (Join-Path $migrationRoot 'gateway')
    try { & npm.cmd ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } }
    finally { Pop-Location }
}
$env:XIAO_GATEWAY_TOKEN_FILE = $tokenFile
$env:XIAO_DATA_ROOT = Join-Path $migrationRoot '.local/server'
$env:XIAO_PORT = [string]$Port
$env:XIAO_HOST = $ListenAddress
& node (Join-Path $migrationRoot 'gateway/src/server.mjs')
exit $LASTEXITCODE
