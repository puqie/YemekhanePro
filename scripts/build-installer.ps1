[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',

    # Lisans imza sirri. Masaustu uygulamasi lisansi BU sirla dogrular; lisans sunucusu
    # da ayni sirla imzalar. Verilmezse yayinlanan uygulama acilista durur (csproj
    # icindeki InjectLicensingSigningSecret hedefi uyarir, -warnaserror ile hataya doner).
    # Depoya YAZILMAZ: ortam degiskeninden okunur ya da parametreyle gecilir.
    [string]$LicensingSigningSecret = $env:YEMEKHANE_LICENSING_SECRET,

    [switch]$SkipTests,
    [switch]$SkipSmoke,
    [switch]$SkipInstallCheck
)

if ([string]::IsNullOrWhiteSpace($LicensingSigningSecret)) {
    throw @'
Lisans imza sirri verilmedi; kurulum uretilse bile uygulama acilista dururdu.

Kullanim:
    $env:YEMEKHANE_LICENSING_SECRET = '<sir>'
    .\scripts\build-installer.ps1 -Version 1.1.0

Sir, lisans sunucusundaki Licensing:SigningSecret ile AYNI olmalidir; aksi halde
sunucunun imzaladigi lisanslari masaustu dogrulayamaz.
'@
}

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$desktopDir = Join-Path $publishRoot 'desktop'
$apiDir = Join-Path $desktopDir 'api'
$installerDir = Join-Path $artifacts 'installer'
$extractDir = Join-Path $artifacts 'installer-validation'
$configuration = 'Release'

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') başarısız oldu: $LASTEXITCODE" }
}

if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
if (Test-Path -LiteralPath $installerDir) { Remove-Item -LiteralPath $installerDir -Recurse -Force }
if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
New-Item -ItemType Directory -Path $desktopDir, $apiDir, $installerDir, $extractDir | Out-Null

Invoke-DotNet @('restore', (Join-Path $root 'Yemekhane.sln'))
Invoke-DotNet @('restore', (Join-Path $root 'src\Yemekhane.Desktop\Yemekhane.Desktop.csproj'), '-r', 'win-x64')
Invoke-DotNet @('restore', (Join-Path $root 'src\Yemekhane.Api\Yemekhane.Api.csproj'), '-r', 'win-x64')
Invoke-DotNet @('build', (Join-Path $root 'Yemekhane.sln'), '-c', $configuration, '--no-restore', '-warnaserror')
if (-not $SkipTests) {
    Invoke-DotNet @('test', (Join-Path $root 'Yemekhane.sln'), '-c', $configuration, '--no-build', '--no-restore', '-warnaserror')
}

$publishCommon = @('-c', $configuration, '-r', 'win-x64', '--self-contained', 'true', '--no-restore',
    '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false',
    "-p:Version=$Version", "-p:LicensingSigningSecret=$LicensingSigningSecret", '-warnaserror')
Invoke-DotNet (@('publish', (Join-Path $root 'src\Yemekhane.Desktop\Yemekhane.Desktop.csproj'), '-o', $desktopDir) + $publishCommon)
Invoke-DotNet (@('publish', (Join-Path $root 'src\Yemekhane.Api\Yemekhane.Api.csproj'), '-o', $apiDir) + $publishCommon)

$licenseDir = Join-Path $desktopDir 'licenses'
New-Item -ItemType Directory -Path $licenseDir | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src\Yemekhane.Reports\Assets\OFL.txt') -Destination (Join-Path $licenseDir 'NotoSans-OFL.txt')

# ProductCode Package.wxs icinde "*" ile her derlemede uretilir: surume gore sabitlenseydi
# ayni surumu yeniden kurmak 1638 ile reddedilirdi.
Invoke-DotNet @('build', (Join-Path $root 'installer\Yemekhane.Installer.wixproj'), '-c', $configuration,
    "-p:ProductVersion=$Version",
    "-p:PublishDir=$desktopDir", "-p:OutputPath=$installerDir", '-warnaserror')

# Kullanicinin cift tiklayacagi dosya .exe'dir: MSI cift tiklandiginda UAC yukseltmesi isteyemez
# ve perMachine paket Error 1925 ile sessizce iptal olur (kullanici yalnizca dolan bir bar gorur).
Invoke-DotNet @('build', (Join-Path $root 'installer-bundle\Yemekhane.Bundle.wixproj'), '-c', $configuration,
    "-p:ProductVersion=$Version", "-p:PublishDir=$desktopDir",
    "-p:MsiPath=$(Join-Path $installerDir "YemekhanePro-$Version-win-x64.msi")",
    "-p:OutputPath=$installerDir", '-warnaserror')

$setup = Get-Item -LiteralPath (Join-Path $installerDir "YemekhanePro-Setup-$Version.exe")
$msi = Get-Item -LiteralPath (Join-Path $installerDir "YemekhanePro-$Version-win-x64.msi")
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$database = $windowsInstaller.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $windowsInstaller, @($msi.FullName, 0))
$view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database,
    @("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"))
