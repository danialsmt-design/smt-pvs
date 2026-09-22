# PVS → ReelPart-New: every database write, for the DB migration

Written 2026-09-22 from the code on branch `session-2026-09-03` (HEAD `83ebde2`, on all 5 lines). Source of
truth for the SQL is `src/Pvs.Data/SqlReelPartRepository.cs`; nothing else in PVS opens a SQL connection.

## 1. How the line PCs reach the database

| Item | Value today | Where it lives |
|---|---|---|
| Instance | **LIVE STATE 2026-09-22 (read from each box): all 5 lines already point at the NAS** — `192.168.0.169,1433` (NAS LAN) with `fallbackServer` `100.125.22.119,1433` (NAS Tailscale). L1 L2 L4 L5 connected on the LAN address, L3 (Wi-Fi only) on the Tailscale fallback. The old Parts Control PC instance `DESKTOP-TECHNIC\SQLEXPRESS` (`192.168.0.134` / Tailscale `100.91.120.113`) is no longer the PVS target. | — |
| Database | `ReelPart-New` | `central.database` |
| Server | `192.168.0.169,1433` on every line (the repo templates in `deploy/line*.config.json` still say `100.91.120.113` / `192.168.0.134` — STALE, do not redeploy them over a box config) | `C:\PvsLineApp\line.config.json` → `central.server` |
| Fail-over server | `central.fallbackServer` = `100.125.22.119,1433` on every line — the same NAS instance over Tailscale; the repo tries the preferred one, falls back on a connect failure and sticks to whichever answered. | same |
| Login | SQL auth, `pvs_ro` (name is misleading — see §2) | `central.userId` |
| Password | NOT in the config: `C:\PvsLineApp\db-password.txt` (read at start-up when `central.password` is empty) | line PC |
| Connection string | `Server=<s>;Database=ReelPart-New;User ID=pvs_ro;Password=…;TrustServerCertificate=True;Connect Timeout=8;` | `LineConfig.CentralConfig.ConnectionStringFor` |
| Health | `/api/health` → `signals.db` (state, lastOkAt, lastError). Both routes down → one WhatsApp to Danial, production held on the line, second WhatsApp on recovery. | `DbHealthService` |

**To repoint a line:** edit `central.server` (+ `fallbackServer`) and `db-password.txt` on the box, restart the
`PvsLineApp` scheduled task (a restart = a deploy: do it when the line is stopped). No code change.

## 2. The login and the grants the new server must reproduce

`pvs_ro` was created read-only (`deploy/CreateSqlLogin.bat`: `CREATE LOGIN … CHECK_POLICY = OFF`,
`db_datareader`) and then given exactly these write grants over time:

| Table | Grant | Script / when |
|---|---|---|
| `dbo.DailyProductionCount` | INSERT | `deploy/grant-pvs-write.bat` (July 2026) |
| `dbo.StockOuts` | UPDATE (Quantity column is the only one PVS touches) | granted by hand 2026-07-27 |
| `dbo.ConsumedReels` | INSERT, UPDATE, SELECT | `deploy/ConsumedReels.sql` (creates the table + grants) |
| `dbo.PartAttrition` | INSERT, UPDATE, SELECT | `deploy/ConsumedReels.sql` |
| everything else | SELECT via `db_datareader` | — |

`ConsumedReels` and `PartAttrition` are PVS-owned tables (ownership map in `Documents/Dantec/MCS/COORDINATION.md`).
Their DDL is in `deploy/ConsumedReels.sql`; it must exist on the new server or the reel-retire path stays
inert (by design: the retire records first and never zeroes a StockOut without a record behind it).

## 3. The writes, one by one

### W1 — `DailyProductionCount` INSERT (the line's production count)

```sql
INSERT INTO DailyProductionCount
  (Date, StartTime, Time, Model, Side, Quantity, Line, OperatorName, LotNo, Shift, EmployeeId, SenderIp, SessionID, ExcessQuantity)
VALUES (@date, @start, @end, @model, @side, @qty, @line, @op, @lot, @shift, '', @ip, @sid, 0)
```

