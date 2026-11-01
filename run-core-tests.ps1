# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.7.2 or newer is required.' }
$dependencyRoot = Join-Path $taskRoot 'deps'
foreach ($dependencyName in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll')) {
    $dependencyPath = Join-Path $taskRoot $dependencyName
    if (-not (Test-Path -LiteralPath $dependencyPath)) {
        Copy-Item -LiteralPath (Join-Path $dependencyRoot $dependencyName) -Destination $dependencyPath
    }
}
$sources = @('I18n.cs','CoreTests.cs','Core.cs','Native.cs','Spectrum.cs','Translation.cs','LocalTranslation.cs') | ForEach-Object { Join-Path $taskRoot $_ }
$testExe = Join-Path $taskRoot 'CoreTests.exe'
& $compilerPath /nologo /target:exe /main:StarChat.CoreTests /langversion:5 /platform:x64 /codepage:65001 /optimize+ "/out:$testExe" /reference:System.Management.dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.Security.dll "/reference:$taskRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$taskRoot\Microsoft.Web.WebView2.WinForms.dll" $sources
if ($LASTEXITCODE -ne 0) { throw "Core test compilation failed: $LASTEXITCODE" }
& $testExe
if ($LASTEXITCODE -ne 0) { throw "Core tests failed: $LASTEXITCODE" }
