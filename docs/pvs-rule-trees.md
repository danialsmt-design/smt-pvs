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

### Feeder decrement per board  ▫ to write
### Parts-out retire (consume)  ▫ to write
### StockOut sync  ▫ to write
### Program-mismatch alarm  ▫ to write
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
