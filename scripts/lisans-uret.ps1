<#
.SYNOPSIS
Sunucusuz lisans anahtari uretir.

.DESCRIPTION
Aktivasyon sunucusu OLMADAN lisans satmak icin kullanilir. Uretilen anahtar,
kuruluma gomulu imza sirriyla dogrulanabilir; musteri anahtari programa girer,
program sunucuya hic baglanmadan lisansi kendisi olusturur ve o bilgisayara baglar.

Anahtar SIRRA bagli oldugu icin, sirri bilmeyen gecerli anahtar uretemez.

.PARAMETER Secret
Imza sirri. Kurulum uretilirken kullanilan YEMEKHANE_LICENSING_SECRET ile AYNI
olmalidir; farkli olursa musteri "Lisans anahtari gecersiz" hatasi alir.
Verilmezse ortam degiskeninden okunur.

.PARAMETER Count
Uretilecek anahtar sayisi. Varsayilan 1.

.PARAMETER Customer
Not amacli musteri adi; ciktiya yazilir, anahtarin icine GIRMEZ.

.EXAMPLE
$env:YEMEKHANE_LICENSING_SECRET = '<sir>'
.\scripts\lisans-uret.ps1 -Customer "Ataturk Ilkokulu"

.EXAMPLE
.\scripts\lisans-uret.ps1 -Count 10 -Csv satislar.csv
#>
[CmdletBinding()]
param(
    [string]$Secret = $env:YEMEKHANE_LICENSING_SECRET,
    [ValidateRange(1, 500)]
    [int]$Count = 1,
    [string]$Customer = '',
    [string]$Csv = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Secret)) {
    throw @'
Imza sirri verilmedi.

Kullanim:
    $env:YEMEKHANE_LICENSING_SECRET = '<sir>'
    .\scripts\lisans-uret.ps1 -Customer "Okul Adi"

Sir, kurulum uretilirken kullanilan ile AYNI olmalidir; aksi halde uretilen
anahtar musterinin kurulumunda "gecersiz" gorunur.
'@
}

# Anahtar alfabesi: karisabilecek karakterler (0/O, 1/I/L) disarida. Anahtar telefonda
# okunup elle yaziliyor; "sifir mi O mu" sorusu destege gereksiz cagri yaratir.
$alphabet = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'

function New-Block {
    $chars = for ($i = 0; $i -lt 4; $i++) {
        $alphabet[(Get-Random -Maximum $alphabet.Length)]
    }
    -join $chars
}

# Son blok: govdenin imza sirriyla hesaplanmis kisaltilmis HMAC'i.
# Masaustundeki OfflineLicenseKey.Checksum ile AYNI hesabi yapar; ikisi ayrilirsa
# uretilen anahtarlar sahada reddedilir.
function Get-Checksum([string]$body) {
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($Secret))
    try {
        $hash = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($body))
        $chars = for ($i = 0; $i -lt 4; $i++) {
            $alphabet[($hash[$i] % $alphabet.Length)]
        }
        -join $chars
    }
    finally { $hmac.Dispose() }
}

$year = (Get-Date).Year
$rows = for ($i = 0; $i -lt $Count; $i++) {
    $body = "YMK-$year-$(New-Block)-$(New-Block)"
    [pscustomobject]@{
        Anahtar = "$body-$(Get-Checksum $body)"
        Musteri = $Customer
        Tarih   = (Get-Date).ToString('yyyy-MM-dd')
    }
}

$rows | Format-Table -AutoSize

if (-not [string]::IsNullOrWhiteSpace($Csv)) {
    # Append: satis kaydiniz birikerek gitsin, her calistirmada silinmesin.
    $exists = Test-Path -LiteralPath $Csv
    $rows | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding utf8 -Append:$exists
    Write-Host ""
    Write-Host "Kaydedildi: $Csv" -ForegroundColor Green
}

Write-Host ""
Write-Host "Anahtari musteriye verin. Musteri programa girdiginde lisans" -ForegroundColor Cyan
Write-Host "O BILGISAYARA baglanir; baska bilgisayarda calismaz." -ForegroundColor Cyan
