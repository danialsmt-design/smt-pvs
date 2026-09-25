# PVS Function Rule Trees

The decision logic for every PVS function. Where the [Operating Rules](pvs-rules.md) state the policy and
the System Brain maps symptom → cause, this maps **function → decision**: for each action, who may trigger it,
every branch, the invariants it must protect, and — the part that catches bugs — **what it must never do**.

**We write the tree before the code.** The code is then checked against its tree. A guardian agent watches this
file against the implementation and flags any drift.

**Template (every function uses this shape):**

```
FUNCTION NAME  (when it is used)
├─ TRIGGER      who/what starts it
├─ GATE         authorisation / preconditions → reject paths
├─ CONFIRM      operator/supervisor confirmation (if destructive)
├─ ACTION       the steps it performs (atomic)
├─ INVARIANTS   what must stay true across the action
├─ MUST NOT     the forbidden effects (the guardrails)
├─ OUTPUT       what it returns / records
└─ REVERSE      how it is undone / resumed
```

Status legend: **✅ locked** (built + verified) · **�draft** (tree agreed, code in progress) · **▫ to write**.

---

## Supervisor functions

### Unload all feeders  ✅ locked (built + guardian-verified + deployed all 5 lines 2026-08-21)
*Month-end: lots finished, all parts return to store to count StockIn. Or a completely new model is loaded.*
*UI: 🗑 Unload line button on verify.html → badge-gated, two-tap confirm modal → return manifest + CSV + restore.*

```
UNLOAD ALL FEEDERS
│
├─ TRIGGER  supervisor taps "Unload feeders" on the operator screen
│
├─ GATE  supervisor badge L2+ (CanReleaseInterlock) ?
│     ├─ no  → reject: "Scan a SUPERVISOR badge (L2+)"  → nothing changes
│     └─ yes ↓
│
├─ CONFIRM  "Takes every reel off the feeders. StockOut counts kept. Continue?"
│     ├─ cancel → nothing changes
│     └─ confirm ↓
│
├─ ACTION  (one atomic step)
│     1. snapshot every loaded feeder  (machine · feeder · part · UID · remaining)  → manifest + undo backup
│     2. clear the feeder→reel mapping  (all machines)
│     3. stop live tracking (inventory cleared) → no decrement, no StockOut sync
│
├─ INVARIANTS  (must stay true)
│     • StockOut qty per UID …… UNCHANGED   (never written — "remainder in the UID maintained")
│     • Lot + produced count …… UNCHANGED   (a half-done lot resumes from its produced qty)
│     • Attrition ………………… NOT written  (a return is not a consume)
│
├─ MUST NOT
│     ✗ zero or adjust StockOut     ✗ end / reset the lot or the board count     ✗ write ConsumedReels or attrition
│
├─ OUTPUT  return manifest (UID · part · qty) → the store counts StockIn against it; audit record "UnloadAll"
│
└─ REVERSE
      ├─ mistake?      → "Restore last unload" (supervisor): re-map the exact reels, re-baseline from StockOut
      └─ real resume?  → NOT restore — load NEW reels from store (same or new UID), scan in normally;
                          the lot's count continues from its produced qty toward target
```

**Resume note:** unloading is a *material* action only. It never touches the lot or the count, so a half-done
lot survives it. The normal "load back" is the ordinary scan flow with fresh reels — not the restore, which
exists only to undo a mistaken unload.

### Select / change lot  ▫ to write
### Force-end lot  ▫ to write
### Set / force production count  ▫ to write
### Select model (pin)  ▫ to write
### Skip machine  ▫ to write
### Load manual feeder list (pen-drive CSV)  ▫ to write
### Restore consumed reel  ▫ to write

## Operator functions

### Parts / feeder change  ▫ to write
### Board-input scan  ▫ to write
### Standby (spare) scan  ▫ to write
### Line-stop reason  ▫ to write

### Call delivery robot  🔧draft (built 2026-09-07, not deployed; MCS dispatcher on the NAS is the robot's single writer)
*Operator needs reels/packs brought from the store. UI: 🤖 Call robot button on verify.html; chip shows queued / on the way / AT LINE; tapping the AT-LINE chip releases it.*

