using System.Globalization;

namespace LViz.Core.Keymap.Parser;

/// <summary>Kinds of tokens emitted by <see cref="DeviceTreeLexer"/>.</summary>
public enum DtTokenKind
{
    LBrace, RBrace,
    LAngle, RAngle,
    LParen, RParen,
    LBracket, RBracket,
    Semi, Comma, Equals, Slash, Amp, Colon,
    String, Ident, Number,
    DirectiveDelete,    // /delete-node/ or /delete-property/
    End,
}

/// <summary>A single lexed token with source position.</summary>
public readonly record struct DtToken(DtTokenKind Kind, string Text, int Line, int Column);

/// <summary>
/// Splits preprocessed devicetree text into tokens for
/// <see cref="DeviceTreeParser"/>. Negative numbers wrapped in parens
/// (devicetree's <c>(-N)</c> convention inside <c>&lt;…&gt;</c> cell arrays)
/// surface as three tokens — <c>LParen Number RParen</c> — which the parser
/// collapses back into a single <see cref="DtCellNumber"/>.
/// </summary>
public static class DeviceTreeLexer
{
    public static List<DtToken> Tokenize(string source)
    {
        var tokens = new List<DtToken>();
        int i = 0;
        int line = 1;
        int lineStart = 0;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\n') { line++; i++; lineStart = i; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            int col = i - lineStart + 1;

            if (c == '"')
            {
                int startCol = col;
                int strStart = ++i;
                var sb = new System.Text.StringBuilder();
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        sb.Append(source[i + 1] switch
                        {
                            'n' => '\n', 't' => '\t', 'r' => '\r',
                            '\\' => '\\', '"' => '"', '0' => '\0',
                            var x => x,
                        });
                        i += 2;
                        continue;
                    }
                    if (source[i] == '\n') { line++; lineStart = i + 1; }
                    sb.Append(source[i]);
                    i++;
                }
                if (i < source.Length) i++; // consume closing quote
                tokens.Add(new DtToken(DtTokenKind.String, sb.ToString(), line, startCol));
                continue;
            }

            if (c == '/')
            {
                // Match /delete-node/ and /delete-property/ as single tokens
                // so the parser can skip cleanly without choking on the slashes.
                if (TryMatch(source, i, "/delete-node/")) {
                    tokens.Add(new DtToken(DtTokenKind.DirectiveDelete, "/delete-node/", line, col));
                    i += "/delete-node/".Length;
                    continue;
                }
                if (TryMatch(source, i, "/delete-property/")) {
                    tokens.Add(new DtToken(DtTokenKind.DirectiveDelete, "/delete-property/", line, col));
                    i += "/delete-property/".Length;
                    continue;
                }
                tokens.Add(new DtToken(DtTokenKind.Slash, "/", line, col));
                i++;
                continue;
            }

            switch (c)
            {
                case '{': tokens.Add(new DtToken(DtTokenKind.LBrace, "{", line, col)); i++; continue;
                case '}': tokens.Add(new DtToken(DtTokenKind.RBrace, "}", line, col)); i++; continue;
                case '<': tokens.Add(new DtToken(DtTokenKind.LAngle, "<", line, col)); i++; continue;
                case '>': tokens.Add(new DtToken(DtTokenKind.RAngle, ">", line, col)); i++; continue;
                case '(': tokens.Add(new DtToken(DtTokenKind.LParen, "(", line, col)); i++; continue;
                case ')': tokens.Add(new DtToken(DtTokenKind.RParen, ")", line, col)); i++; continue;
                case '[': tokens.Add(new DtToken(DtTokenKind.LBracket, "[", line, col)); i++; continue;
                case ']': tokens.Add(new DtToken(DtTokenKind.RBracket, "]", line, col)); i++; continue;
                case ';': tokens.Add(new DtToken(DtTokenKind.Semi, ";", line, col)); i++; continue;
                case ',': tokens.Add(new DtToken(DtTokenKind.Comma, ",", line, col)); i++; continue;
                case '=': tokens.Add(new DtToken(DtTokenKind.Equals, "=", line, col)); i++; continue;
                case '&': tokens.Add(new DtToken(DtTokenKind.Amp, "&", line, col)); i++; continue;
                case ':': tokens.Add(new DtToken(DtTokenKind.Colon, ":", line, col)); i++; continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                int start = i;
                if (c == '-') i++;
                if (i + 1 < source.Length && source[i] == '0' && (source[i + 1] == 'x' || source[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < source.Length && IsHexDigit(source[i])) i++;
                }
                else
                {
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                }
                // Strip C suffixes (U, L, UL, …) so int.Parse downstream works.
                int sufEnd = i;
                while (i < source.Length && (source[i] == 'u' || source[i] == 'U' || source[i] == 'l' || source[i] == 'L')) i++;
                tokens.Add(new DtToken(DtTokenKind.Number, source[start..sufEnd], line, col));
                continue;
            }

            if (IsIdentStart(c) || (c == '#' && i + 1 < source.Length && IsIdentStart(source[i + 1])))
            {
                int start = i;
                i++;
                while (i < source.Length && IsIdentCont(source[i])) i++;
                tokens.Add(new DtToken(DtTokenKind.Ident, source[start..i], line, col));
                continue;
            }

            // Unknown character — skip silently rather than crash on something
            // exotic that downstream consumers don't care about.
            i++;
        }

        tokens.Add(new DtToken(DtTokenKind.End, "", line, i - lineStart + 1));
        return tokens;
    }

    private static bool TryMatch(string source, int start, string needle)
    {
        if (start + needle.Length > source.Length) return false;
        for (int k = 0; k < needle.Length; k++)
            if (source[start + k] != needle[k]) return false;
        return true;
    }

    private static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);

    // '-' is part of devicetree identifiers (e.g. "key-positions",
    // "#binding-cells"). '.' is occasionally seen in property names.
    private static bool IsIdentCont(char c) =>
        c == '_' || c == '-' || c == '.' || char.IsLetterOrDigit(c);

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Parses a numeric literal as produced by the lexer (decimal or 0x hex,
    /// optionally signed). Used by the parser to fold <c>DtCellNumber</c>s.
    /// </summary>
    public static int ParseNumberLiteral(string text)
    {
        var s = text.Trim();
        bool neg = false;
        if (s.StartsWith('-')) { neg = true; s = s[1..]; }
        int value;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = int.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else
            value = int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return neg ? -value : value;
    }
}
