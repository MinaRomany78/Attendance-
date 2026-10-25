#Requires -Version 5.1
<#
.SYNOPSIS
    Restores Cairo font with SHA256 verification.
#>
$ErrorActionPreference = "Stop"
$libRoot = $PSScriptRoot
$webRoot = (Resolve-Path (Join-Path $libRoot "..")).Path
$fontUrl = 'https://cdn.jsdelivr.net/gh/google/fonts@e7db6ca603fd4862010383ff17ffd6cc4ffac1c0/ofl/cairo/Cairo%5Bslnt%2Cwght%5D.ttf'
$fontSha = '667c987182391c91f4e57a2f455b1794fb5e3ee6ca4ef3383e86bb690fa9c964'
foreach ($t in @((Join-Path $webRoot 'fonts/Cairo.ttf'))) {
  $d = Split-Path $t -Parent
  if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
  Invoke-WebRequest -Uri $fontUrl -OutFile $t -UseBasicParsing
  $a = (Get-FileHash -Path $t -Algorithm SHA256).Hash.ToLower()
  if ($a -ne $fontSha.ToLower()) { throw "SHA256 mismatch" }
  Write-Host "ok $t"
}
Write-Host "font restored."
