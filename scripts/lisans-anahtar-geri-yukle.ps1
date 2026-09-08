[CmdletBinding()]
param(
    # Depodaki anahtar cifti: tek satir, "ozel|acik" (her ikisi base64 DER).
    [string]$AnahtarDosyasi = (Join-Path $PSScriptRoot '..\secrets\lisans-anahtar-cifti.key'),

    # Lisans Uretici'nin okudugu DPAPI dosyasi. Bu Windows hesabina baglidir; baska hesap/PC
    # okuyamaz -- bu yuzden yeni bilgisayarda bu betik calistirilir.
    [string]$Hedef = (Join-Path $env:LOCALAPPDATA 'YemekhanePro\lisans-anahtar-cifti.dat'),

    [switch]$Force
)

# Depodaki anahtar ciftini bu Windows hesabinin DPAPI kasasina yazar. Ayni ciftle devam
# edilir: daha once dagitilan kurulumlar ve .lic dosyalari gecerli kalir. Entropi ve bicim
# Lisans Uretici (Yemekhane.KeyTool) ile birebir ayni olmak ZORUNDADIR.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

if (-not (Test-Path $AnahtarDosyasi)) { throw "Anahtar dosyasi bulunamadi: $AnahtarDosyasi" }
$pair = (Get-Content -Raw -Encoding UTF8 $AnahtarDosyasi).Trim()
$parts = $pair.Split('|')
if ($parts.Count -ne 2) { throw 'Anahtar dosyasi "ozel|acik" biciminde degil.' }

# DER basligi: ozel PKCS#8 (30 81 ...), acik SubjectPublicKeyInfo (30 59 ...). Ters yazilmis
# dosya sessizce kabul edilmesin.
$private = [Convert]::FromBase64String($parts[0]); $public = [Convert]::FromBase64String($parts[1])
if ($private[0] -ne 0x30 -or $private[1] -ne 0x81) { throw 'Ilk parca ozel anahtar (PKCS#8) gibi gorunmuyor.' }
if ($public[0] -ne 0x30 -or $public[1] -ne 0x59 -or $public.Length -ne 91) { throw 'Ikinci parca P-256 acik anahtari gibi gorunmuyor.' }

if ((Test-Path $Hedef) -and -not $Force) {
    throw "Hedefte zaten bir anahtar cifti var: $Hedef. Uzerine yazmak icin -Force verin (eski cift kaybolur)."
}

$entropy = [Text.Encoding]::UTF8.GetBytes('YemekhanePro.LisansUretici.v1')
$bytes = [Security.Cryptography.ProtectedData]::Protect(
    [Text.Encoding]::UTF8.GetBytes($pair), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Hedef) | Out-Null
[IO.File]::WriteAllBytes($Hedef, $bytes)
Write-Host "Anahtar cifti geri yuklendi: $Hedef" -ForegroundColor Green
Write-Host 'Lisans Uretici artik bu ciftle lisans ve kurulum uretir; "Yeni cift uret" demeyin.' -ForegroundColor Yellow
