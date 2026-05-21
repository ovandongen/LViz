using LViz.Core.Layout;
using ZmkHidProtocol.Transport;

namespace LViz.Core.Input;

/// <summary>
/// Adapts the app's <see cref="IKeyboardProfile"/> to the library's
/// <see cref="IDeviceMatcher"/>, so the transport layer stays free of
/// MoErgo-specific abstractions. <see cref="MatchesName"/> takes the
/// name-only path because Windows BLE has no VID/PID to compare.
/// </summary>
public sealed class KeyboardProfileMatcher(IKeyboardProfile profile) : IDeviceMatcher
{
    public bool Matches(int vendorId, int productId, string? productName)
        => profile.MatchesHidDevice(vendorId, productId, productName);

    public bool MatchesName(string? productName)
        => profile.NameMatches(productName);
}
