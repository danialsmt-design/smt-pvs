# PVS — Line Deployment Runbook (Line 2 / Line 3)

Procedure for bringing PVS up on a new line. Same build as Line 1; **only `line.config.json` and the data differ.**
Do **Line 2 first as a pilot**, then Line 3 (Line 3 has an extra naming risk — see §4.4).

Legend: ☐ = do it,  ⚠ = known trap,  🔴 = go/no-go gate (don't go live until it passes).

---

## 0. Reference facts (Line 1, for comparison)

| Thing | Value |
|---|---|
| App folder | `C:\PvsLineApp\` |
| Local URL | `http://localhost:5199` (pages: `verify.html`, `setup.html`, `/` monitor, `machine-inv.html`, `reel.html`, `top5.html`, `report.html`) |
| Scheduled tasks | `PvsLineApp` (the app), `PvsNextOut` (kiosk Edge `--app=…/top5.html`) |
| Serial | 9600 baud, 7 data bits, Even parity, 1 stop bit |
| Machines | 4 mounters (M1–M4). **M4 = last on the line = board-count source.** |
| DB | `ReelPart-New` on the Parts PC (`DESKTOP-TECHNIC\SQLEXPRESS`), reached at `100.91.120.113,1433` over Tailscale |
| SQL login | `pvs_ro` (read-only + UPDATE on `StockOuts.Quantity` only). Password in `C:\PvsLineApp\db-password.txt`, **not** in the config. |
| DB writes (ProductBOM/DPC) | Only **ACER-PC (dbo)** can write — run via a desktop `.BAT` in ACER-PC's session. `pvs_ro` is denied INSERT. |
| Panel factors (child boards/panel) | L254:4, L261:6, L264:6, L307:4, L309:4, L311:4, L313:4, L347:4 |
| CSV master part lists | `D:\ASHISH\COM-PARTS LIST BY LINE\LINE <n>\...` on the Parts PC |

---

## 1. Hardware & PC prep (per line)  ☐

- ☐ A dedicated PC on the line (Windows 10/11). Name it clearly (e.g. `LINE2PVS`).
- ☐ **.NET 8 runtime** installed (or deploy the self-contained build).
- ☐ Serial link to all **4 mounters** — USB/PCIe serial adapters, one COM per machine.
  - ⚠ Line 1 uses WCH PCIe dual-serial cards; on a cold boot Windows sometimes fails to enumerate the 2nd card (`CM_PROB_PHANTOM`) → a machine goes missing. Confirm all 4 COM ports survive a **reboot** before go-live.
- 🔴 **Network to the Parts PC.** Prefer **wired ethernet**. ⚠ Line 1's cheap USB Wi-Fi dongle ("AIC8800D80") dropped packet bursts → 40-second DB stalls. If Wi-Fi/Tailscale is the only option, run the burst-loss test in §6.1 and don't go live until it passes.
- ☐ A display for the always-on monitor / next-out kiosk (optional but recommended).

---

## 2. COM-port discovery (per PC)  ☐

COM numbers are assigned per-PC and **re-number on reboot** — never assume Line 1's mapping.

- ☐ Device Manager → Ports (COM & LPT): list the COM numbers present.
- ☐ Match each COM to a physical machine. Easiest: with the app running (§5), open `/` monitor → each machine tile shows `online` + a live frame count when its cable is on the right port. Swap the config mapping until M1–M4 line up with the physical mounters.
- 🔴 Confirm **M4 in the config is the physically LAST machine on the line** (board-count + lot progress come off M4). If your line's last machine isn't "4", set the machine numbers so the highest number = line output.
- ☐ Note the final mapping for the config (§3).

---

## 3. `line.config.json` (per line)  ☐

⚠ The old `deploy/line1.config.json` template is **stale** — it has the pre-Tailscale DB server and is **missing `panelBoards` and the feature flags**. Use the full shape below.

- ☐ `lineId` → **2** or **3** (drives every DB filter — get this right).
- ☐ `lineName` → e.g. `LINE2PVS`.
- ☐ `machines` → the COM mapping from §2 (machine → port, model F130/F209, hasTrayFeeder).
- ☐ `central.server` → `100.91.120.113,1433` (Tailscale) **or** the Parts PC's LAN IP+port if wired. `userId` = `pvs_ro`, `password` = "" (kept in `db-password.txt`).
- 🔴 `panelBoards` → **every model this line runs**, with its child-boards-per-panel factor. **Without this, board counts are wrong by the panel factor.**
- ☐ `syncStockOuts` = **false**, `writeProductionCount` = **false** for the pilot (see §8).
- ☐ Put the SQL password in `C:\PvsLineApp\db-password.txt` (not the config).

Full field set (values are examples — replace):
```
lineId, lineName, shiftTimes[], machines[], serial{9600/7/Even/1},
forecast{redMinutes,amberMinutes}, central{server,database,userId,password},
badge{uidPrefix:"9999"}, syncStockOuts, writeProductionCount, panelBoards{model:factor}
```

---

## 4. Data readiness (DB) — the biggest risk  🔴

The scan checklist and feeder map come **entirely from `ProductBOM`**. Gaps = operators can't scan.

### 4.1 Audit ProductBOM coverage for this line  🔴
- ☐ For **each model** this line runs, and **each side** (A/B), confirm **all 4 machines** have feeder rows in `ProductBOM` WHERE `Line = <lineId>`.
- ⚠ Line 1's L261 B-side had only M1 (M2–M4 missing) — expect similar gaps. Cross-check against the CSVs in `D:\ASHISH\COM-PARTS LIST BY LINE\LINE <n>\`.
- ☐ Fill any gaps via **dbo on ACER-PC** (desktop `.BAT`, `sqlcmd -E`) — `pvs_ro` cannot INSERT. Back up ProductBOM first.

### 4.2 Panel factors  ☐
- ☐ Confirm the child-boards-per-panel factor for every model on this line, and that it matches `panelBoards` in the config (§3).

### 4.3 Part-number sanity  ☐
- ☐ Spot-check for typo part numbers (Line 1 had `WA7-8586-00` vs `…-000`, and an unresolved `VS1-87-012`). A wrong expected part → the reel scan interlocks forever.

### 4.4 🔴 Line 3 model-naming check (Line 3 ONLY)
- PVS auto-detects the model from each machine's C3P program name (parses `L###`) and matches it to the `Products` table **by name**.
- Line 3 uses an **"L3" model variant** while other lines share the common un-suffixed name.
- ☐ On the Line-3 PC, read what the machines actually report (monitor tile → `program`, e.g. `L307…_Cell4.PW4`) and confirm that name resolves to the Line-3 `Products` / `ProductBOM` rows.
- 🔴 If they don't match, model auto-detect and feeder population **will fail** — resolve the naming/mapping **before** go-live. (Do not assume Line 1's behaviour here.)

