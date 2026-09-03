<#
  DEPLOY-WHEN-IDLE — arms the PVS bundle to roll out to each producing line the moment it stops.

  A Sony machine keeps answering serial polls (A4E00) even when parked, so "online" stays true at a stop.
  The trustworthy "not producing" signal is the BOARD RATE: when boards stop completing, boardsPerHour -> 0/null.
  We require that across ALL online machines for several consecutive polls (a jam-clear is usually shorter),
  and we refuse to deploy while an operator is mid-scan (activeMode is a parts-change/full-scan/model-change).

  On a confirmed stop: run the deploy guard (backup + health-check + auto-rollback), then drop the line.
#>
param(
  [Parameter(Mandatory)][string]$PublishDir,
  [int]$PollSec = 50,
  [int]$ConfirmPolls = 5,      # consecutive zero-rate reads before we believe it's really stopped (~4 min)
  [int]$MaxMinutes = 600
)
$ErrorActionPreference = 'Continue'
$guard = "C:\Users\Lourdes Gunadasan\Documents\Dantec\PVS\deploy\deploy-pvs.ps1"
$log   = Join-Path $PublishDir 'deploy-when-idle.log'
function Log($m){ $ts=(Get-Date).ToString('MM-dd HH:mm:ss'); ("{0}  {1}" -f $ts,$m) | Tee-Object -FilePath $log -Append | Out-Null }

$targets = @(
  [pscustomobject]@{ line='LINE2PVS'; ip='100.94.102.44';  cred='C:\Users\Lourdes Gunadasan\line2.cred'; zero=0 },
  [pscustomobject]@{ line='LINE3PVS'; ip='100.105.64.115'; cred='C:\Users\Lourdes Gunadasan\line3.cred'; zero=0 },
  [pscustomobject]@{ line='LINE4PVS'; ip='100.82.187.65';  cred='C:\Users\Lourdes Gunadasan\line4.cred'; zero=0 },
  [pscustomobject]@{ line='LINE5PVS'; ip='100.101.8.76';   cred='C:\Users\Lourdes Gunadasan\line5.cred'; zero=0 }
)
$safeModes = @('None','ShiftChange','')
$pending = [System.Collections.Generic.List[object]]::new()
$targets | ForEach-Object { $pending.Add($_) }
$deadline = (Get-Date).AddMinutes($MaxMinutes)
Log ("ARMED: watching {0} lines; need {1} consecutive zero-rate polls ({2}s apart) then deploy from {3}" -f $pending.Count,$ConfirmPolls,$PollSec,$PublishDir)

while($pending.Count -gt 0 -and (Get-Date) -lt $deadline){
  foreach($t in @($pending)){
    try { $s = Invoke-RestMethod -Uri ("http://{0}:5199/api/status" -f $t.ip) -TimeoutSec 8 }
    catch { Log ("{0}: unreachable" -f $t.line); continue }

    $mode   = [string]$s.verify.activeMode
    $online = @($s.machines | Where-Object { $_.online })
    # rate across online machines: producing if any online machine reports a positive rate
    $rate = 0.0
    foreach($m in $online){ if($m.boardsPerHour){ $rate += [double]$m.boardsPerHour } }
    $producing = ($online.Count -gt 0 -and $rate -gt 0)

    if($producing){ if($t.zero -ne 0){ $t.zero = 0 } ; continue }

    # not producing this poll
    $t.zero++
    Log ("{0}: idle read {1}/{2} (online={3} rate={4} mode={5})" -f $t.line,$t.zero,$ConfirmPolls,$online.Count,[math]::Round($rate,1),$mode)
    if($t.zero -lt $ConfirmPolls){ continue }
    if($safeModes -notcontains $mode){ Log ("{0}: stopped but operator mid-scan (mode={1}) - holding" -f $t.line,$mode); $t.zero = $ConfirmPolls-1; continue }

    Log ("{0}: CONFIRMED STOPPED -> deploying" -f $t.line)
    try {
      & $guard -Ip $t.ip -CredFile $t.cred -PublishDir $PublishDir *>> $log
      Log ("{0}: DEPLOY DONE" -f $t.line)
    } catch { Log ("{0}: DEPLOY FAILED: {1}" -f $t.line,$_) }
    [void]$pending.Remove($t)
  }
  if($pending.Count -eq 0){ break }
  Start-Sleep -Seconds $PollSec
}
Log ("WATCHER EXIT: {0} line(s) still pending: {1}" -f $pending.Count, (@($pending | ForEach-Object { $_.line }) -join ', '))
