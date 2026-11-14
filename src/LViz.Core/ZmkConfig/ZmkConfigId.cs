using ZmkHidProtocol.Protocol;

namespace LViz.Core.ZmkConfig;

/// <summary>
/// Matching the config ID a board reports (<c>0xFA</c>) against the one a
/// config carries (<c>CONFIG_HID_VIZ_CONFIG_ID</c>). zmk-hid-viz sends the ID
/// in one report after the opcode byte, so it cuts anything longer than
/// <see cref="ReplyMaxLength"/> characters: a GUID (36) comes back as its
/// first 31. The manifest carries the full ID, the <c>0xFA</c> reply never does.
/// </summary>
public static class ZmkConfigId
{
    /// <summary>The most characters a <c>0xFA</c> reply carries: one report minus the opcode.</summary>
    public const int ReplyMaxLength = HidConstants.ReportSize - 1;

    /// <summary>
    /// True when <paramref name="reported"/> is <paramref name="expected"/> as
    /// the board sends it: equal, or — for an ID longer than a reply holds —
    /// equal to its first <see cref="ReplyMaxLength"/> characters. Ordinal;
    /// a null or empty side never matches.
    /// </summary>
    public static bool Matches(string? reported, string? expected)
    {
        if (string.IsNullOrEmpty(reported) || string.IsNullOrEmpty(expected))
            return false;

        return string.Equals(reported, expected, StringComparison.Ordinal)
            || (expected.Length > ReplyMaxLength
                && string.Equals(reported, expected[..ReplyMaxLength], StringComparison.Ordinal));
    }
}
