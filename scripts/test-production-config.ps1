[CmdletBinding()]
param(
    [ValidateSet('Local', 'Remote')]
    [string]$Profile = 'Local',
    [string]$ConfigurationFile,
    [switch]$RequireEnvironment
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$file = if ($ConfigurationFile) { $ConfigurationFile } elseif ($Profile -eq 'Remote') {
    Join-Path $root 'deploy\appsettings.Remote.template.json'
} else { Join-Path $root 'deploy\appsettings.Local.json' }
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Yapılandırma bulunamadı: $file" }
$config = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
if ($config.Deployment.Mode -ne $Profile) { throw 'Deployment modu profil ile eşleşmiyor.' }
if ($config.Deployment.TimeZone -ne 'Europe/Istanbul') { throw 'Timezone Europe/Istanbul olmalıdır.' }
if ($Profile -eq 'Remote') {
    if (-not $config.Kestrel.Endpoints.Https.Url.StartsWith('https://')) { throw 'Remote profil HTTPS endpoint gerektirir.' }
    if (-not $config.Kestrel.Endpoints.Https.Certificate.Path) { throw 'Remote profil sertifika yolu gerektirir.' }
    if ($config.Kestrel.Endpoints.Https.Certificate.Password) { throw 'Sertifika parolası dosyada bulunamaz; YEMEKHANE_Kestrel__Endpoints__Https__Certificate__Password kullanın.' }
    if ($config.Deployment.ForwardedHeadersEnabled -and $config.Deployment.KnownProxies.Count -eq 0) { throw 'Proxy allowlist zorunludur.' }
}
$raw = Get-Content -LiteralPath $file -Raw
if ($raw -match '(?i)SigningKey\s*"\s*:\s*"[^"\s]+' -or $raw -match '(?i)Password\s*"\s*:\s*"[^"\s]+') {
    throw 'Yapılandırma dosyasında secret bulundu.'
}
$required = @('YEMEKHANE_Authentication__Jwt__SigningKey')
if ($Profile -eq 'Remote') { $required += 'YEMEKHANE_Kestrel__Endpoints__Https__Certificate__Password' }
if ($RequireEnvironment) {
    foreach ($name in $required) {
        if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Zorunlu environment değişkeni eksik: $name" }
    }
}
[pscustomobject]@{ Profile = $Profile; File = (Resolve-Path -LiteralPath $file).Path; RequiredEnvironment = ($required -join ', '); Valid = $true }
