using LViz.App.Localization;
using LViz.App.ViewModels;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The reusable choice dialog's view model: the two canned prompts (save
/// conflict, quit with unsaved edits) and the dialog-result → conflict-answer
/// mapping. Expected text is always built through <see cref="Loc"/> so the
/// assertions hold in whichever culture the tests run in; the
/// no-bracket-prefix test is what proves every key actually resolves
/// (<see cref="Loc"/> renders a miss as <c>[Key]</c>).
/// </summary>
public class ChoiceDialogViewModelTests
{
    [Fact]
    public void Conflict_Local_UsesLocalFormatAndThreeOptions()
    {
        var vm = ChoiceDialogViewModel.Conflict(new KeymapConflictRequest(false, "x.keymap"));

        Assert.Equal(Loc.Instance["Edit_ConflictTitle"], vm.Title);
        Assert.Equal(Loc.Instance.Format("Edit_ConflictLocalFormat", "x.keymap"), vm.Message);
        Assert.Equal(
            new[] { "cancel", "reload", "overwrite" },
            vm.Options.Select(option => option.Id).ToArray());
        Assert.Equal(
            new[]
            {
                Loc.Instance["Edit_Cancel"],
                Loc.Instance["Edit_ConflictReload"],
                Loc.Instance["Edit_ConflictOverwrite"],
            },
            vm.Options.Select(option => option.Label).ToArray());
        // Only "overwrite" — the action that resolves the conflict — is accent.
        Assert.Equal(
            new[] { false, false, true },
            vm.Options.Select(option => option.IsAccent).ToArray());
    }

    [Fact]
    public void Conflict_GitHub_UsesGitHubFormat()
    {
        var vm = ChoiceDialogViewModel.Conflict(new KeymapConflictRequest(true, "o/r (main)"));

        Assert.Equal(Loc.Instance["Edit_ConflictTitle"], vm.Title);
        Assert.Equal(
            Loc.Instance.Format("Edit_ConflictGitHubFormat", "o/r (main)"),
            vm.Message);
        Assert.Equal(
            new[] { "cancel", "reload", "overwrite" },
            vm.Options.Select(option => option.Id).ToArray());
    }

    [Fact]
    public void QuitWithUnsavedEdits_NamesCount_AndOffersCancelAndQuit()
    {
        var vm = ChoiceDialogViewModel.QuitWithUnsavedEdits(3);

        Assert.Equal(Loc.Instance["Edit_QuitTitle"], vm.Title);
        Assert.Equal(Loc.Instance.Format("Edit_QuitMessageFormat", 3), vm.Message);
        Assert.Equal(
            new[] { "cancel", "quit" },
            vm.Options.Select(option => option.Id).ToArray());
        Assert.Equal(
            new[] { Loc.Instance["Edit_Cancel"], Loc.Instance["Edit_QuitConfirm"] },
            vm.Options.Select(option => option.Label).ToArray());
        Assert.Equal(
            new[] { false, true },
            vm.Options.Select(option => option.IsAccent).ToArray());
    }

    [Theory]
    [InlineData("reload", KeymapSaveConflictChoice.Reload)]
    [InlineData("overwrite", KeymapSaveConflictChoice.Overwrite)]
    [InlineData("cancel", KeymapSaveConflictChoice.Cancel)]
    [InlineData(null, KeymapSaveConflictChoice.Cancel)]
    [InlineData("x", KeymapSaveConflictChoice.Cancel)]
    public void ToConflictChoice_MapsEveryId_UnknownAndNullToCancel(
        string? id, KeymapSaveConflictChoice expected) =>
        Assert.Equal(expected, ChoiceDialogViewModel.ToConflictChoice(id));

    [Fact]
    public void EveryPrompt_TitleMessageAndLabel_ResolvesLocally()
    {
        var dialogs = new[]
        {
            ChoiceDialogViewModel.Conflict(new KeymapConflictRequest(false, "x.keymap")),
            ChoiceDialogViewModel.Conflict(new KeymapConflictRequest(true, "o/r (main)")),
            ChoiceDialogViewModel.QuitWithUnsavedEdits(3),
        };

        var texts = dialogs
            .SelectMany(dialog => new[] { dialog.Title, dialog.Message }
                .Concat(dialog.Options.Select(option => option.Label)))
            .ToList();

        Assert.NotEmpty(texts);
        Assert.All(texts, text => Assert.False(
            text.StartsWith("[", StringComparison.Ordinal),
            $"Unresolved Loc key: {text}"));
    }
}
