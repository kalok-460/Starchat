# SPDX-License-Identifier: GPL-3.0-only
# Live WGC check on an owned fixture window (never the game): the chat block sways
# like the HUD and the script measures how far the reported position lags behind it.
#   .\live_check.ps1 -Worker rust     # starchat-ocr.exe
#   .\live_check.ps1 -Worker python   # ocr\worker.py, for comparison
param([ValidateSet('rust','python')][string]$Worker='rust',[int]$Seconds=12,[int]$IntervalMs=200,[double]$Amplitude=9,[double]$Hertz=0.8,[string]$Dump='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$ocr=Join-Path (Split-Path -Parent $PSScriptRoot) 'ocr'
$fixture=New-Object Windows.Forms.Form
$fixture.Text='StarChat sway fixture';$fixture.FormBorderStyle='None';$fixture.StartPosition='Manual'
$fixture.Location=New-Object Drawing.Point(80,80);$fixture.Size=New-Object Drawing.Size(900,420)
$fixture.BackColor=[Drawing.Color]::FromArgb(28,34,44)
$label=New-Object Windows.Forms.Label
$lines=@('[Global] PilotA: Meet me at the spaceport in ten minutes','[Global] PilotB: My ship is ready, where are you now?','[Global] PilotC: Okay',
         '[Party] Bobinhed: anyone selling quantanium at Area18','[Global] PilotD: Bring supplies to the landing pad please','[Global] Trader_Joe: WTS Polaris 20m aUEC')
$label.Text=$lines -join "`n";$label.AutoSize=$true;$label.ForeColor=[Drawing.Color]::FromArgb(225,235,245)
$label.Font=New-Object Drawing.Font('Arial',15);$baseTop=60;$label.Location=New-Object Drawing.Point(24,$baseTop)
$fixture.Controls.Add($label)
$process=New-Object Diagnostics.Process
if($Worker -eq 'rust'){$process.StartInfo.FileName=Join-Path $ocr 'starchat-ocr.exe'}
else{$process.StartInfo.FileName=Join-Path $ocr 'runtime\python.exe';$process.StartInfo.Arguments='-u "'+(Join-Path $ocr 'worker.py')+'"';$process.StartInfo.WorkingDirectory=$ocr}
$process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
$process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
$process.StartInfo.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$process.StartInfo.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
$report=[ordered]@{worker=$Worker;device='';intervalMs=$IntervalMs;realGameUsed=$false}
$trackKey=$null;$lineY=$null;$sequence=-1;$dy=0.0;$samples=New-Object 'Collections.Generic.List[object]'
$linesMessages=0;$motionMessages=0;$readyMs=$null;$firstTextMs=$null;$texts=@()
try{
  $fixture.Show();[Windows.Forms.Application]::DoEvents()
  $start=[Diagnostics.Stopwatch]::StartNew()
  [Console]::InputEncoding=New-Object Text.UTF8Encoding($false) # No BOM on the worker's stdin.
  [void]$process.Start();$stderr=$process.StandardError.ReadToEndAsync()
  $process.StandardInput.WriteLine((@{type='config';windowHwnd=$fixture.Handle.ToInt64();windowTitle=$fixture.Text;roi=@{x=0;y=0;w=1;h=1};intervalMs=$IntervalMs}|ConvertTo-Json -Compress))
  $read=$process.StandardOutput.ReadLineAsync();$sway=$null
  while(-not $process.HasExited){
    if($sway){
      $offset=[int][Math]::Round($Amplitude*[Math]::Sin(2*[Math]::PI*$Hertz*$sway.Elapsed.TotalSeconds))
      if($label.Top -ne $baseTop+$offset){$label.Top=$baseTop+$offset;$fixture.Update()}
      if($sway.Elapsed.TotalSeconds -ge $Seconds){break}
    } elseif($start.Elapsed.TotalSeconds -gt 40){break}
    [Windows.Forms.Application]::DoEvents()
    while($read.IsCompleted){
      $line=$read.Result;if($null -eq $line){break}
      if($Dump){Add-Content -LiteralPath $Dump -Value $line -Encoding utf8}
      $event=$line|ConvertFrom-Json
      if($event.type -eq 'ready'){$report.device=$event.device;$readyMs=$start.ElapsedMilliseconds}
      elseif($event.type -eq 'lines'){
        $linesMessages++
        if(-not $firstTextMs -and $event.lines.Count -ge $lines.Count){$firstTextMs=$start.ElapsedMilliseconds;$texts=@($event.lines.text);$trackKey=@($event.lines|Sort-Object y)[2].trackKey;$sway=[Diagnostics.Stopwatch]::StartNew();$linesMessages=0;$motionMessages=0}
        $row=@($event.lines|Where-Object{$_.trackKey -eq $trackKey})
        if($row.Count -eq 1){$lineY=[double]$row[0].y;$sequence=$event.sequence;$dy=if($null -ne $event.dy){[double]$event.dy}else{0.0}}
      }
      elseif($event.type -eq 'motion'){$motionMessages++;if($event.base -eq $sequence){$dy=[double]$event.dy}}
      $read=$process.StandardOutput.ReadLineAsync()
    }
    # What an overlay would draw right now versus where the text really is.
    if($sway -and $null -ne $lineY){$samples.Add([pscustomobject]@{shown=$lineY+$dy;actual=$label.Top-$baseTop})}
    [Threading.Thread]::Sleep(4)
  }
} finally {
  if(-not $process.HasExited){try{$process.StandardInput.WriteLine('{"type":"stop"}');$process.StandardInput.Close()}catch{};if(-not $process.WaitForExit(5000)){$process.Kill()}}
  $fixture.Close();$fixture.Dispose()
}
$report.readyMs=$readyMs;$report.firstTextMs=$firstTextMs;$report.linesRead=$texts.Count
$report.textsCorrect=@($texts|Where-Object{$lines -contains $_}).Count
if($samples.Count -gt 20){
  $rest=@($samples|ForEach-Object{$_.shown-$_.actual}|Sort-Object)[[int]($samples.Count/2)]
  $errors=@($samples|ForEach-Object{[Math]::Abs($_.shown-$_.actual-$rest)})
  $sorted=@($errors|Sort-Object)
  $report.linesPerSecond=[Math]::Round($linesMessages/$Seconds,1);$report.motionPerSecond=[Math]::Round($motionMessages/$Seconds,1)
  $report.meanErrorPx=[Math]::Round(($errors|Measure-Object -Average).Average,2)
  $report.p95ErrorPx=[Math]::Round($sorted[[int]($sorted.Count*0.95)],2);$report.maxErrorPx=[Math]::Round($sorted[-1],2)
  $report.swayAmplitudePx=$Amplitude;$report.swayHz=$Hertz
}
$report.stderrTail=if($stderr){(($stderr.Result -split "`n")|Select-Object -Last 3) -join ' | '}else{''}
$report|ConvertTo-Json
