param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory=$true)][uri]$InstallerUrl,
    [ValidateSet('stable','preview')][string]$Channel = 'stable',
    [string]$DotNet = 'dotnet.exe',
    [Parameter(Mandatory=$true)][string]$SignTool,
    [Parameter(Mandatory=$true)][string]$InnoCompiler,
    [uri]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($InstallerUrl.Scheme -ne 'https' -or $InstallerUrl.UserInfo -or $InstallerUrl.Fragment) { throw 'InstallerUrl must be HTTPS without credentials or fragment.' }
if ($TimestampUrl.AbsoluteUri -match '["\s]' -or $TimestampUrl.Scheme -notin @('https','http')) { throw 'Invalid timestamp URL.' }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le [DateTime]::Now) { throw 'A current-user code signing certificate with private key is required.' }
$sha = [Security.Cryptography.SHA256]::Create()
try { $publisherHash = [BitConverter]::ToString($sha.ComputeHash($certificate.RawData)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
$payload = Join-Path $projectRoot "artifacts\signed\$Version\payload"
$packages = Join-Path $projectRoot "artifacts\signed\$Version\packages"
if (Test-Path -LiteralPath $payload) { throw 'This release output already exists. Use a new version or explicitly archive the existing release before rebuilding.' }
New-Item -ItemType Directory -Path $payload,$packages -Force | Out-Null
& $DotNet restore (Join-Path $projectRoot 'src\WorkAssistant.Desktop\WorkAssistant.Desktop.csproj') -m:1 --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& $DotNet build (Join-Path $projectRoot 'src\WorkAssistant.Desktop\WorkAssistant.Desktop.csproj') -c Release --no-restore -m:1 -p:UseSharedCompilation=false "-p:Version=$Version" "-p:PublisherCertificateSha256=$publisherHash" -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\THIRD_PARTY_NOTICES.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\third-party-licenses') -Destination $payload -Recurse
New-Item -ItemType Directory -Path (Join-Path $payload 'samples') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'samples\workspace') -Destination (Join-Path $payload 'samples') -Recurse
# No tests run here. Acceptance is a separate work package.
Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -in @('.exe','.dll') } | ForEach-Object {
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($_.Name)" }
}
Add-Type -AssemblyName System.Security
function Write-SignedManifest([hashtable]$Manifest, [string]$Target) {
    $json = $Manifest | ConvertTo-Json -Depth 8
    $content = [Security.Cryptography.Pkcs.ContentInfo]::new([Text.Encoding]::UTF8.GetBytes($json))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new($content,$false)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::WholeChain
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes($Target,$cms.Encode())
}
$release = @{ Purpose='release'; Version=$Version; Channel=$Channel; MinimumSchema=1; MaximumSchema=1;
    ExecutableSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'WorkAssistant.Desktop.exe') -Algorithm SHA256).Hash.ToLowerInvariant();
    PublishedUtc=[DateTime]::UtcNow.ToString('o'); ExpiresUtc=[DateTime]::UtcNow.AddYears(10).ToString('o') }
Write-SignedManifest $release (Join-Path $payload 'release.p7m')
$signCommand = '"' + [IO.Path]::GetFullPath($SignTool) + '" sign /sha1 ' + $CertificateThumbprint + ' /fd SHA256 /tr ' + $TimestampUrl.AbsoluteUri + ' /td SHA256 $f'
& $InnoCompiler "/DAppVersion=$Version" "/DSourceDir=$payload" "/DOutputDir=$packages" "/Srelease=$signCommand" (Join-Path $projectRoot 'installer\WorkAssistant.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $packages "WorkAssistant-$Version-setup.exe"
& $SignTool verify /pa /all $installer
if ($LASTEXITCODE -ne 0) { throw 'Installer signature verification failed.' }
$update = @{ Purpose='update'; Version=$Version; Channel=$Channel; MinimumSchema=1; MaximumSchema=1; InstallerUrl=$InstallerUrl.AbsoluteUri;
    Sha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant(); Bytes=(Get-Item -LiteralPath $installer).Length;
    PublishedUtc=[DateTime]::UtcNow.ToString('o'); ExpiresUtc=[DateTime]::UtcNow.AddDays(30).ToString('o') }
Write-SignedManifest $update (Join-Path $packages "$Channel.p7m")
Write-Output "Signed release created: $packages"
