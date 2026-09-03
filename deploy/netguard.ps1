<#
  PVS Line Connection Guardian  (netguard)
  ----------------------------------------
  Keeps THIS line PC's network path to the central parts DB alive, with zero
  manual intervention. Runs as a SYSTEM scheduled task (survives reboot/logoff),
  independent of any laptop, cloud, or tokens.

  Behaviour:
    * Every $IntervalSec it TCP-tests the DB ($Db:$Port).
    * While the DB is reachable it does NOTHING to the network (fully passive).
    * Only after $FailsBeforeAct consecutive failures does it run an escalating
      remediation ladder, re-testing after each step and STOPPING the moment the
      DB answers again. After a burst it waits $CooldownSec before acting again
      (anti-flap).
    * -AlertOnly makes it detect + log but never change the network.
    * Never touches the Tailscale tunnel adapter.

  Every cycle it writes netguard-status.json (observable by the PVS app / remotely)
  and appends to netguard.log (rotated at ~1 MB).

  Deploy: copy to C:\PvsLineApp\netguard\netguard.ps1 then run the register block
          at the bottom of this file (commented) once, elevated.
#>
param(
  [string]$Db            = '192.168.0.134',  # central parts DB host
  [int]   $Port          = 1433,             # SQL Server port
  [int]   $IntervalSec   = 20,               # check cadence
  [int]   $FailsBeforeAct= 3,                # ~60 s of failure before remediating
  [int]   $CooldownSec   = 300,              # quiet period after a remediation burst
  [switch]$AlertOnly,                        # detect + log only, never change the network
  [string]$Wifi          = 'Wi-Fi',
  [string]$Eth           = 'Ethernet',
  [string]$Dir           = 'C:\PvsLineApp\netguard'
)

$ErrorActionPreference = 'SilentlyContinue'
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }
$Log    = Join-Path $Dir 'netguard.log'
$Status = Join-Path $Dir 'netguard-status.json'

function Write-Log([string]$msg) {
  $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  try {
    if ((Test-Path $Log) -and (Get-Item $Log).Length -gt 1MB) {
      Move-Item $Log "$Log.1" -Force
    }
    Add-Content -Path $Log -Value $line
  } catch {}
}

function Write-Status($reachable, $fails, $lastAction) {
  try {
    [pscustomobject]@{
      host        = $env:COMPUTERNAME
      time        = (Get-Date -Format 'o')
      db          = "$Db`:$Port"
      dbReachable = [bool]$reachable
      consecutiveFails = [int]$fails
      lastAction  = "$lastAction"
      alertOnly   = [bool]$AlertOnly
    } | ConvertTo-Json -Compress | Set-Content -Path $Status -Encoding UTF8
  } catch {}
}

# Fast TCP reachability test with a hard timeout (Test-Connection/Test-NetConnection can hang).
function Test-Db {
  $c = New-Object System.Net.Sockets.TcpClient
  try {
    $iar = $c.BeginConnect($Db, $Port, $null, $null)
    if ($iar.AsyncWaitHandle.WaitOne(2500, $false) -and $c.Connected) { $c.EndConnect($iar); return $true }
    return $false
  } catch { return $false } finally { $c.Close() }
}

# --- remediation steps -------------------------------------------------------
# A broken static config (DHCP disabled + no default gateway) is the failure we
# saw on Line 5. Convert such an adapter back to automatic and renew.
function Repair-BrokenStatic([string]$alias) {
  $ifc = Get-NetIPInterface -InterfaceAlias $alias -AddressFamily IPv4
  if (-not $ifc) { return $false }
  $gw  = (Get-NetIPConfiguration -InterfaceAlias $alias).IPv4DefaultGateway.NextHop
  if ($ifc.Dhcp -eq 'Disabled' -and -not $gw) {
    Write-Log "REMEDIATE: $alias is a broken static config (DHCP off, no gateway) -> switching to automatic."
    Set-NetIPInterface -InterfaceAlias $alias -Dhcp Enabled
    Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 |
      Where-Object { $_.PrefixOrigin -eq 'Manual' } | Remove-NetIPAddress -Confirm:$false
    Get-NetRoute -InterfaceAlias $alias -DestinationPrefix '0.0.0.0/0' |
      Where-Object { $_.NextHop -ne '0.0.0.0' } | Remove-NetRoute -Confirm:$false
    Set-DnsClientServerAddress -InterfaceAlias $alias -ResetServerAddresses
    ipconfig /renew "$alias" | Out-Null
    Start-Sleep 4
    return $true
  }
  return $false
}

