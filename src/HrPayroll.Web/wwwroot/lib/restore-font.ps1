#Requires -Version 5.1
<#
.SYNOPSIS
    Restores the Cairo font (wwwroot/fonts + PDF-embedded copy) from a pinned
google/fonts commit and verifies every file by SHA256.
    Run once after cloning. See README.md for details.
#>
$ErrorActionPreference = "Stop"
$libRoot = $PSScriptRoot
$webRoot = (Resolve-Path (Join-Path $libRoot ".."))).Path
