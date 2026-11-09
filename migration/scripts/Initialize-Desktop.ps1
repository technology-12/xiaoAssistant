param(
    [string]$GatewayUrl = 'http://127.0.0.1:4388',
    [string]$CandidatePath = '',
    [string]$TokenFile = ''
)
$ErrorActionPreference = 'Stop'
$migrationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$CandidatePath) { $CandidatePath = [IO.Path]::GetFullPath((Join-Path $migrationRoot '../artifacts/opencode-candidate')) }
if (!$TokenFile) { $TokenFile = Join-Path $migrationRoot '.local/gateway.token' }
if (!(Test-Path -LiteralPath $TokenFile)) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($TokenFile)) -Force | Out-Null
    $randomBytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($randomBytes) } finally { $random.Dispose() }
    $newToken = [BitConverter]::ToString($randomBytes).Replace('-', '').ToLowerInvariant()
    [IO.File]::WriteAllText($TokenFile, $newToken, [Text.Encoding]::UTF8)
}
$gatewayUri = [Uri]$GatewayUrl
if ($gatewayUri.Scheme -ne 'https' -and !($gatewayUri.Scheme -eq 'http' -and $gatewayUri.IsLoopback)) { throw 'Remote gateway requires HTTPS.' }
Add-Type -AssemblyName System.Security
$token = [IO.File]::ReadAllText($TokenFile).Trim()
if ($token.Length -lt 32) { throw 'Gateway token must contain at least 32 characters.' }
$cipher = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($token), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$config = @{ Enabled = $true; BaseUrl = $GatewayUrl.TrimEnd('/'); ProtectedToken = [Convert]::ToBase64String($cipher) }
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $CandidatePath 'opencode.connection.json') -Encoding UTF8
Write-Output 'Desktop gateway configuration prepared (Windows current-user encryption; token not printed).'
