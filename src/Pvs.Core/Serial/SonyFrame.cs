using System.Text;

namespace Pvs.Core.Serial;

/// <summary>
/// Wire framing for the Sony SMT host protocol (RS-232C, SI-E / SI-F).
///
/// A message on the wire is:
///     STX | char-count (2 hex) | payload | checksum (2 hex) | ETX
///
/// char-count = number of characters in the payload, as two ASCII hex digits.
/// checksum   = two's-complement of the summed ASCII values of (the two count chars + the
///              payload chars), as two ASCII hex digits.
///
/// The checksum spans the COUNT field as well as the payload. Verified against the manual's
/// worked example (payload "A0" -> frame "02A02D") AND against live machine traffic
/// (2026-07-22): "C5RO" -> "04C5RO83", "R1OL" -> "04R1OL7E", "A4E02" -> "05A4E027F".
/// </summary>
public static class SonyFrame
{
    public const char STX = '\x02';
    public const char ETX = '\x03';

    /// <summary>Two ASCII hex digits (uppercase) of the low byte of <paramref name="value"/>.</summary>
    private static string Hex2(int value) => (value & 0xFF).ToString("X2");

    /// <summary>Checksum over the count field + payload, per the protocol.</summary>
    public static string Checksum(string countAndPayload)
    {
        int sum = 0;
        foreach (char c in countAndPayload) sum += c;
        return Hex2((0x100 - (sum & 0xFF)) & 0xFF);
    }

    /// <summary>Builds a complete on-the-wire frame (including STX/ETX) for a payload.</summary>
    public static string Build(string payload)
    {
        string countAndPayload = Hex2(payload.Length) + payload;
        return STX + countAndPayload + Checksum(countAndPayload) + ETX;
    }

    /// <summary>
    /// Extracts the payload from a raw frame and verifies its length + checksum.
    /// Returns false (payload = null) if the frame is malformed or the checksum fails.
    /// The input may or may not include the STX/ETX sentinels.
    /// </summary>
    public static bool TryParse(string rawFrame, out string? payload)
    {
        payload = null;
        if (string.IsNullOrEmpty(rawFrame)) return false;

        // Tolerate the sentinels being present or already stripped.
        string inner = rawFrame.Trim(STX, ETX);
        if (inner.Length < 4) return false; // need at least count(2) + checksum(2)

        string count = inner.Substring(0, 2);
        if (!int.TryParse(count, System.Globalization.NumberStyles.HexNumber,
                null, out int declaredLen))
            return false;

        // count(2) + payload(declaredLen) + checksum(2)
        if (inner.Length != 4 + declaredLen) return false;

        string candidate = inner.Substring(2, declaredLen);
        string checksum = inner.Substring(2 + declaredLen, 2);

        if (!string.Equals(Checksum(count + candidate), checksum,
                StringComparison.OrdinalIgnoreCase))
            return false;

        payload = candidate;
        return true;
    }

    /// <summary>
    /// Pulls every complete STX..ETX frame out of a rolling receive buffer.
    /// Returns the raw frames found and sets <paramref name="remainder"/> to any trailing
    /// partial data (kept for the next read). Leading junk before an STX is discarded.
    /// </summary>
    public static IReadOnlyList<string> Extract(string buffer, out string remainder)
    {
        var frames = new List<string>();
        int i = 0;
        while (true)
        {
            int start = buffer.IndexOf(STX, i);
            if (start < 0) { remainder = string.Empty; return frames; }

            int end = buffer.IndexOf(ETX, start + 1);
            if (end < 0) { remainder = buffer.Substring(start); return frames; }

            frames.Add(buffer.Substring(start, end - start + 1));
            i = end + 1;
        }
    }
}
