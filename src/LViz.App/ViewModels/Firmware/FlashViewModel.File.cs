using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.Core.Firmware;

namespace LViz.App.ViewModels;

/// <summary>
/// File mode: flash a local <c>.uf2</c> instead of a GitHub build. A local
/// config opens here straight away (<see cref="StartAsync"/>), a GitHub
/// config switches over through <c>UseFileCommand</c>; either way
/// <see cref="FileHalves"/> is filled once the halves are known and the
/// user picks the file and the half it belongs to.
///
/// <para>
/// The plan is always a single half (file flashes are never recorded in
/// the flash history, so there is no reason to batch them). A central file
/// whose image lacks zmk-hid-viz's manifest text gets a stronger warning
/// the user has to tick through — a warning, never a block.
/// </para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>
    /// Text zmk-hid-viz's manifest puts in the firmware image: a central
    /// built with "Flash from LViz" contains it, one without flashing
    /// support does not.
    /// </summary>
    private const string FlashingSupportMarker = "core.system.bootloader";

    // The picked file's bytes; null while nothing is loaded (or after an
    // invalid pick dropped it). Not observable — FileName is its face.
    private byte[]? _fileBytes;

    // The half list currently shown, to skip a rebuild when file mode is
    // re-entered over the same inspected config.
    private IReadOnlyList<FirmwareHalf>? _fileHalvesSource;

    /// <summary>The picked <c>.uf2</c>'s file name; null while none is loaded.</summary>
    [ObservableProperty]
    private string? _fileName;

    /// <summary>Which half the picked file is for; the central by default.</summary>
    [ObservableProperty]
    private FlashHalfRow? _selectedFileHalf;

    /// <summary>The "Flash it anyway" tick that clears the central's missing-support warning.</summary>
    [ObservableProperty]
    private bool _fileWarningAccepted;

    /// <summary>
    /// The halves to pick a target from, central first. Unlike the Halves
    /// step's <see cref="HalfRows"/> these are never ticked — the choice is
    /// <see cref="SelectedFileHalf"/>.
    /// </summary>
    public ObservableCollection<FlashHalfRow> FileHalves { get; } = [];

    /// <summary>
    /// The note file mode always shows: the file has to come from a config
    /// with LViz's modules and flashing turned on, or live features and
    /// flashing from LViz stop working after this flash.
    /// </summary>
    public string FileNotice => Loc.Instance["Flash_FileNotice"];

    /// <summary>
    /// The stronger warning: the loaded file is for the central and its
    /// image carries no zmk-hid-viz manifest text, so the flashed board
    /// likely can't be put into its bootloader by LViz again. Peripheral
    /// files are not checked — they carry no modules.
    /// </summary>
    public bool ShowFileWarning =>
        _fileBytes is not null
        && SelectedFileHalf?.Half.Role == FirmwareHalfRole.Central
        && !Uf2File.ContainsAscii(_fileBytes, FlashingSupportMarker);

    /// <summary>
    /// Show the open dialog and load the picked <c>.uf2</c>. A cancelled
    /// pick or a failed read keeps the previous state apart from the
    /// message; an invalid file is not kept at all.
    /// </summary>
    [RelayCommand]
    private async Task PickFile()
    {
        string? path = await _desktop.PickUf2FileAsync();
        if (path is null)
        {
            return; // cancelled; keep whatever was loaded
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.Instance.Format("Flash_ErrorRead", e.Message);
            return;
        }

        if (!Uf2File.IsValid(bytes))
        {
            _fileBytes = null;
            FileName = null;
            ErrorMessage = Loc.Instance["Flash_ErrorNotUf2"];
            return;
        }

        _fileBytes = bytes;
        // Reset the tick here too, not only through the FileName hook:
        // re-picking a file with the same name doesn't change FileName,
        // so the hook would stay silent.
        FileWarningAccepted = false;
        FileName = Path.GetFileName(path);
        ErrorMessage = null;
        NotifyFileState();
    }

    partial void OnFileNameChanged(string? value)
    {
        FileWarningAccepted = false;
        NotifyFileState();
    }

    partial void OnSelectedFileHalfChanged(FlashHalfRow? value)
    {
        FileWarningAccepted = false;
        NotifyFileState();
    }

    partial void OnFileWarningAcceptedChanged(bool value) => NotifyFileState();

    /// <summary>One place publishing everything derived from the file state.</summary>
    private void NotifyFileState()
    {
        OnPropertyChanged(nameof(ShowFileWarning));
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Fill the half picker for file mode, central pre-selected. Called
    /// whenever file mode starts (<see cref="OnIsFileModeChanged(bool)"/>);
    /// re-entering file mode over the same inspected config keeps the
    /// current selection.
    /// </summary>
    private void PopulateFileHalves()
    {
        if (ReferenceEquals(_fileHalvesSource, _halves))
        {
            return;
        }

        _fileHalvesSource = _halves;
        FileHalves.Clear();
        foreach (var half in _halves)
        {
            FileHalves.Add(new FlashHalfRow(half, HalfLabel(half, _isUnibody), isChecked: false, hint: null));
        }

        SelectedFileHalf = FileHalves.FirstOrDefault(r => r.Half.Role == FirmwareHalfRole.Central)
            ?? FileHalves.FirstOrDefault();
    }

    /// <summary>File mode's share of <see cref="CanNext"/>: a file, a half, and no unticked warning.</summary>
    private bool CanNextFile() =>
        _fileBytes is not null
        && SelectedFileHalf is not null
        && (!ShowFileWarning || FileWarningAccepted);

    /// <summary>Plan the selected half with this file, no SHA (file flashes skip the history), and go.</summary>
    private void NextFile()
    {
        if (SelectedFileHalf is not { } row || _fileBytes is not { } bytes)
        {
            return;
        }

        _plan.Clear();
        _plan.Add((row.Half, bytes));
        _planSha = null;
        ErrorMessage = null;
        Step = FlashStep.Flash; // no Halves step: the half was picked up front
    }
}
