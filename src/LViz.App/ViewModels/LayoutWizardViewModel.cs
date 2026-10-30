using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Core.Layout.Import;

namespace LViz.App.ViewModels;

/// <summary>
/// Everything the wizard needs that it can't discover itself: the keymap LViz
/// already has open (used for ranking and labelling when present), the ids the
/// user layouts already occupy (for id de-duplication), the connected ZMK
/// devices no profile matches yet (device-name prefill candidates) and the
/// folder user layouts live in.
/// </summary>
public sealed record LayoutWizardContext(
    LoadedKeymap? LoadedKeymap,
    IReadOnlyList<string> ExistingUserLayoutIds,
    IReadOnlyList<string> ConnectedDeviceNames,
    string LayoutsDirectory);

/// <summary>
/// What the wizard hands back when the user saves: the finished layout file
/// model and the keymap path LViz should load afterwards (null when the
/// already-open keymap stayed in charge).
/// </summary>
public sealed record LayoutWizardResult(UserLayoutFile Layout, string? KeymapPathToLoad);

/// <summary>One scan candidate dressed for the picker list.</summary>
public sealed record LayoutCandidateItem(
    LayoutCandidate Candidate,
    string Title,
    string Detail,
    bool MatchesKeymap);

/// <summary>
/// View model behind the three-step layout wizard window (source → preview →
/// save). The source step scans a zmk-config folder or file, chooses the
/// keymap to rank and label against, ranks the candidates, and pre-fills
/// name, device name and the "also save into the config folder" flag. The
/// preview step renders the selected candidate through <c>BoardView</c> via
/// <see cref="IBoardSurface"/> with the keymap's base-layer labels; save
/// writes the drafted layout through <see cref="UserLayoutStore"/> and closes
/// with a <see cref="LayoutWizardResult"/>.
/// </summary>
public partial class LayoutWizardViewModel : ObservableObject, IBoardSurface
{
    /// <summary>Keyboard id given to keymaps the wizard parses for itself.</summary>
    private const string WizardKeyboardId = "layout-wizard";

    private readonly LayoutWizardContext _context;

