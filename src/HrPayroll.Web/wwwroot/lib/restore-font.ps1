#Requires -Version 5.1
<#
.SYNOPSIS
    Restores the Cairo font (wwwroot/fonts + PDF-embedded copy) from a pinned
    google/fonts commit and verifies every file by SHA256.
    Run once after cloning. See README.md for details.
#>
$ErrorActionPreference = "Stop"
$libRoot = $PSScriptRoot
$webRoot = (Resolve-Path (Join-Path $libRoot "..")).Path
$infraFontDir = (Resolve-Path (Join-Path $libRoot '..\..\..\HrPayroll.Infrastructure\Assets\Fonts')).Path
$fontUrl = 'https://cdn.jsdelivr.net/gh/google/fonts@e7db6ca603fd4862010383ff17ffd6cc4ffac1c0/ofl/cairo/Cairo%5Bslnt%2Cwght%5D.ttf'
$fontSha = '667c987182391c91f4e57a2f455b1794fb5e3ee6ca4ef3383e86bb690fa9c964'
foreach ($target in @((Join-Path $webRoot 'fonts/Cairo.ttf'), (Join-Path $infraFontDir 'Cairo.ttf'))) {
    $dir = Split-Path $target -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Invoke-WebRequest -Uri $fontUrl -OutFile $target -UseBasicParsing
    $actual = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $fontSha.ToLower()) { throw "SHA256 mismatch for $target" }
    Write-Host "ok  $target"
}
Write-Host "font restored and verified."
