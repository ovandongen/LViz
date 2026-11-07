using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Models;

/// <summary>
/// <see cref="KeyBinding.SameAs"/>: behavior + Ordinal params, decorations
/// ignored, null rejected. Record equality compares the params list by
/// reference, so this is the only binding comparison that works.
/// </summary>
public class KeyBindingSameAsTests
{
    [Fact]
    public void SameAs_EqualContents_DifferentListInstances_IsTrue()
    {
        var a = new KeyBinding("&kp", new[] { "LS", "A" });
        var b = new KeyBinding("&kp", new[] { "LS", "A" });
        Assert.True(a.SameAs(b));
    }

    [Fact]
    public void SameAs_NoParams_SameBehavior_IsTrue()
    {
        Assert.True(new KeyBinding("&trans", Array.Empty<string>())
            .SameAs(new KeyBinding("&trans", Array.Empty<string>())));
    }

    [Fact]
    public void SameAs_DifferentBehavior_IsFalse()
    {
        var a = new KeyBinding("&kp", new[] { "A" });
        var b = new KeyBinding("&to", new[] { "A" });
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void SameAs_DifferentParam_IsFalse()
    {
        var a = new KeyBinding("&kp", new[] { "A" });
        var b = new KeyBinding("&kp", new[] { "B" });
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void SameAs_DifferentParamCount_IsFalse()
    {
        var a = new KeyBinding("&kp", new[] { "A" });
        var b = new KeyBinding("&kp", Array.Empty<string>());
        Assert.False(a.SameAs(b));
        Assert.False(b.SameAs(a));
    }

    [Fact]
    public void SameAs_ParamCaseDifference_IsFalse()
    {
        var a = new KeyBinding("&kp", new[] { "a" });
        var b = new KeyBinding("&kp", new[] { "A" });
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void SameAs_BehaviorCaseDifference_IsFalse()
    {
        var a = new KeyBinding("&KP", Array.Empty<string>());
        var b = new KeyBinding("&kp", Array.Empty<string>());
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void SameAs_DifferentDecorations_IsTrue()
    {
        var a = new KeyBinding("&kp", new[] { "A" }) { DecorationLabel = "copy" };
        var b = new KeyBinding("&kp", new[] { "A" });
        Assert.True(a.SameAs(b));
    }

    [Fact]
    public void SameAs_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new KeyBinding("&kp", new[] { "A" }).SameAs(null!));
    }
}
