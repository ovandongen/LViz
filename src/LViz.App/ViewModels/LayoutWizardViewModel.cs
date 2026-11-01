using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using LViz.Core.Persistence;

namespace LViz.App.ViewModels;

/// <summary>
/// Everything the wizard needs that it can't discover itself: the keymap LViz
/// already has open (used for ranking and labelling when present), the ids the
/// user layouts already occupy (for id de-duplication), the connected ZMK
/// devices no profile matches yet (device-name prefill candidates), the
/// folder user layouts live in, and — when a GitHub keymap is open — the
/// cache folder of its repo + branch to start scanning from.
/// </summary>
public sealed record LayoutWizardContext(
    LoadedKeymap? LoadedKeymap,
    IReadOnlyList<string> ExistingUserLayoutIds,
    IReadOnlyList<string> ConnectedDeviceNames,
    string LayoutsDirectory,
    string? GitHubFolder = null);

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
/// View model behind the three-step layout wizard window (source → adjust →
/// save). The source step scans a zmk-config folder or file, chooses the
/// keymap to rank and label against, ranks the candidates, and pre-fills
/// name, device name and the "also save into the config folder" flag — or,
/// when the user has no board files, answers a few "build from questions"
/// inputs that <see cref="GridLayoutGenerator"/> turns into a plain grid.
/// The adjust step hosts a <see cref="LayoutEditorViewModel"/> built from
/// that source (labels from the keymap's base layer, geometry
/// editable); save writes the edited, normalised keys through
/// <see cref="UserLayoutStore"/> — plus a <c>.dtsi</c> export via
/// <see cref="ZmkPhysicalLayoutWriter"/> unless the candidate itself came
/// from a physical-layout dtsi — and closes with a
/// <see cref="LayoutWizardResult"/>.
/// </summary>
public partial class LayoutWizardViewModel : ObservableObject
{
    /// <summary>Keyboard id given to keymaps the wizard parses for itself.</summary>
    private const string WizardKeyboardId = "layout-wizard";

    private readonly LayoutWizardContext _context;

