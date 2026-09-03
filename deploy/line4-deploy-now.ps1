<#
  LINE 4 — DEPLOY NOW (idle guard intentionally waived; Danial 2026-08-12 "deploy now" while the line is producing).
  The PC is off the network, so this waits ONLY for reachability, then fires the deploy guard
  (backup + health-check + auto-rollback). One-shot: exits after a successful deploy, or after MaxMinutes.
  For the normal, safe rollout use line4-deploy-on-return.ps1 instead — that one waits for a real idle gap.
#>
param(
  [string]$Ip = '100.82.187.65',
  [string]$Cred = 'C:\Users\Lourdes Gunadasan\line4.cred',
  [string]$PublishDir = 'C:\Users\Lourdes Gunadasan\Documents\Dantec\PVS\deploy\_pending',
  [int]$PollSec = 30,
  [int]$MaxMinutes = 720
)
$ErrorActionPreference = 'Continue'
$guard = 'C:\Users\Lourdes Gunadasan\Documents\Dantec\PVS\deploy\deploy-pvs.ps1'
$log   = Join-Path $PublishDir 'line4-deploy-now.log'
function Log($m){ $ts=(Get-Date).ToString('MM-dd HH:mm:ss'); ("{0}  {1}" -f $ts,$m) | Tee-Object -FilePath $log -Append | Out-Null }
$deadline = (Get-Date).AddMinutes($MaxMinutes)
Log "WAITING for LINE4PVS ($Ip) to come back on the network; deploys IMMEDIATELY on contact (idle wait waived)."
while((Get-Date) -lt $deadline){
  try { $s = Invoke-RestMethod -Uri ("http://{0}:5199/api/status" -f $Ip) -TimeoutSec 8 }
  catch { Start-Sleep -Seconds $PollSec; continue }
  $mode = [string]$s.verify.activeMode
  $rate = 0.0; foreach($m in @($s.machines | Where-Object { $_.online })){ if($m.boardsPerHour){ $rate += [double]$m.boardsPerHour } }
  Log ("LINE4 reachable (mode={0} rate={1}) -> deploying now" -f $mode,[math]::Round($rate,1))
  try { & $guard -Ip $Ip -CredFile $Cred -PublishDir $PublishDir *>> $log; Log "LINE4 DEPLOY DONE"; break }
  catch { Log "LINE4 DEPLOY FAILED: $_"; Start-Sleep -Seconds $PollSec }
}
Log "LINE4 deploy-now watcher exit."
