# PVS Review — Preferences

Danial edits this, or the agent updates it from his feedback. The agent reads it every run.

## Priorities (weight these to the top)
- Line-stoppers first: ProductBOM gaps/typos and under-issued lots that would halt scanning.
- Then reliability issues that cause false alarms or lost counts.
- Then material shortfalls (request-with-lead-time warnings).

## Muted / don't report
- (none yet)

## Notes / standing decisions
- syncStockOuts stays ON for Line 1; writeProductionCount stays OFF (no pvs_ro INSERT grant).
- Don't propose auto-deploy of anything — Report + Draft only.
- Lines 2–5 exist in the DB; Line 2 pilot is the near-term expansion, then Line 3 (single-sided L254 = Side 'Full').

## Feedback log (Danial: jot 👍/👎 + notes; the agent folds these into the lists above)
- (empty)
