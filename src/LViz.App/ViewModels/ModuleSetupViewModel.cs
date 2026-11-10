using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.Core.GitHub;
using LViz.Core.Persistence;
using LViz.Core.ZmkConfig;

namespace LViz.App.ViewModels;

/// <summary>The GitHub zmk-config the wizard sets modules up on.</summary>
public sealed record ModuleSetupGitHubTarget(string Owner, string Repo, string Branch);

/// <summary>
/// What to set up: a local zmk-config root or a GitHub target — exactly one
/// of the two, checked by the view-model's constructor — plus a way to ask
/// whether the keyboard is plugged in (the Done step shows it).
/// </summary>
public sealed record ModuleSetupContext(
    string? LocalRoot,
    ModuleSetupGitHubTarget? GitHub,
    Func<bool> IsBoardConnected);

/// <summary>The wizard's steps.</summary>
public enum ModuleSetupStep
{
    Check,
    Choose,
    Review,
    Done,
}

/// <summary>
/// One build entry as a pickable central: its <see cref="ZmkBuildEntry.Index"/>
/// and its label — the board, then " · " and the shields joined with a space
/// when there are any (<c>nice_nano_v2 · cosmos82_right</c>).
/// </summary>
public sealed record ModuleSetupCentralOption(int Index, string Text);

/// <summary>
/// View model behind the module setup wizard window. The check step fetches
/// (for GitHub) and reads the config, shows what LViz found, and stops on a
/// blocker; the choose step pre-selects the central half and the live
/// features to install. Review, Save and Done live in
/// <c>ModuleSetupViewModel.Save.cs</c>. The shell calls
/// <see cref="StartAsync"/> and closes the window on
/// <see cref="CloseRequested"/>.
/// </summary>
public sealed partial class ModuleSetupViewModel : ObservableObject
{
    private readonly ModuleSetupContext _context;
    private readonly IGitHubKeymapService? _gitHub;

    // What Check reads and the Review/Save/Done steps
    // (ModuleSetupViewModel.Save.cs) work with.
    private readonly KeymapBackupStore _backups;
    private readonly Func<Guid> _newGuid;
    private string? _root;
    private ZmkConfigFileSet? _configFiles;
    private ZmkConfigFacts? _facts;
    private string? _baseSha;

    /// <param name="context">Local root or GitHub target to inspect.</param>
    /// <param name="gitHub">Required for a GitHub context, may be null for a local one.</param>
    /// <param name="backups">Backup store the save step snapshots into.</param>
    /// <param name="newGuid">Id source for config ids; tests pass a fixed one.</param>
    public ModuleSetupViewModel(
        ModuleSetupContext context,
        IGitHubKeymapService? gitHub,
        KeymapBackupStore backups,
        Func<Guid> newGuid)
    {
        if ((context.LocalRoot is null) == (context.GitHub is null))
            throw new ArgumentException(
                "Set exactly one of LocalRoot and GitHub.", nameof(context));
        if (context.GitHub is not null && gitHub is null)
            throw new ArgumentException(
                "A GitHub context needs the keymap service.", nameof(gitHub));

        _context = context;
        _gitHub = gitHub;
        _backups = backups;
        _newGuid = newGuid;
    }

    /// <summary>Raised when the user closes the window; carries <see cref="Saved"/>.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>True once the wizard has saved; the close event carries it.</summary>
    public bool Saved { get; private set; }

