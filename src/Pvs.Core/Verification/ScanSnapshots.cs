using Pvs.Core.People;

namespace Pvs.Core.Verification;

/// <summary>
/// Serialisable snapshots of an in-progress scan (full-scan or model-change) so a check survives an app
/// restart / crash / accidental cancel and the operator resumes from the exact feeder they were on.
/// Pure data (records) — the caller persists them (e.g. to JSON) and rebuilds the session with Restore.
/// </summary>
public sealed record BadgeSnapshot(string UserId, string Name, string AccessLevel)
{
    public static BadgeSnapshot? From(Badge? b) => b is null ? null : new(b.UserId, b.Name, b.AccessLevel);
    public Badge ToBadge() => new(UserId, Name, AccessLevel);
}

public sealed record FeederCheckSnapshot(
    int Machine, int Feeder, string ExpectedPart,
    FeederCheckStatus Status, string? ScannedPart, string? ReelUid, BadgeSnapshot? ReleasedBy);

public sealed record FullScanSnapshot(
    ScanPurpose Purpose, FullScanState State, BadgeSnapshot? Operator,
    int Cursor, List<int> Confirmed, List<FeederCheckSnapshot> Items);

public sealed record ModelChangeItemSnapshot(
    int Machine, int Feeder, string ExpectedPart,
    FeederCheckStatus PartStatus, string? ScannedPart, string? ReelUid, BadgeSnapshot? ReleasedBy,
    int? CurrentQty, int? ConfirmedQty, QtyOutcome QtyOutcome, BadgeSnapshot? QtyCorrectedBy);

public sealed record ModelChangeSnapshot(
    ModelChangeState State, BadgeSnapshot? Operator,
    int Cursor, List<int> Confirmed, List<ModelChangeItemSnapshot> Items);
