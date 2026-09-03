namespace Pvs.Core.Inventory;

/// <summary>One ProductBOM row, exactly as stored — no interpretation.</summary>
/// <param name="BomId">ProductBOM.BOMID, so a flagged row can be found and corrected.</param>
/// <param name="SupplyPosition">Empty when the row has no feeder position (hand-placed, or never imported).</param>
public sealed record BomRow(
    int BomId,
    int ProductId,
    int? Line,
    string Side,
    int? Machine,
    string SupplyPosition,
    string PartNumber,
    int Quantity)
{
    public bool IsPositioned => !string.IsNullOrWhiteSpace(SupplyPosition);
}

/// <summary>How far a BOM can be trusted to produce a number someone will act on.</summary>
public enum BomTrustLevel
{
    /// <summary>Nothing found against it. Per-board quantities may be quoted.</summary>
    Trusted,
    /// <summary>Usable, but something looks wrong. Any figure derived from it must carry the caveat.</summary>
    Suspect,
    /// <summary>Not fit to derive quantities from at all. Quote no per-board figure; say why instead.</summary>
    Unusable,
}

/// <summary>Why a BOM was downgraded. The part/row is named so it can be fixed, not just distrusted.</summary>
/// <param name="Detail">Plain sentence naming the row and the number that looks wrong.</param>
public sealed record BomIssue(BomTrustLevel Level, string Code, string Detail);

/// <summary>
/// The verdict on one model's BOM.
/// <para>
/// <see cref="CanQuoteQuantities"/> is the point of the whole type: call it before turning a BOM into a
/// shortage, a cost, or a reorder figure. When it is false the honest output is "L254's BOM has no feeder
/// positions, so I cannot say how many pieces a board needs" — not a number that looks authoritative.
/// </para>
/// </summary>
public sealed record BomVerdict(int ProductId, string Model, BomTrustLevel Level, IReadOnlyList<BomIssue> Issues)
{
    /// <summary>False when the BOM is <see cref="BomTrustLevel.Unusable"/>. Gate every derived figure on this.</summary>
    public bool CanQuoteQuantities => Level != BomTrustLevel.Unusable;

    /// <summary>True when a figure may be stated without a caveat.</summary>
    public bool IsClean => Level == BomTrustLevel.Trusted;

    /// <summary>
    /// Appends the reason to a figure so a caveat cannot be dropped by forgetting to write one.
    /// A clean BOM returns the figure untouched.
    /// </summary>
    public string Qualify(string figure)
    {
        if (IsClean) return figure;
        string why = Issues.Count == 0 ? "BOM not verified" : Issues[0].Detail;
        return Level == BomTrustLevel.Unusable
            ? $"NOT RELIABLE - {why}. Figure withheld: {figure}"
            : $"{figure} (UNVERIFIED - {why})";
    }
}

/// <summary>
/// Decides whether a model's BOM is fit to derive per-board quantities from.
/// <para>
/// This exists because <c>ProductBOM.Quantity</c> is not a trustworthy per-board mount count. The app's own
/// contract says so, and the data proves it: L254 (ProductID 0) stores <c>WA6-3110-000</c> at 31 per board
/// when the real figure is 1, in a BOM where not one row carries a supply position. A shortage report built
/// on that number asks the customer for 16,120 pieces instead of 520.
/// </para>
/// <para>
/// Every check here is a signature of the ways that data actually goes wrong, so a bad BOM is caught before
/// it becomes a purchase request rather than after. Nothing is repaired — a wrong quantity is a person's
/// decision to correct, and silently "fixing" it would hide the entry error that produced it.
/// </para>
/// </summary>
public static class BomTrust
{
    /// <summary>A quantity this many times the model's median is treated as an outlier.</summary>
    public const int OutlierFactor = 8;

    /// <summary>No quantity below this is ever called an outlier, however small the median.
    /// Boards genuinely carry a dozen of one decoupling capacitor; the median alone would flag them.</summary>
    public const int OutlierFloor = 10;

    /// <summary>Below this share of rows carrying a feeder position, the BOM is not a machine export at all.</summary>
    public const double MinPositionedShare = 0.5;

    /// <summary>
    /// Assesses one model's BOM rows. <paramref name="rows"/> must be every row for that model, across all
    /// lines and sides — the median that outlier detection rests on is meaningless on a filtered subset.
    /// </summary>
    public static BomVerdict Assess(int productId, string model, IReadOnlyList<BomRow> rows)
    {
        var issues = new List<BomIssue>();
        model = string.IsNullOrWhiteSpace(model) ? $"ProductID {productId}" : model.Trim();

        if (rows is null || rows.Count == 0)
        {
            issues.Add(new BomIssue(BomTrustLevel.Unusable, "EMPTY", $"{model} has no BOM rows at all"));
            return new BomVerdict(productId, model, BomTrustLevel.Unusable, issues);
        }

        AddPositionIssues(model, rows, issues);
        AddOutlierIssues(model, rows, issues);
        AddStructureIssues(model, productId, rows, issues);
        AddLineLayoutIssues(model, rows, issues);

        var level = issues.Count == 0
            ? BomTrustLevel.Trusted
            : issues.Max(i => i.Level);

        // Worst first, so Qualify() quotes the reason that actually matters.
        return new BomVerdict(productId, model, level, issues.OrderByDescending(i => i.Level).ToList());
    }

