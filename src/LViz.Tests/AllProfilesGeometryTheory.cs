using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Universal geometry invariants applied across every registered profile.
/// Adding a board to <see cref="KeyboardProfileRegistry"/> automatically
/// runs these — no per-board test plumbing needed for the basics.
/// </summary>
public class AllProfilesGeometryTheory
{
    public static IEnumerable<object[]> AllProfiles =>
        KeyboardProfileRegistry.All.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Profile_HasDeclaredKeyCount(IKeyboardProfile p) =>
        Assert.Equal(p.KeyCount, p.Keys.Count);

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Profile_KeysHaveFiniteNonZeroSizes(IKeyboardProfile p) =>
        ProfileGeometryAssertions.AssertFiniteNonZeroSizes(p);

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Profile_KeysFitInsideCanvas(IKeyboardProfile p) =>
        ProfileGeometryAssertions.AssertKeysFitCanvas(p);
}
