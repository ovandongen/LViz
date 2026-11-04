using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Guards the two <c>Strings*.resx</c> files against the two ways a UI string
/// silently goes missing: a key present in only one language, and a literal key
/// used in XAML or C# that no resx file defines. <c>Loc</c> renders a missing
/// key as <c>[Key]</c> instead of failing, so without these tests both mistakes
/// only show up as odd text at runtime.
///
/// The resx files and the sources are read from the source tree (the test
/// project embeds nothing from <c>LViz.App</c>), located by walking up from the
/// test assembly until <c>LViz.sln</c>.
/// </summary>
public class LocalizationKeyTests
{
    private const string EnglishFile = "Strings.resx";
    private const string DutchFile = "Strings.nl.resx";

    /// <summary><c>{Binding [Some_Key], Source={StaticResource Loc}}</c> in XAML.</summary>
    private static readonly Regex AxamlKeyPattern = new(
        @"\[([A-Za-z0-9_]+)\]\s*,\s*Source=\{StaticResource Loc\}",
        RegexOptions.CultureInvariant);

    /// <summary><c>Loc.Instance["Key"]</c> and <c>Loc.Instance.Format("Key", …)</c>.</summary>
    private static readonly Regex CodeKeyPattern = new(
        @"Loc\.Instance(?:\[|\.Format\()\s*""([A-Za-z0-9_]+)""",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EnglishAndDutch_HaveTheSameKeys()
    {
        var english = ResxKeys(EnglishFile);
        var dutch = ResxKeys(DutchFile);

        // A wrong path or an unreadable file would make both sets empty and the
        // comparison below trivially true.
        Assert.True(english.Count >= 300, $"Expected at least 300 keys in {EnglishFile}, found {english.Count}.");
        Assert.True(dutch.Count >= 300, $"Expected at least 300 keys in {DutchFile}, found {dutch.Count}.");

        var missingInDutch = Sorted(english.Except(dutch, StringComparer.Ordinal));
        var missingInEnglish = Sorted(dutch.Except(english, StringComparer.Ordinal));

        var problems = new List<string>();
        if (missingInDutch.Count > 0)
            problems.Add($"Missing from {DutchFile}: {string.Join(", ", missingInDutch)}");
        if (missingInEnglish.Count > 0)
            problems.Add($"Missing from {EnglishFile}: {string.Join(", ", missingInEnglish)}");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryLocKeyInAxaml_ExistsInBothResx()
    {
        AssertUsedKeysExist("*.axaml", AxamlKeyPattern, minimumUses: 200);
    }

    [Fact]
    public void EveryLocKeyInCode_ExistsInBothResx()
    {
        AssertUsedKeysExist("*.cs", CodeKeyPattern, minimumUses: 100);
    }

    /// <summary>
    /// Collects every literal key the <c>Loc</c> users in <paramref name="searchPattern"/>
    /// files pass to <paramref name="regex"/>, checks each one against both resx
    /// key sets, and pins a floor on how many uses were found so a bad source
    /// path can't make the check pass vacuously. Keys built at runtime aren't
    /// literal and aren't matched, which is accepted.
    /// </summary>
    private static void AssertUsedKeysExist(string searchPattern, Regex regex, int minimumUses)
    {
        var uses = LocKeyUses(searchPattern, regex);
        Assert.True(
            uses.Count >= minimumUses,
            $"Expected at least {minimumUses} Loc key uses in {searchPattern} files, found {uses.Count}.");

        var english = ResxKeys(EnglishFile);
        var dutch = ResxKeys(DutchFile);

        var missing = uses
            .Where(use => !english.Contains(use.Key) || !dutch.Contains(use.Key))
            .Select(use => $"{use.Key} ({use.File})")
            .Distinct()
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Localization keys used but defined in neither resx file:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, Sorted(missing)));
    }

    /// <summary>Every (key, file name) literal <see cref="Loc"/> use in the matching source files.</summary>
    private static List<(string Key, string File)> LocKeyUses(string searchPattern, Regex regex)
    {
        var uses = new List<(string Key, string File)>();
        foreach (var file in AppSourceFiles(searchPattern))
        {
            foreach (Match match in regex.Matches(File.ReadAllText(file)))
                uses.Add((match.Groups[1].Value, Path.GetFileName(file)));
        }

        return uses;
    }

    private static List<string> Sorted(IEnumerable<string> keys) =>
        keys.OrderBy(key => key, StringComparer.Ordinal).ToList();

    /// <summary>Repository root: the nearest directory above the test assembly holding <c>LViz.sln</c>.</summary>
    private static string RepoRoot()
    {
        var start = AppContext.BaseDirectory;

        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent!)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LViz.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("LViz.sln not found above " + start);
    }

    /// <summary>The <c>name</c> attribute of every top-level <c>data</c> element in a resx file.</summary>
    private static HashSet<string> ResxKeys(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "LViz.App", "Resources", fileName);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in XDocument.Load(path).Root!.Elements("data"))
        {
            var name = (string?)element.Attribute("name");
            if (!string.IsNullOrEmpty(name))
                keys.Add(name);
        }

        return keys;
    }

    /// <summary>Every file under <c>src/LViz.App</c> matching <paramref name="pattern"/>, ignoring build output.</summary>
    private static IEnumerable<string> AppSourceFiles(string pattern)
    {
        var root = Path.Combine(RepoRoot(), "src", "LViz.App");

        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(file => !HasBuildOutputSegment(file));
    }

    /// <summary>True when any directory segment of the repo-relative path is <c>bin</c> or <c>obj</c>.</summary>
    private static bool HasBuildOutputSegment(string path) =>
        Path.GetRelativePath(RepoRoot(), path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1)
            .Any(segment => segment is "bin" or "obj");
}