```
CALL DELIVERY ROBOT
│
├─ TRIGGER  operator taps "Call robot" (any badge level — it is a request, not a stock action)
│
├─ GATE  robot.dispatcherUrl set in this line's config ?
│     ├─ no  → button hidden; API answers "robot not configured on this line"
│     └─ yes ↓  MCS dispatcher reachable ?
│           ├─ no  → "MCS dispatcher not reachable" → nothing queued
│           └─ yes ↓
│
├─ CONFIRM  none (a duplicate call for the same line returns the EXISTING job — never a second trip)
│
├─ ACTION
│     1. POST {line, by, reason} → MCS /api/robot/call   (PVS never addresses the robot itself)
│     2. dispatcher queues one Deliver job to THIS line's taught waypoint; FIFO across lines; one trip at a time
│     3. operator screen polls /api/robot/status every 3 s → chip: queued (N ahead) · on the way · AT LINE
│     4. operator unloads the plate → taps the chip → POST /api/robot/done → robot takes the next job or returns to the store standby
│     5. no tap within the dwell time (launcher-adjustable, default 180 s) → robot leaves anyway
│
├─ INVARIANTS
│     • StockOut / StockIn / feeders / lot …… UNCHANGED   (moving reels is not issuing or loading them)
│     • The reel LOAD event is still the operator's scan on the feeder — the robot's arrival proves nothing
│     • One writer to the robot: the MCS dispatcher. Lines and the launcher only enqueue.
│
├─ MUST NOT
│     ✗ write any DB table     ✗ call the robot's HTTP API from a line PC     ✗ auto-call on a parts-out (future, and only as a proposal)
│     ✗ block the operator screen when the NAS is down (5 s timeout, then "not reachable")
│
├─ OUTPUT  dispatcher job (#id, status, message) + event log on the launcher's Robot page; nothing recorded in PVS
│
└─ REVERSE  launcher "Stop current" / "Clear queue"; a failed trip is logged and the queue moves on
```

### Parts request to the store (auto, from the forecast)  🔧draft (built 2026-09-07, not deployed)
*The line asks the store for material BEFORE a reel runs out; the store keeper picks the reels (= the stock-out), sends the robot, or dismisses. Store terminals 1+2 show the request; acting on it is the store keeper's decision.*

```
PARTS REQUEST (auto)
│
├─ TRIGGER  PartsRequestService, every 60 s, reads THIS line's own /api/exhaust (no new computation)
│
├─ GATE  robot.dispatcherUrl set AND robot.autoRequest on ?  ── no → service idle
│     feeder row needsRequest (won't last the lot, NO spare staged, lot under-issued) AND minutes-to-run-out ≤ robot.requestMinutes (45) AND run-out time known ?
│     no request already OPEN for that part on this line ?   part not in the DISMISS cool-down (90 min / lot change) ?
│
├─ CONFIRM  none on the line. The STORE decides: acknowledge → scan reels (each scan = MCS StockOut INSERT) → Send robot | Dismiss
│
├─ ACTION
│     1. POST MCS /api/requests {line, lot, part, machine, feeder, minutesLeft, piecesNeeded = lot need − issued, remaining}  (one per part per line)
│     2. keep it in parts-requests.json; poll MCS for its status → 📦 chip on verify.html (waiting for store / store picking / on the robot)
│     3. close it on MCS when TWO consecutive forecasts agree the part is covered (reel loaded / spare staged / lasts the lot) or the lot changes
│     4. store DISMISSED it → do not ask for that part again for the cool-down
│
├─ INVARIANTS
│     • PVS writes NO stock: the StockOut row is written by MCS when the store scans the reel; the load is still the operator's feeder scan
│     • The forecast that drives it is the SAME one the operator sees (exhaust card) — no second model
│     • One open request per (line, part); several feeders of one part share it
│
├─ MUST NOT
│     ✗ write StockOuts/StockIns/ConsumedReels     ✗ move the robot itself (only the store's Send does)     ✗ re-file a dismissed request inside the cool-down
│     ✗ close on one noisy forecast read     ✗ request when a spare of the part is already at the line
│
├─ OUTPUT  request rows in MCS (PartsRequests / PartsRequestReels, MCS catalog); PVS log lines "Parts request #n filed/closed"; /api/robot/requests
│
└─ REVERSE  store Dismiss; PVS auto-close when covered; lot change closes all; robot.autoRequest=false turns it off (button stays)
```

