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
