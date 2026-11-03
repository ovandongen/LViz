using System.Security.Cryptography;
using System.Text;

namespace LViz.Core.GitHub;

/// <summary>
/// The SHA git computes for a blob: the SHA-1 of the ASCII header
/// <c>blob {byte-count}\0</c> (byte-count in decimal) followed by the file
/// bytes. GitHub's Contents API demands exactly this value as the <c>sha</c>
/// of the file being replaced in a commit.
///
/// <para>
/// The editor computes the baseline from the exact bytes it loaded, so no
/// text re-encoding sits between what was read and what is hashed.
/// </para>
/// </summary>
public static class GitBlobSha
{
    /// <summary>Lower-case hex blob SHA of <paramref name="content"/>.</summary>
    public static string Compute(ReadOnlySpan<byte> content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hasher.AppendData(header);
        hasher.AppendData(content);
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}
