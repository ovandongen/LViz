using LViz.Core.Layout;

namespace LViz.Tests;

/// <summary>
/// Test-only second profile. The per-profile settings engines key everything
/// by <see cref="IKeyboardProfile.Id"/>; tests that exercise profile-switching
/// behaviour need two distinct Ids. With Corne as the only real profile, this
/// stand-in supplies a different one.
/// </summary>
internal sealed class FakeKeyboardProfile : IKeyboardProfile
{
    public FakeKeyboardProfile(string id = "FakeBoard", int keyCount = 42)
    {
        Id = id;
        KeyCount = keyCount;
        Keys = Enumerable.Range(0, keyCount)
            .Select(i => new KeyPosition(i, i * 60, 0))
            .ToList();
    }

    public string Id { get; }
    public string DisplayName => Id;
    public int KeyCount { get; }
    public double CanvasWidth => 600;
    public double CanvasHeight => 200;
    public IReadOnlyList<KeyPosition> Keys { get; }

    public bool MatchesHidDevice(int vendorId, int productId, string? productName) => false;
    public bool NameMatches(string? productName) => false;
}
