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

## Automatic functions (no one triggers)

### Board count  ▫ to write
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
