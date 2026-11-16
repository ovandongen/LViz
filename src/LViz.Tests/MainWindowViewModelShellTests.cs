using CommunityToolkit.Mvvm.Input;
using Xunit;
using LViz.App.ViewModels;

namespace LViz.Tests;

/// <summary>The view-model's side of <c>IMainWindowShell</c>: commands reach
/// the shell, and answers from it are applied by the view-model.</summary>
public sealed class MainWindowViewModelShellTests
{
    private static (MainWindowViewModel Vm, FakeMainWindowShell Shell) Build()
    {
        var shell = new FakeMainWindowShell();
        return (TestMainVm.Create(new InMemorySettingsService(), shell: shell), shell);
    }

    [Fact]
    public void QuitAndShowCommands_ReachTheShell()
    {
        var (vm, shell) = Build();

        vm.QuitCommand.Execute(null);
        vm.ShowCommand.Execute(null);
        vm.OpenSettingsCommand.Execute(null);

        Assert.Equal(1, shell.QuitCount);
        Assert.Equal(1, shell.ShowWindowCount);
        Assert.Equal(1, shell.OpenSettingsCount);
    }

    [Fact]
    public async Task OpenShortcutsCommand_AwaitsTheShellDialog()
    {
        var (vm, shell) = Build();

        await vm.OpenShortcutsCommand.ExecuteAsync(null);

        Assert.Equal(1, shell.ShortcutsCount);
    }

    [Fact]
    public async Task CopyDiagnosticsCommand_Copied_SetsStatus()
    {
        var (vm, shell) = Build();

        await ((IAsyncRelayCommand)vm.CopyDiagnosticsCommand).ExecuteAsync(null);

        Assert.Single(shell.CopiedTexts);
        Assert.Equal(LViz.App.Localization.Loc.Instance["Status_DiagnosticsCopied"], vm.StatusMessage);
    }

    [Fact]
    public async Task CopyDiagnosticsCommand_NoClipboard_LeavesStatusAlone()
    {
        var (vm, shell) = Build();
        shell.ClipboardAvailable = false;
        var before = vm.StatusMessage;

        await ((IAsyncRelayCommand)vm.CopyDiagnosticsCommand).ExecuteAsync(null);

        Assert.Equal(before, vm.StatusMessage);
    }

    [Fact]
    public async Task CopyDiagnosticsCommand_ClipboardThrows_ReportsInStatus()
    {
        var (vm, shell) = Build();
        shell.ClipboardFailure = new InvalidOperationException("boom");

        await ((IAsyncRelayCommand)vm.CopyDiagnosticsCommand).ExecuteAsync(null);

        Assert.Equal("Couldn't copy the diagnostics: boom", vm.StatusMessage);
    }

    [Fact]
    public async Task OpenFromGitHubCommand_NoSelection_LoadsNothing()
    {
        var (vm, shell) = Build();

        await vm.OpenFromGitHubCommand.ExecuteAsync(null);

        Assert.Equal(1, shell.GitHubPickerCount);
        Assert.False(vm.HasLayoutLoaded);
    }

    [Fact]
    public void RequestEditCombo_UnknownCombo_NeverOpensTheDialog()
    {
        var (vm, shell) = Build();

        vm.Board.ComboLegend.RequestEdit("0,1");

        Assert.Empty(shell.ComboLabelRequests);
    }

    [Fact]
    public void ActiveLayerIndex_IsMirroredToShownLayer()
    {
        // The device stack's push engines read ShownLayer from their own
        // threads; the view-model keeps it in step with the shown layer.
        var (vm, _) = Build();

        vm.ActiveLayerIndex = 3;

        Assert.Equal(3, vm.ShownLayer.Index);
    }
}
