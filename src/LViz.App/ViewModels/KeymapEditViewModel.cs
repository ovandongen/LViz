using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
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
/// key, clearing a key to <c>&amp;trans</c> / <c>&amp;none</c>, undo/redo, and
/// the change count the edit banner shows, and the save: freeze the edit into
/// a <see cref="KeymapSavePlan"/>, then write it — the local file through
/// <see cref="KeymapFileSaver"/> (with a backup first) or GitHub as a commit
/// against the blob SHA the edit started from. A target that changed
/// underneath the edit is a <see cref="KeymapEditSaveOutcome.Conflict"/>
/// result, not an error.
/// </summary>
public sealed partial class KeymapEditViewModel : ObservableObject
{
    private readonly IGitHubKeymapService? _gitHub;
    private readonly KeymapBackupStore _backups;

    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/>, <paramref name="baseline"/> or
    /// <paramref name="backups"/> is null.
    /// </exception>
    public KeymapEditViewModel(
        KeymapEditSource source,
        KeyboardConfig baseline,
        IGitHubKeymapService? gitHub,
        KeymapBackupStore backups)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(backups);

        Source = source;
        Session = new KeymapEditSession(baseline);
        Keymap = new LoadedKeymap(Session.Current);
        Defines = Preprocessor.CollectDefines(source.BaselineText);
        Session.Changed += OnSessionChanged;
        _gitHub = gitHub;
        _backups = backups;
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
        KeymapBackupStore backups)
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
            backups);
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
    /// The file's object-like <c>#define</c>s read off <see cref="Source"/>'s
    /// baseline text — what the key dialog feeds binding formatting and the
    /// raw-text parser so a typed <c>&amp;mo NAV</c> resolves like the file does.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Defines { get; }

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

    /// <summary>True when the key differs from what it was in the baseline.</summary>
    public bool IsKeyChanged(int layerIndex, int keyIndex) => Session.IsKeyChanged(layerIndex, keyIndex);

    /// <summary>
    /// Freeze the current edit into the text and the change lists a save writes,
    /// against the baseline text this editor opened.
    /// </summary>
    /// <remarks>
    /// <see cref="ZmkKeymapParseException"/> (a writer refused the text) and
    /// <see cref="ArgumentException"/> (the session drifted from the baseline)
    /// propagate — the caller shows them and keeps the user in edit mode.
    /// </remarks>
    public KeymapSavePlan BuildSavePlan() => KeymapSavePlan.Build(Source.BaselineText, Session);

    /// <summary>The English commit message a GitHub save of <paramref name="plan"/> uses.</summary>
    public string CommitMessageFor(KeymapSavePlan plan) => KeymapCommitMessage.Build(plan);

    /// <summary>
    /// Write <paramref name="plan"/> to where this edit came from: the local
    /// file through <see cref="KeymapFileSaver"/> (the content on disk is
    /// backed up first), or GitHub as a commit on the keymap's branch against
    /// <see cref="KeymapEditSource.BaseBlobSha"/> with
    /// <see cref="CommitMessageFor"/>. A target that changed since the edit
    /// started reports <see cref="KeymapEditSaveOutcome.Conflict"/>;
    /// <paramref name="overwrite"/> replaces it anyway.
    /// </summary>
    /// <remarks>
    /// The only exceptions caught are the local <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/>, which become a translated
    /// <see cref="KeymapEditSaveOutcome.Failed"/>; GitHub failures map to
    /// Failed through <see cref="GitHubOpenViewModel.MessageFor"/>, and a
    /// missing service to <c>Edit_SaveGitHubUnavailable</c>. A cancelled
    /// <paramref name="ct"/> propagates. Saving never touches the session —
    /// after any outcome the edit and its undo history are intact.
    /// </remarks>
    public async Task<KeymapEditSaveOutcome> SaveAsync(
        KeymapSavePlan plan,
        bool overwrite,
        CancellationToken ct = default)
    {
        if (!Source.IsGitHub)
        {
            try
            {
                var local = await KeymapFileSaver.SaveAsync(
                    Source.LocalPath, Source.BaselineText, plan.NewSource, overwrite, _backups, ct);

                return local == KeymapFileSaveResult.Saved
                    ? new KeymapEditSaveOutcome.Saved(Source.LocalPath)
                    : new KeymapEditSaveOutcome.Conflict();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn("Editor", $"Local save of {Source.LocalPath} failed: {ex}");
                return new KeymapEditSaveOutcome.Failed(
                    Loc.Instance.Format("Edit_SaveFailedFormat", ex.Message));
            }
        }

        if (_gitHub is null)
            return new KeymapEditSaveOutcome.Failed(Loc.Instance["Edit_SaveGitHubUnavailable"]);

        var save = await _gitHub.SaveAsync(
            Source.GitHub!,
            new UTF8Encoding(false).GetBytes(plan.NewSource),
            Source.BaseBlobSha,
            CommitMessageFor(plan),
            overwrite,
            ct);

        switch (save)
        {
            case GitHubSaveResult.Saved committed:
                DiagnosticLog.Info("Editor", $"Committed {Source.Entry} to GitHub as {committed.CommitSha}.");
                return new KeymapEditSaveOutcome.Saved(
                    _gitHub.LocalPathFor(Source.GitHub!) ?? Source.LocalPath);

            case GitHubSaveResult.Failed failure:
                return new KeymapEditSaveOutcome.Failed(GitHubOpenViewModel.MessageFor(
                    new GitHubApiException(failure.Kind, failure.Message)));

            case GitHubSaveResult.Conflict:
                return new KeymapEditSaveOutcome.Conflict();

            default:
                throw new InvalidOperationException(
                    $"unsupported GitHub save result {save.GetType().Name}.");
        }
    }

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

        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(BannerText));

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();

        Changed?.Invoke(this, EventArgs.Empty);
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