## Automatic functions (no one triggers)

### Board count  ▫ to write
### Board tally correction (HMI key-in · machine mount count · lot-end recalc)  🔧draft (rebuilt 2026-09-11 after external audit)
*The feeders decrement per observed board-complete; a correction sets the tally to a KNOWN count and shares the
difference among the reels. Every reel is ANCHORED (qty at anchor · observed boards at anchor · correction boards
since) and its remaining is DERIVED from the anchor — never a running balance.*

```
BOARD TALLY CORRECTION
│
├─ TRIGGER  operator keys the HMI panel count · C1Z tally sync (apply) · lot-end recalc to lot size / produced
│
├─ GATE  a lot is tracked on this machine ?  and  |delta| ≥ 2 panels (sync) / beyond tolerance (recalc) ?
│     ├─ no  → nothing changes
│     └─ yes ↓
│
├─ ACTION  delta = known count − (observed + corrections so far)
│     for each TRACKED reel:  share = (observed − reel.LoadBoards) / observed      (0 if loaded/recounted just now, 1 if here all run)
│                            reel.CorrectionBoards += round(delta × share)
│                            reel.remaining = StartQty − mount × (observed − LoadBoards + CorrectionBoards)   (clamped ≥0 for display)
│     machine.correction += delta
│
├─ INVARIANTS
│     • a reel loaded AFTER the missed boards is never charged for them (share 0 at load)
│     • a physical recount / reel load is a FRESH ANCHOR — nothing from before it is ever re-charged
│     • the run re-baseline (restart / lot change) re-anchors every reel at the run's start with its current remaining
│     • a correction and its exact reversal cancel to the piece (share uses OBSERVED boards only)
│     • a deduction past zero keeps the true consumption — reversing it restores the exact value
│
├─ MUST NOT
│     ✗ apply the whole delta to every reel     ✗ mutate remaining as a running balance     ✗ touch StockOut here
│
├─ OUTPUT  audit MachineCountSync / MachineTallySync / LotUsageRecalc; remaining persisted; StockOut sync follows
│
└─ REVERSE  key the previous count again (same observed boards) → every reel returns exactly
```

### Attrition report at parts-out (+ >2 % escalation to the production manager)  🔧draft (built 2026-09-14)
*Danial 2026-09-14: "the component shortage during parts exhaust compared with PVS creates an attrition report;
should be less than 2 %; anything more highlight to Mr Raja." C1Z is NOT used for counting (intermittent); its job
is the high-throw feeder alarm (pickup-rate WhatsApp).*

```
ATTRITION AT PARTS-OUT
│
├─ TRIGGER  a parts-out (E03) change COMPLETED with a DIFFERENT reel scanned onto the feeder (confirmed exhaust)
│          (an E03 alone is NOT a sample: at a lot start the machine sends E03 for an unthreaded feeder on a full reel
│           — 2026-09-15 every line read 95-100 % at 0-10 boards; one false WhatsApp went to Raja Rao from L2)
│
├─ GATE  operator scanned the OLD reel = the reel PVS tracks ?  and  a DIFFERENT new reel ?  and  not sampled before ?
│        (Danial: "E03 is correct only if the operator scanned old reel + new reel ID, otherwise false call")
│     ├─ no  → nothing recorded
│     └─ yes ↓
│
├─ ACTION  shortage = PVS remaining on that reel at this instant (≥ 0)
│          percent  = shortage ÷ reel start qty × 100
│          over     = percent > limit (2 %)       escalate = over AND boards this reel ≥ 20
│          row → attrition.json (atomic, 60 days) · audit Attrition / AttritionOver · red row on verify + daily report
│
├─ LOT END (RecordLotUsageAtFinalize)  rows of the lot with escalate AND not yet sent
│          → ONE WhatsApp to the production manager (Raja Rao) listing each reel; rows marked sent + persisted
│
├─ INVARIANTS
│     • READ-ONLY on counts: never changes a reel balance, StockOut, or the lot count
│     • one sample per reel (machine|feeder|uid); one message per lot, never re-sent after a restart or 2nd finalise
│     • a short run over the limit is SHOWN but not escalated (noisy percent)
│     • exactly 2 % is within limit (strict >)
│
├─ MUST NOT
│     ✗ use C1Z pickups for the shortage     ✗ alert mid-lot (the lot's list goes as one message)     ✗ block the serial thread
│
├─ OUTPUT  /api/attrition · report/daily.attrition · verify "Attrition this lot" card · WhatsApp (fire-and-forget, logged if the bridge fails)
│
└─ REVERSE  delete attrition.json + calibration.json (report/shadow only; nothing else was written)
```

