# SPDX-License-Identifier: GPL-3.0-only
# Live check of the manual re-read command on an owned fixture window (never the game).
# A one-letter change in a long line is normally kept as the old text (flicker guard);
# a "reread" for that row must replace it under the same track key.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$ocr=Join-Path (Split-Path -Parent $PSScriptRoot) 'ocr'
$fixture=New-Object Windows.Forms.Form
$fixture.Text='StarChat reread fixture';$fixture.FormBorderStyle='None';$fixture.StartPosition='Manual'
$fixture.Location=New-Object Drawing.Point(80,80);$fixture.Size=New-Object Drawing.Size(900,300)
$fixture.BackColor=[Drawing.Color]::FromArgb(28,34,44)
$label=New-Object Windows.Forms.Label
$before='[Global] PilotA: Meet me at the spaceport in ten minutes';$after='[Global] PilotA: Meet me at the spaceport in two minutes'
$label.Text="$before`n[Global] PilotB: My ship is ready, where are you now?";$label.AutoSize=$true
$label.ForeColor=[Drawing.Color]::White;$label.Font=New-Object Drawing.Font('Arial',15);$label.Location=New-Object Drawing.Point(24,60)
$fixture.Controls.Add($label)
$process=New-Object Diagnostics.Process
$process.StartInfo.FileName=Join-Path $ocr 'starchat-ocr.exe'
$process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
$process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
$process.StartInfo.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
$report=[ordered]@{passed=$false;rereadSupported=$false;realGameUsed=$false}
$key=$null;$stage='read';$changedAt=$null;$sentAt=$null
try{
  $fixture.Show();[Windows.Forms.Application]::DoEvents()
  [Console]::InputEncoding=New-Object Text.UTF8Encoding($false)
  [void]$process.Start();$stderr=$process.StandardError.ReadToEndAsync()
  $process.StandardInput.WriteLine((@{type='config';windowHwnd=$fixture.Handle.ToInt64();windowTitle=$fixture.Text;roi=@{x=0;y=0;w=1;h=1};intervalMs=200}|ConvertTo-Json -Compress))
  $read=$process.StandardOutput.ReadLineAsync();$timer=[Diagnostics.Stopwatch]::StartNew()
  while($timer.Elapsed.TotalSeconds -lt 30 -and -not $process.HasExited -and $stage -ne 'done'){
    [Windows.Forms.Application]::DoEvents()
    if($stage -eq 'hold' -and $changedAt.Elapsed.TotalSeconds -ge 3){
      # Three seconds (two full reviews) later the old reading must still be shown.
      $report.keptOldTextWithoutReread=($current -eq $before)
      $process.StandardInput.WriteLine((@{type='reread';keys=@($key)}|ConvertTo-Json -Compress));$sentAt=[Diagnostics.Stopwatch]::StartNew();$stage='reread'
    }
    while($read.IsCompleted){
      $line=$read.Result;if($null -eq $line){break}
      $event=$line|ConvertFrom-Json
      if($event.type -eq 'ready'){$report.rereadSupported=[bool]$event.reread}
      if($event.type -eq 'lines'){
        $row=@($event.lines|Where-Object{$_.text -like '*PilotA*'})
        if($stage -eq 'read' -and $row.Count -eq 1 -and $row[0].text -eq $before -and $event.lines.Count -eq 2){
          $key=$row[0].trackKey;$label.Text=$label.Text.Replace($before,$after);$fixture.Update();$changedAt=[Diagnostics.Stopwatch]::StartNew();$stage='hold'
        }
        if($row.Count -eq 1 -and $row[0].trackKey -eq $key){$current=$row[0].text}
        if($stage -eq 'reread' -and @($event.reread) -contains $key){
          $report.rereadLatencyMs=$sentAt.ElapsedMilliseconds;$report.textAfterReread=$current;$report.sameTrackKey=($row.Count -eq 1 -and $row[0].trackKey -eq $key)
          $report.passed=$report.keptOldTextWithoutReread -and $current -eq $after -and $report.sameTrackKey;$stage='done'
        }
      }
      $read=$process.StandardOutput.ReadLineAsync()
    }
    [Threading.Thread]::Sleep(5)
  }
} finally {
  if(-not $process.HasExited){try{$process.StandardInput.WriteLine('{"type":"stop"}');$process.StandardInput.Close()}catch{};if(-not $process.WaitForExit(5000)){$process.Kill()}}
  $fixture.Close();$fixture.Dispose()
}
$report.stage=$stage
$report|ConvertTo-Json
if(-not $report.passed){exit 1}
