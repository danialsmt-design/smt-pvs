# PVS Handover — 2026-09-06 (updated 19:35)

In-flight session state for the next operator (e.g. a Fable session). Durable facts live in the
`pvs-*` memory files; this file holds the **moving** state — deployment matrix + open threads.
Git: branch `session-2026-09-03`, HEAD `ece6eb9`, working tree clean.

## ⚠️ Deployment matrix — READ BEFORE ANY DEPLOY
| Line | Tailscale IP | Build | Notes |
|---|---|---|---|
| L1 | 100.69.81.105 | **latest + lot-end calibration (CANARY) + tally-reseed fix `ece6eb9`** | deployed 19:27 while idle (backup stamp `20260906-192701`); lot `HC20799536000` running |
| L2 | 100.94.102.44 | latest **without** calibration | producing |
| L3 | 100.105.64.115 | (unknown, no calibration assumed) | was DOWN; **answering over Tailscale 2026-09-06 19:05**, producing L311MBU — build still unconfirmed |
| L4 | 100.82.187.65 | latest **without** calibration | often idle |
| L5 | 100.101.8.76 | latest **without** calibration | the "thousands-off drift" line (unscanned reel swaps) |

**Rule:** a full redeploy to L2–L5 would spread the L1 canary calibration and break the trial.
For recipient/config-only changes use a **live config-edit + restart**, NOT `deploy-pvs.ps1`.

Pickup-alert recipients **LIVE = Raja (60163327003) + Danish (60122445237)**. Rezman
(60126816059) is committed to the C# default (`93e91a3`) but **NOT pushed live** yet.

## Open threads (status)
1. **Rezman → pickup alert** — committed to default; pending live config-edit (`Alerts.PickupAlertRecipients`) + restart on L1/L2/L3/L4/L5 (L3 reachable again). Do NOT full-redeploy (see rule above).
2. **Lot-end calibration (a4c5b35)** — L1 canary. **FIRED on `HC20799111000` (1500 boards = 375 panels).** Evidence it was sane: M4's pre-reset C1Z showed 1504–1505 attempts on 4-mount feeders = 376 panels vs the 375 it snapped to. **DEFECT FOUND + FIXED (`ece6eb9`):** the recalc snapped `BoardsApplied` to the lot size but the lot-change path never reseeded it, so the next lot's tallies read 459–465 (375 + 90) → 410% deviation / −1476 pcs per feeder in `/api/lot/usagecheck`; an HMI sync or the next lot-end recalc would have handed ~375 panels × mount BACK to every feeder. Fix = `SeedBoardsApplied(0)` on lot/side change + force-end (feeders untouched). Deployed L1 19:27; after restart tallies = 90 = lot panels, usage check 0 gap on all 4, reel remaining unchanged (spot-checked M1 F110–F113). Next validation: L1's lot `HC20799536000` finalize (1200 boards = 300 panels) — tallies should be ~300 with small deltas only. Per-machine recalc deltas for the first lot are only in the L1 log/audit (WinRM read).
3. **Calibration source decision — OPEN.** Currently uses **lot target (PO qty)** → ~0.4% error when a lot over/under-runs. C1M-as-truth is **not viable** (0 C1M reads land; machines refuse A4E00; M4 rejects it — machine HMI config pending). Tightest realistic = **operator-confirmed produced count**, target as fallback. User to decide. **New candidate (2026-09-06): C1Z-derived panels** — per machine, median over its reset-aligned feeders of `VC ÷ mount-per-panel` (M4: 1505/4 = 376 vs target 375). Uses the pickup report only for the BOARD tally, not for consumption/attrition, so it stays clear of the 'don't touch C1Z for counting' rule. Caveats: intermittent landing (M1 landed nothing all lot), counters must be reset-aligned to the lot (M2 F113 read 2128 = un-reset feeder), M2's machine clock is 2003. Not built; user to decide.
4. **Unscanned-reel-change watchdog — DESIGNED, NOT BUILT.** Two triggers: (a) machine parts-out on a feeder while PVS remaining is still high → short/unscanned reel; (b) PVS remaining ~0 but machine keeps picking (C1Z VC rising / boards completing) → fresh unscanned reel. Action: WhatsApp supervisor (line·machine·feeder·part) + mark feeder count untrusted until a rescan re-baselines it. Would have caught L5 `VV5-3000-000` (9,872 phantom, 20 boards). User said "don't code yet."
5. **Daiya % units bug — KNOWN, UNFIXED.** The monitor/daiya compares `machineCounter` (panels) to `targetShift` (boards) → wrong %. Correct = `totalOutput` (boards) vs target (boards), or panels vs target÷perPanel.
6. **L3** — back on Tailscale 2026-09-06 (Wi-Fi); still wants a wired port so the NAS monitor + deploys reach it reliably. Build on box unconfirmed.

