using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Lean C-preprocessor pass for ZMK <c>.keymap</c> files. Text-in, text-out.
/// Does just enough to keep the devicetree lexer happy and to resolve simple
/// <c>#define</c> integer constants used as layer indices.
/// <para>
/// Intentionally limited: drops <c>#include</c> lines entirely (no header
/// resolution), supports object-like <c>#define</c> only (function-like
/// definitions are skipped), and treats every undefined identifier in an
/// <c>#if</c> expression as <c>0</c> — so <c>#ifdef CONFIG_FOO</c> blocks
/// strip cleanly when no Kconfig is in play.
/// </para>
/// </summary>
public static class Preprocessor
{
    /// <summary>
    /// Runs the preprocessor over <paramref name="source"/> and returns the
    /// transformed text. Line count is preserved (skipped/active regions
    /// become blank lines) so devicetree error line numbers still align with
    /// the original source.
    /// </summary>
    public static string Process(string source)
    {
        var withoutComments = StripComments(source);
        var (withoutConditionals, defines) = ApplyConditionalsAndCollectDefines(withoutComments);
        return SubstituteDefines(withoutConditionals, defines);
    }

    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            // String literal — copy through verbatim (incl. escaped quotes).
            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < source.Length)
                {
                    char s = source[i];
                    sb.Append(s);
                    i++;
                    if (s == '\\' && i < source.Length)
                    {
                        sb.Append(source[i]);
                        i++;
                        continue;
                    }
                    if (s == '"') break;
                }
                continue;
            }

            if (c == '/' && i + 1 < source.Length)
            {
                char n = source[i + 1];
                if (n == '/')
                {
                    // Line comment — skip until newline, preserve the newline.
                    i += 2;
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }
                if (n == '*')
                {
                    // Block comment — skip until '*/', preserve newlines so
                    // line numbers stay aligned with the original source.
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        if (source[i] == '\n') sb.Append('\n');
                        i++;
                    }
                    if (i + 1 < source.Length) i += 2; // skip closing */
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static readonly Regex DirectiveRegex = new(@"^\s*#\s*(\w+)(.*)$", RegexOptions.Compiled);

    private static (string Text, Dictionary<string, string> Defines)
        ApplyConditionalsAndCollectDefines(string source)
    {
        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = new StringBuilder(source.Length);

        // Stack of (currentlyActive, anyBranchTaken, parentActive).
        var stack = new Stack<(bool Active, bool Taken, bool ParentActive)>();
        bool active = true;

        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var m = DirectiveRegex.Match(line);
            if (m.Success)
            {
                var directive = m.Groups[1].Value;
                var rest = m.Groups[2].Value.Trim();

                switch (directive)
                {
                    case "if":
                    {
                        bool parentActive = active;
                        bool cond = parentActive && EvaluateExpression(rest, defines);
                        stack.Push((cond, cond, parentActive));
                        active = cond;
                        break;
                    }
                    case "ifdef":
                    {
                        bool parentActive = active;
                        bool cond = parentActive && defines.ContainsKey(rest);
                        stack.Push((cond, cond, parentActive));
                        active = cond;
                        break;
                    }
                    case "ifndef":
                    {
                        bool parentActive = active;
                        bool cond = parentActive && !defines.ContainsKey(rest);
                        stack.Push((cond, cond, parentActive));
                        active = cond;
                        break;
                    }
                    case "elif":
                    {
                        if (stack.Count == 0) break;
                        var top = stack.Pop();
                        bool cond = top.ParentActive && !top.Taken && EvaluateExpression(rest, defines);
                        stack.Push((cond, top.Taken || cond, top.ParentActive));
                        active = cond;
                        break;
                    }
                    case "else":
                    {
                        if (stack.Count == 0) break;
                        var top = stack.Pop();
                        bool cond = top.ParentActive && !top.Taken;
                        stack.Push((cond, true, top.ParentActive));
                        active = cond;
                        break;
                    }
                    case "endif":
                    {
                        if (stack.Count > 0)
                        {
                            var top = stack.Pop();
                            active = top.ParentActive;
                        }
                        break;
                    }
                    case "define":
                        if (active) HandleDefine(rest, defines);
                        break;
                    case "undef":
                        if (active) defines.Remove(rest);
                        break;
                    case "include":
                    case "pragma":
                    case "error":
                    case "warning":
                    case "line":
                        // Silently dropped. Include resolution is out of scope
                        // for v1; the keycode tokens we care about appear in
                        // the keymap source verbatim.
                        break;
                    default:
                        // Unknown directive — drop the line but keep going.
                        break;
                }

                // Replace the directive line with an empty line so line
                // numbers in downstream errors stay aligned with the source.
                output.Append('\n');
                continue;
            }

            if (active) output.Append(line);
            output.Append('\n');
        }

        return (output.ToString(), defines);
    }

    private static void HandleDefine(string rest, Dictionary<string, string> defines)
    {
        if (string.IsNullOrWhiteSpace(rest)) return;
        // Function-like macros: NAME(args) body. Not supported in v1 — skip.
        int firstSpace = IndexOfWhitespaceOrParen(rest);
        if (firstSpace < 0)
        {
            // #define NAME — present-with-no-value (used as a flag for #ifdef).
            defines[rest.Trim()] = "1";
            return;
        }
        if (rest[firstSpace] == '(')
        {
            // Function-like — skip silently.
            return;
        }
        var name = rest[..firstSpace].Trim();
        var value = rest[firstSpace..].Trim();
        if (name.Length == 0) return;
        defines[name] = value;
    }

    private static int IndexOfWhitespaceOrParen(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(' || char.IsWhiteSpace(c)) return i;
        }
        return -1;
    }

    // Identifier tokens that may match a #define key. We do a single linear
    // scan over the non-string text so substitutions are word-bounded and
    // don't disturb keycode tokens inside string literals (rare in keymaps
    // but cheap to honor).
    private static string SubstituteDefines(string source, Dictionary<string, string> defines)
    {
        if (defines.Count == 0) return source;

        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < source.Length)
                {
                    char s = source[i];
                    sb.Append(s);
                    i++;
                    if (s == '\\' && i < source.Length) { sb.Append(source[i]); i++; continue; }
                    if (s == '"') break;
                }
                continue;
            }

            if (IsIdentStart(c))
            {
                int start = i;
                i++;
                while (i < source.Length && IsIdentCont(source[i])) i++;
                var word = source[start..i];
                if (defines.TryGetValue(word, out var replacement))
                {
                    sb.Append(ExpandRecursively(replacement, defines, depth: 0));
                }
                else
                {
                    sb.Append(word);
                }
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static string ExpandRecursively(string value, Dictionary<string, string> defines, int depth)
    {
        if (depth > 8)
            throw new ZmkKeymapParseException("Recursive #define expansion exceeds 8 levels — likely a cycle.");
        var sb = new StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (IsIdentStart(c))
            {
                int start = i;
                i++;
                while (i < value.Length && IsIdentCont(value[i])) i++;
                var word = value[start..i];
                if (defines.TryGetValue(word, out var inner))
                    sb.Append(ExpandRecursively(inner, defines, depth + 1));
                else
                    sb.Append(word);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);
    private static bool IsIdentCont(char c) => c == '_' || char.IsLetterOrDigit(c);

    /// <summary>
    /// Evaluates a preprocessor expression. Supports <c>defined(X)</c>,
    /// integer literals (decimal + hex), unary <c>!</c>, binary
    /// <c>&amp;&amp;</c>, <c>||</c>, <c>==</c>, <c>!=</c>, <c>&lt;</c>,
    /// <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>+</c>, <c>-</c>, and
    /// parens. Unknown identifiers evaluate to <c>0</c>.
    /// </summary>
    internal static bool EvaluateExpression(string expr, IReadOnlyDictionary<string, string> defines)
    {
        var tokens = TokenizeExpression(expr, defines);
        int pos = 0;
        long value = ParseOr(tokens, ref pos);
        return value != 0;
    }

    private static long ParseOr(List<string> t, ref int pos)
    {
        long left = ParseAnd(t, ref pos);
        while (pos < t.Count && t[pos] == "||")
        {
            pos++;
            long right = ParseAnd(t, ref pos);
            left = (left != 0 || right != 0) ? 1 : 0;
        }
        return left;
    }

    private static long ParseAnd(List<string> t, ref int pos)
    {
        long left = ParseEquality(t, ref pos);
        while (pos < t.Count && t[pos] == "&&")
        {
            pos++;
            long right = ParseEquality(t, ref pos);
            left = (left != 0 && right != 0) ? 1 : 0;
        }
        return left;
    }

    private static long ParseEquality(List<string> t, ref int pos)
    {
        long left = ParseRelational(t, ref pos);
        while (pos < t.Count && (t[pos] == "==" || t[pos] == "!="))
        {
            var op = t[pos++];
            long right = ParseRelational(t, ref pos);
            left = op == "==" ? (left == right ? 1 : 0) : (left != right ? 1 : 0);
        }
        return left;
    }

    private static long ParseRelational(List<string> t, ref int pos)
    {
        long left = ParseAdditive(t, ref pos);
        while (pos < t.Count && (t[pos] is "<" or ">" or "<=" or ">="))
        {
            var op = t[pos++];
            long right = ParseAdditive(t, ref pos);
            left = op switch
            {
                "<" => left < right ? 1 : 0,
                ">" => left > right ? 1 : 0,
                "<=" => left <= right ? 1 : 0,
                ">=" => left >= right ? 1 : 0,
                _ => 0,
            };
        }
        return left;
    }

    private static long ParseAdditive(List<string> t, ref int pos)
    {
        long left = ParseUnary(t, ref pos);
        while (pos < t.Count && (t[pos] == "+" || t[pos] == "-"))
        {
            var op = t[pos++];
            long right = ParseUnary(t, ref pos);
            left = op == "+" ? left + right : left - right;
        }
        return left;
    }

    private static long ParseUnary(List<string> t, ref int pos)
    {
        if (pos < t.Count && t[pos] == "!")
        {
            pos++;
            return ParseUnary(t, ref pos) == 0 ? 1 : 0;
        }
        if (pos < t.Count && t[pos] == "-")
        {
            pos++;
            return -ParseUnary(t, ref pos);
        }
        if (pos < t.Count && t[pos] == "+")
        {
            pos++;
            return ParseUnary(t, ref pos);
        }
        return ParsePrimary(t, ref pos);
    }

    private static long ParsePrimary(List<string> t, ref int pos)
    {
        if (pos >= t.Count) return 0;
        var tok = t[pos++];
        if (tok == "(")
        {
            long v = ParseOr(t, ref pos);
            if (pos < t.Count && t[pos] == ")") pos++;
            return v;
        }
        if (long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(tok[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
            return h;
        // Bare identifier we couldn't fold during tokenization — treat as 0.
        return 0;
    }

    private static List<string> TokenizeExpression(string expr, IReadOnlyDictionary<string, string> defines)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (IsIdentStart(c))
            {
                int start = i;
                i++;
                while (i < expr.Length && IsIdentCont(expr[i])) i++;
                var word = expr[start..i];
                if (word == "defined")
                {
                    // defined(NAME) or defined NAME → 1 / 0.
                    int j = i;
                    while (j < expr.Length && char.IsWhiteSpace(expr[j])) j++;
                    string name;
                    if (j < expr.Length && expr[j] == '(')
                    {
                        j++;
                        while (j < expr.Length && char.IsWhiteSpace(expr[j])) j++;
                        int nameStart = j;
                        while (j < expr.Length && IsIdentCont(expr[j])) j++;
                        name = expr[nameStart..j];
                        while (j < expr.Length && char.IsWhiteSpace(expr[j])) j++;
                        if (j < expr.Length && expr[j] == ')') j++;
                    }
                    else
                    {
                        int nameStart = j;
                        while (j < expr.Length && IsIdentCont(expr[j])) j++;
                        name = expr[nameStart..j];
                    }
                    tokens.Add(defines.ContainsKey(name) ? "1" : "0");
                    i = j;
                    continue;
                }
                // Resolve identifier via defines (best-effort, recursive).
                if (defines.TryGetValue(word, out var v))
                    tokens.Add(v.Trim());
                else
                    tokens.Add(word);
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i;
                if (c == '0' && i + 1 < expr.Length && (expr[i + 1] == 'x' || expr[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < expr.Length && IsHexDigit(expr[i])) i++;
                }
                else
                {
                    while (i < expr.Length && char.IsDigit(expr[i])) i++;
                }
                // Strip C integer suffixes (U, L, UL, LL, ULL) — they don't
                // affect value and our parser doesn't model them.
                while (i < expr.Length && (expr[i] == 'u' || expr[i] == 'U' || expr[i] == 'l' || expr[i] == 'L')) i++;
                tokens.Add(expr[start..i].TrimEnd('u', 'U', 'l', 'L'));
                continue;
            }

            // Multi-char operators.
            if (i + 1 < expr.Length)
            {
                var two = expr.Substring(i, 2);
                if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=")
                {
                    tokens.Add(two);
                    i += 2;
                    continue;
                }
            }

            if (c is '!' or '<' or '>' or '(' or ')' or '+' or '-')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            // Unknown character — skip rather than choke.
            i++;
        }
        return tokens;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
