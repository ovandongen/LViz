using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Persistence;

namespace LViz.App.ViewModels;

/// <summary>
/// Where a keymap edit came from: the file the editor read (for a GitHub
/// keymap, the local cache copy), the settings entry it was loaded under, the
/// exact text the session started from, the git blob SHA of the bytes it
/// started from (what a later GitHub save commits against), and the parsed
/// GitHub reference when the entry is one.
/// </summary>
/// <param name="LocalPath">Path of the file the baseline text was read from.</param>
/// <param name="Entry">The settings entry this keymap is listed under — a local
/// path or a <c>github:owner/repo:branch:path</c> string.</param>
/// <param name="BaselineText">The exact text the session parsed, BOM stripped.</param>
/// <param name="BaseBlobSha">Lower-case git blob SHA of the bytes read from
/// <paramref name="LocalPath"/> when the edit started.</param>
/// <param name="GitHub">The parsed reference when <paramref name="Entry"/> is a
/// GitHub entry, else null.</param>
public sealed record KeymapEditSource(
    string LocalPath,
    string Entry,
    string BaselineText,
    string BaseBlobSha,
    GitHubKeymapRef? GitHub)
{
    /// <summary>True when the keymap came from a GitHub repository.</summary>
    public bool IsGitHub => GitHub is not null;

    /// <summary>"owner/repo (branch)" for GitHub, else the file name.</summary>
    public string TargetName =>
        GitHub is { } g ? $"{g.Owner}/{g.Repo} ({g.Branch})" : System.IO.Path.GetFileName(LocalPath);
}

/// <summary>
/// The App-side owner of one <see cref="KeymapEditSession"/>: where the keymap
/// came from (<see cref="Source"/>), the editable model rendered through a
/// fresh <see cref="LoadedKeymap"/> after every change, the single selected
/// key, clearing a key to <c>&amp;trans</c> / <c>&amp;none</c>, undo/redo, the
/// change count the edit banner shows, and the layer shown while editing.
/// <para>
/// It also owns every edit-mode action the window offers — the layer, combo and
/// key-editor commands (<c>KeymapEditViewModel.Layers.cs</c>,
/// <c>KeymapEditViewModel.Dialogs.cs</c>) and the save
/// (<c>KeymapEditViewModel.Save.cs</c>): freeze the edit into a
/// <see cref="KeymapSavePlan"/>, then write it — the local file through
/// <see cref="KeymapFileSaver"/> (with a backup first) or GitHub as a commit
/// against the blob SHA the edit started from. A target that changed
/// underneath the edit is a <see cref="KeymapEditSaveOutcome.Conflict"/>
/// result, not an error. Dialogs go through <see cref="IKeymapEditDialogs"/>;
/// what the main window must do afterwards (settings, reload) leaves through
/// <see cref="Completed"/> and <see cref="Reported"/>.
/// </para>
/// </summary>
public sealed partial class KeymapEditViewModel : ObservableObject
{
    private readonly IGitHubKeymapService? _gitHub;
    private readonly KeymapBackupStore _backups;
    private readonly IKeymapEditDialogs _dialogs;
    private readonly IKeyboardProfile _profile;

    /// <summary>The pending layer structure <see cref="Defines"/> and
    /// <see cref="LayerDefines"/> were last computed for, so key-only changes
    /// don't replay the writers. Empty until the first recompute.</summary>
    private IReadOnlyList<LayerStructureEdit> _definesStructure = [];

    /// <param name="dialogs">Where the edit's dialogs open.</param>
    /// <param name="profile">The board the combo dialog draws its key picker on.</param>
    /// <param name="shownLayerIndex">The layer shown when the edit opens
    /// (clamped into the layer list).</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/>, <paramref name="baseline"/>,
    /// <paramref name="backups"/>, <paramref name="dialogs"/> or
    /// <paramref name="profile"/> is null.
    /// </exception>
    public KeymapEditViewModel(
        KeymapEditSource source,
        KeyboardConfig baseline,
        IGitHubKeymapService? gitHub,
        KeymapBackupStore backups,
        IKeymapEditDialogs dialogs,
        IKeyboardProfile profile,
        int shownLayerIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(profile);

        Source = source;
        Session = new KeymapEditSession(baseline);
        Keymap = new LoadedKeymap(Session.Current);
        Defines = Preprocessor.CollectDefines(source.BaselineText);
        LayerDefines = KeymapWriter.LayerReferenceDefines(source.BaselineText, baseline);
        Session.Changed += OnSessionChanged;
        _gitHub = gitHub;
        _backups = backups;
        _dialogs = dialogs;
        _profile = profile;

        var count = Session.Current.Layers.Count;
        _shownLayerIndex = count == 0 ? 0 : Math.Clamp(shownLayerIndex, 0, count - 1);
        _shownLayerId = count == 0 ? null : Session.LayerIdAt(_shownLayerIndex);
    }

