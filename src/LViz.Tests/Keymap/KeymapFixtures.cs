namespace LViz.Tests.Keymap;

/// <summary>
/// Reads embedded <c>Keymap/Fixtures/*.keymap</c> resources for the
/// real-world and loader test classes.
/// </summary>
internal static class KeymapFixtures
{
    public static string Read(string name)
    {
        var asm = typeof(KeymapFixtures).Assembly;
        var resourceName = $"LViz.Tests.Keymap.Fixtures.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{resourceName}' not found. Available: "
                + string.Join(", ", asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
