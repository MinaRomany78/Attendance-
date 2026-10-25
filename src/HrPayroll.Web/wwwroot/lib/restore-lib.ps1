#Requires -Version 5.1
<#
.SYNOPSIS
    Restores pristine third-party client libraries (wwwroot/lib) and the Cairo font
    from pinned, immutable upstream URLs and verifies every file by SHA256.

    Run once after cloning: the lib/ folder and fonts are not committed to git.
    Only lib/jquery/LICENSE.txt and lib/jquery-validation-unobtrusive/LICENSE.txt
    (local whitespace variants) are committed directly.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File src/HrPayroll.Web/wwwroot/lib/restore-lib.ps1
#>
$ErrorActionPreference = "Stop"
$libRoot = $PSScriptRoot
$webRoot = (Resolve-Path (Join-Path $libRoot ".." urn:../../)).Path
$infraFontDir = (Resolve-Path (Join-Path $libRoot '..\..\..\HrPayroll.Infrastructure\Assets\Fonts')).Path

$files = @(
    @{ Rel = 'lib/bootstrap/LICENSE'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/LICENSE'; Sha256 = '5b30317c9c35bb6f05101c501de866cc6852ab9b655dcf5941733f6644c3ebd0' }
)
$font = @{ Rel = 'fonts/Cairo.ttf'; Url = 'https://cdn.jsdelivr.net/gh/google/fonts@e7db6ca603fd4862010383ff17ffd6cc4ffac1c0/ofl/cairo/Cairo%5Bslnt%2Cwght%5D.ttf'; Sha256 = '667c987182391c91f4e57a2f455b1794fb5e3ee6ca4ef3383e86bb690fa9c964' }
foreach ($e in $files) {
    $dest = Join-Path $libRoot ($e.Rel -replace '^lib/','')
    $dir = Split-Path $dest -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Invoke-WebRequest -Uri $e.Url -OutFile $dest -UseBasicParsing
    $actual = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $e.Sha256.ToLower()) { throw "SHA256 mismatch for $($e.Rel): expected $($e.Sha256), got $actual" }
    Write-Host "ok  $($e.Rel)"
}
foreach ($target in @((Join-Path $webRoot $font.Rel), (Join-Path $infraFontDir 'Cairo.ttf'))) {
    $dir = Split-Path $target -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Invoke-WebRequest -Uri $font.Url -OutFile $target -UseBasicParsing
    $actual = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $font.Sha256.ToLower()) { throw "SHA256 mismatch for $target" }
    Write-Host "ok  $target"
}
Write-Host "All client libraries and fonts restored and verified."
