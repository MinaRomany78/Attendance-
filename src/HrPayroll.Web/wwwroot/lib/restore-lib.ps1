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
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.css'; Sha256 = '26d92d822b9001df805deac29cf32b1c20f3d61e40b641f9b686efa6e1bbc59e' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.css.map'; Sha256 = '40ef1c31b5642e292d517d5c1de5d25127b99d7317520c8bf5c8f09cccb13ea7' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.min.css'; Sha256 = 'cac053fc96311fd81c327c314f8f8c078b0fc4ec7f24c83dc22efb140d774fd0' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.min.css.map'; Sha256 = 'ef60bfa83086bbe3b059e540d376dff4a7b44fca08a330ae6f495f26487386f1' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.css'; Sha256 = 'def5099194a928bff31bbc7a18dbc38ecd13c58528f7332de9d01cf21a7d095a' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.css.map'; Sha256 = 'aaf037f4e32512ce778da7b0a9516613c0d042b8a8e3b6d9b654ecf96bba53c8' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.min.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.min.css'; Sha256 = '3ba95bda45daac6815c38fd10c3e36c983e1648c1112d85385014a1a60fede3d' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-grid.rtl.min.css.map'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-grid.rtl.min.css.map'; Sha256 = '3434998c888c3d12283bbff0efe8c779ff2b7ad3f8ae241af0f323e015511a89' }
    @{ Rel = 'lib/bootstrap/dist/css/bootstrap-reboot.css'; Url = 'https://cdn.jsdelivr.net/npm/bootstrap@5.1.0/dist/css/bootstrap-reboot.css'; Sha256 = 'e336d6af440d161a54c06927e16746597b7cd0a9e1445bf4a97919c959e16a36' }
    @{ Rel = 'PART2'; Url = 'https://example.com'; Sha256 = '0000000000000000000000000000000000000000000000000000000000000000' }
)