## Hard-won domain truths (do not re-litigate)
- **Lot size / PO target = BOARDS** (`DeliveryDocuments.RevisedQty|Quantity`). The machine counts **PANELS** — one R0/board-complete = one panel. `_m4PanelsTotal`, `_boardsApplied`, `SyncToBoardCount` all work in **panels/cycles**.
- **perPanel (boards/panel):** L261=6, L264=6; L254/L307/L309/L311/L313/L347=4 (`Alerts`… no — `PanelBoards` config; same on all lines).
- **Decrement per R0 = feeder-list count × panels** (`SessionCoordinator` ~1641: `perBoard = f.Qty × panels`; `MachineInventory.OnBoardComplete` subtracts it once per R0). This is **correct**. Mount comes from the **feeder list (`f.Qty`)**, NOT ProductBOM.
- **Two drift causes:** (a) **missed R0s** → board count short → under-decrement → phantom remaining → fixed by lot-end calibration to lot size; (b) **unscanned reel swap** → PVS tracks the wrong reel → L5's thousands-off (VS1-9540 family, VV5-3000-000). These are different from pickup **attrition** (VC−TC), the C1Z axis — "not perfected, don't touch it for counting."
- **C1Z landing = 16–42% of attempts** (intermittent; refused A4E00 while actively mounting; the `C1Z000P<program-name>` form is mandatory — bare form = A4E01). Deterministic fresh read only when the machine is **stopped** (Machine Inventory "Stop → Read").
- Count model: **start = StockOut UID qty (accurate); count down by panels × mount; calibrate to lot size at lot end.** Never lose a reel's count — accurate reels are left alone (within tolerance).

## Data access
- **Prefer the HTTP APIs** (`/api/status`, `/api/lot`, `/api/lot/usagecheck`, `/api/machine/counts`, `/api/daiya`, `/api/exhaust`, `/api/calibration`, `/api/feederstats`, `/api/setup`, `/api/serial/trace`) — not gated. **WinRM is gated** by the permission classifier (prompt each time): use only for raw log lines with no API (LotUsageRecalc deltas, per-reel exhaust events, raw frames). The deploy script needs WinRM (one approval).

## Infra / access
- Deploy: `deploy/deploy-pvs.ps1 -Ip <ip> -CredFile C:\Users\Lourdes Gunadasan\line<N>.cred -PublishDir <pub>` (full-set copy, health-check, auto-rollback). Publish: `dotnet publish src\Pvs.LineApp -c Release -r win-x64 --self-contained`. dotnet SDK: `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.
- Lines reached over Tailscale (WinRM) with `line<N>.cred`. Config on box: `C:\PvsLineApp\line.config.json`; logs `C:\PvsLineApp\logs\pvs-<date>.log`; backups `C:\PvsLineApp\_backup\<stamp>\`.
- NAS = Synology `gmssmt1`, Tailscale 100.125.22.119 / LAN **192.168.0.169**. Line monitor at **:8899** (LAN, no Tailscale needed on the floor); DSM :5000. SSH via Posh-SSH + `nas.cred` (user GLOBALSMT, admin); docker at `/usr/local/bin/docker`, project `/volume1/docker/nas-daiya` (`docker compose`). nas-daiya polls each line's LAN IP (L2–L5 have a scoped firewall rule allowing 5199 from 192.168.0.169 only).
- Line LAN IPs (for the NAS monitor): L1 .105, L2 .162, L3 .126(Wi-Fi), L4 .144, L5 .190 — **DHCP, may move**; ask IT for reservations.
- WhatsApp via gms-wabridge Pi 100.90.248.92:8080, `POST /api/send {recipient,message}`.

## First actions for the next session
1. Read the `pvs-*` memory files (esp. `pvs-component-decrement-bible`, `pvs-feeder-consumption-c1z`).
2. Check the deployment matrix above before touching any line.
3. If continuing accuracy work: watch L1 for its lot finalize (validate the calibration), and get the user's decision on the calibration source (§3) and the watchdog (§4).
