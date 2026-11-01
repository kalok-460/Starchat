# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
param([switch]$LiveWorker)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $root 'OcrRegressionTests.exe'
$sources = @('I18n.cs','OcrRegressionTests.cs','Core.cs','OcrClient.cs','Translation.cs','LocalTranslation.cs') | ForEach-Object { Join-Path $root $_ }
& $compiler /nologo /target:exe /main:StarChat.OcrRegressionTests /langversion:5 /platform:x64 /codepage:65001 /optimize+ "/out:$testExe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.Security.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'OCR regression compilation failed.' }
if ($LiveWorker) { & $testExe --lifecycle } else { & $testExe }
if ($LASTEXITCODE -ne 0) { throw 'OCR regression failed.' }
