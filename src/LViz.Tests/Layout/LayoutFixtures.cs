namespace LViz.Tests.Layout;

/// <summary>
/// Reads embedded <c>Layout/Fixtures/*</c> resources (real-world board files)
/// for the layout-importer tests. Same pattern as <c>KeymapFixtures</c>.
/// </summary>
internal static class LayoutFixtures
{
    public static string Read(string fileName)
    {
        var asm = typeof(LayoutFixtures).Assembly;
        var resourceName = $"LViz.Tests.Layout.Fixtures.{fileName}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{resourceName}' not found. Available: "
                + string.Join(", ", asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