| | |
|---|---|
| Trigger | 5-minute timer (`FlushProductionCountAsync`), also on a supervisor force-end and on `POST /api/production/flush` |
| Per-line switch | `writeProductionCount` in the box config — **true on all 5 lines** (PVS is the only DPC writer; the operator apps / external loggers were stopped). A redeploy that resets it silently stops the count (happened once: L1/L3/L4 recorded nothing for days). |
| One row = | one lot + model + side bucket per 5-minute window, per shift/day slot |
| Quantity | **child boards** (panels × boards-per-panel from `panelBoards` in the config), never panels |
| Date / times | `Date` = `yyyy-MM-dd`, `StartTime` and `Time` = `HH:mm:ss`, dated to the **bucket's own day and shift**, not the flush time |
| Shift | `"Morning"` (07:35–19:35) or `"Night"` |
| Tags | `OperatorName = "PVS (auto)"`, `SenderIp = "PVS"`, `SessionID = new GUID`, `EmployeeId = ''`, `ExcessQuantity = 0` |
| Line | `Line` = line id as text (`"1"`…`"5"`) |
| Cap | before writing the current lot it reads `SUM(Quantity)` already recorded for that lot/side/line and never writes past what PVS produced (guards against a second writer) |
| DB down | rows are **held on the line** in `dpc-state.json` and written back when the DB answers, still dated to their own day |
| Volume | up to 12 rows per hour per line while producing |
| Code | `SessionCoordinator.FlushProductionCountAsync`, `SqlReelPartRepository.InsertProductionCountAsync` |

### W2 — `StockOuts` UPDATE Quantity (the reel balance write-back)

```sql
UPDATE StockOuts SET Quantity = @q
WHERE ID = (SELECT TOP 1 ID FROM StockOuts WHERE LTRIM(RTRIM(PartUID)) = LTRIM(RTRIM(@uid)) ORDER BY ID DESC);
```

| | |
|---|---|
| Trigger | 5-minute timer (`RecordAndSyncAsync`) **and** immediately after any count correction (HMI key-in, recount, lot-end recalc, new reel loaded on a parts change) |
| Since | **always on, all lines, 2026-09-14** (the old per-line `syncStockOuts` flag was removed) |
| Rule | the balance of the reel on a feeder IS its StockOut; PVS counts down from the StockOut quantity read when the reel is scanned on and writes the balance back by UID |
| Row picked | the **most recent** `StockOuts` row for that `PartUID` (highest ID). PVS never INSERTs a StockOut row; new issue rows come from the store (MCS Ai entry pages) |
| Skips | reels whose balance has not changed since the last write; a UID with no row → 0 rows, shown as a warning on the StockOut chip / `signals.stockOut` |
| Also | supervisor manual edit `POST /api/reel/qty` (badge-gated) uses the same statement |
| Volume | one row per tracked reel per 5 min when it moved; ~30–40 reels per line |
| Code | `SessionCoordinator.SyncRemainingToStockOutsAsync`, `SyncStockOutsSoon`, `SqlReelPartRepository.UpdateReelQtyAsync` |

### W3 / W4 / W5 — reel retire on a confirmed exhaust (three statements in a fixed order)

