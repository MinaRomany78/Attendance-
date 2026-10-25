#Requires -Version 5.1
<#
.SYNOPSIS
    Restores bundled third-party client libraries (bootstrap-js) from pinned,
    immutable upstream URLs and verifies every file by SHA256.
    Run once after cloning. See README.md for details.
#>
$ErrorActionPreference = "Stop"
$libRoot = $PSScriptRoot

$files = @(
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.bundle.js'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.bundle.js'; Sha256 = 'bd04dfe1ddd6262f6f99640d0389099e3a1e7601213055c50c4317aad1ed8339' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.bundle.js.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.bundle.js.map'; Sha256 = '80ee2e8717c6b8ad0e3634651eec1f82119f1174f96b39b5a075a74c41464df9' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.bundle.min.js'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.bundle.min.js'; Sha256 = '2aebc2552d7dadf4e3a0b80cc830c274e91146584dad8e29b04338b9ecedb363' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.bundle.min.js.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.bundle.min.js.map'; Sha256 = 'c48041c433ef5a193cfc975a1446687a369d7da28551f645c114b50f8264baba' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.esm.js'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.esm.js'; Sha256 = '12e0d750960a9df66e3bc7522cdd1fe6255b55ab33dfa0113ae014dcd277241a' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.esm.js.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.esm.js.map'; Sha256 = '0a5942fec6cbbf22c4f5c4258e61519484dfa9d6519c1bf6cac5f92c9b65fdd8' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.esm.min.js'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.esm.min.js'; Sha256 = '2a3e22ad058fc1f49be4d15ea2cfe1d08ae8239fe7220d07b408d0fb0e2fe931' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.esm.min.js.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.esm.min.js.map'; Sha256 = 'b0fab359c492f5a45ada0a564d5350cde99a8e7f21ac58e05cf8f78fd408b50a' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.js'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.js'; Sha256 = 'e884ad4507ab05c8584b0e89d8659339618e9c346b59799a1b4afa9c2c0de6ce' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.js.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.js.map'; Sha256 = '424979999519eb869805a39144c3fd8df0f5933f09e85c2257633ce890c991d4' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.min.js'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.min.js'; Sha256 = '7382e5e9e48883a128b6193ca4258017c684f76dc4bed535d69aa3072f8d8cd3' }
    @{ Rel = 'lib/bootstrap/dist/js/bootstrap.min.js.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/js/bootstrap.min.js.map'; Sha256 = 'ba2fc5408fb2d0852c63c3db8bcd1bf2cdc67842fe3ecbdd68b4ce3686e99fcf' }
)
foreach ($e in $files) {
    $dest = Join-Path $libRoot ($e.Rel -replace '^lib/','')
    $dir = Split-Path $dest -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Invoke-WebRequest -Uri $e.Url -OutFile $dest -UseBasicParsing
    $actual = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $e.Sha256.ToLower()) { throw "SHA256 mismatch for $($e.Rel)" }
    Write-Host "ok  $($e.Rel)"
}
Write-Host "bootstrap-js restored and verified."
