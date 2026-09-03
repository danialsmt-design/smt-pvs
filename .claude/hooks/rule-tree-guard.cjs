// PVS rule-tree guard — PostToolUse on Write|Edit|MultiEdit.
// On any edit to PVS source, reminds of the function rule trees (docs/pvs-rule-trees.md).
// If an Unload-path change references a forbidden effect (StockOut write / attrition / consume),
// it flags loudly. The reminder is fed back to the model via hookSpecificOutput.additionalContext.
let raw = '';
process.stdin.on('data', d => (raw += d));
process.stdin.on('end', () => {
  let j = {};
  try { j = JSON.parse(raw); } catch { process.exit(0); }
  const ti = j.tool_input || {};
  const f = String(ti.file_path || '').replace(/\\/g, '/');
  const c =
    ti.new_string ||
    ti.content ||
    ti.replacement ||
    (Array.isArray(ti.edits) ? ti.edits.map(e => e.new_string || '').join('\n') : '') ||
    '';
  // Only act on PVS source (and the rule-trees doc itself).
  if (!/Dantec\/PVS\/(src|docs\/pvs-rule-trees)/i.test(f)) process.exit(0);

  let warn = '';
  if (/unload/i.test(c) &&
      /(UpdateReelQtyAsync|AddPartAttritionAsync|RecordConsumedReelAsync|StockOuts?\.Quantity|SyncRemainingToStockOuts)/.test(c)) {
    warn = '⛔ RULE-TREE CHECK (Unload): this change references a StockOut write / attrition / consume inside Unload code. ' +
           'Per docs/pvs-rule-trees.md the Unload tree MUST NOT write StockOut, end/reset the lot, or write attrition. ' +
           'Confirm this is not straying before continuing.';
  }
  let msg = '📐 PVS rule-tree reminder: this edit touches PVS source. Check it against docs/pvs-rule-trees.md ' +
            '— Trigger → Gate → Confirm → Action → INVARIANTS → MUST-NOT → Output → Reverse. ' +
            'If the function has no tree yet, write the tree first.';
  if (warn) msg = warn + '\n\n' + msg;

  process.stdout.write(JSON.stringify({
    hookSpecificOutput: { hookEventName: 'PostToolUse', additionalContext: msg }
  }));
});
