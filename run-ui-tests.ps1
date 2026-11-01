# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
param([switch]$RenderSettings)
$ErrorActionPreference = 'Stop'
$uiTestRoot = $PSScriptRoot
$uiCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$uiSources = @('I18n.cs','Core.cs','Native.cs','OcrClient.cs','Spectrum.cs','Translation.cs','LocalTranslation.cs','Overlay.cs','Visuals.cs','LocalModelDialog.cs','SettingsForm.cs','MainForm.cs','UiTests.cs') | ForEach-Object { Join-Path $uiTestRoot $_ }
& $uiCompiler /nologo /target:winexe /main:StarChat.UiTests /langversion:5 /platform:x64 /codepage:65001 /optimize+ "/out:$uiTestRoot\UiTests.exe" /reference:System.Management.dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.Security.dll "/reference:$uiTestRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$uiTestRoot\Microsoft.Web.WebView2.WinForms.dll" $uiSources
if ($LASTEXITCODE -ne 0) { throw "UI test compilation failed: $LASTEXITCODE" }
$uiStart=@{FilePath=(Join-Path $uiTestRoot 'UiTests.exe');WorkingDirectory=$uiTestRoot;WindowStyle='Hidden';PassThru=$true}
if($RenderSettings){$uiStart.ArgumentList='--render-settings'}
$uiProcess = Start-Process @uiStart
if (-not $uiProcess.WaitForExit(30000)) { $uiProcess.Kill(); throw 'UI tests exceeded 30 seconds; stopped this test process.' }
$uiResult = Join-Path $uiTestRoot $(if($RenderSettings){'test-output\ui-render-tests.json'}else{'test-output\ui-tests.json'})
Get-Content -LiteralPath $uiResult -Raw
if ($uiProcess.ExitCode -ne 0) { throw "UI tests failed: $($uiProcess.ExitCode)" }
