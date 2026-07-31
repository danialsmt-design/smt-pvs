namespace Pvs.Core.Verification;

/// <summary>
/// One audit record of a verification action (a completed/skipped parts change, a full-scan result,
/// or an inventory re-count). Written to the record sink for traceability.
///
/// STAGE 2: records go to a local append-only log (no production DB writes yet). STAGE 3 will also
/// forward these to ReelPart-New (StockOuts / ReelAdjustments / a verification-log table) once the
/// DB owner has signed off on the target tables.
/// </summary>
public sealed record VerificationRecord(
    DateTime At,
    string Line,
    string Mode,              // "PartsChange" / "ShiftChange" / "LotEnd" / "Recount"
    int Machine,
    int Feeder,
    string Outcome,           // "Completed" / "Skipped" / "Released" ...
    string? Operator = null,
    string? Supervisor = null,
    string? ExpectedPart = null,
    string? OldReelUid = null,
    string? NewReelUid = null,
    string? NewReelPart = null,
    int? Quantity = null,
    bool Overridden = false,
    string? Note = null,
    string? LotNo = null);      // production lot number (PONumber) running when this happened

/// <summary>Where verification records are persisted.</summary>
public interface ILineRecordSink
{
    Task WriteAsync(VerificationRecord record, CancellationToken ct = default);
}
