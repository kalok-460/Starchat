# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
# 소유한 테스트 창만 캡처한다. 가림 창이 있어도 원문을 읽고 300ms 주기로 스크롤을 따라가는지 검사한다.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$fixture=New-Object Windows.Forms.Form
$fixture.Text='StarChat scroll fixture'
$fixture.FormBorderStyle='None'
$fixture.StartPosition='Manual'
$fixture.Location=New-Object Drawing.Point(80,80)
$fixture.Size=New-Object Drawing.Size(1050,320)
$fixture.BackColor=[Drawing.Color]::White
$label=New-Object Windows.Forms.Label
$label.Text="[Global] PilotA: Meet me at the spaceport`n[Global] PilotB: My ship is ready`n[Global] PilotC: Okay"
$fixtureLines=@('[Global] PilotA: Meet me at the spaceport','[Global] PilotB: My ship is ready','[Global] PilotC: Okay')
$additions=@('[Global] PilotD: Bring supplies to the landing pad','[Global] PilotE: All clear','[Global] PilotF: Meet the crew at the station tomorrow','[Global] PilotG: Fuel is ready','[Global] PilotH: Departing soon')
$latencies=New-Object 'Collections.Generic.List[long]'
$label.Location=New-Object Drawing.Point(20,35)
$label.AutoSize=$true
$label.Font=New-Object Drawing.Font('Arial',24)
$fixture.Controls.Add($label)
$cover=New-Object Windows.Forms.Form
$cover.FormBorderStyle='None';$cover.StartPosition='Manual';$cover.Location=$fixture.Location;$cover.Size=$fixture.Size
$cover.BackColor=[Drawing.Color]::Red;$cover.TopMost=$true
$coverLabel=New-Object Windows.Forms.Label
$coverLabel.Text='OVERLAY MUST NOT APPEAR';$coverLabel.AutoSize=$true
$coverLabel.Font=New-Object Drawing.Font('Arial',28);$cover.Controls.Add($coverLabel)
$worker=New-Object Diagnostics.Process
$worker.StartInfo.FileName=Join-Path $PSScriptRoot 'runtime\python.exe'
$worker.StartInfo.Arguments='"'+(Join-Path $PSScriptRoot 'worker.py')+'"'
$worker.StartInfo.UseShellExecute=$false;$worker.StartInfo.CreateNoWindow=$true
$worker.StartInfo.RedirectStandardInput=$true;$worker.StartInfo.RedirectStandardOutput=$true;$worker.StartInfo.RedirectStandardError=$true
$worker.StartInfo.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
$worker.StartInfo.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
$messages=New-Object 'Collections.Generic.List[string]'
$report=@{passed=$false;intervalMs=300;realGameUsed=$false;device='';scrollLatencyMs=$null;positionPacketBeforeNewRecognition=$false;overlayExcluded=$true}
$baseline=$null;$scrollTimer=$null
try {
  $fixture.Show();$cover.Show();[Windows.Forms.Application]::DoEvents()
  [void]$worker.Start();$stderr=$worker.StandardError.ReadToEndAsync()
  $worker.StandardInput.WriteLine((@{type='config';windowHwnd=$fixture.Handle.ToInt64();windowTitle=$fixture.Text;roi=@{x=0;y=0;w=1;h=1};intervalMs=300}|ConvertTo-Json -Compress))
  $read=$worker.StandardOutput.ReadLineAsync();$timer=[Diagnostics.Stopwatch]::StartNew()
  while ($timer.Elapsed.TotalSeconds -lt 45 -and -not $worker.HasExited) {
    [Windows.Forms.Application]::DoEvents()
    if ($read.IsCompleted) {
      $line=$read.Result;if ($null -eq $line) { break };$messages.Add($line);$event=$line|ConvertFrom-Json
      if ($event.type -eq 'ready') {$report.device=$event.device}
      if ($event.type -eq 'lines') {
        if (($event.lines.text -join ' ') -match 'OVERLAY') {$report.overlayExcluded=$false;break}
        $tracked=@($event.lines|Where-Object {$baseline -and $_.trackKey -eq $baseline.trackKey})
        if (-not $baseline -and $event.lines.Count -eq 3 -and $event.recognized) {
          $baseline=@($event.lines|Sort-Object y)[1]
          $fixtureLines=@($fixtureLines[1],$fixtureLines[2],$additions[$latencies.Count])
          $label.Text=$fixtureLines -join "`n"
          $fixture.Update();[Windows.Forms.Application]::DoEvents();$scrollTimer=[Diagnostics.Stopwatch]::StartNew()
        } elseif ($baseline -and $tracked.Count -eq 1 -and $tracked[0].trackKey -eq $baseline.trackKey -and $tracked[0].y -lt ($baseline.y-20)) {
          $report.scrollLatencyMs=$scrollTimer.ElapsedMilliseconds
          $latencies.Add($scrollTimer.ElapsedMilliseconds)
          $report.positionPacketBeforeNewRecognition=(-not $event.recognized)
          $report.detectMs=$event.detectMs;$report.frameAgeMs=$event.frameAgeMs
          $report.passed=$report.positionPacketBeforeNewRecognition -and $report.scrollLatencyMs -lt 1000
          if(-not $report.passed -or $latencies.Count -eq $additions.Count){break}
          $baseline=$null;$scrollTimer=$null
        }
      }
      $read=$worker.StandardOutput.ReadLineAsync()
    }
    Start-Sleep -Milliseconds 10
  }
} finally {
  if (-not $worker.HasExited) {$worker.StandardInput.WriteLine('{"type":"stop"}');$worker.StandardInput.Close();if(-not $worker.WaitForExit(5000)){$worker.Kill()}}
  $fixture.Close();$cover.Close();$fixture.Dispose();$cover.Dispose()
  $out=Join-Path (Split-Path $PSScriptRoot) 'test-output'
  $messages|Set-Content -LiteralPath (Join-Path $out 'scroll-check-038.jsonl') -Encoding utf8
  if($stderr){$stderr.Result|Set-Content -LiteralPath (Join-Path $out 'scroll-check-038.stderr.log') -Encoding utf8}
  $report.workerExitCode=$worker.ExitCode
  $report.scrollLatenciesMs=@($latencies)
  $report.passed=$report.passed -and $latencies.Count -eq $additions.Count
  $report|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out 'scroll-check-038.json') -Encoding utf8
}
$report|ConvertTo-Json
if(-not $report.passed){exit 1}