$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
$record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
$msiVersion = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
if ($msiVersion -ne $Version) { throw "MSI ProductVersion beklenenden farklı: $msiVersion" }

$msiexec = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/a', "`"$($msi.FullName)`"", '/qn', "TARGETDIR=`"$extractDir`"") -Wait -PassThru
if ($msiexec.ExitCode -ne 0) { throw "MSI administrative extraction başarısız oldu: $($msiexec.ExitCode)" }
$extractedDesktop = Get-ChildItem -LiteralPath $extractDir -Filter 'Yemekhane.Desktop.exe' -Recurse
$extractedApi = Get-ChildItem -LiteralPath $extractDir -Filter 'Yemekhane.Api.exe' -Recurse
if (-not $extractedDesktop -or -not $extractedApi) { throw 'MSI extraction beklenen yürütülebilir dosyaları içermiyor.' }

if (-not $SkipSmoke) {
    $port = 5262
    $smokeData = Join-Path $artifacts 'smoke-data'
    if (Test-Path -LiteralPath $smokeData) { Remove-Item -LiteralPath $smokeData -Recurse -Force }
    New-Item -ItemType Directory -Path $smokeData | Out-Null
    $secretBytes = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($secretBytes) } finally { $rng.Dispose() }
    $secret = [Convert]::ToBase64String($secretBytes)
    $apiStart = New-Object Diagnostics.ProcessStartInfo
    $apiStart.FileName = Join-Path $apiDir 'Yemekhane.Api.exe'
    $apiStart.Arguments = "--urls http://127.0.0.1:$port"
    $apiStart.WorkingDirectory = $apiDir
    $apiStart.UseShellExecute = $false
    $apiStart.CreateNoWindow = $true
    $apiStart.EnvironmentVariables['YEMEKHANE_Authentication__Jwt__SigningKey'] = $secret
    $apiStart.EnvironmentVariables['YEMEKHANE_LocalDatabase__DataDirectory'] = $smokeData
    $api = [Diagnostics.Process]::Start($apiStart)
    try {
        $healthy = $false
        for ($attempt = 0; $attempt -lt 120; $attempt++) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/health" -TimeoutSec 2
                if ($response.StatusCode -eq 200) { $healthy = $true; break }
            } catch { Start-Sleep -Milliseconds 250 }
        }
        if (-not $healthy) { throw 'Published API health smoke başarısız oldu.' }

        $desktopStart = New-Object Diagnostics.ProcessStartInfo
        $desktopStart.FileName = Join-Path $desktopDir 'Yemekhane.Desktop.exe'
        $desktopStart.WorkingDirectory = $desktopDir
        $desktopStart.UseShellExecute = $false
        $desktopStart.EnvironmentVariables['YEMEKHANE_Api__BaseUri'] = "http://127.0.0.1:$port/"
        $desktop = [Diagnostics.Process]::Start($desktopStart)
        Start-Sleep -Seconds 3
        if ($desktop.HasExited) { throw "Published Desktop smoke sırasında kapandı: $($desktop.ExitCode)" }
        $desktop.Kill()
        $desktop.WaitForExit()
    } finally {
        if ($api -and -not $api.HasExited) { $api.Kill(); $api.WaitForExit() }
    }
}

# Gercek kurulum dogrulamasi: administrative extraction MSI'nin acilabildigini gosterir ama
# kurulumun gercekten calistigini gostermez (1638 "zaten yuklu" hatasi tam olarak burada kacmisti).
# Yonetici hakki yoksa atlanir; CI'da -SkipInstallCheck ile de kapatilabilir.
if (-not $SkipInstallCheck) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Warning 'Kurulum dogrulamasi atlandi: yonetici hakki yok (perMachine paket icin gerekli).'
    } else {
        $installLog = Join-Path $artifacts 'install-verify.log'
        $install = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
            '/i', "`"$($msi.FullName)`"", '/qn', '/l*v', "`"$installLog`"")
        if ($install.ExitCode -ne 0) { throw "MSI kurulumu basarisiz oldu ($($install.ExitCode)). Gunluk: $installLog" }

        # Ayni surumu tekrar kurmak calismalidir: onarim ve basarisiz dagitimi yenileme bu yolu kullanir.
        $reinstall = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
            '/i', "`"$($msi.FullName)`"", '/qn', '/l*v', "`"$installLog`"")
        if ($reinstall.ExitCode -ne 0) { throw "Ayni surumun uzerine kurulum basarisiz oldu ($($reinstall.ExitCode)). Gunluk: $installLog" }

        $uninstall = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
            '/x', "`"$($msi.FullName)`"", '/qn', '/l*v', "`"$installLog`"")
        if ($uninstall.ExitCode -ne 0) { throw "MSI kaldirma basarisiz oldu ($($uninstall.ExitCode)). Gunluk: $installLog" }
    }
}

$setupHash = Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256
$msiHash = Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256
[pscustomobject]@{
    Setup = $setup.FullName
    SetupBytes = $setup.Length
    SetupSHA256 = $setupHash.Hash
    Msi = $msi.FullName
    MsiSHA256 = $msiHash.Hash
    Version = $Version
} | Format-List
