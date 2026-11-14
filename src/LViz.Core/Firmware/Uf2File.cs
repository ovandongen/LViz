using System.Buffers.Binary;
using System.Text;

namespace LViz.Core.Firmware;

/// <summary>
/// Read-only view over the UF2 file format: a run of 512-byte blocks, each
/// carrying a piece of the firmware image, all integers little-endian
/// (see <see href="https://github.com/microsoft/uf2">microsoft/uf2</see>).
/// Used to check a file really is UF2 before flashing it, and to look for a
/// text marker inside the reconstructed image: zmk-hid-viz's manifest strings
/// sit in the image, so a central built with flashing support contains
/// <c>core.system.bootloader</c> — its absence is a warning, never a block.
/// </summary>
public static class Uf2File
{
    /// <summary>The size of one UF2 block in bytes; the whole file is a multiple of it.</summary>
    public const int BlockSize = 512;

    /// <summary>The most payload bytes a block can carry.</summary>
    public const int MaxPayload = 476;

    /// <summary>First magic word, at offset 0.</summary>
    public const uint Magic0 = 0x0A324655;

    /// <summary>Second magic word, at offset 4.</summary>
    public const uint Magic1 = 0x9E5D5157;

    /// <summary>Trailing magic word, at offset 508.</summary>
    public const uint MagicEnd = 0x0AB16F30;

    /// <summary>Block flag (offset 8): this payload does not belong to main flash.</summary>
    public const uint FlagNotMainFlash = 0x1;

    private const int Magic1Offset = 4;
    private const int FlagsOffset = 8;
    private const int TargetAddressOffset = 12;
    private const int PayloadSizeOffset = 16;
    private const int DataOffset = 32;
    private const int MagicEndOffset = BlockSize - 4;

    /// <summary>
    /// Whether the bytes are a non-empty whole number of blocks in which
    /// every block carries all three magic words and a payload size that
    /// fits. Anything a caller would flash must pass this first.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length % BlockSize != 0)
            return false;

        for (var start = 0; start < bytes.Length; start += BlockSize)
        {
            var block = bytes.Slice(start, BlockSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(block) != Magic0)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(block[Magic1Offset..]) != Magic1)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(block[MagicEndOffset..]) != MagicEnd)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(block[PayloadSizeOffset..]) > MaxPayload)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The concatenated payloads of all blocks that belong to main flash,
    /// ordered by target address (blocks at the same address keep their file
    /// order) — the image the marker search reads. Gaps between addresses are
    /// left as gaps: nothing is filled in.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes fail <see cref="IsValid"/>.</exception>
    public static byte[] MainFlashImage(ReadOnlySpan<byte> bytes)
    {
        if (!IsValid(bytes))
            throw new InvalidDataException("the bytes are not a valid UF2 file");

        // Addresses and sizes are read out up front: a lambda may not
        // capture the ref-like `bytes` parameter.
        var blocks = new List<(uint Address, int Start, int Size)>();
        for (var start = 0; start < bytes.Length; start += BlockSize)
        {
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + FlagsOffset));
            if ((flags & FlagNotMainFlash) != 0)
                continue;
            blocks.Add((
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + TargetAddressOffset)),
                start,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + PayloadSizeOffset))));
        }

        // Enumerable.OrderBy is a stable sort: equal addresses keep file order.
        var ordered = blocks.OrderBy(b => b.Address).ToList();

        var image = new byte[ordered.Sum(b => b.Size)];
        var at = 0;
        foreach (var block in ordered)
        {
            bytes.Slice(block.Start + DataOffset, block.Size).CopyTo(image.AsSpan(at));
            at += block.Size;
        }

        return image;
    }

    /// <summary>
    /// Whether the main-flash image contains the ASCII text — the marker
    /// check for flashing support. An invalid file reads as "no" instead of
    /// throwing: a caller that already validated is calling
    /// <see cref="MainFlashImage"/> directly.
    /// </summary>
    public static bool ContainsAscii(ReadOnlySpan<byte> bytes, string text)
    {
        if (!IsValid(bytes))
            return false;
        return MainFlashImage(bytes).AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;
    }
}
