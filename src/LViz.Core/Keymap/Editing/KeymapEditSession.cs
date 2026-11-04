using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// The in-memory model behind the keymap editor: the keymap as loaded
/// (<see cref="Baseline"/>) next to the keymap as edited (<see cref="Current"/>),
/// with every user action kept as one undoable step and a view of what still
/// differs from the baseline.
/// <para>
/// Nothing here touches the <c>.keymap</c> text: the editor edits this model and
/// <see cref="LViz.Core.Keymap.Writer.KeymapWriter"/> splices the difference into
/// the user's file when the edit is saved.
/// </para>
/// <para>
/// Layers are tracked by an internal id (baseline layer <c>i</c> starts as id
/// <c>i</c>) rather than by position, so inserting, removing or moving layers
/// keeps already-recorded steps and per-layer change tracking correct while
/// positions shift underneath them.
/// </para>
/// </summary>
public sealed class KeymapEditSession
{
    /// <summary>Layer id at each position of <see cref="Current"/>.Layers.</summary>
    private readonly List<int> _layerIds = [];

    /// <summary>What each layer id looked like when the session was created.</summary>
    private readonly Dictionary<int, LayerOrigin> _origins = [];

    private readonly Stack<EditStep> _undo = new();
    private readonly Stack<EditStep> _redo = new();

    /// <summary>Layer ids the baseline owns (<c>0 … n−1</c>): a current id at or
    /// above this count is a layer this session added, a number below it that is
    /// gone from <see cref="_layerIds"/> is a layer it deleted.</summary>
    private readonly int _baselineLayerCount;

    /// <summary>One past the highest layer id this session ever handed out. An add
    /// takes it and bumps it, so an id is never reused — not even after the layer
    /// that held it was deleted again — which keeps origins and steps unambiguous.</summary>
    private int _nextLayerId;

    /// <exception cref="ArgumentNullException"><paramref name="baseline"/> is null.</exception>
    public KeymapEditSession(KeyboardConfig baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        Baseline = baseline;
        Current = baseline;

        for (int i = 0; i < baseline.Layers.Count; i++)
        {
            _layerIds.Add(i);
            // Snapshot: the origin must not move if the caller later mutates the
            // list they handed us.
            _origins[i] = new LayerOrigin(baseline.Layers[i].Name, baseline.Layers[i].Bindings.ToArray());
        }

        _baselineLayerCount = baseline.Layers.Count;
        _nextLayerId = baseline.Layers.Count;
    }

    /// <summary>The keymap as it was loaded. This instance is never modified.</summary>
    public KeyboardConfig Baseline { get; }

    /// <summary>
    /// The keymap with every applied edit in it. Starts out as the
    /// <see cref="Baseline"/> instance; each edit replaces it with a new record
    /// whose untouched layers, macros, hold-taps and combos are the same instances.
    /// </summary>
    public KeyboardConfig Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised once per accepted edit and once per undo/redo step.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Set every cell in <paramref name="changes"/> as a single undo step.
    /// </summary>
    /// <remarks>
    /// All cells are checked against <see cref="Current"/> before anything is
    /// written, so a rejected call leaves the session untouched. Changes whose
    /// binding already equals the current one are dropped; when nothing is left
    /// the call is a silent no-op (no step, no event).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A change targets a layer outside the current layer list, or a key outside
    /// that layer's bindings.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Two changes target the same (layer, key), or one carries no binding.
    /// </exception>
    /// <returns>
    /// <see langword="true"/> when at least one cell really changed;
    /// <see langword="false"/> when the list was empty or every change was a no-op.
    /// </returns>
    public bool SetBindings(IReadOnlyList<BindingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var layers = Current.Layers;

        foreach (var change in changes)
        {
            if (change.LayerIndex < 0 || change.LayerIndex >= layers.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(changes),
                    $"binding change targets layer {change.LayerIndex}, the keymap has {layers.Count} layer(s).");
            if (change.KeyIndex < 0 || change.KeyIndex >= layers[change.LayerIndex].Bindings.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(changes),
                    $"binding change targets key {change.KeyIndex} of layer {change.LayerIndex}, "
                    + $"which has {layers[change.LayerIndex].Bindings.Count} binding(s).");
            if (change.Binding is null)
                throw new ArgumentException(
                    $"binding change for layer {change.LayerIndex}, key {change.KeyIndex} has no binding.",
                    nameof(changes));
        }

        var duplicate = changes
            .GroupBy(c => (c.LayerIndex, c.KeyIndex))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException(
                $"two binding changes target layer {duplicate.Key.LayerIndex}, key {duplicate.Key.KeyIndex}.",
                nameof(changes));

        var cells = new List<CellChange>();
        foreach (var change in changes)
        {
            var current = layers[change.LayerIndex].Bindings[change.KeyIndex];
            if (IsSameBinding(current, change.Binding)) continue;
            cells.Add(new CellChange(_layerIds[change.LayerIndex], change.KeyIndex, current, change.Binding));
        }

        if (cells.Count == 0) return false;

        Commit(new BindingStep(cells));
        return true;
    }

