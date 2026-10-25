#Requires -Version 5.1
<#
.SYNOPSIS
    Restores bundled third-party client libraries (bootstrap-css) from pinned,
    immutable upstream URLs and verifies every file by SHA256.
    Run once after cloning. See README.md for details.
#>
$ErrorActionPreference = "Stop"
$libRoot = $PSScriptRoot

$files = @(
    @{ Rel = 'lib/bootstrap/LICENSE'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/LICENSE'; Sha256 = '5b30317c9c35bb6f05101c501de866cc6852ab9b655dcf5941733f6644c3ebd0' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.css'; Sha256 = '26d92d822b9001df805deac29cf32b1c20f3d61e40b641f9b686efa6e1bbc59e' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.css.map'; Sha256 = '40ef1c31b5642e292d517d5c1de5d25127b99d7317520c8bf5c8f09cccb13ea7' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.min.css'; Sha256 = 'cac053fc96311fd81c327c314f8f8c078b0fc4ec7f24c83dc22efb140d774fd0' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.min.css.map'; Sha256 = 'ef60bfa83086bbe3b059e540d376dff4a7b44fca08a330ae6f495f26487386f1' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.css'; Sha256 = 'def5099194a928bff31bbc7a18dbc38ecd13c58528f7332de9d01cf21a7d095a' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.css.map'; Sha256 = 'aaf037f4e32512ce778da7b0a9516613c0d042b8a8e3b6d9b654ecf96bba53c8' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.min.css'; Sha256 = '3ba95bda45daac6815c38fd10c3e36c983e1648c1112d85385014a1a60fede3d' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.min.css.map'; Sha256 = '3434998c888c3d12283bbff0efe8c779ff2b7ad3f8ae241af0f323e015511a89' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.css'; Sha256 = 'e336d6af440d161a54c06927e16746597b7cd0a9e1445bf4a97919c959e16a36' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.css.map'; Sha256 = '1591b42b16cda884d48b8418ed0bcf1514bf4dc727b4c7c559f49374737fa70b' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.min.css'; Sha256 = 'cf4380a51f3c504a1c6174d71d4ed47b271a8a26fd5db0d499e97d1b1d206f1e' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.min.css.map'; Sha256 = 'd816d1b04ffe7335f5b9f9833c6a67122782f6ee88de6e4128d0d25f5a1bde58' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.rtl.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.rtl.css'; Sha256 = 'f0d5f0de4178f4591054f31d8e7183aa85d7454d13c33b0b7c219b2bd53c8279' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.rtl.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.rtl.css.map'; Sha256 = 'fc47561cdeade5760fa650bcf2f8b119fac1bdf1228b5f6402cb1bfb4601554f' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.rtl.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.rtl.min.css'; Sha256 = '6b92a5832b19e1f417c38af322f5c31c4ac50cf7874522dc70fee45fa1eebd24' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.rtl.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.rtl.min.css.map'; Sha256 = '18c0e4e6903974590e8a69010167848d867eee58073d2d236259faa7f589558b' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.css'; Sha256 = '35b159c592e605534bcdbffb07459d15f6fafbc8d71c65fa5d8d7dd2ec206de7' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.css.map'; Sha256 = 'e1622a3e87ffbeb5d83bf8de2787c33902946162307bae15dddfbdfea363bb6d' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.min.css'; Sha256 = 'e7e13198c922688de47984112e13626c9e595e736e5916f0ad73806684d73058' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.min.css.map'; Sha256 = 'a75768a78b2579f6612f8cc6da96bafb61d4ace63551402b1895e67adf0c77d7' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.rtl.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.rtl.css'; Sha256 = 'a5e0061fc1aefd92fd5676d419230debd262e4cc706ef39be778035d4d9c3da4' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.rtl.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.rtl.css.map'; Sha256 = 'a6e0e02b0be5140a2b77d47c1bca1ff4fd825582095054a821b8c32c4b0a107d' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.rtl.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.rtl.min.css'; Sha256 = '5a2e59b854872dfc7a7651208ef5b7058f530bfd4da9d0638fe5c589f492a8de' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-utilities.rtl.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-utilities.rtl.min.css.map'; Sha256 = 'd3691ae329a8139c8479c2d49dc2c6dff4a8b9340c9d324e92d5fef7a3edffcf' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.css'; Sha256 = 'c657b1aa3f7f937ba86d5c067dc899723fde0dda2868d81c7f83852ed2d4ca03' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.css.map'; Sha256 = '0d1bd6af481a9e08f9ff94370d19fab6dce97160f39773a91e80300333434794' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.min.css'; Sha256 = 'cfc391e34328c09f0680ae8ff3d63e86224ae7e71c973147ccb84540b2fdd9b8' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.min.css.map'; Sha256 = '801c20dad980d028b6bb9e20305d63342564bbabf39daae42d2e83efa86d34d4' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.rtl.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.rtl.css'; Sha256 = '499da629a0f803e6fe1c8bedb7097e4ef2c59d5cbca3cfd7e348fe10a570caf6' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.rtl.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.rtl.css.map'; Sha256 = '545be68bf652c101668d2e8faf2f41f335de67f180d7af135cbcb290384d3191' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.rtl.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.rtl.min.css'; Sha256 = 'db6c11e904e27687a24595e9eb391143231251bfc507e02fd758ea999245eae5' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap.rtl.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap.rtl.min.css.map'; Sha256 = '8fbbaa2b95684d3e2b50732bf75d50114e526bde25477ba1f44dbc5c1325ceb3' }
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
Write-Host "bootstrap-css restored and verified."
