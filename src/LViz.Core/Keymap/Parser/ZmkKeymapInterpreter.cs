using LViz.Core.Models;

namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Walks a <see cref="DtRoot"/> AST and builds a <see cref="KeyboardConfig"/>.
/// Two-pass: first registers user-defined behavior arities and collects
/// hold-tap definitions, then slices layer / combo / macro bindings using
/// the now-complete arity map.
/// </summary>
public static class ZmkKeymapInterpreter
{
    public static KeyboardConfig Interpret(DtRoot root, string keyboardId)
    {
        var allNodes = FlattenNodes(root).ToList();

        // Pass 1: collect user-defined behavior arities + hold-tap defs.
        var userArities = new Dictionary<string, int>(StringComparer.Ordinal);
        var holdTaps = new List<HoldTap>();
        foreach (var node in allNodes)
        {
            var compat = GetCompatible(node);
            if (compat is null || node.Label is null) continue;
            if (!compat.StartsWith("zmk,behavior-", StringComparison.Ordinal)) continue;

            var phandle = "&" + node.Label;
            if (TryGetInt(node, "#binding-cells", out var cells))
                userArities[phandle] = cells;
        }

        // Now that arities are known, materialize hold-taps (their hold/tap
        // arities depend on what those bindings consume).
        foreach (var node in allNodes)
        {
            var compat = GetCompatible(node);
            if (compat != "zmk,behavior-hold-tap" || node.Label is null) continue;

            var bindingsProp = GetProperty(node, "bindings");
            if (bindingsProp?.Value is not DtCellArray ca || ca.Cells.Count < 2) continue;

            // Expect two ref cells: <&hold>, <&tap>.
            var refs = ca.Cells.OfType<DtCellRef>().Take(2).ToList();
            if (refs.Count < 2) continue;
            var holdBinding = "&" + refs[0].PhandleName;
            var tapBinding = "&" + refs[1].PhandleName;
            int holdArity = Math.Max(0, BehaviorArityTable.Arity(holdBinding, userArities));
            int tapArity = Math.Max(0, BehaviorArityTable.Arity(tapBinding, userArities));
            holdTaps.Add(new HoldTap("&" + node.Label, holdBinding, tapBinding, holdArity, tapArity));
        }

        // Pass 2: keymap → layers, combos → ZmkCombo, macros → ZmkMacro.
        var layers = new List<Layer>();
        var combos = new List<ZmkCombo>();
        var macros = new List<ZmkMacro>();

        var keymapNode = allNodes.FirstOrDefault(n => GetCompatible(n) == "zmk,keymap");
        if (keymapNode is not null)
        {
            int layerIdx = 0;
            foreach (var layerNode in keymapNode.Children)
            {
                var name = GetString(layerNode, "display-name") ?? layerNode.Name;
                var bindings = SliceBindings(GetProperty(layerNode, "bindings"), userArities);
                layers.Add(new Layer(layerIdx++, name, bindings));
            }
        }

        var combosNode = allNodes.FirstOrDefault(n => GetCompatible(n) == "zmk,combos");
        if (combosNode is not null)
        {
            foreach (var comboNode in combosNode.Children)
            {
                var keyPositions = GetIntList(comboNode, "key-positions");
                var layerScope = GetIntList(comboNode, "layers");
                if (layerScope.Count == 0) layerScope = new[] { -1 };
                var bindings = SliceBindings(GetProperty(comboNode, "bindings"), userArities);
                var binding = bindings.Count > 0 ? bindings[0] : KeyBinding.None;
                var name = comboNode.Label ?? comboNode.Name;
                combos.Add(new ZmkCombo(name, "", keyPositions, layerScope, binding));
            }
        }

        foreach (var node in allNodes)
        {
            var compat = GetCompatible(node);
            if (compat is null || node.Label is null) continue;
            if (!compat.StartsWith("zmk,behavior-macro", StringComparison.Ordinal)) continue;

            var bindingCells = 0;
            if (TryGetInt(node, "#binding-cells", out var bc)) bindingCells = bc;
            var paramNames = Enumerable.Range(1, bindingCells).Select(i => $"p{i}").ToList();
            var bindings = SliceBindings(GetProperty(node, "bindings"), userArities);
            macros.Add(new ZmkMacro("&" + node.Label, paramNames, bindings));
        }

        return new KeyboardConfig(
            KeyboardId: keyboardId,
            Layers: layers,
            Macros: macros,
            HoldTaps: holdTaps,
            Combos: combos);
    }