    // --- steps ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckStep))]
    [NotifyPropertyChangedFor(nameof(IsChooseStep))]
    [NotifyPropertyChangedFor(nameof(IsReviewStep))]
    [NotifyPropertyChangedFor(nameof(IsDoneStep))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private ModuleSetupStep _step;

    public bool IsCheckStep => Step == ModuleSetupStep.Check;
    public bool IsChooseStep => Step == ModuleSetupStep.Choose;
    public bool IsReviewStep => Step == ModuleSetupStep.Review;
    public bool IsDoneStep => Step == ModuleSetupStep.Done;

    // --- check step ---

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>A failed fetch, read, plan or save; red text in the window's bottom bar.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>What LViz found, one line per fact (ZMK, split, central, status).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _factLines = [];

    /// <summary>Why LViz cannot use this config; the blockers joined over lines, else null.</summary>
    [ObservableProperty]
    private string? _blockerMessage;

    /// <summary>The "already set up" / "other fork" notice, else null.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>No blockers and not yet set up — what lets the user press Next.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _canSetUp;

    // --- choose step ---

    [ObservableProperty]
    private IReadOnlyList<ModuleSetupCentralOption> _centralOptions = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private ModuleSetupCentralOption? _selectedCentral;

    [ObservableProperty]
    private bool _isSplit;

    /// <summary>Explains the pre-selected central or asks for a pick, else null.</summary>
    [ObservableProperty]
    private string? _centralHint;

    /// <summary>True when the board has underglow, so the RGB checkbox shows.</summary>
    [ObservableProperty]
    private bool _showRgb;

    [ObservableProperty]
    private bool _rgb;

    [ObservableProperty]
    private bool _signal;

    [ObservableProperty]
    private bool _emit;

    /// <summary>Reveals the emit checkbox under "Advanced".</summary>
    [ObservableProperty]
    private bool _showAdvanced;

    /// <summary>
    /// Reads (and for GitHub first fetches) the config and fills the check
    /// step; a failure lands in <see cref="ErrorMessage"/> and stops. The
    /// choose step is prefilled from the same facts.
    /// </summary>
    public async Task StartAsync()
    {
        Step = ModuleSetupStep.Check;
        ErrorMessage = null;
        BlockerMessage = null;
        StatusMessage = null;
        FactLines = [];
        CanSetUp = false;
        IsBusy = true;
        try
        {
            string root;
            if (_context.GitHub is { } target)
            {
                try
                {
                    var cache = await _gitHub!.FetchRepoAsync(target.Owner, target.Repo, target.Branch);
                    root = cache.Folder;
                    _baseSha = cache.Sha;
                }
                catch (GitHubApiException e)
                {
                    ErrorMessage = GitHubOpenViewModel.MessageFor(e);
                    CanSetUp = false;
                    return;
                }
            }
            else
            {
                root = _context.LocalRoot!;
            }

            ZmkConfigFacts facts;
            try
            {
                var files = ZmkConfigFileSet.Read(root);
                facts = ZmkConfigInspector.Inspect(files);
                _root = root;
                _configFiles = files;
                _facts = facts;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = Loc.Instance.Format("ModuleSetup_ErrorRead", e.Message);
                CanSetUp = false;
                return;
            }

            var options = BuildCentralOptions(facts.Entries);
            FactLines = BuildFactLines(facts, options);

            BlockerMessage = facts.Blockers.Count == 0
                ? null
                : string.Join("\n", facts.Blockers.Select(b => BlockerText(b, facts.UnreadableDetail)));
            CanSetUp = facts.Blockers.Count == 0 && facts.Status != ZmkModuleStatus.SetUp;

            StatusMessage = facts.Status switch
            {
                ZmkModuleStatus.SetUp => Loc.Instance["ModuleSetup_AlreadySetUp"],
                ZmkModuleStatus.NeedsFork => Loc.Instance["ModuleSetup_NeedsForkNotice"],
                _ => null,
            };

            CentralOptions = options;
            SelectedCentral = options.FirstOrDefault(o => o.Index == facts.CentralIndex);
            IsSplit = facts.IsSplit;
            CentralHint = facts.CentralReason switch
            {
                ZmkCentralReason.LeftNaming => Loc.Instance["ModuleSetup_CentralHintGuessed"],
                ZmkCentralReason.Unknown => Loc.Instance["ModuleSetup_CentralHintPick"],
                _ => null,
            };
            ShowRgb = facts.HasUnderglow;
            Rgb = ShowRgb;
            Signal = true;
            Emit = false;
            ShowAdvanced = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static IReadOnlyList<string> BuildFactLines(
        ZmkConfigFacts facts, IReadOnlyList<ModuleSetupCentralOption> options)
    {
        var unknown = Loc.Instance["ModuleSetup_Unknown"];
        var lines = new List<string>
        {
            Loc.Instance.Format("ModuleSetup_FactZmk", facts.ZmkOwner ?? unknown, facts.ZmkRevision ?? unknown),
            facts.IsSplit ? Loc.Instance["ModuleSetup_FactSplit"] : Loc.Instance["ModuleSetup_FactUnibody"],
        };

        lines.Add(facts.CentralReason == ZmkCentralReason.Unknown
                  || options.FirstOrDefault(o => o.Index == facts.CentralIndex) is not { } central
            ? Loc.Instance["ModuleSetup_FactCentralUnknown"]
            : Loc.Instance.Format("ModuleSetup_FactCentral", central.Text, CentralReasonText(facts.CentralReason)));

        lines.Add(facts.Status switch
        {
            ZmkModuleStatus.SetUp => Loc.Instance["ModuleSetup_FactStatusSetUp"],
            ZmkModuleStatus.NeedsFork => Loc.Instance["ModuleSetup_FactStatusNeedsFork"],
            _ => Loc.Instance["ModuleSetup_FactStatusNotSetUp"],
        });
        return lines;
    }

    /// <summary>The central-reason keys as one interpolated family; every <see cref="ZmkCentralReason"/> has one.</summary>
    private static string CentralReasonText(ZmkCentralReason reason) =>
        Loc.Instance[$"ModuleSetup_Reason{reason}"];

    /// <summary>The blocker keys as one interpolated family; Unreadable is formatted with its detail.</summary>
    private static string BlockerText(ZmkConfigBlocker blocker, string? unreadableDetail) =>
        blocker == ZmkConfigBlocker.Unreadable
            ? Loc.Instance.Format("ModuleSetup_Blocker_Unreadable", unreadableDetail ?? "")
            : Loc.Instance[$"ModuleSetup_Blocker_{blocker}"];

    private static IReadOnlyList<ModuleSetupCentralOption> BuildCentralOptions(
        IReadOnlyList<ZmkBuildEntry> entries) =>
        entries
            .Select(e => new ModuleSetupCentralOption(
                e.Index,
                e.Shields.Count > 0 ? $"{e.Board} \u00b7 {string.Join(" ", e.Shields)}" : e.Board))
            .ToList();

    // --- commands ---

    private bool CanNext() => Step switch
    {
        ModuleSetupStep.Check => CanSetUp && !IsBusy,
        ModuleSetupStep.Choose => SelectedCentral is not null,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        switch (Step)
        {
            case ModuleSetupStep.Check:
                Step = ModuleSetupStep.Choose;
                break;
            case ModuleSetupStep.Choose:
                GoToReview();
                break;
        }
    }

    // GoToReview — the plan build behind Next on Choose — lives in
    // ModuleSetupViewModel.Save.cs.

    private bool CanBack() => Step is ModuleSetupStep.Choose or ModuleSetupStep.Review;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        switch (Step)
        {
            case ModuleSetupStep.Choose:
                Step = ModuleSetupStep.Check;
                break;
            case ModuleSetupStep.Review:
                Files = [];
                Step = ModuleSetupStep.Choose;
                break;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(Saved);
}
