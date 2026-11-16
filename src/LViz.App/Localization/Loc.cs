using System.ComponentModel;
using System.Globalization;
using LViz.App.Resources;

namespace LViz.App.Localization;

/// <summary>
/// Lightweight localization singleton for AXAML binding.
/// Usage in AXAML: Text="{Binding [Key_Name], Source={StaticResource Loc}}"
/// Supports runtime language switching via SetCulture().
/// </summary>
public class Loc : INotifyPropertyChanged
{
    /// <summary>Shared culture across all instances (AXAML resource creates its own instance).</summary>
    private static CultureInfo? _culture;

    /// <summary>All live instances, so SetCulture can notify the AXAML-created one too.</summary>
    private static readonly List<WeakReference<Loc>> _instances = [];

    public static Loc Instance { get; } = new();

    /// <summary>Raised after culture changes.</summary>
    public static event Action? CultureChanged;

    public Loc()
    {
        _instances.Add(new WeakReference<Loc>(this));
    }

    public string this[string key] =>
        Strings.ResourceManager.GetString(key, _culture) ?? $"[{key}]";

    /// <summary>Switches the UI language at runtime.</summary>
    public void SetCulture(string cultureCode)
    {
        _culture = string.IsNullOrEmpty(cultureCode) || cultureCode == "en"
            ? CultureInfo.InvariantCulture
            : new CultureInfo(cultureCode);

        _instances.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weakRef in _instances)
        {
            if (weakRef.TryGetTarget(out var loc))
                loc.PropertyChanged?.Invoke(loc, new PropertyChangedEventArgs("Item[]"));
        }

        CultureChanged?.Invoke();
    }

    /// <summary>Gets a localized format string and applies arguments.</summary>
    public string Format(string key, params object[] args)
    {
        var template = this[key];
        return string.Format(template, args);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
