using System.Text.Json;
using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>
/// Importer 2 of 3: the <c>info.json</c> board files of nickcoutsos'
/// keymap-editor (and QMK's own <c>info.json</c>). One
/// <see cref="ImportedLayout"/> per property of <c>layouts</c>, in document
/// order, keys in array order (= keymap binding order). Positions/sizes are
/// in u and get converted to ZMK's centi-u / centi-degrees; <c>rx</c>/<c>ry</c>
/// are the absolute rotation origin in u, like ZMK's physical attrs. Unknown
/// properties (<c>row</c>, <c>col</c>, <c>label</c>, <c>matrix</c>, …) are
/// ignored. Pure function; malformed input throws
/// <see cref="LayoutImportException"/>.
/// </summary>
public static class InfoJsonImporter
{
    public static IReadOnlyList<ImportedLayout> Import(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // JsonException.LineNumber is 0-based; humans count from 1.
            string where = ex.LineNumber is long line ? $" at line {line + 1}" : "";
            throw new LayoutImportException($"info.json is not valid JSON{where}. {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("layouts", out var layouts)
                || layouts.ValueKind != JsonValueKind.Object)
            {
                throw new LayoutImportException("info.json has no 'layouts' object");
            }

            string? baseName = GetString(root, "name") ?? GetString(root, "keyboard_name");
            var properties = layouts.EnumerateObject().ToList();

            var imported = new List<ImportedLayout>(properties.Count);
            foreach (var property in properties)
            {
                imported.Add(ImportLayout(
                    property.Name, baseName, properties.Count, property.Value));
            }
            return imported;
        }
    }

    private static ImportedLayout ImportLayout(
        string layoutName, string? baseName, int layoutCount, JsonElement layout)
    {
        if (layout.ValueKind != JsonValueKind.Object
            || !layout.TryGetProperty("layout", out var keyArray)
            || keyArray.ValueKind != JsonValueKind.Array
            || keyArray.GetArrayLength() == 0)
        {
            throw new LayoutImportException(
                $"info.json layout '{layoutName}' has no 'layout' array with keys");
        }

        var keys = new List<DtsiKeyPhysical>();
        var warnings = new List<LayoutImportWarning>();
        int index = 0;
        foreach (var key in keyArray.EnumerateArray())
        {
            keys.Add(ImportKey(layoutName, index++, key, warnings));
        }

        string name = layoutCount == 1
            ? baseName ?? layoutName
            : baseName is null ? layoutName : $"{baseName} ({layoutName})";

        return new ImportedLayout(name, LayoutSource.InfoJson, keys, warnings);
    }

    private static DtsiKeyPhysical ImportKey(
        string layoutName, int index, JsonElement key, List<LayoutImportWarning> warnings)
    {
        double x = GetNumber(key, "x")
            ?? throw new LayoutImportException(KeyError(layoutName, index, "has no 'x'"));
        double y = GetNumber(key, "y")
            ?? throw new LayoutImportException(KeyError(layoutName, index, "has no 'y'"));
        double w = GetNumber(key, "w") ?? 1;
        double h = GetNumber(key, "h") ?? 1;
        double r = GetNumber(key, "r") ?? 0;

        if (w <= 0 || h <= 0)
        {
            throw new LayoutImportException(KeyError(
                layoutName, index, $"has a non-positive size (w: {w}, h: {h})"));
        }

        double? rx = GetNumber(key, "rx");
        double? ry = GetNumber(key, "ry");
        if (r != 0 && (rx is null || ry is null))
        {
            // No explicit origin: the rotation happens about the key centre.
            warnings.Add(new LayoutImportWarning(LayoutWarningCode.RotationWithoutPivot, index));
        }

        return new DtsiKeyPhysical(
            Width: Centi(w),
            Height: Centi(h),
            X: Centi(x),
            Y: Centi(y),
            Rotation: Centi(r),
            // Missing origin: key centre when the key rotates, 0 when it does not.
            Rx: rx is not null ? Centi(rx.Value) : r != 0 ? Centi(x + w / 2) : 0,
            Ry: ry is not null ? Centi(ry.Value) : r != 0 ? Centi(y + h / 2) : 0);
    }

    private static string KeyError(string layoutName, int index, string problem) =>
        $"info.json layout '{layoutName}' key {index} {problem}";

    /// <summary>u (or degrees) to centi-u (or centi-degrees), away-from-zero
    /// rounding. A "-0.0" from the file lands on int 0 — ints have no signed
    /// zero, so no separate normalisation is needed.</summary>
    private static int Centi(double value) =>
        (int)Math.Round(value * 100, MidpointRounding.AwayFromZero);

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Missing, null, non-numeric, or a non-object owner (a key that
    /// is not an object at all) all read as absent — the caller's required
    /// check then refuses with layout name and key index.</summary>
    private static double? GetNumber(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double parsed)
            ? parsed
            : null;
}
