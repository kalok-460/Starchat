# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $taskRoot 'StarChat.exe' }
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.7.2 or newer is required.' }
foreach ($dependencyName in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
    $dependencySource = Join-Path $taskRoot "deps\$dependencyName"
    $dependencyTarget = Join-Path $taskRoot $dependencyName
    if (-not (Test-Path -LiteralPath $dependencyTarget) -or (Get-FileHash -LiteralPath $dependencySource).Hash -ne (Get-FileHash -LiteralPath $dependencyTarget).Hash) {
        Copy-Item -LiteralPath $dependencySource -Destination $dependencyTarget -Force
    }
}
$sources = @('AssemblyInfo.cs','I18n.cs','Program.cs','Core.cs','Native.cs','OcrClient.cs','Spectrum.cs','Translation.cs','LocalTranslation.cs','Overlay.cs','Visuals.cs','LocalModelDialog.cs','SettingsForm.cs','MainForm.cs') | ForEach-Object { Join-Path $taskRoot $_ }
& $compilerPath /nologo /target:winexe /langversion:5 /platform:x64 /codepage:65001 /optimize+ "/win32icon:$taskRoot\assets\StarChat.ico" "/resource:$taskRoot\assets\ui.en.json,StarChat.ui.en.json" "/out:$OutputPath" /reference:System.Management.dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.Security.dll "/reference:$taskRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$taskRoot\Microsoft.Web.WebView2.WinForms.dll" $sources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Write-Output "Built: $OutputPath"