    // ---- slicer ------------------------------------------------------------

    private static IReadOnlyList<KeyBinding> SliceBindings(
        DtProperty? bindingsProp,
        IReadOnlyDictionary<string, int> userArities)
    {
        if (bindingsProp?.Value is not DtCellArray ca || ca.Cells.Count == 0)
            return Array.Empty<KeyBinding>();

        var output = new List<KeyBinding>();
        int i = 0;
        var cells = ca.Cells;
        while (i < cells.Count)
        {
            if (cells[i] is not DtCellRef refCell)
            {
                // Stray param with no preceding '&' — skip and keep going.
                i++;
                continue;
            }

            var behavior = "&" + refCell.PhandleName;
            int arity = BehaviorArityTable.Arity(behavior, userArities);

            // &bt is special: arity 2 if next token is BT_SEL, else 1.
            if (behavior == "&bt")
            {
                arity = (i + 1 < cells.Count
                         && cells[i + 1] is DtCellIdent ident
                         && ident.Text == "BT_SEL") ? 2 : 1;
            }

            i++; // consume the ref
            var paramList = new List<string>();

            if (arity == BehaviorArityTable.UnknownArity)
            {
                // Greedy: consume non-ref cells until next ref or end.
                while (i < cells.Count && cells[i] is not DtCellRef)
                {
                    paramList.Add(StringifyCell(cells[i]));
                    i++;
                }
            }
            else
            {
                for (int k = 0; k < arity && i < cells.Count; k++)
                {
                    paramList.Add(StringifyCell(cells[i]));
                    i++;
                }
            }

            output.Add(new KeyBinding(behavior, paramList));
        }
        return output;
    }

    private static string StringifyCell(DtCell cell) => cell switch
    {
        DtCellIdent id => id.Text,
        DtCellNumber n => n.RawText.Trim(),
        DtCellParenExpr p => p.RawText,
        DtCellRef r => "&" + r.PhandleName,
        _ => "",
    };

    // ---- AST traversal helpers --------------------------------------------

    private static IEnumerable<DtNode> FlattenNodes(DtRoot root)
    {
        foreach (var child in root.Children)
            foreach (var n in FlattenNode(child))
                yield return n;
    }

    private static IEnumerable<DtNode> FlattenNode(DtNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var n in FlattenNode(child))
                yield return n;
    }

    private static DtProperty? GetProperty(DtNode node, string name)
        => node.Properties.FirstOrDefault(p => p.Name == name);

    private static string? GetCompatible(DtNode node)
        => GetProperty(node, "compatible")?.Value is DtStringList sl && sl.Values.Count > 0
            ? sl.Values[0]
            : null;

    private static string? GetString(DtNode node, string name)
        => GetProperty(node, name)?.Value is DtStringList sl && sl.Values.Count > 0
            ? sl.Values[0]
            : null;

    private static bool TryGetInt(DtNode node, string name, out int value)
    {
        value = 0;
        var prop = GetProperty(node, name);
        if (prop?.Value is DtCellArray ca && ca.Cells.Count > 0 && ca.Cells[0] is DtCellNumber n)
        {
            value = n.Value;
            return true;
        }
        return false;
    }

    private static IReadOnlyList<int> GetIntList(DtNode node, string name)
    {
        var prop = GetProperty(node, name);
        if (prop?.Value is not DtCellArray ca) return Array.Empty<int>();
        return ca.Cells.OfType<DtCellNumber>().Select(c => c.Value).ToList();
    }
}
