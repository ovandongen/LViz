using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// Gate test for the batch 024 fixtures: every folder reaches the test output
/// with the files the inspector cards rely on.
/// </summary>
public class ZmkConfigFixturesTests
{
    [Theory]
    [InlineData("cosmos82", new[] { "config/west.yml", "build.yaml", "config/cosmos82.conf", "boards/shields/cosmos82/Kconfig.defconfig" })]
    [InlineData("slabwing", new[] { "config/west.yml", "build.yaml", "config/slabwing.conf", "boards/shields/slabwing/Kconfig.defconfig" })]
    [InlineData("glove80", new[] { "config/west.yml", "build.yaml", "config/glove80.conf" })]
    [InlineData("go60", new[] { "config/west.yml", "build.yaml", "config/go60.conf" })]
    [InlineData("zzeneg", new[] { "config/west.yml", "build.yaml", "config/corne.conf" })]
    [InlineData("dongle", new[] { "config/west.yml", "build.yaml", "config/totem.conf" })]
    public void Fixture_HasItsFiles(string name, string[] files)
    {
        foreach (var file in files)
            Assert.False(string.IsNullOrWhiteSpace(ZmkConfigFixtures.Read(name, file)), $"{name}/{file}");
    }

    [Fact]
    public void Go60_KeepsItsMissingTrailingNewline()
    {
        Assert.False(ZmkConfigFixtures.Read("go60", "config/west.yml").EndsWith('\n'));
        Assert.False(ZmkConfigFixtures.Read("go60", "build.yaml").EndsWith('\n'));
    }

    [Fact]
    public void CopyToTemp_CopiesEveryFile()
    {
        var temp = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            Assert.True(File.Exists(Path.Combine(temp, "boards", "shields", "cosmos82", "Kconfig.defconfig")));
            Assert.True(File.Exists(Path.Combine(temp, "config", "west.yml")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