function Renew-Dhcp([string]$alias) {
  $a = Get-NetAdapter -Name $alias
  if (-not $a -or $a.Status -ne 'Up') { return $false }
  Write-Log "REMEDIATE: DHCP renew on $alias."
  ipconfig /renew "$alias" | Out-Null
  Start-Sleep 4
  return $true
}

# Re-associate Wi-Fi to a known-good SSID profile (last resort). Only fires if a
# profile name is supplied via netguard.ssid so we never connect to something wrong.
function Reconnect-Wifi {
  $ssidFile = Join-Path $Dir 'netguard.ssid'
  if (-not (Test-Path $ssidFile)) { return $false }
  $ssid = (Get-Content $ssidFile -Raw).Trim()
  if (-not $ssid) { return $false }
  Write-Log "REMEDIATE: re-associating Wi-Fi to profile '$ssid'."
  netsh wlan connect name="$ssid" | Out-Null
  Start-Sleep 6
  return $true
}

function Invoke-Ladder {
  # Try each step; stop the instant the DB answers again.
  foreach ($step in @(
      { Repair-BrokenStatic $Eth },
      { Repair-BrokenStatic $Wifi },
      { Renew-Dhcp $Eth },
      { Renew-Dhcp $Wifi },
      { Reconnect-Wifi })) {
    $did = & $step
    if ($did -and (Test-Db)) { return $true }
  }
  return (Test-Db)
}

# --- main loop ---------------------------------------------------------------
Write-Log "netguard start (db=$Db`:$Port interval=${IntervalSec}s threshold=$FailsBeforeAct alertOnly=$AlertOnly)"
$fails = 0
$lastAction = 'none'
$cooldownUntil = (Get-Date).AddSeconds(-1)

while ($true) {
  try {
    if (Test-Db) {
      if ($fails -gt 0) { Write-Log "RECOVERED: DB reachable again after $fails miss(es)." }
      $fails = 0
      Write-Status $true 0 $lastAction
    }
    else {
      $fails++
      Write-Log "DB unreachable ($fails/$FailsBeforeAct)."
      Write-Status $false $fails $lastAction
      if ($fails -ge $FailsBeforeAct -and (Get-Date) -ge $cooldownUntil) {
        if ($AlertOnly) {
          Write-Log "ALERT-ONLY: would remediate now, but -AlertOnly is set."
          $lastAction = "alert @ $(Get-Date -Format 'HH:mm:ss')"
        }
        else {
          $ok = Invoke-Ladder
          $lastAction = "remediate -> $(if($ok){'RECOVERED'}else{'still down'}) @ $(Get-Date -Format 'HH:mm:ss')"
          Write-Log $lastAction
          if ($ok) { $fails = 0 }
          $cooldownUntil = (Get-Date).AddSeconds($CooldownSec)
        }
        Write-Status (Test-Db) $fails $lastAction
      }
    }
  } catch {
    Write-Log "ERROR in loop: $($_.Exception.Message)"
  }
  Start-Sleep -Seconds $IntervalSec
}

<#  ----- ONE-TIME REGISTER (run elevated on the line PC) -----
$dir = 'C:\PvsLineApp\netguard'
$act = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dir\netguard.ps1`""
$trg = New-ScheduledTaskTrigger -AtStartup
$prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -MultipleInstances IgnoreNew -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'PvsNetGuard' -Action $act -Trigger $trg -Principal $prn -Settings $set -Force
Start-ScheduledTask -TaskName 'PvsNetGuard'
#>