    /// <summary>
    /// Read <paramref name="localPath"/> fresh (for a GitHub keymap: its local
    /// cache copy), parse it as the baseline, and open an edit on it.
    /// </summary>
    /// <param name="localPath">The file to read and parse.</param>
    /// <param name="entry">The settings entry it is listed under — parsed as a
    /// GitHub reference when it is one, else kept as a plain local entry.</param>
    /// <param name="gitHub">The service a GitHub save commits through, or null
    /// when GitHub is not wired up (a GitHub save then reports it as
    /// unavailable).</param>
    /// <param name="backups">Backup store the local save snapshots into; never
    /// null.</param>
    /// <param name="dialogs">Where the edit's dialogs open.</param>
    /// <param name="profile">The board the combo dialog draws on.</param>
    /// <param name="shownLayerIndex">The layer shown when the edit opens.</param>
    /// <remarks>
    /// Read and parse failures (IOException, UnauthorizedAccessException,
    /// <see cref="ZmkKeymapParseException"/>) propagate to the caller, which
    /// shows them — an editor opens only on a keymap that parses.
    /// </remarks>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file is not readable.</exception>
    /// <exception cref="ZmkKeymapParseException">The text does not parse.</exception>
    public static KeymapEditViewModel Start(
        string localPath,
        string entry,
        IGitHubKeymapService? gitHub,
        KeymapBackupStore backups,
        IKeymapEditDialogs dialogs,
        IKeyboardProfile profile,
        int shownLayerIndex = 0)
    {
        // The SHA is of the bytes as read, before any text decoding, so it
        // matches exactly what a later GitHub save is committed against.
        var bytes = File.ReadAllBytes(localPath);

        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var baseBlobSha = GitBlobSha.Compute(bytes);
        GitHubKeymapRef.TryParse(entry, out var gitHubRef);

        var baseline = ZmkKeymapLoader.LoadFromText(text, "editor", localPath);
        return new KeymapEditViewModel(
            new KeymapEditSource(localPath, entry, text, baseBlobSha, gitHubRef),
            baseline,
            gitHub,
            backups,
            dialogs,
            profile,
            shownLayerIndex);
    }

    /// <summary>Where this edit came from.</summary>
    public KeymapEditSource Source { get; }

    /// <summary>The in-memory edit model behind this view-model.</summary>
    public KeymapEditSession Session { get; }

    /// <summary>
    /// The current keymap as the main window renders it — replaced with a new
    /// <see cref="LoadedKeymap"/> over <see cref="KeymapEditSession.Current"/>
    /// after every change.
    /// </summary>
    public LoadedKeymap Keymap { get; private set; }

    /// <summary>
    /// The file's object-like <c>#define</c>s as the pending layer structure
    /// leaves them — read off the text a save would write right now (the
    /// baseline text with the session's pending layer edits replayed onto it),
    /// so after a layer move they carry the moved values. What the key dialog
    /// feeds binding formatting and the raw-text parser so a typed
    /// <c>&amp;mo NAV</c> resolves like the file does.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Defines { get; private set; }

    /// <summary>
    /// The defines this file uses as a layer reference, as the pending layer
    /// structure leaves them — the values a save would write. The only names a
    /// layer number may be shown as, so a preview writes <c>&amp;mo NAV</c>
    /// where the file does and never names a layer after an unrelated define
    /// with the same value. Passed to the key dialog as
    /// <see cref="KeyDialogRequest.LayerDefines"/>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> LayerDefines { get; private set; }

    /// <summary>The one selected key, or null when nothing is selected.</summary>
    [ObservableProperty]
    private int? _selectedKeyIndex;

    /// <summary>How many changes still differ from the baseline.</summary>
    public int ChangeCount => Session.ChangeCount;

    /// <summary>True while anything differs from the baseline.</summary>
    public bool HasChanges => ChangeCount > 0;

    public bool CanUndo => Session.CanUndo;

    public bool CanRedo => Session.CanRedo;

    /// <summary>The edit-banner text: none / one / the count as a format.</summary>
    public string BannerText => ChangeCount switch
    {
        0 => Loc.Instance["Edit_BannerNone"],
        1 => Loc.Instance["Edit_BannerOne"],
        _ => Loc.Instance.Format("Edit_BannerFormat", ChangeCount),
    };

    /// <summary>
    /// Raised once per session change, after <see cref="Keymap"/> has been
    /// rebuilt and the change-count notifications have gone out — a view that
    /// re-renders the board listens to this rather than to the property events.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// True from the start of the edit until the main window closes it
    /// (<see cref="Close"/>). A dialog answer that arrives after that is thrown
    /// away, and the edit's commands refuse to run.
    /// </summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>The main window left this edit (saved, discarded, reloaded):
    /// from now on its commands do nothing and late dialog answers are dropped.</summary>
    public void Close()
    {
        IsOpen = false;
        NotifyCommandState();
    }