    /// <summary>
    /// A BOM with no feeder positions was never reconciled against a machine feeder list, so nothing in it
    /// has been checked against the machines — including its quantities. It also cannot drive a scan
    /// checklist, so the line cannot verify the model even with material in hand.
    /// </summary>
    private static void AddPositionIssues(string model, IReadOnlyList<BomRow> rows, List<BomIssue> issues)
    {
        int positioned = rows.Count(r => r.IsPositioned);
        if (positioned == 0)
        {
            issues.Add(new BomIssue(BomTrustLevel.Unusable, "NO_POSITIONS",
                $"{model}: not one of {rows.Count} BOM rows has a supply position, so no quantity in it has " +
                "ever been checked against a machine feeder list"));
            return;
        }

        double share = (double)positioned / rows.Count;
        if (share < MinPositionedShare)
            issues.Add(new BomIssue(BomTrustLevel.Suspect, "FEW_POSITIONS",
                $"{model}: only {positioned} of {rows.Count} BOM rows carry a supply position"));
    }

    /// <summary>
    /// Flags a quantity that stands far outside the model's own distribution. Deliberately relative to the
    /// median rather than an absolute cap: a board with 40 identical capacitors is ordinary, but a single
    /// 31 among rows that are otherwise all 1 is an entry error.
    /// </summary>
    private static void AddOutlierIssues(string model, IReadOnlyList<BomRow> rows, List<BomIssue> issues)
    {
        int median = Median(rows.Select(r => r.Quantity).ToList());
        long threshold = Math.Max(OutlierFloor, (long)median * OutlierFactor);

        foreach (var r in rows.Where(r => r.Quantity >= threshold).OrderByDescending(r => r.Quantity))
            issues.Add(new BomIssue(BomTrustLevel.Suspect, "QTY_OUTLIER",
                $"{model}: {r.PartNumber} is {r.Quantity} per board (BOMID {r.BomId}) against a model median " +
                $"of {median} - verify against the machine feeder list before ordering to it"));

        if (rows.Any(r => r.Quantity <= 0))
            issues.Add(new BomIssue(BomTrustLevel.Suspect, "QTY_NOT_POSITIVE",
                $"{model}: {rows.Count(r => r.Quantity <= 0)} row(s) have a quantity of zero or less"));
    }

    /// <summary>Rows that cannot be attributed to a line or machine, and product IDs that should not exist.</summary>
    private static void AddStructureIssues(string model, int productId, IReadOnlyList<BomRow> rows, List<BomIssue> issues)
    {
        if (productId <= 0)
            issues.Add(new BomIssue(BomTrustLevel.Suspect, "PRODUCT_ID",
                $"{model} is stored under ProductID {productId}, which is not a valid product key"));

        int noLine = rows.Count(r => r.Line is null or <= 0);
        if (noLine > 0)
            issues.Add(new BomIssue(BomTrustLevel.Suspect, "NO_LINE",
                $"{model}: {noLine} of {rows.Count} rows have no line, so they belong to no line's layout"));

        int noMachine = rows.Count(r => r.Machine is null or <= 0);
        if (noMachine > 0)
            issues.Add(new BomIssue(BomTrustLevel.Suspect, "NO_MACHINE",
                $"{model}: {noMachine} of {rows.Count} rows are not assigned to a machine"));
    }

    /// <summary>
    /// Decides whether summing a part's rows ACROSS LINES reconstructs a board or double-counts it — the same
    /// column meaning opposite things in different models, which no amount of care at the call site can resolve.
    /// <para>
    /// Verified against Canon's own documents, 7 Aug 2026:
    /// <list type="bullet">
    /// <item>L307 and L313 keep <b>B Side under Line 1 and A Side under Line 5</b> — complementary halves of one
    /// board. Summing across lines is REQUIRED; every two-sided part reconciles with Canon only when summed.</item>
    /// <item>L261 keeps <b>two complete copies of all 34 parts, on Lines 1 and 3, both sides on each</b>.
    /// Summing across lines DOUBLES every quantity.</item>
    /// </list>
    /// The distinguishing signature is whether one part+side is stored on more than one line. Complementary
    /// layouts never repeat a side; duplicate layouts always do.
    /// </para>
    /// </summary>
    private static void AddLineLayoutIssues(string model, IReadOnlyList<BomRow> rows, List<BomIssue> issues)
    {
        // Only rows attributed to a real line can be judged; Line-NULL "Full" rows are board-level entries.
        var lined = rows.Where(r => r.Line is > 0).ToList();
        if (lined.Count == 0) return;

        var repeated = lined
            .GroupBy(r => (r.PartNumber, Side: (r.Side ?? "").Trim()), StringTupleComparer.Instance)
            .Where(g => g.Select(r => r.Line!.Value).Distinct().Count() > 1)
            .ToList();

        if (repeated.Count == 0) return;

        var lines = string.Join(", ", repeated
            .SelectMany(g => g.Select(r => r.Line!.Value))
            .Distinct().OrderBy(n => n));

        issues.Add(new BomIssue(BomTrustLevel.Suspect, "DUPLICATE_LINE_LAYOUT",
            $"{model}: {repeated.Count} part+side combination(s) are stored on more than one line " +
            $"(lines {lines}), so these are DUPLICATE layouts, not complementary sides - summing a part's " +
            "rows across lines double-counts it. Use one line's copy, or reconcile the copies first"));
    }

    /// <summary>Case-insensitive comparer for the (part, side) grouping key.</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string PartNumber, string Side)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string PartNumber, string Side) a, (string PartNumber, string Side) b) =>
            string.Equals(a.PartNumber, b.PartNumber, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Side, b.Side, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string PartNumber, string Side) x) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(x.PartNumber ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(x.Side ?? ""));
    }

    /// <summary>Lower median of the sorted quantities. Never returns less than 1, so the threshold stays sane.</summary>
    private static int Median(List<int> values)
    {
        if (values.Count == 0) return 1;
        values.Sort();
        int m = values[values.Count / 2];
        return m > 0 ? m : 1;
    }
}
