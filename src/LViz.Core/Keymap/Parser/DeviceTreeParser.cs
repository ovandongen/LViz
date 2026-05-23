using System.Text;

namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Recursive-descent parser over <see cref="DeviceTreeLexer"/> tokens.
/// Produces a <see cref="DtRoot"/> tree containing every node and property
/// in the source. Not a full devicetree implementation — just enough to
/// extract the parts a ZMK keymap cares about (keymap / combos / macros /
/// behavior definitions). Malformed regions are skipped with best-effort
/// recovery so the rest of the file still parses.
/// </summary>
public static class DeviceTreeParser
{
    public static DtRoot Parse(IReadOnlyList<DtToken> tokens)
    {
        int pos = 0;
        var children = new List<DtNode>();

        while (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.End)
        {
            // Skip stray ';' between top-level nodes.
            if (tokens[pos].Kind == DtTokenKind.Semi) { pos++; continue; }
            if (tokens[pos].Kind == DtTokenKind.DirectiveDelete) { SkipDirective(tokens, ref pos); continue; }

            var node = TryParseNode(tokens, ref pos);
            if (node is not null) children.Add(node);
            else if (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.End)
                pos++; // forward progress on unrecognized token
        }

        return new DtRoot(children);
    }

    private static DtNode? TryParseNode(IReadOnlyList<DtToken> tokens, ref int pos)
    {
        int start = pos;
        string? label = null;
        string name;
        bool isReference;
        int line = tokens[pos].Line;
        int col = tokens[pos].Column;

        // Three shapes:
        //   '/'         { … };          — root node
        //   '&'phandle  { … };          — overlay by reference
        //   label ':' name { … };       — labelled declaration
        //   name        { … };          — plain declaration
        if (tokens[pos].Kind == DtTokenKind.Slash)
        {
            name = "/";
            isReference = false;
            pos++;
        }
        else if (tokens[pos].Kind == DtTokenKind.Amp)
        {
            pos++;
            if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.Ident) { pos = start + 1; return null; }
            name = tokens[pos++].Text;
            isReference = true;
        }
        else if (tokens[pos].Kind == DtTokenKind.Ident
                 && pos + 1 < tokens.Count && tokens[pos + 1].Kind == DtTokenKind.Colon)
        {
            label = tokens[pos].Text;
            pos += 2;
            if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.Ident) { pos = start + 1; return null; }
            name = tokens[pos++].Text;
            isReference = false;
        }
        else if (tokens[pos].Kind == DtTokenKind.Ident)
        {
            name = tokens[pos++].Text;
            isReference = false;
        }
        else
        {
            return null;
        }

        if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.LBrace)
        {
            // Not actually a node — rewind and let the caller move on.
            pos = start;
            return null;
        }
        var openBrace = tokens[pos];
        pos++; // consume '{'

        var properties = new List<DtProperty>();
        var children = new List<DtNode>();

        while (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.RBrace && tokens[pos].Kind != DtTokenKind.End)
        {
            if (tokens[pos].Kind == DtTokenKind.Semi) { pos++; continue; }
            if (tokens[pos].Kind == DtTokenKind.DirectiveDelete) { SkipDirective(tokens, ref pos); continue; }

            // Decide: nested node vs property. Both start with an Ident (or
            // '&' for overlay), but a node has '{' (possibly after label).
            int save = pos;
            if (LooksLikeNode(tokens, pos))
            {
                var child = TryParseNode(tokens, ref pos);
                if (child is not null) children.Add(child);
                else if (pos == save) pos++;
                continue;
            }

            var prop = TryParseProperty(tokens, ref pos);
            if (prop is not null) properties.Add(prop);
            else if (pos == save) pos++;
        }

        if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.RBrace)
        {
            throw new ZmkKeymapParseException(
                $"Unclosed '{{' for node '{name}' opened at line {openBrace.Line}, " +
                $"column {openBrace.Column} (reached end of file).");
        }
        pos++; // '}'
        if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.Semi) pos++;

        return new DtNode(label, name, isReference, properties, children, line, col);
    }

    private static bool LooksLikeNode(IReadOnlyList<DtToken> tokens, int pos)
    {
        // '&phandle {' or 'name {' or 'label : name {' all peek-able with
        // bounded lookahead.
        if (pos >= tokens.Count) return false;
        var t = tokens[pos];
        if (t.Kind == DtTokenKind.Amp)
            return pos + 2 < tokens.Count
                && tokens[pos + 1].Kind == DtTokenKind.Ident
                && tokens[pos + 2].Kind == DtTokenKind.LBrace;
        if (t.Kind == DtTokenKind.Slash)
            return pos + 1 < tokens.Count && tokens[pos + 1].Kind == DtTokenKind.LBrace;
        if (t.Kind != DtTokenKind.Ident) return false;
        if (pos + 1 < tokens.Count && tokens[pos + 1].Kind == DtTokenKind.LBrace) return true;
        if (pos + 3 < tokens.Count
            && tokens[pos + 1].Kind == DtTokenKind.Colon
            && tokens[pos + 2].Kind == DtTokenKind.Ident
            && tokens[pos + 3].Kind == DtTokenKind.LBrace) return true;
        return false;
    }

    private static DtProperty? TryParseProperty(IReadOnlyList<DtToken> tokens, ref int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.Ident) return null;
        var nameTok = tokens[pos++];

        // Boolean property: 'name;'
        if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.Semi)
        {
            pos++;
            return new DtProperty(nameTok.Text, new DtBool(), nameTok.Line, nameTok.Column);
        }

        if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.Equals)
        {
            // Not a property after all — push back the ident.
            pos--;
            return null;
        }
        pos++; // consume '='

        var value = ParsePropertyValue(tokens, ref pos);

        // Consume trailing ';' (skip stray tokens until we find it).
        while (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.Semi
               && tokens[pos].Kind != DtTokenKind.RBrace
               && tokens[pos].Kind != DtTokenKind.End) pos++;
        if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.Semi) pos++;

        return new DtProperty(nameTok.Text, value, nameTok.Line, nameTok.Column);
    }

    private static DtValue ParsePropertyValue(IReadOnlyList<DtToken> tokens, ref int pos)
    {
        // Strings — possibly comma-separated list.
        if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.String)
        {
            var strings = new List<string> { tokens[pos++].Text };
            while (pos + 1 < tokens.Count
                   && tokens[pos].Kind == DtTokenKind.Comma
                   && tokens[pos + 1].Kind == DtTokenKind.String)
            {
                pos++; // comma
                strings.Add(tokens[pos++].Text);
            }
            return new DtStringList(strings);
        }

        // Cell array — '<…>' possibly continued with ', <…>' segments.
        if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.LAngle)
        {
            var cells = new List<DtCell>();
            ParseCellGroup(tokens, ref pos, cells);
            while (pos + 1 < tokens.Count
                   && tokens[pos].Kind == DtTokenKind.Comma
                   && tokens[pos + 1].Kind == DtTokenKind.LAngle)
            {
                pos++; // comma
                ParseCellGroup(tokens, ref pos, cells);
            }
            return new DtCellArray(cells);
        }

        // Byte array — '[ … ]'.
        if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.LBracket)
        {
            pos++;
            var bytes = new List<byte>();
            while (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.RBracket && tokens[pos].Kind != DtTokenKind.End)
            {
                if (tokens[pos].Kind == DtTokenKind.Number || tokens[pos].Kind == DtTokenKind.Ident)
                {
                    try { bytes.Add(Convert.ToByte(tokens[pos].Text, 16)); } catch { }
                }
                pos++;
            }
            if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.RBracket) pos++;
            return new DtByteArray(bytes);
        }

        // Unknown value — return an empty cell array so downstream code
        // doesn't crash, but skip the rest of the line.
        return new DtCellArray(Array.Empty<DtCell>());
    }

    private static void ParseCellGroup(IReadOnlyList<DtToken> tokens, ref int pos, List<DtCell> cells)
    {
        if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.LAngle) return;
        var open = tokens[pos];
        pos++; // '<'

        while (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.RAngle && tokens[pos].Kind != DtTokenKind.End)
        {
            var tok = tokens[pos];

            // A structural token (closing brace, semicolon, opening brace)
            // inside a cell array means the user forgot the closing '>' —
            // surface that loudly rather than silently consuming past the
            // bindings into surrounding syntax.
            if (tok.Kind is DtTokenKind.RBrace or DtTokenKind.LBrace or DtTokenKind.Semi)
            {
                throw new ZmkKeymapParseException(
                    $"Unterminated '<...>' starting at line {open.Line}, column {open.Column} " +
                    $"(saw '{tok.Text}' at line {tok.Line}, column {tok.Column}).");
            }

            if (tok.Kind == DtTokenKind.Amp)
            {
                pos++;
                if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.Ident)
                {
                    cells.Add(new DtCellRef(tokens[pos].Text));
                    pos++;
                }
                continue;
            }

            if (tok.Kind == DtTokenKind.Number)
            {
                try { cells.Add(new DtCellNumber(DeviceTreeLexer.ParseNumberLiteral(tok.Text), tok.Text)); }
                catch { cells.Add(new DtCellIdent(tok.Text)); }
                pos++;
                continue;
            }

            if (tok.Kind == DtTokenKind.LParen)
            {
                cells.Add(new DtCellParenExpr(CollectParenExpr(tokens, ref pos)));
                continue;
            }

            if (tok.Kind == DtTokenKind.Ident)
            {
                cells.Add(new DtCellIdent(tok.Text));
                pos++;
                continue;
            }

            // Stray separator (comma) inside a cell array — skip.
            pos++;
        }

        if (pos >= tokens.Count || tokens[pos].Kind != DtTokenKind.RAngle)
        {
            throw new ZmkKeymapParseException(
                $"Unterminated '<...>' starting at line {open.Line}, column {open.Column} " +
                "(reached end of file).");
        }
        pos++; // consume '>'
    }

    /// <summary>
    /// Consumes a parenthesized group from <paramref name="pos"/> (which
    /// must point at <c>(</c>) and returns its verbatim text including the
    /// outer parens. Handles nested parens so modifier-stacked keycodes
    /// like <c>(LC(LS(LA(X))))</c> survive intact.
    /// </summary>
    private static string CollectParenExpr(IReadOnlyList<DtToken> tokens, ref int pos)
    {
        var sb = new StringBuilder();
        int depth = 0;
        while (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.End)
        {
            var tok = tokens[pos];
            if (tok.Kind == DtTokenKind.LParen)
            {
                sb.Append('(');
                depth++;
                pos++;
                continue;
            }
            if (tok.Kind == DtTokenKind.RParen)
            {
                sb.Append(')');
                depth--;
                pos++;
                if (depth == 0) return sb.ToString();
                continue;
            }
            if (tok.Kind == DtTokenKind.Amp)
            {
                sb.Append('&');
                pos++;
                continue;
            }
            // Non-paren tokens — concatenate text with spaces so the
            // verbatim form remains parse-friendly for the keycode mapper.
            if (sb.Length > 0 && sb[^1] != '(' && sb[^1] != ' ') sb.Append(' ');
            sb.Append(tok.Text);
            pos++;
        }
        return sb.ToString();
    }

    private static void SkipDirective(IReadOnlyList<DtToken> tokens, ref int pos)
    {
        // /delete-node/ and /delete-property/ take one ident then ';'.
        pos++; // directive
        while (pos < tokens.Count && tokens[pos].Kind != DtTokenKind.Semi && tokens[pos].Kind != DtTokenKind.End) pos++;
        if (pos < tokens.Count && tokens[pos].Kind == DtTokenKind.Semi) pos++;
    }
}
