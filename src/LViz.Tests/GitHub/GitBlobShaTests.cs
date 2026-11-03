using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// Local git blob SHAs, checked against the digests git itself produces for
/// the same content (the empty blob and a one-word file).
/// </summary>
public class GitBlobShaTests
{
    [Fact]
    public void Compute_EmptyContent_IsTheEmptyBlobSha()
    {
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", GitBlobSha.Compute([]));
    }

    [Fact]
    public void Compute_HelloNewline_IsTheKnownBlobSha()
    {
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", GitBlobSha.Compute("hello\n"u8));
    }
}
