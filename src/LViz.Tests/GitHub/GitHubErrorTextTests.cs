using LViz.App.Services.GitHub;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// <see cref="GitHubErrorText"/>: the translated text every view-model shows
/// for a failed GitHub call. Runs on the real <see cref="LViz.App.Localization.Loc"/>,
/// so it pins that each kind resolves to its own entry in the resx files.
/// </summary>
public sealed class GitHubErrorTextTests
{
    [Fact]
    public void For_EveryKind_GetsDistinctResolvedText()
    {
        var kinds = Enum.GetValues<GitHubErrorKind>();
        var texts = kinds
            .Select(kind => GitHubErrorText.For(new GitHubApiException(kind, "detail")))
            .ToList();

        foreach (var text in texts)
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            // Loc answers "[Key]" for missing entries — nothing may start that way.
            Assert.False(text.StartsWith('['));
        }

        Assert.Equal(kinds.Length, texts.Distinct().Count());
    }
}
