#Requires -Version 5.1
<#
.SYNOPSIS
    Restores bundled third-party client libraries (vendor-js) from pinned,
    immutable upstream URLs and verifies every file by SHA256.
    Run once after cloning. See README.md for details.
#>
$ErrorActionPreference = "Stop"
$libRoot = $PSScriptRoot

$files = @(
    @{ Rel = 'lib/jquery-validation-unobtrusive/jquery.validate.unobtrusive.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery-validation-unobtrusive@4.0.0/dist/jquery.validate.unobtrusive.js'; Sha256 = 'c496c130b1a161b5e53ebaeb75dad256676ec85e8a12d6f112c631c3b858655e' }
    @{ Rel = 'lib/jquery-validation-unobtrusive/jquery.validate.unobtrusive.min.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery-validation-unobtrusive@4.0.0/dist/jquery.validate.unobtrusive.min.js'; Sha256 = '44558558820fb230780ee711e23ab0c535b0d77666b48facead551d8b2666579' }
    @{ Rel = 'lib/jquery-validation/LICENSE.md'; Url = 'https://cdn.jsdelivr.net/npm/jquery-validation@1.19.5/LICENSE.md'; Sha256 = 'f398878cab338b869638bdac1aeae76bf3ac11b2b89da6e0b68bc1a645733440' }
    @{ Rel = 'lib/jquery-validation/dist/additional-methods.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery-validation@1.19.5/dist/additional-methods.js'; Sha256 = 'd0778c5b24146d36deed21a74986e38fdf87540d2128398452d6d8a1329ef819' }
    @{ Rel = 'lib/jquery-validation/dist/additional-methods.min.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery-validation@1.19.5/dist/additional-methods.min.js'; Sha256 = 'f74465ceecfca4864ae20f68d88ee718afbd9f9714f516ddb781adc513b96ab0' }
    @{ Rel = 'lib/jquery-validation/dist/jquery.validate.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery-validation@1.19.5/dist/jquery.validate.js'; Sha256 = '9b497cd560cf886ec2706ec20ec4ee673bdc1afc859ab418e432c8c71dda446c' }
    @{ Rel = 'lib/jquery-validation/dist/jquery.validate.min.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery-validation@1.19.5/dist/jquery.validate.min.js'; Sha256 = '270524b0d27afd1d3b6622d1a176c678daed94564c143297e217a63e21ce9820' }
    @{ Rel = 'lib/jquery/dist/jquery.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery@3.6.0/dist/jquery.js'; Sha256 = '1fe2bb5390a75e5d61e72c107cab528fc3c29a837d69aab7d200e1dbb5dcd239' }
    @{ Rel = 'lib/jquery/dist/jquery.min.js'; Url = 'https://cdn.jsdelivr.net/npm/jquery@3.6.0/dist/jquery.min.js'; Sha256 = 'ff1523fb7389539c84c65aba19260648793bb4f5e29329d2ee8804bc37a3fe6e' }
    @{ Rel = 'lib/jquery/dist/jquery.min.map'; Url = 'https://code.jquery.com/jquery-3.6.0.min.map'; Sha256 = '399548fb0e7b146c12f5ba18099a47d594a970fee96212eee0ab4852f3e56782' }
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
Write-Host "vendor-js restored and verified."
$webRoot = (Resolve-Path (Join-Path $libRoot ".." urn:../../)).Path