Fires when a parts change **completes**: machine parts-out, operator scanned the OLD reel (= the reel PVS
tracks) and a DIFFERENT new reel. (Danial's day-one rule: a lone E03 is a false call.)

1. **W3 `ConsumedReels` INSERT** (idempotent: only if no open record for the UID)
   ```sql
   IF NOT EXISTS (SELECT 1 FROM ConsumedReels WHERE LTRIM(RTRIM(Uid)) = LTRIM(RTRIM(@uid)) AND Restored = 0)
   INSERT INTO ConsumedReels (Uid, PartNumber, Rank, RemainingAtRetire, Line, LotNo, RetiredAt, Restored)
   VALUES (@uid, @part, @rank, @rem, @line, @lot, GETDATE(), 0);
   ```
   `Rank` read from `PartRanks` (blank if none), `RemainingAtRetire` = PVS balance at that moment, `Line` = line name.
2. **W4 `StockOuts` UPDATE Quantity = 0** for the old UID (same statement as W2).
3. **W5 `PartAttrition` MERGE** (+ remaining pieces, + 1 reel):
   ```sql
   MERGE dbo.PartAttrition AS t USING (SELECT @p AS PartNumber) AS s ON LTRIM(RTRIM(t.PartNumber)) = LTRIM(RTRIM(s.PartNumber))
   WHEN MATCHED THEN UPDATE SET AttritionPcs = AttritionPcs + @pcs, Reels = Reels + @reels, LastAt = GETDATE()
   WHEN NOT MATCHED THEN INSERT (PartNumber, AttritionPcs, Reels, LastAt) VALUES (@p, @pcs, @reels, GETDATE());
   ```

If step 1 throws (table missing, no grant), steps 2 and 3 do not run. Runs on every line since 2026-09-14.
Volume: one retire per confirmed exhaust, ~5–20 per line per day. Code: `RetireOutgoingReelAsync`.

### W6 — restore a retired reel (supervisor, `POST /api/reel/restore`)

Reverse of W3–W5, in this order: `StockOuts` UPDATE Quantity = `RemainingAtRetire`;
`UPDATE ConsumedReels SET Restored = 1, RestoredAt = GETDATE() WHERE Uid = @uid AND Restored = 0`;
`PartAttrition` MERGE with negative deltas. Code: `RestoreConsumedReelAsync`.

### Not database writes (stay on the line PC, `C:\PvsLineApp\`)

Audit / verification records (`records\*.jsonl`), reel balances (`remaining.json`), held production
(`dpc-state.json`), lot progress, machine tallies, attrition report (`attrition.json`), exhaust calibration,
feeder-reel map, manual lots, pending slips. None of these go to SQL; nothing in the migration touches them.

## 4. What PVS reads (the new server must serve these tables with the same columns)

| Table | Used for |
|---|---|
| `Products`, `ProductBOM` | model list, feeder map (machine / supply position / part / per-board count), BOM usage for the daily report and shortage |
| `DeliveryDocuments` | current lot per line, lot target, lot model, lot options, upcoming lots, next delivery lot |
| `StockOuts` | reel lookup by UID, issued quantity for a newly scanned reel, reels issued per lot / at line, spare (not-on-feeder) stock |
| `StockIns` | store stock for the shortage view (RemainingQty is zeroed on issue) |
| `DailyProductionCount` | boards already recorded for a lot (the W1 cap), daily report runs |
| `Users` | badge UID → name / level (cached on the line; `PreloadBadgesAsync`) |
| `PartRanks` | A/B/C rank on retire |
| `PartPrices` | shortage / value view |
| `ConsumedReels`, `PartAttrition` | restore lookup, reports |

Format quirks the new schema must keep: `DailyProductionCount.Date` is text `yyyy-MM-dd` with `Shift`
`Morning`/`Night`; `ProductionLog` (read by others, not PVS) uses `dd-MM-yyyy`; every UID and part compare is
`LTRIM(RTRIM(…))`; a reel UID is the whole number (never a truncated prefix); `StockOuts` may hold several rows
per UID and PVS always takes the highest `ID`.

## 5. Other writers of the same tables (so the cutover moves them together)

| Writer | Table / operation |
|---|---|
| MCS Ai entry pages (NAS) | `StockIns` INSERT, `StockOuts` INSERT (new issues), master data |
| Ashish's manual scan / clear HTML page | `StockOuts.Quantity → 0` (empty-reel returns, list clears) |
| PC3 Parts Control desktop app (ACER-PC, Integrated Security) | the original front door; connection to be repointed or retired |
| ProductionAPI / anything else on `.134` | connection string still TBD (see memory `mcs-cutover-readiness`) |
| nas-daiya, MCS dispatcher, Floor Ai | read only |

## 6. Cutover checklist for PVS

> **Status 2026-09-22:** steps 1–5 are already DONE for the five line apps (all boxes on `192.168.0.169`, fallback `100.125.22.119`; today's 88 L1 production rows were written to and read back from the NAS). Still to confirm: step 2 (the hourly `.134` → NAS mirror must be OFF), step 4 (old instance read-only), step 7 (MCS pages, Ashish's clear page, PC3 app). L5 showed recently closed sockets to `192.168.0.134:1433` with no owning process — most likely the netguard reachability probe, not a data writer; verify and retire that probe.

1. On the new server: restore `ReelPart-New`; run `deploy/ConsumedReels.sql`; create login `pvs_ro`
   (SQL auth) as `db_datareader` + the four grants in §2; check identity seeds on `DailyProductionCount`,
   `StockOuts`, `ConsumedReels` continue past the old maximum.
2. Stop the hourly prod → NAS mirror before the new DB takes writes, or it will overwrite them.
3. Switch **all five lines in one pass** while they are stopped: `central.server`, `central.fallbackServer`,
   `db-password.txt`, restart `PvsLineApp`. A line left on the old server keeps writing there.
4. Make the old instance read-only (or unreachable) the moment the last line is switched, so no line can fail
   over back to it.
5. Verify per line within 10 minutes: `/api/health` → `db.state = ok`; StockOut chip green with a fresh
   write time; a `DailyProductionCount` row with `OperatorName = 'PVS (auto)'` appears within 5 minutes of
   the first boards.
6. Held production (§W1) written during the switch lands on the new DB dated to its own day — no manual
   re-entry. Check it once with:
   ```sql
   SELECT Line, Date, Shift, LotNo, SUM(Quantity) FROM DailyProductionCount
   WHERE SenderIp = 'PVS' AND Date >= '<cutover day>' GROUP BY Line, Date, Shift, LotNo ORDER BY 1,2,3;
   ```
7. Repoint the MCS Ai pages, Ashish's clear page and the PC3 app in the same window (§5).

## 7. Writes PVS makes that are NOT on the Parts DB (unchanged by the migration)

| Target | What | Code |
|---|---|---|
| MCS dispatcher on the NAS (`robot.dispatcherUrl`, `http://192.168.0.169:8090`) | `POST /api/requests` (parts request for the store, 45 min before a forecast run-out) and `POST /api/requests/{id}/close`. The MCS app owns the `PartsRequests` / `PartsRequestReels` tables in its own catalog; PVS never writes them directly. | `PartsRequestService` |
| MCS dispatcher | `POST /api/robot/call`, `POST /api/robot/done` (the 🤖 button only proxies; the dispatcher is the single writer to the robot) | `RobotCaller` |
| gms-wabridge Pi (`alerts.whatsAppBridgeUrl`, `http://100.90.248.92:8080`) | `POST /api/send` — DB-down alert, pickup-rate alert, attrition escalation | `WhatsAppSender` |
| e-mail relay | daily report mail (if configured) | `EmailSender` |
| MCS schedule (read) | `GET <dispatcherUrl>/api/schedule` every 5 min for the Plan card | `PlanFeedService` |

Completeness check (2026-09-22): `grep SqlClient` over `src/` hits only `Pvs.Data/SqlReelPartRepository*.cs`; the
`BadgeCache` and `Shortage` partials contain no INSERT/UPDATE/MERGE/DELETE. `NullReelPartRepository` is the
no-DB stub. Nothing in `deploy/`, `WATCHDOG/` or `nas-daiya/` writes to the Parts DB.

## 8. Where to look in the code

`src/Pvs.Data/SqlReelPartRepository.cs` (all SQL) · `src/Pvs.Core/Config/LineConfig.cs` (`CentralConfig`)
· `src/Pvs.LineApp/Program.cs` lines ~25–35 (password file) · `src/Pvs.LineApp/Verification/SessionCoordinator.cs`
(`FlushProductionCountAsync`, `SyncRemainingToStockOutsAsync`, `RetireOutgoingReelAsync`, `RestoreConsumedReelAsync`)
· `src/Pvs.LineApp/Runtime/DbHealthService.cs` · `deploy/ConsumedReels.sql`, `deploy/CreateSqlLogin.bat`,
`deploy/grant-pvs-write.bat`, `deploy/DEPLOYMENT-RUNBOOK.md`.