### Balance reconcile (load qty · line clock · learned rate)  🔧draft (built 2026-09-24)
*Danial: "recheck the balance — when loaded, when exhausted, how many boards completed; if it does not match closely,
adjust the balance; check periodically and update it." Root case: L5 M3 silent 08:37–12:11 on 2026-09-22 (235 panels
never deducted, reel reported 1,944 left when empty); L1 reels over-deducted to zero (list count too high).*

```
BALANCE RECONCILE
│
├─ TRIGGER  every 5-min tick · every re-baseline · at a confirmed exhaust (that feeder) · POST /api/inventory/reconcile
│
├─ INPUTS  per reel: LoadQty (confirmed at load / recount) · LoadClock (line clock then)
│          line clock = monotonic last-machine panels + panels added by HMI/supervisor adoption (persisted, dpc-state)
│          rate = the feeder-list count, ALWAYS (Danial: the list can't be wrong or the product won't qualify);
│                 UNITS: DB feeder map QtyPerUnit = per child board (× boards-per-panel); a pen-drive Mount Step / JUKI QTY
│                 = per PANEL as-is (2026-09-24: L1 charged 64 for a 16/panel feeder, L3 ×6, every reel to zero early)
│                 the real rate each exhaust proves (LoadQty ÷ panels) is reported only, never applied
│
├─ ACTION  expected = LoadQty − rate × (clock − LoadClock), clamped ≥ 0
│          tolerance = max(3 panels' worth, 1 % of LoadQty)
│          |tracked − expected| > tolerance → tracked := expected (audit BalanceReconcile) → StockOut write follows
│
├─ AT EXHAUST  real rate = LoadQty ÷ panels since load → learned per part (over-count visible: negative row)
│              attrition row = LIST theory vs reality: LoadQty − list × panels (negative = PVS hit zero first)
│
├─ SILENT MACHINE  line clock moved ≥ 10 panels between ticks, machine tally did not → alarm (health warn, audit
│                  MachineSilent), real-time enable re-sent (cooldown/report-guarded); reels covered by the reconcile
│
├─ INVARIANTS
│     • a physical recount / keyed count / new reel is a NEW base (LoadQty, LoadClock) — never overridden by an older one
│     • a reel PVS ran to zero stays tracked at zero (its UID is on the feeder) — never dropped at a re-baseline
│     • the line clock never goes backwards; if it did (state reset) the reel is left alone
│
├─ MUST NOT
│     ✗ use one machine's own tally as the clock     ✗ apply a learned rate (the list is the rate)     ✗ touch StockOut directly (the sync does)
│
├─ OUTPUT  log + audit per adjustment · /api/health serial.silent · attrition rows may be negative
│
└─ REVERSE  a supervisor recount (SetRemaining) sets the balance and the base
```

### Feeder decrement per board  ▫ to write
### Feeder Master — the only source for the reel count-down  🔧draft (built 2026-09-25)
*Danial: "a table per model — MC1..MC4, total mount shots per board and per panel — editable, a settings page in each
line's PVS; this is the master for the reel count-down, no other source is allowed."*

