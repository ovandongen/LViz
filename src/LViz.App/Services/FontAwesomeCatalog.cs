using System.Reflection;
using System.Text.Json;

namespace LViz.App.Services;

/// <summary>
/// Reflective view onto the Font Awesome catalog embedded inside the
/// <c>Projektanker.Icons.Avalonia.FontAwesome</c> NuGet package. The picker
/// UI in <c>IconPickerDialog</c> renders the resulting list as a searchable
/// grid; the catalog is loaded lazily once per process so the dialog opens
/// without paying the JSON parse cost up front at app start.
///
/// <para>The package ships the full FA Free catalog as the manifest resource
/// <c>Projektanker.Icons.Avalonia.FontAwesome.Assets.icons.json</c>. The
/// upstream schema is roughly:</para>
/// <code>
/// { "coffee": { "styles": ["solid"], "search": { "terms": ["mug","..."] }, ... }, ... }
/// </code>
/// <para>We prepend <c>fa-</c> to each key so the returned <see cref="Entry.Name"/>
/// can be fed directly to <c>&lt;i:Icon Value="…"/&gt;</c>.</para>
/// </summary>
public static class FontAwesomeCatalog
{
    public sealed record Entry(string Name, IReadOnlyList<string> SearchTerms);

    private const string AssemblyName = "Projektanker.Icons.Avalonia.FontAwesome";
    private const string ResourceName = "Projektanker.Icons.Avalonia.FontAwesome.Assets.icons.json";

    private static readonly Lazy<IReadOnlyList<Entry>> _all = new(LoadCatalog);

    /// <summary>The full FA Free catalog, ordered by the upstream JSON.</summary>
    public static IReadOnlyList<Entry> All => _all.Value;

    private static IReadOnlyList<Entry> LoadCatalog()
    {
        var asm = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == AssemblyName)
            ?? Assembly.Load(AssemblyName);

        using var s = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"FA catalog resource '{ResourceName}' not found in '{AssemblyName}'.");
        using var doc = JsonDocument.Parse(s);

        var list = new List<Entry>(capacity: 2048);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var name = "fa-" + prop.Name;
            IReadOnlyList<string> terms = Array.Empty<string>();
            if (prop.Value.TryGetProperty("search", out var search)
                && search.TryGetProperty("terms", out var t)
                && t.ValueKind == JsonValueKind.Array)
            {
                var collected = new List<string>();
                foreach (var term in t.EnumerateArray())
                {
                    if (term.ValueKind == JsonValueKind.String)
                    {
                        var v = term.GetString();
                        if (!string.IsNullOrEmpty(v)) collected.Add(v!);
                    }
                }
                if (collected.Count > 0) terms = collected;
            }
            list.Add(new Entry(name, terms));
        }
        return list;
    }

    /// <summary>
    /// Case-insensitive substring match against <see cref="Entry.Name"/> and
    /// <see cref="Entry.SearchTerms"/>. An empty / whitespace query returns
    /// <see cref="All"/> unchanged.
    /// </summary>
    public static IReadOnlyList<Entry> Filter(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        var needle = query.Trim();
        var result = new List<Entry>();
        foreach (var e in All)
        {
            if (e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(e);
                continue;
            }
            foreach (var term in e.SearchTerms)
            {
                if (term.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(e);
                    break;
                }
            }
        }
        return result;
    }
}
