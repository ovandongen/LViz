using Avalonia.Input;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Catalog;
using Xunit;

namespace LViz.Tests;

public class KeyRecorderTests
{
    [Theory]
    [InlineData(PhysicalKey.A, "A")]
    [InlineData(PhysicalKey.Digit1, "NUMBER_1")]
    [InlineData(PhysicalKey.Digit0, "NUMBER_0")]
    [InlineData(PhysicalKey.Period, "PERIOD")]
    [InlineData(PhysicalKey.Quote, "SINGLE_QUOTE")]
    [InlineData(PhysicalKey.ArrowLeft, "LEFT_ARROW")]
    [InlineData(PhysicalKey.F24, "F24")]
    [InlineData(PhysicalKey.NumPad5, "KP_NUMBER_5")]
    [InlineData(PhysicalKey.MetaLeft, "LEFT_GUI")]
    [InlineData(PhysicalKey.BrowserBack, null)]
    [InlineData(PhysicalKey.None, null)]
    public void ToZmk_MapsKnownKeys_AndIgnoresUnknown(PhysicalKey key, string? expected) =>
        Assert.Equal(expected, KeyRecorderMap.ToZmk(key));

    [Fact]
    public void ToZmk_EveryMappedName_IsCanonicalInTheCatalog()
    {
        var mapped = 0;
        foreach (var key in Enum.GetValues<PhysicalKey>())
        {
            var name = KeyRecorderMap.ToZmk(key);
            if (name is null)
                continue;

            var keycode = ZmkKeycodeCatalog.Find(name);
            Assert.NotNull(keycode);
            Assert.Equal(name, keycode!.Name);
            mapped++;
        }

        Assert.True(mapped > 100, $"only {mapped} physical keys mapped — table regressed");
    }

    [Theory]
    [InlineData(PhysicalKey.ControlLeft, true)]
    [InlineData(PhysicalKey.ShiftLeft, true)]
    [InlineData(PhysicalKey.AltLeft, true)]
    [InlineData(PhysicalKey.MetaLeft, true)]
    [InlineData(PhysicalKey.ControlRight, true)]
    [InlineData(PhysicalKey.ShiftRight, true)]
    [InlineData(PhysicalKey.AltRight, true)]
    [InlineData(PhysicalKey.MetaRight, true)]
    [InlineData(PhysicalKey.A, false)]
    [InlineData(PhysicalKey.CapsLock, false)]
    public void IsModifier_TrueForTheEightModifierKeys(PhysicalKey key, bool expected) =>
        Assert.Equal(expected, KeyRecorderMap.IsModifier(key));

    [Theory]
    [InlineData(PhysicalKey.Digit1, KeyModifiers.Shift, "NUMBER_1", ZmkModifiers.LeftShift)]
    [InlineData(PhysicalKey.C, KeyModifiers.Control | KeyModifiers.Shift, "C",
        ZmkModifiers.LeftCtrl | ZmkModifiers.LeftShift)]
    [InlineData(PhysicalKey.Q, KeyModifiers.Meta, "Q", ZmkModifiers.LeftGui)]
    [InlineData(PhysicalKey.A, KeyModifiers.None, "A", ZmkModifiers.None)]
    public void KeyDown_MappedKey_RecordsKeycodeWithModifierFlags(
        PhysicalKey key, KeyModifiers modifiers, string name, ZmkModifiers zmkModifiers)
    {
        var recorder = new KeyRecorder();

        var result = recorder.KeyDown(key, modifiers);

        Assert.NotNull(result);
        Assert.Equal(name, result!.ZmkName);
        Assert.Equal(zmkModifiers, result.Modifiers);
    }

    [Fact]
    public void Modifier_PressedAndReleasedAlone_RecordsItself()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        var result = recorder.KeyUp(PhysicalKey.ShiftLeft);