```
FEEDER MASTER
│
├─ WHAT  per line: one block per MODEL + SIDE → boards/panel + per machine (MC1..MC4) rows {feeder, part, shots/board}
│        shots are PER BOARD, whole numbers; per panel = shots × boards/panel (derived, never typed)
│
├─ IN FORCE  the block whose model + side = the running model + side. Every list consumer (count-down, forecast,
│            checklist, lot-end check, DPC boards/panel) reads it and nothing else.
│
├─ FILL  (a) supervisor edits on master.html (badge) — validated, versioned, history kept, audited FeederMasterSaved
│        (b) "Import from DB" (badge): preview row-by-row diff → apply
│        (c) pen-drive CSV per machine on master.html or the ⚙ page → import into the block (badge, must divide by boards/panel)
│        (d) AUTO: a model + side with no block yet gets the LIVE list imported as UNREVIEWED (pen-drive counts
│            ÷ boards/panel, must divide — else flagged) so the line is never blind; shown as UNREVIEWED until saved
│
├─ ACTION on save/import (running model)  rebuild list → re-baseline inventory → balance reconcile → StockOut sync
│
├─ INVARIANTS
│     • no fraction of a shot (26.5/board = the panel factor is wrong, not the part)
│     • the DB map / pen drive never feed the count-down directly again
│     • the previous version is kept (feeder-master-history.jsonl) — any save is reversible
│
├─ MUST NOT  ✗ learn/adjust shots from exhausts   ✗ silently change a block   ✗ track a model with no block
│            ✗ count from a cached list at start-up (no block = nothing tracked until the live read fills it)
│            ✗ let a pen-drive file or the DB map feed the count-down directly (both are imports only)
│
├─ OUTPUT  master.html (dropdown model, tab per machine, opens on the running model) · /api/master · audits
│
└─ REVERSE  re-save the previous version from history (supervisor)
```

### Pen-drive feeder list — when it applies  🔧draft (built 2026-09-24)
*Danial: "use the pen drive only if the current running program is from the pen drive, otherwise use from DB."*

```
PEN-DRIVE LIST APPLIES?
│
├─ TRIGGER  a pen-drive list is loaded for a machine · every 3-min model tick · every re-baseline
│
├─ GATE  machine program (C3P) known ?
│     ├─ no  → keep the pen-drive list for now (re-checked when the name arrives)
│     └─ yes → same MODEL and SIDE as the file's label ?   ("L307 - B SIDE _Cell1" vs "L307 B SIDE MC1")
│              ├─ yes → pen-drive list is the feeder list for that machine (counts PER PANEL as-is)
│              └─ no  → that machine uses the DB feeder map (counts per board × panel factor);
│                        audit ManualListIgnored once; a NEW load for a mismatching program is REFUSED
│                        (PROGRAM-MISMATCH) unless the supervisor forces it
│
├─ INVARIANTS
│     • a pen-drive list never outlives the program it came from
│     • the decision is per machine (a reshuffled cell can be on the pen drive while the rest use the DB)
│     • nothing is deleted: the list stays loaded and applies again if the machine returns to that program
│
├─ OUTPUT  /api/feeders/manual/status → applies + machineProgram per machine · log · audit
│
└─ REVERSE  Reload-from-DB drops the lists; loading the matching program's file re-applies
```

### Parts-out retire (consume)  ▫ to write
### StockOut sync  ▫ to write
### Program-mismatch alarm (+ re-ask before believing it)  🔧draft (built 2026-09-21)
*PVS caches each machine's program name (C3P) and normally never re-asks. The cache can go stale: L1 M4 2026-09-21
flashed "M4:L313 vs L307" while its HMI showed L307.*

