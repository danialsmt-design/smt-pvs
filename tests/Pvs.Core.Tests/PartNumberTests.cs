using Pvs.Core.Verification;
using Xunit;

namespace Pvs.Core.Tests;

public class PartNumberTests
{
    // --- plain parts (no slash) behave exactly as before ---
    [Theory]
    [InlineData("AA1234-567", "AA1234-567", true)]
    [InlineData("AA1234-567", "aa1234-567", true)]   // case-insensitive
    [InlineData("  AA1234-567 ", "AA1234-567", true)] // trimmed
    [InlineData("AA1234-567", "AA1234-568", false)]  // different part = mismatch (interlock holds)
    [InlineData("", "", true)]                        // both blank matched before; keep it
    [InlineData("AA1234-567", "", false)]
    public void Plain_parts_match_exactly(string a, string b, bool expected) =>
        Assert.Equal(expected, PartNumber.Matches(a, b));

    // --- slash = substitute list: any listed alternative is accepted ---
    [Theory]
    [InlineData("A/B", "A", true)]      // scan the primary
    [InlineData("A/B", "B", true)]      // scan the substitute
    [InlineData("A/B", "C", false)]     // a part NOT in the list is still rejected
    [InlineData("A / B", "B", true)]    // spaces around the slash
    [InlineData("A /B", "B", true)]     // leading-slash-on-substitute style
    [InlineData("A/B/C", "C", true)]    // multiple substitutes
    [InlineData("A/B/C", "D", false)]
    [InlineData("A/b", "B", true)]      // case-insensitive across the list
    public void Substitute_on_expected_side(string expected, string scanned, bool ok) =>
        Assert.Equal(ok, PartNumber.Matches(expected, scanned));

    // --- slash can appear on either side; match if the sets intersect ---
    [Theory]
    [InlineData("B", "A/B", true)]      // slash on the scanned side
    [InlineData("A/B", "B/C", true)]    // both sides list alternatives, B is common
    [InlineData("A/B", "C/D", false)]   // disjoint sets = mismatch
    public void Substitute_on_either_side(string a, string b, bool ok) =>
        Assert.Equal(ok, PartNumber.Matches(a, b));

    // --- empty / stray-slash segments are ignored, never auto-match ---
    [Theory]
    [InlineData("A/", "A", true)]
    [InlineData("/A", "A", true)]
    [InlineData("A/B", "", false)]      // a blank scan never matches a real substitute list
    public void Stray_slash_segments_ignored(string a, string b, bool ok) =>
        Assert.Equal(ok, PartNumber.Matches(a, b));
}
