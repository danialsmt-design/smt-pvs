# PVS Operating Rules

The rules the Parts Verification System follows on every line. All lines run these; behaviour is identical unless noted.

---

## 🎯 Lot lifecycle — supervisor only
- **Lot change, force-end lot, and set-produced-count are supervisor (L2+ badge) ONLY.** PVS never does them on its own.
- PVS **never auto-follows the DB lot, never auto-ends a lot, never auto-writes a production count** under a lot it picked itself (`SupervisorLotOnly`, default on — enforced on both the auto-detect and pinned-model paths).
- **New lot, same model = just select it** on the ⚙ Model & Lot screen (this resets the board count to 0). No force-end needed. Force-end is only for closing a lot *without* starting the next one.
- PVS's **automatic** jobs are: count boards against the supervisor's lot, and manage stock-out quantities (`syncStockOuts`).

## 🧩 Model
- The supervisor pins the running model; PVS **verifies it against the machine's C3P program** (verified / mismatch / unverified).
- **Set model supersedes an in-progress check** — it cancels the stale check and re-pins in one action, so a shift-change scan started for the old model can never deadlock the change.
- If a machine's program changes mid-lot without the supervisor ending/changing the lot, PVS **flashes a program-mismatch warning** — it never silently resets.

## ⚙ Machines & skip
- **Every line is 4 machines. All are serial (Sony) except Line 3 machine 1 = JUKI** (manual trigger, no serial). Line 3 serial ports: M2 = COM3, M3 = COM6, M4 = COM4.
- **Skip is supervisor-only. PVS never auto-skips a machine — even if its serial isn't answering.** A silent Sony stays in the parts check until a supervisor explicitly skips it. (A stopped/parked Sony still answers `A4E00`; a truly dead one must still stay in the check.)
- A **skipped machine** is excluded from the program-mismatch alarm and from program-verify.

## 🔩 Feeders
- **All feeder lists come from ONE source: ProductBOM** (fresh from the DB, never a stale local cache).
- **Total feeder parts must equal the Canon BOM** — regardless of how many machines or cells the line is running.
- **Breakdown reshuffle:** the supervisor loads per-machine feeder CSVs from a pen drive (both Sony and JUKI RS-1 / "FEEDER LIST CANON" export formats are read). Every row in a loaded file goes to the chosen machine; the loaded total must still match Canon BOM.
- A **slash in a part number = a substitute** part.

## 🔢 Counting
- **Two counters:** the never-reset serial report counter vs the operator-resettable panel count. Adoption is **capped at the lot target**.
- **Count reset-guard:** if a tech resets the count mid-lot, PVS keeps counting through to lot end.
- **Reconcile right after a board-complete** (in the ~40-second gap), not on a blind timer, so no count is lost.

## 📦 Reels & stock — THE STOCK MODEL (governing)
- **StockOut = reels in the feeders + standby reels near the line.** StockOut holds ONLY what is physically at the line right now — loaded reels **plus** rack spares. It is **not** a history of everything ever issued.
- **A reel on a feeder is PRODUCTION, never a spare. A reel issued to the line but NOT on a feeder is a STANDBY** (rack spare). The **spare / "spare ×N"** count for a part = its StockOut reels **not on any feeder**.
- **When a reel is CONSUMED (runs out in the line) it LEAVES StockOut for `ConsumedReels`.** A **parts-change swap** = the machine ran that feeder OUT, so the outgoing reel is consumed: PVS records it in `ConsumedReels`, then **zeroes its StockOut quantity — UNCONDITIONALLY** (no rank/remaining threshold; a parts-change means it ran out, and the tracked remaining is drift-prone). **Reversible** via the reel-restore action (supervisor). **Model-changes are NOT retired** — those reels come off still-full and return to standby.
- The **exhaust card** then reads that clean StockOut: it **predicts usage/run-out on the reel in the feeder**, and shows the rest of that part's StockOut as the **standby reel(s)**.
- Reel rank **A / B / C** (`PartRanks`) is recorded on the consumed reel for the restore record; it **NO LONGER gates** the retire (was A:0 / B:<30 / C:<200 — removed 2026-08-17).
- The zeroed remainder accumulates as **attrition** per part (`PartAttrition`); a **bi-weekly attrition report** goes out (1st & 15th).
- A spare count that looks **too high** = **consumed reels that never left StockOut** (a reel changed the old way, outside PVS's parts-change flow). Fix = get the consumed reel recorded out of StockOut; **never recount loaded reels**.

## 🚀 Deploy / operations
- **Ship all `Pvs.*.dll` together** (Core + Data + LineApp) — never a partial DLL swap.
- Deploy only through the **guard** (`deploy-pvs.ps1`): full-set check → backup → health-check → **auto-rollback** if it doesn't come up.
- **Roll out to idle lines** — detect "stopped" by **board-rate = 0**, not the online flag.

---

## Planned (not yet built)
- **Bare-board pack-label capture** at input: scan the pack label (lot # + unique ID), lot-match + de-duplicate + reconcile to lot size, with pack quantity pulled from `StockOuts`. Pending the final board→model (`ModelBoard`) mapping.