    /// <summary>
    /// Give the layer at <paramref name="layerIndex"/> a new
    /// <c>display-name</c> as a single undo step.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="layerIndex"/> is outside the current layer list.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank or contains a control character (the same
    /// rule <see cref="LViz.Core.Keymap.Writer.KeymapWriter.Apply"/> enforces,
    /// since the name goes into a quoted devicetree string).
    /// </exception>
    /// <returns>
    /// <see langword="false"/> when the layer already has that name; the call then
    /// records nothing.
    /// </returns>
    public bool RenameLayer(int layerIndex, string name)
    {
        var layers = Current.Layers;

        if (layerIndex < 0 || layerIndex >= layers.Count)
            throw new ArgumentOutOfRangeException(
                nameof(layerIndex),
                $"layer rename targets layer {layerIndex}, the keymap has {layers.Count} layer(s).");
        ValidateLayerName(name, nameof(name));

        if (string.Equals(name, layers[layerIndex].Name, StringComparison.Ordinal)) return false;

        Commit(new RenameStep(_layerIds[layerIndex], layers[layerIndex].Name, name));
        return true;
    }

    /// <summary>
    /// Insert, remove or move a layer as a single undo step.
    /// </summary>
    /// <remarks>
    /// The session does <b>not</b> work out what the edit does to the keymap —
    /// renumbering layer references (<c>&amp;mo 2</c> → <c>&amp;mo 3</c>) happens
    /// elsewhere. The caller passes the resulting keymap in <paramref name="after"/>
    /// and the step stores both snapshots, so undo/redo swap between them; what is
    /// checked here is that the shape of <paramref name="after"/> agrees with the
    /// edit, and that the edit itself is legal against <see cref="Current"/>.
    /// <para>
    /// The caller has to derive <paramref name="after"/> from the session's
    /// <see cref="Current"/>, not from the baseline: redo replays the snapshot as
    /// given, so a change that is missing from it disappears from the keymap again
    /// the moment the step is redone.
    /// </para>
    /// <para>
    /// Ids follow the layer: an add hands out a fresh one (and its origin becomes
    /// the name and bindings it was added with, so the new layer's own keys read as
    /// unchanged until they are edited), a delete drops one, a move carries it along.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> or
    /// <paramref name="after"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An index of the edit is outside the current layer list (for an add: outside
    /// <c>0 … layer count</c>).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An add carries the wrong number of bindings or an invalid name, or
    /// <paramref name="after"/> has the number of layers the edit does not produce.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The last layer of the keymap would be deleted, or the keymap has no layer to
    /// size a new layer against.
    /// </exception>
    /// <returns>
    /// <see langword="false"/> only for a move whose target equals its source; the
    /// call then records nothing.
    /// </returns>
    public bool ApplyStructure(LayerStructureEdit edit, KeyboardConfig after)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(after);

