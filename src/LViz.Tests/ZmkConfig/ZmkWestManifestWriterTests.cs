using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// <see cref="ZmkWestManifestWriter.AddModules"/> run against the real
/// zmk-config fixtures and a few inline manifests: the writer may only add
/// the ovandongen remote and the two module projects (or switch an existing
/// zmk-raw-hid over to it), and must leave every other byte as it was.
/// </summary>
public class ZmkWestManifestWriterTests
{
    private static string West(string name) => ZmkConfigFixtures.Read(name, "config/west.yml");

    private static string[] Lines(string text) => text.Split('\n');

    [Fact]
    public void Cosmos82_AddsRemoteAndBothProjects_ChangingNothingElse()
    {
        var text = West("cosmos82");

        var result = ZmkWestManifestWriter.AddModules(text, "main", "main");

        // The remote sits between the badjeff item and its trailing comments.
        Assert.Contains(
            "      url-base: https://github.com/badjeff\n" +
            "    - name: ovandongen\n" +
            "      url-base: https://github.com/ovandongen\n" +
            "    # Additional modules",
            result);
        // Both projects follow the last item, self/path stay below them.
        Assert.Contains(
            "      revision: zmk-0.3\n" +
            "    - name: zmk-raw-hid\n      remote: ovandongen\n      revision: main\n" +
            "    - name: zmk-hid-viz\n      remote: ovandongen\n      revision: main\n" +
            "  self:\n" +
            "    path: config",
            result);
        // Every original line is still present, in order.
        Assert.True(IsSubsequence(Lines(text), Lines(result)));
    }

    [Fact]
    public void Slabwing_AddsRemoteAfterZmkfirmwareAndProjectsAfterZmk()
    {
        var result = ZmkWestManifestWriter.AddModules(West("slabwing"), "main", "main");

        Assert.Contains(
            "    - name: zmkfirmware\n" +
            "      url-base: https://github.com/zmkfirmware\n" +
            "    - name: ovandongen\n" +
            "      url-base: https://github.com/ovandongen\n" +
            "  projects:",
            result);
        Assert.Contains(
            "      import: app/west.yml\n" +
            "    - name: zmk-raw-hid\n      remote: ovandongen\n      revision: main\n" +
            "    - name: zmk-hid-viz\n      remote: ovandongen\n      revision: main\n" +
            "  self:",
            result);

        var parsed = ZmkWestManifestText.Parse(result);
        Assert.Equal(3, parsed.Projects.Count);
        Assert.Equal(["zmk", "zmk-raw-hid", "zmk-hid-viz"], parsed.Projects.Select(p => p.Values["name"]));
    }

    [Fact]
    public void Zzeneg_SwitchesRawHidToTheExistingOvandongenRemote()
    {
        var text = West("zzeneg");

        var result = ZmkWestManifestWriter.AddModules(text, "main", "main");

        var lines = Lines(result);
        Assert.Equal(Lines(text).Length, lines.Length); // switch in place, nothing appended
        var rawHid = IndexOfLine(lines, "    - name: zmk-raw-hid");
        Assert.Equal("      remote: ovandongen", lines[rawHid + 1]);
        Assert.Equal("      revision: main", lines[rawHid + 2]);
        // The existing zmk-hid-viz is never touched.
        var hidViz = IndexOfLine(lines, "    - name: zmk-hid-viz");
        Assert.Equal("      remote: ovandongen", lines[hidViz + 1]);
        Assert.Equal("      revision: main", lines[hidViz + 2]);
        // The remote is reused, not added.
        var parsed = ZmkWestManifestText.Parse(result);
        Assert.Equal(["zmkfirmware", "zzeneg", "ovandongen"], parsed.Remotes.Select(r => r.Values["name"]));
    }

    [Fact]
    public void Zzeneg_ReplacesTheRevisionInPlace()
    {
        var result = ZmkWestManifestWriter.AddModules(West("zzeneg"), "v0.3-lviz", "main");

        var lines = Lines(result);
        var rawHid = IndexOfLine(lines, "    - name: zmk-raw-hid");
        Assert.Equal("      revision: v0.3-lviz", lines[rawHid + 2]);
    }