        Assert.NotNull(result);
        Assert.Equal("LEFT_SHIFT", result!.ZmkName);
        Assert.Equal(ZmkModifiers.None, result.Modifiers);
        Assert.True(recorder.IsDone);
    }

    [Fact]
    public void ModifierHeld_WhenKeyIsPressed_ModifierUpRecordsNothing()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        var result = recorder.KeyDown(PhysicalKey.Digit1, KeyModifiers.Shift);

        Assert.NotNull(result);
        Assert.Equal("NUMBER_1", result!.ZmkName);
        Assert.Equal(ZmkModifiers.LeftShift, result.Modifiers);
        Assert.Null(recorder.KeyUp(PhysicalKey.ShiftLeft));
    }

    [Fact]
    public void ModifierChord_RecordsNothing()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        Assert.Null(recorder.KeyDown(PhysicalKey.ControlLeft, KeyModifiers.Shift | KeyModifiers.Control));
        Assert.Null(recorder.KeyUp(PhysicalKey.ControlLeft));
        Assert.Null(recorder.KeyUp(PhysicalKey.ShiftLeft));
        Assert.False(recorder.IsDone);
    }

    [Fact]
    public void UnmappedKey_RecordsNothing_ButRecordingGoesOn()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.BrowserBack, KeyModifiers.None));
        Assert.False(recorder.IsDone);

        var result = recorder.KeyDown(PhysicalKey.B, KeyModifiers.None);

        Assert.NotNull(result);
        Assert.Equal("B", result!.ZmkName);
        Assert.Equal(ZmkModifiers.None, result.Modifiers);
    }

    [Fact]
    public void UnmappedKey_Released_SoloModifierRecordsAgain()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.BrowserBack, KeyModifiers.None));
        Assert.Null(recorder.KeyUp(PhysicalKey.BrowserBack));

        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        var result = recorder.KeyUp(PhysicalKey.ShiftLeft);

        Assert.NotNull(result);
        Assert.Equal("LEFT_SHIFT", result!.ZmkName);
        Assert.Equal(ZmkModifiers.None, result.Modifiers);
        Assert.True(recorder.IsDone);
    }

    [Fact]
    public void UnmappedKeyWhileModifierHeld_RecordsNothing_ButUnsticksAfterwards()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        Assert.Null(recorder.KeyDown(PhysicalKey.BrowserBack, KeyModifiers.Shift));
        Assert.Null(recorder.KeyUp(PhysicalKey.BrowserBack));
        Assert.Null(recorder.KeyUp(PhysicalKey.ShiftLeft));
        Assert.False(recorder.IsDone);

        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        var result = recorder.KeyUp(PhysicalKey.ShiftLeft);

        Assert.NotNull(result);
        Assert.Equal("LEFT_SHIFT", result!.ZmkName);
    }

    [Fact]
    public void SoloModifier_WhileUnmappedKeyStillHeld_RecordsNothing()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.BrowserBack, KeyModifiers.None));
        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        Assert.Null(recorder.KeyUp(PhysicalKey.ShiftLeft));
        Assert.False(recorder.IsDone);
    }

    [Fact]
    public void ModifierAutoRepeat_StaysTheSoloCandidate()
    {
        var recorder = new KeyRecorder();

        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        Assert.Null(recorder.KeyDown(PhysicalKey.ShiftLeft, KeyModifiers.Shift));
        var result = recorder.KeyUp(PhysicalKey.ShiftLeft);

        Assert.NotNull(result);
        Assert.Equal("LEFT_SHIFT", result!.ZmkName);
    }

    [Fact]
    public void AfterAResult_TheRecorderIsDone()
    {
        var recorder = new KeyRecorder();

        Assert.NotNull(recorder.KeyDown(PhysicalKey.Period, KeyModifiers.None));
        Assert.True(recorder.IsDone);
        Assert.Null(recorder.KeyDown(PhysicalKey.A, KeyModifiers.None));
        Assert.Null(recorder.KeyUp(PhysicalKey.ShiftLeft));
    }
}
