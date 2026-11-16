using System.Buffers.Binary;
using System.Text;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.ViewModels;
using LViz.Core.Firmware;
using LViz.Core.GitHub;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The flash window's file mode over the real cosmos82 fixture with a local
/// context: it opens without any build lookup, lists both halves with the
/// central pre-selected, validates the picked <c>.uf2</c>, and warns on a
/// central file whose image lacks zmk-hid-viz's manifest text until the
/// user ticks "Flash it anyway". Next plans one half and skips Halves.
/// </summary>
public sealed class FlashViewModelFileTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lviz-flashfile-{Guid.NewGuid():N}");
    private readonly FakeBuilds _builds = new();
    private readonly FirmwareFlashHistory _history;
    private readonly FakeDesktopInteraction _desktop = new();

    public FlashViewModelFileTests()
    {
        Directory.CreateDirectory(_tempDir);
        _history = new FirmwareFlashHistory(Path.Combine(_tempDir, "flash-history.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string T(string key) => Loc.Instance[key];

    [Fact]
    public async Task Start_LocalContext_OpensFileModeWithoutBuildLookup()
    {
        var vm = LocalVm();

        await vm.StartAsync();

        Assert.True(vm.IsFileMode);
        Assert.Empty(_builds.FindCalls);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsSourceStep);
    }

    [Fact]
    public async Task FileHalves_ListsBothHalves_WithTheCentralSelected()
    {
        var vm = LocalVm();
        await vm.StartAsync();

        Assert.Equal(2, vm.FileHalves.Count);
        var central = vm.FileHalves[0];
        var peripheral = vm.FileHalves[1];
        Assert.Equal(FirmwareHalfRole.Central, central.Half.Role);
        Assert.Equal(T("Flash_HalfWithCentral").Replace("{0}", T("Flash_HalfRight")), central.Label);
        Assert.DoesNotContain("[Key", central.Label); // the label resolved, not just matched itself
        Assert.Equal(T("Flash_HalfLeft"), peripheral.Label);
        Assert.Same(central, vm.SelectedFileHalf);
    }

    [Fact]
    public void FileNotice_Resolves()
    {
        var vm = LocalVm();

        Assert.Equal(T("Flash_FileNotice"), vm.FileNotice);
        Assert.DoesNotContain("[Key", vm.FileNotice);
    }

    [Fact]
    public async Task PickFile_NotUf2_ReportsAndKeepsNoFile()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = Write("junk.uf2", "definitely not a UF2 file"u8.ToArray());

        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.Equal(T("Flash_ErrorNotUf2"), vm.ErrorMessage);
        Assert.Null(vm.FileName);
        Assert.False(vm.ShowFileWarning);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task PickFile_Unreadable_ReportsTheReadError_AndKeepsNoFile()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = _tempDir; // reading a directory throws (IOException/UnauthorizedAccessException)

        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("[Key", vm.ErrorMessage); // Flash_ErrorRead resolved with the message
        Assert.Null(vm.FileName);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task PickFile_UsesTheDesktopFilePicker()
    {
        // The picked file comes from the desktop interaction the VM was
        // built with — no window hook involved.
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = WithMarker();

        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.Equal("support.uf2", vm.FileName);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelledPick_ChangesNothing()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = WithMarker();
        await vm.PickFileCommand.ExecuteAsync(null);
        _desktop.Uf2Path = null;

        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.Equal("support.uf2", vm.FileName);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task CentralWithoutMarker_Warns_UntilTicked_AndANewFileResetsTheTick()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = NoMarker("one.uf2");
        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.Equal("one.uf2", vm.FileName);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.ShowFileWarning);
        Assert.False(vm.NextCommand.CanExecute(null));

        vm.FileWarningAccepted = true;
        Assert.True(vm.NextCommand.CanExecute(null));

        _desktop.Uf2Path = NoMarker("two.uf2");
        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.False(vm.FileWarningAccepted); // a new file is a new warning to tick
        Assert.True(vm.ShowFileWarning);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task PeripheralWithoutMarker_NoWarning_AndTheTickResetsWithTheHalf()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = NoMarker();
        await vm.PickFileCommand.ExecuteAsync(null);
        vm.FileWarningAccepted = true; // accepted while the central was selected

        vm.SelectedFileHalf = vm.FileHalves[1];

        Assert.False(vm.FileWarningAccepted); // switching the half resets it
        Assert.False(vm.ShowFileWarning); // peripheral files are not checked
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task CentralWithMarker_NoWarning()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = WithMarker();
        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.Equal(FirmwareHalfRole.Central, vm.SelectedFileHalf!.Half.Role);
        Assert.False(vm.ShowFileWarning);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task Next_FileMode_PlansThePickedHalf_AndSkipsHalves()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        _desktop.Uf2Path = WithMarker();
        await vm.PickFileCommand.ExecuteAsync(null);
        Assert.True(vm.IsSourceStep);

        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsFlashStep);
        Assert.False(vm.IsHalvesStep);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.NextCommand.CanExecute(null)); // nothing left to advance on the Flash step
    }

    // --- fixtures and fakes ---

    /// <summary>One valid 512-byte UF2 block carrying the ASCII payload (as in FlashViewModelTests).</summary>
    private static byte[] Uf2Block(string payload)
    {
        var bytes = Encoding.ASCII.GetBytes(payload);
        var block = new byte[Uf2File.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), Uf2File.Magic0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), Uf2File.Magic1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), 1); // block count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), (uint)bytes.Length);
        bytes.CopyTo(block.AsSpan(32));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(508), Uf2File.MagicEnd);
        return block;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string NoMarker(string name = "plain.uf2") =>
        Write(name, Uf2Block("ZMK firmware image without any manifest text"));

    private string WithMarker() =>
        Write("support.uf2", Uf2Block("zmk behaviors here core.system.bootloader and more"));

    private FlashViewModel LocalVm()
    {
        var keyboard = new FakeKeyboard();
        var root = ZmkConfigFixtures.Root("cosmos82");
        return new FlashViewModel(
            new FlashContext(root, root, null, null),
            _builds,
            _history,
            keyboard,
            new FlashRun(keyboard, new Uf2DriveWatcher(new NoDrives())),
            _desktop);
    }

    private sealed class NoDrives : IUf2DriveSource
    {
        public IReadOnlyList<Uf2Drive> List() => [];
    }

    private sealed class FakeKeyboard : IKeyboardFlashAccess
    {
        public bool IsConnected => false;

        public KeyboardBus Bus => KeyboardBus.None;

        public int? BootloaderPeripheralSlots => null;

        public Task<ControlResult> EnterBootloaderAsync(byte targetHalf, CancellationToken ct = default) =>
            Task.FromResult(new ControlResult(ControlOutcome.Failed, "keyboard not connected"));

        public Task<string?> QueryConfigIdAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>Only the lookup is observable here: file mode must not touch the builds service at all.</summary>
    private sealed class FakeBuilds : IFirmwareBuildService
    {
        public readonly List<(string? Sha, string? Branch)> FindCalls = [];

        public string? Login => "build-user";

        public bool IsSignedIn => true;

        public bool IsLoginPersisted => true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task EnsureRestoredAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("a local config has no builds login to restore");

        public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FirmwareBuild> FindBuildAsync(
            string owner, string repo, string? headSha, string? branch, CancellationToken ct = default)
        {
            FindCalls.Add((headSha, branch));
            throw new NotSupportedException("file mode must never look a build up");
        }

        public Task<FirmwareDownloadResult> DownloadAsync(
            string owner, string repo, string sha, long artifactId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PeripheralAdvice> AdvisePeripheralAsync(
            string owner, string repo, string? lastFlashedSha, string buildSha, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