    /// <summary>
    /// Raised when an action finished that the main window must follow up on —
    /// a save that was written, or a conflict answered with "reload". Single
    /// subscriber (the main window); the action waits for the handler, which is
    /// how the notice dialog after a save stays inside the saving state.
    /// </summary>
    public event Func<KeymapEditCompletion, Task>? Completed;

    /// <summary>A translated line for the status bar or a toast.</summary>
    public event Action<KeymapEditReport>? Reported;

    private void Report(KeymapEditReportKind kind, string message) =>
        Reported?.Invoke(new KeymapEditReport(kind, message));

    /// <summary>Select one key; a negative index clears the selection.</summary>
    public void SelectKey(int keyIndex) => SelectedKeyIndex = keyIndex < 0 ? null : keyIndex;

    /// <summary>Drop the selection.</summary>
    public void ClearSelection() => SelectedKeyIndex = null;

    /// <summary>
    /// Make one key <c>&amp;none</c> (<paramref name="toNone"/>) or
    /// <c>&amp;trans</c> as a single undo step — Delete and Shift+Delete on the
    /// selected key.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> — without touching anything — when the indices
    /// fall outside <see cref="KeymapEditSession.Current"/>, or when the key
    /// already holds that binding (no step, no event).
    /// </returns>
    public bool ClearKey(int layerIndex, int keyIndex, bool toNone)
    {
        var layers = Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return false;
        if (keyIndex < 0 || keyIndex >= layers[layerIndex].Bindings.Count) return false;

        var target = toNone ? KeyBinding.None : KeyBinding.Transparent;
        return Session.SetBindings([new BindingChange(layerIndex, keyIndex, target)]);
    }

    /// <summary>
    /// Write <paramref name="binding"/> onto one key as a single undo step —
    /// the key dialog's Confirm.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> — without touching anything — when the indices
    /// fall outside <see cref="KeymapEditSession.Current"/>, or when the key
    /// already holds that binding (no step, no event).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is null.</exception>
    public bool SetKey(int layerIndex, int keyIndex, KeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var layers = Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return false;
        if (keyIndex < 0 || keyIndex >= layers[layerIndex].Bindings.Count) return false;

        return Session.SetBindings([new BindingChange(layerIndex, keyIndex, binding)]);
    }

    /// <summary>True while the session holds combo adds, changes or deletes
    /// that a save has not written yet.</summary>
    public bool HasPendingComboEdits => Session.PendingComboEdits.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Session.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Session.Redo();

    /// <summary>
    /// One session change: rebuild the rendered keymap first, so anything a
    /// handler reads is current, then notify the derived properties and the
    /// undo/redo commands, and finally raise <see cref="Changed"/> once.
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        Keymap = new LoadedKeymap(Session.Current);
        RefreshDefines();
        FollowShownLayer();

        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(HasPendingComboEdits));

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        NotifyCommandState();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Refresh <see cref="Defines"/> and <see cref="LayerDefines"/> when the
    /// pending layer structure changed since the last look: replay the pending
    /// edits onto the baseline text exactly as the save does and read the
    /// defines off that text, so a preview spells layer numbers with the
    /// values the saved file will carry. An empty edit list replays to the
    /// baseline text, so undoing back to the baseline restores the original
    /// values. Key-only changes never change the pending structure, so they
    /// leave both lists (and their instances) alone.
    /// </summary>
    /// <remarks>
    /// A writer failure (its bug guard throws) keeps the previous lists and
    /// the previous structure, logging instead of throwing, so the next change
    /// tries again and the session never sees an exception from this handler.
    /// </remarks>
    private void RefreshDefines()
    {
        var edits = Session.PendingStructureEdits;
        if (edits.SequenceEqual(_definesStructure)) return;

        try
        {
            var text = KeymapLayerWriter.ApplyAll(Source.BaselineText, edits);
            var defines = Preprocessor.CollectDefines(text);
            var layerDefines = KeymapWriter.LayerReferenceDefines(text, Session.Current);

            Defines = defines;
            LayerDefines = layerDefines;
            _definesStructure = edits;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Edit", $"Defines refresh for the pending layer structure failed: {ex}");
        }
    }
}

/// <summary>
/// What a <see cref="KeymapEditViewModel.SaveAsync"/> attempt amounted to:
/// written, refused because the target changed since the edit started, or
/// failed with translated text for a toast. Nothing is half-written.
/// </summary>
public abstract record KeymapEditSaveOutcome
{
    private KeymapEditSaveOutcome() { }

    /// <summary>Written; reload the keymap from <paramref name="ReloadPath"/>.</summary>
    public sealed record Saved(string ReloadPath) : KeymapEditSaveOutcome;

    /// <summary>The file changed since the edit started; nothing was written.</summary>
    public sealed record Conflict : KeymapEditSaveOutcome;

    /// <summary>Not written; <paramref name="Message"/> is translated, ready for a toast.</summary>
    public sealed record Failed(string Message) : KeymapEditSaveOutcome;
}
