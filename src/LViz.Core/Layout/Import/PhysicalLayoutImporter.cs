using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>
/// Importer 1 of 3: ZMK <c>zmk,physical-layout</c> dtsi nodes into
/// <see cref="ImportedLayout"/>s. One result per node in file order, named
/// by its <c>display-name</c> when present, else by its node name. A file
/// without any physical-layout node imports as an empty list; malformed
/// nodes throw <see cref="LayoutImportException"/>.
/// </summary>
public static class PhysicalLayoutImporter
{
    public static IReadOnlyList<ImportedLayout> Import(string dtsiText)
    {
        var layouts = DtsiPhysicalLayoutParser.ParseAll(dtsiText);
        var imported = new List<ImportedLayout>(layouts.Count);
        foreach (var layout in layouts)
        {
            imported.Add(new ImportedLayout(
                Name: layout.DisplayName ?? layout.NodeName,
                Source: LayoutSource.PhysicalLayout,
                Keys: layout.Keys,
                Warnings: []));
        }
        return imported;
    }
}
