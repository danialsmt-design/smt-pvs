<#
  LINE 4 is offline (Tailscale "last seen"; ping times out) — the PC dropped off the network, not a rollout issue.
  This waits for it to come back reachable and idle, then deploys the pending bundle via the guard (backup +
  health-check + auto-rollback). One-shot: exits after a successful deploy, or after MaxMinutes.
#>
param(
  [string]$Ip = '100.82.187.65',
  [string]$Cred = 'C:\Users\Lourdes Gunadasan\line4.cred',
  [string]$PublishDir = 'C:\Users\Lourdes Gunadasan\Documents\Dantec\PVS\deploy\_pending',
  [int]$PollSec = 60,
  [int]$MaxMinutes = 720
)
$ErrorActionPreference = 'Continue'
$guard = 'C:\Users\Lourdes Gunadasan\Documents\Dantec\PVS\deploy\deploy-pvs.ps1'
$log   = Join-Path $PublishDir 'line4-return.log'
function Log($m){ $ts=(Get-Date).ToString('MM-dd HH:mm:ss'); ("{0}  {1}" -f $ts,$m) | Tee-Object -FilePath $log -Append | Out-Null }
$deadline = (Get-Date).AddMinutes($MaxMinutes)
$safe = @('None','ShiftChange','')
Log "WAITING for LINE4PVS ($Ip) to return; will deploy when reachable + idle."
while((Get-Date) -lt $deadline){
  try { $s = Invoke-RestMethod -Uri ("http://{0}:5199/api/status" -f $Ip) -TimeoutSec 8 }
  catch { Start-Sleep -Seconds $PollSec; continue }
  $mode = [string]$s.verify.activeMode
  $rate = 0.0; foreach($m in @($s.machines | Where-Object { $_.online })){ if($m.boardsPerHour){ $rate += [double]$m.boardsPerHour } }
  if($rate -gt 0){ Log ("LINE4 back but producing (rate={0}) - waiting for an idle gap." -f [math]::Round($rate,1)); Start-Sleep -Seconds $PollSec; continue }
  if($safe -notcontains $mode){ Log ("LINE4 back but mid-scan (mode={0}) - holding." -f $mode); Start-Sleep -Seconds $PollSec; continue }
  Log "LINE4 reachable + idle (mode=$mode) -> deploying"
  try { & $guard -Ip $Ip -CredFile $Cred -PublishDir $PublishDir *>> $log; Log "LINE4 DEPLOY DONE"; break }
  catch { Log "LINE4 DEPLOY FAILED: $_"; Start-Sleep -Seconds $PollSec }
}
Log "LINE4 watcher exit."
