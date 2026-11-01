# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
$ErrorActionPreference = 'Stop'
$translationRoot = $PSScriptRoot
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /langversion:5 /platform:x64 /codepage:65001 /optimize+ "/out:$translationRoot\TranslationTests.exe" /reference:System.Drawing.dll /reference:System.dll /reference:System.Core.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $translationRoot 'I18n.cs') (Join-Path $translationRoot 'Translation.cs') (Join-Path $translationRoot 'LocalTranslation.cs') (Join-Path $translationRoot 'TranslationTests.cs')
if ($LASTEXITCODE -ne 0) { throw "Translation test compilation failed: $LASTEXITCODE" }
& (Join-Path $translationRoot 'TranslationTests.exe')
if ($LASTEXITCODE -ne 0) { throw "Translation tests failed: $LASTEXITCODE" }
