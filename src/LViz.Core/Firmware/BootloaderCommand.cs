using System.Buffers.Binary;
using System.Globalization;
using ZmkHidProtocol.Protocol;

namespace LViz.Core.Firmware;

/// <summary>
/// The <c>0xB0 core.system.bootloader</c> report for zmk-hid-viz firmware built
/// with <c>CONFIG_HID_VIZ_SYSTEM</c>: it reboots the central, or a peripheral
/// via the split link, into its UF2 bootloader so a new firmware file can be
/// dropped on the drive that appears. Confirm-flagged — the device answers with
/// a 0xF7 ack echoing the ref; for a peripheral <c>ok</c> only means "queued on
/// the split link", and a malformed report gets no reply at all.
/// Wire layout (zmk-hid-viz README, "System: bootloader / reset"): byte 0
/// <see cref="Opcode"/>, 1–2 ref (uint16 LE), 3 target
/// (<see cref="CentralTarget"/> or a peripheral slot 0..n-1), 4–9 ASCII magic
/// <c>HVBOOT</c>, 10 a <see cref="Crc8"/> over bytes 0–9, 11–31 zero.
/// </summary>
public static class BootloaderCommand
{
    /// <summary>Report opcode (byte 0).</summary>
    public const byte Opcode = 0xB0;

    /// <summary>Target byte (byte 3) addressing the central itself; peripherals use slots 0..n-1.</summary>
    public const byte CentralTarget = 0xFF;

    /// <summary>
    /// Manifest capability id prefix: firmware advertises the command as
    /// <c>core.system.bootloader/&lt;n&gt;</c> with <c>n</c> the number of
    /// peripheral slots (0 on a unibody).
    /// </summary>
    public const string CapabilityPrefix = "core.system.bootloader/";

    /// <summary>CRC-8/SMBUS polynomial (x^8 + x^2 + x + 1).</summary>
    private const byte Poly = 0x07;

    private const int MagicOffset = 4;
    private const int CrcOffset = 10; // magic "HVBOOT" is six bytes

    /// <summary>
    /// CRC-8/SMBUS over <paramref name="data"/>: polynomial 0x07, init 0x00,
    /// no reflection, no final XOR.
    /// </summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ Poly) : (byte)(crc << 1);
        }
        return crc;
    }

    /// <summary>
    /// One 32-byte bootloader report as laid out in the class summary: the
    /// <paramref name="reference"/> (echoed back in the 0xF7 ack) and
    /// <paramref name="target"/> behind the opcode, the magic, and the CRC
    /// over everything before it.
    /// </summary>
    public static byte[] Build(ushort reference, byte target)
    {
        var report = new byte[HidConstants.ReportSize];
        report[0] = Opcode;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(1, 2), reference);
        report[3] = target;
        "HVBOOT"u8.CopyTo(report.AsSpan(MagicOffset));
        report[CrcOffset] = Crc8(report.AsSpan(0, CrcOffset));
        return report;
    }

    /// <summary>
    /// How many peripheral bootloader slots a manifest advertises: the number
    /// behind the first id starting with <see cref="CapabilityPrefix"/> (ordinal)
    /// whose rest parses as a non-negative invariant int; an id whose rest does
    /// not parse is skipped. Null when no usable id is present — the firmware
    /// has no system commands or <c>CONFIG_HID_VIZ_SYSTEM</c> is off. Feed it the
    /// <c>Id</c> of every entry of the device manifest's capability list.
    /// </summary>
    public static int? PeripheralSlots(IEnumerable<string> capabilityIds)
    {
        foreach (var id in capabilityIds)
        {
            if (!id.StartsWith(CapabilityPrefix, StringComparison.Ordinal))
                continue;
            if (int.TryParse(id.AsSpan(CapabilityPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slots)
                && slots >= 0)
                return slots;
        }
        return null;
    }
}