```
PROGRAM-MISMATCH ALARM
│
├─ TRIGGER  the cached program names of the ONLINE, NON-SKIPPED machines give more than one model
│           · cue 2: a C1Z pickup report for the cached name comes back ALL ZERO
│
├─ ACTION  alarm shows at once (banner + health down)  AND  PVS re-asks the program name (C3P):
│            mismatch  → every machine in the comparison (the stale name can be on either side)
│            zero C1Z  → that machine, 4 s after the report releases the line
│          throttle per machine: 60 s while stopped · 5 min while in AUTO (A4E00 pollutes report reads)
│          a C3P held back by a report read is NOT counted — the next tick asks again
│
├─ CONFIRM  the fresh names agree → alarm clears by itself · still disagree → the alarm is REAL
│
├─ INVARIANTS
│     • read-only: never changes the selected model, the lot, a count or a feeder list
│     • a skipped or off-line machine neither raises nor joins a re-check
│     • no periodic C3P while the names agree (the serial stays quiet for report reads)
│
├─ MUST NOT
│     ✗ suppress or delay the alarm while re-asking     ✗ send C3P while a report is collecting
│
├─ OUTPUT  log "Program re-check M{n}…" · /api/status programWarn · supervisor can force it: POST /api/programs/query
│
└─ REVERSE  nothing to reverse (a read)
```

### Exhaust forecast  ▫ to write
### Downtime capture (R1-fed, auto-record + operator comment)  🔧draft
*Supersedes the 2026-08-18 operator-tap-only rule: the machine's R1 stream now auto-records downtime; the operator reason becomes a COMMENT on the auto-captured stop.*

```
DOWNTIME CAPTURE  (per line; fed by the R1 machine-condition stream)
│
├─ SOURCE  each machine's latched condition (MachineConditionTracker) + parts-out (R2E03) + board-completes (R0)
│
├─ AUTO-RECORD (no operator tap needed)
│     line was PRODUCING (a machine MOUNTING) and then stops  → open a downtime span, tagged with the MACHINE reason:
│         any ES → estop · any RC → error · all/most PW → starved · else SP → stopped · all offline → offline
│     a machine returns to MOUNTING (ST / board-complete)      → close the span (recovery)
│     capture which machine(s) were in the stop state
│
├─ OPERATOR COMMENT (annotates, does not trigger)
│     operator taps a reason (machine / waiting_part / no_air / rest / scheduled) and/or a free note
│       → added as a COMMENT on the open (or most-recent) auto stop. Effective reason = operator comment ?? machine reason.
│     break window is a HINT (defaults the comment to "rest"); never auto-excludes.
│
├─ INVARIANTS
│     • Downtime is only counted when the line WAS producing today and then stopped — never for a line that never ran
│       (Unknown/idle-from-start is NOT downtime).
│     • Per-cell parts-exhaust recovery (parts-out → cell mounts again) is unchanged — the automatic half already there.
│     • READ-ONLY toward the machine — never sends a control command.
│
├─ MUST NOT
│     ✗ record idle/never-started time as downtime     ✗ send C5* to the machine     ✗ change board count / lot / stock
│
├─ OUTPUT  /api/stop/state (open stop + machine reason + comment + elapsed) · daily report tallies by reason · per-cell recovery
│
└─ REVERSE  operator re-comments to re-classify; a span closes on recovery or at shift end.
```

### Daiya Graph (auto-filled production report)  🔧draft
*The GMS-QP-15F06 "Daiya Graph" the operators fill by hand — auto-filled from PVS per line/date/shift. Printable `/daiya.html` (+ .xls export later).*

```
DAIYA GRAPH  (per line · date · shift)
│
├─ SOURCE
│     • header/lot  ← Model+Side, CurrentLotNo (PO), lot target (=LOT SIZE / TARGET-SHIFT), board/side (BOARD NAME)
│     • names       ← SCANNED BADGES for the shift: line-leader = L2+ badge, machine operators [1-4] = L1 badges
│     • start/end   ← first & last board of the shift (PROD START / END TIME)
│     • counter/out ← PVS board count (Sony C1M rejects A4E00) → MACHINE COUNTER; lot pcs → TOTAL OUTPUT
│     • hourly grid ← M4 (line-out) board-completes bucketed per clock hour → panel; pcs = panel × per-panel
│     • downtime    ← the R1-fed downtime spans, each mapped to a LEGEND code:
│                       rest→B · scheduled→A/B · waiting_part→K · operator machine / R1 estop→N or I ·
│                       R1 error→J · R1 starved→O · model change→F/G
│
├─ INVARIANTS
│     • READ-ONLY report — assembles existing data; changes no count/lot/stock/machine.
│     • Hourly "pcs" = line-out (last machine), not summed across machines.
│     • Names only from a scanned badge — never typed/guessed.
│
├─ MUST NOT
│     ✗ write to the machine or DB from the report   ✗ invent a name / a stop   ✗ double-count boards across machines
│
├─ OUTPUT  /api/daiya (assembled JSON) → printable /daiya.html matching GMS-QP-15F06; .xls export (later)
│
└─ REVERSE  n/a (report). Live current shift + a date/shift history picker.
```