    public LayoutWizardViewModel(LayoutWizardContext context)
    {
        _context = context;
        foreach (var name in context.ConnectedDeviceNames)
            ConnectedDeviceNames.Add(name);

        // BoardView needs a layout to bind from before the first candidate is
        // selected; a one-key stand-in keeps the canvas valid until
        // SetProfile re-targets it in the preview.
        BoardLayout = new BoardLayoutViewModel(new UserLayoutProfile(new UserLayoutFile(
            UserLayoutFile.CurrentSchemaVersion,
            "preview",
            "preview",
            null,
            [new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Right)])));
        BoardStyle = new BoardStyleViewModel(AppTheme.PressHighlightDefaultHex);
    }

    /// <summary>Folder picker, wired up by the window. Null → command no-ops.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>File picker, wired up by the window. Null → command no-ops.</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    /// <summary>Raised on cancel (null) and, later, on save (the result).</summary>
    public event Action<LayoutWizardResult?>? CloseRequested;

    // --- steps ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceStep))]
    [NotifyPropertyChangedFor(nameof(IsPreviewStep))]
    [NotifyPropertyChangedFor(nameof(IsSaveStep))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private int _currentStep;

    public bool IsSourceStep => CurrentStep == 0;
    public bool IsPreviewStep => CurrentStep == 1;
    public bool IsSaveStep => CurrentStep == 2;

    // --- source step ---

    public ObservableCollection<LayoutCandidateItem> Candidates { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private LayoutCandidateItem? _selectedCandidate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadedKeymapNotice))]
    private string? _sourcePath;

    /// <summary>
    /// Before the first scan: tells the user the keymap already open in LViz
    /// will be used (the wizard opened from the status-bar link otherwise only
    /// asks for a folder). After a scan <see cref="SourceInfo"/> says the same,
    /// so this goes null. Null when no keymap is loaded.
    /// </summary>
    public string? LoadedKeymapNotice =>
        SourcePath is null && _context.LoadedKeymap is { Layers.Count: > 0 } keymap
            ? Loc.Instance.Format("Wizard_UsingLoadedKeymapFormat", keymap.Layers[0].Bindings.Count)
            : null;

    [ObservableProperty]
    private string? _sourceInfo;

    /// <summary>True while a scan runs in the background; blocks browsing and Next.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isScanning;

    // --- save step fields (prefilled here, used when saving) ---

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    [ObservableProperty]
    private string _hidName = "";

    /// <summary>Connected ZMK devices no profile matches yet — device-name suggestions.</summary>
    public ObservableCollection<string> ConnectedDeviceNames { get; } = [];

    [ObservableProperty]
    private bool _alsoSaveToConfig;

    [ObservableProperty]
    private bool _canSaveToConfig;

    // --- state the preview/save steps read ---

    internal LoadedKeymap? Keymap { get; private set; }
    internal string? KeymapPathToLoad { get; private set; }
    internal ZmkConfigScan? Scan { get; private set; }

    // --- preview step (IBoardSurface for BoardView) ---

    public ObservableCollection<KeyViewModel> LeftKeys { get; } = [];
    public ObservableCollection<KeyViewModel> RightKeys { get; } = [];

    public BoardLayoutViewModel BoardLayout { get; }
    public BoardStyleViewModel BoardStyle { get; }

    // The preview is static — no combo badges, and taps do nothing.
    public bool IsComboOverlayVisible => false;
    public Action<int>? OnKeyTapped => null;
    public Action<int>? OnKeyRightTapped => null;

    /// <summary>
    /// Key-count mismatch between the selected candidate and the keymap, plus
    /// the importer's own warnings for it, newline-joined; null when there is
    /// nothing to flag.
    /// </summary>
    [ObservableProperty]
    private string? _previewWarning;

    // --- save step ---

    /// <summary>Last save failure shown on the save step; cleared on step change.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    // --- commands ---

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFolder()
    {
        if (PickFolder is null)
            return;
        if (await PickFolder() is string path)
            await LoadSourceAsync(path, isFolder: true);
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFile()
    {
        if (PickFile is null)
            return;
        if (await PickFile() is string path)
            await LoadSourceAsync(path, isFolder: false);
    }

    private bool CanBrowse() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() => CurrentStep++;

    private bool CanNext() => CurrentStep switch
    {
        0 => SelectedCandidate is not null && !IsScanning,
        1 => true,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back() => CurrentStep--;

    private bool CanBack() => CurrentStep is 1 or 2;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void UseDeviceName(string? deviceName)
    {
        if (deviceName is not null)
            HidName = deviceName;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var file = UserLayoutDraft.Create(
            SelectedCandidate!.Candidate.Layout, Name, HidName, _context.ExistingUserLayoutIds);
        try
        {
            UserLayoutStore.Save(file, _context.LayoutsDirectory);
            if (AlsoSaveToConfig && CanSaveToConfig && Scan is { } scan)
                UserLayoutStore.Save(file, Path.Combine(scan.Root, "config"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.Instance.Format("Wizard_SaveFailedFormat", ex.Message);
            return;
        }

        CloseRequested?.Invoke(new LayoutWizardResult(file, KeymapPathToLoad));
    }

    private bool CanSave() =>
        CurrentStep == 2 && SelectedCandidate is not null && !string.IsNullOrWhiteSpace(Name);

    partial void OnCurrentStepChanged(int value) => ErrorMessage = null;

    partial void OnSelectedCandidateChanged(LayoutCandidateItem? value) => BuildPreview(value);

    /// <summary>
    /// Re-render the preview step for <paramref name="item"/>: a draft profile
    /// from the candidate's layout (never written to disk — the throwaway id
    /// exists only to satisfy the draft), caps labelled from the keymap's base
    /// layer where it has a binding for the index and from the key index
    /// otherwise, and the mismatch/importer warnings collected into
    /// <see cref="PreviewWarning"/>. Null clears the surface.
    /// </summary>
    private void BuildPreview(LayoutCandidateItem? item)
    {
        LeftKeys.Clear();
        RightKeys.Clear();
        if (item is null)
        {
            PreviewWarning = null;
            return;
        }

        var layout = item.Candidate.Layout;
        var draft = UserLayoutDraft.Create(
            layout, string.IsNullOrWhiteSpace(Name) ? "preview" : Name, null, []);
        var profile = new UserLayoutProfile(draft);
        BoardLayout.SetProfile(profile);

        var keymapCount = KeymapKeyCount;
        for (var i = 0; i < profile.Keys.Count; i++)
        {
            var pos = profile.Keys[i];
            var key = new KeyViewModel(pos);
            if (Keymap is { } keymap && keymapCount is int bindings && i < bindings)
            {
                // Same paint path as MainWindowViewModel.ApplyActiveLayer, pinned to layer 0.
                var r = keymap.Resolve(0, i);
                key.ApplyBinding(
                    r.Binding,
                    activeLayerIndex: 0,
                    targetLayer: r.TargetLayer,
                    targetLayerName: r.TargetLayerName,
                    profileId: profile.Id,
                    holdTap: r.HoldTap,
                    macros: keymap.MacrosByName);
            }
            else
            {
                key.Label = i.ToString();
            }
            (pos.Hand == Hand.Left ? LeftKeys : RightKeys).Add(key);
        }

        var lines = new List<string>();
        if (keymapCount is int count && count != layout.Keys.Count)
        {
            lines.Add(Loc.Instance.Format(
                "Wizard_KeyCountMismatchFormat", layout.Keys.Count, count));
        }
        lines.AddRange(layout.Warnings);
        PreviewWarning = lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// What the background half of <see cref="LoadSourceAsync"/> reads from
    /// disk: the scan, and the keymap the wizard parsed itself (with its path),
    /// or the reason it couldn't.
    /// </summary>
    private sealed record SourceRead(
        ZmkConfigScan Scan, LoadedKeymap? OwnKeymap, string? OwnKeymapPath, string? KeymapError);

    /// <summary>
    /// Scan <paramref name="path"/> (folder or single board file) and rebuild
    /// everything the source step and the prefills show: candidates ranked
    /// against the keymap's key count, the info lines, and name / device name /
    /// save-into-config. The disk work runs on a background thread (a wrongly
    /// picked big folder must not freeze the app); the results are applied
    /// back on the caller's (UI) thread. A single unreadable keymap file
    /// degrades to "no keymap" with a line in <see cref="SourceInfo"/>;
    /// everything else the scanner already handles per-file.
    /// </summary>
    internal async Task LoadSourceAsync(string path, bool isFolder)
    {
        SourcePath = path;
        SourceInfo = Loc.Instance["Wizard_Scanning"];
        IsScanning = true;
        try
        {
            bool needOwnKeymap = _context.LoadedKeymap is null;
            ApplySource(await Task.Run(() => ReadSource(path, isFolder, needOwnKeymap)));
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static SourceRead ReadSource(string path, bool isFolder, bool needOwnKeymap)
    {
        var scan = isFolder ? ZmkConfigScanner.ScanFolder(path) : ZmkConfigScanner.ScanFile(path);
        if (!needOwnKeymap || scan.KeymapPaths.Count == 0)
            return new SourceRead(scan, null, null, null);

        string keymapPath = scan.KeymapPaths[0];
        try
        {
            return new SourceRead(
                scan, new LoadedKeymap(ZmkKeymapLoader.Load(keymapPath, WizardKeyboardId)), keymapPath, null);
        }
        catch (ZmkKeymapParseException e)
        {
            return new SourceRead(scan, null, keymapPath, e.Message);
        }
    }

    private void ApplySource(SourceRead read)
    {
        var scan = read.Scan;
        Scan = scan;

        ApplyKeymap(read, out var keymapNote, out var keymapLine);

        var keyCount = KeymapKeyCount;
        Candidates.Clear();
        foreach (var candidate in ZmkConfigScanner.Rank(scan.Candidates, keyCount))
        {
            Candidates.Add(new LayoutCandidateItem(
                candidate,
                Loc.Instance.Format(
                    "Wizard_CandidateFormat",
                    SourceName(candidate.Layout.Source),
                    candidate.Layout.Name,
                    candidate.Layout.Keys.Count),
                Path.GetRelativePath(scan.Root, candidate.FilePath),
                keyCount is int n && candidate.Layout.Keys.Count == n));
        }
        SelectedCandidate = Candidates.FirstOrDefault();

        var lines = new List<string>();
        if (keymapLine is not null)
            lines.Add(keymapLine);
        if (keymapNote is not null)
            lines.Add(keymapNote);
        if (scan.Problems.Count > 0)
            lines.Add(Loc.Instance.Format("Wizard_ProblemsFormat", scan.Problems.Count));
        if (Candidates.Count == 0)
            lines.Add(Loc.Instance["Wizard_NoCandidates"]);
        SourceInfo = string.Join("\n", lines);

        Name = UserLayoutDraft.SuggestName(scan.KeyboardName, scan.ShieldName, FolderName(scan.Root));
        HidName = SuggestDeviceName(scan);
        CanSaveToConfig = Directory.Exists(Path.Combine(scan.Root, "config"));
        AlsoSaveToConfig = false;
    }

    /// <summary>
    /// Pick the keymap the wizard ranks and (in the preview step) labels with:
    /// the one LViz already has open wins and stays unloaded; otherwise the
    /// first keymap the scan found, which LViz loads after saving. A keymap
    /// that refuses to parse leaves the wizard keymap-less with a note.
    /// Outputs: the SourceInfo line naming the used keymap, and the "couldn't
    /// read" line (mutually exclusive).
    /// </summary>
    private void ApplyKeymap(SourceRead read, out string? unreadableLine, out string? keymapLine)
    {
        unreadableLine = null;
        keymapLine = null;

        if (_context.LoadedKeymap is LoadedKeymap loaded)
        {
            Keymap = loaded;
            KeymapPathToLoad = null;
            keymapLine = Loc.Instance.Format(
                "Wizard_KeymapUsedFormat",
                Loc.Instance["Wizard_KeymapCurrent"],
                KeymapKeyCount ?? 0);
            return;
        }

        Keymap = read.OwnKeymap;
        KeymapPathToLoad = null;
        if (read.OwnKeymapPath is not string path)
            return;

        if (read.KeymapError is string error)
        {
            unreadableLine = Loc.Instance.Format(
                "Wizard_KeymapUnreadableFormat", Path.GetFileName(path), error);
            return;
        }

        KeymapPathToLoad = path;
        keymapLine = Loc.Instance.Format(
            "Wizard_KeymapUsedFormat", Path.GetFileName(path), KeymapKeyCount ?? 0);
    }

    /// <summary>Key count of the keymap's first layer — the ranking signal.</summary>
    private int? KeymapKeyCount =>
        Keymap is { Layers.Count: > 0 } keymap ? keymap.Layers[0].Bindings.Count : null;

    /// <summary>
    /// Device-name prefill: a connected device that mentions the firmware
    /// keyboard name (or that it mentions — either way, case-insensitive) wins,
    /// then the keyboard name itself, then the suggested board name.
    /// </summary>
    private string SuggestDeviceName(ZmkConfigScan scan)
    {
        string? keyboard = scan.KeyboardName;
        if (!string.IsNullOrWhiteSpace(keyboard))
        {
            keyboard = keyboard.Trim();
            foreach (var device in _context.ConnectedDeviceNames)
            {
                if (device.Trim() is { Length: > 0 } name
                    && (name.Contains(keyboard, StringComparison.OrdinalIgnoreCase)
                        || keyboard.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    return name;
            }
            return keyboard;
        }
        return UserLayoutDraft.SuggestName(null, scan.ShieldName, FolderName(scan.Root));
    }

    private static string SourceName(LayoutSource source) => source switch
    {
        LayoutSource.PhysicalLayout => Loc.Instance["Wizard_Source_PhysicalLayout"],
        LayoutSource.InfoJson => Loc.Instance["Wizard_Source_InfoJson"],
        _ => Loc.Instance["Wizard_Source_MatrixTransform"],
    };

    /// <summary>Leaf folder name of an absolute path, separator-safe.</summary>
    private static string FolderName(string path) => new DirectoryInfo(path).Name;
}
