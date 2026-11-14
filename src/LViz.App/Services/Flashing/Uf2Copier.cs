using LViz.Core.Firmware;

namespace LViz.App.Services.Flashing;

/// <summary>
/// Outcome of a copy onto a bootloader drive: <c>(true, null)</c> on
/// success, otherwise a short message for the flash window.
/// </summary>
public sealed record Uf2CopyResult(bool Ok, string? Error);

/// <summary>
/// Writes the firmware onto a UF2 bootloader drive. A UF2 bootloader is a
/// file system in RAM: the half flashes itself as blocks arrive and reboots
/// once it has them all, so the drive dying <em>during or right after</em>
/// the write is the normal happy path, not an error. The file is written
/// with a plain <see cref="FileStream"/> rather than <c>File.Copy</c>,
/// because the metadata and extended attributes <c>File.Copy</c> carries
/// over make some bootloaders (on macOS) choke.
/// </summary>
public static class Uf2Copier
{
    /// <summary>The fixed file name a UF2 bootloader picks up.</summary>
    public const string TargetFileName = "firmware.uf2";

    private const int ChunkSize = 4096;

    /// <summary>
    /// Validates <paramref name="uf2"/> first (nothing is written when it is
    /// not a real UF2 file), then writes it to
    /// <c>&lt;driveRoot&gt;\firmware.uf2</c> in 4 KiB chunks, counting the
    /// bytes handed over, and flushes — hard flush for a real
    /// <see cref="FileStream"/>, plain flush otherwise. An IO error on
    /// open, write, flush or dispose is judged by what it left behind: the
    /// drive gone (<c>INFO_UF2.TXT</c> no longer readable) after every byte
    /// was handed over counts as success — the half rebooted; the drive
    /// gone early is "the drive went away during the copy"; anything else
    /// reports the exception's message. Caller cancellation propagates.
    /// </summary>
    /// <param name="uf2">The firmware image; must pass <see cref="Uf2File.IsValid"/>.</param>
    /// <param name="driveRoot">Mount root of the bootloader drive.</param>
    /// <param name="ct">Cancels the copy.</param>
    /// <param name="openTarget">How to open the target file; tests inject failing streams.</param>
    public static async Task<Uf2CopyResult> CopyAsync(
        ReadOnlyMemory<byte> uf2,
        string driveRoot,
        CancellationToken ct,
        Func<string, Stream>? openTarget = null)
    {
        if (!Uf2File.IsValid(uf2.Span))
            return new Uf2CopyResult(false, "not a UF2 file");

        var targetPath = Path.Combine(driveRoot, TargetFileName);
        Stream? stream = null;
        var written = 0;
        try
        {
            stream = (openTarget ?? DefaultOpen)(targetPath);
            while (written < uf2.Length)
            {
                var chunk = uf2.Slice(written, Math.Min(ChunkSize, uf2.Length - written));
                await stream.WriteAsync(chunk, ct);
                written += chunk.Length;
            }

            if (stream is FileStream file)
                file.Flush(flushToDisk: true);
            else
                await stream.FlushAsync(ct);

            // Dispose inside the try: a bootloader finishes flashing on
            // close and can throw its way out of a rebooting drive.
            stream.Dispose();
            stream = null;
            return new Uf2CopyResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var gone = !File.Exists(Path.Combine(driveRoot, SystemUf2DriveSource.InfoFileName));
            if (!gone)
                return new Uf2CopyResult(false, ex.Message);
            return written == uf2.Length
                ? new Uf2CopyResult(true, null)
                : new Uf2CopyResult(false, "the drive went away during the copy");
        }
        finally
        {
            if (stream is not null)
                DisposeQuietly(stream);
        }
    }

    private static Stream DefaultOpen(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096);

    private static void DisposeQuietly(Stream stream)
    {
        // Cleanup only — the outcome is already decided, and a drive that
        // rebooted mid-write can throw on anything it still has open.
        try
        {
            stream.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