    [Fact]
    public void Glove80_AlreadySetUp_ChangesNothing()
    {
        var text = West("glove80");

        Assert.Equal(text, ZmkWestManifestWriter.AddModules(text, "main", "main"));
    }

    [Fact]
    public void Go60_AlreadySetUp_ChangesNothing_WithoutTrailingNewline()
    {
        var text = West("go60");

        var result = ZmkWestManifestWriter.AddModules(text, "main", "main");

        Assert.Equal(text, result);
        Assert.False(result.EndsWith('\n')); // and it stays without one
    }

    [Fact]
    public void RemoteNameTakenForElse_AddsTheSuffixedRemote()
    {
        var result = ZmkWestManifestWriter.AddModules(
            "manifest:\n" +
            "  remotes:\n" +
            "    - name: ovandongen\n" +
            "      url-base: https://github.com/someone-else\n" +
            "  projects:\n" +
            "    - name: zmk\n" +
            "      url: https://github.com/zmkfirmware/zmk\n" +
            "      revision: main\n",
            "main", "main");

        Assert.Contains(
            "      url-base: https://github.com/someone-else\n" +
            "    - name: ovandongen-lviz\n" +
            "      url-base: https://github.com/ovandongen\n" +
            "  projects:",
            result);
        Assert.Contains(
            "    - name: zmk-raw-hid\n      remote: ovandongen-lviz\n      revision: main\n" +
            "    - name: zmk-hid-viz\n      remote: ovandongen-lviz\n      revision: main",
            result);
    }

    [Fact]
    public void NoRemotesSection_InsertsTheRemoteSectionBeforeProjects()
    {
        var result = ZmkWestManifestWriter.AddModules(
            "manifest:\n" +
            "  projects:\n" +
            "    - name: zmk\n" +
            "      url: https://github.com/zmkfirmware/zmk\n" +
            "      revision: main\n",
            "main", "main");

        Assert.Contains(
            "manifest:\n" +
            "  remotes:\n" +
            "    - name: ovandongen\n" +
            "      url-base: https://github.com/ovandongen\n" +
            "  projects:",
            result);

        var parsed = ZmkWestManifestText.Parse(result);
        Assert.Single(parsed.Remotes);
        Assert.NotNull(parsed.Project("zmk-raw-hid"));
        Assert.NotNull(parsed.Project("zmk-hid-viz"));
    }

    [Fact]
    public void CrlfFile_ResultEndsEveryLineWithCrlf()
    {
        var crlf = West("cosmos82").Replace("\n", "\r\n");

        var result = ZmkWestManifestWriter.AddModules(crlf, "main", "main");

        Assert.Contains("    - name: ovandongen\r\n      url-base: https://github.com/ovandongen\r\n", result);
        Assert.EndsWith("\r\n", result);
        Assert.All(Lines(result)[..^1], line => Assert.EndsWith("\r", line));
    }

    [Fact]
    public void WithoutProjectsSection_ThrowsTheFormatException()
    {
        var ex = Assert.Throws<ZmkConfigFormatException>(
            () => ZmkWestManifestWriter.AddModules("manifest:\n  self:\n    path: config\n", "main", "main"));

        Assert.Equal("config/west.yml", ex.File);
    }

    [Fact]
    public void UnparseableManifest_PropagatesTheFormatException()
    {
        Assert.Throws<ZmkConfigFormatException>(
            () => ZmkWestManifestWriter.AddModules("manifest:\n  projects:\n    hello\n", "main", "main"));
    }

    /// <summary>Every line of <paramref name="needle"/> appears in <paramref name="haystack"/>, in order.</summary>
    private static bool IsSubsequence(string[] needle, string[] haystack)
    {
        var i = 0;
        foreach (var line in haystack)
        {
            if (i < needle.Length && line == needle[i])
                i++;
        }
        return i == needle.Length;
    }

    private static int IndexOfLine(string[] lines, string line)
    {
        var index = Array.IndexOf(lines, line);
        Assert.True(index >= 0, $"line not found: {line}");
        return index;
    }
}
