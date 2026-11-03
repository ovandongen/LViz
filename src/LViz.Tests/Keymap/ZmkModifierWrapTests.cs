using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Flat-form modifier wrappers: building <c>["LC","LS","A"]</c> from a
/// keycode + flag set, taking it apart again, and formatter interop.
/// </summary>
public class ZmkModifierWrapTests
{
    [Fact]
    public void Wrap_EmitsOutermostFirst_InFixedOrder()
    {
        var flat = ZmkModifierWrap.Wrap("A", ZmkModifiers.LeftShift | ZmkModifiers.LeftCtrl);
        Assert.Equal(new[] { "LC", "LS", "A" }, flat);
    }

    [Fact]
    public void Wrap_ResultReNestsInFormatter()
    {
        var flat = ZmkModifierWrap.Wrap("A", ZmkModifiers.LeftShift | ZmkModifiers.LeftCtrl);
        var text = ZmkBindingFormatter.Format(
            new KeyBinding("&kp", flat), Array.Empty<KeyValuePair<string, string>>());
        Assert.Equal("&kp LC(LS(A))", text);
    }

    [Fact]
    public void Wrap_NoFlags_IsJustTheKeycode()
    {
        Assert.Equal(new[] { "A" }, ZmkModifierWrap.Wrap("A", ZmkModifiers.None));
    }

    [Fact]
    public void Wrap_BlankKeycode_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ZmkModifierWrap.Wrap("", ZmkModifiers.LeftShift));
    }

    [Fact]
    public void Unwrap_LeadingFunctionsBecomeModifiers()
    {
        var unwrapped = ZmkModifierWrap.Unwrap(new[] { "LS", "LA", "UP_ARROW" });
        Assert.Equal(ZmkModifiers.LeftShift | ZmkModifiers.LeftAlt, unwrapped!.Modifiers);
        Assert.Equal("UP_ARROW", unwrapped.Keycode);
    }

    [Fact]
    public void Unwrap_BareKeycode_IsNoModifiers()
    {
        var unwrapped = ZmkModifierWrap.Unwrap(new[] { "A" });
        Assert.Equal(ZmkModifiers.None, unwrapped!.Modifiers);
        Assert.Equal("A", unwrapped.Keycode);
    }

    [Fact]
    public void Unwrap_RepeatedFunctionSetsFlagAgain()
    {
        var unwrapped = ZmkModifierWrap.Unwrap(new[] { "LS", "LS", "A" });
        Assert.Equal(ZmkModifiers.LeftShift, unwrapped!.Modifiers);
        Assert.Equal("A", unwrapped.Keycode);
    }

    [Fact]
    public void Unwrap_NoSingleTrailingKeycode_IsNull()
    {
        Assert.Null(ZmkModifierWrap.Unwrap(Array.Empty<string>()));
        Assert.Null(ZmkModifierWrap.Unwrap(new[] { "LS" }));
        Assert.Null(ZmkModifierWrap.Unwrap(new[] { "LS", "A", "B" }));
    }

    [Theory]
    [InlineData(ZmkModifiers.LeftCtrl)]
    [InlineData(ZmkModifiers.LeftShift)]
    [InlineData(ZmkModifiers.LeftAlt)]
    [InlineData(ZmkModifiers.LeftGui)]
    [InlineData(ZmkModifiers.RightCtrl)]
    [InlineData(ZmkModifiers.RightShift)]
    [InlineData(ZmkModifiers.RightAlt)]
    [InlineData(ZmkModifiers.RightGui)]
    [InlineData(ZmkModifiers.RightAlt | ZmkModifiers.LeftGui)]
    public void WrapThenUnwrap_RoundTrips(ZmkModifiers modifiers)
    {
        var unwrapped = ZmkModifierWrap.Unwrap(ZmkModifierWrap.Wrap("N1", modifiers))!;
        Assert.Equal(modifiers, unwrapped.Modifiers);
        Assert.Equal("N1", unwrapped.Keycode);
    }

    [Theory]
    [InlineData(ZmkModifiers.LeftCtrl, "LC")]
    [InlineData(ZmkModifiers.LeftShift, "LS")]
    [InlineData(ZmkModifiers.LeftAlt, "LA")]
    [InlineData(ZmkModifiers.LeftGui, "LG")]
    [InlineData(ZmkModifiers.RightCtrl, "RC")]
    [InlineData(ZmkModifiers.RightShift, "RS")]
    [InlineData(ZmkModifiers.RightAlt, "RA")]
    [InlineData(ZmkModifiers.RightGui, "RG")]
    public void FunctionFor_SingleFlag(ZmkModifiers single, string expected)
    {
        Assert.Equal(expected, ZmkModifierWrap.FunctionFor(single));
    }

    [Fact]
    public void FunctionFor_NotASingleFlag_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ZmkModifierWrap.FunctionFor(ZmkModifiers.LeftCtrl | ZmkModifiers.LeftShift));
        Assert.Throws<ArgumentException>(
            () => ZmkModifierWrap.FunctionFor(ZmkModifiers.None));
    }
}
