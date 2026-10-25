#Requires -Version 5.1
<#
.SYNOPSIS
    Restores all third-party client libraries and the Cairo font.
    Run once after cloning. See README.md for details.
#>
$ErrorActionPreference = "Stop"
& "$PSScriptRoot/restore-bootstrap-css.ps1"
& "$PSScriptRoot/restore-bootstrap-js.ps1"
& "$PSScriptRoot/restore-vendor-js.ps1"
& "$PSScriptRoot/restore-font.ps1"
Write-Host "All client libraries restored and verified."
