# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
# 인공 창을 실제 WGC로 캡처하여 OCR 파이프 연결을 검사한다. 게임이나 사용자 창은 읽지 않는다.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$fixture = New-Object Windows.Forms.Form
$fixture.Text = 'StarChat OCR fixture'
$fixture.FormBorderStyle = 'None'
$fixture.StartPosition = 'Manual'
$fixture.Location = New-Object Drawing.Point(80,80)
$fixture.Size = New-Object Drawing.Size(950,180)
$fixture.BackColor = [Drawing.Color]::White
$fixture.TopMost = $true
$label = New-Object Windows.Forms.Label
$label.Text = '[Global] Pilot: Hello Star Citizen'
$label.Location = New-Object Drawing.Point(20,45)
$label.AutoSize = $true
$label.Font = New-Object Drawing.Font('Arial',28)
$label.ForeColor = [Drawing.Color]::Black
$fixture.Controls.Add($label)
$cover = New-Object Windows.Forms.Form
$cover.Text = 'StarChat overlay fixture'
$cover.FormBorderStyle = 'None'
$cover.StartPosition = 'Manual'
$cover.Location = New-Object Drawing.Point(80,80)
$cover.Size = New-Object Drawing.Size(950,180)
$cover.BackColor = [Drawing.Color]::Red
$cover.TopMost = $true
$coverLabel = New-Object Windows.Forms.Label
$coverLabel.Text = 'OVERLAY MUST NOT APPEAR'
$coverLabel.AutoSize = $true
$coverLabel.Font = New-Object Drawing.Font('Arial',28)
$coverLabel.Location = New-Object Drawing.Point(20,45)
$cover.Controls.Add($coverLabel)
$worker = New-Object Diagnostics.Process
$worker.StartInfo.FileName = Join-Path $PSScriptRoot 'runtime\python.exe'
$worker.StartInfo.Arguments = '"' + (Join-Path $PSScriptRoot 'worker.py') + '"'
$worker.StartInfo.UseShellExecute = $false
$worker.StartInfo.CreateNoWindow = $true
$worker.StartInfo.RedirectStandardInput = $true
$worker.StartInfo.RedirectStandardOutput = $true
$worker.StartInfo.RedirectStandardError = $true
$worker.StartInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
$worker.StartInfo.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
$messages = New-Object 'Collections.Generic.List[string]'
$passed = $false
try {
    $fixture.Show()
    $cover.Show()
    [Windows.Forms.Application]::DoEvents()
    [void]$worker.Start()
    $stderrTask = $worker.StandardError.ReadToEndAsync()
    $config = @{type='config';windowTitle=$fixture.Text;windowHwnd=$fixture.Handle.ToInt64();roi=@{x=0;y=0;w=1;h=1};intervalMs=500;fullIntervalMs=2000} | ConvertTo-Json -Compress
    $worker.StandardInput.WriteLine($config)
    $read = $worker.StandardOutput.ReadLineAsync()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 45 -and !$worker.HasExited) {
        [Windows.Forms.Application]::DoEvents()
        if ($read.IsCompleted) {
            $line = $read.Result
            if ($null -eq $line) { break }
            $messages.Add($line)
            $event = $line | ConvertFrom-Json
            if ($event.type -eq 'lines') {
                $allText = ($event.lines | ForEach-Object { $_.text }) -join ' '
                if ($allText -match 'Hello Star Citizen' -and $allText -notmatch 'OVERLAY') {
                    $passed = $true
                    break
                }
            }
            $read = $worker.StandardOutput.ReadLineAsync()
        }
        Start-Sleep -Milliseconds 30
    }
} finally {
    if (!$worker.HasExited) {
        $worker.StandardInput.WriteLine('{"type":"stop"}')
        $worker.StandardInput.Close()
        if (!$worker.WaitForExit(5000)) { $worker.Kill() }
    }
    $fixture.Close()
    $cover.Close()
    $fixture.Dispose()
    $cover.Dispose()
    $messages | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'capture-check-output.jsonl') -Encoding utf8
    if ($stderrTask) { $stderrTask.Result | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'capture-check-diagnostics.txt') -Encoding utf8 }
}
@{passed=$passed;seconds=[Math]::Round($timer.Elapsed.TotalSeconds,2);workerExitCode=$worker.ExitCode} | ConvertTo-Json -Compress
if (!$passed) { exit 1 }
