# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
param([string]$Version='0.3.13', [Parameter(Mandatory=$true)][string]$DestinationRoot)
$ErrorActionPreference='Stop'
$packagePython=Join-Path $PSScriptRoot 'ocr\runtime\python.exe'
if (-not (Test-Path -LiteralPath $packagePython)) { throw 'Run ocr/setup.ps1 first.' }
& $packagePython (Join-Path $PSScriptRoot 'package_release.py') --version $Version --destination $DestinationRoot
if ($LASTEXITCODE -ne 0) { throw 'Release packaging failed.' }