        var layers = Current.Layers;
        var count = layers.Count;
        int expected;

        switch (edit)
        {
            case LayerStructureEdit.Add add:
                if (add.AtIndex < 0 || add.AtIndex > count)
                    throw new ArgumentOutOfRangeException(
                        nameof(edit),
                        $"layer add targets position {add.AtIndex}, the keymap has {count} layer(s).");
                if (count == 0)
                    throw new InvalidOperationException(
                        "the keymap has no layer, so the key count of a new layer is unknown.");
                if (add.Bindings is null)
                    throw new ArgumentException("layer add has no bindings.", nameof(edit));
                if (add.Bindings.Count != layers[0].Bindings.Count)
                    throw new ArgumentException(
                        $"layer add carries {add.Bindings.Count} binding(s), the keymap has "
                        + $"{layers[0].Bindings.Count} key(s).",
                        nameof(edit));
                ValidateLayerName(add.Name, nameof(edit));
                expected = count + 1;
                break;

            case LayerStructureEdit.Delete delete:
                if (delete.Index < 0 || delete.Index >= count)
                    throw new ArgumentOutOfRangeException(
                        nameof(edit),
                        $"layer delete targets layer {delete.Index}, the keymap has {count} layer(s).");
                if (count == 1)
                    throw new InvalidOperationException(
                        "deleting the only layer would leave the keymap without a layer.");
                expected = count - 1;
                break;

            case LayerStructureEdit.Move move:
                if (move.From < 0 || move.From >= count)
                    throw new ArgumentOutOfRangeException(
                        nameof(edit),
                        $"layer move starts at {move.From}, the keymap has {count} layer(s).");
                if (move.To < 0 || move.To >= count)
                    throw new ArgumentOutOfRangeException(
                        nameof(edit),
                        $"layer move ends at {move.To}, the keymap has {count} layer(s).");
                if (move.From == move.To) return false;
                expected = count;
                break;

            default:
                throw new ArgumentException(
                    $"unsupported layer structure edit {edit.GetType().Name}.", nameof(edit));
        }

        if (after.Layers.Count != expected)
            throw new ArgumentException(
                $"{Describe(edit)} needs the resulting keymap to have {expected} layer(s), "
                + $"it has {after.Layers.Count}.",
                nameof(after));

        var beforeIds = _layerIds.ToArray();
        var ids = new List<int>(_layerIds);

        switch (edit)
        {
            case LayerStructureEdit.Add add:
                // A fresh id, never handed out again, whose origin is what it was
                // added with — the baseline it is measured against from here on.
                var newId = _nextLayerId++;
                _origins[newId] = new LayerOrigin(add.Name, [.. add.Bindings]);
                ids.Insert(add.AtIndex, newId);
                break;

            case LayerStructureEdit.Delete delete:
                ids.RemoveAt(delete.Index);
                break;

            case LayerStructureEdit.Move move:
                // Remove, then insert in the shortened list — LayerStructureEdit's rule.
                var moved = ids[move.From];
                ids.RemoveAt(move.From);
                ids.Insert(move.To, moved);
                break;
        }