    public LayoutWizardViewModel(LayoutWizardContext context)
    {
        _context = context;
        foreach (var name in context.ConnectedDeviceNames)
            ConnectedDeviceNames.Add(name);
        ApplyGridDefaultFromKeymap();
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

    // --- build from questions (grid source) ---

    /// <summary>True when the layout comes from the questions below instead of
    /// scanned board files; the toggle is always available on step 0.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isGridSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridKeyCount))]
    [NotifyPropertyChangedFor(nameof(GridMatchesKeymap))]
    [NotifyPropertyChangedFor(nameof(GridKeyCountText))]
    private bool _gridSplit = true;

    // The three numbers arrive from steppers or typing and may be
    // out-of-range; they are stored clamped so GridKeyCount never throws.
    // [ObservableProperty] hooks see the value already stored, so clamping
    // on the way in needs a hand-written SetProperty setter.
    private int _gridKeysPerHalf = 18;
    private int _gridColumns = 6;
    private int _gridThumbKeys = 3;

    // The keymap default (ApplyGridDefaultFromKeymap) runs only while the user
    // has changed none of the three counts (the split toggle re-runs it);
    // _applyingGridDefault keeps the default's own write through the public
    // setters from counting as an edit.
    private bool _gridEditedByUser;
    private bool _applyingGridDefault;

    /// <summary>A count the user changed (stepper or typing) wins over the
    /// keymap default for good; our own default write must not count, and a
    /// write that leaves the value as it was (a binding echoing it back) is
    /// not a change at all.</summary>
    private void MarkGridEditedByUser()
    {
        if (!_applyingGridDefault)
            _gridEditedByUser = true;
    }

    /// <summary>Main keys per half without thumb keys (all main keys when
    /// unsplit); the rows follow from this and <see cref="GridColumns"/>.</summary>
    public int GridKeysPerHalf
    {
        get => _gridKeysPerHalf;
        set
        {
            if (SetProperty(ref _gridKeysPerHalf,
                    Math.Clamp(value, GridLayoutGenerator.MinKeysPerHalf, GridLayoutGenerator.MaxKeysPerHalf)))
            {
                MarkGridEditedByUser();
                NotifyGridSummary();
            }
        }
    }

    /// <summary>Columns per half (whole board when unsplit).</summary>
    public int GridColumns
    {
        get => _gridColumns;
        set
        {
            if (SetProperty(ref _gridColumns,
                    Math.Clamp(value, GridLayoutGenerator.MinColumns, GridLayoutGenerator.MaxColumns)))
            {
                MarkGridEditedByUser();
                NotifyGridSummary();
            }
        }
    }

    /// <summary>Thumb keys per half (whole board when unsplit).</summary>
    public int GridThumbKeys
    {
        get => _gridThumbKeys;
        set
        {
            if (SetProperty(ref _gridThumbKeys,
                    Math.Clamp(value, GridLayoutGenerator.MinThumbKeys, GridLayoutGenerator.MaxThumbKeys)))
            {
                MarkGridEditedByUser();
                NotifyGridSummary();
            }
        }
    }

    // The generator's limits, for the view's numeric inputs.
    public int GridMinKeysPerHalf => GridLayoutGenerator.MinKeysPerHalf;
    public int GridMaxKeysPerHalf => GridLayoutGenerator.MaxKeysPerHalf;
    public int GridMinColumns => GridLayoutGenerator.MinColumns;
    public int GridMaxColumns => GridLayoutGenerator.MaxColumns;
    public int GridMinThumbKeys => GridLayoutGenerator.MinThumbKeys;
    public int GridMaxThumbKeys => GridLayoutGenerator.MaxThumbKeys;

    /// <summary>How many keys the current answers would generate.</summary>
    public int GridKeyCount => GridLayoutGenerator.KeyCount(CurrentGridOptions);

    /// <summary>Whether the generated board fits the keymap's base layer.</summary>
    public bool GridMatchesKeymap => KeymapKeyCount == GridKeyCount;

    /// <summary>The key count dressed for the questions panel.</summary>
    public string GridKeyCountText => Loc.Instance.Format("Wizard_GridKeyCountFormat", GridKeyCount);

    private GridLayoutOptions CurrentGridOptions => new(GridSplit, GridKeysPerHalf, GridColumns, GridThumbKeys);

    /// <summary>Re-raises the read-only summary lines after an answer or the
    /// keymap they compare against changed.</summary>
    private void NotifyGridSummary()
    {
        OnPropertyChanged(nameof(GridKeyCount));
        OnPropertyChanged(nameof(GridMatchesKeymap));
        OnPropertyChanged(nameof(GridKeyCountText));
    }

    // Split vs unibody changes what the keymap default should be, so the toggle
    // re-runs it instead of counting as an edit.
    partial void OnGridSplitChanged(bool value) => ApplyGridDefaultFromKeymap();

    /// <summary>
    /// Default keys per half to the keymap the wizard has: a split board fits
    /// half the bindings minus the thumb keys, a unibody all of them minus the
    /// thumbs. Runs only while the user has changed none of the three counts
    /// (their own edits win for good) and nowhere the result would fall below
    /// the generator's floor — there the current value simply stays. Called
    /// whenever <see cref="Keymap"/> gets a new value and when the split
    /// toggles, never by anything else.
    /// </summary>
    private void ApplyGridDefaultFromKeymap()
    {
        if (_gridEditedByUser || KeymapKeyCount is not int keymapKeys)
            return;

        int perHalf = GridSplit
            ? keymapKeys / 2 - GridThumbKeys
            : keymapKeys - GridThumbKeys;
        if (perHalf < GridLayoutGenerator.MinKeysPerHalf)
            return;

        _applyingGridDefault = true;
        try
        {
            // The public setter clamps to the maximum.
            GridKeysPerHalf = perHalf;
            NotifyGridSummary();
        }
        finally
        {
            _applyingGridDefault = false;
        }
    }

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

    /// <summary>The keymap the wizard ranks and labels with: the one LViz
    /// already has open (from the first breath), else the one the last scan
    /// parsed for itself (null when even that failed to read).</summary>
    internal LoadedKeymap? Keymap => _scannedKeymap ?? _context.LoadedKeymap;

    /// <summary>Keymap the last scan parsed; null when it found or read none,
    /// or when the loaded keymap is in charge.</summary>
    private LoadedKeymap? _scannedKeymap;

    internal string? KeymapPathToLoad { get; private set; }
    internal ZmkConfigScan? Scan { get; private set; }

    // --- adjust step (shared layout editor) ---

    /// <summary>
    /// The adjust step's editor for the selected candidate. Built when the
    /// user leaves step 0 (see <see cref="EnsureEditor"/>), kept across
    /// Back/Next until the candidate or the scan changes, and null again when
    /// the candidate is dropped.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private LayoutEditorViewModel? _editor;

    /// <summary>The source the current editor was built from — the selected
    /// <see cref="LayoutCandidateItem"/> or the grid <see cref="GridLayoutOptions"/>
    /// record; the rebuild test is equality against it. Null after a fresh scan.</summary>
    private object? _editorSource;

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

    [RelayCommand]
    private void UseGridSource()
    {
        // A blank Name stays blank — questions fill no name in; Save insists on one.
        IsGridSource = true;
        NextCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void UseFileSource()
    {
        IsGridSource = false;
        NextCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (CurrentStep == 0)
            EnsureEditor();
        CurrentStep++;
    }

    private bool CanNext() => CurrentStep switch
    {
        0 => IsGridSource || (SelectedCandidate is not null && !IsScanning),
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
        // What the editor shows, normalised — not the raw import: any
        // dragging/rotating the user did has to survive into the file.
        var file = UserLayoutDraft.Create(
            Editor!.Keys, Name, HidName, _context.ExistingUserLayoutIds);

        // A saved layout found in the scan is saved back under its own id, so
        // it replaces itself instead of piling up <id>-2 copies.
        if (!IsGridSource && SelectedCandidate!.Candidate.Layout.SavedId is { } savedId)
            file = file with { Id = savedId };

        // A candidate that already IS a physical-layout dtsi ships its own
        // source of truth; exporting a twin would only shadow it. The writer
        // itself never replaces a dtsi LViz did not generate. A generated
        // grid has no board file, so it always exports.
        var exportDtsi = IsGridSource
            || SelectedCandidate!.Candidate.Layout.Source != LayoutSource.PhysicalLayout;

        var directories = new List<string> { _context.LayoutsDirectory };
        if (AlsoSaveToConfig && CanSaveToConfig && Scan is { } scan)
            directories.Add(Path.Combine(scan.Root, "config"));

        // All files or none: a failed write puts back the ones already written.
        FileRollback? rollback = null;
        try
        {
            rollback = FileRollback.Capture(directories.SelectMany(dir => new[]
            {
                UserLayoutStore.PathFor(file.Id, dir),
                ZmkPhysicalLayoutWriter.PathFor(file.Id, dir),
            }));
            foreach (var dir in directories)
            {
                UserLayoutStore.Save(file, dir);
                if (exportDtsi)
                    ZmkPhysicalLayoutWriter.WriteNextTo(file, dir, createIfMissing: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rollback?.Restore();
            ErrorMessage = Loc.Instance.Format("Wizard_SaveFailedFormat", ex.Message);
            return;
        }

        CloseRequested?.Invoke(new LayoutWizardResult(file, KeymapPathToLoad));
    }

    // Grid mode needs no candidate — the editor holds the generated keys.
    private bool CanSave() =>
        CurrentStep == 2 && Editor is not null
        && (IsGridSource || SelectedCandidate is not null)
        && !string.IsNullOrWhiteSpace(Name);

    partial void OnCurrentStepChanged(int value) => ErrorMessage = null;

    partial void OnSelectedCandidateChanged(LayoutCandidateItem? value)
    {
        // Dropping the candidate empties the adjust step; picking a new one
        // builds nothing yet — the editor is created on Next (EnsureEditor).
        if (value is null)
        {
            Editor = null;
            _editorSource = null;
            PreviewWarning = null;
        }
    }

    /// <summary>
    /// Build the adjust step's editor for the chosen source — the generated
    /// grid while <see cref="IsGridSource"/>, <see cref="SelectedCandidate"/>
    /// otherwise: keys drafted from it, caps labelled from the keymap's base
    /// layer where it has a binding for the index and from the key index
    /// otherwise, and a split line where the hands currently sit. Called by
    /// <see cref="Next"/> when leaving step 0. When the source equals the one
    /// the current editor was already built from the editor (and with it any
    /// edits the user made) is kept untouched; only a different source
    /// rebuilds it and recomputes <see cref="PreviewWarning"/>. The grid
    /// source is the answers record, so the same answers keep the editor and
    /// any changed answer rebuilds it.
    /// </summary>
    private void EnsureEditor()
    {
        if (IsGridSource)
        {
            var options = CurrentGridOptions;
            if (Equals(options, _editorSource))
                return;

            var generated = GridLayoutGenerator.Generate(options);
            var generatedKeys = UserLayoutDraft.KeysFrom(generated);
            Editor = new LayoutEditorViewModel(
                generatedKeys,
                EditorLabels.For(Keymap, generatedKeys.Count, WizardKeyboardId),
                UserLayoutGeometry.InitialSplitX(generatedKeys),
                isSplit: options.Split);
            _editorSource = options;

            // A generated grid carries no importer warnings; only the count check.
            PreviewWarning = KeymapKeyCount is int keymapCount && keymapCount != generatedKeys.Count
                ? Loc.Instance.Format("Wizard_KeyCountMismatchFormat", generatedKeys.Count, keymapCount)
                : null;
            return;
        }

        if (SelectedCandidate is not { } candidate || Equals(candidate, _editorSource))
            return;

        var layout = candidate.Candidate.Layout;
        var keys = UserLayoutDraft.KeysFrom(layout);
        Editor = new LayoutEditorViewModel(
            keys,
            EditorLabels.For(Keymap, keys.Count, WizardKeyboardId),
            UserLayoutGeometry.InitialSplitX(keys));
        _editorSource = candidate;

        var lines = new List<string>();
        if (KeymapKeyCount is int count && count != layout.Keys.Count)
            lines.Add(Loc.Instance.Format("Wizard_KeyCountMismatchFormat", layout.Keys.Count, count));
        lines.AddRange(layout.Warnings.Select(FormatWarning));
        PreviewWarning = lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Localized text for one importer warning code.</summary>
    internal static string FormatWarning(LayoutImportWarning warning) =>
        warning.Code switch
        {
            LayoutWarningCode.MatrixTransformRough =>
                Loc.Instance["Wizard_Warning_MatrixTransformRough"],
            LayoutWarningCode.MatrixTransformGrid =>
                Loc.Instance["Wizard_Warning_MatrixTransformGrid"],
            _ => Loc.Instance.Format(
                "Wizard_Warning_RotationWithoutPivotFormat", warning.KeyIndex ?? 0),
        };

    /// <summary>
    /// What the background half of <see cref="LoadSourceAsync"/> reads from
    /// disk: the scan, and the keymap the wizard parsed itself (with its path),
    /// or the reason it couldn't.
    /// </summary>
    private sealed record SourceRead(
        ZmkConfigScan Scan, LoadedKeymap? OwnKeymap, string? OwnKeymapPath, string? KeymapError);

    /// <summary>
    /// Open-time kick from the window wiring: with a GitHub keymap open there
    /// is no local zmk-config folder to browse to, so scan that repo's cache
    /// folder straight away. A no-op when no GitHub keymap is open or the
    /// cache vanished; the constructor deliberately doesn't call it, so tests
    /// stay synchronous.
    /// </summary>
    public Task StartAsync() =>
        _context.GitHubFolder is string folder && Directory.Exists(folder)
            ? LoadSourceAsync(folder, isFolder: true)
            : Task.CompletedTask;

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

        // A keymap just arrived: default the grid answers to it unless the
        // user has already touched them.
        ApplyGridDefaultFromKeymap();

        // The keymap the grid summary compares against just changed.
        NotifyGridSummary();

        var keyCount = KeymapKeyCount;
        // A new scan means new candidate objects: the editor must rebuild on
        // the next Next even if the same file ends up selected again.
        _editorSource = null;
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

        // A saved layout on top keeps its own name, since saving replaces it.
        Name = SelectedCandidate?.Candidate.Layout is { SavedId: not null } saved
            ? saved.Name
            : UserLayoutDraft.SuggestName(scan.KeyboardName, scan.ShieldName, FolderName(scan.Root));
        HidName = SuggestDeviceName(scan);
        CanSaveToConfig = !IsGitHubSource(scan.Root) && Directory.Exists(Path.Combine(scan.Root, "config"));
        AlsoSaveToConfig = false;
    }

    /// <summary>
    /// True when the scanned root is the open GitHub repo's cache folder:
    /// writing a config folder back into the cache would be lost on the next
    /// download (and pushing to GitHub comes with the editor batch), so the
    /// "also save to config" option is not offered there.
    /// </summary>
    private bool IsGitHubSource(string root)
    {
        if (_context.GitHubFolder is not string folder)
            return false;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    /// <summary>
    /// Pick the keymap the wizard ranks and (in the adjust step) labels with:
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

        if (_context.LoadedKeymap is not null)
        {
            _scannedKeymap = null;
            KeymapPathToLoad = null;
            keymapLine = Loc.Instance.Format(
                "Wizard_KeymapUsedFormat",
                Loc.Instance["Wizard_KeymapCurrent"],
                KeymapKeyCount ?? 0);
            return;
        }

        _scannedKeymap = read.OwnKeymap;
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
        LayoutSource.UserLayout => Loc.Instance["Wizard_Source_UserLayout"],
        LayoutSource.PhysicalLayout => Loc.Instance["Wizard_Source_PhysicalLayout"],
        LayoutSource.InfoJson => Loc.Instance["Wizard_Source_InfoJson"],
        _ => Loc.Instance["Wizard_Source_MatrixTransform"],
    };

    /// <summary>Leaf folder name of an absolute path, separator-safe.</summary>
    private static string FolderName(string path) => new DirectoryInfo(path).Name;
}