### Machine condition (R1 operating state)  🔧draft
*The machine's own operating state, from the Sony R1 real-time stream (SI-F manual §8.2 Table 8-5). Answers "is it actively mounting" without inferring from a frozen rate.*

```
MACHINE CONDITION  (per machine, latched from the R1 real-time stream)
│
├─ SOURCE  R1 "Operation Information" codes the machine pushes (real-time ON via C5RO, already enabled):
│     ST=auto started · SP=auto stopped · PW=board-wait · PE=wait released · LD=board at placement
│     AU=entered auto · MA=not-auto · OL=online · FL=off-line · ES=e-stop · RC=recovering · HT/SH=halt/release
│     (+ R0 board-complete = definitely mounting)
│
├─ DERIVE (latch last significant signal)
│     ST | PE | LD | board-complete .......... MOUNTING   (actively placing)
│     PW ...................................... STARVED    (in auto, waiting for a board)
│     SP ...................................... STOPPED
│     ES ...................................... E-STOP
│     MA ...................................... NOT-AUTO   (operator left auto mode)
│     RC ...................................... RECOVERING
│     HT ...................................... HALTED (until SH → back to IDLE)
│     AU / OL (not yet running) ............. IDLE (online, ready)
│     FL / never online ..................... OFFLINE
│     nothing yet (post-restart) ............ UNKNOWN
│
├─ INVARIANTS
│     • READ-ONLY — condition is derived from what the machine reports. It reflects state, never sets it.
│     • bph stays a productive-time rate; the UI shows liveness from CONDITION, not the frozen bph.
│
├─ MUST NOT
│     ✗ send any control command to the machine (C5ST/C5SP/C5HT…) — PVS never starts/stops a mounter
│     ✗ change the board count, lot, or feeder tracking from a condition change
│
├─ OUTPUT  per-machine {condition, since, lastBoardAgeSec} in /api/status + /api/health; a status bar on verify.html
│
└─ REVERSE  n/a (pure monitor). Post-restart shows UNKNOWN until the first R1 (persistence/D0-poll = follow-on).
```


## Lot-end count check (Danial 2026-09-25)

```
LOT SIZE is the truth: total boards produced in a lot MUST equal the lot size. The machine count is for
reference and the parts-exhaust forecast. Panels made while PVS was down are lost to PVS's own count and
M4 refuses the C1M read (A4E00), so the OPERATOR closes the gap near lot end.
├─ WHEN     PVS count is within 10 boards of the lot size (or past it) and the check is not yet answered for this lot
├─ POP-UP   operator screen (verify.html lotFlash): "COUNT CHECK — read M4 completed-PWB (panels), compare with PVS N panels"
│           ├─ Matches  → badge → audit LotEndCheck "count matches"; pop-up gone for this lot
│           ├─ Update   → machine panels + badge → capped adoption (never above lot size +10 %, never backwards)
│           │            → audit AdoptCount + LotEndCheck "count updated a→b"; refused values keep PVS's count
│           └─ Later    → snoozes 5 min on that screen only; the check stays due on the server
├─ WHO      any registered badge answers (operators update the card near lot end); force stays supervisor-only (⚙ Set count)
├─ PERSIST  answered lot + note in lot-progress.json (survives a restart); once per lot
└─ MUST NOT ✗ auto-adopt from a serial counter to satisfy the check   ✗ dismiss without an answer   ✗ change the lot size
```
