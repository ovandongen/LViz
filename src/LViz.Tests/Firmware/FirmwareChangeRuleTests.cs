using LViz.Core.Firmware;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.Firmware;

/// <summary>
/// The pure "does the peripheral half need a flash" rule — change lists are
/// hand-built records, no HTTP involved.
/// </summary>
public class FirmwareChangeRuleTests
{
    [Theory]
    [InlineData("config/cosmos82.keymap")]
    [InlineData("CONFIG/X.KEYMAP")]
    [InlineData("README.md")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData("config/cosmos82.layout.json")]
    [InlineData(".gitignore")]
    [InlineData("LICENSE")]
    [InlineData("docs/LICENSE.txt")]
    public void IsCentralOnly_CentralOnlyPaths_True(string path) =>
        Assert.True(FirmwareChangeRule.IsCentralOnly(path));

    [Theory]
    [InlineData("config/west.yml")]
    [InlineData("build.yaml")]
    [InlineData("config/cosmos82.conf")]
    [InlineData("boards/shields/cosmos82/cosmos82_left.overlay")]
    [InlineData("boards/shields/cosmos82/Kconfig.defconfig")]
    [InlineData("config/cosmos82.dtsi")]
    [InlineData("keymap.json")]
    public void IsCentralOnly_PeripheralPaths_False(string path) =>
        Assert.False(FirmwareChangeRule.IsCentralOnly(path));

    [Fact]
    public void PeripheralNeedsFlash_OnlyKeymapAndDocs_False() =>
        Assert.False(FirmwareChangeRule.PeripheralNeedsFlash(
            new GitHubCompareResult(["config/cosmos82.keymap", "README.md"], false)));

    [Fact]
    public void PeripheralNeedsFlash_KeymapPlusWestYml_True() =>
        Assert.True(FirmwareChangeRule.PeripheralNeedsFlash(
            new GitHubCompareResult(["config/cosmos82.keymap", "config/west.yml"], false)));

    [Fact]
    public void PeripheralNeedsFlash_NoChanges_False() =>
        Assert.False(FirmwareChangeRule.PeripheralNeedsFlash(new GitHubCompareResult([], false)));

    [Fact]
    public void PeripheralNeedsFlash_NoChangesButTruncated_True() =>
        Assert.True(FirmwareChangeRule.PeripheralNeedsFlash(new GitHubCompareResult([], true)));
}
