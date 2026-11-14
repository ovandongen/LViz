using System.Buffers.Binary;
using System.Text;
using LViz.Core.Firmware;
using Xunit;

namespace LViz.Tests.Firmware;

/// <summary>
/// The UF2 block format: validation over every block's three magic words
/// and payload size, the main-flash image rebuilt in target-address order
/// without the <c>not-main-flash</c> blocks, and the marker search that
/// reads the image as one byte run — so a string split across a block
/// boundary is still found.
/// </summary>
public sealed class Uf2FileTests
{
    private const uint Base = 0x1000;

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Concat(params byte[][] blocks)
    {
        var all = new byte[blocks.Sum(b => b.Length)];
        var at = 0;
        foreach (var block in blocks)
        {
            block.CopyTo(all.AsSpan(at));
            at += block.Length;
        }

        return all;
    }

    /// <summary>A correct 512-byte block carrying <paramref name="payload"/> at <paramref name="addr"/>.</summary>
    private static byte[] Block(uint addr, byte[] payload, uint flags = 0)
    {
        var block = new byte[Uf2File.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), Uf2File.Magic0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), Uf2File.Magic1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), addr);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), 0); // block number
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), 1); // block count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), (uint)payload.Length); // file size
        payload.CopyTo(block.AsSpan(32));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(508), Uf2File.MagicEnd);
        return block;
    }

    // --- IsValid ---

    [Fact]
    public void IsValid_TwoBlocks_True() =>
        Assert.True(Uf2File.IsValid(Concat(Block(Base, Ascii("AA")), Block(Base + 2, Ascii("BB")))));

    [Fact]
    public void IsValid_FullBlock_True_TruncatedTo511_False()
    {
        var bytes = Concat(Block(Base, Ascii("AA")));
        Assert.True(Uf2File.IsValid(bytes.AsSpan(0, 512)));
        Assert.False(Uf2File.IsValid(bytes.AsSpan(0, 511)));
    }

    [Fact]
    public void IsValid_BadEndMagic_False()
    {
        var block = Block(Base, Ascii("AA"));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(508), 0xDEADBEEF);
        Assert.False(Uf2File.IsValid(block));
    }

    [Fact]
    public void IsValid_PayloadSize477_False()
    {
        var block = Block(Base, Ascii("AA"));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)Uf2File.MaxPayload + 1);
        Assert.False(Uf2File.IsValid(block));
    }

    [Fact]
    public void IsValid_Empty_False() => Assert.False(Uf2File.IsValid(Array.Empty<byte>()));

    // --- MainFlashImage ---

    [Fact]
    public void MainFlashImage_OrdersByTargetAddress_NotFileOrder()
    {
        var bytes = Concat(Block(0x2000, Ascii("BB")), Block(0x1000, Ascii("AA")));
        Assert.Equal("AABB", Encoding.ASCII.GetString(Uf2File.MainFlashImage(bytes)));
    }

    [Fact]
    public void MainFlashImage_LeavesOutNotMainFlashBlocks()
    {
        var bytes = Concat(
            Block(Base, Ascii("AA")),
            Block(Base + 2, Ascii("BB"), Uf2File.FlagNotMainFlash));
        Assert.Equal("AA", Encoding.ASCII.GetString(Uf2File.MainFlashImage(bytes)));
    }

    [Fact]
    public void MainFlashImage_InvalidBytes_Throws() =>
        Assert.Throws<InvalidDataException>(() => Uf2File.MainFlashImage(new byte[] { 1, 2, 3 }));

    // --- ContainsAscii ---

    [Fact]
    public void ContainsAscii_MarkerSplitAcrossBlocks_True()
    {
        // "core.sys" ends block 1, "tem.bootloader" starts block 2; the
        // contiguous addresses make the image one run again.
        var bytes = Concat(Block(0x1000, Ascii("core.sys")), Block(0x1008, Ascii("tem.bootloader")));
        Assert.True(Uf2File.ContainsAscii(bytes, "core.system.bootloader"));
    }

    [Fact]
    public void ContainsAscii_OtherText_False()
    {
        var bytes = Concat(Block(Base, Ascii("core.system.nothing")));
        Assert.False(Uf2File.ContainsAscii(bytes, "core.system.bootloader"));
    }

    [Fact]
    public void ContainsAscii_InvalidBytes_False() =>
        Assert.False(Uf2File.ContainsAscii(new byte[] { 1, 2, 3 }, "AA"));
}
