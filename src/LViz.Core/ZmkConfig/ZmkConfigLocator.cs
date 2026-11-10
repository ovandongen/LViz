namespace LViz.Core.ZmkConfig;

/// <summary>
/// Finds the zmk-config root a keymap file belongs to: the folder that
/// holds <c>config/west.yml</c>.
/// </summary>
public static class ZmkConfigLocator
{
    /// <summary>
    /// The first folder holding <c>config/west.yml</c>, starting at the
    /// keymap's own folder and walking up to 3 parents. Null when none of
    /// those folders has one. The keymap file itself need not exist.
    /// </summary>
    public static string? FindRoot(string keymapPath)
    {
        if (string.IsNullOrWhiteSpace(keymapPath))
            return null;

        var folder = Path.GetDirectoryName(Path.GetFullPath(keymapPath));
        for (var checks = 0; folder is not null && checks <= 3; checks++)
        {
            if (File.Exists(Path.Combine(folder, "config", "west.yml")))
                return folder;
            folder = Path.GetDirectoryName(folder);
        }
        return null;
    }
}
