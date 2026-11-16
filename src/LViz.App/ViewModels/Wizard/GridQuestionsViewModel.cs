using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Layout.Import;

namespace LViz.App.ViewModels;

/// <summary>
/// The wizard's "build from questions" answers: split or unibody, keys per
/// half, columns and thumb keys, which <see cref="GridLayoutGenerator"/> turns
/// into a plain grid. Owns the clamping of the three counts, the summary lines
/// and the keymap default; the wizard reads <see cref="Options"/> and tells
/// this view-model when its keymap changed (<see cref="KeymapChanged"/>).
/// </summary>
public sealed partial class GridQuestionsViewModel : ObservableObject
{
    private readonly Func<int?> _keymapKeyCount;

    /// <param name="keymapKeyCount">Key count of the keymap the wizard has
    /// (null when it has none); the summary compares against it and the
    /// default sizes the answers to it.</param>
    public GridQuestionsViewModel(Func<int?> keymapKeyCount)
    {
        _keymapKeyCount = keymapKeyCount;
        ApplyDefaultFromKeymap();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyCount))]
    [NotifyPropertyChangedFor(nameof(MatchesKeymap))]
    [NotifyPropertyChangedFor(nameof(KeyCountText))]
    private bool _split = true;

    // The three numbers arrive from steppers or typing and may be
    // out-of-range; they are stored clamped so KeyCount never throws.
    // [ObservableProperty] hooks see the value already stored, so clamping
    // on the way in needs a hand-written SetProperty setter.
    private int _keysPerHalf = 18;
    private int _columns = 6;
    private int _thumbKeys = 3;

    // The keymap default (ApplyDefaultFromKeymap) runs only while the user
    // has changed none of the three counts (the split toggle re-runs it);
    // _applyingDefault keeps the default's own write through the public
    // setters from counting as an edit.
    private bool _editedByUser;
    private bool _applyingDefault;

    /// <summary>A count the user changed (stepper or typing) wins over the
    /// keymap default for good; our own default write must not count, and a
    /// write that leaves the value as it was (a binding echoing it back) is
    /// not a change at all.</summary>
    private void MarkEditedByUser()
    {
        if (!_applyingDefault)
            _editedByUser = true;
    }

    /// <summary>Main keys per half without thumb keys (all main keys when
    /// unsplit); the rows follow from this and <see cref="Columns"/>.</summary>
    public int KeysPerHalf
    {
        get => _keysPerHalf;
        set
        {
            if (SetProperty(ref _keysPerHalf,
                    Math.Clamp(value, GridLayoutGenerator.MinKeysPerHalf, GridLayoutGenerator.MaxKeysPerHalf)))
            {
                MarkEditedByUser();
                NotifySummary();
            }
        }
    }

    /// <summary>Columns per half (whole board when unsplit).</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            if (SetProperty(ref _columns,
                    Math.Clamp(value, GridLayoutGenerator.MinColumns, GridLayoutGenerator.MaxColumns)))
            {
                MarkEditedByUser();
                NotifySummary();
            }
        }
    }

    /// <summary>Thumb keys per half (whole board when unsplit).</summary>
    public int ThumbKeys
    {
        get => _thumbKeys;
        set
        {
            if (SetProperty(ref _thumbKeys,
                    Math.Clamp(value, GridLayoutGenerator.MinThumbKeys, GridLayoutGenerator.MaxThumbKeys)))
            {
                MarkEditedByUser();
                NotifySummary();
            }
        }
    }

    // The generator's limits, for the view's numeric inputs.
    public int MinKeysPerHalf => GridLayoutGenerator.MinKeysPerHalf;
    public int MaxKeysPerHalf => GridLayoutGenerator.MaxKeysPerHalf;
    public int MinColumns => GridLayoutGenerator.MinColumns;
    public int MaxColumns => GridLayoutGenerator.MaxColumns;
    public int MinThumbKeys => GridLayoutGenerator.MinThumbKeys;
    public int MaxThumbKeys => GridLayoutGenerator.MaxThumbKeys;

    /// <summary>How many keys the current answers would generate.</summary>
    public int KeyCount => GridLayoutGenerator.KeyCount(Options);

    /// <summary>Whether the generated board fits the keymap's base layer.</summary>
    public bool MatchesKeymap => _keymapKeyCount() == KeyCount;

    /// <summary>The key count dressed for the questions panel.</summary>
    public string KeyCountText => Loc.Instance.Format("Wizard_GridKeyCountFormat", KeyCount);

    /// <summary>The answers as the generator's input record.</summary>
    public GridLayoutOptions Options => new(Split, KeysPerHalf, Columns, ThumbKeys);

    /// <summary>Re-raises the read-only summary lines after an answer or the
    /// keymap they compare against changed.</summary>
    private void NotifySummary()
    {
        OnPropertyChanged(nameof(KeyCount));
        OnPropertyChanged(nameof(MatchesKeymap));
        OnPropertyChanged(nameof(KeyCountText));
    }

    /// <summary>The wizard's keymap got a new value: default the answers to it
    /// (unless the user has touched them) and refresh the summary lines.</summary>
    public void KeymapChanged()
    {
        ApplyDefaultFromKeymap();
        NotifySummary();
    }

    // Split vs unibody changes what the keymap default should be, so the toggle
    // re-runs it instead of counting as an edit.
    partial void OnSplitChanged(bool value) => ApplyDefaultFromKeymap();

    /// <summary>
    /// Default keys per half to the keymap the wizard has: a split board fits
    /// half the bindings minus the thumb keys, a unibody all of them minus the
    /// thumbs. Runs only while the user has changed none of the three counts
    /// (their own edits win for good) and nowhere the result would fall below
    /// the generator's floor — there the current value simply stays. Called
    /// from the constructor, from <see cref="KeymapChanged"/> and when the
    /// split toggles, never by anything else.
    /// </summary>
    private void ApplyDefaultFromKeymap()
    {
        if (_editedByUser || _keymapKeyCount() is not int keymapKeys)
            return;

        int perHalf = Split
            ? keymapKeys / 2 - ThumbKeys
            : keymapKeys - ThumbKeys;
        if (perHalf < GridLayoutGenerator.MinKeysPerHalf)
            return;

        _applyingDefault = true;
        try
        {
            // The public setter clamps to the maximum.
            KeysPerHalf = perHalf;
            NotifySummary();
        }
        finally
        {
            _applyingDefault = false;
        }
    }
}
