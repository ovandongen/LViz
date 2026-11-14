using System.Buffers.Binary;
using System.Text;
using LViz.App.Services.Flashing;
using LViz.Core.Firmware;
using Xunit;

namespace LViz.Tests.Flashing;

/// <summary>
/// The copy onto the bootloader drive: validation first (nothing written
/// for junk), a plain write of <c>firmware.uf2</c>, and the reboot rules —
/// a drive that dies after every byte was handed over is success, one that
/// dies early is not. The "drive" is a temp folder carrying an
/// <c>INFO_UF2.TXT</c>; deleting that file simulates the half rebooting.
/// </summary>
public sealed class Uf2CopierTests : IDisposable
{
    private const uint Base = 0x1000;
    private const string InfoFile = "INFO_UF2.TXT";

    private readonly string _drive;
    private readonly byte[] _uf2;

    public Uf2CopierTests()
    {
        _drive = Path.Combine(Path.GetTempPath(), "lviz-uf2-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_drive);
        File.WriteAllText(Path.Combine(_drive, InfoFile),
            "UF2 Bootloader 0.6.0 lib/nrfx (v2.0.0) Board-ID: nRF52840-nicenano");
        _uf2 = Concat(Block(Base, Ascii("AA")), Block(Base + 2, Ascii("BB")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_drive, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string TargetPath => Path.Combine(_drive, "firmware.uf2");

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

    /// <summary>A correct 512-byte block (the <c>Uf2FileTests</c> helper).</summary>
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

    /// <summary>
    /// Takes every byte, then throws <see cref="IOException"/> once on the
    /// first dispose — a bootloader flashing its last blocks on close —
    /// and stays quiet afterwards so cleanup cannot fail a test.
    /// </summary>
    private sealed class RebootingStream : MemoryStream
    {
        private bool _firstDispose = true;

        public int Taken { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Taken += buffer.Length;
            return base.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (_firstDispose)
            {
                _firstDispose = false;
                base.Dispose(disposing);
                throw new IOException("device disconnected");
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Fails the very first write, as a stick pulled mid-copy would.</summary>
    private sealed class WriteThrowingStream : MemoryStream
    {
        private readonly string _message;

        public WriteThrowingStream(string message) => _message = message;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException(_message);
    }

    [Fact]
    public async Task CopyAsync_ValidUf2_WritesFirmwareUf2ByteForByte()
    {
        var result = await Uf2Copier.CopyAsync(_uf2, _drive, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(_uf2, File.ReadAllBytes(TargetPath));
    }

    [Fact]
    public async Task CopyAsync_InvalidBytes_ReportsAndWritesNothing()
    {
        var result = await Uf2Copier.CopyAsync(new byte[] { 1, 2, 3 }, _drive, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("not a UF2 file", result.Error);
        Assert.False(File.Exists(TargetPath));
    }

    [Fact]
    public async Task CopyAsync_DriveRebootsOnDispose_CountsAsSuccess()
    {
        File.Delete(Path.Combine(_drive, InfoFile)); // the half rebooted mid-close
        var stream = new RebootingStream();

        var result = await Uf2Copier.CopyAsync(_uf2, _drive, CancellationToken.None, _ => stream);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(_uf2.Length, stream.Taken);
    }

    [Fact]
    public async Task CopyAsync_WriteFailsWhileDriveIsThere_ReportsTheError()
    {
        var result = await Uf2Copier.CopyAsync(
            _uf2, _drive, CancellationToken.None, _ => new WriteThrowingStream("disk is gone, sort of"));

        Assert.False(result.Ok);
        Assert.Equal("disk is gone, sort of", result.Error);
    }

    [Fact]
    public async Task CopyAsync_WriteFailsAfterDriveWent_ReportsDriveWentAway()
    {
        File.Delete(Path.Combine(_drive, InfoFile));

        var result = await Uf2Copier.CopyAsync(
            _uf2, _drive, CancellationToken.None, _ => new WriteThrowingStream("disk is gone, sort of"));

        Assert.False(result.Ok);
        Assert.Equal("the drive went away during the copy", result.Error);
    }

    [Fact]
    public async Task CopyAsync_Cancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Uf2Copier.CopyAsync(_uf2, _drive, cts.Token));
    }
}
