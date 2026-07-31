using System.Text.RegularExpressions;

namespace Pvs.Core.Serial;

public enum MessageKind
{
    Unknown,
    /// <summary>R2E03 — genuine parts-out (machine stopped, out of parts). The ONLY code that raises an operator prompt.</summary>
    PartsOut,
    /// <summary>R2E04 — parts-out on cassette/cart alternation. Part of the same event burst as R2E03.</summary>
    PartsOutAlternation,
    /// <summary>R2E02 — pickup error (abnormal/missed pickup/recognition). NOT an exhaustion on its own; ~17x more frequent than R2E03.</summary>
    PickupError,
    /// <summary>R2E05 — transport error.</summary>
    TransportError,
    /// <summary>R2E08 — error stop (any other error; Z is 000). Tray faults present as this.</summary>
    ErrorStop,
    /// <summary>R0CT / R0EP — one board finished. Drives the board-rate / consumption.</summary>
    BoardComplete,
    /// <summary>R1xx — a machine status change (online, auto, stopped, recovery running, real-time on, ...).</summary>
    Status,
    /// <summary>R3GD / R3NG — recovery succeeded / failed.</summary>
    Recovery,
    /// <summary>A0/A2/A3 — acknowledge / end-of-transfer / not-found.</summary>
    Ack,
    /// <summary>A1Enn — negative ack, resend request.</summary>
    Nak,
    /// <summary>A4Enn — command rejected (wrong state / protocol / off-line).</summary>
    Rejected,
    /// <summary>A5Enn — machine BUSY; the command should be retried shortly (SI-F only).</summary>
    Busy
}

/// <summary>
/// A decoded Sony host-protocol message. Fields are only populated where the message type
/// carries them (e.g. Feeder/Step for R2 codes, ErrorCode for A-nak/reject/busy).
/// </summary>
public readonly record struct SonyMessage(
    string Payload,
    MessageKind Kind,
    string? StatusCode = null,   // e.g. "OL","AU","ST","SP","RO","RC","ER" for R1; "CT"/"EP" for R0/R3
    int? Feeder = null,          // Z field = supply position (the feeder that ran out / errored)
    int? Step = null,            // N field = NC step number
    int? ErrorCode = null,       // the nn in A1Enn / A4Enn / A5Enn
    bool RecoveryOk = false)     // R3GD => true, R3NG => false
{
    // R2 error family: R2E{ee}S{sss}N{nnnn}Z{zzz}M{mmm}T{ttt}  (optionally trailing H/TI on SI-F)
    private static readonly Regex R2 = new(
        @"^R2E(?<ee>\d{2})S\d{3}N(?<n>\d{4})Z(?<z>\d{3})", RegexOptions.Compiled);
    private static readonly Regex R1 = new(@"^R1(?<code>[A-Z]{2,3})", RegexOptions.Compiled);
    private static readonly Regex R0 = new(@"^R0(?<code>CT|EP)", RegexOptions.Compiled);
    private static readonly Regex R3 = new(@"^R3(?<code>GD|NG)", RegexOptions.Compiled);
    private static readonly Regex AErr = new(@"^A(?<a>[145])E(?<ee>\d{2})", RegexOptions.Compiled);

    public static SonyMessage Parse(string payload)
    {
        payload ??= string.Empty;

        var m = R2.Match(payload);
        if (m.Success)
        {
            int ee = int.Parse(m.Groups["ee"].Value);
            int step = int.Parse(m.Groups["n"].Value);
            int feeder = int.Parse(m.Groups["z"].Value);
            var kind = ee switch
            {
                2 => MessageKind.PickupError,
                3 => MessageKind.PartsOut,
                4 => MessageKind.PartsOutAlternation,
                5 => MessageKind.TransportError,
                8 => MessageKind.ErrorStop,
                _ => MessageKind.Unknown
            };
            // For an error-stop (E08) the Z field is 000 = no specific feeder.
            int? feederOrNull = kind == MessageKind.ErrorStop && feeder == 0 ? null : feeder;
            return new SonyMessage(payload, kind, StatusCode: $"E{ee:D2}", Feeder: feederOrNull, Step: step);
        }

        m = R0.Match(payload);
        if (m.Success)
            return new SonyMessage(payload, MessageKind.BoardComplete, StatusCode: m.Groups["code"].Value);

        m = R3.Match(payload);
        if (m.Success)
            return new SonyMessage(payload, MessageKind.Recovery, StatusCode: m.Groups["code"].Value,
                RecoveryOk: m.Groups["code"].Value == "GD");

        m = R1.Match(payload);
        if (m.Success)
            return new SonyMessage(payload, MessageKind.Status, StatusCode: m.Groups["code"].Value);

        m = AErr.Match(payload);
        if (m.Success)
        {
            int a = int.Parse(m.Groups["a"].Value);
            int ee = int.Parse(m.Groups["ee"].Value);
            var kind = a switch { 1 => MessageKind.Nak, 4 => MessageKind.Rejected, 5 => MessageKind.Busy, _ => MessageKind.Unknown };
            return new SonyMessage(payload, kind, ErrorCode: ee);
        }

        if (payload is "A0" or "A2" or "A3")
            return new SonyMessage(payload, MessageKind.Ack, StatusCode: payload);

        return new SonyMessage(payload, MessageKind.Unknown);
    }

    /// <summary>True when this message should be de-bounced together with another as the same
    /// physical parts-out (same feeder + same step). A real exhaust emits E02/E02/E04/E03 within ~4s.</summary>
    public bool IsPartsOutBurstMember =>
        Kind is MessageKind.PickupError or MessageKind.PartsOutAlternation or MessageKind.PartsOut;
}