---

## 5. Install & first start  ☐

- ☐ Copy the Release build to `C:\PvsLineApp\` (DLLs + `wwwroot\`).
- ☐ Place `line.config.json` (§3) and `db-password.txt` in `C:\PvsLineApp\`.
- ☐ Create the `PvsLineApp` scheduled task (AtStartup / AtLogon, runs the app; app listens on `localhost:5199`).
- ☐ (Optional) Create the `PvsNextOut` kiosk task (Edge `--app=http://localhost:5199/top5.html`, AtLogon, interactive user).
- ☐ Start `PvsLineApp`; browse `http://localhost:5199/`.

---

## 6. Connectivity & smoke test  🔴

### 6.1 DB link quality  🔴
- ☐ A single-row query returns fast (<1 s).
- ☐ A **multi-row** query (e.g. the daily report / a full ProductBOM read) returns in **~0.1–1 s, not ~40 s.** A 40 s stall = the burst-loss problem (§1) — fix the link (wire it / switch to Tailscale) before continuing.
- ☐ Burst-loss check if on Wi-Fi/Tailscale: sustained pings PC→Parts-PC show ~0% loss (Line 1 showed 100% loss over the bad dongle).

### 6.2 Serial / machines  🔴
- ☐ All 4 machines show `online` on the monitor, with a rising frame count.
- ☐ `program` reads on each machine (proves C3P works).
- ☐ A finished board bumps M4's count (proves R0 board-complete arrives) — watch during production.

### 6.3 Model & feeders  🔴
- ☐ Auto-detect shows the running model + side (or a supervisor sets it on `/setup.html`).
- ☐ The feeder checklist / machine-inventory populates for that model (proves the BOM read + §4 coverage).

---

## 7. Go-live verification  ☐

- ☐ Monitor `/` shows model · side · lot · current shift.
- ☐ **Parts-out interlock**: on a real parts-out, `verify.html` prompts and the reel scan verifies/interlocks correctly (this is the core safety function).
- ☐ **Model-change / shift-change check**: runs, checklist per machine, badge-gated supervisor release works.
- ☐ **Lot count** increments off M4 and shows sensible produced/target (remember: 2-sided lots share a lot number; a B→A side change resets the count).
- ☐ Exhaust "next to run out" populates after a check grounds the feeders.
- ☐ Bilingual EN/Myanmar shows on the scan/exhaust/model-change/popup areas.

---

## 8. Pilot, writers, rollback  ☐

- ☐ **Parallel-run 1–2 shifts**: PVS observes; operators keep their existing process as backup.
- ⚠ Keep `writeProductionCount` and `syncStockOuts` **OFF** during the pilot.
  - Turning on `writeProductionCount` needs: `pvs_ro` INSERT grant on `DailyProductionCount` **and** the existing operator app must **stop** writing that line's DPC rows — otherwise **double-counting**.
  - `syncStockOuts` writes live remaining back to `StockOuts.Quantity` (grant already exists) — only enable once balances are trusted on that line.
- ☐ **Rollback** = stop the `PvsLineApp` scheduled task; nothing else on the line depends on it. Operators revert to the old process. No DB cleanup needed while the writers are OFF.

---

## 9. Roll-up risks to watch across 3 lines

- **DB load / host**: the Parts DB is still the single 4 GB shop-floor Acer (was near-full, unbacked-up). Three lines reading/writing raises the priority of the planned **DB-server migration** and regular backups.
- **Freshness**: the PVS logic was only ever exercised on Line 1, with many fixes landing recently. Treat Line 2 as a genuine pilot; fix what surfaces before Line 3.
- **Per-line hardware**: serial reliability and network quality are the two things that vary most PC-to-PC — they, not the software, are the usual go-live blockers.

---

### One-line summary
Software is line-agnostic and ready; **go/no-go rests on per-line ProductBOM completeness, the Line-3 naming match, and DB link quality** — clear those gates per line and pilot Line 2 before Line 3.
