# PVS Action Checklist

What PVS does — and what the operator/supervisor must do — for each action.
Legend: **[built]** live now · **[build]** on the to-do list.

---

## SUPERVISOR actions (require an L2+ supervisor badge)

### 1. Select / Change lot
- [ ] Scan **supervisor badge** (L2+) — **[built]** required, no lot change without it
- [ ] Confirm the **model** (auto-read from the machines; check it matches what's running)
- [ ] **Machine selection:** every machine shows **ticked**; **untick any machine NOT used** for this lot — **[build]**
- [ ] Pick the **lot number** from the list → sets the **target** + delivery date — **[built]**
- [ ] PVS scopes the **feeder checklist + scan + exhaust to the ticked machines only** — **[build]**
- [ ] **Verify the feeder list** for the used machines (scan / confirm each) — **[built]**
- [ ] PVS **reconciles total feeders vs the BOM** for the lot → warn / override / block on mismatch — **[build, behavior TBD]**
- [ ] Confirm → **lot bound, counting starts** against it
- PVS **never** auto-selects or auto-changes the lot — **[built]**

### 2. Force-end lot
- [ ] Scan **supervisor badge** (L2+) — **[built]**
- [ ] Confirm the **final count** (fix via Set-count first if the machine was reset)
- [ ] PVS **finalizes the count → writes to DailyProductionCount**, then **clears** the lot
- [ ] Ready for the next Select-lot
- PVS **never** auto-ends — not on a model change, not on hitting target — **[built]**

### 3. Set / force production count
- [ ] Scan **supervisor badge** (L2+) — **[built]**
- [ ] Enter the **actual count** (e.g. after a tech reset zeroed the machine)
- [ ] PVS **re-anchors its lot count** to the entered value (force overrides the target cap) — **[built]**
- [ ] **Audit row** written: who / when / old→new — **[built]**

---

## OPERATOR actions

### 4. Parts / feeder change
- [ ] Trigger the change — **auto** on parts-out, or **manual** (Line 3: manual) picking machine + feeder — **[built]**
- [ ] Scan the new reel: **PART barcode**, then **UID**
- [ ] PVS **verifies against the expected part** — substitute (`/`) accepted as valid — **[built]**
- [ ] **Confirm quantity** (pre-filled from parts control)

---

## PVS AUTOMATIC (no one does anything)
- [x] **Count boards** against the supervisor's lot (M4 = Cell 4 = PCB-out) — **[built]**
- [x] **Hold the count through a machine reset** — never ratchets down; only a supervisor force sets it down — **[built]**
- [x] **Write production count → DB**, but only against the **supervisor's** lot (never a self-picked one) — **[built]**
- [x] **Manage stock-out quantities** (reel consumption) — **[built]**
- [x] **Flash a program-mismatch warning** if machines disagree on model mid-lot — **[built]**
- [ ] **Port→machine mapping dynamic by Cell** (machine# = Cell#) — **[build]**
- [ ] **Reconcile the count right after a board-complete** (~40 s gap), not on a blind timer — **[build]**
