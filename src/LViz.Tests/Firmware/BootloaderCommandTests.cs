using LViz.Core.Firmware;
using Xunit;

namespace LViz.Tests.Firmware;

/// <summary>
/// The 0xB0 core.system.bootloader report: the CRC-8/SMBUS checksum against the
/// zmk-hid-viz README's worked example, the 32-byte packet layout (LE ref,
/// target byte, HVBOOT magic, trailing zeros), and the manifest's
/// <c>core.system.bootloader/&lt;n&gt;</c> slot-count parsing.
/// </summary>
public class BootloaderCommandTests
{
    // ─── Crc8 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Crc8_ReadmeExampleBytes_Is53()
    {
        // The README's central report with ref 0x0B0B, up to but excluding the CRC byte.
        ReadOnlySpan<byte> data = new byte[] { 0xB0, 0x0B, 0x0B, 0xFF, 0x48, 0x56, 0x42, 0x4F, 0x4F, 0x54 };
        Assert.Equal((byte)0x53, BootloaderCommand.Crc8(data));
    }

    // ─── Build ───────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Central0B0B_MatchesReadmeExample()
    {
        var report = BootloaderCommand.Build(0x0B0B, BootloaderCommand.CentralTarget);

        Assert.Equal(32, report.Length);
        Assert.Equal(
            new byte[] { 0xB0, 0x0B, 0x0B, 0xFF, 0x48, 0x56, 0x42, 0x4F, 0x4F, 0x54, 0x53 },
            report[..11]);
        Assert.All(report[11..], b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void Build_PeripheralSlot0_WritesLeRefTargetAndCrc()
    {
        var report = BootloaderCommand.Build(0x0102, target: 0);

        Assert.Equal(0x02, report[1]);
        Assert.Equal(0x01, report[2]);
        Assert.Equal((byte)0, report[3]);
        Assert.Equal(BootloaderCommand.Crc8(report.AsSpan(0, 10)), report[10]);
    }

    // ─── PeripheralSlots ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(new string[] { "core.layer.state", "core.system.bootloader/1" }, 1)]
    [InlineData(new string[] { "core.system.bootloader/0" }, 0)]
    [InlineData(new string[] { "core.system.reset/1" }, null)]
    [InlineData(new string[] { "core.system.bootloader/x", "core.system.bootloader/2" }, 2)]
    [InlineData(new string[] { }, null)]
    public void PeripheralSlots_ParsesTheFirstUsableCount(string[] capabilityIds, int? expected)
        => Assert.Equal(expected, BootloaderCommand.PeripheralSlots(capabilityIds));
}