        Commit(new StructureStep(edit, Current, beforeIds, after, [.. ids]));
        return true;
    }

    /// <summary>
    /// The layer adds, deletes and moves that turn the baseline layer list into
    /// the current one, in the order the save path replays them against the
    /// <c>.keymap</c> text before writing out the diff of the result. Derived
    /// from where each layer started and where it is now, not from the undo
    /// history, so steps the user reversed by hand (a move and the move back, an
    /// add and the delete of that layer) never show up.
    /// </summary>
    /// <remarks>
    /// The order: deletes from the highest baseline position down (so every
    /// index is still the baseline one), then moves, leaving the longest run of
    /// layers that is already in baseline order where it is, then adds at their
    /// final positions, lowest first. An add carries the layer's current name and
    /// the bindings it was added with; key edits on it come out of the binding
    /// diff. Empty whenever the layer ids are back to the baseline list.
    /// </remarks>
    public IReadOnlyList<LayerStructureEdit> PendingStructureEdits
    {
        get
        {
            if (IdsMatchBaseline()) return [];

            var edits = new List<LayerStructureEdit>();
            var work = Enumerable.Range(0, _baselineLayerCount).ToList();
            var kept = _layerIds.Where(id => id < _baselineLayerCount).ToList();
            var keptSet = kept.ToHashSet();

            for (int i = work.Count - 1; i >= 0; i--)
            {
                if (keptSet.Contains(work[i])) continue;
                edits.Add(new LayerStructureEdit.Delete(i));
                work.RemoveAt(i);
            }

            // Each moved layer lands right after its predecessor in the current
            // order. Processing them in that order keeps everything placed so far,
            // plus the run that stays, in current order.
            var stays = LongestIncreasingRun(kept);
            for (int p = 0; p < kept.Count; p++)
            {
                var id = kept[p];
                if (stays.Contains(id)) continue;
                var from = work.IndexOf(id);
                work.RemoveAt(from);
                var to = p == 0 ? 0 : work.IndexOf(kept[p - 1]) + 1;
                work.Insert(to, id);
                if (from != to) edits.Add(new LayerStructureEdit.Move(from, to));
            }

            for (int p = 0; p < _layerIds.Count; p++)
            {
                var id = _layerIds[p];
                if (id < _baselineLayerCount) continue;
                edits.Add(new LayerStructureEdit.Add(p, Current.Layers[p].Name, _origins[id].Bindings));
            }

            return edits;
        }
    }

    /// <summary>The ids on one longest strictly increasing subsequence of
    /// <paramref name="ids"/>: the layers a reorder can leave where they are.</summary>
    private static HashSet<int> LongestIncreasingRun(IReadOnlyList<int> ids)
    {
        var length = new int[ids.Count];
        var previous = new int[ids.Count];
        var best = -1;
        for (int i = 0; i < ids.Count; i++)
        {
            length[i] = 1;
            previous[i] = -1;
            for (int j = 0; j < i; j++)
                if (ids[j] < ids[i] && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    previous[i] = j;
                }
            if (best < 0 || length[i] > length[best]) best = i;
        }

        var run = new HashSet<int>();
        for (var i = best; i >= 0; i = previous[i]) run.Add(ids[i]);
        return run;
    }

    private static string Describe(LayerStructureEdit edit) => edit switch
    {
        LayerStructureEdit.Add => "layer add",
        LayerStructureEdit.Delete => "layer delete",
        LayerStructureEdit.Move => "layer move",
        _ => "layer structure edit",
    };

    /// <summary>The name rule <see cref="LViz.Core.Keymap.Writer.KeymapWriter.Apply"/>
    /// enforces, since the name is written into a quoted devicetree string.</summary>
    private static void ValidateLayerName(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("layer name is empty.", paramName);
        if (name.Any(char.IsControl))
            throw new ArgumentException("layer name contains a control character.", paramName);
    }

    /// <summary>Are the layer ids still the baseline's, in the baseline's order?</summary>
    private bool IdsMatchBaseline()
    {
        if (_layerIds.Count != _baselineLayerCount) return false;
        for (int i = 0; i < _layerIds.Count; i++)
            if (_layerIds[i] != i) return false;
        return true;
    }

    /// <summary>Undo the last step. Does nothing when there is no step to undo.</summary>
    /// <returns><see langword="true"/> when a step was undone.</returns>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        var step = _undo.Pop();
        step.Revert(this);
        _redo.Push(step);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Redo the last undone step.</summary>
    /// <returns><see langword="true"/> when a step was redone.</returns>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        var step = _redo.Pop();
        step.Apply(this);
        _undo.Push(step);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// True when the key at <paramref name="layerIndex"/> /
    /// <paramref name="keyIndex"/> differs from what it was in the baseline (and a
    /// key the baseline did not have differs by definition). An index outside
    /// <see cref="Current"/> reads as unchanged.
    /// </summary>
    public bool IsKeyChanged(int layerIndex, int keyIndex)
    {
        if (layerIndex < 0 || layerIndex >= Current.Layers.Count) return false;

        var bindings = Current.Layers[layerIndex].Bindings;
        if (keyIndex < 0 || keyIndex >= bindings.Count) return false;
        if (!TryGetOrigin(layerIndex, out var origin)) return false;

        return KeyDiffers(
            bindings[keyIndex],
            keyIndex < origin.Bindings.Count ? origin.Bindings[keyIndex] : null);
    }

    /// <summary>
    /// True when the layer at <paramref name="layerIndex"/> is named differently
    /// than in the baseline. An index outside <see cref="Current"/> reads as
    /// unchanged.
    /// </summary>
    public bool IsLayerRenamed(int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= Current.Layers.Count) return false;
        if (!TryGetOrigin(layerIndex, out var origin)) return false;

        return !string.Equals(Current.Layers[layerIndex].Name, origin.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the layer at <paramref name="layerIndex"/> was inserted by this
    /// session — its id is not one of the baseline's. An index outside
    /// <see cref="Current"/> reads as not added.
    /// </summary>
    public bool IsLayerAdded(int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= _layerIds.Count) return false;
        return _layerIds[layerIndex] >= _baselineLayerCount;
    }

    /// <summary>
    /// Which baseline layer the layer now at <paramref name="layerIndex"/> is: its
    /// position in <see cref="Baseline"/>. <see langword="null"/> for a layer this
    /// session added, and for an index outside <see cref="Current"/>.
    /// </summary>
    /// <remarks>
    /// What lets per-layer settings (tab colours, hidden layers, …) follow a layer
    /// that moved: pass the current position, get the identity the settings were
    /// keyed by. The inverse lookup — where a baseline layer sits now — is
    /// <see cref="BaselineLayerMap"/>.
    /// </remarks>
    public int? BaselineIndexOf(int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= _layerIds.Count) return null;
        var id = _layerIds[layerIndex];
        return id < _baselineLayerCount ? id : null;
    }

    /// <summary>
    /// The session's id of the layer now at <paramref name="layerIndex"/>, or
    /// <see langword="null"/> for an index outside <see cref="Current"/>. Ids
    /// stay with their layer through moves, undo and redo and are never reused,
    /// so a caller can hold one to find the same layer again with
    /// <see cref="IndexOfLayerId"/>.
    /// </summary>
    public int? LayerIdAt(int layerIndex) =>
        layerIndex >= 0 && layerIndex < _layerIds.Count ? _layerIds[layerIndex] : null;

    /// <summary>
    /// Where the layer with id <paramref name="layerId"/> sits now in
    /// <see cref="Current"/>.Layers, or <see langword="null"/> when it isn't
    /// there (deleted, or its add was undone).
    /// </summary>
    public int? IndexOfLayerId(int layerId)
    {
        var position = _layerIds.IndexOf(layerId);
        return position < 0 ? null : position;
    }

    /// <summary>
    /// Where every baseline layer sits now: one entry per baseline layer
    /// (<see cref="Baseline"/>.Layers.Count), entry <c>b</c> being the position in
    /// <see cref="Current"/>.Layers where baseline layer <c>b</c> now is, or
    /// <see langword="null"/> when it was deleted. A fresh session maps every layer
    /// to its own position: <c>[0, 1, …, n−1]</c>.
    /// </summary>
    /// <remarks>
    /// What the save path uses to move per-layer settings along with their layer.
    /// A new list on every call — mutating it cannot affect the session.
    /// </remarks>
    public IReadOnlyList<int?> BaselineLayerMap()
    {
        var map = new int?[_baselineLayerCount];
        for (int position = 0; position < _layerIds.Count; position++)
        {
            var id = _layerIds[position];
            if (id < _baselineLayerCount) map[id] = position;
        }
        return map;
    }

    /// <summary>
    /// Changed keys and renamed layers over every layer, plus what is structurally
    /// different from the baseline: one per added layer, one per deleted baseline
    /// layer, and one more as soon as the surviving baseline layers sit in a
    /// different relative order. Always computed from <see cref="Current"/> against
    /// the origins — never from the undo stack — so undoing everything, editing a
    /// key back to its baseline value, or moving a layer and moving it back reads
    /// as clean again.
    /// </summary>
    public int ChangeCount
    {
        get
        {
            var count = 0;

            for (int layer = 0; layer < Current.Layers.Count; layer++)
            {
                if (!TryGetOrigin(layer, out var origin)) continue;
                var bindings = Current.Layers[layer].Bindings;

                if (!string.Equals(Current.Layers[layer].Name, origin.Name, StringComparison.Ordinal)) count++;

                // Lengths can differ once a caller hands in a differently shaped
                // layer; a key that appeared or disappeared is a change either way.
                for (int key = 0; key < Math.Max(bindings.Count, origin.Bindings.Count); key++)
                {
                    var now = key < bindings.Count ? bindings[key] : null;
                    var was = key < origin.Bindings.Count ? origin.Bindings[key] : null;
                    if (KeyDiffers(now, was)) count++;
                }
            }

            // Structure. Ids below the baseline count are baseline layers, in the
            // order they now sit in; anything above it was added here.
            var baselinePresent = new List<int>();
            foreach (var id in _layerIds)
            {
                if (id < _baselineLayerCount) baselinePresent.Add(id);
                else count++;
            }

            count += _baselineLayerCount - baselinePresent.Count; // the deleted ones

            // Baseline ids start out ascending, so their relative order is intact
            // exactly while they still ascend; a deleted one leaves a gap, not a
            // reorder.
            for (int i = 1; i < baselinePresent.Count; i++)
            {
                if (baselinePresent[i] < baselinePresent[i - 1])
                {
                    count++;
                    break;
                }
            }

            return count;
        }
    }

    public bool HasPendingChanges => ChangeCount > 0;

    // ---- applying steps -------------------------------------------------------

    /// <summary>
    /// Apply a fresh step: it becomes the top of the undo stack and the redo
    /// history is dropped, because the timeline now diverges here.
    /// </summary>
    private void Commit(EditStep step)
    {
        step.Apply(this);
        _undo.Push(step);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Write one cell per entry, grouped by layer so a layer is rebuilt once.</summary>
    private void WriteCells(IReadOnlyList<CellChange> cells, Func<CellChange, KeyBinding> pick)
    {
        foreach (var group in cells.GroupBy(c => c.LayerId))
        {
            var position = PositionOf(group.Key);
            var bindings = Current.Layers[position].Bindings.ToArray();

            foreach (var cell in group) bindings[cell.KeyIndex] = pick(cell);

            ReplaceLayer(position, Current.Layers[position] with { Bindings = bindings });
        }
    }

    private void SetLayerName(int layerId, string name)
    {
        var position = PositionOf(layerId);
        ReplaceLayer(position, Current.Layers[position] with { Name = name });
    }

    /// <summary>Swap one layer in place; everything else keeps its instance.</summary>
    private void ReplaceLayer(int position, Layer layer)
    {
        var layers = Current.Layers.ToArray();
        layers[position] = layer with { Index = position }; // Index mirrors position, always
        Current = Current with { Layers = layers };
    }

    /// <summary>Swap the whole layer list and its ids for a snapshot taken by a
    /// structural step. The caller of <see cref="ApplyStructure"/> owns the
    /// numbering inside <c>after</c> — it is stored and replayed as given.</summary>
    private void RestoreStructure(KeyboardConfig config, IReadOnlyList<int> ids)
    {
        _layerIds.Clear();
        _layerIds.AddRange(ids);
        Current = config;
    }

    /// <summary>Where a layer id sits now. Steps carry ids precisely because this
    /// answer moves once layers are inserted, removed or moved; an id that is gone
    /// means a step is being replayed against the wrong shape.</summary>
    private int PositionOf(int layerId)
    {
        var position = _layerIds.IndexOf(layerId);
        if (position < 0)
            throw new InvalidOperationException($"layer id {layerId} is no longer part of this session.");
        return position;
    }

    /// <summary>
    /// The baseline state of the layer at <paramref name="layerIndex"/> — for a
    /// layer added in this session, the name and bindings it was added with. False
    /// only for an id that was never registered, which no path here produces.
    /// </summary>
    private bool TryGetOrigin(int layerIndex, out LayerOrigin origin) =>
        _origins.TryGetValue(_layerIds[layerIndex], out origin!);

    /// <summary>KeyBinding's record equality ignores the contents of
    /// <c>Params</c> — compare behavior plus params explicitly. Internal because
    /// <see cref="KeymapEditDiff"/> computes the same equality for the save diff;
    /// <see cref="LViz.Core.Keymap.Writer.KeymapWriter"/> still carries its own
    /// private copy, and lifting all of them into Models is a later change.</summary>
    internal static bool IsSameBinding(KeyBinding a, KeyBinding b) =>
        string.Equals(a.Behavior, b.Behavior, StringComparison.Ordinal)
        && a.Params.SequenceEqual(b.Params, StringComparer.Ordinal);

    /// <summary>A missing side means the key was added or taken away since the
    /// baseline, which differs from any binding.</summary>
    private static bool KeyDiffers(KeyBinding? now, KeyBinding? was) =>
        now is null || was is null || !IsSameBinding(now, was);

    // ---- steps ----------------------------------------------------------------
    // Nested inside the session on purpose: a step reaches the id list and the
    // origin map directly, which is what lets the structural step below re-shuffle
    // them.

    /// <summary>
    /// One user action. Carries both directions, so undo and redo replay stored
    /// values instead of re-deriving them.
    /// </summary>
    private abstract record EditStep
    {
        public abstract void Apply(KeymapEditSession session);

        public abstract void Revert(KeymapEditSession session);
    }

    /// <summary>One cell: which layer id and key, and what it held before and after.</summary>
    private sealed record CellChange(int LayerId, int KeyIndex, KeyBinding Old, KeyBinding New);

    /// <summary>
    /// A multi-key action — "clear these three keys" — as a single step: undoing
    /// it restores all of its cells at once.
    /// </summary>
    private sealed record BindingStep(IReadOnlyList<CellChange> Cells) : EditStep
    {
        public override void Apply(KeymapEditSession session) => session.WriteCells(Cells, static c => c.New);

        public override void Revert(KeymapEditSession session) => session.WriteCells(Cells, static c => c.Old);
    }

    private sealed record RenameStep(int LayerId, string OldName, string NewName) : EditStep
    {
        public override void Apply(KeymapEditSession session) => session.SetLayerName(LayerId, NewName);

        public override void Revert(KeymapEditSession session) => session.SetLayerName(LayerId, OldName);
    }

    /// <summary>
    /// One structural action: insert, remove or move a layer. Unlike the other
    /// steps it carries whole snapshots — the keymap and the id list on both sides
    /// — because a layer coming, going or shifting moves every position underneath
    /// it; replaying the edit position-by-position would have to re-derive what the
    /// caller already knew.
    /// </summary>
    private sealed record StructureStep(
        LayerStructureEdit Edit,
        KeyboardConfig Before,
        IReadOnlyList<int> BeforeIds,
        KeyboardConfig After,
        IReadOnlyList<int> AfterIds) : EditStep
    {
        public override void Apply(KeymapEditSession session) => session.RestoreStructure(After, AfterIds);

        public override void Revert(KeymapEditSession session) => session.RestoreStructure(Before, BeforeIds);
    }

    /// <summary>What a layer id is measured against: what it was named and held in
    /// the baseline, or — for a layer added later — what it was added with.</summary>
    private sealed record LayerOrigin(string Name, IReadOnlyList<KeyBinding> Bindings);
}
