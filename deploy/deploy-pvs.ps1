<#
  PVS DEPLOY GUARD — the ONLY sanctioned way to update a live line.

  Enforces the rules that a hand-copy keeps breaking:
   * ships the COMPLETE assembly set together (Pvs.Core + Pvs.Data + Pvs.LineApp) + wwwroot —
     never a partial DLL swap (the TypeLoad / 0xE0434352 DI-startup failure).
   * backs up what's on the box BEFORE touching it.
   * health-checks the API after restart.
   * AUTO-ROLLS-BACK to the backup and restarts if the new build doesn't come up.

  Usage:
    .\deploy-pvs.ps1 -Ip 100.94.102.44 -CredFile "C:\Users\Lourdes Gunadasan\line2.cred" `
        -PublishDir "<self-contained publish out>"

  It refuses to proceed unless the publish contains the full set, so an incomplete deploy can't start.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Ip,
  [Parameter(Mandatory)][string]$CredFile,
  [Parameter(Mandatory)][string]$PublishDir,
  [string]$AppDir = 'C:\PvsLineApp',
  [int]$Port = 5199,
  [int]$HealthTimeoutSec = 40
)
$ErrorActionPreference = 'Stop'

# --- RULE 1: the publish must be complete, or we refuse to deploy at all -----------------------------
$required = @('Pvs.Core.dll','Pvs.Data.dll','Pvs.LineApp.dll','Pvs.LineApp.exe')
foreach ($f in $required) {
  if (-not (Test-Path (Join-Path $PublishDir $f))) { throw "REFUSING DEPLOY: publish is missing $f (incomplete build)." }
}
if (-not (Test-Path (Join-Path $PublishDir 'wwwroot'))) { throw "REFUSING DEPLOY: publish is missing wwwroot." }
$cred = Import-CliXml $CredFile
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Write-Host "Deploying to $Ip from $PublishDir (backup stamp $stamp)"

$s = New-PSSession -ComputerName $Ip -Credential $cred
try {
  # --- stop the app + back up the current assemblies + wwwroot on the box --------------------------
  Invoke-Command -Session $s -ArgumentList $AppDir,$stamp {
    param($AppDir,$stamp)
    if (-not (Test-Path "$AppDir\Pvs.LineApp.exe")) { throw "No existing install at $AppDir on this box." }
    Stop-ScheduledTask -TaskName 'PvsLineApp' -ErrorAction SilentlyContinue
    Start-Sleep 2
    Get-Process Pvs.LineApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep 2
    $bak = "$AppDir\_backup\$stamp"
    New-Item -ItemType Directory -Path $bak -Force | Out-Null
    Get-ChildItem "$AppDir\Pvs.*.dll" | ForEach-Object { Copy-Item $_.FullName "$bak\$($_.Name)" -Force }
    if (Test-Path "$AppDir\wwwroot") { Copy-Item "$AppDir\wwwroot" "$bak\wwwroot" -Recurse -Force }
    "backed up $(@(Get-ChildItem "$bak\Pvs.*.dll").Count) assemblies to $bak"
  }

  # --- RULE 2: copy the WHOLE Pvs.* set + wwwroot together over SMB --------------------------------
  New-PSDrive -Name PVSDEP -PSProvider FileSystem -Root "\\$Ip\C`$" -Credential $cred | Out-Null
  $dst = "PVSDEP:" + ($AppDir.Substring(2))
  Get-ChildItem "$PublishDir\Pvs.*.dll" | ForEach-Object { Copy-Item $_.FullName "$dst\$($_.Name)" -Force }
  Copy-Item "$PublishDir\Pvs.LineApp.exe" "$dst\Pvs.LineApp.exe" -Force
  Copy-Item "$PublishDir\wwwroot\*" "$dst\wwwroot\" -Recurse -Force
  Remove-PSDrive PVSDEP

  # --- start + health-check ------------------------------------------------------------------------
  Invoke-Command -Session $s { Start-ScheduledTask -TaskName 'PvsLineApp' }
  $healthy = $false
  $deadline = (Get-Date).AddSeconds($HealthTimeoutSec)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep 4
    try { $r = Invoke-RestMethod "http://$Ip`:$Port/api/status" -TimeoutSec 6; if ($r.line) { $healthy = $true; break } } catch {}
  }

  if ($healthy) {
    Write-Host "DEPLOY OK: $($r.line) is up, $((@($r.machines | Where-Object {$_.online})).Count)/$(@($r.machines).Count) machines online. Backup kept at $AppDir\_backup\$stamp"
  }
  else {
    Write-Warning "NEW BUILD DID NOT COME UP — ROLLING BACK to $stamp"
    Invoke-Command -Session $s -ArgumentList $AppDir,$stamp {
      param($AppDir,$stamp)
      Stop-ScheduledTask -TaskName 'PvsLineApp' -ErrorAction SilentlyContinue; Start-Sleep 2
      Get-Process Pvs.LineApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 2
      $bak = "$AppDir\_backup\$stamp"
      Get-ChildItem "$bak\Pvs.*.dll" | ForEach-Object { Copy-Item $_.FullName "$AppDir\$($_.Name)" -Force }
      if (Test-Path "$bak\wwwroot") { Copy-Item "$bak\wwwroot\*" "$AppDir\wwwroot\" -Recurse -Force }
      Start-ScheduledTask -TaskName 'PvsLineApp'
    }
    Start-Sleep 8
    try { $r2 = Invoke-RestMethod "http://$Ip`:$Port/api/status" -TimeoutSec 8; Write-Host "ROLLED BACK: $($r2.line) restored and up." }
    catch { Write-Error "ROLLBACK FAILED to verify — manual check needed on $Ip." }
    throw "Deploy aborted and rolled back (new build failed health check)."
  }
}
finally { Remove-PSSession $s }
